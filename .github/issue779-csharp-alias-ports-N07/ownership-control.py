"""Defined Linux borrowed-tool ownership control; no alias/native authority."""
import argparse
import ctypes
import json
import os
import pathlib
import re
import resource
import select
import signal
import stat
import subprocess
import time


def now():
    return time.clock_gettime_ns(time.CLOCK_BOOTTIME) // 1000000


def identity(s):
    return (s.st_dev, s.st_ino, s.st_mode, s.st_uid, s.st_gid, s.st_nlink, s.st_size, s.st_mtime_ns, s.st_ctime_ns)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--root', required=True)
    parser.add_argument('--work-end-ms', required=True, type=int)
    args = parser.parse_args()
    original = args.work_end_ms
    root = pathlib.Path(args.root)
    end = original
    controller = os.getpid()
    outer_group = os.getpgrp()
    if (re.fullmatch(r'/var/lib/issue779-alias-[0-9a-f]{32}', str(root)) is None
            or os.geteuid() != 0 or outer_group != controller or not 0 < end - now() <= 80000):
        return 1
    work = root / 'ownership'
    work.mkdir(mode=0o700)
    failure = None
    proc = None
    logs = []
    references = {}
    reaped = []
    tool = None
    record = {}
    started = now()

    def check():
        if now() >= end:
            raise ValueError('original-deadline')

    def fail(category):
        nonlocal failure
        if failure is None:
            failure = category

    def fresh(path, data, mode=0o600):
        fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, mode)
        error = None
        try:
            view = memoryview(data)
            while view:
                check()
                written = os.write(fd, view)
                if written <= 0:
                    raise ValueError('write')
                view = view[written:]
        except BaseException as exc:
            error = exc
        finally:
            try:
                os.close(fd)
            except BaseException as exc:
                error = error or exc
        if error is not None:
            raise error

    def limit():
        resource.setrlimit(resource.RLIMIT_FSIZE, (1048576, 1048576))
        resource.setrlimit(resource.RLIMIT_CORE, (0, 0))

    def children(pid):
        # Only actual descendants of this fresh test process are enumerated.
        path = pathlib.Path('/proc') / str(pid) / 'task' / str(pid) / 'children'
        try:
            with path.open('rb') as source:
                raw = source.read(4097)
        except FileNotFoundError:
            return []
        if len(raw) > 4096 or re.fullmatch(rb'(?:[0-9]+\s*)*', raw) is None:
            raise ValueError('children-bound')
        values = [int(v) for v in raw.split()]
        if len(values) > 16 or any(v <= 1 or v == controller for v in values):
            raise ValueError('children-domain')
        return values

    def retain(pid):
        if pid in references:
            return
        if pid <= 1 or pid == controller or len(references) >= 16:
            raise ValueError('retained-domain')
        try:
            before_group = os.getpgid(pid)
            fd = os.pidfd_open(pid, 0)
        except ProcessLookupError:
            return
        # Register the real kernel handle BEFORE any post-open operation can fail.
        references[pid] = fd
        if before_group != outer_group or os.getpgid(pid) != outer_group:
            raise ValueError('retained-group')
        check()

    def collect():
        check()
        queue = children(controller)
        visited = set()
        while queue:
            check()
            pid = queue.pop(0)
            if pid in visited:
                continue
            visited.add(pid)
            if len(visited) > 16:
                raise ValueError('subtree-bound')
            retain(pid)
            queue.extend(children(pid))

    try:
        check()
        libc = ctypes.CDLL(None, use_errno=True)
        if libc.prctl(36, 1, 0, 0, 0) != 0:
            raise ValueError('subreaper-unavailable')
        check()
        payload = (root / 'code' / 'timeout-shim.sh').read_bytes()
        if payload != b'#!/bin/bash\n# Test-only borrowed timeout, keeping tools in the registered outer Bash group.\nset -euo pipefail\nexec /usr/bin/timeout --foreground "$@"\n':
            raise ValueError('shim-pin')
        fresh(work / 'timeout', payload, 0o700)
        marker = work / 'tool.pid'
        script = work / 'call.sh'
        # Capture actual tool PID before redirection/subcommands. Rename follows
        # successful printf and its FD close, so final-name existence is atomic publication.
        producer = ('tool_pid=$BASHPID; set -o noclobber; umask 077; '
                    'printf "%s\\n" "$tool_pid" > "$1.tmp"; '
                    '/usr/bin/mv -T -- "$1.tmp" "$1"; exec /usr/bin/sleep 60')
        import shlex
        text = ('set -euo pipefail\nexport PATH=' + str(work) + ':/usr/sbin:/usr/bin:/sbin:/bin\n'
                'phase=work; fixture_start=' + str(started // 1000) + '; hard_end=' + str((end + 10000) // 1000) + '; work_end=' + str(end // 1000) + '\n'
                '. ' + str(root / 'code/candidate-functions.sh') + '\n'
                'try_bounded /bin/bash --noprofile --norc -c ' + shlex.quote(producer) + ' fixed ' + str(marker) + '\n').encode()
        fresh(script, text)
        for extension in ('stdout', 'stderr'):
            logs.append(os.open(work / extension, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600))
        proc = subprocess.Popen(['/bin/bash', '--noprofile', '--norc', str(script)],
                                stdin=subprocess.DEVNULL, stdout=logs[0], stderr=logs[1],
                                start_new_session=False, umask=0o077, preexec_fn=limit,
                                env={'PATH': '/usr/sbin:/usr/bin:/sbin:/bin', 'LC_ALL': 'C', 'HOME': '/nonexistent'})
        # Same outer group. Parent retains returned child/kernel reference before checks.
        retain(proc.pid)
        if os.getpgid(proc.pid) != outer_group:
            raise ValueError('inner-group')
        check()
        ready_end = min(end, started + 2000)
        while not marker.exists():
            check()
            if now() >= ready_end:
                raise ValueError('tool-start-missing')
        before = marker.lstat()
        if (not stat.S_ISREG(before.st_mode) or before.st_uid != 0 or before.st_gid != 0
                or before.st_nlink != 1 or not 2 <= before.st_size <= 32):
            raise ValueError('tool-marker')
        fd = os.open(marker, os.O_RDONLY | os.O_NOFOLLOW)
        try:
            if identity(os.fstat(fd)) != identity(before):
                raise ValueError('marker-open-identity')
            raw = os.read(fd, 33)
            if identity(os.fstat(fd)) != identity(before) or identity(marker.lstat()) != identity(before):
                raise ValueError('marker-read-identity')
        finally:
            os.close(fd)
        if re.fullmatch(rb'[1-9][0-9]*\n', raw) is None:
            raise ValueError('tool-pid')
        tool = int(raw)
        collect()
        if tool not in references or os.getpgid(tool) != outer_group:
            raise ValueError('borrowed-tool-group')
        record['outer_group_inherited'] = True
        record['borrowed_tool_in_registered_group'] = True
        record['atomic_complete_pid_marker'] = True
    except BaseException:
        fail('ownership-setup')
    finally:
        if proc is not None:
            # On setup failure collect actual descendants again; no group signal
            # can kill this controller. The outer timeout still covers every child.
            try:
                collect()
            except BaseException:
                fail('subtree-retention')
            # Kill leader first, then retained descendants, using only real pidfds.
            ordered = ([proc.pid] if proc.pid in references else []) + [p for p in references if p != proc.pid]
            for pid in ordered:
                try:
                    signal.pidfd_send_signal(references[pid], signal.SIGKILL, None, 0)
                except ProcessLookupError:
                    pass
                except BaseException:
                    fail('retained-child-kill')
            try:
                while True:
                    check()
                    try:
                        pid, status = os.waitpid(-1, os.WNOHANG)
                    except ChildProcessError:
                        break
                    if pid > 0:
                        reaped.append((pid, status))
                        if len(reaped) > 16:
                            raise ValueError('reaped-bound')
                proc.returncode = next((os.waitstatus_to_exitcode(s) for p, s in reaped if p == proc.pid), None)
                record['borrowed_subtree_absent'] = children(controller) == []
                if not record['borrowed_subtree_absent']:
                    fail('subtree-survived')
            except BaseException:
                fail('subtree-reap')
            for pid, fd in references.items():
                try:
                    poll = select.poll(); poll.register(fd, select.POLLIN)
                    if not poll.poll(0):
                        fail('retained-child-live')
                except BaseException:
                    fail('retained-child-inspection')
            if proc.returncode != -signal.SIGKILL or tool not in [p for p, s in reaped]:
                fail('actual-reaped-members')
        else:
            fail('leader-missing')
        reference_closed = []
        for fd in references.values():
            try:
                os.close(fd); reference_closed.append(True)
            except BaseException:
                reference_closed.append(False); fail('pidfd-close')
        record['retained_references_closed'] = all(reference_closed)
        closed = []
        for fd in logs:
            try:
                os.close(fd); closed.append(True)
            except BaseException:
                closed.append(False); fail('log-close')
        record['log_files_closed'] = len(closed) == 2 and all(closed)
        if not record['log_files_closed']:
            fail('log-close')
        try:
            check()
        except BaseException:
            fail('final-clock')
    record.update(kind='timeout_descendant_ownership_control', clear=failure is None, failure_category=failure,
                  forced_hang_control=True, alias_qualification_credit=False, native_authority=False,
                  original_work_end_boottime_ms=original, unchanged_work_end_boottime_ms=end,
                  leader_reaped=proc is not None and any(p == proc.pid for p, s in reaped),
                  borrowed_tool_reaped=tool is not None and any(p == tool for p, s in reaped),
                  reaped_count=len(reaped), elapsed_ms=now() - started, pipes_created=0, pipes_closed=True,
                  controller_not_signalled=True, inner_new_session=False)
    try:
        check()
        fresh(root / 'output/ownership-control.json', (json.dumps(record, indent=2) + '\n').encode())
        check()
        print('TIMEOUT_OWNERSHIP_CONTROL_EXIT=' + str(0 if failure is None else 1), flush=True)
        check()
    except BaseException:
        return 1
    return 0 if failure is None else 1


if __name__ == '__main__':
    raise SystemExit(main())
