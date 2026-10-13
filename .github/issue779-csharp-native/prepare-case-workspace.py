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

SOURCE = "a6933fa3dc2c7a88832abf337447f88527477703"
PARENT = "0adef6032217b4181898a4d6d129e1248cb0eda3"
SOURCE_TREE = "867aec31f950052666275268f08fe1a7f7fa3c25"
SOURCE_COUNT = 2869
CAPTURE_SHA = "b1e43764ca61d2430f4cfc6e77aa20c3504fcc1be226e1d8e59f21ef127953e7"
CAPTURE_MAP_SHA = "c541eb54bce090149404e8a3c0c4036e684096f0170e669aed65453a69ad894a"
CAPTURE_RAW_MAP_SHA = "66566e2243da54a4cc34266a7652c35bf58de73e73892fdffd10fb4518ab9a38"
N15_BUILD_PROJECTION_SHA = "5aa7188c4dbe5632d7602271eff23837fc49f098a3ae46b9e06d1157a2cc476a"
OWNER_SOURCE = SOURCE
OWNER_PARENT = PARENT
OWNER_SOURCE_TREE = SOURCE_TREE
OWNER_CAPTURE_SHA = CAPTURE_SHA
OWNER_CAPTURE_MAP_SHA = CAPTURE_MAP_SHA
OWNER_CAPTURE_RAW_MAP_SHA = CAPTURE_RAW_MAP_SHA
OWNER_BUILD_PROJECTION_SHA = "0c5f9605840c134d9b21866f2eb813111884a431540f0da2eb516ff0146e0c8c"
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
            "projection_tree_key": "source_tree", "shape": "linux-pipe"},
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
    expected_root_mode = 0o700 if role == "source" else 0o555
    need(stat.S_ISDIR(rst.st_mode) and rst.st_uid == 0 and rst.st_gid == 0 and
         stat.S_IMODE(rst.st_mode) == expected_root_mode, "input-root-custody")
    manifest_st = digest_file(manifest_path, manifest_sha, MANIFEST_CAP, deadline_ms)
    _, node_bytes = read_pinned(nodes_path, nodes_sha, CONTROL_CAP, deadline_ms)
    try:
        nodes = json.loads(node_bytes, object_pairs_hook=unique_pairs)
    except (ValueError, UnicodeError):
        raise Reject("node-json-shape")
    need(json.dumps(nodes, sort_keys=True, separators=(",", ":")).encode() == node_bytes,
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
        need(rec is not None and stat.S_IMODE(st.st_mode) == int(rec["mode"], 8),
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

def validate_build_command_records(receipt, case):
    commands = receipt.get("commands")
    need(type(commands) is list and 43 <= len(commands) <= 64 and
         [row.get("ordinal") for row in commands if type(row) is dict] == list(range(len(commands))),
         "build-command-record-count-or-ordinals")
    selector = "-p:EvidencePrivateQualification=" + case
    for row in commands:
        need(type(row) is dict and type(row.get("argv")) is list and
             all(type(arg) is str for arg in row["argv"]), "build-command-shape")
        logs = row.get("logs")
        need(type(row.get("exit")) is int and row["exit"] == 0 and
             row.get("failure") is None and row.get("waited") is True and
             row.get("group_absent") is True and row.get("timed_out") is False and
             row.get("forced_cleanup") is False and row.get("late_completion") is not True and
             type(logs) is list and len(logs) == 2 and
             all(type(log) is dict and is_sha(log.get("sha256")) for log in logs),
             "build-command-settlement")
    dotnet = [row for row in commands
              if row["argv"] and Path(row["argv"][0]).name == "dotnet" and
              len(row["argv"]) > 1 and row["argv"][1] in ("restore", "publish", "test")]
    if case in ("N13", "N14"):
        need([row["argv"][1] for row in dotnet] ==
             ["restore", "publish", "restore", "test", "restore", "test"],
             "owner-death-build-command-order")
        for row in dotnet:
            args = row["argv"]
            need(args.count(selector) == 1 and
                 sum(arg.startswith("-p:EvidencePrivateQualification=") for arg in args) == 1,
                 "owner-death-build-command-selector")
        need(sum(arg.startswith("-p:EvidencePrivateQualification=")
                 for row in commands for arg in row["argv"]) == 6,
             "owner-death-build-selector-cardinality")
        return

    need(len(commands) == 43 and [row["ordinal"] for row in commands] == list(range(43)),
         "n15-build-command-inventory")
    need([row["argv"][1] for row in dotnet] == ["restore", "publish", "restore", "test"],
         "n15-build-command-order")
    cli_project = "/Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj"
    selected = [row for row in dotnet if len(row["argv"]) > 2 and
                row["argv"][1] in ("restore", "publish") and
                row["argv"][2].endswith(cli_project)]
    need([row["argv"][1] for row in selected] == ["restore", "publish"] and
         all(row["argv"].count(selector) == 1 and
             sum(arg.startswith("-p:EvidencePrivateQualification=") for arg in row["argv"]) == 1
             for row in selected) and
         sum(arg.startswith("-p:EvidencePrivateQualification=")
             for row in commands for arg in row["argv"]) == 2,
         "n15-private-cli-selector")

def validate_build_receipt_data(receipt, case):
    need(case in BUILD_CONTRACTS, "build-case")
    contract = BUILD_CONTRACTS[case]
    common = (receipt.get("schema") == "issue779-csharp-fdd-build-v5" and
              type(receipt.get("exit")) is int and receipt["exit"] == 0 and
              receipt.get("failure") is None and receipt.get("diagnostics") == [] and
              receipt.get("private_qualification") == case and
              receipt.get("selected_variant") == case and
              receipt.get("source_commit") == contract["source"] and
              receipt.get("capture_input_sha256") == contract["capture"] and
              receipt.get("capture_projection_sha256") == contract["build_projection"] and
              receipt.get("build_prerequisite_only") is True and
              receipt.get("native_execution") is False and
              receipt.get("checkpoint_pass") is False)
    need(common, "build-variant-source-binding")
    if case in ("N13", "N14"):
        need(receipt.get("case") == case and receipt.get("source_revision") == contract["source"] and
             receipt.get("source_parent") == contract["parent"] and
             receipt.get("harness_parent") == contract["source"] and
             receipt.get("harness_retry_parent") == contract["source"] and
             receipt.get("harness_retry_parent_parent") == contract["parent"] and
             receipt.get("source_count") == SOURCE_COUNT and
             receipt.get("source_capture_sha256") == contract["capture"] and
             receipt.get("source_map_sha256") == contract["map"] and
             receipt.get("source_capture_map_sha256") == contract["raw_map"] and
             receipt.get("test_execution") is True and
             receipt.get("native_acceptance") is False and
             receipt.get("review_clear") is False and
             receipt.get("source_review_clear") is False and
             receipt.get("fixture_review_clear") is False and
             receipt.get("root_transport_review_clear") is False and
             receipt.get("duration_policy_seconds") ==
             {"original_request":240,"owner_runtime":240,"timeout_stop":5,"n14_owner_bound":245} and
             "linux_pipe_regression" not in receipt,
             "owner-death-build-contract")
        for field in ("owner_death_phase_frame_regression", "owner_death_cli_qualification"):
            need(_valid_test_counters(receipt.get(field)), "owner-death-build-test-selection")
        phase = receipt["owner_death_phase_frame_regression"]
        need(phase.get("root_factory_exercised") is False and
             phase.get("unprivileged_library_behavior_only") is True,
             "owner-death-build-test-scope")
    else:
        need(receipt.get("harness_parent") == contract["source"] and
             receipt.get("harness_retry_parent") == contract["source"] and
             receipt.get("harness_retry_parent_parent") == contract["parent"] and
             all(field not in receipt for field in
                 ("case", "source_revision", "source_parent", "source_count",
                  "source_capture_sha256", "source_capture_map_sha256", "source_map_sha256",
                  "duration_policy_seconds", "test_execution")) and
             "owner_death_phase_frame_regression" not in receipt and
             "owner_death_cli_qualification" not in receipt and
             "linux_pipe_regression" in receipt,
             "n15-build-contract-shape")
        pipe = receipt.get("linux_pipe_regression")
        counters = pipe.get("counters", {}) if type(pipe) is dict else {}
        need(type(pipe) is dict and
             set(pipe) == {"method", "counters", "trx_sha256", "unprivileged_library_behavior_only",
                           "root_factory_exercised", "native_acceptance", "runtime_basis"} and
             pipe.get("method") == "OwnedRawPipeReadWrappingUsesHandleModeAndJoinsBothEofs" and
             all(counters.get(key) == "1" for key in ("total", "executed", "passed")) and
             all(counters.get(key) == "0" for key in
                 ("failed", "error", "notExecuted", "timeout", "aborted")) and
             is_sha(pipe.get("trx_sha256")) and
             pipe.get("unprivileged_library_behavior_only") is True and
             pipe.get("root_factory_exercised") is False and
             pipe.get("native_acceptance") is False and
             pipe.get("runtime_basis") == "selected SDK dotnet host",
             "n15-linux-pipe-build-contract")
    validate_build_command_records(receipt, case)
    snapshots = ("source_before", "source_after_assets", "source_after_build", "source_final")
    for field in snapshots:
        snap = receipt.get(field)
        need(type(snap) is dict and snap.get("head") == contract["source"] and
             snap.get("tree") == contract["tree"] and snap.get("count") == SOURCE_COUNT and
             snap.get("source_map_sha256") == contract["map"] and
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
    capture_projection_sha = source_projection_sha(capture, "source_tree")
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
             "dotnet" in input_files["runtime"]["files"] and
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
