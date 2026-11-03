"""Fixed private cancellation capture. Data decoding issues no runtime authority.

The fixture supplies only its independently generated root-private paths and
reviewed source/image hashes. Production execution and signaling stay in C#.
The OS reader opens generated directories with no-follow retained descriptors;
it observes quarantine and never deletes paths, changes ownership or frees IDs.
"""
import base64
import hashlib
import json
import os
import re
import stat
import time
import pwd
import grp
from check_cancellation_records import check_joined_records

MAX_ROOT_BYTES = 112 * 1024
STREAM_LINE_LIMIT = 96 * 1024
ROOT_TERMINAL = (b"ASEVD410: The protected empty Observation execution or final cleanup could not be established. "
                 b"Fix: use an explicit mode and supported protected worker. See start-here/evidencehost.md.\n")
DESCRIPTOR_KEYS = frozenset(('schema', 'run_id', 'worker_pid', 'broker_pid', 'worker_uid', 'worker_gid',
 'subject_uid', 'subject_gid', 'unit', 'cgroup', 'job_deadline_utc', 'tool_root', 'subject_root',
 'output_parent', 'output_slot', 'dotnet_path', 'test_output_root', 'policy_file', 'mode',
 'socket_path', 'descriptor_path', 'entry_sha256', 'policy_sha256', 'base_revision',
 'subject_revision', 'workflow_identity', 'provider', 'platform', 'proof_digest',
 'output_parent_identity', 'observation_profile_ids', 'observation_producer_ids', 'paths',
 'admission_seconds', 'start_seconds', 'collection_seconds', 'cleanup_seconds', 'stopping_seconds',
 'diff_file', 'diff_sha256', 'solution'))


class Rejected(ValueError):
    """Closed error; no untrusted value or inner exception is retained."""


def require(ok):
    if not ok:
        raise Rejected('cancellation-capture-rejected')


def pairs(items):
    r = {}; seen = set()
    for k, v in items:
        require(type(k) is str and k.casefold() not in seen)
        seen.add(k.casefold()); r[k] = v
    return r


def decode(raw, limit):
    require(type(raw) is bytes and 0 < len(raw) <= limit)
    try:
        return json.loads(raw.decode('utf-8'), object_pairs_hook=pairs,
                          parse_constant=lambda _: require(False))
    except (ValueError, TypeError, UnicodeError, RecursionError):
        raise Rejected('cancellation-capture-rejected') from None


def digest(raw):
    return hashlib.sha256(raw).hexdigest()


def hash_spelling(value):
    require(type(value) is str and re.fullmatch('[0-9a-f]{64}', value) is not None)


def uint(value, lo=0, hi=(1 << 32) - 2):
    require(type(value) is int and lo <= value <= hi)
    return value


def stream(value):
    require(type(value) is dict and set(value) == {'received_bytes', 'retained_bytes', 'discarded_bytes',
                                                'eof', 'failure', 'sha256', 'base64'})
    uint(value['received_bytes'], 0, 65536); uint(value['retained_bytes'], 0, 65536)
    require(type(value['discarded_bytes']) is int and value['discarded_bytes'] == 0
            and value['eof'] is True and value['failure'] == 'None'
            and type(value['base64']) is str and len(value['base64']) <= 87384)
    hash_spelling(value['sha256'])
    try:
        raw = base64.b64decode(value['base64'].encode('ascii'), validate=True)
    except (ValueError, UnicodeError):
        raise Rejected('cancellation-capture-rejected') from None
    require(base64.b64encode(raw).decode('ascii') == value['base64']
            and len(raw) == value['received_bytes'] == value['retained_bytes']
            and digest(raw) == value['sha256'])
    return raw


def _parse_original(raw, descriptor_raw, *, case, generation, source, base, entry_sha, policy_sha):
    """Validate full original projections; detached input alone proves no ownership."""
    require(case in ('N08', 'N09') and type(generation) is str
            and re.fullmatch('[0-9a-f]{32}', generation) and generation != '0' * 32)
    require(type(raw) is bytes and 0 < len(raw) <= MAX_ROOT_BYTES)
    lines = raw.splitlines(keepends=True)
    require(len(lines) == 5 and b''.join(lines) == raw and all(x.endswith(b'\n') for x in lines)
            and len(lines[0]) <= 1024 and len(lines[1]) <= 4097
            and len(lines[2]) <= STREAM_LINE_LIMIT and len(lines[3]) <= 1024
            and lines[4] == ROOT_TERMINAL)
    joined = decode(lines[2], STREAM_LINE_LIMIT)
    require(type(joined) is dict and set(joined) == {'schema', 'generation', 'stdout', 'stderr',
       'received_bytes', 'received_byte_limit', 'observation_only', 'native_authority', 'native_acceptance'}
       and joined['schema'] == 'issue779-cancellation-joined-streams-v1' and joined['generation'] == generation
       and joined['observation_only'] is True and joined['native_authority'] is False
       and joined['native_acceptance'] is False)
    stdout = stream(joined['stdout']); stderr = stream(joined['stderr'])
    uint(joined['received_bytes'], 0, 65536); uint(joined['received_byte_limit'], 1, 16 * 1024 * 1024)
    require(joined['received_bytes'] == len(stdout) + len(stderr)
            and joined['received_bytes'] <= joined['received_byte_limit'])
    failure = decode(lines[3], 1024)
    require(type(failure) is dict and set(failure) == {'schema', 'phase', 'error_kind', 'diagnostic_code',
                                                    'account_failure', 'control_failure', 'custody_failure'}
            and failure['schema'] == 'evidence-native-observation-failure-v4')
    # These are data fields of the genuine failed command, never acceptance gates.
    require(failure['phase'] in ('ServerRun', 'ServerCompletion', 'WorkerExit', 'WorkerCompletion')
            and failure['error_kind'] in ('Admission', 'ControlLine', 'Cancelled', 'InvalidOperation')
            and failure['diagnostic_code'] in (None, 'ASEVD410', 'ASEVD420'))
    require(failure['account_failure'] is None and failure['custody_failure'] is None)
    cf = failure['control_failure']
    if cf is not None:
        require(type(cf) is dict and set(cf) == {'stage','operation','error_kind','diagnostic_code'}
                and cf['stage'] in ('RequestRead','RequestClassify','RequestLifetime','AcceptLoop',
                    'Accept','AcceptJoin','HandlerJoin','CapacityWait','HandlerDispatch','ControlRegistration',
                    'HandlerFailureCommit','WorkerTerminalTaskCompleted','ProtocolIncomplete')
                and cf['operation'] in (None,'Ready','Stop','Wait','Exit')
                and cf['error_kind'] in ('Unknown','Admission','ControlLine','Cancelled','InvalidOperation')
                and cf['diagnostic_code'] in (None,'ASEVD402','ASEVD410','ASEVD420'))
    d = decode(descriptor_raw, 65536)
    require(type(d) is dict and set(d) == DESCRIPTOR_KEYS)
    root = '/run/appsurface-evidence-' + generation
    unit = 'appsurface-evidence-worker-' + generation + '.service'
    require(d['schema'] == 'evidence-worker-linux-v1' and d['run_id'] == 'csharp/' + generation
            and d['unit'] == unit and d['cgroup'] == '/system.slice/' + unit
            and d['descriptor_path'] == root + '/worker/worker-control.json'
            and d['socket_path'] == root + '/worker/broker/control.sock'
            and d['output_parent'] == root + '/output' and d['output_slot'] == 'evidence'
            and d['test_output_root'] == root + '/raw-results' and d['mode'] == 'observation'
            and d['provider'] == 'github-actions' and d['platform'] == 'linux-x64'
            and d['proof_digest'] == '' and d['base_revision'] == base and d['subject_revision'] == source
            and d['entry_sha256'] == entry_sha and d['policy_sha256'] == policy_sha
            and d['observation_profile_ids'] == ['empty'] and d['observation_producer_ids'] == []
            and d['paths'] == ['docs/designs/issue-779-csharp-supervision-core.md']
            and all(d[x] is None for x in ('diff_file', 'diff_sha256', 'solution')))
    for k in ('worker_uid', 'worker_gid', 'subject_uid', 'subject_gid'):
        uint(d[k], 1)
    require(d['worker_uid'] != d['subject_uid'] and d['worker_gid'] != d['subject_gid'])
    uint(d['worker_pid'], 1, (1 << 31) - 1); uint(d['broker_pid'], 1, (1 << 31) - 1)
    require(d['worker_pid'] != d['broker_pid'])
    kernel = decode(lines[1], 4097)
    check_joined_records(case=case, signal_raw=lines[0], kernel_raw=lines[1], worker_stdout=stdout,
        worker_stderr=stderr, expected_generation=generation, expected_uid=d['worker_uid'],
        expected_gid=d['worker_gid'], expected_pid=d['worker_pid'],
        expected_starttime_ticks=kernel['process']['starttime_ticks'],
        expected_descriptor_sha256=digest(descriptor_raw))
    require(joined['received_byte_limit'] == kernel['pumps']['received_byte_limit'])
    return d, {'signal.json': lines[0], 'kernel.json': lines[1], 'joined-streams.json': lines[2],
               'root-failure.json': lines[3], 'worker.stdout': stdout, 'worker.stderr': stderr,
               'worker-control.json': descriptor_raw}


def parse_original(raw, descriptor_raw, **expected):
    """Closed data-only rejection without copied input values or chained errors."""
    try:
        return _parse_original(raw, descriptor_raw, **expected)
    except (ValueError, TypeError, KeyError, UnicodeError, OverflowError, RecursionError):
        raise Rejected('cancellation-capture-rejected') from None


def identity(s):
    return (s.st_dev, s.st_ino, s.st_mode, s.st_uid, s.st_gid, s.st_nlink, s.st_size,
            s.st_mtime_ns, s.st_ctime_ns)


class NativeCapture:
    """One root-only, no-follow, original-deadline capture; owns only local read FDs."""
    def __init__(self, end_ms):
        self.end = end_ms / 1000
        self.fds = []

    def check(self):
        require(time.clock_gettime(time.CLOCK_BOOTTIME) < self.end)

    def directory(self, parent, name, uid, gid, mode):
        self.check(); before = os.stat(name, dir_fd=parent, follow_symlinks=False)
        require(stat.S_ISDIR(before.st_mode) and before.st_uid == uid and before.st_gid == gid
                and stat.S_IMODE(before.st_mode) == mode)
        fd = os.open(name, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=parent)
        self.fds.append(fd); require(identity(os.fstat(fd)) == identity(before)); self.check()
        return fd, before

    def read(self, parent, name, uid, gid, mode, limit):
        self.check(); before = os.stat(name, dir_fd=parent, follow_symlinks=False)
        require(stat.S_ISREG(before.st_mode) and before.st_uid == uid and before.st_gid == gid
                and stat.S_IMODE(before.st_mode) == mode and before.st_nlink == 1 and before.st_size <= limit)
        fd = os.open(name, os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=parent)
        try:
            require(identity(os.fstat(fd)) == identity(before)); chunks = []; count = 0
            while True:
                self.check(); chunk = os.read(fd, min(65536, limit + 1 - count))
                if not chunk: break
                count += len(chunk); require(count <= limit); chunks.append(chunk)
            require(count == before.st_size and identity(os.fstat(fd)) == identity(before)
                    and identity(os.stat(name, dir_fd=parent, follow_symlinks=False)) == identity(before))
            self.check(); return b''.join(chunks)
        finally:
            os.close(fd)

    def close(self):
        failed = False
        for fd in reversed(self.fds):
            try: os.close(fd)
            except OSError: failed = True
        self.fds.clear(); require(not failed)

    def group_empty(self, root, unit):
        """Fresh exact generated-group inspection, with no unit query or PID selection."""
        parent = root
        for name in ('sys','fs','cgroup','system.slice'):
            self.check()
            fd = os.open(name, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC,dir_fd=parent)
            self.fds.append(fd); s = os.fstat(fd)
            require(s.st_uid == 0 and s.st_gid == 0 and not stat.S_IMODE(s.st_mode) & 0o022)
            parent = fd
        self.check()
        try:
            before = os.stat(unit,dir_fd=parent,follow_symlinks=False)
        except FileNotFoundError:
            self.check(); return {'exists':False,'populated':None,'frozen':None,'inode':None}
        require(stat.S_ISDIR(before.st_mode) and before.st_uid == 0 and before.st_gid == 0
                and not stat.S_IMODE(before.st_mode) & 0o022)
        fd = os.open(unit,os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC,dir_fd=parent)
        self.fds.append(fd); require(identity(os.fstat(fd)) == identity(before))
        ef = os.open('cgroup.events',os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC,dir_fd=fd)
        try:
            es = os.fstat(ef); require(stat.S_ISREG(es.st_mode) and es.st_uid == es.st_gid == 0)
            raw = b''
            while True:
                self.check(); part = os.read(ef,1025-len(raw))
                if not part: break
                raw += part; require(len(raw) <= 1024)
            values = {}
            for line in raw.decode('ascii').splitlines():
                k,v = line.split(); require(k not in values and k in ('populated','frozen') and v in ('0','1'))
                values[k] = v
            require(values == {'populated':'0','frozen':'0'})
            require(identity(os.fstat(ef)) == identity(es))
        finally: os.close(ef)
        require(identity(os.fstat(fd)) == identity(before)
                and identity(os.stat(unit,dir_fd=parent,follow_symlinks=False)) == identity(before))
        self.check(); return {'exists':True,'populated':False,'frozen':False,'inode':before.st_ino}


def capture(private, generation, case, source, base, entry_sha, policy_sha, end_ms):
    """Capture the actual joined private records and exact quarantined layout.

    No ID release, deletion, activation, proof, new deadline or capability occurs.
    Original worker process exit closes its FDs in the kernel; this does not claim
    that a particular managed Dispose call returned successfully.
    """
    require(os.geteuid() == 0 and re.fullmatch('[0-9a-f]{32}', generation) is not None)
    require(private == '/run/appsurface-evidence-fixture/' + generation)
    for value in (entry_sha, policy_sha): hash_spelling(value)
    require(re.fullmatch('[0-9a-f]{40}', source) and re.fullmatch('[0-9a-f]{40}', base))
    c = NativeCapture(int(end_ms)); result = None
    try:
        c.check(); root = os.open('/', os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC); c.fds.append(root)
        rs = os.fstat(root); require(rs.st_uid == rs.st_gid == 0 and not stat.S_IMODE(rs.st_mode) & 0o022)
        run = os.open('run', os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=root); c.fds.append(run)
        rs = os.fstat(run); require(rs.st_uid == rs.st_gid == 0 and not stat.S_IMODE(rs.st_mode) & 0o022)
        fix, _ = c.directory(run, 'appsurface-evidence-fixture', 0, 0, 0o700)
        p, ps = c.directory(fix, generation, 0, 0, 0o700); log, _ = c.directory(p, 'logs', 0, 0, 0o700)
        raw = c.read(log, 'n01.stderr', 0, 0, 0o600, MAX_ROOT_BYTES)
        require(c.read(log, 'n01.stdout', 0, 0, 0o600, 0) == b'')
        # Root-owned generation/descriptor selection is fixed before using any descriptor field.
        name = 'appsurface-evidence-' + generation
        ws = os.stat(name, dir_fd=run, follow_symlinks=False)
        require(stat.S_ISDIR(ws.st_mode) and ws.st_uid == 0 and ws.st_gid > 0
                and stat.S_IMODE(ws.st_mode) == 0o750)
        work, work_before = c.directory(run, name, 0, ws.st_gid, 0o750)
        control, _ = c.directory(work, 'worker', 0, ws.st_gid, 0o710)
        descriptor = c.read(control, 'worker-control.json', 0, ws.st_gid, 0o440, 65536)
        d, records = parse_original(raw, descriptor, case=case, generation=generation,
                                   source=source, base=base, entry_sha=entry_sha, policy_sha=policy_sha)
        require(d['worker_gid'] == ws.st_gid)
        out, out_before = c.directory(work, 'output', d['worker_uid'], d['worker_gid'], 0o700)
        i = d['output_parent_identity']; require(type(i) is dict and set(i) == {'device_major','device_minor','inode','uid','gid'})
        require(i == {'device_major': os.major(out_before.st_dev), 'device_minor': os.minor(out_before.st_dev),
                      'inode': out_before.st_ino, 'uid': out_before.st_uid, 'gid': out_before.st_gid})
        entries = os.listdir(out); require(entries == ([] if case == 'N08' else ['evidence']))
        slot = None
        if case == 'N09':
            sf, ss = c.directory(out, 'evidence', d['worker_uid'], d['worker_gid'], 0o700)
            require(os.listdir(sf) == []); slot = {'device': ss.st_dev, 'inode': ss.st_ino, 'mode': '0700', 'empty': True}
        rr = os.stat('raw-results', dir_fd=work, follow_symlinks=False)
        require(stat.S_ISDIR(rr.st_mode) and rr.st_uid == d['subject_uid'] and rr.st_gid > 0
                and rr.st_gid not in (d['worker_gid'], d['subject_gid']) and stat.S_IMODE(rr.st_mode) == 0o710)
        rawfd, _ = c.directory(work, 'raw-results', d['subject_uid'], rr.st_gid, 0o710)
        require(os.listdir(rawfd) == [])
        require(not os.path.lexists('/proc/' + str(d['worker_pid'])))
        require(not os.path.lexists('/proc/' + str(d['broker_pid'])))
        group = c.group_empty(root, d['unit'])
        c.check(); users = []; groups = []
        for n, uid, gid in [('evw' + generation[:28], d['worker_uid'], d['worker_gid']),
                            ('evs' + generation[:28], d['subject_uid'], d['subject_gid'])]:
            f = pwd.getpwnam(n); r = pwd.getpwuid(uid); c.check()
            require(f == r and f.pw_name == n and f.pw_uid == uid and f.pw_gid == gid)
            users.append({'name': n, 'uid': uid, 'gid': gid, 'retained': True})
        for n, gid in [('evw' + generation[:28], d['worker_gid']), ('evs' + generation[:28], d['subject_gid']),
                       ('evr' + generation[:28], rr.st_gid)]:
            f = grp.getgrnam(n); r = grp.getgrgid(gid); c.check()
            require(f == r and f.gr_name == n and f.gr_gid == gid and f.gr_mem == [])
            groups.append({'name': n, 'gid': gid, 'retained': True})
        require(identity(os.fstat(out)) == identity(out_before)
                and identity(os.stat('output', dir_fd=work, follow_symlinks=False)) == identity(out_before)
                and identity(os.fstat(work)) == identity(work_before)
                and identity(os.stat(name, dir_fd=run, follow_symlinks=False)) == identity(work_before))
        observation = {'schema':'issue779-cancellation-filesystem-nss-v1', 'generation':generation, 'case':case,
            'output_parent_descriptor_identity_match':True, 'output_slot':slot, 'manifest_present':False,
            'artifact_files_present':False, 'account_disposition':'preserved-quarantined', 'users':users, 'groups':groups,
            'original_worker_pid_absent':True, 'original_broker_pid_absent':True,
            'fresh_generated_worker_group':group,
            'worker_fd_disposition':'kernel-closed-on-original-monitored-normal-exit',
            'native_acceptance':False, 'trusted':False}
        records['filesystem-nss-observation.json'] = (json.dumps(observation,sort_keys=True,separators=(',',':'))+'\n').encode()
        for n, b in records.items():
            c.check(); require('/' not in n)
            fd = os.open(n, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC,0o600,dir_fd=p)
            try:
                os.fchmod(fd,0o600); off = 0
                while off < len(b):
                    c.check(); wrote = os.write(fd,b[off:]); require(wrote > 0); off += wrote
                os.fsync(fd); c.check()
            finally: os.close(fd)
        require(identity(os.fstat(work)) == identity(work_before)); c.check()
        result = {'schema':'issue779-cancellation-private-capture-v1','case':case,'generation':generation,
            'source':source,'entry_sha256':entry_sha,'policy_sha256':policy_sha,
            'record_files':{n:{'bytes':len(b),'sha256':digest(b)} for n,b in records.items()},
            'observation_only':True,'native_authority':False,'native_acceptance':False}
    finally:
        c.close()
    c.check(); return result
