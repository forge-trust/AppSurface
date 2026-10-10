#!/usr/bin/env python3
"""Unprivileged, metadata-only proposed V3 audit; no loader/payload execution."""
import argparse
import hashlib
import json
import math
import os
import re
import selectors
import signal
import stat
import subprocess
import sys
import time

SCHEMA = "issue779-fixture-os-elf-pins-v2"
MAX_AUDIT = 1024 * 1024
MAX_PATH = 4096
MAX_FILES = 4096
MAX_ELFS = 2048
MAX_ALIASES = 64
MAX_LINKS = 16
MAX_DEPENDENCIES = 128
MAX_TREE_NODES = 65536
MAX_TREE_BYTES = 4 * 1024**3
MAX_FILE_BYTES = 2 * 1024**3
MAX_INTEGER = 2**53 - 1
MAX_COMMAND_BYTES = 1024 * 1024
SONAME = re.compile(r"[A-Za-z0-9_.+-]{1,256}\Z")
# Ordered, fixed trusted OS locations, never caller PATH or a supplied executable.
TOOLS = {
    name: ("/usr/bin/" + name, "/bin/" + name)
    for name in (
        "dd", "sha256sum", "stat", "find", "timeout", "head", "jq", "readelf", "awk",
        "sort", "cmp", "cp", "chmod", "chown", "install", "systemd-run", "systemctl",
        "getent", "setpriv", "strace", "date", "sleep", "od", "tr", "cut", "cat",
        "grep", "wc", "readlink", "uname", "setsid", "ps", "mv", "mkfifo", "rm",
    )
}
TOOLS["bash"] = ("/usr/bin/bash",)
TOOLS["env"] = ("/usr/bin/env",)
# Fixed negative-test parser interpreter; no caller executable or module path.
TOOLS["python3.12"] = ("/usr/bin/python3.12",)
PYTHON_STDLIB_ROOT = "/usr/lib/python3.12"
TOOLS["ldconfig"] = ("/usr/sbin/ldconfig.real", "/sbin/ldconfig.real")


class AuditError(Exception):
    """Closed failure; exception text from external operations is never published."""


def reject(category):
    raise AuditError(category)


def canonical_path(value):
    if (not isinstance(value, str) or not value.startswith("/") or value == "/"
            or len(value.encode("utf-8")) > MAX_PATH
            or any(ord(c) < 32 or ord(c) == 127 for c in value)
            or any(c in ("", ".", "..") for c in value[1:].split("/"))):
        reject("path-shape")
    return value


def identity(info):
    return (info.st_dev, info.st_ino, info.st_mode, info.st_uid, info.st_gid,
            info.st_nlink, info.st_size, info.st_mtime_ns, info.st_ctime_ns)


def root_node(info, directory=False):
    if info.st_uid != 0 or info.st_gid != 0 or info.st_mode & 0o022:
        reject("os-metadata")
    if directory and not stat.S_ISDIR(info.st_mode):
        reject("os-parent")


class Audit:
    """Holds sampled identities, not authority. One caller monotonic expiry owns all I/O."""

    def __init__(self, deadline):
        now = time.monotonic()
        if not math.isfinite(deadline) or not 0 < deadline - now <= 60:
            reject("deadline-input")
        self.deadline = deadline
        self.nodes = {}
        self.files = {}
        self.aliases = {}
        self.elf = {}
        self.commands = {}
        self.cache = {}
        self.failure_context = None
        self.roots = ()
        self.trees = {}

    def check(self):
        if time.monotonic() >= self.deadline:
            reject("deadline")

    def sample(self, path):
        self.check()
        info = os.lstat(path)
        value = identity(info)
        prior = self.nodes.get(path)
        if prior is not None and prior != value:
            reject("identity-changed")
        if len(self.nodes) >= MAX_TREE_NODES and prior is None:
            reject("node-bound")
        self.nodes[path] = value
        return info

    def resolve_os(self, literal):
        """Resolve only actual reviewed root-owned OS links; retain literal membership."""
        canonical_path(literal)
        queue = literal[1:].split("/")
        current = ""
        links = []
        steps = 0
        root_node(self.sample("/"), True)
        while queue:
            self.check()
            steps += 1
            if steps > 256:
                reject("alias-step-bound")
            current += "/" + queue.pop(0)
            info = self.sample(current)
            if stat.S_ISLNK(info.st_mode):
                if len(links) >= MAX_LINKS:
                    reject("alias-link-bound")
                if info.st_uid != 0 or info.st_gid != 0 or stat.S_IMODE(info.st_mode) != 0o777:
                    reject("alias-metadata")
                if not 0 <= info.st_dev <= MAX_INTEGER or not 0 < info.st_ino <= MAX_INTEGER:
                    reject("alias-integer-bound")
                target = os.readlink(current)
                if (not target or len(target.encode("utf-8")) > MAX_PATH
                        or any(ord(c) < 32 or ord(c) == 127 for c in target)):
                    reject("alias-target")
                if identity(os.lstat(current)) != identity(info):
                    reject("alias-changed")
                links.append({"path": current, "target": target, "uid": 0, "gid": 0,
                              "mode": "777", "device": info.st_dev, "inode": info.st_ino})
                raw = target if target.startswith("/") else os.path.dirname(current) + "/" + target
                normalized = []
                for part in raw.split("/"):
                    if part in ("", "."):
                        continue
                    if part == "..":
                        if not normalized:
                            reject("alias-root-escape")
                        normalized.pop()
                    else:
                        normalized.append(part)
                resolved = canonical_path("/" + "/".join(normalized))
                queue = resolved[1:].split("/") + queue
                current = ""
            else:
                root_node(info, bool(queue))
                if not queue and (not stat.S_ISREG(info.st_mode) or info.st_nlink != 1):
                    reject("os-file-shape")
        return current, links

    def no_link_chain(self, path):
        """Inputs and resolved terminals do not inherit the OS alias exception."""
        canonical_path(path)
        current = ""
        root_node(self.sample("/"), True)
        for part in path[1:].split("/"):
            current += "/" + part
            info = self.sample(current)
            if stat.S_ISLNK(info.st_mode):
                reject("deployment-link")
            root_node(info, current != path)
        return info

    def hash_regular(self, path):
        before = self.no_link_chain(path)
        if not stat.S_ISREG(before.st_mode) or before.st_nlink != 1 or before.st_size > MAX_FILE_BYTES:
            reject("file-bound-or-shape")
        fd = os.open(path, os.O_RDONLY | os.O_CLOEXEC | os.O_NOFOLLOW)
        result = hashlib.sha256()
        prefix = b""
        count = 0
        try:
            if identity(os.fstat(fd)) != identity(before):
                reject("file-open-substitution")
            while True:
                self.check()
                data = os.read(fd, min(65536, before.st_size - count + 1))
                if not data:
                    break
                if not prefix:
                    prefix = data[:64]
                count += len(data)
                if count > before.st_size or count > MAX_FILE_BYTES:
                    reject("file-growth")
                result.update(data)
            if count != before.st_size or identity(os.fstat(fd)) != identity(before):
                reject("file-changed")
        finally:
            os.close(fd)
        if identity(self.no_link_chain(path)) != identity(before):
            reject("named-file-changed")
        return result.hexdigest(), prefix

    def pin_os(self, literal):
        if literal not in self.files and len(self.files) >= MAX_FILES:
            reject("file-row-bound")
        resolved, links = self.resolve_os(literal)
        if links and literal not in self.aliases and len(self.aliases) >= MAX_ALIASES:
            reject("alias-row-bound")
        digest, prefix = self.hash_regular(resolved)
        after, after_links = self.resolve_os(literal)
        if after != resolved or links != after_links:
            reject("alias-before-after")
        prior = self.files.get(literal)
        if prior is not None and prior != digest:
            reject("file-hash-changed")
        if prior is None and len(self.files) >= MAX_FILES:
            reject("file-row-bound")
        self.files[literal] = digest
        if links:
            row = {"literal": literal, "links": links, "resolved_path": resolved,
                   "resolved_sha256": digest}
            previous = self.aliases.get(literal)
            if previous is not None and previous != row:
                reject("alias-changed")
            if previous is None and len(self.aliases) >= MAX_ALIASES:
                reject("alias-row-bound")
            self.aliases[literal] = row
        return digest, prefix

    def inventory(self, root):
        """No exclusions. Exact file+directory identities and file hashes before/after."""
        initial = self.no_link_chain(root)
        if not stat.S_ISDIR(initial.st_mode) or stat.S_IMODE(initial.st_mode) != 0o555:
            reject("sealed-root")
        entries = {}
        hashes = {}
        total = 0
        pending = [root]
        while pending:
            self.check()
            path = pending.pop()
            info = self.no_link_chain(path)
            if path in entries or len(entries) >= MAX_TREE_NODES:
                reject("tree-node-bound")
            entries[path] = identity(info)
            if stat.S_ISDIR(info.st_mode):
                if stat.S_IMODE(info.st_mode) != 0o555:
                    reject("sealed-directory")
                # Charge each name before storing/sorting; scandir is metadata only.
                names = []
                with os.scandir(path) as scan:
                    for item in scan:
                        self.check()
                        if len(names) + len(entries) + len(pending) >= MAX_TREE_NODES:
                            reject("tree-node-bound")
                        canonical_path(item.path)
                        names.append(item.path)
                if identity(os.lstat(path)) != identity(info):
                    reject("directory-changed")
                pending.extend(sorted(names, reverse=True))
            elif stat.S_ISREG(info.st_mode):
                if info.st_nlink != 1 or stat.S_IMODE(info.st_mode) not in (0o444, 0o555):
                    reject("sealed-file")
                total += info.st_size
                if total > MAX_TREE_BYTES:
                    reject("tree-byte-bound")
                digest, prefix = self.hash_regular(path)
                hashes[path] = (digest, prefix)
            else:
                reject("tree-special-node")
        return entries, hashes

    def run(self, args):
        """Only fixed readelf or ldconfig -p, sanitized env, counted pipes, original deadline."""
        self.check()
        if self.deadline - time.monotonic() <= 2:
            reject("command-reserve")
        process = None
        selector = None
        parts = {"stdout": bytearray(), "stderr": bytearray()}
        error = None
        try:
            process = subprocess.Popen(args, stdin=subprocess.DEVNULL,
                                       stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                       start_new_session=True, close_fds=True,
                                       env={"PATH": "/usr/bin:/bin", "LANG": "C", "LC_ALL": "C"})
            selector = selectors.DefaultSelector()
            for name in parts:
                stream = getattr(process, name)
                os.set_blocking(stream.fileno(), False)
                selector.register(stream, selectors.EVENT_READ, name)
            while selector.get_map():
                self.check()
                left = self.deadline - time.monotonic() - 1
                if left <= 0:
                    reject("command-deadline")
                for key, _ in selector.select(min(left, .1)):
                    data = os.read(key.fileobj.fileno(), 65536)
                    if not data:
                        selector.unregister(key.fileobj)
                    else:
                        if sum(map(len, parts.values())) + len(data) > MAX_COMMAND_BYTES:
                            reject("command-output-bound")
                        parts[key.data].extend(data)
            process.wait(timeout=max(.001, self.deadline - time.monotonic() - 1))
            if process.returncode != 0:
                reject("metadata-command-failed")
        except BaseException as caught:
            error = caught
        finally:
            # No postspawn path can skip kill/reap, each close, or group absence inspection.
            if process is not None:
                try:
                    group_present = True
                    try:
                        os.killpg(process.pid, 0)
                    except ProcessLookupError:
                        group_present = False
                    if group_present:
                        os.killpg(process.pid, signal.SIGKILL)
                except ProcessLookupError:
                    pass
                except BaseException as caught:
                    error = error or caught
                try:
                    left = self.deadline - time.monotonic()
                    if left <= 0:
                        reject("reap-deadline")
                    process.wait(timeout=left)
                except BaseException as caught:
                    error = error or caught
                for name in parts:
                    try:
                        getattr(process, name).close()
                    except BaseException as caught:
                        error = error or caught
                try:
                    os.killpg(process.pid, 0)
                except ProcessLookupError:
                    pass
                except BaseException as caught:
                    error = error or caught
                else:
                    error = error or AuditError("command-group-retained")
            if selector is not None:
                try:
                    selector.close()
                except BaseException as caught:
                    error = error or caught
        if error is not None:
            raise error
        self.check()
        return bytes(parts["stdout"]).decode("ascii", errors="strict")

    def initialize_tools(self):
        for name, choices in TOOLS.items():
            self.check()
            selected = next((p for p in choices if os.path.lexists(p)), None)
            if selected is None:
                reject("required-os-tool-missing")
            # Helpers are trusted executable ELF, not shell wrappers or caller commands.
            _, magic = self.pin_os(selected)
            if not magic.startswith(b"\x7fELF"):
                reject("os-tool-not-ELF")
            self.commands[name] = selected
        self.pin_os("/etc/ld.so.cache")
        text = self.run([self.commands["ldconfig"], "-p", "-C", "/etc/ld.so.cache"])
        lines = text.splitlines()
        if not lines or not re.fullmatch(r"\d+ libs found in cache `[^`]+\x27", lines[0].strip()):
            reject("cache-format")
        row = re.compile(r"\s*(\S+) \(([^)]+)\) => (\S+)\s*\Z")
        count = 0
        for line in lines[1:]:
            self.check()
            if not line.strip() or line.startswith("Cache generated by:"):
                continue
            match = row.fullmatch(line)
            if match is None:
                reject("cache-format")
            count += 1
            if count > MAX_FILES:
                reject("cache-row-bound")
            soname, abi, path = match.groups()
            if not SONAME.fullmatch(soname):
                reject("cache-soname")
            # Do not guess hwcap preference, x32, architecture or additional ABI qualifiers.
            if abi == "libc6,x86-64":
                canonical_path(path)
                self.cache.setdefault(soname, []).append(path)
            elif "x86-64" in abi:
                reject("unsupported-cache-abi")

    def inspect_elf(self, path):
        digest, prefix = self.pin_os(path)
        if len(prefix) < 20 or prefix[:7] != b"\x7fELF\x02\x01\x01" or int.from_bytes(prefix[18:20], "little") != 62:
            reject("unsupported-ELF-ABI")
        header = self.run([self.commands["readelf"], "-h", "--", path])
        if "ELF64" not in header or "Advanced Micro Devices X86-64" not in header:
            reject("unsupported-ELF-header")
        dynamic = self.run([self.commands["readelf"], "-d", "--", path])
        program = self.run([self.commands["readelf"], "-l", "--", path])
        needed = []
        for match in re.finditer(r"\(NEEDED\).*?\[([^\]]+)\]", dynamic):
            if len(needed) >= MAX_DEPENDENCIES:
                reject("dependency-row-bound")
            needed.append(match.group(1))
        if len(needed) > MAX_DEPENDENCIES or len(set(needed)) != len(needed) or any(not SONAME.fullmatch(n) for n in needed):
            reject("dependency-shape")
        if re.search(r"\((?:AUDIT|DEPAUDIT|FILTER|AUXILIARY)\)|NODEFLIB", dynamic):
            reject("unsupported-loader-feature")
        if re.search(r"\(RPATH\)", dynamic):
            # Inherited DT_RPATH is caller-context dependent; never reconstruct it by guessing.
            reject("unsupported-inherited-RPATH")
        runpaths = re.findall(r"\(RUNPATH\).*?\[([^\]]*)\]", dynamic)
        if len(runpaths) > 1:
            reject("runpath-shape")
        directories = []
        if runpaths:
            for part in runpaths[0].split(":"):
                expanded = part.replace("${ORIGIN}", os.path.dirname(path)).replace("$ORIGIN", os.path.dirname(path))
                if "$" in expanded or not part or not expanded.startswith("/"):
                    reject("unsupported-RUNPATH")
                canonical_path(expanded)
                if not os.path.isdir(expanded):
                    reject("runpath-directory-missing")
                self.resolve_directory(expanded)
                directories.append(expanded)
        interpreters = re.findall(r"Requesting program interpreter: ([^\]]+)\]", program)
        if len(interpreters) > 1:
            reject("interpreter-shape")
        interpreter = interpreters[0] if interpreters else None
        if interpreter:
            self.pin_os(canonical_path(interpreter))
        resolved = []
        for name in sorted(needed):
            runpath_candidates = [d + "/" + name for d in directories if os.path.lexists(d + "/" + name)]
            cache_rows = self.cache.get(name, [])
            # Conservative closed resolution: exactly one literal across direct RUNPATH/cache.
            # No arbitrary choose-first cache entry or ignored conflicting candidate.
            candidates = sorted(set(runpath_candidates + cache_rows))
            if len(candidates) != 1:
                self.latch_dependency_failure(path, name, runpath_candidates, cache_rows, candidates)
                reject("dependency-unresolved-or-ambiguous")
            selected = candidates[0]
            dep_hash, _ = self.pin_os(selected)
            resolved.append({"soname": name, "path": selected, "sha256": dep_hash})
        self.pin_os(path)
        return {"path": path, "sha256": digest, "interpreter": interpreter, "resolved": resolved}

    def latch_dependency_failure(self, requester, soname, runpath, cache, combined):
        """Retain bounded first rejection data only; never choose a dependency or mask failure."""
        if self.failure_context is not None:
            return
        try:
            self.check()
            canonical_path(requester)
            if not SONAME.fullmatch(soname):
                return
            rows = []
            for group in (runpath, cache, combined):
                self.check()
                if len(group) > MAX_FILES:
                    return
                copy = []
                for path in group[:8]:
                    self.check()
                    canonical_path(path)
                    copy.append(path)
                rows.append(copy)
            record = {"schema": "issue779-os-audit-dependency-failure-context-v1",
                      "category": "dependency-unresolved-or-ambiguous", "requester": requester,
                      "soname": soname, "candidate_count": len(combined),
                      "runpath_count": len(runpath), "cache_count": len(cache),
                      "truncated": any(len(group) > 8 for group in (runpath, cache, combined)),
                      "runpath_candidates": rows[0], "cache_candidates": rows[1],
                      "combined_candidates": rows[2]}
            # Count encoding incrementally; never publish a truncated diagnostic.
            data = bytearray()
            for chunk in json.JSONEncoder(ensure_ascii=True, separators=(",", ":"), sort_keys=True).iterencode(record):
                self.check()
                encoded = chunk.encode("ascii")
                if len(data) + len(encoded) > 32768:
                    return
                data.extend(encoded)
            self.check()
            self.failure_context = bytes(data)
        except BaseException:
            # Diagnostic failure does not replace the existing fixed rejection.
            return

    def publish_failure_context(self, output):
        """Best-effort private optional sidecar on the original deadline, with no success authority."""
        try:
            self.check()
            if self.failure_context is None or not 0 < len(self.failure_context) <= 32768:
                return False
            self.publish(output + ".failure-context.json", self.failure_context)
            self.check()
            return True
        except BaseException:
            return False

    def resolve_directory(self, path):
        # RUNPATH directories must themselves be strict no-link root-owned OS/deployment paths.
        info = self.no_link_chain(path)
        if not stat.S_ISDIR(info.st_mode):
            reject("runpath-directory")

    def require_fixed_python_bootstrap(self):
        """Reject alternate archive/virtual-environment bootstrap before parser execution.

        All parent directories are actual root-owned non-writable OS directories.
        The fixture repeats these fixed absence checks immediately before invoking
        the interpreter with -I -S -B. No caller package path is introduced.
        """
        for path in ("/usr/lib/python312.zip", "/usr/bin/pyvenv.cfg", "/usr/pyvenv.cfg"):
            self.check()
            self.resolve_directory(os.path.dirname(path))
            if os.path.lexists(path):
                reject("python-alternate-bootstrap")
        self.check()

    def pin_python_standard_library(self):
        """Pin the complete fixed OS stdlib, including existing bytecode and native modules.

        This is private native-test parser custody data. It grants no worker or
        result authority. Root-owned non-writable directories are mandatory;
        file aliases use the existing recorded OS-only link rule. No directory
        alias, exclusion, caller path or package discovery is accepted. The
        original audit deadline and node/file/byte limits cover both passes.
        """
        root = PYTHON_STDLIB_ROOT
        canonical_path(root)
        entries, hashes, native, pending = {}, {}, [], [root]
        total = 0
        while pending:
            self.check()
            path = pending.pop()
            info = self.sample(path)
            if path in entries or len(entries) >= MAX_TREE_NODES:
                reject("python-stdlib-node-bound")
            entries[path] = identity(info)
            if stat.S_ISDIR(info.st_mode):
                self.resolve_directory(path)
                names = []
                with os.scandir(path) as scan:
                    for item in scan:
                        self.check()
                        canonical_path(item.path)
                        if len(names) + len(entries) + len(pending) >= MAX_TREE_NODES:
                            reject("python-stdlib-node-bound")
                        names.append(item.path)
                if identity(os.lstat(path)) != identity(info):
                    reject("python-stdlib-directory-changed")
                pending.extend(sorted(names, reverse=True))
            elif stat.S_ISREG(info.st_mode) or stat.S_ISLNK(info.st_mode):
                resolved, _ = self.resolve_os(path)
                actual = self.sample(resolved)
                total += actual.st_size
                if total > MAX_TREE_BYTES:
                    reject("python-stdlib-byte-bound")
                digest, prefix = self.pin_os(path)
                hashes[path] = (digest, resolved)
                if prefix.startswith(b"\x7fELF"):
                    native.append(resolved)
            else:
                reject("python-stdlib-node-kind")
        self.check()
        if not hashes:
            reject("python-stdlib-empty")
        return (entries, hashes), sorted(set(native))

    def build(self, runtime, tool, include_tool, helper, n07_helper):
        self.roots = (runtime, tool, helper, n07_helper)
        self.require_fixed_python_bootstrap()
        python_before, python_native = self.pin_python_standard_library()
        pending = list(self.commands.values()) + python_native
        for root in self.roots:
            entries, hashes = self.inventory(root)
            self.trees[root] = (entries, hashes)
            for path, (_, prefix) in hashes.items():
                if prefix.startswith(b"\x7fELF") and (root == runtime or root == helper or root == n07_helper or include_tool):
                    pending.append(path)
        while pending:
            self.check()
            path = pending.pop()
            if path in self.elf:
                continue
            if len(self.elf) >= MAX_ELFS:
                reject("ELF-row-bound")
            row = self.inspect_elf(path)
            self.elf[path] = row
            pending.extend(dep["path"] for dep in row["resolved"])
            if row["interpreter"]:
                pending.append(row["interpreter"])
        # Recompute complete node/file/hash sets, not merely already-selected ELF files.
        self.require_fixed_python_bootstrap()
        if self.pin_python_standard_library()[0] != python_before:
            reject("python-stdlib-before-after")
        for root in self.roots:
            if self.inventory(root) != self.trees[root]:
                reject("deployment-before-after")
        for path, digest in list(self.files.items()):
            actual, _ = self.pin_os(path)
            if actual != digest:
                reject("OS-before-after")
        for path, expected in self.nodes.items():
            self.check()
            if identity(os.lstat(path)) != expected:
                reject("OS-node-before-after")
        record = {"schema": SCHEMA,
                  "aliases": [self.aliases[p] for p in sorted(self.aliases)],
                  "elf": [self.elf[p] for p in sorted(self.elf)],
                  "files": [{"path": p, "sha256": self.files[p]} for p in sorted(self.files)]}
        self.check()
        encoded = bytearray()
        encoder = json.JSONEncoder(ensure_ascii=True, separators=(",", ":"), sort_keys=True)
        for chunk in encoder.iterencode(record):
            self.check()
            data = chunk.encode("ascii")
            if len(encoded) + len(data) > MAX_AUDIT:
                reject("audit-byte-bound")
            encoded.extend(data)
        data = bytes(encoded)
        self.check()
        return data

    def publish(self, output, data):
        """Exclusive caller-owned 0600 data under retained private parent; late error stays error."""
        canonical_path(output)
        parent, name = os.path.split(output)
        # Output may be in a private caller directory below sticky /tmp. No input/OS alias exception.
        paths = ["/"]
        current = ""
        for part in parent[1:].split("/"):
            current += "/" + part
            paths.append(current)
        retained = []
        fd = None
        failure = None
        try:
            prior = None
            for path in paths:
                self.check()
                info = os.lstat(path)
                if not stat.S_ISDIR(info.st_mode) or stat.S_ISLNK(info.st_mode):
                    reject("output-parent-link")
                if path == parent:
                    if info.st_uid != os.geteuid() or stat.S_IMODE(info.st_mode) != 0o700:
                        reject("output-private-parent")
                elif info.st_uid != 0 or (info.st_mode & 0o022 and not info.st_mode & stat.S_ISVTX):
                    reject("output-ancestor")
                opened = os.open(path if prior is None else os.path.basename(path),
                                 os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC,
                                 dir_fd=prior)
                retained.append((path, opened, identity(info)))
                if identity(os.fstat(opened)) != identity(info):
                    reject("output-parent-substitution")
                prior = opened
            self.check()
            fd = os.open(name, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC,
                         0o600, dir_fd=prior)
            os.fchmod(fd, 0o600)
            view = memoryview(data)
            while view:
                self.check()
                written = os.write(fd, view)
                if written <= 0:
                    reject("output-write")
                view = view[written:]
            os.fsync(fd)
            info = os.fstat(fd)
            named = os.stat(name, dir_fd=prior, follow_symlinks=False)
            if (identity(info) != identity(named) or info.st_uid != os.geteuid()
                    or info.st_nlink != 1 or stat.S_IMODE(info.st_mode) != 0o600
                    or info.st_size != len(data)):
                reject("output-metadata")
            for path, opened, expected in retained:
                if identity(os.fstat(opened)) != expected or identity(os.lstat(path)) != expected:
                    # Parent mtime changes when creating this file: compare custody fields below instead.
                    if identity(os.fstat(opened))[:6] != expected[:6] or identity(os.lstat(path))[:6] != expected[:6]:
                        reject("output-parent-changed")
            self.check()
        except BaseException as caught:
            failure = caught
        finally:
            if fd is not None:
                try:
                    os.close(fd)
                except BaseException as caught:
                    failure = failure or caught
            for _, opened, _ in reversed(retained):
                try:
                    os.close(opened)
                except BaseException as caught:
                    failure = failure or caught
        if failure is not None:
            raise failure
        self.check()


class ClosedParser(argparse.ArgumentParser):
    """Reject argument errors without reproducing caller values."""

    def error(self, message):
        reject("arguments")


def main():
    parser = ClosedParser(description=__doc__)
    parser.add_argument("--runtime-root", required=True)
    parser.add_argument("--tool-root", required=True)
    parser.add_argument("--helper-root", required=True)
    parser.add_argument("--n07-helper-root", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--deadline-monotonic", required=True, type=float)
    parser.add_argument("--include-tool-elf", action="store_true")
    audit = None
    output = None
    try:
        args = parser.parse_args()
        if sys.platform != "linux" or os.uname().machine != "x86_64" or os.geteuid() == 0:
            reject("unprivileged-Linux-x64-required")
        runtime = canonical_path(args.runtime_root)
        tool = canonical_path(args.tool_root)
        if runtime == tool or runtime.startswith(tool + "/") or tool.startswith(runtime + "/"):
            reject("overlapping-inputs")
        output = canonical_path(args.output)
        if any(output == root or output.startswith(root + "/") for root in (runtime, tool)):
            reject("output-in-input")
        audit = Audit(args.deadline_monotonic)
        audit.initialize_tools()
        helper = canonical_path(args.helper_root)
        if any(helper == r or helper.startswith(r + "/") or r.startswith(helper + "/") for r in (runtime, tool)):
            reject("helper-tree-overlap")
        n07_helper = canonical_path(args.n07_helper_root)
        if any(n07_helper == r or n07_helper.startswith(r + "/") or r.startswith(n07_helper + "/") for r in (runtime, tool, helper)):
            reject("n07-helper-tree-overlap")
        if any(output == r or output.startswith(r + "/") for r in (helper, n07_helper)):
            reject("output-in-helper-input")
        data = audit.build(runtime, tool, args.include_tool_elf, helper, n07_helper)
        publication_sha256 = hashlib.sha256(data).hexdigest()
        audit.publish(output, data)
        audit.check()
        print("OS_AUDIT_PROPOSED:METADATA_ONLY:" + publication_sha256, flush=True)
        audit.check()
        return 0
    except AuditError as error:
        if audit is not None and output is not None:
            audit.publish_failure_context(output)
        print("OS_AUDIT_REJECTED:" + error.args[0], file=sys.stderr)
    except Exception:
        print("OS_AUDIT_REJECTED:metadata-operation", file=sys.stderr)
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
