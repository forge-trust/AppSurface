"""Root-owned Ubuntu data test only; neither fixture Main nor Evidence authority."""
import argparse
import hashlib
import json
import os
import pathlib
import re
import resource
import shlex
import signal
import stat
import subprocess
import time

CANDIDATE = 'd2044c87573b87c652c8eacd30f307d0695c4c3c39372396b9dae0f363ea35c7'
DONOR = '1ac7d1bd6c308264613ca8c1bef352f65708b50a7f6812c1800e576605cd52a8'
CASE_CATEGORIES = {
    'positive': None, 'two-links': None, 'no-alias': None,
    'target-change': 'alias-lstat-pin', 'link-substitution': 'alias-lstat-pin',
    'missing-link': 'alias-node', 'unexpected-link': 'alias-unreviewed-link',
    'writable-parent': 'alias-writable-node', 'byte-change': 'file-pin-changed',
    'growth': 'file-pin-changed', 'hash-boundary-growth': 'file-pin-changed',
    'link-boundary-substitution': 'alias-lstat-pin',
}
LOG_LIMIT = 1048576
RECEIPT_LIMIT = 262144

class Reject(ValueError):
    """Only literal closed categories are captured; raw exceptions are discarded."""

def require(value, category):
    if not value:
        raise Reject(category)

def sha(data):
    return hashlib.sha256(data).hexdigest()

def closed_json(data):
    def pairs(items):
        result = {}
        for key, value in items:
            require(key not in result, 'duplicate-json')
            result[key] = value
        return result
    return json.loads(data, object_pairs_hook=pairs)

def now_ms():
    return time.clock_gettime_ns(time.CLOCK_BOOTTIME) // 1000000

def identity(s):
    return (s.st_dev, s.st_ino, s.st_mode, s.st_uid, s.st_gid, s.st_nlink, s.st_size, s.st_mtime_ns, s.st_ctime_ns)

def root_path(path):
    require(path.is_absolute() and '..' not in path.parts, 'root-path')
    current = pathlib.Path('/')
    for part in path.parts:
        if part == '/':
            continue
        current = current / part
        info = current.lstat()
        require(stat.S_ISDIR(info.st_mode) and info.st_uid == 0 and info.st_gid == 0 and not info.st_mode & 0o022, 'root-ancestor')
    return identity(path.lstat())

def read_file(path, limit, writable=False):
    root_path(path.parent)
    before = path.lstat()
    require(stat.S_ISREG(before.st_mode) and before.st_uid == 0 and before.st_gid == 0 and before.st_nlink == 1 and before.st_size <= limit, 'regular-root-file')
    require(not before.st_mode & (0o022 if writable else 0o222), 'file-write-mode')
    fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW)
    try:
        require(identity(os.fstat(fd)) == identity(before), 'file-open-identity')
        chunks = []; total = 0
        while True:
            chunk = os.read(fd, min(65536, limit + 1 - total))
            if not chunk:
                break
            chunks.append(chunk); total += len(chunk)
            require(total <= limit, 'file-byte-bound')
        require(identity(os.fstat(fd)) == identity(before) and identity(path.lstat()) == identity(before), 'file-read-identity')
        return b''.join(chunks)
    finally:
        os.close(fd)

def child_limits():
    resource.setrlimit(resource.RLIMIT_FSIZE, (LOG_LIMIT, LOG_LIMIT))
    resource.setrlimit(resource.RLIMIT_CORE, (0, 0))

class Run:
    def __init__(self, root, hard_ms):
        self.root = root
        self.hard = hard_ms
        self.work = hard_ms - 10000
        self.started = now_ms()
        self.failure = None
        self.errors = []
        self.commands = []
        self.rows = []
        self.processes = []
        self.out = root / 'output'
        self.data = root / 'data'
        self.data_identity = None
        self.cleanup_complete = False
        self.late_marker = self.out / 'qualification-late-failure.json'

    def fault(self, category):
        if self.failure is None:
            self.failure = category
        if len(self.errors) < 32:
            self.errors.append(category)

    def check(self, cleanup=False):
        require(now_ms() < (self.hard if cleanup else self.work), 'original-deadline')

    def attempt(self, category, callback):
        try:
            return callback()
        except BaseException:
            self.fault(category)
            return None

    def create(self, path, data, mode=0o600):
        self.check()
        fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, mode)
        error = None
        try:
            view = memoryview(data)
            while view:
                self.check(); written = os.write(fd, view)
                require(written > 0, 'file-write'); view = view[written:]
        except BaseException as exc:
            error = exc
        finally:
            try:
                os.close(fd)
            except BaseException:
                self.fault('create-close')
                if error is None:
                    error = Reject('create-close')
        if error is not None:
            raise error
        self.check()

    def group_absent(self, proc):
        try:
            os.killpg(proc.pid, 0)
            return False
        except ProcessLookupError:
            return True

    def settle(self, proc, record):
        # Every independent operation is attempted after any earlier failure.
        absent = self.attempt('group-inspect', lambda: self.group_absent(proc))
        if absent is not True:
            record['forced'] = True
            def kill():
                try:
                    os.killpg(proc.pid, signal.SIGKILL)
                except ProcessLookupError:
                    pass
            self.attempt('group-kill', kill)
        if not record['waited']:
            def reap():
                self.check(True)
                code = proc.wait(timeout=max(0.001, (self.hard - now_ms()) / 1000))
                record['exit'] = code; record['waited'] = True
            self.attempt('child-reap', reap)
        record['group_absent'] = self.attempt('group-final-inspect', lambda: self.group_absent(proc)) is True
        # No Popen PIPE objects are used: two retained log FDs and DEVNULL only.
        for stream_name in ('stdin', 'stdout', 'stderr'):
            stream = getattr(proc, stream_name, None)
            if stream is not None:
                self.attempt('pipe-close-' + stream_name, stream.close)
        if not record['waited'] or not record['group_absent'] or record['forced']:
            self.fault('command-settlement')

    def command(self, argv, label):
        self.check()
        record = {'label': label, 'argv': argv, 'exit': None, 'waited': False,
                  'group_absent': False, 'forced': False, 'timeout': False,
                  'log_files_closed': False, 'pipes_created': 0, 'pipes_closed': True,
                  'logs': {}, 'elapsed_ms': None}
        self.commands.append(record)
        started = now_ms(); handles = []; proc = None; paths = []
        try:
            for suffix in ('stdout', 'stderr'):
                path = self.out / (label + '.' + suffix)
                fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
                handles.append(fd); paths.append(path)
            proc = subprocess.Popen(argv, stdin=subprocess.DEVNULL, stdout=handles[0], stderr=handles[1],
                                    env={'PATH': '/usr/sbin:/usr/bin:/sbin:/bin', 'LC_ALL': 'C', 'HOME': '/nonexistent'},
                                    start_new_session=True, umask=0o077, preexec_fn=child_limits)
            # Register actual returned process before wait or a post-spawn deadline check.
            self.processes.append((proc, record))
            self.check()
            try:
                record['exit'] = proc.wait(timeout=max(0.001, min(10, (self.work - now_ms()) / 1000)))
                record['waited'] = True
            except subprocess.TimeoutExpired:
                record['timeout'] = True; self.fault('child-timeout')
            except BaseException:
                self.fault('child-wait')
        except BaseException as exc:
            self.fault(str(exc) if isinstance(exc, Reject) else 'child-launch')
        finally:
            if proc is not None:
                self.settle(proc, record)
            close_results = []
            for fd in handles:
                try:
                    os.close(fd); close_results.append(True)
                except BaseException:
                    self.fault('log-fd-close'); close_results.append(False)
            record['log_files_closed'] = len(close_results) == 2 and all(close_results)
            for path in paths:
                def hash_log(p=path):
                    data = read_file(p, LOG_LIMIT, writable=True)
                    record['logs'][p.name] = {'bytes': len(data), 'sha256': sha(data)}
                self.attempt('log-final-binding', hash_log)
            record['elapsed_ms'] = now_ms() - started
            self.attempt('command-final-deadline', self.check)
        require(self.failure is None, 'command-first-failure')
        require(record['exit'] is not None and record['waited'] and record['group_absent'] and record['log_files_closed'], 'command-record')
        return record['exit']

    def run_case(self, kind, case, expected_category):
        self.check()
        here = self.root / 'code'
        tree = self.data / (case + '-' + kind); tree.mkdir(mode=0o700)
        target = tree / 'payload'; self.create(target, b'owned-alias-data\n')
        link = tree / 'alias'; link.symlink_to('payload')
        literal = link; links = [link]
        if case == 'two-links':
            outer = tree / 'outer'; outer.symlink_to('alias'); literal = outer; links = [outer, link]
        # Reserve replacement while the original link exists, preventing inode reuse.
        replacement = tree / 'replacement'
        if case in ('target-change', 'link-substitution', 'link-boundary-substitution'):
            replacement.symlink_to('missing' if case == 'target-change' else 'payload')
            require(replacement.lstat().st_ino != link.lstat().st_ino, 'replacement-inode')
        def link_record(path):
            info = path.lstat()
            return {'path': str(path), 'target': os.readlink(path), 'uid': info.st_uid, 'gid': info.st_gid,
                    'mode': format(stat.S_IMODE(info.st_mode), 'o'), 'device': info.st_dev, 'inode': info.st_ino}
        digest = sha(target.read_bytes())
        requested = target if case == 'no-alias' else literal
        aliases = [] if case == 'no-alias' else [{'literal': str(literal), 'resolved_path': str(target),
                    'resolved_sha256': digest, 'links': [link_record(p) for p in links]}]
        audit = tree / 'audit.json'
        self.create(audit, (json.dumps({'files': [{'path': str(requested), 'sha256': digest}], 'aliases': aliases}) + '\n').encode())
        if case in ('target-change', 'link-substitution'):
            replacement.replace(link)
        elif case == 'missing-link':
            link.unlink()
        elif case == 'unexpected-link':
            target.unlink(); target.symlink_to('/dev/null')
        elif case == 'writable-parent':
            os.chmod(tree, 0o722)  # Outer root700 still prevents unrelated writers.
        elif case in ('byte-change', 'growth'):
            target.write_bytes(b'other-alias-data\n' if case == 'byte-change' else b'grown\n' * 100)
        shim = tree / 'shim'; shim.mkdir(mode=0o700)
        self.create(shim / 'timeout', read_file(here / 'timeout-shim.sh', 4096), 0o700)
        boundary = tree / 'boundary.trace'
        hook = ''
        if case == 'hash-boundary-growth':
            hook = '/usr/bin/printf %s grown-after-hash >> ' + shlex.quote(str(target)) + '\n'
        elif case == 'link-boundary-substitution':
            hook = '/usr/bin/mv -T -- ' + shlex.quote(str(replacement)) + ' ' + shlex.quote(str(link)) + '\n'
        if hook:
            hook += '/usr/bin/printf "HASH_BOUNDARY_MUTATION_COMPLETE\\n" > ' + shlex.quote(str(boundary)) + '\n'
        # Fixed tiny seam executes the real hash first. No fake hash/stat/readlink output.
        hash_shim = '#!/bin/bash\nset -euo pipefail\n[[ $# == 2 && $1 == -- && $2 == ' + shlex.quote(str(target)) + ' ]]\n/usr/bin/sha256sum "$@"\n' + hook
        self.create(shim / 'sha256sum', hash_shim.encode(), 0o700)
        globals_text = 'os_cache_ready=0\ndeclare -a os_cache_file_paths=() os_cache_file_hashes=() os_cache_alias_paths=() os_cache_alias_rows=()\n'
        if kind == 'candidate':
            globals_text += 'os_alias_declarations_ready=0\ndeclare -a os_alias_resolved=() os_alias_hashes=() os_alias_starts=() os_alias_counts=() os_alias_link_paths=() os_alias_link_targets=() os_alias_link_metadata=()\n'
        call = ('set -euo pipefail\nexport LC_ALL=C PATH=' + shlex.quote(str(shim) + ':/usr/sbin:/usr/bin:/sbin:/bin') + '\numask 077\n'
                'phase=work; diagnostic_stage=os-input; diagnostic_failure_reported=0\n'
                'fixture_start=' + str(self.started // 1000) + '; hard_end=' + str(self.hard // 1000) + '; work_end=' + str(self.work // 1000) + '; MAX_INPUT_FILE_BYTES=1048576\n'
                'os_audit=' + shlex.quote(str(audit)) + '\n' + globals_text +
                '. ' + shlex.quote(str(here / (kind + '-functions.sh'))) + '\nnegative_os_cache_initialize\n'
                '[[ $os_cache_ready == 1 ]]\nprintf "QUALIFICATION_CACHE_INITIALIZED\\n"\n'
                'verify_os_path ' + shlex.quote(str(requested)) + ' ' + shlex.quote(digest) + '\nremaining >/dev/null\nprintf "QUALIFICATION_CASE_OK\\n"\n')
        script = tree / 'selected-call.sh'; self.create(script, call.encode())
        label = case + '-' + kind
        code = self.command(['/bin/bash', '--noprofile', '--norc', str(script)], label)
        stdout = read_file(self.out / (label + '.stdout'), LOG_LIMIT, writable=True)
        stderr = read_file(self.out / (label + '.stderr'), LOG_LIMIT, writable=True)
        require(stdout == (b'QUALIFICATION_CACHE_INITIALIZED\nQUALIFICATION_CASE_OK\n' if expected_category is None else b'QUALIFICATION_CACHE_INITIALIZED\n'), 'cache-and-case-marker')
        require(code == (0 if expected_category is None else 1), 'case-numeric-exit')
        require(stderr == (b'' if expected_category is None else ('CHECKPOINT_REJECTED:' + expected_category + '\n').encode()), 'exact-original-category')
        if hook:
            require(read_file(boundary, 128, writable=True) == b'HASH_BOUNDARY_MUTATION_COMPLETE\n', 'boundary-reached')
        return {'kind': kind, 'exit': code, 'category': expected_category, 'cache_initialized': True,
                'boundary_reached': bool(hook), 'stdout_sha256': sha(stdout), 'stderr_sha256': sha(stderr)}

    def cleanup(self):
        # No account/unit operations; only actual registered children and fresh root700 data.
        for proc, record in self.processes:
            if not record['waited'] or not record['group_absent']:
                self.settle(proc, record)
        if self.data_identity is None:
            return
        def remove():
            self.check(True)
            require(identity(self.data.lstat())[:5] == self.data_identity[:5], 'cleanup-root-identity')
            stack = [(self.data, False)]; count = 0
            while stack:
                self.check(True); path, post = stack.pop(); count += 1
                require(count <= 1024, 'cleanup-node-bound')
                require(path == self.data or self.data in path.parents, 'cleanup-owned-path')
                info = path.lstat()
                require(info.st_uid == 0 and info.st_gid == 0, 'cleanup-owner')
                if stat.S_ISDIR(info.st_mode):
                    if post:
                        path.rmdir()
                    else:
                        names = os.listdir(path); require(len(names) <= 64, 'cleanup-directory-bound')
                        stack.append((path, True))
                        stack.extend((path / name, False) for name in names)
                else:
                    require(stat.S_ISREG(info.st_mode) or stat.S_ISLNK(info.st_mode), 'cleanup-type')
                    if stat.S_ISREG(info.st_mode):
                        require(info.st_nlink == 1, 'cleanup-single-link')
                    path.unlink()  # A symlink is never followed.
            self.cleanup_complete = True
        self.attempt('owned-data-cleanup', remove)
        self.attempt('cleanup-final-deadline', lambda: self.check(True))

    def publish(self, pins):
        self.attempt('pre-publication-deadline', lambda: self.check(True))
        clear = self.failure is None and self.cleanup_complete and len(self.rows) == 12
        result = {'schema': 'issue779-alias-linux-qualification-v3', 'kind': 'linux_nounset_qualification',
                  'clear': clear, 'candidate_sha256': CANDIDATE, 'donor_sha256': DONOR,
                  'exit': 0 if clear else 1, 'failure_category': self.failure, 'failure_history': self.errors,
                  'cases': self.rows, 'commands': self.commands, 'source_pins': pins,
                  'cleanup_complete': self.cleanup_complete, 'original_deadline_boottime_ms': self.hard,
                  'original_work_end_boottime_ms': self.work, 'finished_boottime_ms': now_ms(),
                  'elapsed_ms': now_ms() - self.started, 'native_authority': False, 'native_execution': False,
                  'physical_alias_equivalence_review': None, 'requires_wrapper_exit_zero_and_no_late_sidecar': True,
                  'timeout_ownership_control_sha256': sha(read_file(self.out / 'ownership-control.json', 4096, writable=True)) if (self.out / 'ownership-control.json').exists() else None}
        data = (json.dumps(result, indent=2) + '\n').encode(); require(len(data) <= RECEIPT_LIMIT, 'receipt-byte-bound')
        fd = None
        try:
            fd = os.open(self.out / 'qualification.json', os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
            view = memoryview(data)
            while view:
                require(now_ms() < self.hard, 'publication-deadline')
                n = os.write(fd, view); require(n > 0, 'publication-write'); view = view[n:]
        except BaseException:
            self.fault('receipt-publication')
        finally:
            if fd is not None:
                self.attempt('receipt-close', lambda: os.close(fd))
        self.attempt('post-publication-deadline', lambda: self.check(True))
        code = 0 if clear and self.failure is None else 1
        try:
            self.check(True)
            print('ALIAS_LINUX_QUALIFICATION_EXIT=' + str(code), flush=True)
            self.check(True)
        except BaseException:
            self.fault('terminal-publication'); code = 1
        if self.failure is not None:
            # Fixed closed sidecar invalidates any earlier clear receipt. No raw text.
            self.attempt('late-sidecar', lambda: self.write_late())
        return code

    def write_late(self):
        data = (json.dumps({'exit': 1, 'clear': False, 'failure_category': self.failure, 'native_authority': False}) + '\n').encode()
        fd = os.open(self.late_marker, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
        try:
            os.write(fd, data)
        finally:
            os.close(fd)

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--execute', action='store_true'); parser.add_argument('--root', required=True)
    parser.add_argument('--deadline-boottime-ms', required=True, type=int)
    args = parser.parse_args()
    root = pathlib.Path(args.root)
    require(args.execute and re.fullmatch(r'/var/lib/issue779-alias-[0-9a-f]{32}', str(root)) is not None, 'explicit-fresh-root')
    require(os.uname().sysname == 'Linux' and os.getuid() == os.geteuid() == os.getgid() == os.getegid() == 0, 'root-linux-data')
    current = now_ms(); require(10000 < args.deadline_boottime_ms - current <= 90000, 'original-deadline-input')
    os.umask(0o077); root_path(root)
    run = Run(root, args.deadline_boottime_ms); pins = {}
    run.out.mkdir(mode=0o700)
    try:
        run.check()
        selection = closed_json(read_file(root / 'code' / 'selection.json', 65536))
        require(selection['candidate_sha256'] == CANDIDATE and selection['donor_sha256'] == DONOR, 'source-pins')
        for kind in ('donor', 'candidate'):
            item = selection['selections'][kind]
            require(item['selected_file'] == kind + '-functions.sh', 'selected-basename')
            selected = read_file(root / 'code' / item['selected_file'], 65536)
            full = read_file(root / 'code' / (kind + '-full-source.data'), 131072)
            require(sha(full) == (DONOR if kind == 'donor' else CANDIDATE), 'full-source-pin')
            lines = full.splitlines(keepends=True); pieces = []
            for entry in item['functions']:
                block = b''.join(lines[entry['first_line'] - 1:entry['last_line']])
                require(sha(block) == entry['sha256'] and len(block) == entry['bytes'], 'function-span-pin'); pieces.append(block)
            require(selected == b'\n'.join(pieces) and sha(selected) == item['selected_sha256'], 'selection-pin')
            pins[kind] = {'full_sha256': sha(full), 'selected_sha256': sha(selected), 'functions': len(pieces)}
        require(run.command(['/bin/bash', '--version'], 'bash-version') == 0, 'bash-version-exit')
        version = read_file(run.out / 'bash-version.stdout', LOG_LIMIT, writable=True)
        require(re.match(rb'GNU bash, version (?:[4-9]|[1-9][0-9])\.', version) is not None, 'bash-version')
        require(run.command(['/usr/bin/python3', '-I', '-S', '-B', str(root / 'code' / 'ownership-control.py'), '--root', str(root), '--work-end-ms', str(run.work)], 'timeout-ownership-control') == 0, 'timeout-ownership-control-exit')
        ownership = closed_json(read_file(run.out / 'ownership-control.json', 4096, writable=True))
        require(ownership.get('clear') is True and ownership.get('borrowed_subtree_absent') is True and ownership.get('outer_group_inherited') is True and ownership.get('controller_not_signalled') is True and ownership.get('inner_new_session') is False and ownership.get('atomic_complete_pid_marker') is True and ownership.get('retained_references_closed') is True and ownership.get('leader_reaped') is True and ownership.get('borrowed_tool_reaped') is True and ownership.get('original_work_end_boottime_ms') == run.work and ownership.get('unchanged_work_end_boottime_ms') == run.work and ownership.get('alias_qualification_credit') is False, 'timeout-ownership-control-binding')
        run.data.mkdir(mode=0o700); run.data_identity = identity(run.data.lstat())
        for case, category in CASE_CATEGORIES.items():
            donor = run.run_case('donor', case, category)
            candidate = run.run_case('candidate', case, category)
            require(donor['exit'] == candidate['exit'] and donor['category'] == candidate['category'], 'paired-equivalence')
            run.rows.append({'case': case, 'expected_category': category, 'donor': donor, 'candidate': candidate})
    except BaseException as exc:
        run.fault(str(exc) if isinstance(exc, Reject) else 'qualification-error')
    finally:
        run.cleanup()
    return run.publish(pins)

if __name__ == '__main__':
    raise SystemExit(main())
