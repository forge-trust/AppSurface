"""One finite SDK failed-test observation, not an application/process owner or acceptance authority."""
from pathlib import Path
import argparse
import ctypes
import hashlib
import importlib.util
import json
import math
import os
import platform
import re
import stat
import struct
import time

from measurement_arguments import MODULES

NAMES = tuple(name + suffix for name in MODULES for suffix in ('.dll', '.pdb'))
SUPPORT_NAMES = tuple('ForgeTrust.AppSurface.Evidence.ExitBarrier.Support' + suffix for suffix in ('.dll', '.pdb'))
STATE_FIELDS = set(('schema stage normal_code normal_status stopped_code stopped_status start_reserved '
                    'start_joined stop_joined pending_failure physically_settled cleanup_failed '
                    'group_empty_twice pump_joined captured_pid last_main_pid last_exec_pid last_code '
                    'last_status last_active last_substate output_drained').split())
PREFIX_LIMIT = 1024 * 1024
REPORT_LIMIT = 8 * 1024 * 1024
EVENT_BATCH_BYTES = 32 * 1024
EVENT_BATCH_LIMIT = 8
EVENT_LIMIT = 2048
EVENT_HEADER = struct.Struct('=iIII')
EVENT_MASK = 0x100 | 0x8 | 0x200  # IN_CREATE, IN_CLOSE_WRITE, IN_DELETE only.
TRACKER_NAMES = tuple(re.compile(re.escape(module.encode('ascii')) +
                      rb'_[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}')
                      for module in MODULES)
WORKER_DIAGNOSTIC_LEAVES = ('stderr.prefix', 'stdout.prefix', 'state.json')
WORKER_DIAGNOSTIC_SCHEMA = 'issue779-sdk-worker-diagnostics-v1'


class PassiveTmpEvents:
    """Bounded event metadata only; matching a module-prefix/GUID is not an exact tracker binding.

    No filename or hit bytes survive parsing. Missing events never prove absence, including when
    arming was late, a watch was lost, or any budget/queue/parser limit made observation incomplete.
    The eight batches and 2048 records are lifetime bounds, not renewed by each controller poll.
    """

    def __init__(self):
        self.rows = [{'module': module, 'create': 0, 'close_write': 0, 'delete': 0} for module in MODULES]
        self.batches = self.events = self.ignored_events = 0
        self.overflow = self.parse_error = self.watch_lost = self.limit_reached = False

    def feed(self, data, watch):
        """Consume one complete kernel-read batch; malformed or foreign records close observation."""
        if self.overflow or self.parse_error or self.watch_lost or self.limit_reached:
            return
        if self.batches >= EVENT_BATCH_LIMIT:
            self.limit_reached = True
            return
        self.batches += 1
        if type(data) is not bytes or not 0 < len(data) <= EVENT_BATCH_BYTES or type(watch) is not int or watch < 0:
            self.parse_error = True
            return
        offset = 0
        while offset < len(data):
            if self.events >= EVENT_LIMIT:
                self.limit_reached = True
                return
            if len(data) - offset < EVENT_HEADER.size:
                self.parse_error = True
                return
            wd, mask, cookie, size = EVENT_HEADER.unpack_from(data, offset)
            offset += EVENT_HEADER.size
            if size > 256 or size % 4 or size > len(data) - offset:
                self.parse_error = True
                return
            name = data[offset:offset + size]
            offset += size
            self.events += 1
            if mask & 0x4000:  # IN_Q_OVERFLOW has no filename and wd == -1.
                self.overflow = True
                return
            if wd != watch or cookie != 0:
                self.parse_error = True
                return
            if mask == 0x8000 and not size:  # IN_IGNORED: the kernel removed the watch.
                self.watch_lost = True
                return
            if mask & ~(EVENT_MASK | 0x40000000) or not mask & EVENT_MASK or not size:
                self.parse_error = True
                return
            end = name.find(b'\0')
            if end <= 0 or any(name[end:]):
                self.parse_error = True
                return
            name = name[:end]
            index = next((i for i, pattern in enumerate(TRACKER_NAMES) if pattern.fullmatch(name)), None)
            if index is None or mask & 0x40000000:  # Directories are not hit-file events.
                self.ignored_events += 1
                continue
            for bit, field in ((0x100, 'create'), (0x8, 'close_write'), (0x200, 'delete')):
                if mask & bit:
                    self.rows[index][field] += 1
        if self.events == EVENT_LIMIT or self.batches == EVENT_BATCH_LIMIT:
            self.limit_reached = True


class PassiveTmpObserver:
    """SDK-negative Python fixture observation; no process control, hit recovery or acceptance authority.

    Arm once around the existing real process sampler (which checks all four UID/GID values),
    intentionally following /proc/<pid>/root and /proc/self/fd kernel links. The retained directory
    is watched, never enumerated or read for counters. Errors become closed flags only. Finish in
    the controller's finally before its original outcome is consumed; every owned FD is attempted.
    """

    def __init__(self):
        self.data = PassiveTmpEvents()
        self.temporary = self.notification = self.watch = None
        self.attempted = self.armed = self.identity_checked = self.finished = False
        self.unavailable = True
        self.read_error = self.close_error = self.deadline_reached = False
        self.end = None

    def arm_once(self, descriptor, unit, sample, end):
        """Attempt once after the real descriptor, using only its original case deadline and sampler."""
        if self.attempted or self.finished:
            return
        self.attempted = True
        self.end = end
        try:
            check_deadline(end)
            if platform.system() != 'Linux':
                return
            pid, uid, gid = (descriptor[key] for key in ('worker_pid', 'worker_uid', 'worker_gid'))
            require(type(pid) is int and 0 < pid <= 2147483647
                    and all(type(value) is int and 0 < value < 4294967295 for value in (uid, gid)), 'event-identity')
            before = sample(pid, uid, gid, unit)
            require((before['pid'], before['uid'], before['gid']) == (pid, uid, gid)
                    and before['cgroup'] == descriptor['cgroup'] and type(before['starttime']) is int
                    and before['starttime'] > 0, 'event-identity')
            self.temporary = os.open('/proc/' + str(pid) + '/root/tmp',
                                     os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC | os.O_NOFOLLOW)
            metadata = os.fstat(self.temporary)
            require(stat.S_ISDIR(metadata.st_mode) and metadata.st_uid == 0 and metadata.st_gid == 0
                    and metadata.st_ino > 0, 'event-directory')
            keys = ('pid', 'uid', 'gid', 'starttime', 'cgroup')
            after = sample(pid, uid, gid, unit)
            require(all(before[key] == after[key] for key in keys), 'event-identity')
            check_deadline(end)
            libc = ctypes.CDLL(None, use_errno=True)
            init = libc.inotify_init1
            init.argtypes, init.restype = [ctypes.c_int], ctypes.c_int
            add = libc.inotify_add_watch
            add.argtypes, add.restype = [ctypes.c_int, ctypes.c_char_p, ctypes.c_uint32], ctypes.c_int
            self.notification = init(os.O_NONBLOCK | os.O_CLOEXEC)
            require(self.notification >= 0, 'event-unavailable')
            self.watch = add(self.notification, ('/proc/self/fd/' + str(self.temporary)).encode('ascii'),
                             EVENT_MASK | 0x01000000)  # IN_ONLYDIR; follow this intentionally retained FD.
            require(self.watch >= 0, 'event-unavailable')
            final = sample(pid, uid, gid, unit)
            retained = os.fstat(self.temporary)
            require(all(before[key] == final[key] for key in keys)
                    and (metadata.st_dev, metadata.st_ino, metadata.st_mode, metadata.st_uid, metadata.st_gid)
                    == (retained.st_dev, retained.st_ino, retained.st_mode, retained.st_uid, retained.st_gid), 'event-identity')
            check_deadline(end)
            self.identity_checked = self.armed = True
            self.unavailable = False
        except Exception:
            self._close()

    def drain(self):
        """Read only the nonblocking notification FD within lifetime byte/record bounds and case end."""
        if not self.armed or self.finished or self.read_error:
            return
        try:
            while not (self.data.overflow or self.data.parse_error or self.data.watch_lost or self.data.limit_reached):
                if time.monotonic() >= self.end:
                    self.deadline_reached = True
                    return
                try:
                    batch = os.read(self.notification, EVENT_BATCH_BYTES)
                except BlockingIOError:
                    return
                if not batch:
                    self.read_error = True
                    return
                self.data.feed(batch, self.watch)
        except Exception:
            self.read_error = True

    def _close(self):
        for name in ('notification', 'temporary'):
            fd = getattr(self, name)
            setattr(self, name, None)
            if fd is not None and fd >= 0:
                try:
                    os.close(fd)
                except Exception:
                    self.close_error = True

    def finish(self):
        """Emit at most one small closed packet to the existing private collector stdout log."""
        if self.finished:
            return
        try:
            self.drain()
        finally:
            self._close()
            self.finished = True
        packet = {'schema': 'issue779-sdk-private-tmp-events-v1', 'arm_attempted': self.attempted, 'armed': self.armed,
                  'identity_checked': self.identity_checked, 'unavailable': self.unavailable,
                  'overflow': self.data.overflow, 'parse_error': self.data.parse_error,
                  'watch_lost': self.data.watch_lost, 'limit_reached': self.data.limit_reached,
                  'deadline_reached': self.deadline_reached, 'read_error': self.read_error,
                  'close_error': self.close_error, 'batches': self.data.batches,
                  'events': self.data.events, 'ignored_events': self.data.ignored_events,
                  'modules': self.data.rows, 'exact_tracker_binding': False,
                  'absence_proof': False, 'native_acceptance': False}
        try:
            print(json.dumps(packet, sort_keys=True), flush=True)
        except Exception:
            pass  # A diagnostic sink failure must not replace the original controller outcome.


def require(value, category):
    if not value:
        raise RuntimeError('sdk-failure-' + category)


def strict_json(data):
    def pairs(items):
        result, folded = {}, set()
        for key, value in items:
            require(key.casefold() not in folded, 'json-members')
            folded.add(key.casefold())
            result[key] = value
        return result
    return json.loads(data, object_pairs_hook=pairs)


def check_deadline(end):
    require(type(end) in (float, int) and math.isfinite(end) and time.monotonic() < end, 'deadline')


def identity(metadata):
    return (metadata.st_dev, metadata.st_ino, metadata.st_mode, metadata.st_uid, metadata.st_gid,
            metadata.st_nlink, metadata.st_size, metadata.st_mtime_ns, metadata.st_ctime_ns)


def read_leaf(root, name, limit, uid, gid, end, *, permissions=0o600):
    check_deadline(end)
    fd = os.open(name, os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC | os.O_NONBLOCK, dir_fd=root)
    try:
        before = os.fstat(fd)
        require(stat.S_ISREG(before.st_mode) and before.st_uid == uid and before.st_gid == gid
                and before.st_nlink == 1 and stat.S_IMODE(before.st_mode) == permissions
                and 0 <= before.st_size <= limit, 'diagnostic-file')
        body = bytearray()
        while len(body) <= before.st_size:
            check_deadline(end)
            part = os.read(fd, min(65536, before.st_size + 1 - len(body)))
            if not part:
                break
            body.extend(part)
        require(len(body) == before.st_size and identity(before) == identity(os.fstat(fd)), 'diagnostic-stability')
        require(identity(before) == identity(os.stat(name, dir_fd=root, follow_symlinks=False)), 'diagnostic-name')
        check_deadline(end)
        return bytes(body)
    finally:
        os.close(fd)


def read_failure(generation, expected_pid, end, *, parent=Path('/run'), uid=0, gid=0):
    """Read fixed diagnostic data; portable owner/parent parameters issue no process or admission capability."""
    require(type(generation) is str and re.fullmatch('[0-9a-f]{32}', generation), 'generation')
    require(type(expected_pid) is int and 0 < expected_pid <= 2147483647, 'producer-pid')
    name = 'appsurface-evidence-producer-diagnostics-' + generation
    parent_fd = os.open(parent, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
    try:
        metadata = os.fstat(parent_fd)
        require(metadata.st_uid == uid and metadata.st_gid == gid and not metadata.st_mode & 0o022, 'diagnostic-parent')
        root = os.open(name, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=parent_fd)
        try:
            before = os.fstat(root)
            require(before.st_uid == uid and before.st_gid == gid and stat.S_IMODE(before.st_mode) == 0o700, 'diagnostic-root')
            require(set(os.listdir(root)) == {'state.json', 'stdout.prefix', 'stderr.prefix'}, 'diagnostic-names')
            state_bytes = read_leaf(root, 'state.json', 4096, uid, gid, end)
            stdout = read_leaf(root, 'stdout.prefix', PREFIX_LIMIT, uid, gid, end)
            stderr = read_leaf(root, 'stderr.prefix', PREFIX_LIMIT, uid, gid, end)
            state = strict_json(state_bytes)
            require(set(state) == STATE_FIELDS and state['schema'] == 'evidence-producer-failure-state-v1'
                    and state['stage'] == 'settlement', 'producer-state-shape')
            require(type(state['normal_code']) is int and state['normal_code'] == 1
                    and type(state['normal_status']) is int and state['normal_status'] == 1
                    and type(state['captured_pid']) is int and state['captured_pid'] == expected_pid, 'producer-normal-exit')
            for field in ('start_reserved', 'start_joined', 'stop_joined', 'physically_settled',
                          'group_empty_twice', 'pump_joined', 'output_drained'):
                require(state[field] is True, 'producer-joined')
            require(state['cleanup_failed'] is False and state['pending_failure'] == 'None', 'producer-cleanup')
            summaries = re.findall(rb'Failed!\s*-\s*Failed:\s*(\d+),\s*Passed:\s*(\d+),\s*Skipped:\s*(\d+),\s*Total:\s*(\d+),', stdout + b'\n' + stderr)
            require(len(summaries) == 1 and tuple(map(int, summaries[0])) == (1, 5, 0, 6), 'actual-test-summary')
            # The unchanged --verbosity quiet command emits the failed row, not Assert.Fail's message.
            failed_row = (rb'NativePreparation\.Subject\.Tests\.SubjectTests\.Calculator_branches_are_exercised'
                          rb'\(left:\s*1,\s*right:\s*2,\s*operation:\s*0,\s*expected:\s*3\)\s*\[FAIL\]')
            require(len(re.findall(failed_row, stderr)) == 1, 'actual-test-failure')
            require(identity(before) == identity(os.fstat(root))
                    and identity(before) == identity(os.stat(name, dir_fd=parent_fd, follow_symlinks=False)), 'diagnostic-root-stability')
            check_deadline(end)
            return {'normal_code': 1, 'normal_status': 1, 'captured_pid': expected_pid,
                    'physically_settled': True, 'group_empty_twice': True, 'pump_joined': True,
                    'output_drained': True, 'cleanup_failed': False, 'pending_failure': 'None',
                    'tests': {'failed': 1, 'passed': 5, 'skipped': 0, 'total': 6},
                    'files': {name: {'bytes': len(data), 'sha256': hashlib.sha256(data).hexdigest()}
                              for name, data in [('state.json', state_bytes), ('stdout.prefix', stdout), ('stderr.prefix', stderr)]},
                    'native_acceptance': False}
        finally:
            os.close(root)
    finally:
        os.close(parent_fd)


def driver():
    path = Path(__file__).resolve().parents[1] / 'issue779-checkpoint3/run-native.py'
    spec = importlib.util.spec_from_file_location('sdk_failure_driver', path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def read_case_marker(generation, end, *, parent=Path('/run'), uid=0, gid=0):
    """Require the real durable pre-assert marker after producer scratch was sealed; data only, never admission."""
    require(type(generation) is str and re.fullmatch('[0-9a-f]{32}', generation), 'generation')
    owned, nodes = [], []
    try:
        check_deadline(end)
        root = os.open(parent, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
        owned.append(root)
        metadata = os.fstat(root)
        require(metadata.st_uid == uid and metadata.st_gid == gid and not metadata.st_mode & 0o022, 'marker-parent')
        names = ('appsurface-evidence-producer-data-' + generation, 'scratch', 'tmp')
        for index, name in enumerate(names):
            check_deadline(end)
            child = os.open(name, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=root)
            owned.append(child)
            before = os.fstat(child)
            require(before.st_uid == uid and before.st_gid == gid and before.st_ino > 0
                    and stat.S_IMODE(before.st_mode) in ((0o700, 0o711) if index == 0 else (0o700,)), 'marker-directory')
            nodes.append((root, name, child, before))
            root = child
        body = read_leaf(root, 'checkpoint2-case-entered', 64, uid, gid, end, permissions=0o600)
        require(body == b'test-failure', 'actual-test-marker')
        for parent_fd, name, child, before in reversed(nodes):
            require(identity(before) == identity(os.fstat(child))
                    and identity(before) == identity(os.stat(name, dir_fd=parent_fd, follow_symlinks=False)), 'marker-stability')
        check_deadline(end)
        return {'bytes': len(body), 'sha256': hashlib.sha256(body).hexdigest(), 'sealed_root_owned': True}
    finally:
        failed = False
        for fd in reversed(owned):
            try: os.close(fd)
            except OSError: failed = True
        if failed: raise RuntimeError('sdk-failure-marker-close')


def inspect_case(generation, descriptor, producer, stderr, observations, end):
    r = driver()
    require(producer is not None, 'actual-producer')
    packets = r.events(stderr, 'evidence-sdk-producer-failure-v1')
    require(len(packets) == 1, 'ready-guard-count')
    packet = packets[0]
    require(set(packet) == {'schema', 'producer_exit_code', 'application_unit', 'original_application_ready_guard', 'native_acceptance'}
            and type(packet['producer_exit_code']) is int and packet['producer_exit_code'] == 1
            and packet['original_application_ready_guard'] is True and packet['native_acceptance'] is False
            and packet['application_unit'] == 'appsurface-evidence-application-' + generation + '.service', 'ready-guard')
    require(any(row.get('phase') == 'ready' and row.get('dcp_checked') is True for row in observations), 'original-readiness')
    state = read_failure(generation, producer['pid'], end)
    marker = read_case_marker(generation, end)
    path = Path('/run/appsurface-evidence-' + generation + '/output/evidence/evidence-manifest.json')
    require(path.stat().st_size <= 1024 * 1024, 'manifest-bound')
    manifest = strict_json(path.read_bytes())
    require(manifest['ExecutionVerdict'] == 'Incomplete' and manifest['Metrics']['CleanupCompleted'] is True
            and manifest['ClaimKind'] == 'None' and manifest['Eligibility'] == 'None', 'failed-manifest')
    rows = manifest['ProducerResults']
    require(len(rows) == 1 and rows[0]['Outcome'] == 'Failed' and rows[0].get('Artifacts') in (None, [])
            and rows[0]['SatisfiedAssertionIds'] == [] and rows[0]['Diagnostic'] == 'The restricted coverage process exited with code 1.', 'failed-producer')
    r.c2.verify_sealed_output(path.parent)
    return {'original_application_ready_guard': packet, 'producer': state, 'test_branch_marker': marker,
            'manifest_sha256': hashlib.sha256(path.read_bytes()).hexdigest(),
            'execution_verdict': 'Incomplete', 'claim_kind': 'None', 'eligibility': 'None', 'cleanup_completed': True,
            'producer_result': {'outcome': rows[0]['Outcome'], 'diagnostic': rows[0]['Diagnostic'],
                                'artifact_count': 0, 'assertion_count': 0},
            'worker': {key: descriptor[key] for key in ('worker_pid', 'worker_uid', 'worker_gid', 'cgroup')},
            'account_binding': {**{key: descriptor[key] for key in ('worker_uid', 'worker_gid', 'subject_uid', 'subject_gid')},
                                **{key: descriptor['application'][key] for key in ('application_uid', 'application_gid', 'results_gid', 'resource_access_gid')}}}


def image_rows(tool):
    result = {}
    for name in NAMES:
        path = tool / name
        require(path.is_file() and not path.is_symlink() and 0 < path.stat().st_size <= 32 * 1024 * 1024, 'module-size')
        result[name] = hashlib.sha256(path.read_bytes()).hexdigest()
    return result


def support_rows(tool):
    """Bind the two uninstrumented support files without granting execution or admission."""
    rows = {}
    for name in SUPPORT_NAMES:
        path = tool / name
        fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC | os.O_NONBLOCK)
        try:
            before = os.fstat(fd)
            require(stat.S_ISREG(before.st_mode) and before.st_nlink == 1
                    and 0 < before.st_size <= 1024 * 1024, 'support-size')
            body = bytearray()
            while len(body) <= before.st_size:
                part = os.read(fd, min(65536, before.st_size + 1 - len(body)))
                if not part: break
                body.extend(part)
            require(len(body) == before.st_size and identity(before) == identity(os.fstat(fd))
                    == identity(path.lstat()), 'support-stability')
            rows[name] = hashlib.sha256(body).hexdigest()
        finally:
            os.close(fd)
    return rows



SUPPORT_LIBRARY = 'ForgeTrust.AppSurface.Evidence.ExitBarrier.Support/0.2.0.0'
SUPPORT_ASSEMBLY = 'ForgeTrust.AppSurface.Evidence.ExitBarrier.Support'
SUPPORT_RUNTIME_FILES = {'ForgeTrust.AppSurface.Cli.deps.json': 1024 * 1024,
                         'ForgeTrust.AppSurface.Cli.runtimeconfig.json': 64 * 1024}


def _read_dependency_leaf(root, name, maximum):
    """Read one fixed regular leaf through its retained directory; never follow or block on a FIFO."""
    fd = os.open(name, os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC | os.O_NONBLOCK, dir_fd=root)
    try:
        before = os.fstat(fd)
        require(stat.S_ISREG(before.st_mode) and before.st_nlink == 1
                and 0 < before.st_size <= maximum, 'support-runtime-file')
        body = bytearray()
        while len(body) <= before.st_size:
            part = os.read(fd, min(65536, before.st_size + 1 - len(body)))
            if not part:
                break
            body.extend(part)
        require(len(body) == before.st_size and identity(before) == identity(os.fstat(fd))
                == identity(os.stat(name, dir_fd=root, follow_symlinks=False)), 'support-runtime-stability')
        return bytes(body), identity(before)
    finally:
        os.close(fd)


def _dependency_json(body):
    """Reject duplicate exact JSON names; dependency/package/asset names retain ordinal case."""
    def pairs(items):
        result = {}
        for key, value in items:
            require(key not in result, 'support-runtime-json-members')
            result[key] = value
        return result
    def constant(_):
        raise ValueError('non-json-constant')
    try:
        value = json.loads(body, object_pairs_hook=pairs, parse_constant=constant)
    except (ValueError, UnicodeError, RecursionError):
        raise RuntimeError('sdk-failure-support-runtime-json') from None
    require(type(value) is dict, 'support-runtime-json')
    return value


def support_dependency_data(tool):
    """Validate the published net10 CLI support graph and return hashes/closed metadata only.

    The two bounded no-follow, single-link regular files must retain their named metadata
    through reading both leaves. This checks data, not assembly loading, native execution,
    ownership or acceptance. No dependency bytes, properties or assembly counters are edited.
    """
    root = None
    try:
        root = os.open(tool, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
        before = os.fstat(root)
        bodies, snapshots = {}, {}
        for name, maximum in SUPPORT_RUNTIME_FILES.items():
            bodies[name], snapshots[name] = _read_dependency_leaf(root, name, maximum)
        for name, expected in snapshots.items():
            require(identity(os.stat(name, dir_fd=root, follow_symlinks=False)) == expected,
                    'support-runtime-stability')
        after, named = os.fstat(root), os.stat(tool, follow_symlinks=False)
        keys = lambda value: (value.st_dev, value.st_ino, value.st_mode, value.st_uid, value.st_gid)
        require(keys(before) == keys(after) == keys(named), 'support-runtime-directory')
    except OSError:
        raise RuntimeError('sdk-failure-support-runtime-file') from None
    finally:
        if root is not None:
            os.close(root)
    deps = _dependency_json(bodies['ForgeTrust.AppSurface.Cli.deps.json'])
    config = _dependency_json(bodies['ForgeTrust.AppSurface.Cli.runtimeconfig.json'])
    runtime_target = deps.get('runtimeTarget')
    require(type(runtime_target) is dict and runtime_target.get('name') == '.NETCoreApp,Version=v10.0',
            'support-runtime-target')
    target_name = runtime_target['name']
    targets, libraries = deps.get('targets'), deps.get('libraries')
    require(type(targets) is dict and type(libraries) is dict and type(targets.get(target_name)) is dict,
            'support-runtime-graph')
    target = targets[target_name]
    cli = [name for name in libraries if name.startswith('ForgeTrust.AppSurface.Cli/')]
    require(len(cli) == 1 and type(libraries[cli[0]]) is dict
            and libraries[cli[0]].get('type') == 'project', 'support-runtime-cli')
    roots = [name for name in target if name.startswith('ForgeTrust.AppSurface.Cli/')]
    require(roots == cli and type(target[cli[0]]) is dict, 'support-runtime-cli')
    dependencies = target[cli[0]].get('dependencies')
    require(type(dependencies) is dict and dependencies.get(SUPPORT_ASSEMBLY) == '0.2.0.0',
            'support-runtime-cli-dependency')
    helper = libraries.get(SUPPORT_LIBRARY)
    require(type(helper) is dict and helper.get('type') == 'reference'
            and [name for name in libraries if name.startswith(SUPPORT_ASSEMBLY + '/')] == [SUPPORT_LIBRARY],
            'support-runtime-library')
    helper_target = target.get(SUPPORT_LIBRARY)
    require(type(helper_target) is dict
            and [name for name in target if name.startswith(SUPPORT_ASSEMBLY + '/')] == [SUPPORT_LIBRARY],
            'support-runtime-entry')
    runtime = helper_target.get('runtime')
    require(type(runtime) is dict and set(runtime) == {SUPPORT_ASSEMBLY + '.dll'}, 'support-runtime-entry')
    assembly = runtime[SUPPORT_ASSEMBLY + '.dll']
    require(type(assembly) is dict and assembly.get('assemblyVersion') == '0.2.0.0'
            and assembly.get('fileVersion') == '0.2.0.0', 'support-runtime-version')
    options = config.get('runtimeOptions')
    require(type(options) is dict and options.get('tfm') == 'net10.0' and 'framework' not in options
            and 'includedFrameworks' not in options, 'support-runtime-framework')
    frameworks = options.get('frameworks')
    expected = {'Microsoft.NETCore.App': '10.0.0', 'Microsoft.AspNetCore.App': '10.0.0'}
    require(type(frameworks) is list and len(frameworks) == 2
            and all(type(row) is dict and type(row.get('name')) is str
                    and row['name'] in expected and row.get('version') == expected[row['name']]
                    for row in frameworks)
            and {row['name'] for row in frameworks} == set(expected), 'support-runtime-framework')
    return {'schema': 'issue779-sdk-support-dependency-v1',
            'files': {name: {'sha256': hashlib.sha256(body).hexdigest(), 'bytes': len(body)}
                      for name, body in bodies.items()},
            'runtime_target': target_name, 'tfm': 'net10.0', 'helper_library': SUPPORT_LIBRARY,
            'assembly_version': '0.2.0.0', 'file_version': '0.2.0.0', 'native_acceptance': False}


def require_worker(worker, expected, instrumented):
    require(worker['schema'] == 'evidence-worker-measurement-v3' and type(worker['normal_exit_code']) is int
            and worker['normal_exit_code'] == 1 and worker['original_pidfd_exit'] is True
            and worker['native_acceptance'] is False
            and worker['capture_phase'] == 'pre_exit_after_original_handlers'
            and worker['cached_original_pid_live'] is True and worker['acknowledged_before_original_exit'] is True
            and re.fullmatch('[0-9a-f]{64}', worker['barrier_nonce_sha256']), 'failed-worker-measurement')
    for key, descriptor_key in [('pid', 'worker_pid'), ('uid', 'worker_uid'), ('gid', 'worker_gid')]:
        value = worker[key]
        require(type(value) is int and 0 < value < 4294967295 and value == expected[descriptor_key], 'worker-binding')
    require(type(worker['start_time']) is int and worker['start_time'] > 0 and worker['cgroup'] == expected['cgroup'], 'worker-identity')
    rows = worker['modules']
    require(len(rows) == 6 and {row['module'] for row in rows} == {name + '.dll' for name in MODULES}, 'worker-modules')
    required = {'ForgeTrust.AppSurface.' + name + '.dll' for name in ('Cli', 'Evidence.Contracts', 'Evidence.Coverage', 'Evidence.Aspire')}
    for row in rows:
        require(row['module_sha256'] == instrumented[row['module']], 'worker-image')
        if row['module'] in required or row['outcome'] == 'unchanged-worker-handoff':
            require(row['outcome'] == 'unchanged-worker-handoff'
                    and re.fullmatch('[0-9a-f]{64}', row['worker_sha256'])
                    and row['worker_sha256'] == row['cached_sha256'] == row['host_sha256'], 'worker-handoff')
        else:
            require(row['outcome'] in ('worker-file-absent', 'prior-root-file-unchanged'), 'worker-unused-module')


def require_failed_report(body, workspace, source_capture):
    # Standard collector module/class names are case sensitive. Do not apply state-field alias rules here.
    report = json.loads(body)
    require(len(report) == 6 and {Path(name).name for name in report} == {name + '.dll' for name in MODULES}, 'report-modules')
    relative = 'Evidence/ForgeTrust.AppSurface.Evidence.Coverage/EvidenceRestrictedCoverageProducer.cs'
    source = (workspace / relative).read_bytes()
    require(hashlib.sha256(source).hexdigest() == source_capture['files'][relative]['sha256'], 'producer-source')
    lines = source.decode().splitlines()
    targets = [index for index, text in enumerate(lines) if 'The restricted coverage process exited with code {process.ExitCode' in text]
    require(len(targets) == 1 and targets[0] > 0
            and 'result = CreateResult(producer, EvidenceProducerOutcome.Failed' in lines[targets[0] - 1], 'producer-source-arm')
    expected = {str(targets[0]), str(targets[0] + 1)}
    hits = [value for module in report.values() for path, types in module.items()
            if Path(path).name == 'EvidenceRestrictedCoverageProducer.cs'
            for methods in types.values() for name, method in methods.items() if 'RunAsync' in name
            for line, value in method['Lines'].items() if line in expected]
    require(any(type(value) is int and value > 0 for value in hits), 'producer-failure-arm-hit')


def closed_case_result(result):
    """Project the original failed controller outcome before any acceptance check; never echo arbitrary error text."""
    known = {'KeyError', 'TypeError', 'ValueError', 'FileNotFoundError', 'PermissionError', 'TimeoutExpired',
             'sdk-failure-actual-producer', 'sdk-failure-ready-guard-count', 'sdk-failure-ready-guard',
             'sdk-failure-original-readiness', 'sdk-failure-failed-manifest', 'sdk-failure-failed-producer',
             'sdk-failure-actual-test-summary', 'sdk-failure-actual-test-failure', 'sdk-failure-producer-state-shape',
             'sdk-failure-producer-normal-exit', 'sdk-failure-producer-joined', 'sdk-failure-producer-cleanup',
             'negative-no-root-success', 'original-all-groups-empty', 'actual-application-settlement',
             'actual-apphost-before-control', 'original-numeric-account-custody', 'sealed-output-root'}
    category = result.get('failure_category')
    code = result.get('root_exit')
    return {'schema': 'issue779-sdk-failure-controller-v1', 'passed': result.get('passed') is True,
            'root_exit': code if type(code) is int and -128 <= code <= 255 else None,
            'groups_empty': result.get('groups_empty') is True,
            'final_groups_empty': result.get('final_groups_empty') is True,
            'failure_category': category if type(category) is str and category in known else 'other',
            'native_acceptance': False}


def read_worker_copy(root, name, limit, uid, gid):
    """Read a bounded private temporary leaf within the existing external process bound."""
    fd = os.open(name, os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC | os.O_NONBLOCK, dir_fd=root)
    try:
        metadata = os.fstat(fd)
        require(stat.S_ISREG(metadata.st_mode) and metadata.st_uid == uid and metadata.st_gid == gid
                and metadata.st_nlink == 1 and stat.S_IMODE(metadata.st_mode) == 0o600
                and 0 <= metadata.st_size <= limit, 'worker-diagnostic-leaf')
        body = bytearray()
        while len(body) <= metadata.st_size:
            part = os.read(fd, min(65536, metadata.st_size + 1 - len(body)))
            if not part: break
            body.extend(part)
        require(len(body) == metadata.st_size and identity(metadata) == identity(os.fstat(fd))
                == identity(os.stat(name, dir_fd=root, follow_symlinks=False)), 'worker-diagnostic-stability')
        return bytes(body)
    finally:
        os.close(fd)


def read_worker_stage(directory, *, uid=0, gid=0, complete=False):
    """Read three opaque copied leaves, not worker outcome or process authority.

    The original C2 reader owns source validation. This second read checks only
    its private temporary copies, after the original controller returned. UID/GID
    parameters permit ordinary small-file controls without constructing owners.
    External controller/finalizer bounds remain unchanged; each leaf is <=1 MiB.
    """
    root = os.open(directory, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
    try:
        before = os.fstat(root)
        require(before.st_uid == uid and before.st_gid == gid and stat.S_IMODE(before.st_mode) == 0o700,
                'worker-diagnostic-stage')
        names = {'sdk-test-failure-worker-' + name for name in WORKER_DIAGNOSTIC_LEAVES}
        require(set(os.listdir(root)) == (names | {'complete.json'} if complete else names), 'worker-diagnostic-stage')
        result = {}
        for name in WORKER_DIAGNOSTIC_LEAVES:
            selected = 'sdk-test-failure-worker-' + name
            result[name] = read_worker_copy(root, selected, PREFIX_LIMIT, uid, gid)
        if complete:
            packet = strict_json(read_worker_copy(root, 'complete.json', 4096, uid, gid))
            expected = {'schema': WORKER_DIAGNOSTIC_SCHEMA, 'files': {
                name: {'bytes': len(body), 'sha256': hashlib.sha256(body).hexdigest()} for name, body in result.items()}}
            require(packet == expected and all(type(row['bytes']) is int for row in packet['files'].values()),
                    'worker-diagnostic-complete')
        require(identity(before) == identity(os.fstat(root)) == identity(directory.lstat()), 'worker-diagnostic-stage')
        return result
    finally:
        os.close(root)


def retain_worker_diagnostics(r, result, temporary, end, *, uid=0, gid=0):
    """Best-effort post-join copy using the exact generation returned by r.execute.

    No descriptor, guessed generation, process authority or changed result is
    supplied. Partial copies have no completion record and are never appended.
    Nothing is printed and every observer error preserves the original outcome.
    """
    try:
        generation = result.get('generation')
        if type(generation) is not str or not re.fullmatch('[0-9a-f]{32}', generation): return
        check_deadline(end)
        directory = temporary / 'worker-diagnostics'
        directory.mkdir(mode=0o700)
        if r.c2.retain_producer_prefixes(generation, directory, 'sdk-test-failure', role='worker') is not True: return
        check_deadline(end)
        leaves = read_worker_stage(directory, uid=uid, gid=gid)
        packet = {'schema': WORKER_DIAGNOSTIC_SCHEMA, 'files': {
            name: {'bytes': len(body), 'sha256': hashlib.sha256(body).hexdigest()} for name, body in leaves.items()}}
        r.c2.write(directory / 'complete.json', json.dumps(packet, sort_keys=True).encode())
    except Exception:
        pass


def append_worker_diagnostics(temporary, log, maximum, *, uid=0, gid=0):
    """Append private diagnostic bytes only; unavailable/bad data never changes finalization.

    One fixed packet and fixed section labels enter the existing combined log.
    Stderr comes first. If necessary the block is explicitly truncated at the
    original shared ceiling, with no new retained evidence file or allowance.
    """
    try:
        directory = temporary / 'worker-diagnostics'
        leaves = read_worker_stage(directory, uid=uid, gid=gid, complete=True)
        status = {'schema': WORKER_DIAGNOSTIC_SCHEMA, 'retained': True, 'absence_proof': False, 'native_acceptance': False}
        block = b'\n' + json.dumps(status, sort_keys=True).encode() + b'\n'
        for name in WORKER_DIAGNOSTIC_LEAVES:
            block += ('\nOriginal CSharp worker ' + name + ' (private):\n').encode() + leaves[name]
        fd = os.open(log, os.O_WRONLY | os.O_APPEND | os.O_NOFOLLOW | os.O_CLOEXEC | os.O_NONBLOCK)
        try:
            before = os.fstat(fd)
            require(stat.S_ISREG(before.st_mode) and before.st_nlink == 1 and before.st_size <= maximum,
                    'worker-diagnostic-log')
            remaining = maximum - before.st_size
            marker = b'\nWorker diagnostics truncated at existing combined-log bound.\n'
            if len(block) > remaining:
                if remaining < len(marker): return
                block = block[:remaining - len(marker)] + marker
            body = memoryview(block)
            while body:
                count = os.write(fd, body)
                require(count > 0, 'worker-diagnostic-write')
                body = body[count:]
        finally:
            os.close(fd)
    except Exception:
        pass


def execute(args):
    r = driver()
    end = time.monotonic() + 840
    originals = strict_json((args.evidence / 'original-supervision.json').read_bytes())
    require(originals['source'] == args.source and set(originals['sdk_files']) == set(NAMES), 'source-originals')
    require(set(originals['exit_support']) == set(SUPPORT_NAMES)
            and support_rows(args.sdk_tool) == originals['exit_support'], 'support-original')
    require(support_dependency_data(args.sdk_tool) == originals['exit_support_runtime'], 'support-runtime-original')
    instrumented = image_rows(args.sdk_tool)
    require(all(instrumented[name] != originals['sdk_files'][name] for name in NAMES if name.endswith('.dll')), 'instrumentation')
    temporary = args.evidence.parent / 'checkpoint3-sdk-failure-work'
    temporary.mkdir(mode=0o700)  # Fresh, unretained working directory; an existing candidate is never adopted.
    case_args = argparse.Namespace(**vars(args), output=temporary, subject=args.workspace / '.github/issue779-checkpoint3/subject')
    case_args.prepared = args.prepared / 'payload-preparation.json'
    case_args.digests = args.evidence / 'registration-digests.json'
    tools, runtime = r.stage(case_args, end, sdk_only=True)
    require(support_rows(tools['sdk-normal'][0]) == originals['exit_support'], 'support-staged')
    require(support_dependency_data(tools['sdk-normal'][0]) == originals['exit_support_runtime'], 'support-runtime-staged')
    observer = PassiveTmpObserver()
    case_args.sdk_tmp_observer = observer
    try:
        result = r.execute(case_args, 'sdk-test-failure', tools, runtime, end)
    finally:
        try:
            observer.finish()
        except Exception:
            pass  # Even an unexpected observer error cannot replace original execution failure.
    retain_worker_diagnostics(r, result, temporary, end)
    print(json.dumps(closed_case_result(result), sort_keys=True), flush=True)
    require(result.get('passed') is True and result.get('groups_empty') is True and result.get('final_groups_empty') is True, 'original-control')
    require(result.get('root_exit') == 1 and len(result.get('worker_measurements', [])) == 1, 'natural-failed-worker')
    require_worker(result['worker_measurements'][0], result['sdk_failure']['worker'], instrumented)
    result.update({'schema': 'issue779-sdk-failed-producer-v1', 'source': args.source,
                   'sdk_original': originals['sdk_files'], 'sdk_instrumented': instrumented,
                   'exit_support': originals['exit_support'],
                   'exit_support_runtime': originals['exit_support_runtime'], 'finalized': False})
    r.write(temporary / 'provisional.json', r.canonical(result))


def finalize(args):
    r = driver()
    temporary = args.evidence.parent / 'checkpoint3-sdk-failure-work'
    # Collector and original owner streams are combined only AFTER both writers have joined.
    # This fixed private diagnostic is not a source of native outcome or counter authority.
    log = args.evidence / 'sdk-test-failure-diagnostics.log'
    owner_log = temporary / 'sdk-test-failure-owner.stderr'
    if owner_log.is_file():
        require(not owner_log.is_symlink() and owner_log.stat().st_size <= r.MAX_LOG, 'owner-log-bound')
        data = owner_log.read_bytes()
        fd = os.open(log, os.O_WRONLY | os.O_APPEND | os.O_NOFOLLOW | os.O_CLOEXEC)
        try:
            before = os.fstat(fd)
            separator = b'\nOriginal CSharp owner stderr (private):\n'
            require(stat.S_ISREG(before.st_mode) and before.st_nlink == 1
                    and before.st_size + len(separator) + len(data) <= r.MAX_LOG, 'combined-log-bound')
            body = memoryview(separator + data)
            while body:
                count = os.write(fd, body)
                require(count > 0, 'diagnostic-write')
                body = body[count:]
        finally:
            os.close(fd)
    append_worker_diagnostics(temporary, log, r.MAX_LOG)
    require(args.collector_exit == 0, 'collector-exit')
    require(owner_log.is_file() and not owner_log.is_symlink(), 'original-owner-log')
    originals = strict_json((args.evidence / 'original-supervision.json').read_bytes())
    result = strict_json((temporary / 'provisional.json').read_bytes())
    require(result['source'] == args.source == originals['source'] and result['sdk_original'] == originals['sdk_files'], 'final-source')
    require(image_rows(args.sdk_tool) == originals['sdk_files'], 'restored-originals')
    require(set(originals['exit_support']) == set(SUPPORT_NAMES) and result['exit_support'] == originals['exit_support']
            and support_rows(args.sdk_tool) == originals['exit_support'], 'support-restored')
    require(result['exit_support_runtime'] == originals['exit_support_runtime']
            == support_dependency_data(args.sdk_tool), 'support-runtime-restored')
    report = args.evidence / 'native-sdk-failure.json'
    require(report.is_file() and not report.is_symlink() and 0 < report.stat().st_size <= REPORT_LIMIT, 'completed-report')
    capture = strict_json((args.evidence / 'source.json').read_bytes())
    require(capture['source'] == args.source, 'report-source')
    require_failed_report(report.read_bytes(), args.workspace, capture)
    result.update({'finalized': True, 'restored_originals': True,
                   'coverage_sha256': hashlib.sha256(report.read_bytes()).hexdigest(), 'coverage_bytes': report.stat().st_size,
                   'diagnostics_sha256': hashlib.sha256(log.read_bytes()).hexdigest(), 'diagnostics_bytes': log.stat().st_size})
    output = args.evidence / 'native'
    output.mkdir(mode=0o700, exist_ok=True)
    r.write(output / 'sdk-test-failure.json', r.canonical(result))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('operation', choices=('run', 'finalize'))
    parser.add_argument('--collector-exit', type=int, default=0)
    for name in ('evidence', 'tool', 'sdk-tool', 'runtime', 'prepared', 'workspace'):
        parser.add_argument('--' + name, type=Path, required=True)
    for name in ('source', 'base'):
        parser.add_argument('--' + name, required=True)
    args = parser.parse_args()
    os.umask(0o077)
    require(os.geteuid() == 0 and platform.system() == 'Linux' and platform.machine() == 'x86_64', 'native-owner-platform')
    require(re.fullmatch('[0-9a-f]{40}', args.source) and re.fullmatch('[0-9a-f]{40}', args.base), 'revisions')
    require(all(getattr(args, name).is_absolute() for name in ('evidence', 'tool', 'sdk_tool', 'runtime', 'prepared', 'workspace')), 'absolute-inputs')
    (execute if args.operation == 'run' else finalize)(args)


if __name__ == '__main__':
    main()
