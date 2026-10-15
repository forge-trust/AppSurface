#!/usr/bin/python3
"""Materialize one sealed N13/N14/N15 request workspace; never launches a worker."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import stat
import sys
import time
from datetime import datetime, timezone

SOURCE = '71d004d6cf5a791ddbb8e73e3c538248985a8505'
PARENT = '957da1f23ff743778fa39e3e559511ccdb281345'
SOURCE_TREE = 'd15b1f3a7532947d525e4d74cdc7457f6b8923ea'
SOURCE_COUNT = 2872
CAPTURE_SHA = '8f5822ab0f5da555ef142a28321631f1960e61463fe14a83b33e83d521934046'
CAPTURE_MAP_SHA = "308346155ca912db28838729e5081791e2a7b6a50bcac726093c6f07d1d85abe"
CAPTURE_RAW_MAP_SHA = "3a09062a50b035b1534ca89cc77dd29353d4ce20769ae01b4ccffbbecbd140a3"
N15_BUILD_PROJECTION_SHA = "c2e846a09c6e7561eea1a1531d34a422ba0b7379a05be4e4fcb17bd18134f891"
OWNER_SOURCE = SOURCE
OWNER_PARENT = PARENT
OWNER_SOURCE_TREE = SOURCE_TREE
OWNER_CAPTURE_SHA = CAPTURE_SHA
OWNER_CAPTURE_MAP_SHA = CAPTURE_MAP_SHA
OWNER_CAPTURE_RAW_MAP_SHA = CAPTURE_RAW_MAP_SHA
OWNER_BUILD_PROJECTION_SHA = "c2e846a09c6e7561eea1a1531d34a422ba0b7379a05be4e4fcb17bd18134f891"
OWNER_CAPTURE_FIVE_FIELD_SHA = OWNER_BUILD_PROJECTION_SHA
BUILD_CONTRACTS = {
    "N13": {"source": OWNER_SOURCE, "parent": OWNER_PARENT, "tree": OWNER_SOURCE_TREE,
            "capture": OWNER_CAPTURE_SHA, "map": OWNER_CAPTURE_MAP_SHA,
            "raw_map": OWNER_CAPTURE_RAW_MAP_SHA, "capture_projection": OWNER_CAPTURE_FIVE_FIELD_SHA,
            "build_projection": OWNER_BUILD_PROJECTION_SHA,
            "projection_tree_key": "tree", "shape": "owner-death"},
    "N14": {"source": OWNER_SOURCE, "parent": OWNER_PARENT, "tree": OWNER_SOURCE_TREE,
            "capture": OWNER_CAPTURE_SHA, "map": OWNER_CAPTURE_MAP_SHA,
            "raw_map": OWNER_CAPTURE_RAW_MAP_SHA, "capture_projection": OWNER_CAPTURE_FIVE_FIELD_SHA,
            "build_projection": OWNER_BUILD_PROJECTION_SHA,
            "projection_tree_key": "tree", "shape": "owner-death"},
    "N15": {"source": SOURCE, "parent": PARENT, "tree": SOURCE_TREE,
            "capture": CAPTURE_SHA, "map": CAPTURE_MAP_SHA,
            "raw_map": CAPTURE_RAW_MAP_SHA, "capture_projection": N15_BUILD_PROJECTION_SHA,
            "build_projection": N15_BUILD_PROJECTION_SHA,
            "projection_tree_key": "tree", "shape": "linux-pipe"},
}
FIXTURE_ROOT = Path("/var/lib/appsurface-evidence-fixture")
PRIVATE_ROOT = Path("/run/appsurface-evidence-fixture")
INPUT_ROOT = Path("/var/lib")
FILE_CAP = 2 * 1024 * 1024 * 1024
TREE_CAP = 4 * 1024 * 1024 * 1024
NODE_CAP = 65536
CORE_FILE_CAP = 256 * 1024 * 1024
CORE_TREE_CAP = 1024 * 1024 * 1024
CORE_NODE_CAP = 8192
MANIFEST_CAP = 8 * 1024 * 1024
CONTROL_CAP = 4 * 1024 * 1024
FIXTURE_MAX_SECONDS = 600
FIXTURE_CLEANUP_RESERVE_SECONDS = 30
JOB_MAX_SECONDS = 240
JOB_CLEANUP_SECONDS = 60

class Reject(Exception):
    def __init__(self, code):
        super().__init__(code)
        self.code = code

def need(ok, code):
    if not ok:
        raise Reject(code)

def boottime_ms():
    clock = getattr(time, "CLOCK_BOOTTIME", None)
    need(clock is not None, "boottime-unavailable")
    return int(time.clock_gettime(clock) * 1000)

def check_deadline(deadline_ms):
    need(boottime_ms() < deadline_ms, "original-job-deadline")

def bounded_call(deadline_ms, argv, *, stdout=None):
    import subprocess
    check_deadline(deadline_ms)
    remain = max(1, (deadline_ms - boottime_ms()) / 1000)
    try:
        cp = subprocess.run(argv, stdin=subprocess.DEVNULL, stdout=stdout,
                            stderr=subprocess.DEVNULL, timeout=remain,
                            check=False, close_fds=True)
    except subprocess.TimeoutExpired:
        raise Reject("bounded-operation-timeout")
    need(cp.returncode == 0, "bounded-operation")
    check_deadline(deadline_ms)

def read_pinned(path, expected, cap, deadline_ms):
    p = Path(path)
    safe_path(p)
    st = p.lstat()
    need(stat.S_ISREG(st.st_mode) and st.st_nlink == 1 and st.st_size <= cap,
         "input-file-shape")
    need(st.st_uid == 0 and st.st_gid == 0, "input-file-owner")
    h = hashlib.sha256()
    chunks = []
    fd = os.open(p, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK | os.O_CLOEXEC)
    try:
        opened = os.fstat(fd)
        need(identity(st) == identity(opened), "input-file-substitution")
        total = 0
        while True:
            check_deadline(deadline_ms)
            block = os.read(fd, min(1024 * 1024, cap + 1 - total))
            if not block:
                break
            total += len(block)
            need(total <= cap, "input-file-growth")
            h.update(block)
            chunks.append(block)
        after = os.fstat(fd)
        need(identity(opened) == identity(after) and total == st.st_size,
             "input-file-changed")
    finally:
        os.close(fd)
    need(h.hexdigest() == expected, "input-file-digest")
    return st, b"".join(chunks)

def digest_file(path, expected, cap, deadline_ms):
    p = Path(path)
    safe_path(p)
    st = p.lstat()
    need(stat.S_ISREG(st.st_mode) and st.st_nlink == 1 and st.st_size <= cap,
         "input-file-shape")
    need(st.st_uid == 0 and st.st_gid == 0, "input-file-owner")
    h = hashlib.sha256()
    fd = os.open(p, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK | os.O_CLOEXEC)
    try:
        opened = os.fstat(fd)
        need(identity(st) == identity(opened), "input-file-substitution")
        total = 0
        while True:
            check_deadline(deadline_ms)
            block = os.read(fd, 1024 * 1024)
            if not block:
                break
            total += len(block)
            need(total <= cap, "input-file-growth")
            h.update(block)
        after = os.fstat(fd)
        need(identity(opened) == identity(after) and total == st.st_size,
             "input-file-changed")
    finally:
        os.close(fd)
    need(h.hexdigest() == expected, "input-file-digest")
    return st

def identity(st):
    return (st.st_dev, st.st_ino, st.st_mode, st.st_uid, st.st_gid,
            st.st_nlink, st.st_size, st.st_mtime_ns, st.st_ctime_ns)

def safe_path(path):
    s = str(path)
    need(path.is_absolute() and "//" not in s and not s.endswith("/") and
         not any(x in ("", ".", "..") for x in s.split("/")[1:]) and
         not any(ord(c) < 32 or ord(c) == 127 for c in s), "path-shape")
    cur = Path("/")
    for part in path.parts[1:]:
        cur /= part
        need(not cur.is_symlink(), "path-symlink")

def safe_ancestors(path):
    safe_path(path)
    cur = path
    while True:
        st = cur.lstat()
        need(stat.S_ISDIR(st.st_mode) and st.st_uid == 0 and st.st_gid == 0 and
             (stat.S_IMODE(st.st_mode) & 0o022) == 0, "unsafe-ancestor")
        if cur == Path("/"):
            break
        cur = cur.parent

def read_json(path, sha, cap, deadline_ms):
    _, raw = read_pinned(path, sha, cap, deadline_ms)
    try:
        return json.loads(raw, object_pairs_hook=unique_pairs)
    except (ValueError, UnicodeError):
        raise Reject("json-shape")

def unique_pairs(rows):
    out = {}
    for k, v in rows:
        need(k not in out, "duplicate-json-key")
        out[k] = v
    return out

def validate_tree(role, root, manifest_path, manifest_sha, nodes_path,
                  nodes_sha, receipt, deadline_ms):
    root = Path(root)
    safe_ancestors(root.parent)
    rst = root.lstat()
    expected_root_mode = 0o555
    need(stat.S_ISDIR(rst.st_mode) and rst.st_uid == 0 and rst.st_gid == 0 and
         stat.S_IMODE(rst.st_mode) == expected_root_mode, "input-root-custody")
    manifest_st = digest_file(manifest_path, manifest_sha, MANIFEST_CAP, deadline_ms)
    _, node_bytes = read_pinned(nodes_path, nodes_sha, CONTROL_CAP, deadline_ms)
    try:
        nodes = json.loads(node_bytes, object_pairs_hook=unique_pairs)
    except (ValueError, UnicodeError):
        raise Reject("node-json-shape")
    need(json.dumps(nodes, sort_keys=True, separators=(",", ":")).encode() + b"\n" == node_bytes,
         "node-json-not-canonical")
    need(set(nodes) == {"directories", "files", "root_name", "schema"} and
         nodes["schema"] == "issue779-build-node-inventory-v1" and
         nodes["root_name"] == role and isinstance(nodes["files"], dict) and
         isinstance(nodes["directories"], dict) and
         0 < len(nodes["files"]) + len(nodes["directories"]) <= NODE_CAP and
         "." in nodes["directories"], "node-inventory-shape")
    art = receipt["artifacts"][role]
    need(art["tsv_sha256"] == manifest_sha and art["nodes_sha256"] == nodes_sha and
         art["file_count"] == len(nodes["files"]) and
         art["node_count"] == len(nodes["files"]) + len(nodes["directories"]) and
         sum(v.get("bytes", -1) for v in nodes["files"].values()) == art["total_bytes"],
         "build-role-map-binding")
    for rel, rec in nodes["files"].items():
        allowed_modes = ("0644", "0755") if role == "source" else ("0444", "0555")
        need(valid_rel(rel) and set(rec) == {"bytes", "mode", "sha256"} and
             type(rec["bytes"]) is int and 0 <= rec["bytes"] <= FILE_CAP and
             rec["mode"] in allowed_modes and
             is_sha(rec["sha256"]), "node-file-shape")
    for rel, rec in nodes["directories"].items():
        need((rel == "." or valid_rel(rel)) and set(rec) == {"mode"} and
             rec["mode"] == ("0700" if role == "source" else "0555"), "node-directory-shape")
    manifest = {}
    previous = ""
    _, manifest_bytes = read_pinned(manifest_path, manifest_sha, MANIFEST_CAP, deadline_ms)
    for row in manifest_bytes.splitlines(keepends=True):
        check_deadline(deadline_ms)
        need(len(row) <= 8192 and row.endswith(b"\n"), "manifest-row")
        fields = row[:-1].decode("utf-8", "strict").split("\t")
        need(len(fields) == 3 and fields[0] in ("0644", "0755", "0444", "0555") and
             is_sha(fields[1]) and valid_rel(fields[2]), "manifest-shape")
        need(fields[2] not in manifest and (not previous or fields[2] > previous),
             "manifest-order-or-duplicate")
        previous = fields[2]
        manifest[fields[2]] = (fields[0], fields[1])
    need(len(nodes["files"]) + len(nodes["directories"]) <= NODE_CAP,
         "node-count-bound")
    if role != "source":
        need(len(nodes["files"]) + len(nodes["directories"]) <= CORE_NODE_CAP,
             "core-node-bound")
    need(set(manifest) == set(nodes["files"]), "manifest-membership")
    for rel, (mode, sha) in manifest.items():
        rec = nodes["files"][rel]
        need(mode == rec["mode"] and sha == rec["sha256"], "manifest-node-binding")
    expected_dirs = set(nodes["directories"])
    expected_files = set(nodes["files"])
    seen_dirs, seen_files = set(), set()
    total = 0
    for base, dirs, files in os.walk(root, topdown=True, followlinks=False):
        check_deadline(deadline_ms)
        bp = Path(base)
        relbase = bp.relative_to(root).as_posix()
        if relbase == ".": relbase = "."
        st = bp.lstat()
        need(stat.S_ISDIR(st.st_mode) and st.st_nlink >= 2 and
             st.st_uid == 0 and st.st_gid == 0, "input-directory-shape")
        rec = nodes["directories"].get(relbase)
        need(rec is not None and stat.S_IMODE(st.st_mode) == 0o555,
             "input-directory-mode")
        seen_dirs.add(relbase)
        for name in dirs:
            q = bp / name
            need(not q.is_symlink(), "input-symlink")
        for name in files:
            q = bp / name
            rel = q.relative_to(root).as_posix()
            stf = q.lstat()
            need(stat.S_ISREG(stf.st_mode) and stf.st_nlink == 1 and
                 stf.st_uid == 0 and stf.st_gid == 0, "input-regular-file")
            recf = nodes["files"].get(rel)
            need(recf is not None and stf.st_size == recf["bytes"] and
                 stat.S_IMODE(stf.st_mode) == int(recf["mode"], 8), "input-file-map")
            total += stf.st_size
            if role == "source":
                need(total <= TREE_CAP, "input-tree-byte-bound")
            else:
                need(stf.st_size <= CORE_FILE_CAP and total <= CORE_TREE_CAP,
                     "core-tree-envelope")
            digest_file(q, recf["sha256"], FILE_CAP, deadline_ms)
            seen_files.add(rel)
    need(seen_dirs == expected_dirs and seen_files == expected_files,
         "input-full-tree-membership")
    need(manifest_st.st_size <= MANIFEST_CAP, "manifest-size")
    return nodes

def valid_rel(s):
    return (isinstance(s, str) and 0 < len(s.encode("utf-8")) <= 4096 and not s.startswith("/") and
            "\\" not in s and not any(ord(c) < 32 or ord(c) == 127 for c in s) and
            all(p not in ("", ".", "..") and len(p) <= 255 for p in s.split("/")))

def is_sha(s):
    return isinstance(s, str) and re.fullmatch(r"[0-9a-f]{64}", s) is not None

def source_projection_sha(capture, tree_key):
    projection = {"validation_commit":capture["validation_commit"],
                  "previous_validation_commit":capture["previous_validation_commit"],
                  "source_count":capture["source_count"],
                  tree_key:capture["source_tree"], "source":capture["source"]}
    return hashlib.sha256(json.dumps(projection, sort_keys=True,
                                     separators=(",",":")).encode()).hexdigest()

def validate_case(case):
    need(case in ("N13", "N14", "N15"), "case-generation")
    return case

def validate_workflow_identity(value):
    need(re.fullmatch(r"github-actions-[0-9]{1,20}-[1-9][0-9]{0,3}", value) is not None,
         "workflow-identity-shape")
    return value

def calculate_work_deadline(now_wall, now_boot_ms, utc_epoch, deadline_ms):
    remain_wall = utc_epoch - now_wall
    remain_boot = (deadline_ms - now_boot_ms) / 1000
    need(0 < remain_wall <= JOB_MAX_SECONDS + 2 and
         0 < remain_boot <= JOB_MAX_SECONDS, "original-job-deadline-bound")
    need(abs(remain_wall - remain_boot) <= 2.5, "deadline-clock-disagreement")
    work_end = min(deadline_ms - JOB_CLEANUP_SECONDS * 1000,
                   now_boot_ms + int(min(remain_wall, remain_boot) * 1000) -
                   JOB_CLEANUP_SECONDS * 1000)
    need(work_end > now_boot_ms, "original-cleanup-reserve")
    return work_end

def write_all(fd, data):
    view = memoryview(data)
    while view:
        written = os.write(fd, view)
        need(written > 0, "short-write")
        view = view[written:]

def parse_deadline(value):
    need(re.fullmatch(r"[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z", value) is not None,
         "utc-deadline-shape")
    try:
        dt = datetime.strptime(value, "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=timezone.utc)
    except ValueError:
        raise Reject("utc-deadline-value")
    return dt.timestamp()

def parse_args(argv):
    # argparse's normal duplicate handling is last-value-wins; reject repeats first.
    valued = {"--generation", "--case", "--source-root", "--source-map", "--source-map-sha256",
              "--source-nodes", "--source-nodes-sha256", "--tool-root", "--tool-map",
              "--tool-map-sha256", "--tool-nodes", "--tool-nodes-sha256", "--runtime-root",
              "--runtime-map", "--runtime-map-sha256", "--runtime-nodes", "--runtime-nodes-sha256",
              "--build-receipt", "--build-receipt-sha256", "--source-capture",
              "--source-capture-sha256", "--job-deadline-utc", "--job-deadline-boottime-ms",
              "--workflow-identity"}
    seen = set()
    for arg in argv:
        key = arg.split("=", 1)[0]
        if key in valued:
            need(key not in seen, "duplicate-option")
            seen.add(key)
        else:
            need(not arg.startswith("--"), "unknown-option")
    need(seen == valued, "missing-option")
    ap = argparse.ArgumentParser(allow_abbrev=False)
    for name in sorted(valued):
        ap.add_argument(name, required=True)
    return ap.parse_args(argv)

def _valid_test_counters(result):
    counters = result.get("counters", {}) if isinstance(result, dict) else {}
    return (isinstance(result, dict) and result.get("native_acceptance") is False and
            is_sha(result.get("trx_sha256")) and
            all(counters.get(k) == value for k, value in
                {"total":"1", "executed":"1", "passed":"1", "failed":"0",
                 "error":"0", "notExecuted":"0", "timeout":"0", "aborted":"0"}.items()))



# Byte-identical common prerequisite contract; no native acceptance authority.
INTEGRATED_BUILD_SOURCE = '"""Pure build prerequisites shared by the image builder and native case readers.\n\nThis module performs no I/O and issues no native credit. All fourteen images\nmust carry actual passing source-selected core and CLI test results. Commands\nare checked by their operation, project and selector, not an incidental count\nof unrelated Git/package queries. Runtime case acceptance remains separate.\n"""\n\nimport pathlib\nimport re\n\nSCHEMA = "issue779-integrated16-build-v1"\nSELECTORS = ("None", *(f"N{i:02d}" for i in range(4, 17)))\nPIPE_METHOD = "OwnedRawPipeReadWrappingUsesHandleModeAndJoinsBothEofs"\nFRAME_METHOD = "OwnerDeathVariantIsExclusiveAndFrameIsExactBoundedData"\nCLI_METHOD = "CompiledImageOwnsOnlyItsSelectedCheckpoint"\nCORE_PROJECT = "Evidence/ForgeTrust.AppSurface.Evidence.Supervision.Tests/ForgeTrust.AppSurface.Evidence.Supervision.Tests.csproj"\nCLI_PROJECT = "Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj"\nCLI_TEST_PROJECT = "Cli/ForgeTrust.AppSurface.Cli.Tests/ForgeTrust.AppSurface.Cli.Tests.csproj"\nCORE_CLASS = "ForgeTrust.AppSurface.Evidence.Supervision.Tests."\nCLI_CLASS = "ForgeTrust.AppSurface.Cli.Tests.EvidenceNativeQualificationTests"\nSOURCE_PHASES = ("source_before", "source_after_assets", "source_after_build", "source_final")\n\n\nclass BuildContractError(ValueError):\n    """Closed metadata rejection; never include caller values in its message."""\n\n\ndef require(ok, category):\n    if not ok:\n        raise BuildContractError(category)\n\n\ndef is_sha(value):\n    return type(value) is str and re.fullmatch("[0-9a-f]{64}", value) is not None\n\n\ndef expected_regressions(selector):\n    require(selector in SELECTORS, "build-selector")\n    if selector in ("N13", "N14"):\n        return (("owner_death_phase_frame_regression", CORE_CLASS + "LinuxOwnerDeathPhaseFrameTests", FRAME_METHOD),\n                ("owner_death_cli_qualification", CLI_CLASS, CLI_METHOD))\n    return (("linux_pipe_regression", CORE_CLASS + "LinuxOutputPipesTests", PIPE_METHOD),\n            ("compiled_cli_qualification", CLI_CLASS, CLI_METHOD))\n\n\ndef validate_command_records(commands, selector):\n    require(selector in SELECTORS, "build-selector")\n    require(type(commands) is list and 0 < len(commands) <= 128, "build-command-bound")\n    for ordinal, row in enumerate(commands):\n        require(type(row) is dict and type(row.get("ordinal")) is int and row["ordinal"] == ordinal,\n                "build-command-ordinal")\n        args = row.get("argv")\n        require(type(args) is list and args and all(type(arg) is str and arg for arg in args),\n                "build-command-arguments")\n        # MSBuild accepts /p:, -property:, case aliases and multi-property\n        # arguments. A second selector in any such form cannot be ignored.\n        # Response files are also forbidden: they can supply unseen properties.\n        canonical = "-p:EvidencePrivateQualification=" + selector\n        require(not any(arg.startswith("@") for arg in args)\n                and all("evidenceprivatequalification" not in arg.lower()\n                        or (selector != "None" and arg == canonical) for arg in args),\n                "build-selector-alias-or-response-file")\n        require(type(row.get("exit")) is int and row["exit"] == 0 and row.get("failure") is None\n                and row.get("waited") is True and row.get("group_absent") is True\n                and row.get("forced_cleanup") is False and row.get("timed_out") is False\n                and row.get("late_completion") is not True, "build-command-terminal")\n        for name, expected in (("pipe_eofs", [True, True]), ("read_errors", [False, False]),\n                               ("write_errors", [False, False]), ("discarded_bytes", [0, 0])):\n            values = row.get(name)\n            require(type(values) is list and len(values) == 2\n                    and all(type(actual) is type(wanted) and actual == wanted\n                            for actual, wanted in zip(values, expected)), "build-command-output")\n        logs = row.get("logs")\n        require(type(logs) is list and len(logs) == 2\n                and all(type(log) is dict and is_sha(log.get("sha256"))\n                        and type(log.get("bytes")) is int and 0 <= log["bytes"] <= 8 * 1024 * 1024\n                        for log in logs), "build-command-logs")\n    selected = [row for row in commands if pathlib.PurePosixPath(row["argv"][0]).name == "dotnet"\n                and len(row["argv"]) > 1 and row["argv"][1] in ("restore", "publish", "test", "build")]\n    require([row["argv"][1] for row in selected] == ["restore", "publish", "restore", "test", "restore", "test"],\n            "build-operation-sequence")\n    projects = (CLI_PROJECT, CLI_PROJECT, CORE_PROJECT, CORE_PROJECT, CLI_TEST_PROJECT, CLI_TEST_PROJECT)\n    for row, project in zip(selected, projects):\n        args = row["argv"]\n        require(len(args) > 2 and args[2].endswith("/" + project), "build-project")\n        flags = [arg for arg in args if arg.startswith("-p:EvidencePrivateQualification=")]\n        require(flags == ([] if selector == "None" else ["-p:EvidencePrivateQualification=" + selector]),\n                "build-command-selector")\n        require(("--locked-mode" in args) if args[1] == "restore" else ("--no-restore" in args),\n                "build-restore-mode")\n        if args[1] in ("test", "publish"):\n            require("-p:UseSharedCompilation=false" in args, "build-compiler-mode")\n    for row, (_, class_name, method) in zip((selected[3], selected[5]), expected_regressions(selector)):\n        args = row["argv"]\n        require(args.count("--filter") == 1 and args[args.index("--filter") + 1]\n                == "FullyQualifiedName=" + class_name + "." + method, "build-test-filter")\n    require(sum(arg.startswith("-p:EvidencePrivateQualification=") for row in commands for arg in row["argv"])\n            == (0 if selector == "None" else 6), "build-selector-cardinality")\n    return True\n\n\ndef validate_build_receipt(receipt, selector, source):\n    """Validate a measured receipt against the independently pinned source tuple.\n\n    ``source`` has commit, parent, tree, count, capture, projection, map and\n    raw_map fields. It comes from the immutable common capture, never from the\n    receipt being validated. File/archive checks remain with the I/O caller.\n    """\n    require(selector in SELECTORS and type(receipt) is dict and type(source) is dict, "build-receipt-shape")\n    require(set(source) == {"commit", "parent", "tree", "count", "capture", "projection", "map", "raw_map"},\n            "build-source-binding-shape")\n    require(receipt.get("schema") == SCHEMA and type(receipt.get("exit")) is int and receipt["exit"] == 0\n            and receipt.get("failure") is None and receipt.get("diagnostics") == [], "build-receipt-terminal")\n    require(receipt.get("private_qualification") == selector and receipt.get("selected_variant") == selector,\n            "build-receipt-selector")\n    for field, binding in (("source_commit", "commit"), ("source_revision", "commit"), ("source_parent", "parent"),\n                           ("source_count", "count"), ("capture_sha256", "capture"),\n                           ("source_capture_sha256", "capture"), ("capture_input_sha256", "capture"),\n                           ("capture_projection_sha256", "projection"), ("source_map_sha256", "map"),\n                           ("source_capture_map_sha256", "raw_map"), ("harness_parent", "commit"),\n                           ("harness_retry_parent", "commit"), ("harness_retry_parent_parent", "parent")):\n        require(type(receipt.get(field)) is type(source[binding]) and receipt[field] == source[binding],\n                "build-receipt-source")\n    require(receipt.get("build_prerequisite_only") is True and receipt.get("test_execution") is True\n            and all(receipt.get(field) is False for field in ("native_execution", "native_acceptance", "checkpoint_pass",\n                                                            "review_clear", "source_review_clear", "fixture_review_clear",\n                                                            "root_transport_review_clear"))\n            and receipt.get("os_audit") is None, "build-receipt-scope")\n    validate_command_records(receipt.get("commands"), selector)\n    for phase in SOURCE_PHASES:\n        fact = receipt.get(phase)\n        require(type(fact) is dict and fact.get("head") == source["commit"] and fact.get("tree") == source["tree"]\n                and type(fact.get("count")) is int and fact["count"] == source["count"]\n                and fact.get("source_map_sha256") == source["map"]\n                and all(fact.get(key) is True for key in ("index_tree_matches", "physical_git_sha1", "physical_sha256_modes")),\n                "build-source-recheck")\n    for key, class_name, method in expected_regressions(selector):\n        fact = receipt.get(key)\n        require(type(fact) is dict and fact.get("class_name") == class_name and fact.get("method") == method\n                and fact.get("native_acceptance") is False and fact.get("root_factory_exercised") is False\n                and fact.get("unprivileged_library_behavior_only") is True\n                and is_sha(fact.get("trx_sha256")), "build-regression-binding")\n        counters = fact.get("counters")\n        require(type(counters) is dict and all(counters.get(key) == value for key, value in\n                {"total": "1", "executed": "1", "passed": "1", "failed": "0", "error": "0",\n                 "notExecuted": "0", "timeout": "0", "aborted": "0"}.items()), "build-regression-outcome")\n    known = {"owner_death_phase_frame_regression", "owner_death_cli_qualification", "linux_pipe_regression",\n             "compiled_cli_qualification"}\n    expected = {row[0] for row in expected_regressions(selector)}\n    require(not (known - expected) & set(receipt), "build-regression-foreign-selection")\n    return True\n'
INTEGRATED_BUILD_SOURCE_SHA = '4e1e774f57d1b878a0985ac218a27490ae4de8da1f21f81cd696dbe6e9c399f8'
INTEGRATED_SOURCE_BINDING = {'commit': '71d004d6cf5a791ddbb8e73e3c538248985a8505', 'parent': '957da1f23ff743778fa39e3e559511ccdb281345', 'tree': 'd15b1f3a7532947d525e4d74cdc7457f6b8923ea', 'count': 2872, 'capture': '8f5822ab0f5da555ef142a28321631f1960e61463fe14a83b33e83d521934046', 'projection': 'c2e846a09c6e7561eea1a1531d34a422ba0b7379a05be4e4fcb17bd18134f891', 'map': '308346155ca912db28838729e5081791e2a7b6a50bcac726093c6f07d1d85abe', 'raw_map': '3a09062a50b035b1534ca89cc77dd29353d4ce20769ae01b4ccffbbecbd140a3'}
_integrated_build_scope = {}
if hashlib.sha256(INTEGRATED_BUILD_SOURCE.encode()).hexdigest() != INTEGRATED_BUILD_SOURCE_SHA:
    raise ValueError('integrated-build-contract-pin')
exec(compile(INTEGRATED_BUILD_SOURCE, '<pinned-pure-build-contract>', 'exec'), _integrated_build_scope)
def validate_integrated_build(receipt, selector):
    _integrated_build_scope['validate_build_receipt'](receipt, selector, INTEGRATED_SOURCE_BINDING)
    validate_integrated_artifact_metadata(receipt)
def validate_integrated_artifact_metadata(receipt):
    artifacts = receipt.get('artifacts')
    check = _integrated_build_scope['require']
    check(all(name not in receipt or receipt[name] is False for name in
              ('runtime_acceptance', 'trusted_enabled', 'native_credit', 'all16_final_integrated_native_verified')),
          'build-supplemental-acceptance-claim')
    check(type(artifacts) is dict and set(artifacts) == {'source', 'tool', 'runtime'}, 'build-artifact-roles')
    for role, fact in artifacts.items():
        check(type(fact) is dict and _integrated_build_scope['is_sha'](fact.get('tsv_sha256'))
              and _integrated_build_scope['is_sha'](fact.get('nodes_sha256')), 'build-artifact-digests')
        check(type(fact.get('file_count')) is int and 0 < fact['file_count'] <= 8192
              and type(fact.get('node_count')) is int and fact['file_count'] <= fact['node_count'] <= 8192
              and type(fact.get('total_bytes')) is int and 0 <= fact['total_bytes'] <= 1073741824,
              'build-artifact-counts')
    check(artifacts['source']['file_count'] == INTEGRATED_SOURCE_BINDING['count'], 'build-artifact-source-count')


def validate_build_receipt_data(receipt, case):
    need(case in BUILD_CONTRACTS, "build-case")
    need(case in ("N13", "N14", "N15"), "build-selector")
    contract = BUILD_CONTRACTS[case]
    validate_integrated_build(receipt, case)
    if case in ("N13", "N14"):
        need(receipt.get("case") == case and
             receipt.get("duration_policy_seconds") ==
             {"original_request":240,"owner_runtime":240,"timeout_stop":5,"n14_owner_bound":245} and
             "linux_pipe_regression" not in receipt, "owner-death-build-shape")
    else:
        need(all(field not in receipt for field in
                 ("case", "duration_policy_seconds",
                  "owner_death_phase_frame_regression", "owner_death_cli_qualification")) and
             "linux_pipe_regression" in receipt, "n15-build-shape")
        pipe = receipt.get("linux_pipe_regression")
        need(type(pipe) is dict and
             set(pipe) == {"method", "class_name", "counters", "trx_sha256",
                          "unprivileged_library_behavior_only", "root_factory_exercised",
                          "native_acceptance", "runtime_basis"} and
             pipe.get("runtime_basis") == "selected SDK dotnet host",
             "n15-linux-pipe-shape")
    snapshots = ("source_before", "source_after_assets", "source_after_build", "source_final")
    for field in snapshots:
        snap = receipt.get(field)
        need(type(snap) is dict and snap.get("head") == contract["source"] and
             snap.get("tree") == contract["tree"] and snap.get("count") == SOURCE_COUNT and
             snap.get("source_map_sha256") == INTEGRATED_SOURCE_BINDING["map"] and
             snap.get("physical_sha256_modes") is True and
             snap.get("physical_git_sha1") is True and snap.get("index_tree_matches") is True,
             "build-source-snapshot")
    arts = receipt.get("artifacts")
    need(type(arts) is dict and set(arts) == {"source", "tool", "runtime"} and
         type(arts.get("source")) is dict and arts["source"].get("file_count") == SOURCE_COUNT,
         "build-artifact-roles-and-source-count")
    return receipt

def build_receipt(args, deadline_ms):
    receipt = read_json(args.build_receipt, args.build_receipt_sha256, CONTROL_CAP, deadline_ms)
    return validate_build_receipt_data(receipt, args.case)

def copy_role(src, dst, nodes, deadline_ms):
    dst.mkdir(mode=0o700, parents=True, exist_ok=False)
    for rel in sorted(nodes["directories"], key=lambda x: (x.count("/"), x)):
        check_deadline(deadline_ms)
        if rel == ".": continue
        target = dst / rel
        target.mkdir(mode=0o700, parents=True, exist_ok=False)
    for rel, rec in sorted(nodes["files"].items()):
        check_deadline(deadline_ms)
        source, target = src / rel, dst / rel
        before = source.lstat()
        need(stat.S_ISREG(before.st_mode) and before.st_nlink == 1 and
             before.st_uid == 0 and before.st_gid == 0 and
             stat.S_IMODE(before.st_mode) == int(rec["mode"], 8), "copy-source-shape")
        infd = os.open(source, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK | os.O_CLOEXEC)
        outfd = os.open(target, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC, 0o600)
        h = hashlib.sha256(); total = 0
        try:
            opened = os.fstat(infd)
            need(identity(before) == identity(opened), "copy-source-substitution")
            while True:
                check_deadline(deadline_ms)
                block = os.read(infd, 1024 * 1024)
                if not block: break
                total += len(block); need(total <= rec["bytes"], "copy-source-growth")
                h.update(block)
                view = memoryview(block)
                while view:
                    n = os.write(outfd, view); view = view[n:]
            after = os.fstat(infd)
            need(identity(opened) == identity(after) and total == rec["bytes"] and
                 h.hexdigest() == rec["sha256"], "copy-source-changed")
            os.fchown(outfd, 0, 0)
            os.fchmod(outfd, 0o555 if int(rec["mode"],8) & 0o111 else 0o444)
            os.fsync(outfd)
        finally:
            os.close(infd); os.close(outfd)
    for rel, rec in sorted(nodes["directories"].items(), key=lambda x: (-x[0].count("/"), x[0])):
        target = dst if rel == "." else dst / rel
        os.chown(target, 0, 0, follow_symlinks=False)
        os.chmod(target, 0o555, follow_symlinks=False)
    return

def main(argv):
    need(os.geteuid() == 0 and sys.platform.startswith("linux"), "trusted-linux-root-required")
    args = parse_args(argv)
    need(re.fullmatch(r"[0-9a-f]{32}", args.generation) is not None, "generation-shape")
    validate_case(args.case)
    contract = BUILD_CONTRACTS[args.case]
    for k in ("source_map_sha256", "source_nodes_sha256", "tool_map_sha256", "tool_nodes_sha256",
              "runtime_map_sha256", "runtime_nodes_sha256", "build_receipt_sha256", "source_capture_sha256"):
        need(is_sha(getattr(args, k)), "digest-syntax")
    inroot = INPUT_ROOT / ("appsurface-evidence-input-" + args.generation)
    safe_ancestors(inroot.parent)
    roots = {"source":Path(args.source_root), "tool":Path(args.tool_root), "runtime":Path(args.runtime_root)}
    for role, root in roots.items():
        need(root == inroot / role, "role-root-binding")
    need(re.fullmatch(r"[1-9][0-9]{0,14}", args.job_deadline_boottime_ms) is not None,
         "boottime-deadline-shape")
    deadline_ms = int(args.job_deadline_boottime_ms)
    utc_epoch = parse_deadline(args.job_deadline_utc)
    now_wall = time.time(); now_boot = boottime_ms()
    # Materialization stops before the original cleanup reserve begins.
    work_deadline = calculate_work_deadline(now_wall, now_boot, utc_epoch, deadline_ms)
    check_deadline(work_deadline)
    workflow = validate_workflow_identity(args.workflow_identity)
    receipt = build_receipt(args, work_deadline)
    capture = read_json(args.source_capture, args.source_capture_sha256, CONTROL_CAP, work_deadline)
    need(args.source_capture_sha256 == contract["capture"] and
         capture.get("schema") == "issue779-csharp-common-source-capture-v4" and
         capture.get("validation_commit") == contract["source"] and
         capture.get("previous_validation_commit") == contract["parent"] and
         capture.get("source_tree") == contract["tree"] and capture.get("source_count") == SOURCE_COUNT and
         capture.get("clean") is True and capture.get("index_checksum_verified") is True and
         capture.get("index_tree_blob_modes_equal") is True and capture.get("no_alternates") is True and
         capture.get("native_credit") is False and
         capture.get("all16_final_integrated_native_verified") is False and
         isinstance(capture.get("source"), dict) and
         set(capture["source"]) == {"sha256", "modes"}, "current-capture-binding")
    compact_map = hashlib.sha256(json.dumps({"sha256":capture["source"]["sha256"],"modes":capture["source"]["modes"]},
                                           sort_keys=True,separators=(",",":")).encode()).hexdigest()
    raw_map = {name:{"sha256":capture["source"]["sha256"][name],
                     "mode":capture["source"]["modes"][name]}
               for name in sorted(capture["source"]["sha256"])}
    raw_map_sha = hashlib.sha256(json.dumps(raw_map, sort_keys=True, separators=(",",":")).encode()).hexdigest()
    capture_projection_sha = source_projection_sha(capture, "tree")
    build_projection_sha = source_projection_sha(capture, contract["projection_tree_key"])
    need(compact_map == contract["map"] and raw_map_sha == contract["raw_map"] and
         capture_projection_sha == contract["capture_projection"] and
         build_projection_sha == contract["build_projection"] and
         receipt.get("capture_projection_sha256") == build_projection_sha,
         "capture-map-projection")
    input_files = {}
    for role in ("source", "tool", "runtime"):
        prefix = role.replace("-", "_")
        manifest = getattr(args, role + "_map")
        nodes_path = getattr(args, role + "_nodes")
        nodes = validate_tree(role, roots[role], manifest,
            getattr(args, role + "_map_sha256"), nodes_path,
            getattr(args, role + "_nodes_sha256"), receipt, work_deadline)
        input_files[role] = nodes
    src_nodes = input_files["source"]
    need(set(capture["source"]) == {"sha256", "modes"} and
         len(capture["source"]["sha256"]) == SOURCE_COUNT and
         set(capture["source"]["sha256"]) == set(src_nodes["files"]) and
         set(capture["source"]["modes"]) == set(src_nodes["files"]), "capture-source-membership")
    for rel, rec in src_nodes["files"].items():
        need(capture["source"]["sha256"][rel] == rec["sha256"] and
             capture["source"]["modes"][rel] == rec["mode"], "capture-source-map-binding")
    for role in ("source", "tool", "runtime"):
        manifest_path = Path(getattr(args, role + "_map"))
        nodes_path = Path(getattr(args, role + "_nodes"))
        need(manifest_path.is_relative_to(inroot) and nodes_path.is_relative_to(inroot),
             "control-file-root-binding")
    receipt_path = Path(args.build_receipt)
    capture_path = Path(args.source_capture)
    need(receipt_path.is_relative_to(inroot) and capture_path.is_relative_to(inroot),
         "review-control-root-binding")
    need("fixture-policy.json" not in input_files["tool"]["files"], "policy-collision")
    stage = FIXTURE_ROOT / args.generation
    private = PRIVATE_ROOT / args.generation
    safe_ancestors(FIXTURE_ROOT.parent); safe_ancestors(PRIVATE_ROOT.parent)
    for fixed_root in (FIXTURE_ROOT, PRIVATE_ROOT):
        if not fixed_root.exists():
            fixed_root.mkdir(mode=0o755)
            os.chown(fixed_root, 0, 0)
            os.chmod(fixed_root, 0o755)
        stroot = fixed_root.lstat()
        need(stat.S_ISDIR(stroot.st_mode) and stroot.st_uid == 0 and stroot.st_gid == 0 and
             (stat.S_IMODE(stroot.st_mode) & 0o022) == 0, "fixture-root-custody")
    need(not stage.exists() and not stage.is_symlink() and not private.exists() and not private.is_symlink(),
         "generation-collision")
    FIXTURE_ROOT.mkdir(mode=0o755, parents=True, exist_ok=True)
    PRIVATE_ROOT.mkdir(mode=0o755, parents=True, exist_ok=True)
    os.mkdir(stage, 0o700); os.chown(stage, 0, 0)
    os.mkdir(private, 0o700); os.chown(private, 0, 0)
    try:
        for role, destname in (("tool", "tool"), ("runtime", "runtime"), ("source", "subject")):
            copy_role(roots[role], stage / destname, input_files[role], work_deadline)
        os.chmod(stage / "tool", 0o755)
        policy = {"Id":"checkpoint-empty","Version":"1","ConservativeProfileId":"conservative",
            "Profiles":[{"Id":"empty","Scope":"Targeted","Resources":[],"Producers":[],"Obligations":[]},
                        {"Id":"conservative","Scope":"Release","Resources":[{"Id":"build","Readiness":"completion","DeadlineSeconds":1,"Requires":[]}],"Producers":[],"Obligations":[]}],
            "Rules":[{"Id":"docs","Pattern":"docs/**","ProfileId":"empty","Precedence":0}]}
        policy_bytes = json.dumps(policy, sort_keys=True, separators=(",", ":")).encode()
        policy_path = stage / "tool" / "fixture-policy.json"
        need(not policy_path.exists(), "policy-collision")
        fd = os.open(policy_path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
        try:
            write_all(fd, policy_bytes); os.fchown(fd, 0, 0); os.fchmod(fd, 0o444); os.fsync(fd)
        finally: os.close(fd)
        os.chmod(stage / "tool", 0o555)
        os.chmod(stage, 0o555)
        # Verify all source roots again after copying, then verify all sealed destinations.
        for role, destname in (("tool","tool"),("runtime","runtime"),("source","subject")):
            nodes = input_files[role]
            verify_materialized(stage / destname, nodes, work_deadline,
                                allow_policy=(role == "tool"), policy_path=policy_path)
            validate_tree(role, roots[role], getattr(args, role + "_map"),
                getattr(args, role + "_map_sha256"), getattr(args, role + "_nodes"),
                getattr(args, role + "_nodes_sha256"), receipt, work_deadline)
        need("ForgeTrust.AppSurface.Cli.dll" in input_files["tool"]["files"] and
             "ForgeTrust.AppSurface.Cli.deps.json" in input_files["tool"]["files"] and
             "ForgeTrust.AppSurface.Cli.runtimeconfig.json" in input_files["tool"]["files"] and
             "dotnet" in input_files["runtime"]["files"],
             "normal-fdd-inputs-missing")
        request = {"schema":"evidence-supervisor-linux-v1","mode":"observation",
            "tool_root":str(stage / "tool"),"runtime_root":str(stage / "runtime"),
            "runtime_host":str(stage / "runtime" / "dotnet"),
            "entry_path":str(stage / "tool" / "ForgeTrust.AppSurface.Cli.dll"),
            "policy_file":str(policy_path),"subject_root":str(stage / "subject"),
            "base_revision":contract["parent"],"subject_revision":contract["source"],"workflow_identity":workflow,
            "paths":["docs/designs/issue-779-csharp-supervision-core.md"],
            "observation_profile_ids":["empty"],"observation_producer_ids":[],
            "admission_seconds":10,"start_seconds":30,"collection_seconds":10,
            "cleanup_seconds":JOB_CLEANUP_SECONDS,"stopping_seconds":5,
            "job_deadline_utc":args.job_deadline_utc}
        req_bytes = (json.dumps(request, sort_keys=True, separators=(",", ":")) + "\n").encode()
        need(len(req_bytes) <= 65536 and not (stage / "subject" / request["paths"][0]).is_symlink(),
             "request-bound")
        req_path = private / "request.json"
        fd = os.open(req_path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
        try:
            write_all(fd, req_bytes); os.fchown(fd, 0, 0); os.fchmod(fd, 0o600); os.fsync(fd)
        finally: os.close(fd)
        req_sha = hashlib.sha256(req_bytes).hexdigest()
        policy_sha = hashlib.sha256(policy_bytes).hexdigest()
        _, verified_policy = read_pinned(policy_path, policy_sha, 4096, work_deadline)
        need(verified_policy == policy_bytes, "policy-final-pin")
        checksums = private / "request-policy.sha256"
        checksum_bytes = (f"{req_sha}  {req_path}\n{policy_sha}  {policy_path}\n").encode()
        fd = os.open(checksums, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
        try:
            write_all(fd, checksum_bytes); os.fchown(fd,0,0); os.fchmod(fd,0o600); os.fsync(fd)
        finally: os.close(fd)
        # Re-read exact generated metadata after final writes; it is still staging-only.
        _, final_req = read_pinned(req_path, req_sha, 65536, work_deadline)
        _, final_policy = read_pinned(policy_path, policy_sha, 4096, work_deadline)
        checksum_sha = hashlib.sha256(checksum_bytes).hexdigest()
        _, final_checksum = read_pinned(checksums, checksum_sha, 4096, work_deadline)
        need(final_policy == policy_bytes and json.loads(final_req) == request and
             final_checksum == checksum_bytes, "final-request-policy-binding")
        check_deadline(work_deadline)
        print("CASE_WORKSPACE_STAGED:%s:%s:REQUEST_AND_POLICY_PINNED:NO_OWNER_LAUNCH:NO_NATIVE_CREDIT" %
              (args.case, args.generation))
    except Exception:
        # Preserve partial generation for bounded external quarantine; never recursively delete it.
        try: os.chmod(stage, 0o700, follow_symlinks=False)
        except OSError: pass
        raise

def walk_files(root):
    out = set()
    for base, dirs, files in os.walk(root, followlinks=False):
        for d in dirs: need(not (Path(base)/d).is_symlink(), "materialized-symlink")
        for f in files: out.add(Path(base)/f)
    return out

def verify_materialized(root, nodes, deadline_ms, allow_policy=False, policy_path=None):
    expected = set(nodes["files"])
    if allow_policy: expected.add("fixture-policy.json")
    dirs_expected = set(nodes["directories"])
    seen_f, seen_d = set(), set()
    for base, dirs, files in os.walk(root, topdown=True, followlinks=False):
        check_deadline(deadline_ms)
        bp = Path(base); relbase = bp.relative_to(root).as_posix()
        if relbase == ".": relbase = "."
        st = bp.lstat(); need(stat.S_ISDIR(st.st_mode) and st.st_uid == 0 and st.st_gid == 0 and
            stat.S_IMODE(st.st_mode) == 0o555, "sealed-directory-shape")
        if relbase != ".": need(relbase in dirs_expected, "sealed-extra-directory")
        seen_d.add(relbase)
        for d in dirs: need(not (bp/d).is_symlink(), "sealed-symlink")
        for name in files:
            p = bp/name; rel = p.relative_to(root).as_posix(); stf=p.lstat()
            need(stat.S_ISREG(stf.st_mode) and stf.st_nlink==1 and stf.st_uid==0 and stf.st_gid==0,
                 "sealed-file-shape")
            if rel == "fixture-policy.json" and allow_policy:
                need(stf.st_size <= 4096 and stat.S_IMODE(stf.st_mode)==0o444, "policy-shape")
                continue
            need(rel in nodes["files"], "sealed-extra-file")
            rec=nodes["files"][rel]
            need(stf.st_size==rec["bytes"] and stat.S_IMODE(stf.st_mode)==
                 (0o555 if int(rec["mode"],8)&0o111 else 0o444), "sealed-file-map")
            digest_file(p, rec["sha256"], FILE_CAP, deadline_ms); seen_f.add(rel)
    need(seen_f==expected-({"fixture-policy.json"} if allow_policy else set()) and
         seen_d==dirs_expected, "sealed-tree-membership")
    if allow_policy:
        need(policy_path is not None and policy_path.exists(), "policy-missing")
        need(sum(r["bytes"] for r in nodes["files"].values()) + policy_path.stat().st_size <= CORE_TREE_CAP and
             len(nodes["files"]) + len(nodes["directories"]) + 1 <= CORE_NODE_CAP,
             "policy-core-envelope")

def main_guard():
    try:
        main(sys.argv[1:])
    except Reject as exc:
        print("CASE_WORKSPACE_REJECTED:%s" % exc.code, file=sys.stderr)
        return 1
    except (OSError, OverflowError, TypeError, KeyError, ValueError):
        print("CASE_WORKSPACE_REJECTED:bounded-input-or-filesystem", file=sys.stderr)
        return 1
    return 0

if __name__ == "__main__":
    raise SystemExit(main_guard())
