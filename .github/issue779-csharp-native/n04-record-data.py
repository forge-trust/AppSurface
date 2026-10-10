"""Closed fixture data checks only. Never authenticate native actors or grant acceptance."""
import hashlib
import json
import re

# Replaced by the renderer from the exact frozen a358 C# enum declarations.
ENUMS = {'EvidenceControlOperation': ['Ready', 'Stop', 'Wait', 'Exit', 'Run', 'Artifacts', 'Artifact', 'ApplicationStart', 'ResourceWait'], 'EvidenceNativeObservationErrorKind': ['Unknown', 'Admission', 'Accounts', 'OutputPipe', 'ControlLine', 'Cancelled', 'Timeout', 'Io', 'AccessDenied', 'Unsupported', 'Disposed', 'Argument', 'InvalidData', 'InvalidOperation'], 'EvidenceNativeObservationPhase': ['Unknown', 'CallerCancellation', 'ProtectedInput', 'JobDeadline', 'BackendConnect', 'OwnerActivation', 'Plan', 'AccountCreate', 'WorkspaceCreate', 'ListenerBind', 'WorkerCreate', 'WorkerStart', 'ServerCreate', 'ServerLifetime', 'ServerRun', 'ServerCompletion', 'WorkerExit', 'WorkerStop', 'WorkerCompletion', 'BeginTeardown', 'Custody', 'FileVerification', 'CleanupBegin', 'ServerCancel', 'ListenerClose', 'ServerJoin', 'WorkerJoin', 'CleanupCustody', 'AccountsClose', 'WorkerClose', 'CustodyClose', 'WorkspaceClose', 'OwnerFinalCheck', 'ServerLifetimeClose', 'JobClose', 'OwnerClose', 'InputClose', 'BackendClose', 'FinalDeadline', 'ResultCheck'], 'LinuxAccountPreparationStage': ['Unknown', 'NamesAbsent', 'ReserveUtility', 'UtilityCreate', 'UtilityExecute', 'IdentityRead', 'OwnershipVerify', 'CleanupCheck', 'CleanupNameCheck', 'CleanupUtility', 'FinalNamesAbsent', 'FinalOwnershipCheck'], 'LinuxAccountUtilityStage': ['Unknown', 'OwnerCheck', 'Pipes', 'BackendConnect', 'Recipe', 'Start', 'CloseWrites', 'UnitRead', 'TerminalCheck', 'ObservationDelay', 'BeginTeardown', 'Stop', 'GroupRead', 'OutputJoin', 'PipeDispose', 'BackendDispose', 'PhysicalSettlement', 'FinalOwnerCheck'], 'LinuxControlFailureStage': ['Unknown', 'PeerCheck', 'WorkerExitTask', 'RequestLifetime', 'AcceptLoop', 'HandlerJoin', 'CapacityWait', 'Accept', 'AcceptJoin', 'ControlRegistration', 'HandlerDispatch', 'RequestRead', 'RequestClassify', 'CleanupRegistration', 'Stop', 'WaitJoin', 'ReplyGate', 'ReadyAuthorization', 'ReadyClaim', 'ReadyData', 'ResponseData', 'WaitClaim', 'ExitClaim', 'ResponseWrite', 'ConnectionRelease', 'PostWriteCheck', 'ReplyCommit', 'HandlerFailureCommit', 'ReplyGateRelease', 'ControlRelease', 'CleanupRegistrationClose', 'ExitCommit', 'AcceptCancel', 'ListenerClose', 'PendingAcceptJoin', 'HandlersJoin', 'DescendantsStop', 'ControlsJoin', 'FinalCancellation', 'CleanupBound', 'OwnerCheck', 'ProtocolIncomplete', 'WorkerTerminalTaskCompleted', 'ReplyGateClose', 'ListenerState', 'ListenerCancellation', 'ListenerWorkspace', 'ListenerParent', 'ListenerSocketMetadata', 'ListenerSocketName', 'ListenerEndpoint', 'ListenerWorkerSelection', 'ListenerOwnerIdentity', 'ListenerWorkerIdentity', 'ListenerDescriptor', 'ListenerNativeAccept', 'ListenerAcceptedPeer', 'ProcessState', 'ProcessSelection', 'ProcessExpectedSample', 'ProcessExpectedPid', 'ProcessExpectedStartTime', 'ProcessExpectedLiveState', 'ProcessExpectedUid', 'ProcessExpectedGid', 'ProcessExpectedCgroup', 'ProcessInitialContinuity', 'ProcessRepeatedContinuity', 'ProcessReadContinuity', 'ProcessRetainedProcRoot', 'ProcessRetainedProcess', 'ProcessRetainedStatus', 'ProcessRetainedStat', 'ProcessRetainedCgroup', 'ProcessNamedProcRoot', 'ProcessNamedProcess', 'ProcessNamedStatus', 'ProcessNamedStat', 'ProcessNamedCgroup', 'ProcessFirstStatRead', 'ProcessFirstStatParse', 'ProcessStatusRead', 'ProcessStatusParse', 'ProcessCgroupRead', 'ProcessCgroupParse', 'ProcessLastStatRead', 'ProcessLastStatParse', 'ProcessFileSystemInspect', 'ProcessFileSystemType', 'ProcessDirectoryStat', 'ProcessDirectoryInode', 'ProcessDirectoryType', 'ProcessRetainedProcessDeviceMajor', 'ProcessRetainedProcessDeviceMinor', 'ProcessRetainedProcessInode', 'ProcessRetainedProcessUid', 'ProcessRetainedProcessGid', 'ProcessRetainedProcessMode', 'ProcessRetainedProcessMetadata', 'ListenerAdmissionDrain', 'ListenerSocketClose', 'ListenerNamedSocketClose', 'ListenerParentClose', 'ListenerNativeAcceptOperationAborted', 'ListenerNativeAcceptInterrupted', 'ListenerNativeAcceptConnectionAborted', 'ListenerNativeAcceptSocketOther', 'ListenerAcceptedClose'], 'LinuxCustodyNodeKind': ['Generation', 'Control', 'Broker', 'Output', 'RawResults', 'Slot', 'Descriptor', 'Socket', 'Plan', 'Manifest', 'Summary'], 'LinuxCustodyOperation': ['Unknown', 'Platform', 'Settlement', 'AccountOwner', 'NodeSelection', 'Open', 'RetainedStat', 'AncestorPolicy', 'OriginalPolicy', 'BaselineComparison', 'NamedOpen', 'NamedStat', 'NamedComparison', 'InventoryRead', 'InventoryDecode', 'InventoryPolicy', 'InventoryComparison', 'HashRead', 'HashEof', 'HashFinalize', 'HashComparison', 'Chown', 'Chmod', 'TerminalPolicy', 'OriginalOwnerClose', 'FileRead', 'FileVerification', 'AccountRelease', 'RootRecheck', 'Cancellation', 'HolderState', 'OwnerIdentity'], 'LinuxRunAccountFailure': ['InvalidData', 'IdentityMismatch', 'NssFailed', 'UnsupportedPlatform', 'OperationFailed', 'CleanupFailed'], 'LinuxRunAccountOperation': ['CreateUser', 'CreateResultsGroup', 'DeleteUser', 'DeleteGroup'], 'LinuxSystemdStartError': ['Other', 'AccessDenied', 'InvalidArgs', 'NoReply', 'ServiceUnknown', 'UnknownMethod', 'UnitExists', 'LoadFailed', 'NoSuchUnit'], 'SupervisionCustodyFailure': ['None', 'Cancelled', 'SettlementValidationFailed', 'PreflightFailed', 'MutationFailed', 'LocalCloseFailed', 'FinalNativeRecheckFailed']}
CODES = frozenset(('ASEVD402','ASEVD404','ASEVD407','ASEVD409','ASEVD410','ASEVD420','ASEVD421'))
ROOT_TERMINAL = b'ASEVD410: The protected empty Observation execution or final cleanup could not be established. Fix: use an explicit mode and supported protected worker. See start-here/evidencehost.md.\n'
def require_data(value):
    if not value: raise ValueError('negative-fixture-data-rejected')

def unique_pairs(items):
    result={}; folded=set()
    for key,value in items:
        require_data(key.casefold() not in folded)
        folded.add(key.casefold()); result[key]=value
    return result

def decode_data(raw):
    return json.loads(raw.decode('utf-8'),object_pairs_hook=unique_pairs,
                      parse_constant=lambda _:require_data(False))

def exact_object(value,keys):
    require_data(type(value) is dict and set(value)==set(keys))

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

"""Strict detached negative-record consistency checks; no OS access or native authority."""
import json
import re

MAX_JSON_BYTES = 4096
MAX_STREAM_BYTES = 1024 * 1024
MAX_RECEIVED_LIMIT = 16 * 1024 * 1024
UINT32_MAX = (1 << 32) - 1
UINT64_MAX = (1 << 64) - 1
TOP = frozenset(('schema', 'generation', 'worker_unit', 'process', 'ready', 'terminal',
                 'cgroup', 'pumps', 'joins', 'observation_only', 'native_authority', 'native_acceptance'))


class KernelRecordRejected(ValueError):
    """Closed data rejection, deliberately retaining no supplied values or cause text."""


def _reject():
    raise KernelRecordRejected('negative-kernel-data-rejected')


def _require(value):
    if not value:
        _reject()


def _uint(value, low, high):
    _require(type(value) is int and low <= value <= high)


def _digest(value):
    _require(type(value) is str and re.fullmatch('[0-9a-f]{64}', value) is not None)


def _object(value, keys):
    _require(type(value) is dict and set(value) == set(keys))


def _pairs(pairs):
    result = {}
    folded = set()
    for key, value in pairs:
        canonical = key.casefold()
        _require(canonical not in folded)
        folded.add(canonical)
        result[key] = value
    return result


def _stream(value, expected_sha256, expected_bytes):
    _object(value, ('received_bytes', 'retained_bytes', 'discarded_bytes', 'eof', 'failure', 'sha256'))
    for key in ('received_bytes', 'retained_bytes'):
        _uint(value[key], 0, MAX_STREAM_BYTES)
        _require(value[key] == expected_bytes)
    _uint(value['discarded_bytes'], 0, 0)
    _require(value['eof'] is True and value['failure'] == 'None')
    _digest(value['sha256'])
    _require(value['sha256'] == expected_sha256)


def check_kernel_record(raw, *, expected_generation, expected_uid, expected_gid,
                        expected_pid, expected_starttime_ticks, expected_descriptor_sha256,
                        expected_stdout_sha256, expected_stdout_bytes,
                        expected_stderr_sha256, expected_stderr_bytes):
    """Return True for detached data consistency only; never issue admission or acceptance.

    ``raw`` is exactly a JSON object of at most 4096 UTF-8 bytes, optionally followed
    by one LF. Expected values are caller data, not authenticated identities. The
    original source-built holder and root-private stream capture must independently
    establish provenance. No path, command, timer, descriptor or mutable lease is
    consumed or created. Every rejection has one fixed message and no chained cause.
    Both stream hashes/counts must come from full captured bytes outside this parser.
    The negative-case terminal is fixed CLD_EXITED(1), status 1; zero and signals reject.
    """
    try:
        _require(type(raw) is bytes and 0 < len(raw) <= MAX_JSON_BYTES + 1)
        body = raw[:-1] if raw.endswith(b'\n') else raw
        _require(0 < len(body) <= MAX_JSON_BYTES and body.startswith(b'{') and body.endswith(b'}'))
        _require(type(expected_generation) is str
                 and re.fullmatch('[0-9a-f]{32}', expected_generation) is not None
                 and expected_generation != '0' * 32)
        _uint(expected_uid, 1, UINT32_MAX - 1)
        _uint(expected_gid, 1, UINT32_MAX - 1)
        _uint(expected_pid, 1, (1 << 31) - 1)
        _uint(expected_starttime_ticks, 1, UINT64_MAX)
        for value in (expected_descriptor_sha256, expected_stdout_sha256, expected_stderr_sha256):
            _digest(value)
        _uint(expected_stdout_bytes, 0, MAX_STREAM_BYTES)
        _uint(expected_stderr_bytes, 0, MAX_STREAM_BYTES)
        value = json.loads(body.decode('utf-8', errors='strict'), object_pairs_hook=_pairs,
                           parse_constant=lambda _: _reject())
        _object(value, TOP)
        _require(value['schema'] == 'issue779-negative-kernel-observation-v1')
        _require(value['generation'] == expected_generation)
        unit = 'appsurface-evidence-worker-' + expected_generation + '.service'
        group = '/system.slice/' + unit
        _require(value['worker_unit'] == unit)
        process = value['process']
        _object(process, ('pid', 'starttime_ticks', 'uid4', 'gid4', 'control_group'))
        _uint(process['pid'], 1, (1 << 31) - 1)
        _uint(process['starttime_ticks'], 1, UINT64_MAX)
        _require(process['pid'] == expected_pid and process['starttime_ticks'] == expected_starttime_ticks)
        for name, expected in (('uid4', expected_uid), ('gid4', expected_gid)):
            ids = process[name]
            _require(type(ids) is list and len(ids) == 4)
            for identity in ids:
                _uint(identity, 1, UINT32_MAX - 1)
                _require(identity == expected)
        _require(process['control_group'] == group)
        ready = value['ready']
        _object(ready, ('committed', 'descriptor_sha256'))
        _require(ready['committed'] is True)
        _digest(ready['descriptor_sha256'])
        _require(ready['descriptor_sha256'] == expected_descriptor_sha256)
        terminal = value['terminal']
        _object(terminal, ('exec_main_pid', 'exec_main_code', 'exec_main_status', 'active_state', 'sub_state'))
        _uint(terminal['exec_main_pid'], 1, (1 << 31) - 1)
        _uint(terminal['exec_main_code'], 1, 1)
        _uint(terminal['exec_main_status'], 1, 1)
        _require(terminal['exec_main_pid'] == expected_pid)
        _require((terminal['active_state'], terminal['sub_state']) in
                 (('active', 'exited'), ('inactive', 'dead'), ('failed', 'failed')))
        cg = value['cgroup']
        _object(cg, ('exists', 'populated', 'frozen', 'device_major', 'device_minor', 'inode'))
        _require(type(cg['exists']) is bool)
        if cg['exists']:
            _require(cg['populated'] is False and cg['frozen'] is False)
            _uint(cg['device_major'], 0, UINT32_MAX)
            _uint(cg['device_minor'], 0, UINT32_MAX)
            _uint(cg['inode'], 1, UINT64_MAX)
        else:
            _require(all(cg[k] is None for k in ('populated', 'frozen', 'device_major', 'device_minor', 'inode')))
        pumps = value['pumps']
        _object(pumps, ('stdout', 'stderr', 'received_bytes', 'received_byte_limit', 'failure', 'discarded_bytes'))
        _stream(pumps['stdout'], expected_stdout_sha256, expected_stdout_bytes)
        _stream(pumps['stderr'], expected_stderr_sha256, expected_stderr_bytes)
        _uint(pumps['received_bytes'], 0, MAX_RECEIVED_LIMIT)
        _uint(pumps['received_byte_limit'], 1, MAX_RECEIVED_LIMIT)
        _uint(pumps['discarded_bytes'], 0, 0)
        _require(pumps['received_bytes'] == expected_stdout_bytes + expected_stderr_bytes
                 and pumps['received_bytes'] <= pumps['received_byte_limit'] and pumps['failure'] == 'None')
        _object(value['joins'], ('startup', 'pending_stop', 'monitor', 'server', 'pumps'))
        _require(all(x is True for x in value['joins'].values()))
        _require(value['observation_only'] is True and value['native_authority'] is False
                 and value['native_acceptance'] is False)
        return True
    except (ValueError, TypeError, KeyError, OverflowError, RecursionError, UnicodeError):
        raise KernelRecordRejected('negative-kernel-data-rejected') from None


WORKER_402 = b'ASEVD402: The protected broker identity changed. Fix: use an explicit mode and supported protected worker. See start-here/evidencehost.md.\n'
HELPER_KEYS = ('schema','status','generation','owner_pid','owner_start_ticks','owner_uid4',
    'owner_gid4','owner_groups','worker_pid','worker_start_ticks','worker_uid4','worker_gid4',
    'worker_groups','worker_stop_observed','ready_committed_notification',
    'replacement_peer_tuple_matched','replacement_request_bytes','replacement_received_bytes',
    'peer_live_identity_rechecked','replacement_write_shutdown','native_authority')
LIVE_KEYS = ('schema','pid','starttime_ticks','uid4','gid4','groups','control_group',
    'native_executable_matches','managed_argv_matches','native_authority',
    'no_new_privileges','cap_inheritable','cap_permitted','cap_effective','cap_bounding','cap_ambient')
DESCRIPTOR_KEYS = ('schema', 'run_id', 'worker_pid', 'broker_pid', 'worker_uid', 'worker_gid', 'subject_uid', 'subject_gid', 'unit', 'cgroup', 'job_deadline_utc', 'tool_root', 'subject_root', 'output_parent', 'output_slot', 'dotnet_path', 'test_output_root', 'policy_file', 'mode', 'socket_path', 'descriptor_path', 'entry_sha256', 'policy_sha256', 'base_revision', 'subject_revision', 'workflow_identity', 'provider', 'platform', 'proof_digest', 'output_parent_identity', 'observation_profile_ids', 'observation_producer_ids', 'paths', 'admission_seconds', 'start_seconds', 'collection_seconds', 'cleanup_seconds', 'stopping_seconds', 'diff_file', 'diff_sha256', 'solution')


def closed_uint(value,minimum=1,maximum=(1<<32)-2):
    require_data(type(value) is int and minimum<=value<=maximum)


def check_n04_records(root_stderr,helper_stdout,helper_stderr,helper_live_raw,descriptor_raw,*,
                      generation,request_sha256,expected_helper_unit):
    """Detached consistency only: all paths/images/units/bytes need external source-bound custody.

    Full worker output is compared through the original C# full-pump hash/count guard
    against the fixed expected 402 bytes. This is NOT an independent raw-worker capture.
    Actual helper PROC samples and pidfd/peer provenance cannot be established by JSON.
    """
    require_data(type(generation) is str and re.fullmatch('[0-9a-f]{32}',generation) is not None
                 and generation!='0'*32)
    require_data(type(request_sha256) is str and re.fullmatch('[0-9a-f]{64}',request_sha256) is not None)
    require_data(expected_helper_unit=='appsurface-evidence-n04-coordinator-'+generation+'.service')
    require_data(type(root_stderr) is bytes and 0<len(root_stderr)<=6144 and root_stderr.endswith(b'\n')
                 and b'\r' not in root_stderr)
    lines=root_stderr.splitlines(keepends=True)
    require_data(len(lines)==3 and all(x.endswith(b'\n') for x in lines)
                 and len(lines[0])<=4097 and len(lines[1])<=1024 and lines[2]==ROOT_TERMINAL)
    failure=validate_failure(decode_data(lines[1]))
    require_data(failure['phase'] in ('ServerRun','ServerCompletion') and failure['error_kind']=='Admission'
                 and failure['diagnostic_code']=='ASEVD410' and failure['account_failure'] is None
                 and failure['custody_failure'] is None)
    require_data(type(helper_stdout) is bytes and 0<len(helper_stdout)<=4096
                 and helper_stdout.endswith(b'\n') and helper_stdout.count(b'\n')==1
                 and helper_stderr==b'')
    helper=decode_data(helper_stdout);exact_object(helper,HELPER_KEYS)
    require_data(helper['schema']=='issue779-n04-root-coordinator-result-v2'
                 and helper['status']=='observed' and helper['generation']==generation
                 and helper['native_authority'] is False)
    for key in ('owner_pid','worker_pid'): closed_uint(helper[key],1,(1<<31)-1)
    for key in ('owner_start_ticks','worker_start_ticks'): closed_uint(helper[key],1,(1<<64)-1)
    for key in ('worker_stop_observed','ready_committed_notification','replacement_peer_tuple_matched',
                'peer_live_identity_rechecked','replacement_write_shutdown'):
        require_data(helper[key] is True)
    for key in ('replacement_request_bytes','replacement_received_bytes'):
        require_data(type(helper[key]) is int and helper[key]==0)
    live=decode_data(helper_live_raw);exact_object(live,LIVE_KEYS)
    for name,expected in (('no_new_privileges',1),('cap_inheritable',0),('cap_permitted',524333),
                          ('cap_effective',524333),('cap_bounding',524333),('cap_ambient',0)):
        require_data(type(live[name]) is int and live[name]==expected)
    require_data(live['schema']=='issue779-n04-helper-live-v2' and live['native_authority'] is False
                 and live['native_executable_matches'] is True and live['managed_argv_matches'] is True)
    closed_uint(live['pid'],1,(1<<31)-1);closed_uint(live['starttime_ticks'],1,(1<<64)-1)
    require_data(live['control_group']=='/system.slice/'+expected_helper_unit
                 and len({live['pid'],helper['owner_pid'],helper['worker_pid']})==3)
    require_data(type(descriptor_raw) is bytes and 0<len(descriptor_raw)<=65536)
    descriptor=decode_data(descriptor_raw);exact_object(descriptor,DESCRIPTOR_KEYS)
    require_data(descriptor['schema']=='evidence-worker-linux-v1' and descriptor['run_id']=='csharp/'+generation
                 and descriptor['broker_pid']==helper['owner_pid'] and descriptor['worker_pid']==helper['worker_pid'])
    for key in ('worker_uid','worker_gid','subject_uid','subject_gid'): closed_uint(descriptor[key])
    for obj,uid,gid in ((helper,0,0),(live,0,0)):
        names=('owner_uid4','owner_gid4','owner_groups') if obj is helper else ('uid4','gid4','groups')
        require_data(obj[names[0]]==[uid]*4 and obj[names[1]]==[gid]*4
                     and type(obj[names[2]]) is list and obj[names[2]] in ([],[0]))
        require_data(all(type(x) is int for name in names for x in obj[name]))
    uid,gid=descriptor['worker_uid'],descriptor['worker_gid']
    require_data(helper['worker_uid4']==[uid]*4 and helper['worker_gid4']==[gid]*4
                 and type(helper['worker_groups']) is list and helper['worker_groups'] in ([],[gid]))
    require_data(all(type(x) is int for name in ('worker_uid4','worker_gid4','worker_groups') for x in helper[name]))
    workspace='/run/appsurface-evidence-'+generation
    unit='appsurface-evidence-worker-'+generation+'.service'
    require_data(descriptor['unit']==unit and descriptor['cgroup']=='/system.slice/'+unit
                 and descriptor['descriptor_path']==workspace+'/worker/worker-control.json'
                 and descriptor['socket_path']==workspace+'/worker/broker/control.sock'
                 and descriptor['output_parent']==workspace+'/output'
                 and descriptor['output_slot']=='evidence' and descriptor['test_output_root']==workspace+'/raw-results'
                 and descriptor['provider']=='github-actions' and descriptor['platform']=='linux-x64'
                 and descriptor['proof_digest']=='')
    check_kernel_record(lines[0],expected_generation=generation,expected_uid=uid,expected_gid=gid,
        expected_pid=descriptor['worker_pid'],expected_starttime_ticks=helper['worker_start_ticks'],
        expected_descriptor_sha256=hashlib.sha256(descriptor_raw).hexdigest(),
        expected_stdout_sha256=hashlib.sha256(b'').hexdigest(),expected_stdout_bytes=0,
        expected_stderr_sha256=hashlib.sha256(WORKER_402).hexdigest(),expected_stderr_bytes=len(WORKER_402))
    return {'schema':'issue779-n04-detached-consistency-v1','control':'N04','generation':generation,
        'worker_pid':helper['worker_pid'],'worker_starttime_ticks':helper['worker_start_ticks'],
        'replacement_pid':live['pid'],'original_broker_pid':helper['owner_pid'],
        'descriptor_sha256':hashlib.sha256(descriptor_raw).hexdigest(),'request_sha256':request_sha256,
        'worker_full_output_matches_fixed402_via_original_holder':True,
        'independent_raw_worker_capture':False,'observation_only':True,
        'native_authority':False,'native_acceptance':False}
