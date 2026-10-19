"""Actual root N13/N14 bridge; current prerequisites never imply a native pass.

The caller owns root entry and the absolute monotonic deadline. This fixed adapter
copies complete verified current images, seals them, runs the retained shell once,
joins that child and checks the actual private record/pipes. No build is executed.
See README.md for the exact context, evidence and reusable materialization APIs.
"""
from __future__ import annotations

import hashlib
import json
import math
import os
from pathlib import Path
import re
import stat
import subprocess
import time
import types
from datetime import datetime, timezone

HERE = Path(__file__).resolve().parent
CONTEXT_KEYS = frozenset(("source", "selector", "capture_path", "capture_sha256",
    "build_receipt_path", "build_receipt_sha256", "tool_root", "runtime_root",
    "output", "generation", "binding_validator", "binding_validator_sha256", "workflow_identity"))
WRAPPERS = {"N13": "checkpoint-n13-owner-killed.sh", "N14": "checkpoint-n14-owner-stopped.sh"}
COMMON = "checkpoint-owner-death-common.sh"
PARSER_REL = ".github/issue779-checkpoint1/procedures/parsers/check_owner_death_record.py"
BINDING_REL = ".github/issue779-checkpoint1/binding.py"
PREFIX = ".github/issue779-checkpoint1/procedures/owner-death/"
MAX_JSON = 8 * 1024 * 1024
MAX_LOG = 8 * 1024 * 1024


def need(value, category):
    if not value:
        raise ValueError("owner-death-" + category)


def digest(raw):
    return hashlib.sha256(raw).hexdigest()


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":")).encode() + b"\n"


def decode(raw):
    def pairs(rows):
        value = {}
        for key, item in rows:
            need(key not in value, "duplicate-json-key")
            value[key] = item
        return value
    need(type(raw) is bytes and len(raw) <= MAX_JSON, "json-bound")
    return json.loads(raw, object_pairs_hook=pairs)


def remaining(deadline):
    need(type(deadline) is float and math.isfinite(deadline), "deadline")
    left = deadline - time.monotonic()
    need(left > 0, "original-deadline")
    return left


def identity(s):
    return (s.st_dev, s.st_ino, s.st_mode, s.st_uid, s.st_gid, s.st_nlink,
            s.st_size, s.st_mtime_ns, s.st_ctime_ns)


def read(path, cap, *, deadline):
    p = Path(path)
    need(p.is_absolute() and p.resolve(strict=True) == p, "file-path")
    fd = os.open(p, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK | os.O_CLOEXEC)
    try:
        before = os.fstat(fd)
        need(stat.S_ISREG(before.st_mode) and before.st_nlink == 1
             and 0 <= before.st_size <= cap, "file-shape")
        data = bytearray()
        while len(data) <= before.st_size:
            remaining(deadline)
            block = os.read(fd, min(1024 * 1024, before.st_size + 1 - len(data)))
            if not block:
                break
            data.extend(block)
        need(len(data) == before.st_size and identity(before) == identity(os.fstat(fd))
             == identity(p.lstat()), "file-changed")
        return bytes(data)
    finally:
        os.close(fd)


def save(path, raw):
    need(type(raw) is bytes and len(raw) <= MAX_LOG, "retained-bound")
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC, 0o600)
    try:
        view = memoryview(raw)
        while view:
            n = os.write(fd, view)
            need(n > 0, "retained-short-write")
            view = view[n:]
        os.fchown(fd, 0, 0)
        os.fchmod(fd, 0o600)
        os.fsync(fd)
    finally:
        os.close(fd)


def _load(path, expected, *, deadline):
    raw = read(path, 131072, deadline=deadline)
    need(digest(raw) == expected, "module-binding")
    module = types.ModuleType("c1_owner_" + Path(path).stem)
    module.__file__ = str(path)
    # Execute the exact captured bytes already read, not a second path lookup.
    exec(compile(raw, str(path), "exec", dont_inherit=True), module.__dict__)
    return module


def _context(context, deadline):
    need(type(context) is dict and set(context) == CONTEXT_KEYS, "context-fields")
    need(context["selector"] in WRAPPERS and re.fullmatch("[0-9a-f]{40}", context["source"])
         and re.fullmatch("[0-9a-f]{32}", context["generation"]), "context-identity")
    need(re.fullmatch(r"github-actions-[0-9]{1,20}-[1-9][0-9]{0,3}", context["workflow_identity"]),
         "workflow-identity")
    need(os.geteuid() == 0 and os.uname().sysname == "Linux", "already-root-linux-required")
    need(remaining(deadline) <= 900, "outer-deadline-bound")
    for key in ("capture_sha256", "build_receipt_sha256", "binding_validator_sha256"):
        need(type(context[key]) is str and re.fullmatch("[0-9a-f]{64}", context[key]), "input-hash")
    cap_raw = read(context["capture_path"], MAX_JSON, deadline=deadline)
    build_raw = read(context["build_receipt_path"], MAX_JSON, deadline=deadline)
    need(digest(cap_raw) == context["capture_sha256"]
         and digest(build_raw) == context["build_receipt_sha256"], "input-binding")
    capture, build = decode(cap_raw), decode(build_raw)
    need(capture["source"] == context["source"], "source")
    source_root = Path(build["source_root"])
    need(Path(context["binding_validator"]) == source_root / BINDING_REL
         and context["binding_validator_sha256"] == capture["files"][BINDING_REL]["sha256"],
         "fixed-binding-validator")
    binding = _load(context["binding_validator"], context["binding_validator_sha256"], deadline=deadline)
    binding.verify_build(build, cap_raw, context["source"], context["selector"], deadline=deadline)
    need(build["tool"]["root"] == context["tool_root"]
         and build["runtime"]["root"] == context["runtime_root"], "selected-roots")
    need(HERE == source_root / PREFIX.rstrip("/"), "adapter-source-root")
    for name in ("adapter.py", "workspace.py", COMMON, WRAPPERS[context["selector"]]):
        need(digest(read(HERE / name, 131072, deadline=deadline))
             == capture["files"][PREFIX + name]["sha256"], "current-procedure")
    return binding, capture, build, cap_raw, build_raw


def _copy_file(source, target, row, *, deadline, seal=True):
    need(source.resolve(strict=True) == source, "copy-alias")
    infd = os.open(source, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK | os.O_CLOEXEC)
    outfd = None
    try:
        before = os.fstat(infd)
        need(stat.S_ISREG(before.st_mode) and before.st_nlink == 1
             and before.st_size == row["bytes"] and 0 <= before.st_size <= 2 * 1024**3
             and format(stat.S_IMODE(before.st_mode), "04o") == row["mode"], "current-copy-source")
        outfd = os.open(target, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC, 0o600)
        sha, total = hashlib.sha256(), 0
        while True:
            remaining(deadline)
            block = os.read(infd, 1024 * 1024)
            if not block:
                break
            total += len(block)
            need(total <= row["bytes"], "current-copy-growth")
            sha.update(block)
            view = memoryview(block)
            while view:
                n = os.write(outfd, view)
                need(n > 0, "current-copy-short-write")
                view = view[n:]
        need(total == row["bytes"] and sha.hexdigest() == row["sha256"]
             and identity(before) == identity(os.fstat(infd)) == identity(source.lstat()), "current-copy-changed")
        os.fchown(outfd, 0, 0)
        os.fchmod(outfd, (0o555 if int(row["mode"], 8) & 0o111 else 0o444) if seal else int(row["mode"], 8))
        os.fsync(outfd)
    finally:
        if outfd is not None:
            os.close(outfd)
        os.close(infd)


def _copy_tree(source, target, rows, directories, workspace, *, deadline, seal=True):
    need(len(rows) + len(directories) <= 65536
         and sum(r["bytes"] for r in rows.values()) <= 4 * 1024**3, "current-tree-envelope")
    target.mkdir(mode=0o700)
    for name in sorted(directories, key=lambda s: (s.count("/"), s)):
        remaining(deadline)
        workspace.need(workspace.valid_rel(name), "current-directory-name")
        (target / name).mkdir(mode=0o700)
    for name, row in sorted(rows.items()):
        workspace.need(workspace.valid_rel(name), "current-file-name")
        _copy_file(source / name, target / name, row, deadline=deadline, seal=seal)
    directory_mode = "0555" if seal else "0700"
    nodes = {"files": {n: {**row, "mode": ("0555" if int(row["mode"], 8) & 0o111 else "0444") if seal else row["mode"]}
                      for n, row in rows.items()},
             "directories": {".": {"mode": directory_mode}, **{n: {"mode": directory_mode} for n in directories}}}
    for name in sorted(directories, key=lambda s: (-s.count("/"), s)):
        os.chmod(target / name, int(directory_mode, 8), follow_symlinks=False)
    os.chmod(target, int(directory_mode, 8))
    boot_end = workspace.boottime_ms() + int(remaining(deadline) * 1000)
    if seal:
        workspace.verify_materialized(target, nodes, boot_end)
    else:
        # N16 owns its original later source seal. Verify these actual retained
        # modes/membership now; never label a sealed projection as writable input.
        seen_files, seen_dirs = set(), set()
        for base, dirs, files in os.walk(target, followlinks=False):
            remaining(deadline)
            bp = Path(base)
            rel = bp.relative_to(target).as_posix()
            st = bp.lstat()
            need(stat.S_ISDIR(st.st_mode) and st.st_uid == st.st_gid == 0
                 and stat.S_IMODE(st.st_mode) == 0o700, "retained-source-directory")
            seen_dirs.add(rel)
            for name in dirs:
                need(not (bp / name).is_symlink(), "retained-source-link")
            for name in files:
                leaf = bp / name
                relfile = leaf.relative_to(target).as_posix()
                row = nodes["files"][relfile]
                st = workspace.digest_file(leaf, row["sha256"], 2 * 1024**3, boot_end)
                need(st.st_size == row["bytes"] and stat.S_IMODE(st.st_mode) == int(row["mode"], 8),
                     "retained-source-mode")
                seen_files.add(relfile)
        need(seen_files == set(nodes["files"]) and seen_dirs == set(nodes["directories"]), "retained-source-membership")
    return nodes


def prepare_inputs(context, *, deadline):
    """Retain complete verified current source/images as sealed root-owned copies.

    Root-only fixture I/O. Returns paths/maps, not native owner/account capabilities.
    Partial generations are retained on failure and are never reused or deleted.
    """
    binding, capture, build, cap_raw, build_raw = _context(context, deadline)
    return prepare_verified_inputs(context, binding, capture, build, cap_raw, build_raw, deadline=deadline)


def prepare_verified_inputs(context, binding, capture, build, cap_raw, build_raw, *, deadline, source_sealed=True):
    """Shared root staging after the caller's fixed current provenance boundary.

    Actual source/build verification is repeated. Set source_sealed=False only
    when the original procedure owns the subsequent seal (N16); source modes then
    remain 0644/0755 and directories0700. Tool/runtime are always sealed. Truthful
    TSV/node maps are written from the actual retained copies, not build claims.
    """
    need(os.geteuid() == 0 and type(source_sealed) is bool, "retained-root-required")
    need(re.fullmatch("[0-9a-f]{32}", context["generation"]), "retained-generation")
    need(digest(cap_raw) == context["capture_sha256"] and digest(build_raw) == context["build_receipt_sha256"], "retained-input-hashes")
    binding.verify_build(build, cap_raw, context["source"], context["selector"], deadline=deadline)
    need(capture == binding.decode(cap_raw) and build == binding.decode(build_raw)
         and build["tool"]["root"] == context["tool_root"] and build["runtime"]["root"] == context["runtime_root"], "retained-input-data")
    workspace = _load(HERE / "workspace.py", capture["files"][PREFIX + "workspace.py"]["sha256"], deadline=deadline)
    retained = Path("/var/lib/appsurface-evidence-input-" + context["generation"])
    workspace.safe_ancestors(retained.parent)
    need(not os.path.lexists(retained), "retained-generation-exists")
    retained.mkdir(mode=0o700)
    roots, nodes = {}, {}
    for role in ("source", "tool", "runtime"):
        if role == "source":
            root = Path(build["source_root"])
            rows = {n: {"sha256": row["sha256"], "mode": row["mode"], "bytes": (root / n).lstat().st_size}
                    for n, row in capture["files"].items()}
            directories = set()
            for name in rows:
                parts = name.split("/")
                directories.update("/".join(parts[:i]) for i in range(1, len(parts)))
        else:
            root = Path(build[role]["root"])
            rows, directories = build[role]["files"], set(build[role]["directories"])
            need(len(rows) + len(directories) + 1 <= 8192
                 and sum(row["bytes"] for row in rows.values()) <= 1024**3
                 and all(row["bytes"] <= 256 * 1024**2 for row in rows.values()), "historical-core-envelope")
        roots[role] = retained / role
        nodes[role] = _copy_tree(root, roots[role], rows, directories, workspace, deadline=deadline,
                                 seal=(role != "source" or source_sealed))
    control = retained / "control"
    control.mkdir(mode=0o700)
    save(control / "source.json", cap_raw)
    save(control / "build-receipt.json", build_raw)
    copies = {"binding.py": Path(context["binding_validator"]),
              "check_owner_death_record.py": Path(build["source_root"]) / PARSER_REL,
              COMMON: HERE / COMMON}
    if context["selector"] in WRAPPERS:
        copies[WRAPPERS[context["selector"]]] = HERE / WRAPPERS[context["selector"]]
    for name, source in copies.items():
        raw = read(source, 131072, deadline=deadline)
        rel = BINDING_REL if name == "binding.py" else PARSER_REL if name == "check_owner_death_record.py" else PREFIX + name
        need(digest(raw) == capture["files"][rel]["sha256"], "control-copy-binding")
        save(control / name, raw)
        os.chmod(control / name, 0o444)
    for role in ("source", "tool", "runtime"):
        node = {"schema": "issue779-build-node-inventory-v1", "root_name": role, **nodes[role]}
        tsv = "".join(f"{row['mode']}\t{row['sha256']}\t{name}\n" for name, row in sorted(nodes[role]["files"].items())).encode()
        save(control / (role + ".tsv"), tsv)
        save(control / (role + "-nodes.json"), canonical(node))
    binding.verify_build(build, cap_raw, context["source"], context["selector"], deadline=deadline)
    os.chmod(retained, 0o555)
    return {"retained": retained, "control": control, "roots": roots, "nodes": nodes,
            "capture": capture, "build": build, "capture_raw": cap_raw, "build_raw": build_raw}


def materialize(context, prepared, *, deadline):
    """Create the fixed historical empty-observation workspace/request, without launch.

    Reuse only for a procedure whose original policy/request is identical. The
    original job clock starts once here, before its workspace copy, with 240s and
    the original 60s request cleanup reserve; the caller's outer clock is unchanged.
    """
    capture, build = prepared["capture"], prepared["build"]
    workspace = _load(HERE / "workspace.py", capture["files"][PREFIX + "workspace.py"]["sha256"], deadline=deadline)
    parent = subprocess.run(["git", "--no-optional-locks", "-c", "core.fsmonitor=false",
        "-c", "safe.directory=" + build["source_root"], "rev-parse", context["source"] + "^"],
        cwd=build["source_root"], stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
        timeout=min(15, remaining(deadline)), check=False)
    need(parent.returncode == 0 and re.fullmatch(b"[0-9a-f]{40}\\n", parent.stdout), "current-source-parent")
    base_revision = parent.stdout.decode().strip()
    now_wall, now_boot = time.time(), workspace.boottime_ms()
    job_utc = datetime.fromtimestamp(int(now_wall) + 240, timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    boot_end = now_boot + 240000
    work_end = min(workspace.calculate_work_deadline(now_wall, now_boot, workspace.parse_deadline(job_utc), boot_end),
                   now_boot + int(remaining(deadline) * 1000))
    need("fixture-policy.json" not in prepared["nodes"]["tool"]["files"], "policy-collision")
    stage, private = workspace.FIXTURE_ROOT / context["generation"], workspace.PRIVATE_ROOT / context["generation"]
    for root in (workspace.FIXTURE_ROOT, workspace.PRIVATE_ROOT):
        workspace.safe_ancestors(root.parent)
        if not os.path.lexists(root):
            root.mkdir(mode=0o755)
            os.chmod(root, 0o755)
        s = root.lstat()
        need(stat.S_ISDIR(s.st_mode) and s.st_uid == s.st_gid == 0
             and stat.S_IMODE(s.st_mode) == 0o755 and not root.is_symlink(), "fixture-parent-custody")
    need(not os.path.lexists(stage) and not os.path.lexists(private), "workspace-generation-exists")
    stage.mkdir(mode=0o700)
    private.mkdir(mode=0o700)
    for role, name in (("tool", "tool"), ("runtime", "runtime"), ("source", "subject")):
        workspace.copy_role(prepared["roots"][role], stage / name, prepared["nodes"][role], work_end)
    os.chmod(stage / "tool", 0o755)
    policy = {"Id":"checkpoint-empty","Version":"1","ConservativeProfileId":"conservative",
        "Profiles":[{"Id":"empty","Scope":"Targeted","Resources":[],"Producers":[],"Obligations":[]},
                    {"Id":"conservative","Scope":"Release","Resources":[{"Id":"build","Readiness":"completion","DeadlineSeconds":1,"Requires":[]}],"Producers":[],"Obligations":[]}],
        "Rules":[{"Id":"docs","Pattern":"docs/**","ProfileId":"empty","Precedence":0}]}
    policy_path = stage / "tool/fixture-policy.json"
    policy_raw = json.dumps(policy, sort_keys=True, separators=(",", ":")).encode()
    save(policy_path, policy_raw)
    os.chmod(policy_path, 0o444)
    os.chmod(stage / "tool", 0o555)
    os.chmod(stage, 0o555)
    for role, name in (("tool", "tool"), ("runtime", "runtime"), ("source", "subject")):
        workspace.verify_materialized(stage / name, prepared["nodes"][role], work_end,
            allow_policy=(role == "tool"), policy_path=policy_path)
        workspace.verify_materialized(prepared["roots"][role], prepared["nodes"][role], work_end)
    need(all(n in prepared["nodes"]["tool"]["files"] for n in
             ("ForgeTrust.AppSurface.Cli.dll", "ForgeTrust.AppSurface.Cli.deps.json", "ForgeTrust.AppSurface.Cli.runtimeconfig.json"))
         and "dotnet" in prepared["nodes"]["runtime"]["files"], "normal-fdd-inputs")
    request = {"schema":"evidence-supervisor-linux-v1","mode":"observation",
        "tool_root":str(stage / "tool"),"runtime_root":str(stage / "runtime"),
        "runtime_host":str(stage / "runtime/dotnet"),"entry_path":str(stage / "tool/ForgeTrust.AppSurface.Cli.dll"),
        "policy_file":str(policy_path),"subject_root":str(stage / "subject"),
        "base_revision":base_revision,"subject_revision":context["source"],"workflow_identity":context["workflow_identity"],
        "paths":["docs/designs/issue-779-csharp-supervision-core.md"],
        "observation_profile_ids":["empty"],"observation_producer_ids":[],
        "admission_seconds":10,"start_seconds":30,"collection_seconds":10,
        "cleanup_seconds":60,"stopping_seconds":5,"job_deadline_utc":job_utc}
    request_raw = canonical(request)
    request_path = private / "request.json"
    need(len(request_raw) <= 65536 and not (stage / "subject" / request["paths"][0]).is_symlink(), "request-bound")
    save(request_path, request_raw)
    checksums = f"{digest(request_raw)}  {request_path}\n{digest(policy_raw)}  {policy_path}\n".encode()
    save(private / "request-policy.sha256", checksums)
    need(workspace.read_pinned(request_path, digest(request_raw), 65536, work_end)[1] == request_raw
         and workspace.read_pinned(policy_path, digest(policy_raw), 4096, work_end)[1] == policy_raw
         and workspace.read_pinned(private / "request-policy.sha256", digest(checksums), 4096, work_end)[1] == checksums,
         "request-policy-final-binding")
    return {"stage": stage, "private": private, "request": request_path,
            "request_sha256": digest(request_raw), "policy_sha256": digest(policy_raw), "job_deadline_utc": job_utc}


def run_bounded(argv, stdout, stderr, *, deadline):
    """Run one fixed fixture child; terminate early enough for its original 30s cleanup.

    Never signal the launcher's process group from here. The retained shell owns
    launcher/unit cleanup. A timeout/nonzero/forced kill is a failure, not evidence.
    """
    stop_at = min(time.monotonic() + 600, deadline - 30)
    need(stop_at > time.monotonic(), "dispatch-reserve")
    child = subprocess.Popen(argv, stdin=subprocess.DEVNULL, stdout=stdout, stderr=stderr, close_fds=True,
        env={"PATH": "/usr/sbin:/usr/bin:/sbin:/bin", "HOME": "/nonexistent", "LANG": "C.UTF-8"})
    timed_out = False
    try:
        try:
            code = child.wait(timeout=max(.001, stop_at - time.monotonic()))
        except subprocess.TimeoutExpired:
            timed_out = True
            child.terminate()
            try:
                code = child.wait(timeout=max(.001, deadline - time.monotonic()))
            except subprocess.TimeoutExpired:
                child.kill()
                child.wait(timeout=max(.001, deadline - time.monotonic()))
                raise ValueError("owner-death-unresolved-cleanup")
    finally:
        if child.poll() is None:
            child.terminate()
            try:
                child.wait(timeout=max(.001, deadline - time.monotonic()))
            except subprocess.TimeoutExpired:
                child.kill()
                child.wait(timeout=max(.001, deadline - time.monotonic()))
    need(not timed_out and code == 0, "fixture-terminal")
    remaining(deadline)
    return code


def validate_evidence(context, evidence_raw, *, capture_raw):
    """Revalidate actual retained record data and immutable bindings; no native authority.

    The shared aggregator additionally binds retained private raw logs to this packet.
    This function does not start a process, inspect an owner or grant a capability.
    """
    evidence, capture = decode(evidence_raw), decode(capture_raw)
    need(digest(capture_raw) == context["capture_sha256"] and capture["source"] == context["source"], "evidence-capture")
    need(set(evidence) == {"schema", "source", "selector", "generation", "source_capture_sha256",
         "build_receipt_sha256", "procedure_sha256", "common_sha256", "parser_sha256", "record", "retained"}, "evidence-fields")
    selector = context["selector"]
    need(selector in WRAPPERS and evidence["schema"] == "issue779-checkpoint1-owner-death-evidence-v1"
         and evidence["source"] == context["source"] and evidence["selector"] == selector
         and evidence["generation"] == context["generation"]
         and evidence["source_capture_sha256"] == context["capture_sha256"]
         and evidence["build_receipt_sha256"] == context["build_receipt_sha256"], "evidence-identity")
    for key, path in (("procedure_sha256", PREFIX + WRAPPERS[selector]), ("common_sha256", PREFIX + COMMON),
                      ("parser_sha256", PARSER_REL)):
        need(evidence[key] == capture["files"][path]["sha256"], "evidence-procedure")
    # Source-owned data parser is loaded by the fixed caller before this pure callback.
    need(_PARSER_SHA == capture["files"][PARSER_REL]["sha256"], "fixed-parser-binding")
    _PARSER.validate(evidence["record"])
    record = evidence["record"]
    need(record["case"] == selector and record["generation"] == context["generation"], "record-generation")
    retained = evidence["retained"]
    expected = {"root.stdout", "root.stderr", "fixture-stdout.log", "fixture-stderr.log", "owner-death-result.json", "QUARANTINED"}
    need(type(retained) is dict and set(retained) == expected, "retained-files")
    for row in retained.values():
        need(type(row) is dict and set(row) == {"sha256", "bytes"}
             and type(row["sha256"]) is str and re.fullmatch("[0-9a-f]{64}", row["sha256"])
             and type(row["bytes"]) is int and 0 <= row["bytes"] <= MAX_LOG, "retained-row")
    for leaf, key in (("root.stdout", "stdout"), ("root.stderr", "stderr")):
        need(retained[leaf]["sha256"] == record["pipes"][key + "_sha256"]
             and retained[leaf]["bytes"] == record["pipes"][key + "_bytes"], "record-pipe-binding")
    expected_record = canonical(record)
    need(retained["owner-death-result.json"] == {"sha256": digest(expected_record), "bytes": len(expected_record)}, "embedded-record-binding")
    phase = (record["phase_line"] + "\n").encode("ascii")
    fixture_line = f"OWNER_DEATH_MEASURED:{selector}:QUARANTINED:NOT_NATIVE_ACCEPTANCE\n".encode()
    for leaf, raw in (("root.stdout", phase), ("fixture-stdout.log", fixture_line),
                      ("fixture-stderr.log", b""), ("QUARANTINED", b"OWNER_DEATH_OBSERVED_OUTPUT_QUARANTINED\n")):
        need(retained[leaf] == {"sha256": digest(raw), "bytes": len(raw)}, "fixed-retained-data")
    return {"case": selector, "passed": True, "skipped": False,
            "procedure_sha256": evidence["procedure_sha256"], "evidence_sha256": digest(evidence_raw)}


def verify_retained_evidence(context, *, deadline):
    """Re-read the finite actual retained leaves, then apply the data-only callback.

    Shared reconciliation can call this instead of trusting the returned hashes.
    It starts no child, performs no native repair and does not refresh source.
    """
    output = Path(context["output"])
    raw = read(output / (context["selector"] + "-evidence.json"), MAX_JSON, deadline=deadline)
    evidence = decode(raw)
    for leaf, row in evidence["retained"].items():
        need(leaf in {"root.stdout", "root.stderr", "fixture-stdout.log", "fixture-stderr.log", "owner-death-result.json", "QUARANTINED"}, "retained-leaf-name")
        data = read(output / leaf, MAX_LOG, deadline=deadline)
        need({"sha256": digest(data), "bytes": len(data)} == row, "actual-retained-leaf")
    capture_raw = read(context["capture_path"], MAX_JSON, deadline=deadline)
    return validate_evidence(context, raw, capture_raw=capture_raw)


# Caller verifies the complete current source before importing this fixed module.
# Bind the exact parser bytes used by the data-only callback to that capture.
_PARSER_RAW = (HERE.parent / "parsers/check_owner_death_record.py").read_bytes()
_PARSER_SHA = digest(_PARSER_RAW)
_PARSER = types.ModuleType("c1_owner_death_record_data")
exec(compile(_PARSER_RAW, PARSER_REL, "exec", dont_inherit=True), _PARSER.__dict__)


def execute(context, *, deadline):
    """Run exactly the selected real N13/N14 procedure and retain CASE-evidence.json.

    Already-root Linux only. Failure preserves partial sealed trees/quarantine and
    original child cleanup; no synthetic record, skip, accepted claim or success path.
    """
    prepared = prepare_inputs(context, deadline=deadline)
    binding, capture, build, cap_raw, _ = _context(context, deadline)
    need(_PARSER_SHA == capture["files"][PARSER_REL]["sha256"], "current-parser-binding")
    materialized = materialize(context, prepared, deadline=deadline)
    output = binding.absolute(context["output"])
    need(output.resolve(strict=True) == output, "output-alias")
    metadata = output.lstat()
    need(stat.S_ISDIR(metadata.st_mode) and metadata.st_uid == metadata.st_gid == 0
         and stat.S_IMODE(metadata.st_mode) == 0o700, "output-custody")
    need({p.name for p in output.iterdir()} == {"plan.json", "source.json", "build-receipt.json",
         "procedure-inventory.json", "execution.json"}, "output-initial-membership")
    need(read(output / "source.json", MAX_JSON, deadline=deadline) == cap_raw
         and digest(read(output / "build-receipt.json", MAX_JSON, deadline=deadline))
         == context["build_receipt_sha256"], "output-input-binding")
    control, stage = prepared["control"], materialized["stage"]
    selector, wrapper = context["selector"], WRAPPERS[context["selector"]]
    argv = ["/usr/bin/bash", "--noprofile", "--norc", str(control / wrapper), "--execute",
        "--source-capture", str(control / "source.json"), "--source-capture-sha256", context["capture_sha256"],
        "--request", str(materialized["request"]), "--request-sha256", materialized["request_sha256"],
        "--runtime-host", str(stage / "runtime/dotnet"), "--runtime-host-sha256", build["runtime"]["files"]["dotnet"]["sha256"],
        "--entry", str(stage / "tool/ForgeTrust.AppSurface.Cli.dll"), "--entry-sha256", build["tool"]["files"]["ForgeTrust.AppSurface.Cli.dll"]["sha256"],
        "--build-receipt", str(control / "build-receipt.json"), "--build-receipt-sha256", context["build_receipt_sha256"],
        "--output-root", "/run/appsurface-evidence-" + context["generation"] + "/output",
        "--parser", str(control / "check_owner_death_record.py"), "--parser-sha256", capture["files"][PARSER_REL]["sha256"],
        "--generation", context["generation"], "--source-revision", context["source"],
        "--binding-validator", str(control / "binding.py"), "--binding-validator-sha256", context["binding_validator_sha256"],
        "--binding-deadline-monotonic", repr(deadline)]
    try:
        with (output / "fixture-stdout.log").open("xb") as stdout, (output / "fixture-stderr.log").open("xb") as stderr:
            os.chmod(stdout.name, 0o600)
            os.chmod(stderr.name, 0o600)
            run_bounded(argv, stdout, stderr, deadline=deadline)
        expected = f"OWNER_DEATH_MEASURED:{selector}:QUARANTINED:NOT_NATIVE_ACCEPTANCE\n".encode()
        need(read(output / "fixture-stdout.log", 65536, deadline=deadline) == expected
             and read(output / "fixture-stderr.log", 65536, deadline=deadline) == b"", "wrapper-output")
        record_raw = read(materialized["private"] / "owner-death-result.json", 65536, deadline=deadline)
        record = decode(record_raw)
        _PARSER.validate(record)
        root_stdout = read(materialized["private"] / "root.stdout", 4096, deadline=deadline)
        root_stderr = read(materialized["private"] / "root.stderr", 131072, deadline=deadline)
        need(root_stdout == (record["phase_line"] + "\n").encode()
             and digest(root_stdout) == record["pipes"]["stdout_sha256"]
             and digest(root_stderr) == record["pipes"]["stderr_sha256"]
             and len(root_stderr) == record["pipes"]["stderr_bytes"], "actual-pipe-binding")
        for name, raw in (("root.stdout", root_stdout), ("root.stderr", root_stderr),
                          ("owner-death-result.json", record_raw),
                          ("QUARANTINED", read(materialized["private"] / "QUARANTINED", 256, deadline=deadline))):
            save(output / name, raw)
        retained = {}
        for name in ("root.stdout", "root.stderr", "fixture-stdout.log", "fixture-stderr.log", "owner-death-result.json", "QUARANTINED"):
            raw = read(output / name, MAX_LOG, deadline=deadline)
            retained[name] = {"sha256": digest(raw), "bytes": len(raw)}
        evidence = {"schema": "issue779-checkpoint1-owner-death-evidence-v1", "source": context["source"],
            "selector": selector, "generation": context["generation"], "source_capture_sha256": context["capture_sha256"],
            "build_receipt_sha256": context["build_receipt_sha256"], "procedure_sha256": capture["files"][PREFIX + wrapper]["sha256"],
            "common_sha256": capture["files"][PREFIX + COMMON]["sha256"], "parser_sha256": capture["files"][PARSER_REL]["sha256"],
            "record": record, "retained": retained}
        evidence_raw = canonical(evidence)
        row = validate_evidence(context, evidence_raw, capture_raw=cap_raw)
        save(output / (selector + "-evidence.json"), evidence_raw)
        return row
    finally:
        # Failure diagnostics stay private and confer no pass. Preserve the original
        # exception even if an optional leaf is absent or its bounded read fails.
        for name, cap in (("root.stdout", 4096), ("root.stderr", 131072),
                          ("owner-death-result.json", 65536), ("QUARANTINED", 256)):
            try:
                destination = output / name
                if not os.path.lexists(destination):
                    save(destination, read(materialized["private"] / name, cap, deadline=deadline))
            except (OSError, ValueError):
                pass
        # Never delete, reuse or repair generation-owned accounts/workspaces.
