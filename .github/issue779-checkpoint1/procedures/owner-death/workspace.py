"""N13/N14 sealed workspace operations retained from the historical real procedure.

These functions inspect/copy real files only. Current source/build verification lives
in adapter.py and the shared binding.py; no historical build/TRX schema is accepted.
The native copy, custody, UTC/boottime budgets and sealed-tree checks below are retained.
"""
import hashlib
import os
from pathlib import Path
import re
import stat
import sys
import time
from datetime import datetime, timezone

FIXTURE_ROOT = Path("/var/lib/appsurface-evidence-fixture")
PRIVATE_ROOT = Path("/run/appsurface-evidence-fixture")
FILE_CAP = 2 * 1024 * 1024 * 1024
CORE_TREE_CAP = 1024 * 1024 * 1024
CORE_NODE_CAP = 8192
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

def valid_rel(s):
    return (isinstance(s, str) and 0 < len(s.encode("utf-8")) <= 4096 and not s.startswith("/") and
            "\\" not in s and not any(ord(c) < 32 or ord(c) == 127 for c in s) and
            all(p not in ("", ".", "..") and len(p) <= 255 for p in s.split("/")))

def is_sha(s):
    return isinstance(s, str) and re.fullmatch(r"[0-9a-f]{64}", s) is not None

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
