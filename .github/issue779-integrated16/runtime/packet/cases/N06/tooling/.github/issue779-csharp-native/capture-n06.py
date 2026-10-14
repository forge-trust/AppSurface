#!/usr/bin/env python3
"""Fixed root-private slot capture. Native provenance is checked by the owning fixture."""
import base64,hashlib,io,json,os,re,stat,sys,tarfile
_slot_os = os
CASE = 'N06'
def require(v):
    if not v: raise ValueError('negative-fixture-data-rejected')
def pairs(items):
    result={}; seen=set()
    for k,v in items:
        require(k.casefold() not in seen); seen.add(k.casefold()); result[k]=v
    return result
def decode(b):
    return json.loads(b.decode('utf-8'),object_pairs_hook=pairs,parse_constant=lambda _:require(False))
def fingerprint(s):
    return (s.st_dev,s.st_ino,s.st_mode,s.st_nlink,s.st_uid,s.st_gid,s.st_size,s.st_mtime_ns,s.st_ctime_ns)
def parents(path):
    require(path.startswith('/') and not path.endswith('/') and '//' not in path)
    parts=path.split('/')[1:]; require(all(x and x not in ('.','..') for x in parts))
    current=''
    for part in parts[:-1]:
        current+='/'+part; s=os.lstat(current)
        require(stat.S_ISDIR(s.st_mode) and not stat.S_ISLNK(s.st_mode))
def read_private(path,limit,uid=0,gid=0,mode=0o600):
    parents(path); named=os.lstat(path)
    require(stat.S_ISREG(named.st_mode) and named.st_nlink==1 and named.st_uid==uid
            and named.st_gid==gid and stat.S_IMODE(named.st_mode)==mode and named.st_size<=limit)
    fd=os.open(path,os.O_RDONLY|os.O_NOFOLLOW|os.O_NONBLOCK|os.O_CLOEXEC)
    try:
        before=os.fstat(fd); require(fingerprint(before)==fingerprint(named))
        data=b''
        while len(data)<=limit:
            block=os.read(fd,min(65536,limit+1-len(data)))
            if not block: break
            data+=block
        require(len(data)<=limit and len(data)==before.st_size)
        require(fingerprint(os.fstat(fd))==fingerprint(before)
                and fingerprint(os.lstat(path))==fingerprint(before))
        return data
    finally: os.close(fd)
def save(privateDir,name,data):
    require('/' not in name and name not in ('.','..'))
    p=privateDir+'/'+name; require(not os.path.lexists(p))
    fd=os.open(p,os.O_WRONLY|os.O_CREAT|os.O_EXCL|os.O_NOFOLLOW|os.O_CLOEXEC,0o600)
    try:
        s=os.fstat(fd); require(stat.S_ISREG(s.st_mode) and s.st_nlink==1 and s.st_uid==s.st_gid==0)
        offset=0
        while offset<len(data):
            n=os.write(fd,data[offset:]); require(n>0); offset+=n
        os.fsync(fd); require(os.fstat(fd).st_size==len(data))
    finally: os.close(fd)
    require(read_private(p,len(data))==data)
def ident(path,value,kind):
    require(type(value) is dict and set(value)=={'device_major','device_minor','inode','uid','gid','mode','links','length'})
    require(all(type(v) is int and v>=0 for v in value.values()))
    parents(path); s=os.lstat(path)
    require((kind=='d' and stat.S_ISDIR(s.st_mode)) or (kind=='f' and stat.S_ISREG(s.st_mode))
            or (kind=='l' and stat.S_ISLNK(s.st_mode)))
    measured={'device_major':os.major(s.st_dev),'device_minor':os.minor(s.st_dev),'inode':s.st_ino,
              'uid':s.st_uid,'gid':s.st_gid,'mode':s.st_mode,'links':s.st_nlink,'length':s.st_size}
    require(measured==value); return s

ENUMS = {'EvidenceControlOperation': ['Ready', 'Stop', 'Wait', 'Exit', 'Run', 'Artifacts', 'Artifact', 'ApplicationStart', 'ResourceWait'], 'EvidenceNativeObservationErrorKind': ['Unknown', 'Admission', 'Accounts', 'OutputPipe', 'ControlLine', 'Cancelled', 'Timeout', 'Io', 'AccessDenied', 'Unsupported', 'Disposed', 'Argument', 'InvalidData', 'InvalidOperation'], 'EvidenceNativeObservationPhase': ['Unknown', 'CallerCancellation', 'ProtectedInput', 'JobDeadline', 'BackendConnect', 'OwnerActivation', 'Plan', 'AccountCreate', 'WorkspaceCreate', 'ListenerBind', 'WorkerCreate', 'WorkerStart', 'ServerCreate', 'ServerLifetime', 'ServerRun', 'ServerCompletion', 'WorkerExit', 'WorkerStop', 'WorkerCompletion', 'BeginTeardown', 'Custody', 'FileVerification', 'CleanupBegin', 'ServerCancel', 'ListenerClose', 'ServerJoin', 'WorkerJoin', 'CleanupCustody', 'AccountsClose', 'WorkerClose', 'CustodyClose', 'WorkspaceClose', 'OwnerFinalCheck', 'ServerLifetimeClose', 'JobClose', 'OwnerClose', 'InputClose', 'BackendClose', 'FinalDeadline', 'ResultCheck'], 'LinuxAccountPreparationStage': ['Unknown', 'NamesAbsent', 'ReserveUtility', 'UtilityCreate', 'UtilityExecute', 'IdentityRead', 'OwnershipVerify', 'CleanupCheck', 'CleanupNameCheck', 'CleanupUtility', 'FinalNamesAbsent', 'FinalOwnershipCheck'], 'LinuxAccountUtilityStage': ['Unknown', 'OwnerCheck', 'Pipes', 'BackendConnect', 'Recipe', 'Start', 'CloseWrites', 'UnitRead', 'TerminalCheck', 'ObservationDelay', 'BeginTeardown', 'Stop', 'GroupRead', 'OutputJoin', 'PipeDispose', 'BackendDispose', 'PhysicalSettlement', 'FinalOwnerCheck'], 'LinuxControlFailureStage': ['Unknown', 'PeerCheck', 'WorkerExitTask', 'RequestLifetime', 'AcceptLoop', 'HandlerJoin', 'CapacityWait', 'Accept', 'AcceptJoin', 'ControlRegistration', 'HandlerDispatch', 'RequestRead', 'RequestClassify', 'CleanupRegistration', 'Stop', 'WaitJoin', 'ReplyGate', 'ReadyAuthorization', 'ReadyClaim', 'ReadyData', 'ResponseData', 'WaitClaim', 'ExitClaim', 'ResponseWrite', 'ConnectionRelease', 'PostWriteCheck', 'ReplyCommit', 'HandlerFailureCommit', 'ReplyGateRelease', 'ControlRelease', 'CleanupRegistrationClose', 'ExitCommit', 'AcceptCancel', 'ListenerClose', 'PendingAcceptJoin', 'HandlersJoin', 'DescendantsStop', 'ControlsJoin', 'FinalCancellation', 'CleanupBound', 'OwnerCheck', 'ProtocolIncomplete', 'WorkerTerminalTaskCompleted', 'ReplyGateClose', 'ListenerState', 'ListenerCancellation', 'ListenerWorkspace', 'ListenerParent', 'ListenerSocketMetadata', 'ListenerSocketName', 'ListenerEndpoint', 'ListenerWorkerSelection', 'ListenerOwnerIdentity', 'ListenerWorkerIdentity', 'ListenerDescriptor', 'ListenerNativeAccept', 'ListenerAcceptedPeer', 'ProcessState', 'ProcessSelection', 'ProcessExpectedSample', 'ProcessExpectedPid', 'ProcessExpectedStartTime', 'ProcessExpectedLiveState', 'ProcessExpectedUid', 'ProcessExpectedGid', 'ProcessExpectedCgroup', 'ProcessInitialContinuity', 'ProcessRepeatedContinuity', 'ProcessReadContinuity', 'ProcessRetainedProcRoot', 'ProcessRetainedProcess', 'ProcessRetainedStatus', 'ProcessRetainedStat', 'ProcessRetainedCgroup', 'ProcessNamedProcRoot', 'ProcessNamedProcess', 'ProcessNamedStatus', 'ProcessNamedStat', 'ProcessNamedCgroup', 'ProcessFirstStatRead', 'ProcessFirstStatParse', 'ProcessStatusRead', 'ProcessStatusParse', 'ProcessCgroupRead', 'ProcessCgroupParse', 'ProcessLastStatRead', 'ProcessLastStatParse', 'ProcessFileSystemInspect', 'ProcessFileSystemType', 'ProcessDirectoryStat', 'ProcessDirectoryInode', 'ProcessDirectoryType', 'ProcessRetainedProcessDeviceMajor', 'ProcessRetainedProcessDeviceMinor', 'ProcessRetainedProcessInode', 'ProcessRetainedProcessUid', 'ProcessRetainedProcessGid', 'ProcessRetainedProcessMode', 'ProcessRetainedProcessMetadata', 'ListenerAdmissionDrain', 'ListenerSocketClose', 'ListenerNamedSocketClose', 'ListenerParentClose', 'ListenerNativeAcceptOperationAborted', 'ListenerNativeAcceptInterrupted', 'ListenerNativeAcceptConnectionAborted', 'ListenerNativeAcceptSocketOther', 'ListenerAcceptedClose'], 'LinuxCustodyNodeKind': ['Generation', 'Control', 'Broker', 'Output', 'RawResults', 'Slot', 'Descriptor', 'Socket', 'Plan', 'Manifest', 'Summary'], 'LinuxCustodyOperation': ['Unknown', 'Platform', 'Settlement', 'AccountOwner', 'NodeSelection', 'Open', 'RetainedStat', 'AncestorPolicy', 'OriginalPolicy', 'BaselineComparison', 'NamedOpen', 'NamedStat', 'NamedComparison', 'InventoryRead', 'InventoryDecode', 'InventoryPolicy', 'InventoryComparison', 'HashRead', 'HashEof', 'HashFinalize', 'HashComparison', 'Chown', 'Chmod', 'TerminalPolicy', 'OriginalOwnerClose', 'FileRead', 'FileVerification', 'AccountRelease', 'RootRecheck', 'Cancellation', 'HolderState', 'OwnerIdentity'], 'LinuxRunAccountFailure': ['InvalidData', 'IdentityMismatch', 'NssFailed', 'UnsupportedPlatform', 'OperationFailed', 'CleanupFailed'], 'LinuxRunAccountOperation': ['CreateUser', 'CreateResultsGroup', 'DeleteUser', 'DeleteGroup'], 'LinuxSystemdStartError': ['Other', 'AccessDenied', 'InvalidArgs', 'NoReply', 'ServiceUnknown', 'UnknownMethod', 'UnitExists', 'LoadFailed', 'NoSuchUnit'], 'SupervisionCustodyFailure': ['None', 'Cancelled', 'SettlementValidationFailed', 'PreflightFailed', 'MutationFailed', 'LocalCloseFailed', 'FinalNativeRecheckFailed']}
CODES = frozenset(('ASEVD402','ASEVD404','ASEVD407','ASEVD409','ASEVD410','ASEVD420','ASEVD421'))
ROOT_TERMINAL = b'ASEVD410: The protected empty Observation execution or final cleanup could not be established. Fix: use an explicit mode and supported protected worker. See start-here/evidencehost.md.\n'
WORKER_TERMINAL = b'ASEVD409: Fresh output allocation or activation failed. Fix: use an explicit mode and supported protected worker. See start-here/evidencehost.md.\n'
NEGATIVE_CAPS = {
    'negative-setup.json':1024, 'negative-post-join.json':1024,
    'negative-worker-projection.json':1024, 'negative-kernel-observation.json':4097,
    'negative-worker-raw.json':4097, 'negative-root-failure.json':1024,
    'negative-root-terminal.txt':1024, 'negative-descriptor.json':65536,
    'negative-account-ids.tsv':128,
    'n05-worker.stdout':0, 'n05-worker.stderr':2048,
    'n06-worker.stdout':0, 'n06-worker.stderr':2048,
}


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

def validate_fixture_frames(raw,case):
    """Seven exact LF frames, real producer raw bytes; returns detached data only."""
    require_data(case in ('N05','N06') and type(raw) is bytes and 0<len(raw)<=14336)
    require_data(raw.endswith(b'\n') and b'\r' not in raw)
    lines=raw.splitlines(keepends=True)
    require_data(len(lines)==7 and all(x.endswith(b'\n') for x in lines))
    require_data(all(0<len(x)<=n for x,n in zip(lines,(1024,1024,1024,4097,4097,1024,1024))))
    require_data(lines[6]==ROOT_TERMINAL)
    setup,after,projection,kernel,worker,failure=[decode_data(x) for x in lines[:6]]
    schema='issue779-n05-slot-inspection-v1' if case=='N05' else 'issue779-n06-symlink-inspection-v1'
    keys=('schema','phase','parent','slot','sentinel','sha256') if case=='N05' else ('schema','phase','parent','target','link','target_sha256','sentinel','sha256')
    exact_object(setup,keys);exact_object(after,keys)
    require_data(setup['schema']==after['schema']==schema and setup['phase']=='setup' and after['phase']=='post_join')
    require_data({k:v for k,v in setup.items() if k!='phase'}=={k:v for k,v in after.items() if k!='phase'})
    exact_object(projection,('schema','origin','allocation','terminal_diagnostic','stdout_bytes','stderr_bytes','native_authority'))
    require_data(projection['schema']=='issue779-'+case.lower()+'-joined-worker-output-v1'
                 and projection['origin']=='joined-worker-output' and projection['terminal_diagnostic']=='ASEVD409'
                 and type(projection['stdout_bytes']) is int and projection['stdout_bytes']==0
                 and type(projection['stderr_bytes']) is int and 0<projection['stderr_bytes']<=2048
                 and projection['native_authority'] is False)
    alloc=projection['allocation'];exact_object(alloc,('schema','phase','operation','stageOutcome','terminalCode','errorClass','nativeErrno'))
    require_data(alloc['schema']=='evidence-allocation-failure-v1' and alloc['phase']=='Allocation'
                 and alloc['operation']=='CreateSlot' and alloc['stageOutcome']=='Failed'
                 and alloc['terminalCode']=='StageFailed' and alloc['errorClass']=='Io'
                 and (alloc['nativeErrno'] is None or type(alloc['nativeErrno']) is int and 1<=alloc['nativeErrno']<=4095))
    exact_object(worker,('schema','stdout_base64','stderr_base64','stdout_bytes','stderr_bytes','native_authority'))
    require_data(worker['schema']=='issue779-'+case.lower()+'-joined-worker-raw-v1' and worker['native_authority'] is False
                 and worker['stdout_base64']=='' and type(worker['stdout_bytes']) is int and worker['stdout_bytes']==0
                 and type(worker['stderr_bytes']) is int and 0<worker['stderr_bytes']<=2048
                 and type(worker['stderr_base64']) is str)
    stdout=base64.b64decode(worker['stdout_base64'],validate=True)
    stderr=base64.b64decode(worker['stderr_base64'],validate=True)
    require_data(base64.b64encode(stdout).decode('ascii')==worker['stdout_base64']
                 and base64.b64encode(stderr).decode('ascii')==worker['stderr_base64']
                 and stdout==b'' and len(stderr)==worker['stderr_bytes']==projection['stderr_bytes'])
    worker_lines=stderr.splitlines(keepends=True)
    require_data(len(worker_lines)==2 and worker_lines[1]==WORKER_TERMINAL and decode_data(worker_lines[0])==alloc)
    validate_failure(failure)
    return {'lines':lines,'kernel':kernel,'stdout':stdout,'stderr':stderr,'failure':failure}

TOP = frozenset(('schema', 'generation', 'worker_unit', 'process', 'ready', 'terminal',
                 'cgroup', 'pumps', 'joins', 'observation_only', 'native_authority', 'native_acceptance'))

MAX_RECEIVED_LIMIT = 16 * 1024 * 1024

MAX_STREAM_BYTES = 1024 * 1024

UINT64_MAX = (1 << 64) - 1

UINT32_MAX = (1 << 32) - 1

MAX_JSON_BYTES = 4096

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
SLOT_SENTINEL_BYTES = b'issue779-owned-slot-sentinel\n'
SLOT_SENTINEL_SHA256 = hashlib.sha256(SLOT_SENTINEL_BYTES).hexdigest()
SLOT_FRAME_MEMBERS = (
    'negative-setup.json',
    'negative-post-join.json',
    'negative-worker-projection.json',
    'negative-kernel-observation.json',
    'negative-worker-raw.json',
    'negative-root-failure.json',
    'negative-root-terminal.txt',
)
SLOT_DESCRIPTOR_KEYS = frozenset((
    'schema', 'run_id', 'worker_pid', 'broker_pid', 'worker_uid', 'worker_gid',
    'subject_uid', 'subject_gid', 'unit', 'cgroup', 'job_deadline_utc',
    'tool_root', 'subject_root', 'output_parent', 'output_slot', 'dotnet_path',
    'test_output_root', 'policy_file', 'mode', 'socket_path', 'descriptor_path',
    'entry_sha256', 'policy_sha256', 'base_revision', 'subject_revision',
    'workflow_identity', 'provider', 'platform', 'proof_digest',
    'output_parent_identity', 'observation_profile_ids', 'observation_producer_ids',
    'paths', 'admission_seconds', 'start_seconds', 'collection_seconds',
    'cleanup_seconds', 'stopping_seconds', 'diff_file', 'diff_sha256', 'solution',
))
SLOT_IDENTITY_KEYS = ('device_major', 'device_minor', 'inode', 'uid', 'gid', 'mode', 'links', 'length')


def _slot_identity(value):
    exact_object(value, SLOT_IDENTITY_KEYS)
    for key in ('device_major', 'device_minor'):
        require_data(type(value[key]) is int and 0 <= value[key] <= UINT32_MAX)
    require_data(type(value['inode']) is int and 0 < value['inode'] <= UINT64_MAX)
    require_data(type(value['uid']) is int and 0 <= value['uid'] < UINT32_MAX)
    require_data(type(value['gid']) is int and 0 <= value['gid'] < UINT32_MAX)
    require_data(type(value['mode']) is int and 0 <= value['mode'] <= UINT32_MAX)
    require_data(type(value['links']) is int and 0 < value['links'] <= UINT32_MAX)
    require_data(type(value['length']) is int and 0 <= value['length'] <= UINT64_MAX)


def _slot_descriptor(raw, generation, uid, gid, subject_uid, subject_gid):
    require_data(type(raw) is bytes and 0 < len(raw) <= 65536 and b'\n' not in raw and b'\r' not in raw)
    value = decode_data(raw)
    exact_object(value, SLOT_DESCRIPTOR_KEYS)
    require_data(value['schema'] == 'evidence-worker-linux-v1'
                 and value['run_id'] == 'csharp/' + generation)
    require_data(type(value['worker_pid']) is int and 1 <= value['worker_pid'] < (1 << 31)
                 and type(value['broker_pid']) is int and 1 <= value['broker_pid'] < (1 << 31)
                 and value['worker_pid'] != value['broker_pid'])
    for key in ('worker_uid', 'worker_gid', 'subject_uid', 'subject_gid'):
        require_data(type(value[key]) is int and 0 < value[key] < UINT32_MAX)
    require_data(value['worker_uid'] == uid and value['worker_gid'] == gid
                 and value['subject_uid'] == subject_uid and value['subject_gid'] == subject_gid)
    unit = 'appsurface-evidence-worker-' + generation + '.service'
    require_data(value['unit'] == unit and value['cgroup'] == '/system.slice/' + unit
                 and value['output_slot'] == 'evidence' and value['mode'] == 'observation'
                 and value['provider'] == 'github-actions' and value['platform'] == 'linux-x64'
                 and value['proof_digest'] == '')
    string_fields = SLOT_DESCRIPTOR_KEYS - {
        'worker_pid', 'broker_pid', 'worker_uid', 'worker_gid', 'subject_uid', 'subject_gid',
        'output_parent_identity', 'observation_profile_ids', 'observation_producer_ids', 'paths',
        'admission_seconds', 'start_seconds', 'collection_seconds', 'cleanup_seconds', 'stopping_seconds',
        'diff_file', 'diff_sha256', 'solution',
    }
    require_data(all(type(value[key]) is str for key in string_fields))
    require_data(all(value[key] is None or type(value[key]) is str for key in ('diff_file', 'diff_sha256', 'solution')))
    for key in ('observation_profile_ids', 'observation_producer_ids', 'paths'):
        require_data(type(value[key]) is list and all(type(item) is str for item in value[key]))
    for key in ('admission_seconds', 'start_seconds', 'collection_seconds', 'cleanup_seconds', 'stopping_seconds'):
        require_data(type(value[key]) is int and value[key] >= 0)
    for key in ('entry_sha256', 'policy_sha256'):
        require_data(re.fullmatch('[0-9a-f]{64}', value[key]) is not None)
    require_data(_slot_os.path.isabs(value['output_parent'])
                 and _slot_os.path.isabs(value['test_output_root'])
                 and _slot_os.path.normpath(_slot_os.path.join(value['output_parent'], '../raw-results'))
                 == _slot_os.path.normpath(value['test_output_root']))
    exact_object(value['output_parent_identity'], ('device_major', 'device_minor', 'inode', 'uid', 'gid'))
    parent = value['output_parent_identity']
    require_data(type(parent['device_major']) is int and 0 <= parent['device_major'] <= UINT32_MAX
                 and type(parent['device_minor']) is int and 0 <= parent['device_minor'] <= UINT32_MAX
                 and type(parent['inode']) is int and 0 < parent['inode'] <= UINT64_MAX
                 and type(parent['uid']) is int and 0 < parent['uid'] < UINT32_MAX
                 and type(parent['gid']) is int and 0 < parent['gid'] < UINT32_MAX
                 and parent['uid'] == uid and parent['gid'] == gid)
    return value


def _slot_account_ids(raw, uid, gid, subject_uid, subject_gid):
    """The actual root fixture emits one ordered five-column TSV record."""
    require_data(type(raw) is bytes and 0 < len(raw) <= 128
                 and raw.endswith(b'\n') and raw.count(b'\n') == 1 and b'\r' not in raw)
    fields = raw[:-1].split(b'\t')
    require_data(len(fields) == 5 and all(re.fullmatch(rb'[1-9][0-9]*', field) is not None for field in fields))
    numbers = tuple(int(field) for field in fields)
    require_data(numbers[:4] == (uid, gid, subject_uid, subject_gid)
                 and all(0 < number < UINT32_MAX for number in numbers)
                 and len({gid, subject_gid, numbers[4]}) == 3)
    return numbers[4]


def _slot_validate_metadata(setup, after, case, uid, gid, subject_uid, results_gid):
    for value in (setup, after):
        parent = value['parent']
        sentinel = value['sentinel']
        _slot_identity(parent)
        _slot_identity(sentinel)
        require_data(parent['uid'] == uid and parent['gid'] == gid and parent['mode'] == 0x41c0)
        require_data(parent['device_major'] == sentinel['device_major']
                     and parent['device_minor'] == sentinel['device_minor'])
        require_data(sentinel['mode'] == 0x8180 and sentinel['links'] == 1
                     and sentinel['length'] == len(SLOT_SENTINEL_BYTES))
        require_data(value['sha256'] == SLOT_SENTINEL_SHA256)
        if case == 'N05':
            slot = value['slot']
            _slot_identity(slot)
            require_data(slot['uid'] == uid and slot['gid'] == gid and slot['mode'] == 0x41c0
                         and slot['links'] >= 2 and slot['inode'] != parent['inode']
                         and slot['device_major'] == parent['device_major']
                         and slot['device_minor'] == parent['device_minor']
                         and sentinel['inode'] not in (parent['inode'], slot['inode'])
                         and sentinel['uid'] == uid and sentinel['gid'] == gid
                         and sentinel['device_major'] == slot['device_major']
                         and sentinel['device_minor'] == slot['device_minor'])
        else:
            target = value['target']
            link = value['link']
            _slot_identity(target)
            _slot_identity(link)
            require_data(target['uid'] == subject_uid and target['gid'] == results_gid
                         and target['mode'] == 0x41c8 and target['links'] >= 2
                         and target['inode'] != parent['inode']
                         and target['device_major'] == parent['device_major']
                         and target['device_minor'] == parent['device_minor'])
            require_data(link['uid'] == 0 and link['gid'] == 0 and link['mode'] == 0xa1ff
                         and link['links'] == 1 and link['length'] == len(b'../raw-results')
                         and link['inode'] not in (parent['inode'], target['inode'])
                         and link['device_major'] == parent['device_major']
                         and link['device_minor'] == parent['device_minor'])
            require_data(sentinel['inode'] not in (parent['inode'], target['inode'], link['inode'])
                         and sentinel['uid'] == 0 and sentinel['gid'] == 0
                         and sentinel['device_major'] == target['device_major']
                         and sentinel['device_minor'] == target['device_minor']
                         and value['target_sha256'] == SLOT_SENTINEL_SHA256)


def validate_slot_archive(values, case, expected_generation, expected_uid, expected_gid,
                          expected_subject_uid, expected_subject_gid):
    """Validate fixed retained N05/N06 members as detached data; never creates native credit.

    values is the raw-member mapping returned by inspect_retention. The exact seven
    frame members, descriptor, account ID map, and case-specific full stdout/stderr
    bytes are consumed by fixed archive names. This adapter has no caller override map,
    alias lookup, process, filesystem, Git, or network operation. Start time is
    read from the kernel record itself because the retained descriptor has no
    start-time field; this check is only consistency. A caller must independently
    authenticate the root-private archive and original-worker provenance.
    """
    try:
        require_data(type(values) is dict and type(case) is str and case in ('N05', 'N06'))
        require_data(type(expected_generation) is str
                     and re.fullmatch('[0-9a-f]{32}', expected_generation) is not None
                     and expected_generation != '0' * 32)
        for number in (expected_uid, expected_gid, expected_subject_uid, expected_subject_gid):
            require_data(type(number) is int and 0 < number < UINT32_MAX)
        require_data(expected_uid != expected_subject_uid and expected_gid != expected_subject_gid)
        stdout_name = case.lower() + '-worker.stdout'
        stderr_name = case.lower() + '-worker.stderr'
        opposite = 'n06' if case == 'N05' else 'n05'
        required = set(SLOT_FRAME_MEMBERS) | {
            'negative-descriptor.json', 'negative-account-ids.tsv', stdout_name, stderr_name,
        }
        require_data(required <= set(values))
        require_data(opposite + '-worker.stdout' not in values and opposite + '-worker.stderr' not in values)
        require_data(all(type(values[name]) is bytes for name in required))
        caps = {
            'negative-setup.json': 1024, 'negative-post-join.json': 1024,
            'negative-worker-projection.json': 1024, 'negative-kernel-observation.json': 4097,
            'negative-worker-raw.json': 4097, 'negative-root-failure.json': 1024,
        }
        for name, cap in caps.items():
            raw = values[name]
            require_data(0 < len(raw) <= cap and raw.endswith(b'\n')
                         and raw.count(b'\n') == 1 and b'\r' not in raw)
        require_data(values['negative-root-terminal.txt'] == ROOT_TERMINAL)
        frame_bytes = b''.join(values[name] for name in SLOT_FRAME_MEMBERS)
        frames = validate_fixture_frames(frame_bytes, case)
        setup = decode_data(values['negative-setup.json'])
        after = decode_data(values['negative-post-join.json'])
        descriptor = _slot_descriptor(values['negative-descriptor.json'], expected_generation,
                                      expected_uid, expected_gid, expected_subject_uid, expected_subject_gid)
        results_gid = _slot_account_ids(values['negative-account-ids.tsv'], expected_uid, expected_gid,
                                        expected_subject_uid, expected_subject_gid)
        _slot_validate_metadata(setup, after, case, expected_uid, expected_gid, expected_subject_uid, results_gid)
        parent = setup['parent']
        descriptor_parent = descriptor['output_parent_identity']
        require_data(all(parent[key] == descriptor_parent[key]
                         for key in ('device_major', 'device_minor', 'inode', 'uid', 'gid')))
        kernel = frames['kernel']
        process = kernel['process']
        stdout = values[stdout_name]
        stderr = values[stderr_name]
        require_data(stdout == frames['stdout'] and stderr == frames['stderr'])
        require_data(stdout == b'' and len(stderr) > 0)
        require_data(check_kernel_record(
            values['negative-kernel-observation.json'],
            expected_generation=expected_generation,
            expected_uid=expected_uid,
            expected_gid=expected_gid,
            expected_pid=descriptor['worker_pid'],
            expected_starttime_ticks=process['starttime_ticks'],
            expected_descriptor_sha256=hashlib.sha256(values['negative-descriptor.json']).hexdigest(),
            expected_stdout_sha256=hashlib.sha256(stdout).hexdigest(),
            expected_stdout_bytes=len(stdout),
            expected_stderr_sha256=hashlib.sha256(stderr).hexdigest(),
            expected_stderr_bytes=len(stderr),
        ) is True)
        return True
    except (ValueError, TypeError, KeyError, OverflowError, RecursionError, UnicodeError, OSError):
        raise ValueError('slot-archive-data-rejected') from None

def body():
    require(len(sys.argv) == 5 and os.geteuid() == 0)
    private, workspace, generation, policy_hash = sys.argv[1:]
    require(re.fullmatch('[0-9a-f]{32}', generation) is not None and generation != '0' * 32)
    require(re.fullmatch('[0-9a-f]{64}', policy_hash) is not None)
    require(workspace == '/run/appsurface-evidence-' + generation)
    p = os.lstat(private)
    require(stat.S_ISDIR(p.st_mode) and p.st_uid == p.st_gid == 0 and stat.S_IMODE(p.st_mode) == 0o700)
    raw = read_private(private + '/logs/n01.stderr', 14336)
    require(read_private(private + '/logs/n01.stdout', 0) == b'')
    frames = validate_fixture_frames(raw, CASE)
    lines = frames['lines']
    values = dict(zip(SLOT_FRAME_MEMBERS, lines))
    descriptor_path = workspace + '/worker/worker-control.json'
    ds = os.lstat(descriptor_path)
    require(ds.st_gid > 0)
    descriptor_bytes = read_private(descriptor_path, 65536, 0, ds.st_gid, 0o440)
    descriptor = decode_data(descriptor_bytes)
    require(descriptor['descriptor_path'] == descriptor_path and descriptor['output_parent'] == workspace + '/output'
            and descriptor['test_output_root'] == workspace + '/raw-results'
            and descriptor['socket_path'] == workspace + '/worker/broker/control.sock'
            and descriptor['policy_sha256'] == policy_hash)
    uid, gid = descriptor['worker_uid'], descriptor['worker_gid']
    setup, after = decode_data(lines[0]), decode_data(lines[1])
    parent = workspace + '/output'
    ident(parent, after['parent'], 'd')
    require(sorted(os.listdir(parent)) == ['evidence'])
    if CASE == 'N05':
        slot = parent + '/evidence'
        ident(slot, after['slot'], 'd')
        require(sorted(os.listdir(slot)) == ['allocation-sentinel'])
        sentinel_path = slot + '/allocation-sentinel'
        sentinel_uid, sentinel_gid = uid, gid
    else:
        target, link = workspace + '/raw-results', parent + '/evidence'
        ident(target, after['target'], 'd')
        before = ident(link, after['link'], 'l')
        require(os.readlink(link) == '../raw-results' and fingerprint(os.lstat(link)) == fingerprint(before))
        require(sorted(os.listdir(target)) == ['allocation-sentinel'])
        sentinel_path, sentinel_uid, sentinel_gid = target + '/allocation-sentinel', 0, 0
    ident(sentinel_path, after['sentinel'], 'f')
    require(read_private(sentinel_path, len(SLOT_SENTINEL_BYTES), sentinel_uid, sentinel_gid) == SLOT_SENTINEL_BYTES)
    raw_dir = os.lstat(workspace + '/raw-results')
    require(stat.S_ISDIR(raw_dir.st_mode) and raw_dir.st_uid == descriptor['subject_uid']
            and 0 < raw_dir.st_gid < UINT32_MAX and stat.S_IMODE(raw_dir.st_mode) == 0o710)
    if CASE == 'N05':
        require(os.listdir(workspace + '/raw-results') == [])
    account_bytes = ('\t'.join(str(x) for x in (uid,gid,descriptor['subject_uid'],descriptor['subject_gid'],raw_dir.st_gid)) + '\n').encode('ascii')
    values.update({'negative-descriptor.json': descriptor_bytes, 'negative-account-ids.tsv': account_bytes,
                  CASE.lower() + '-worker.stdout': frames['stdout'], CASE.lower() + '-worker.stderr': frames['stderr']})
    require(validate_slot_archive(values, CASE, generation, uid, gid, descriptor['subject_uid'], descriptor['subject_gid']) is True)
    # Preserve actual frame bytes, including their one LF. No serializer/splitter may change them.
    for name in (*SLOT_FRAME_MEMBERS, 'negative-descriptor.json', 'negative-account-ids.tsv',
                 CASE.lower() + '-worker.stdout', CASE.lower() + '-worker.stderr'):
        save(private, name, values[name])

if __name__ == '__main__':
    try:
        body()
    except Exception:
        print('NEGATIVE_RECORDS_REJECTED', file=sys.stderr)
        raise SystemExit(1)
