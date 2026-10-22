"""Current-source C1 prerequisite data; never native ownership or acceptance.

The publisher records real command terminals and complete inventories. Shape
validation is deliberately separate from FD/Git verification, so detached tests
cannot accidentally exercise a privileged factory. No historical build schema,
command count, TRX fact, review Boolean or image artifact is translated here.
"""

from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path
import re
import stat
import subprocess
import time


SELECTORS = ("None", *(f"N{i:02d}" for i in range(4, 17)))
CASES = tuple(f"N{i:02d}" for i in range(1, 17))
CASE_SETS = {"None": CASES[:3], **{s: (s,) for s in SELECTORS[1:]}}
FIXED_ENVIRONMENT = (
    "PATH=/usr/bin:/bin", "HOME=/nonexistent", "LANG=C.UTF-8",
    "DOTNET_CLI_TELEMETRY_OPTOUT=1", "DOTNET_CLI_HOME=/tmp", "XDG_RUNTIME_DIR=/tmp",
)
MAX_JSON = 8 * 1024 * 1024
MAX_LOG = 8 * 1024 * 1024
MAX_FILE = 2 * 1024 * 1024 * 1024
MAX_TREE = 4 * 1024 * 1024 * 1024
MAX_NODES = 65536
HEX40 = re.compile(r"[0-9a-f]{40}\Z")
HEX64 = re.compile(r"[0-9a-f]{64}\Z")
CLI_PROJECT = "Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj"
HELPER_PROJECTS = {
    "peer": (".github/issue779-checkpoint1/procedures/helpers/peer/NativePeerBroker.csproj", "NativePeerBroker.dll"),
    "n04": (".github/issue779-checkpoint1/procedures/helpers/n04/NativeRootCoordinator.csproj", "NativeRootCoordinator.dll"),
    "n07": (".github/issue779-checkpoint1/procedures/helpers/n07/NativeRootCoordinator.csproj", "NativeRootCoordinator.dll"),
}
MEASUREMENT_MODULES = ("ForgeTrust.AppSurface.Cli", "ForgeTrust.AppSurface.Evidence.Contracts",
    "ForgeTrust.AppSurface.Evidence.Planner", "ForgeTrust.AppSurface.Evidence.Coverage",
    "ForgeTrust.AppSurface.Evidence.Aspire", "ForgeTrust.AppSurface.Evidence.Supervision")
MEASUREMENT_NAMES = frozenset(name + suffix for name in MEASUREMENT_MODULES for suffix in (".dll", ".pdb"))
# This supplemental collector observes root supervision only. Full six-module
# reports remain required because the completed consumer predecessor is merged.
MEASUREMENT_INSTRUMENTED_NAMES = frozenset(
    "ForgeTrust.AppSurface.Evidence.Supervision" + suffix for suffix in (".dll", ".pdb"))
MEASUREMENT_FILTERS = (
    "[ForgeTrust.AppSurface.Evidence.Supervision]ForgeTrust.AppSurface.Evidence.Supervision.Linux*",
    "[ForgeTrust.AppSurface.Evidence.Supervision]ForgeTrust.AppSurface.Evidence.Supervision.Supervision*",
)
MEASUREMENT_COLLECTOR_SUFFIX = "/.store/coverlet.console/10.0.1/coverlet.console/10.0.1/tools/net10.0/any/coverlet.console.dll"


def required_helpers(selector):
    """Fixed actual helper images required by each historical native procedure."""
    require(selector in SELECTORS, "helper-selector")
    rows = {"peer"} if selector in ("None", "N04", "N05", "N06", "N07", "N08", "N11", "N12") else set()
    if selector == "N04": rows.add("n04")
    if selector == "N07": rows.add("n07")
    return rows



class Rejected(ValueError):
    """Closed data/verification category, without supplied path or raw text."""


def require(condition, category):
    if not condition:
        raise Rejected(category)


def exact(value, fields, category):
    require(type(value) is dict and set(value) == set(fields), category)


def integer(value, low, high):
    return type(value) is int and low <= value <= high


def digest(raw):
    return hashlib.sha256(raw).hexdigest()


def canonical(value):
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":"),
                      allow_nan=False).encode("utf-8")


def _pairs(rows):
    result = {}
    for key, value in rows:
        require(key not in result, "duplicate-json-member")
        result[key] = value
    return result


def decode(raw):
    require(type(raw) is bytes and 0 < len(raw) <= MAX_JSON, "json-bytes")
    def invalid(_):
        raise Rejected("json-number")
    try:
        value = json.loads(raw.decode("utf-8", errors="strict"), object_pairs_hook=_pairs,
                           parse_constant=invalid)
    except (UnicodeError, json.JSONDecodeError, RecursionError):
        raise Rejected("json-encoding") from None
    def depth(item, level):
        require(level <= 24, "json-depth")
        if type(item) is dict:
            for child in item.values():
                depth(child, level + 1)
        elif type(item) is list:
            for child in item:
                depth(child, level + 1)
    depth(value, 0)
    return value


def relative(value):
    require(type(value) is str and 0 < len(value.encode("utf-8")) <= 4095
            and not value.startswith("/") and not any(ord(c) < 32 or ord(c) == 127 for c in value)
            and "\\" not in value and ":" not in value
            and all(p not in ("", ".", "..", ".git") for p in value.split("/")), "relative-path")
    return value


def absolute(value):
    require(type(value) is str and value.startswith("/") and len(value.encode("utf-8")) <= 4095
            and value != "/" and "\\" not in value
            and not any(ord(c) < 32 or ord(c) == 127 for c in value)
            and all(p not in ("", ".", "..") for p in value[1:].split("/")), "absolute-path")
    return Path(value)


def overlaps(first, second):
    return first == second or first in second.parents or second in first.parents


def _identity(s):
    return (s.st_dev, s.st_ino, s.st_mode, s.st_uid, s.st_gid, s.st_nlink,
            s.st_size, s.st_mtime_ns, s.st_ctime_ns)


def _remaining(deadline):
    require(time.monotonic() < deadline, "verification-deadline")


def read(path, maximum=MAX_JSON, *, deadline):
    """Retain a no-follow leaf FD, bound bytes and compare opened/final/named facts.

    Native root staging must separately walk protected parents; this read does
    not issue a trusted-parent or filesystem-custody capability.
    """
    _remaining(deadline)
    path = Path(path)
    before = path.lstat()
    require(stat.S_ISREG(before.st_mode) and before.st_nlink == 1
            and 0 <= before.st_size <= maximum, "read-node")
    fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK | os.O_CLOEXEC)
    try:
        require(_identity(before) == _identity(os.fstat(fd)), "read-open-substitution")
        body = bytearray()
        while True:
            _remaining(deadline)
            chunk = os.read(fd, min(65536, maximum + 1 - len(body)))
            if not chunk:
                break
            body.extend(chunk)
            require(len(body) <= before.st_size, "read-growth")
        require(len(body) == before.st_size and _identity(before) == _identity(os.fstat(fd))
                == _identity(path.lstat()), "read-final-substitution")
        _remaining(deadline)
        return bytes(body)
    finally:
        os.close(fd)


def file_digest(path, expected_bytes, *, deadline):
    """Hash a potentially large immutable input through one retained FD."""
    require(integer(expected_bytes, 0, MAX_FILE), "hash-expected-length")
    path = Path(path)
    before = path.lstat()
    require(stat.S_ISREG(before.st_mode) and before.st_nlink == 1
            and before.st_size == expected_bytes <= MAX_FILE, "hash-node")
    fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK | os.O_CLOEXEC)
    try:
        require(_identity(before) == _identity(os.fstat(fd)), "hash-open-substitution")
        sha = hashlib.sha256()
        git = hashlib.sha1(b"blob " + str(before.st_size).encode() + b"\0")
        total = 0
        while True:
            _remaining(deadline)
            chunk = os.read(fd, 65536)
            if not chunk:
                break
            total += len(chunk)
            require(total <= before.st_size, "hash-growth")
            sha.update(chunk)
            git.update(chunk)
        require(total == before.st_size and _identity(before) == _identity(os.fstat(fd))
                == _identity(path.lstat()), "hash-final-substitution")
        return sha.hexdigest(), git.hexdigest(), format(stat.S_IMODE(before.st_mode), "04o")
    finally:
        os.close(fd)


def validate_capture(value, expected_source):
    """Validate a detached complete capture; actual bytes/Git remain separate."""
    exact(value, ("schema", "source", "tree", "count", "files"), "capture-fields")
    require(value["schema"] == "issue779-checkpoint2-source-v1"
            and type(expected_source) is str and HEX40.fullmatch(expected_source)
            and value["source"] == expected_source
            and type(value["tree"]) is str and HEX40.fullmatch(value["tree"]), "capture-identity")
    files = value["files"]
    require(type(files) is dict and integer(value["count"], 1, MAX_NODES)
            and len(files) == value["count"], "capture-count")
    for name, row in files.items():
        relative(name)
        exact(row, ("sha256", "mode", "git_blob"), "capture-file-fields")
        require(type(row["sha256"]) is str and HEX64.fullmatch(row["sha256"])
                and row["mode"] in ("0644", "0755")
                and type(row["git_blob"]) is str and HEX40.fullmatch(row["git_blob"]), "capture-file")
    return value


def validate_image(value, expected_entry):
    exact(value, ("root", "entry", "files", "directories"), "image-fields")
    absolute(value["root"])
    require(value["entry"] == expected_entry, "image-entry")
    files, directories = value["files"], value["directories"]
    require(type(files) is dict and type(directories) is dict and expected_entry in files
            and 1 <= len(files) + len(directories) <= MAX_NODES, "image-nodes")
    require(not set(files) & set(directories), "image-node-overlap")
    total = 0
    for name, row in files.items():
        relative(name)
        exact(row, ("sha256", "bytes", "mode"), "image-file-fields")
        require(type(row["sha256"]) is str and HEX64.fullmatch(row["sha256"])
                and integer(row["bytes"], 0, MAX_FILE)
                and row["mode"] in ("0644", "0755", "0444", "0555"), "image-file")
        total += row["bytes"]
    require(total <= MAX_TREE, "image-bytes")
    for name, mode in directories.items():
        relative(name)
        require(mode in ("0700", "0750", "0755", "0555"), "image-directory-mode")
    for name in (*files, *directories):
        parts = name.split("/")
        require(all("/".join(parts[:i]) in directories for i in range(1, len(parts))),
                "image-missing-ancestor")
    return value


def validate_build(value, capture_raw, expected_source, selector):
    """Strict current receipt, with exactly the two genuine publication phases.

    A passing detached model proves grammar only. verify_build additionally
    checks raw logs, full source/Git and every actual image file.
    """
    if type(value) is dict and value.get("schema") == "issue779-checkpoint1-build-v2":
        return validate_measured_build(value, capture_raw, expected_source, selector)
    capture = validate_capture(decode(capture_raw), expected_source)
    exact(value, ("schema", "source", "selector", "configuration", "sdk_version", "source_root",
                  "source_capture_sha256", "source_after_capture_sha256", "commands", "tool", "runtime", "helpers",
                  "native_execution"), "build-fields")
    require(selector in SELECTORS and value["selector"] == selector
            and value["schema"] == "issue779-checkpoint1-build-v1"
            and value["source"] == expected_source and value["configuration"] == "Release"
            and value["sdk_version"] == "10.0.401" and value["native_execution"] is False, "build-identity")
    require(value["source_capture_sha256"] == digest(capture_raw)
            == value["source_after_capture_sha256"], "build-capture-binding")
    source = absolute(value["source_root"])
    validate_image(value["tool"], "ForgeTrust.AppSurface.Cli.dll")
    validate_image(value["runtime"], "dotnet")
    tool, runtime = absolute(value["tool"]["root"]), absolute(value["runtime"]["root"])
    require(not overlaps(source, tool) and not overlaps(source, runtime) and not overlaps(tool, runtime),
            "build-root-overlap")
    rows = value["commands"]
    require(type(rows) is list and len(rows) == 2, "build-command-count")
    logs = set()
    for row, phase, verb in zip(rows, ("locked-restore", "publish"), ("restore", "publish")):
        exact(row, ("phase", "argv", "exit", "timed_out", "joined", "log_path", "log_sha256", "log_bytes"),
              "build-command-fields")
        require(row["phase"] == phase and type(row["exit"]) is int and row["exit"] == 0
                and row["timed_out"] is False and row["joined"] is True, "build-command-terminal")
        argv = row["argv"]
        require(type(argv) is list and 3 <= len(argv) <= 64 and all(type(a) is str and a
                and len(a.encode("utf-8")) <= 8192 and not any(ord(c) < 32 or ord(c) == 127 for c in a)
                for a in argv), "build-command-argv")
        require(Path(argv[0]).name == "dotnet" and argv[1] == verb
                and (argv[2] == CLI_PROJECT or argv[2] == str(source / CLI_PROJECT))
                and "-warnaserror" in argv
                and [a for a in argv if "EvidencePrivateQualification" in a]
                == ["-p:EvidencePrivateQualification=" + selector], "build-command-selection")
        # Close the command grammar rather than permitting --no-build, a second
        # output/configuration, DefineConstants or a different MSBuild target.
        # The publisher executes these actual commands, not receipt-defined
        # arbitrary executable text.
        flags = {"-warnaserror", "-p:UseSharedCompilation=false",
                 "-p:EvidencePrivateQualification=" + selector, "--nologo"}
        flags |= {"--locked-mode", "--disable-parallel"} if verb == "restore" else {"--no-restore"}
        paired = {"-c": "Release", "--configuration": "Release", "-o": str(tool),
                  "--output": str(tool), "--self-contained": "false"} if verb == "publish" else {}
        seen_options = set()
        index = 3
        while index < len(argv):
            option = argv[index]
            canonical_option = {"--configuration": "-c", "--output": "-o"}.get(option, option)
            require(canonical_option not in seen_options, "build-command-duplicate-option")
            seen_options.add(canonical_option)
            if option in paired:
                require(index + 1 < len(argv) and argv[index + 1] == paired[option], "build-command-option-value")
                index += 2
            else:
                require(option in flags, "build-command-unknown-option")
                index += 1
        require("--locked-mode" in argv if verb == "restore" else "--no-restore" in argv,
                "build-command-mode")
        if verb == "publish":
            options = list(zip(argv, argv[1:]))
            require(sum(a in ("-c", "--configuration") for a in argv) == 1
                    and any(a in ("-c", "--configuration") and b == "Release" for a, b in options),
                    "build-command-release")
            outputs = [b for a, b in options if a in ("-o", "--output")]
            require(outputs == [str(tool)], "build-command-output")
        log = str(absolute(row["log_path"]))
        require(log not in logs and type(row["log_sha256"]) is str
                and HEX64.fullmatch(row["log_sha256"]) and integer(row["log_bytes"], 0, MAX_LOG),
                "build-command-log")
        logs.add(log)
    helpers = value["helpers"]
    require(type(helpers) is dict and set(helpers) == required_helpers(selector), "helper-set")
    roots = [source, tool, runtime]
    for helper_id, helper in helpers.items():
        exact(helper, ("image", "commands"), "helper-fields")
        project, entry = HELPER_PROJECTS[helper_id]
        validate_image(helper["image"], entry)
        root = absolute(helper["image"]["root"])
        require(all(not overlaps(root, other) for other in roots), "helper-root-overlap")
        roots.append(root)
        validate_helper_commands(helper_id, helper["commands"], source, root, logs)
    return capture


def validate_measured_build(value, capture_raw, expected_source, selector):
    """Validate a private instrumented image while retaining the original build inventory.

    The v1 publication commands remain literal and unchanged. Only the selected root-supervision
    DLL and its PDB may differ in the actual staged image; all other files,
    directory modes, runtime and helper inputs retain their original bindings.
    This detached check grants no execution or native authority. ``verify_build``
    additionally reads the original v1 receipt and pinned collector before staging.
    """
    exact(value, ("schema", "source", "selector", "configuration", "sdk_version", "source_root",
        "source_capture_sha256", "source_after_capture_sha256", "commands", "tool", "runtime", "helpers",
        "native_execution", "measurement"), "measurement-build-fields")
    measurement = value["measurement"]
    exact(measurement, ("schema", "original_build_path", "original_build_sha256", "original_tool",
        "instrumented_modules", "collector_path", "collector_sha256"), "measurement-fields")
    require(measurement["schema"] == "issue779-c1-measurement-build-v1", "measurement-schema")
    original_path = absolute(measurement["original_build_path"])
    collector = absolute(measurement["collector_path"])
    require(str(collector).endswith(MEASUREMENT_COLLECTOR_SUFFIX), "measurement-collector-path")
    for key in ("original_build_sha256", "collector_sha256"):
        require(type(measurement[key]) is str and HEX64.fullmatch(measurement[key]), "measurement-hash")
    original = validate_image(measurement["original_tool"], "ForgeTrust.AppSurface.Cli.dll")
    # Reuse the complete v1 command/source/image grammar, without adopting a
    # caller-supplied alternate command or qualification selection.
    projected = {key: child for key, child in value.items() if key != "measurement"}
    projected["schema"] = "issue779-checkpoint1-build-v1"
    capture = validate_build(projected, capture_raw, expected_source, selector)
    tool = value["tool"]
    require(original_path == absolute(value["commands"][0]["log_path"]).parent / "build.json",
        "measurement-original-path")
    require(original["root"] == tool["root"] and original["entry"] == tool["entry"]
        and original["directories"] == tool["directories"]
        and set(original["files"]) == set(tool["files"]), "measurement-image-shape")
    maps = measurement["instrumented_modules"]
    require(type(maps) is dict and set(maps) == MEASUREMENT_INSTRUMENTED_NAMES
        and MEASUREMENT_NAMES <= set(tool["files"]), "measurement-module-set")
    for name, row in tool["files"].items():
        previous = original["files"][name]
        if name in MEASUREMENT_INSTRUMENTED_NAMES:
            require(type(maps[name]) is str and HEX64.fullmatch(maps[name])
                and maps[name] == row["sha256"] and row["mode"] == previous["mode"],
                "measurement-module-binding")
            if name.endswith(".dll"):
                require(row["sha256"] != previous["sha256"], "measurement-module-unchanged")
        else:
            require(row == previous, "measurement-unselected-file")
    return capture


def validate_helper_commands(helper_id, rows, source, output, logs):
    """Close two genuine helper phases; helper builds have no qualification symbols."""
    require(type(rows) is list and len(rows) == 2, "helper-command-count")
    project, _ = HELPER_PROJECTS[helper_id]
    for row, phase, verb in zip(rows, ("locked-restore", "publish"), ("restore", "publish")):
        exact(row, ("phase", "argv", "exit", "timed_out", "joined", "log_path", "log_sha256", "log_bytes"), "helper-command-fields")
        require(row["phase"] == "helper-" + helper_id + "-" + phase and type(row["exit"]) is int
                and row["exit"] == 0 and row["timed_out"] is False and row["joined"] is True,
                "helper-command-terminal")
        argv = row["argv"]
        require(type(argv) is list and 3 <= len(argv) <= 64
                and all(type(a) is str and 0 < len(a.encode()) <= 8192
                        and not any(ord(c) < 32 or ord(c) == 127 for c in a) for a in argv), "helper-command-argv")
        require(Path(argv[0]).is_absolute() and Path(argv[0]).name == "dotnet"
                and argv[1] == verb and argv[2] in (project, str(source / project)), "helper-command-selection")
        flags = {"-warnaserror", "-p:UseSharedCompilation=false", "--nologo"}
        flags |= {"--locked-mode", "--disable-parallel"} if verb == "restore" else {"--no-restore"}
        # Restore forwards MSBuild properties; dotnet restore has no -c option.
        pairs = {}
        if verb == "restore":
            flags.add("-p:Configuration=Release")
        else:
            pairs.update({"-c": "Release", "--configuration": "Release",
                          "-o": str(output), "--output": str(output), "--self-contained": "false"})
        seen = set()
        index = 3
        while index < len(argv):
            option = argv[index]
            canonical_option = {"--configuration": "-c", "--output": "-o"}.get(option, option)
            require(canonical_option not in seen, "helper-command-duplicate-option")
            seen.add(canonical_option)
            if option in pairs:
                require(index + 1 < len(argv) and argv[index + 1] == pairs[option], "helper-command-option-value")
                index += 2
            else:
                require(option in flags, "helper-command-unknown-option")
                index += 1
        require("-warnaserror" in seen and ("--locked-mode" in seen if verb == "restore" else "--no-restore" in seen), "helper-command-mode")
        require(("-p:Configuration=Release" if verb == "restore" else "-c") in seen, "helper-command-release")
        if verb == "publish": require("-o" in seen, "helper-command-output")
        log = str(absolute(row["log_path"]))
        require(log not in logs and type(row["log_sha256"]) is str and HEX64.fullmatch(row["log_sha256"])
                and integer(row["log_bytes"], 0, MAX_LOG), "helper-command-log")
        logs.add(log)


def verify_image(value, *, deadline):
    """Inspect the complete actual inventory, including directories, without following links."""
    root = absolute(value["root"])
    require(root.resolve(strict=True) == root, "image-root-alias")
    before = root.lstat()
    require(stat.S_ISDIR(before.st_mode) and not root.is_symlink(), "image-root")
    seen_files, seen_dirs = set(), set()
    pending = [root]
    directories_before = [(root, _identity(before))]
    while pending:
        directory = pending.pop()
        _remaining(deadline)
        with os.scandir(directory) as entries:
            for entry in entries:
                _remaining(deadline)
                p = Path(entry.path)
                name = p.relative_to(root).as_posix()
                s = entry.stat(follow_symlinks=False)
                require(not entry.is_symlink(), "image-link")
                if stat.S_ISDIR(s.st_mode):
                    require(name in value["directories"]
                            and format(stat.S_IMODE(s.st_mode), "04o") == value["directories"][name],
                            "image-directory-inventory")
                    seen_dirs.add(name)
                    directories_before.append((p, _identity(s)))
                    pending.append(p)
                else:
                    require(stat.S_ISREG(s.st_mode) and name in value["files"], "image-file-inventory")
                    row = value["files"][name]
                    sha, _, mode = file_digest(p, row["bytes"], deadline=deadline)
                    require(sha == row["sha256"] and mode == row["mode"], "image-file-binding")
                    seen_files.add(name)
                require(len(seen_files) + len(seen_dirs) <= MAX_NODES, "image-node-bound")
    require(seen_files == set(value["files"]) and seen_dirs == set(value["directories"]), "image-inventory")
    require(_identity(before) == _identity(root.lstat()), "image-root-substitution")
    for path, identity in directories_before:
        _remaining(deadline)
        require(_identity(path.lstat()) == identity, "image-directory-substitution")


def inventory(root, entry, *, deadline):
    """Measure a complete actual publish/runtime file map for the publisher.

    ``root`` is a canonical existing absolute Path; ``entry`` is the fixed
    relative managed CLI or dotnet entry. No symlink, hardlink, special node,
    unknown mode or omitted directory is accepted. The root itself is not a
    directory row. This returns measured data and grants no native authority.
    """
    root = absolute(str(root))
    relative(entry)
    require(root.resolve(strict=True) == root, "inventory-root-alias")
    first = root.lstat()
    require(stat.S_ISDIR(first.st_mode), "inventory-root")
    value = {"root": str(root), "entry": entry, "files": {}, "directories": {}}
    snapshots = [(root, _identity(first))]
    pending = [root]
    total = 0
    while pending:
        directory = pending.pop()
        _remaining(deadline)
        with os.scandir(directory) as entries:
            for node in entries:
                _remaining(deadline)
                p = Path(node.path)
                name = relative(p.relative_to(root).as_posix())
                fact = node.stat(follow_symlinks=False)
                require(not node.is_symlink(), "inventory-link")
                if stat.S_ISDIR(fact.st_mode):
                    value["directories"][name] = format(stat.S_IMODE(fact.st_mode), "04o")
                    snapshots.append((p, _identity(fact)))
                    pending.append(p)
                else:
                    require(stat.S_ISREG(fact.st_mode) and fact.st_nlink == 1, "inventory-file")
                    sha, _, mode = file_digest(p, fact.st_size, deadline=deadline)
                    value["files"][name] = {"sha256": sha, "bytes": fact.st_size, "mode": mode}
                    total += fact.st_size
                    require(total <= MAX_TREE, "inventory-tree-bytes")
                require(len(value["files"]) + len(value["directories"]) <= MAX_NODES, "inventory-nodes")
    for path, identity in snapshots:
        _remaining(deadline)
        require(_identity(path.lstat()) == identity, "inventory-directory-substitution")
    validate_image(value, entry)
    return value


def prepare_publish_image(root, *, deadline):
    """Normalize a fresh, exclusively owned SDK publish tree before inventory.

    The canonical root and its immediate 0700 parent must belong to the current
    effective UID/GID. Every descendant must have that same ownership. Only
    regular single-link files in 0600/0640/0644/0700/0744/0750/0755/0444/0555
    and directories in 0700/0750/0755/0555 are accepted. No bytes are read or
    hashed here; ``inventory`` must subsequently measure the complete image.

    A bounded whole-tree preflight performs no chmod. A second pass reopens
    captured nodes through no-follow directory FDs, rechecks their identities,
    and changes only the opened FD: executable files become 0755, other files
    0644, child directories 0755 and the root 0700. FD use is constant rather
    than proportional to the image size. No path-based chmod is used.

    Returns only ``{"files": count, "directories": count}``; directories
    exclude the root, as in inventory. This is packaging, never custody or
    native authority. A race, expired deadline or I/O failure rejects with a
    fixed publication-mode category. A late failure may leave earlier owned
    nodes normalized; callers must discard the failed publication, not retry
    it as if an atomic conversion had completed. The caller must have already
    joined the publisher and exclude concurrent writers throughout this call.
    """
    parent_fd = root_fd = None

    def need(condition, suffix):
        require(condition, "publication-mode-" + suffix)

    def check():
        need(type(deadline) in (int, float)
             and time.monotonic() < deadline < float("inf"), "deadline")

    def owned(fact):
        return fact.st_uid == uid and fact.st_gid == gid

    def named(fd, name):
        return os.stat(name, dir_fd=fd, follow_symlinks=False)

    def anchor():
        check()
        need(_identity(os.fstat(parent_fd)) == parent_identity
             == _identity(parent.lstat()), "parent-changed")
        need(_identity(os.fstat(root_fd)) == directories[()]
             == _identity(named(parent_fd, root.name)), "root-changed")

    def directory(parts):
        # Each openat resolves exactly one component. Closing the previous FD
        # keeps deep trees from consuming one descriptor per path component.
        anchor()
        fd = os.dup(root_fd)
        try:
            for length, name in enumerate(parts, 1):
                check()
                expected = directories[parts[:length]]
                need(_identity(named(fd, name)) == expected, "directory-changed")
                child = os.open(name, directory_flags, dir_fd=fd)
                try:
                    need(_identity(os.fstat(child)) == expected
                         == _identity(named(fd, name)), "directory-open")
                except BaseException:
                    os.close(child)
                    raise
                previous, fd = fd, child
                os.close(previous)
            need(_identity(os.fstat(fd)) == directories[parts], "directory-changed")
            return fd
        except BaseException:
            os.close(fd)
            raise

    def leaf(fd, name, expected):
        check()
        need(_identity(named(fd, name)) == expected, "file-changed")
        opened = os.open(name, file_flags, dir_fd=fd)
        try:
            need(_identity(os.fstat(opened)) == expected
                 == _identity(named(fd, name)), "file-open")
            return opened
        except BaseException:
            os.close(opened)
            raise

    def normalize(fd, expected, mode):
        check()
        need(_identity(os.fstat(fd)) == expected, "before-chmod")
        changed = stat.S_IMODE(expected[2]) != mode
        if changed:
            os.fchmod(fd, mode)
        after = os.fstat(fd)
        # fchmod may change mode and ctime only. Retain its actual new ctime
        # for every subsequent FD/named recheck, rather than ignoring it.
        wanted = (expected[:2] + (stat.S_IFMT(expected[2]) | mode,) + expected[3:8] + (after.st_ctime_ns,)
                  if changed else expected)
        need(_identity(after) == wanted, "after-chmod")
        check()
        return _identity(after)

    try:
        check()
        uid, gid = os.geteuid(), os.getegid()
        root = absolute(str(root))
        parent = root.parent
        need(root.resolve(strict=True) == root, "canonical-root")
        parent_before = parent.lstat()
        need(stat.S_ISDIR(parent_before.st_mode) and owned(parent_before)
             and stat.S_IMODE(parent_before.st_mode) == 0o700, "parent")
        directory_flags = os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC
        file_flags = os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK | os.O_CLOEXEC
        parent_fd = os.open(parent, directory_flags)
        parent_identity = _identity(parent_before)
        need(_identity(os.fstat(parent_fd)) == parent_identity
             == _identity(parent.lstat()), "parent-open")
        before = named(parent_fd, root.name)
        directory_modes = (0o700, 0o750, 0o755, 0o555)
        file_modes = (0o600, 0o640, 0o644, 0o700, 0o744, 0o750, 0o755, 0o444, 0o555)
        need(stat.S_ISDIR(before.st_mode) and owned(before)
             and stat.S_IMODE(before.st_mode) in directory_modes, "root")
        root_fd = os.open(root.name, directory_flags, dir_fd=parent_fd)
        directories, files = {(): _identity(before)}, {}
        anchor()
        pending, total = [()], 0
        while pending:
            parts = pending.pop()
            fd = directory(parts)
            try:
                with os.scandir(fd) as entries:
                    for entry in entries:
                        check()
                        path = parts + (entry.name,)
                        relative("/".join(path))
                        fact = named(fd, entry.name)
                        need(owned(fact), "owner")
                        mode = stat.S_IMODE(fact.st_mode)
                        if stat.S_ISDIR(fact.st_mode):
                            need(mode in directory_modes, "directory-mode")
                            directories[path] = _identity(fact)
                            pending.append(path)
                        else:
                            need(stat.S_ISREG(fact.st_mode) and fact.st_nlink == 1,
                                 "file-kind-or-links")
                            need(mode in file_modes and 0 <= fact.st_size <= MAX_FILE, "file-mode-or-size")
                            files[path] = _identity(fact)
                            total += fact.st_size
                            need(total <= MAX_TREE, "tree-bytes")
                            opened = leaf(fd, entry.name, files[path])
                            try:
                                need(_identity(os.fstat(opened)) == files[path]
                                     == _identity(named(fd, entry.name)), "preflight-file")
                            finally:
                                os.close(opened)
                        need(len(files) + len(directories) - 1 <= MAX_NODES, "nodes")
                need(_identity(os.fstat(fd)) == directories[parts], "preflight-directory")
            finally:
                os.close(fd)
        # No fchmod has occurred above this boundary. Every mutation below is
        # limited to the captured object reopened and checked through root_fd.
        anchor()
        for path, expected in files.items():
            fd = directory(path[:-1])
            try:
                opened = leaf(fd, path[-1], expected)
                try:
                    mode = 0o755 if stat.S_IMODE(expected[2]) & 0o111 else 0o644
                    files[path] = normalize(opened, expected, mode)
                    need(_identity(named(fd, path[-1])) == files[path], "file-after-chmod")
                finally:
                    os.close(opened)
                need(_identity(os.fstat(fd)) == directories[path[:-1]], "file-parent-changed")
            finally:
                os.close(fd)
        # Descendants first: changing a directory's own mode/ctime must not
        # invalidate the still-captured parent used to reopen it.
        for parts in sorted(directories, key=len, reverse=True):
            fd = directory(parts)
            try:
                directories[parts] = normalize(fd, directories[parts], 0o755 if parts else 0o700)
                if parts:
                    ancestor = directory(parts[:-1])
                    try:
                        need(_identity(named(ancestor, parts[-1])) == directories[parts]
                             == _identity(os.fstat(fd)), "directory-after-chmod")
                    finally:
                        os.close(ancestor)
                else:
                    anchor()
            finally:
                os.close(fd)
        anchor()
        # Reopen the whole captured set after mutation. Any added/removed node
        # changes its directory identity; no changed path is silently adopted.
        for parts in directories:
            fd = directory(parts)
            os.close(fd)
        for path, expected in files.items():
            fd = directory(path[:-1])
            try:
                opened = leaf(fd, path[-1], expected)
                os.close(opened)
            finally:
                os.close(fd)
        anchor()
        return {"files": len(files), "directories": len(directories) - 1}
    except Rejected as error:
        if str(error).startswith("publication-mode-"):
            raise
        raise Rejected("publication-mode-path") from None
    except (OSError, ValueError, TypeError, OverflowError, RuntimeError):
        raise Rejected("publication-mode-io") from None
    finally:
        close_failed = False
        for fd in (root_fd, parent_fd):
            if fd is not None:
                try:
                    os.close(fd)
                except OSError:
                    close_failed = True
        if close_failed:
            raise Rejected("publication-mode-close") from None


def verify_source(value, root, *, deadline):
    """Compare the entire current captured tree to immutable Git entries and real file bytes.

    These are read-only Git operations. No write-tree, restore, index refresh or
    build occurs. Runtime native root staging must still retain its own audited
    source/tool/runtime copies for the duration of a case.
    """
    require(type(value) is dict, "capture-fields")
    validate_capture(value, value.get("source"))
    root = absolute(str(root))
    require(root.resolve(strict=True) == root, "source-root-alias")
    def git(*args):
        _remaining(deadline)
        result = subprocess.run(["git", "--no-optional-locks", "-c", "core.fsmonitor=false",
                                 "-c", "safe.directory=" + str(root), *args],
                                cwd=root, stdin=subprocess.DEVNULL,
                                stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                                timeout=min(15, max(.001, deadline - time.monotonic())), check=False)
        require(result.returncode == 0 and len(result.stdout) <= MAX_JSON, "source-git")
        return result.stdout
    require(git("rev-parse", "HEAD").decode().strip() == value["source"]
            and git("rev-parse", "HEAD^{tree}").decode().strip() == value["tree"], "source-git-identity")
    require(not git("status", "--porcelain=v1", "--untracked-files=all").strip(), "source-not-clean")
    rows = git("ls-tree", "-rz", "--full-tree", value["source"])
    observed = set()
    for raw in rows.split(b"\0"):
        if not raw:
            continue
        header, raw_name = raw.split(b"\t", 1)
        mode, kind, blob = header.decode("ascii").split()
        name = raw_name.decode("utf-8", errors="strict")
        require(kind == "blob" and mode in ("100644", "100755") and name in value["files"],
                "source-git-entry")
        expected = value["files"][name]
        require(blob == expected["git_blob"] and expected["mode"] == ("0755" if mode == "100755" else "0644"),
                "source-git-mode")
        p = root / name
        require(all(not ancestor.is_symlink() for ancestor in (p, *p.parents) if ancestor != root.parent),
                "source-symlink")
        size = p.lstat().st_size
        sha, actual_blob, actual_mode = file_digest(p, size, deadline=deadline)
        require((sha, actual_blob, actual_mode) == (expected["sha256"], blob, expected["mode"]), "source-file")
        observed.add(name)
    require(observed == set(value["files"]), "source-complete-set")


def verify_build(value, capture_raw, expected_source, selector, *, deadline):
    capture = validate_build(value, capture_raw, expected_source, selector)
    if value["schema"] == "issue779-checkpoint1-build-v2":
        measurement = value["measurement"]
        original_raw = read(measurement["original_build_path"], deadline=deadline)
        require(digest(original_raw) == measurement["original_build_sha256"], "measurement-original-hash")
        original = decode(original_raw)
        require(type(original) is dict and original.get("schema") == "issue779-checkpoint1-build-v1",
            "measurement-original-schema")
        validate_build(original, capture_raw, expected_source, selector)
        require(original["tool"] == measurement["original_tool"], "measurement-original-tool")
        require(all(value[key] == child for key, child in original.items() if key not in ("schema", "tool")),
            "measurement-original-fields")
        collector_raw = read(measurement["collector_path"], 32 * 1024 * 1024, deadline=deadline)
        require(digest(collector_raw) == measurement["collector_sha256"], "measurement-collector-hash")
    all_commands = list(value["commands"])
    for helper in value["helpers"].values(): all_commands.extend(helper["commands"])
    for row in all_commands:
        raw = read(row["log_path"], MAX_LOG, deadline=deadline)
        require(len(raw) == row["log_bytes"] and digest(raw) == row["log_sha256"], "command-log-binding")
        require(re.search(rb"\b(?:warning|error) [A-Z]+[0-9]+:", raw) is None, "command-log-diagnostic")
    verify_source(capture, value["source_root"], deadline=deadline)
    verify_image(value["tool"], deadline=deadline)
    verify_image(value["runtime"], deadline=deadline)
    for helper in value["helpers"].values(): verify_image(helper["image"], deadline=deadline)
    return capture


def validate_result(value, capture_raw):
    """Grammar for an actual case receipt; detached values establish no native facts."""
    exact(value, ("schema", "source", "source_capture_sha256", "selector", "build_receipt_sha256",
                  "plan_sha256", "exit", "skipped", "cases", "native_acceptance"), "result-fields")
    capture = decode(capture_raw)
    validate_capture(capture, capture["source"])
    require(value["schema"] == "issue779-checkpoint1-result-v1"
            and value["source"] == capture["source"] and value["source_capture_sha256"] == digest(capture_raw)
            and value["selector"] in SELECTORS and value["native_acceptance"] is False,
            "result-identity")
    require(type(value["exit"]) is int and value["exit"] == 0
            and type(value["skipped"]) is int and value["skipped"] == 0, "result-terminal")
    for key in ("build_receipt_sha256", "plan_sha256"):
        require(type(value[key]) is str and HEX64.fullmatch(value[key]), "result-input-hash")
    rows = value["cases"]
    require(type(rows) is list and tuple(row.get("case") for row in rows if type(row) is dict)
            == CASE_SETS[value["selector"]] and len(rows) == len(CASE_SETS[value["selector"]]), "result-case-set")
    for row in rows:
        exact(row, ("case", "passed", "skipped", "procedure_sha256", "evidence_sha256"), "result-case-fields")
        require(row["passed"] is True and row["skipped"] is False
                and all(type(row[k]) is str and HEX64.fullmatch(row[k])
                        for k in ("procedure_sha256", "evidence_sha256")), "result-case-terminal")
    return value


def aggregate(results, capture_raw):
    """Require exactly fourteen source-bound selector receipts and all sixteen once.

    The command adapter additionally verifies each receipt's actual plan, build
    and evidence bytes. This function alone validates detached data, not native
    authorship, physical exit, or acceptance.
    """
    require(type(results) is list and len(results) == len(SELECTORS), "aggregate-selector-count")
    seen = set()
    cases = []
    for result in results:
        validate_result(result, capture_raw)
        selector = result["selector"]
        require(selector not in seen, "aggregate-duplicate-selector")
        seen.add(selector)
        cases.extend(row["case"] for row in result["cases"])
    require(seen == set(SELECTORS) and len(cases) == 16 and set(cases) == set(CASES), "aggregate-case-set")
    capture = decode(capture_raw)
    return {"schema": "issue779-checkpoint1-aggregate-v1", "source": capture["source"],
            "source_capture_sha256": digest(capture_raw), "selectors": list(SELECTORS),
            "cases": list(CASES), "passed": 16, "skipped": 0, "exit": 0,
            "native_acceptance": False, "coverage_credit": False}


def root_verify(args):
    """Recheck the current bundle at an already retained-root procedure boundary.

    The procedure still pins these input files, the validator and its protected
    parents before invoking this command. This does not replace those native
    pins, authorize a unit, or renew the caller's absolute monotonic deadline.
    """
    deadline = args.deadline_monotonic
    require(type(deadline) is float and deadline == deadline
            and 0 < deadline - time.monotonic() <= 900, "root-binding-deadline")
    capture_raw = read(args.source_capture, deadline=deadline)
    build_raw = read(args.build_receipt, deadline=deadline)
    value = decode(build_raw)
    verify_build(value, capture_raw, args.source, args.selector, deadline=deadline)
    return {"schema": "issue779-checkpoint1-root-binding-v1", "source": args.source,
            "selector": args.selector, "source_capture_sha256": digest(capture_raw),
            "build_receipt_sha256": digest(build_raw), "native_execution": False}


def main(argv=None):
    """Fixed verification-only CLI for retained-root procedures; no command injection API."""
    import argparse
    import sys
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)
    command = subparsers.add_parser("root-verify")
    command.add_argument("--source-capture", type=Path, required=True)
    command.add_argument("--build-receipt", type=Path, required=True)
    command.add_argument("--source", required=True)
    command.add_argument("--selector", choices=SELECTORS, required=True)
    command.add_argument("--deadline-monotonic", type=float, required=True)
    args = parser.parse_args(argv)
    try:
        print(json.dumps(root_verify(args), sort_keys=True))
        return 0
    except (Rejected, OSError, ValueError, subprocess.SubprocessError):
        print("C1_CURRENT_BINDING_REJECTED", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
