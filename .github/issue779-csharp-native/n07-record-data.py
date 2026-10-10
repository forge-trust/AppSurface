"""N07 detached consistency and root-private capture. JSON grants no native authority.

The externally pinned fixture authenticates this module before Python interpretation.
Operational entry is fixed capture/sampler only; importing the module runs no operation.
"""
import base64
import hashlib
import json
import os
import re
import stat
import sys
import time

MAX_STDERR = 65536
MAX_RAW_LINE = 98304
MAX_ROOT_LINE = 6145
MAX_ROOT_CAPTURE = 114688
MAX_QUOTA = 16 * 1024 * 1024
U32 = (1 << 32) - 1
U64 = (1 << 64) - 1
DESCRIPTOR_KEYS = ('schema', 'run_id', 'worker_pid', 'broker_pid', 'worker_uid', 'worker_gid', 'subject_uid', 'subject_gid', 'unit', 'cgroup', 'job_deadline_utc', 'tool_root', 'subject_root', 'output_parent', 'output_slot', 'dotnet_path', 'test_output_root', 'policy_file', 'mode', 'socket_path', 'descriptor_path', 'entry_sha256', 'policy_sha256', 'base_revision', 'subject_revision', 'workflow_identity', 'provider', 'platform', 'proof_digest', 'output_parent_identity', 'observation_profile_ids', 'observation_producer_ids', 'paths', 'admission_seconds', 'start_seconds', 'collection_seconds', 'cleanup_seconds', 'stopping_seconds', 'diff_file', 'diff_sha256', 'solution')
TASKS = ('RanToCompletion', 'Faulted', 'Canceled')
ROOT_TERMINAL = b'ASEVD410: The protected empty Observation execution or final cleanup could not be established. Fix: use an explicit mode and supported protected worker. See start-here/evidencehost.md.\n'
CAPS = (1 << 0) | (1 << 2) | (1 << 3) | (1 << 5) | (1 << 19)

class RecordRejected(ValueError):
    """Fixed closed failure; no raw input, exception, path or output echo."""

def require(value):
    if not value:
        raise RecordRejected('N07_RECORDS_REJECTED')

def uint(value, low=0, high=U64):
    require(type(value) is int and low <= value <= high)

def obj(value, keys):
    require(type(value) is dict and set(value) == set(keys))

def pairs(rows):
    result = {}
    seen = set()
    for key, value in rows:
        require(type(key) is str and key.casefold() not in seen)
        seen.add(key.casefold())
        result[key] = value
    return result

def decode(raw, cap):
    require(type(raw) is bytes and 0 < len(raw) <= cap)
    return json.loads(raw.decode('utf-8', errors='strict'), object_pairs_hook=pairs,
                      parse_constant=lambda _: require(False))

def digest(value):
    require(type(value) is str and re.fullmatch('[0-9a-f]{64}', value) is not None)

def sha(raw):
    return hashlib.sha256(raw).hexdigest()

def no_authority(value):
    require(value['native_authority'] is False and value['native_acceptance'] is False)

def ids(value, expected, minimum=0):
    require(type(value) is list and len(value) == 4)
    for item in value:
        uint(item, minimum, U32 - 1)
        require(item == expected)

def task(value):
    obj(value, ('completed', 'status', 'completed_successfully'))
    require(value['completed'] is True and value['status'] in TASKS
            and type(value['completed_successfully']) is bool
            and value['completed_successfully'] == (value['status'] == 'RanToCompletion'))

def first_frame(stderr):
    """Only exact original Allocate catch data is eligible; prior guards remain inconclusive."""
    require(type(stderr) is bytes and 0 < len(stderr) <= MAX_STDERR)
    pos = stderr.find(b'\n')
    require(0 < pos <= 1024)
    frame = stderr[:pos]
    require(all(32 <= x <= 126 for x in frame))
    value = decode(frame, 1024)
    obj(value, ('schema', 'phase', 'operation', 'error_family', 'capture_point',
                'terminal_observed', 'observation_only', 'native_authority', 'native_acceptance'))
    require(value['schema'] == 'issue779-n07-precleanup-allocation-fault-v1'
            and value['phase'] == 'Allocation' and value['operation'] == 'CheckParentIdentity'
            and value['error_family'] == 'Admission'
            and value['capture_point'] == 'AllocateCatchBeforeCallbackRethrow'
            and value['terminal_observed'] is False and value['observation_only'] is True)
    no_authority(value)
    require(b'issue779-n07-precleanup-allocation-fault-v1' not in stderr[pos + 1:])
    return frame + b'\n', value

def raw_pair(raw):
    require(type(raw) is bytes and raw.endswith(b'\n') and len(raw) <= MAX_RAW_LINE)
    value = decode(raw, MAX_RAW_LINE)
    obj(value, ('schema', 'stdout_base64', 'stderr_base64', 'stdout_bytes', 'stderr_bytes', 'native_authority'))
    require(value['schema'] == 'issue779-n07-joined-worker-raw-v1' and value['native_authority'] is False)
    uint(value['stdout_bytes'], 0, 0)
    uint(value['stderr_bytes'], 1, MAX_STDERR)
    streams = []
    for name in ('stdout', 'stderr'):
        text = value[name + '_base64']
        require(type(text) is str)
        data = base64.b64decode(text, validate=True)
        require(base64.b64encode(data).decode('ascii') == text and len(data) == value[name + '_bytes'])
        streams.append(data)
    require(streams[0] == b'')
    frame, _ = first_frame(streams[1])
    return streams[0], streams[1], frame

def parent_data(value, uid, gid):
    obj(value, ('device_major', 'device_minor', 'inode', 'uid', 'gid', 'mode', 'nlink'))
    for name in ('device_major', 'device_minor', 'uid', 'gid', 'mode', 'nlink'):
        uint(value[name], 0, U32)
    uint(value['inode'], 1)
    require(value['uid'] == uid and value['gid'] == gid and value['mode'] == 0o40700 and value['nlink'] == 2)

def check_records(root_raw, helper_raw, helper_live_raw, descriptor_raw, *, generation,
                  request_sha256, helper_unit, expected_policy_sha256, expected_source):
    """Detached DATA consistency only; authentic full image/holder/private capture must be supplied externally."""
    require(re.fullmatch('[0-9a-f]{32}', generation) is not None and int(generation, 16) != 0)
    digest(request_sha256)
    digest(expected_policy_sha256)
    require(helper_unit == 'appsurface-evidence-n07-coordinator-' + generation + '.service')
    require(type(root_raw) is bytes and len(root_raw) <= MAX_ROOT_CAPTURE and root_raw.endswith(b'\n'))
    lines = root_raw.splitlines(keepends=True)
    require(len(lines) == 4 and all(x.endswith(b'\n') and b'\r' not in x for x in lines))
    require(all(len(x) <= limit for x, limit in zip(lines, (MAX_RAW_LINE, MAX_ROOT_LINE, 1025, 1024))))
    require(lines[3] == ROOT_TERMINAL)
    stdout, stderr, frame = raw_pair(lines[0])
    root = decode(lines[1], MAX_ROOT_LINE)
    obj(root, ('schema', 'ready_descriptor_sha256', 'server', 'failure_settlement',
               'observation_only', 'native_authority', 'native_acceptance'))
    require(root['schema'] == 'issue779-n07-root-failure-observation-v1' and root['observation_only'] is True)
    no_authority(root)
    task(root['server'])
    require(root['ready_descriptor_sha256'] == sha(descriptor_raw))
    settlement = root['failure_settlement']
    obj(settlement, ('schema', 'generation', 'worker_unit', 'process', 'terminal', 'monitor', 'joins',
                     'cgroup', 'pumps', 'observation_only', 'native_authority', 'native_acceptance'))
    require(settlement['schema'] == 'issue779-n07-failure-settlement-v1'
            and settlement['generation'] == generation and settlement['observation_only'] is True)
    no_authority(settlement)
    task(settlement['monitor'])
    unit = 'appsurface-evidence-worker-' + generation + '.service'
    require(settlement['worker_unit'] == unit)
    descriptor = decode(descriptor_raw, 65536)
    obj(descriptor, DESCRIPTOR_KEYS)
    require(re.fullmatch('[0-9a-f]{40}', expected_source) is not None and descriptor['subject_revision'] == expected_source)
    require(descriptor['schema'] == 'evidence-worker-linux-v1'
            and descriptor['run_id'] == 'csharp/' + generation and descriptor['unit'] == unit
            and descriptor['cgroup'] == '/system.slice/' + unit
            and descriptor['mode'] == 'observation' and descriptor['proof_digest'] == ''
            and descriptor['policy_sha256'] == expected_policy_sha256)
    uid, gid, pid = descriptor['worker_uid'], descriptor['worker_gid'], descriptor['worker_pid']
    uint(uid, 1, U32 - 1)
    uint(gid, 1, U32 - 1)
    uint(pid, 1, (1 << 31) - 1)
    process = settlement['process']
    obj(process, ('pid', 'starttime_ticks', 'uid4', 'gid4', 'control_group'))
    uint(process['pid'], 1, (1 << 31) - 1)
    uint(process['starttime_ticks'], 1)
    require(process['pid'] == pid and process['control_group'] == descriptor['cgroup'])
    for name, expected in (('uid4', uid), ('gid4', gid)):
        require(type(process[name]) is list and len(process[name]) == 4)
        for item in process[name]:
            uint(item, 1, U32 - 1)
            require(item == expected)
    terminal = settlement['terminal']
    obj(terminal, ('exec_main_pid', 'exec_main_code', 'exec_main_status', 'active_state', 'sub_state', 'source'))
    uint(terminal['exec_main_pid'], 1, (1 << 31) - 1)
    uint(terminal['exec_main_code'], 1, 3)
    uint(terminal['exec_main_status'], 0, 255)
    require(terminal['exec_main_pid'] == pid and terminal['source'] == 'original-final-selected-unit-read')
    require(terminal['exec_main_code'] == 1 or 1 <= terminal['exec_main_status'] <= 64)
    require((terminal['active_state'], terminal['sub_state']) in (('inactive', 'dead'), ('failed', 'failed')))
    joins = settlement['joins']
    obj(joins, ('startup', 'pending_stop', 'selected_unit', 'group', 'monitor', 'pumps'))
    require(all(x is True for x in joins.values()))
    group = settlement['cgroup']
    obj(group, ('exists', 'populated', 'frozen', 'device_major', 'device_minor', 'inode'))
    require(type(group['exists']) is bool)
    if group['exists']:
        require(group['populated'] is False and group['frozen'] is False)
        uint(group['device_major'], 0, U32)
        uint(group['device_minor'], 0, U32)
        uint(group['inode'], 1)
    else:
        require(all(group[x] is None for x in ('populated', 'frozen', 'device_major', 'device_minor', 'inode')))
    pumps = settlement['pumps']
    obj(pumps, ('stdout', 'stderr', 'received_bytes', 'received_byte_limit', 'discarded_bytes', 'failure'))
    uint(pumps['received_byte_limit'], 1, MAX_QUOTA)
    uint(pumps['received_bytes'], 0, MAX_QUOTA)
    uint(pumps['discarded_bytes'], 0, 0)
    require(pumps['failure'] == 'None' and pumps['received_bytes'] == len(stdout) + len(stderr)
            and pumps['received_bytes'] <= pumps['received_byte_limit'])
    for name, data in (('stdout', stdout), ('stderr', stderr)):
        stream = pumps[name]
        obj(stream, ('received_bytes', 'retained_bytes', 'discarded_bytes', 'eof', 'failure', 'sha256'))
        for field in ('received_bytes', 'retained_bytes'):
            uint(stream[field], 0, MAX_STDERR)
            require(stream[field] == len(data))
        uint(stream['discarded_bytes'], 0, 0)
        require(stream['eof'] is True and stream['failure'] == 'None' and stream['sha256'] == sha(data))
    # Exact ordinary first-failure v4 validator is attached from the reviewed donor below.
    validate_failure(decode(lines[2], 1025))
    require(helper_raw.endswith(b'\n') and helper_raw.count(b'\n') == 1)
    helper = decode(helper_raw, 4096)
    obj(helper, ('schema', 'status', 'generation', 'owner_pid', 'owner_start_ticks', 'owner_uid4', 'owner_gid4',
                 'owner_groups', 'owner_cgroup', 'worker_pid', 'worker_start_ticks', 'worker_uid4', 'worker_gid4',
                 'worker_groups', 'worker_cgroup', 'worker_cap_eff', 'worker_no_new_privileges',
                 'worker_stop_observed', 'ready_committed_notification', 'original_worker_resumed',
                 'original_parent', 'replacement_parent', 'descriptor_sha256', 'request_sha256',
                 'allocation_observed', 'native_authority', 'native_acceptance'))
    require(helper['schema'] == 'issue779-n07-root-coordinator-result-v1'
            and helper['status'] == 'parent-substitution-observed' and helper['generation'] == generation)
    no_authority(helper)
    require(helper['allocation_observed'] is False and helper['worker_stop_observed'] is True
            and helper['ready_committed_notification'] is True and helper['original_worker_resumed'] is True
            and helper['descriptor_sha256'] == sha(descriptor_raw) and helper['request_sha256'] == request_sha256)
    require(helper['worker_pid'] == pid and helper['worker_start_ticks'] == process['starttime_ticks']
            and helper['worker_uid4'] == process['uid4'] and helper['worker_gid4'] == process['gid4']
            and helper['worker_cgroup'] == process['control_group'] and helper['worker_groups'] in ([], [gid])
            and helper['worker_cap_eff'] == '0000000000000000' and helper['worker_no_new_privileges'] == '1')
    uint(helper['worker_pid'], 1, (1 << 31) - 1)
    uint(helper['worker_start_ticks'], 1)
    ids(helper['worker_uid4'], uid, 1)
    ids(helper['worker_gid4'], gid, 1)
    require(type(helper['worker_groups']) is list)
    for item in helper['worker_groups']:
        uint(item, gid, gid)
    uint(helper['owner_pid'], 1, (1 << 31) - 1)
    uint(helper['owner_start_ticks'], 1)
    require(helper['owner_pid'] == descriptor['broker_pid'] and helper['owner_pid'] != pid
            and helper['owner_uid4'] == helper['owner_gid4'] == [0, 0, 0, 0]
            and helper['owner_groups'] in ([], [0])
            and helper['owner_cgroup'] == '/system.slice/appsurface-evidence-owner-' + generation + '.service')
    ids(helper['owner_uid4'], 0)
    ids(helper['owner_gid4'], 0)
    require(type(helper['owner_groups']) is list)
    for item in helper['owner_groups']:
        uint(item, 0, 0)
    original, replacement = helper['original_parent'], helper['replacement_parent']
    parent_data(original, uid, gid)
    parent_data(replacement, uid, gid)
    require(original['inode'] != replacement['inode'] and all(original[x] == replacement[x] for x in ('device_major', 'device_minor')))
    expected = descriptor['output_parent_identity']
    obj(expected, ('device_major', 'device_minor', 'inode', 'uid', 'gid'))
    require(all(original[x] == expected[x] for x in expected))
    require(helper_live_raw.endswith(b'\n') and helper_live_raw.count(b'\n') == 1)
    live = decode(helper_live_raw, 2048)
    obj(live, ('schema', 'pid', 'starttime_ticks', 'uid4', 'gid4', 'groups', 'control_group',
               'native_executable_matches', 'managed_argv_matches', 'native_authority', 'no_new_privileges',
               'cap_inheritable', 'cap_permitted', 'cap_effective', 'cap_bounding', 'cap_ambient'))
    uint(live['pid'], 1, (1 << 31) - 1)
    uint(live['starttime_ticks'], 1)
    require(live['schema'] == 'issue779-n07-helper-live-v2' and live['uid4'] == live['gid4'] == [0] * 4
            and live['groups'] in ([], [0]) and live['control_group'] == '/system.slice/' + helper_unit
            and live['native_executable_matches'] is True and live['managed_argv_matches'] is True
            and live['native_authority'] is False and live['pid'] not in (pid, helper['owner_pid']))
    ids(live['uid4'], 0)
    ids(live['gid4'], 0)
    require(type(live['groups']) is list)
    for item in live['groups']:
        uint(item, 0, 0)
    for field, expected in (('no_new_privileges', 1), ('cap_inheritable', 0), ('cap_permitted', CAPS),
                            ('cap_effective', CAPS), ('cap_bounding', CAPS), ('cap_ambient', 0)):
        uint(live[field])
        require(live[field] == expected)
    return {'schema': 'issue779-n07-detached-consistency-v1', 'control': 'N07', 'generation': generation,
            'data_consistent': True, 'worker_exec_main_code': terminal['exec_main_code'],
            'worker_exec_main_status': terminal['exec_main_status'], 'monitor_status': settlement['monitor']['status'],
            'server_status': root['server']['status'], 'native_authority': False, 'native_acceptance': False}, lines, stdout, stderr, frame, helper, descriptor

ENUMS = {'EvidenceControlOperation': ['Ready', 'Stop', 'Wait', 'Exit', 'Run', 'Artifacts', 'Artifact', 'ApplicationStart', 'ResourceWait'], 'EvidenceNativeObservationErrorKind': ['Unknown', 'Admission', 'Accounts', 'OutputPipe', 'ControlLine', 'Cancelled', 'Timeout', 'Io', 'AccessDenied', 'Unsupported', 'Disposed', 'Argument', 'InvalidData', 'InvalidOperation'], 'EvidenceNativeObservationPhase': ['Unknown', 'CallerCancellation', 'ProtectedInput', 'JobDeadline', 'BackendConnect', 'OwnerActivation', 'Plan', 'AccountCreate', 'WorkspaceCreate', 'ListenerBind', 'WorkerCreate', 'WorkerStart', 'ServerCreate', 'ServerLifetime', 'ServerRun', 'ServerCompletion', 'WorkerExit', 'WorkerStop', 'WorkerCompletion', 'BeginTeardown', 'Custody', 'FileVerification', 'CleanupBegin', 'ServerCancel', 'ListenerClose', 'ServerJoin', 'WorkerJoin', 'CleanupCustody', 'AccountsClose', 'WorkerClose', 'CustodyClose', 'WorkspaceClose', 'OwnerFinalCheck', 'ServerLifetimeClose', 'JobClose', 'OwnerClose', 'InputClose', 'BackendClose', 'FinalDeadline', 'ResultCheck'], 'LinuxAccountPreparationStage': ['Unknown', 'NamesAbsent', 'ReserveUtility', 'UtilityCreate', 'UtilityExecute', 'IdentityRead', 'OwnershipVerify', 'CleanupCheck', 'CleanupNameCheck', 'CleanupUtility', 'FinalNamesAbsent', 'FinalOwnershipCheck'], 'LinuxAccountUtilityStage': ['Unknown', 'OwnerCheck', 'Pipes', 'BackendConnect', 'Recipe', 'Start', 'CloseWrites', 'UnitRead', 'TerminalCheck', 'ObservationDelay', 'BeginTeardown', 'Stop', 'GroupRead', 'OutputJoin', 'PipeDispose', 'BackendDispose', 'PhysicalSettlement', 'FinalOwnerCheck'], 'LinuxControlFailureStage': ['Unknown', 'PeerCheck', 'WorkerExitTask', 'RequestLifetime', 'AcceptLoop', 'HandlerJoin', 'CapacityWait', 'Accept', 'AcceptJoin', 'ControlRegistration', 'HandlerDispatch', 'RequestRead', 'RequestClassify', 'CleanupRegistration', 'Stop', 'WaitJoin', 'ReplyGate', 'ReadyAuthorization', 'ReadyClaim', 'ReadyData', 'ResponseData', 'WaitClaim', 'ExitClaim', 'ResponseWrite', 'ConnectionRelease', 'PostWriteCheck', 'ReplyCommit', 'HandlerFailureCommit', 'ReplyGateRelease', 'ControlRelease', 'CleanupRegistrationClose', 'ExitCommit', 'AcceptCancel', 'ListenerClose', 'PendingAcceptJoin', 'HandlersJoin', 'DescendantsStop', 'ControlsJoin', 'FinalCancellation', 'CleanupBound', 'OwnerCheck', 'ProtocolIncomplete', 'WorkerTerminalTaskCompleted', 'ReplyGateClose', 'ListenerState', 'ListenerCancellation', 'ListenerWorkspace', 'ListenerParent', 'ListenerSocketMetadata', 'ListenerSocketName', 'ListenerEndpoint', 'ListenerWorkerSelection', 'ListenerOwnerIdentity', 'ListenerWorkerIdentity', 'ListenerDescriptor', 'ListenerNativeAccept', 'ListenerAcceptedPeer', 'ProcessState', 'ProcessSelection', 'ProcessExpectedSample', 'ProcessExpectedPid', 'ProcessExpectedStartTime', 'ProcessExpectedLiveState', 'ProcessExpectedUid', 'ProcessExpectedGid', 'ProcessExpectedCgroup', 'ProcessInitialContinuity', 'ProcessRepeatedContinuity', 'ProcessReadContinuity', 'ProcessRetainedProcRoot', 'ProcessRetainedProcess', 'ProcessRetainedStatus', 'ProcessRetainedStat', 'ProcessRetainedCgroup', 'ProcessNamedProcRoot', 'ProcessNamedProcess', 'ProcessNamedStatus', 'ProcessNamedStat', 'ProcessNamedCgroup', 'ProcessFirstStatRead', 'ProcessFirstStatParse', 'ProcessStatusRead', 'ProcessStatusParse', 'ProcessCgroupRead', 'ProcessCgroupParse', 'ProcessLastStatRead', 'ProcessLastStatParse', 'ProcessFileSystemInspect', 'ProcessFileSystemType', 'ProcessDirectoryStat', 'ProcessDirectoryInode', 'ProcessDirectoryType', 'ProcessRetainedProcessDeviceMajor', 'ProcessRetainedProcessDeviceMinor', 'ProcessRetainedProcessInode', 'ProcessRetainedProcessUid', 'ProcessRetainedProcessGid', 'ProcessRetainedProcessMode', 'ProcessRetainedProcessMetadata', 'ListenerAdmissionDrain', 'ListenerSocketClose', 'ListenerNamedSocketClose', 'ListenerParentClose', 'ListenerNativeAcceptOperationAborted', 'ListenerNativeAcceptInterrupted', 'ListenerNativeAcceptConnectionAborted', 'ListenerNativeAcceptSocketOther', 'ListenerAcceptedClose'], 'LinuxCustodyNodeKind': ['Generation', 'Control', 'Broker', 'Output', 'RawResults', 'Slot', 'Descriptor', 'Socket', 'Plan', 'Manifest', 'Summary'], 'LinuxCustodyOperation': ['Unknown', 'Platform', 'Settlement', 'AccountOwner', 'NodeSelection', 'Open', 'RetainedStat', 'AncestorPolicy', 'OriginalPolicy', 'BaselineComparison', 'NamedOpen', 'NamedStat', 'NamedComparison', 'InventoryRead', 'InventoryDecode', 'InventoryPolicy', 'InventoryComparison', 'HashRead', 'HashEof', 'HashFinalize', 'HashComparison', 'Chown', 'Chmod', 'TerminalPolicy', 'OriginalOwnerClose', 'FileRead', 'FileVerification', 'AccountRelease', 'RootRecheck', 'Cancellation', 'HolderState', 'OwnerIdentity'], 'LinuxRunAccountFailure': ['InvalidData', 'IdentityMismatch', 'NssFailed', 'UnsupportedPlatform', 'OperationFailed', 'CleanupFailed'], 'LinuxRunAccountOperation': ['CreateUser', 'CreateResultsGroup', 'DeleteUser', 'DeleteGroup'], 'LinuxSystemdStartError': ['Other', 'AccessDenied', 'InvalidArgs', 'NoReply', 'ServiceUnknown', 'UnknownMethod', 'UnitExists', 'LoadFailed', 'NoSuchUnit'], 'SupervisionCustodyFailure': ['None', 'Cancelled', 'SettlementValidationFailed', 'PreflightFailed', 'MutationFailed', 'LocalCloseFailed', 'FinalNativeRecheckFailed']}
CODES = frozenset(('ASEVD402','ASEVD404','ASEVD407','ASEVD409','ASEVD410','ASEVD420','ASEVD421'))

require_data = require
exact_object = obj

def enum_data(value,name,nullable=False):
    require_data(value is None and nullable or type(value) is str and value in ENUMS[name])

def code_data(value):
    require_data(value is None or type(value) is str and value in CODES)

def family(value):
    enum_data(value['error_kind'],'EvidenceNativeObservationErrorKind')
    code_data(value['diagnostic_code'])

def validate_failure(value):
    """Exact v4 serializer member sets and finite values, including null projections."""
    exact_object(value,('schema','phase','error_kind','diagnostic_code','account_failure','control_failure','custody_failure'))
    require_data(value['schema']=='evidence-native-observation-failure-v4')
    enum_data(value['phase'],'EvidenceNativeObservationPhase'); family(value)
    account=value['account_failure']
    if account is not None:
        exact_object(account,('preparation_stage','utility_stage','operation','error_kind','diagnostic_code','account_code','exec_main_code','exec_main_status','dbus_category'))
        enum_data(account['preparation_stage'],'LinuxAccountPreparationStage')
        enum_data(account['utility_stage'],'LinuxAccountUtilityStage')
        enum_data(account['operation'],'LinuxRunAccountOperation',True);family(account)
        enum_data(account['account_code'],'LinuxRunAccountFailure',True)
        enum_data(account['dbus_category'],'LinuxSystemdStartError',True)
        code,status=account['exec_main_code'],account['exec_main_status']
        require_data(code is None and status is None or type(code) is int and 1<=code<=6 and type(status) is int and 0<=status<=255)
    control=value['control_failure']
    if control is not None:
        require_data(value['phase'] in ('ServerRun','ServerCompletion'))
        exact_object(control,('stage','operation','error_kind','diagnostic_code'))
        enum_data(control['stage'],'LinuxControlFailureStage')
        enum_data(control['operation'],'EvidenceControlOperation',True);family(control)
    custody=value['custody_failure']
    if custody is not None:
        require_data(value['phase'] in ('Custody','CleanupCustody','FileVerification','AccountsClose'))
        exact_object(custody,('procedure','node_kind','operation','error_kind','diagnostic_code'))
        enum_data(custody['procedure'],'SupervisionCustodyFailure')
        enum_data(custody['node_kind'],'LinuxCustodyNodeKind',True)
        enum_data(custody['operation'],'LinuxCustodyOperation');family(custody)
    return value


# PRIVATE operational helpers. Every call is inside the externally contained original root utility interval.
def fingerprint(s):
    return (s.st_dev, s.st_ino, s.st_mode, s.st_nlink, s.st_uid, s.st_gid, s.st_size, s.st_mtime_ns, s.st_ctime_ns)

def read_private(path, cap, check, uid=0, gid=0, mode=0o600):
    check()
    require(path.startswith('/') and '//' not in path and all(x not in ('', '.', '..') for x in path[1:].split('/')))
    current = ''
    for component in path[1:].split('/')[:-1]:
        current += '/' + component
        s = os.lstat(current)
        require(stat.S_ISDIR(s.st_mode))
    named = os.lstat(path)
    require(stat.S_ISREG(named.st_mode) and named.st_nlink == 1 and named.st_uid == uid and named.st_gid == gid
            and stat.S_IMODE(named.st_mode) == mode and named.st_size <= cap)
    fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK | os.O_CLOEXEC)
    try:
        before = os.fstat(fd)
        require(fingerprint(named) == fingerprint(before))
        chunks = []
        size = 0
        while size <= cap:
            check()
            block = os.read(fd, min(65536, cap + 1 - size))
            if not block:
                break
            size += len(block)
            chunks.append(block)
        require(size <= cap and size == before.st_size and fingerprint(os.fstat(fd)) == fingerprint(before)
                and fingerprint(os.lstat(path)) == fingerprint(before))
        check()
        return b''.join(chunks)
    finally:
        os.close(fd)

def write_private(private, name, data, check):
    require('/' not in name and name not in ('.', '..'))
    check()
    fd = os.open(private + '/' + name, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC, 0o600)
    try:
        s = os.fstat(fd)
        require(stat.S_ISREG(s.st_mode) and s.st_nlink == 1 and s.st_uid == s.st_gid == 0)
        offset = 0
        while offset < len(data):
            check()
            count = os.write(fd, data[offset:])
            require(count > 0)
            offset += count
        os.fsync(fd)
    finally:
        os.close(fd)
    require(read_private(private + '/' + name, len(data), check) == data)

def selected_unit(raw, unit, terminal=None):
    """Actual fixed systemctl show facts; nonzero queries are rejected by Bash before capture."""
    fields = {}
    for line in raw.decode('ascii', errors='strict').splitlines():
        key, sep, value = line.partition('=')
        require(sep == '=' and key not in fields)
        fields[key] = value
    obj(fields, ('Id', 'LoadState', 'ActiveState', 'SubState', 'Job', 'MainPID', 'ControlGroup',
                 'ExecMainPID', 'ExecMainCode', 'ExecMainStatus'))
    require(fields['Id'] == unit and fields['Job'] == '' and fields['MainPID'] == '0'
            and fields['ControlGroup'] in ('', '/system.slice/' + unit))
    require((fields['ActiveState'], fields['SubState']) in (('inactive', 'dead'), ('failed', 'failed')))
    require(fields['LoadState'] in ('loaded', 'not-found'))
    for name in ('ExecMainPID', 'ExecMainCode', 'ExecMainStatus'):
        require(re.fullmatch('[0-9]+', fields[name]) is not None)
    if terminal is not None:
        require(fields['LoadState'] == 'loaded')
        for a, b in (('ExecMainPID', 'exec_main_pid'), ('ExecMainCode', 'exec_main_code'),
                     ('ExecMainStatus', 'exec_main_status')):
            require(int(fields[a]) == terminal[b])
    return fields

def capture(private, workspace, generation, request_hash, helper_unit, policy_hash, expected_source, end):
    """Actual no-follow postjoin object checks, never deletion/adoption/account release."""
    def check():
        require(time.clock_gettime_ns(time.CLOCK_BOOTTIME) // 1000000 < end)
    check()
    p = os.lstat(private)
    require(stat.S_ISDIR(p.st_mode) and p.st_uid == p.st_gid == 0 and stat.S_IMODE(p.st_mode) == 0o700)
    require(workspace == '/run/appsurface-evidence-' + generation)
    dpath = workspace + '/worker/worker-control.json'
    ds = os.lstat(dpath)
    descriptor = read_private(dpath, 65536, check, 0, ds.st_gid, 0o440)
    root_raw = read_private(private + '/logs/n01.stderr', MAX_ROOT_CAPTURE, check)
    require(read_private(private + '/logs/n01.stdout', 0, check) == b'')
    helper_raw = read_private(private + '/logs/n07-helper.stdout', 4096, check)
    require(read_private(private + '/logs/n07-helper.stderr', 0, check) == b'')
    live = read_private(private + '/n07-helper-live.json', 2048, check)
    result, lines, stdout, stderr, frame, helper, d = check_records(root_raw, helper_raw, live, descriptor,
        generation=generation, request_sha256=request_hash, helper_unit=helper_unit, expected_policy_sha256=policy_hash, expected_source=expected_source)
    require(d['output_parent'] == workspace + '/output' and d['output_slot'] == 'evidence'
            and d['descriptor_path'] == dpath)
    settlement = decode(lines[1], MAX_ROOT_LINE)['failure_settlement']
    selected_unit(read_private(private + '/n07-worker-unit.txt', 65536, check), d['unit'], settlement['terminal'])
    selected_unit(read_private(private + '/n07-helper-unit.txt', 65536, check), helper_unit)
    snapshots = []
    handles = []
    try:
        for name, expected in (('output-n07-retained-' + generation, helper['original_parent']),
                               ('output', helper['replacement_parent'])):
            check()
            path = workspace + '/' + name
            named = os.lstat(path)
            fd = os.open(path, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
            handles.append(fd)
            held = os.fstat(fd)
            require(fingerprint(named) == fingerprint(held) and stat.S_ISDIR(held.st_mode))
            actual = dict(device_major=os.major(held.st_dev), device_minor=os.minor(held.st_dev), inode=held.st_ino,
                          uid=held.st_uid, gid=held.st_gid, mode=held.st_mode, nlink=held.st_nlink)
            require(actual == expected and os.listdir(fd) == [])
            snapshots.append((path, fd, fingerprint(held)))
        for path, fd, expected in snapshots:
            check()
            require(fingerprint(os.fstat(fd)) == fingerprint(os.lstat(path)) == expected and os.listdir(fd) == [])
        raw_path = workspace + '/raw-results'
        rawfd = os.open(raw_path, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
        handles.append(rawfd)
        before = os.fstat(rawfd)
        require(before.st_uid == d['subject_uid'] and 0 < before.st_gid < U32
                and stat.S_IMODE(before.st_mode) == 0o710 and os.listdir(rawfd) == [])
        require(fingerprint(os.lstat(raw_path)) == fingerprint(os.fstat(rawfd)) == fingerprint(before))
        ids = (d['worker_uid'], d['worker_gid'], d['subject_uid'], d['subject_gid'], before.st_gid)
        for value in ids:
            uint(value, 1, U32 - 1)
        for name, data in (('n07-worker-raw.json', lines[0]), ('n07-root-failure.json', lines[1]),
                           ('n07-ordinary-failure.json', lines[2]), ('n07-root-terminal.txt', lines[3]),
                           ('n07-worker.stdout', stdout), ('n07-worker.stderr', stderr),
                           ('n07-precleanup-frame.json', frame), ('n07-descriptor.json', descriptor),
                           ('n07-helper-result.json', helper_raw),
                           ('n07-parent-postjoin.json', json.dumps({'original_parent': helper['original_parent'],
                            'replacement_parent': helper['replacement_parent'], 'both_empty': True,
                            'native_authority': False}, separators=(',', ':')).encode() + b'\n'),
                           ('n07-account-ids.tsv', ('\t'.join(str(x) for x in ids) + '\n').encode('ascii')),
                           ('n07-consistency.json', json.dumps(result, separators=(',', ':')).encode() + b'\n')):
            write_private(private, name, data, check)
        # Bracket retained objects and authentic source captures around publication too.
        for path, fd, expected in snapshots:
            check()
            require(fingerprint(os.fstat(fd)) == fingerprint(os.lstat(path)) == expected and os.listdir(fd) == [])
        require(read_private(dpath, 65536, check, 0, ds.st_gid, 0o440) == descriptor
                and read_private(private + '/logs/n01.stderr', MAX_ROOT_CAPTURE, check) == root_raw)
        check()
    finally:
        failed = False
        for fd in handles:
            try:
                os.close(fd)
            except OSError:
                failed = True
        require(not failed)

def main():
    try:
        require(len(sys.argv) == 10 and sys.argv[1] == 'capture')
        capture(*sys.argv[2:9], int(sys.argv[9]))
        return 0
    except Exception:
        sys.stderr.write('N07_RECORDS_REJECTED\n')
        return 1

if __name__ == '__main__':
    sys.exit(main())
