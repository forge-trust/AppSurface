"""Historical detached archive predicates; current source/base are bound by the caller.

These functions establish data consistency only, never a native owner or acceptance.
The fixed root procedure and original numeric terminal are independently required.
"""
import hashlib, json, re, io, tarfile, math, os, stat, base64, types, sys
N07_RECEIPT_SCHEMA = 'issue779-csharp-n07-run-v1'

N07_CASE_PHASE = 'actual-csharp-n07'

class Rejected(ValueError):
    def __init__(self, category):
        super().__init__('native-test-rejected')
        self.category = category

def require(value, category):
    if not value:
        raise Rejected(category)

def validate_n07_case_binding(receipt):
    """Validate N07 receipt identity and record shape; this grants no native pass."""
    require(type(receipt) is dict, 'n07-receipt-object')
    require(receipt.get('schema') == N07_RECEIPT_SCHEMA
            and receipt.get('case') == NEGATIVE_CASE == 'N07'
            and receipt.get('case_phase') == N07_CASE_PHASE, 'n07-receipt-case-binding')
    require(type(receipt.get('exit')) is int and receipt['exit'] in (0, 1), 'n07-receipt-exit-type')
    require(type(receipt.get('native_dispatched')) is bool
            and type(receipt.get('native_case_receipt_verified')) is bool, 'n07-receipt-status-type')
    require(not receipt['native_case_receipt_verified'] or receipt['native_dispatched'],
            'n07-receipt-status-order')
    commands = receipt.get('commands')
    require(type(commands) is list, 'n07-command-list')
    phases = []
    for ordinal, row in enumerate(commands):
        require(type(row) is dict and type(row.get('ordinal')) is int
                and row['ordinal'] == ordinal and type(row.get('phase')) is str,
                'n07-command-ordinal-or-phase')
        phases.append(row['phase'])
    require('actual-csharp-n01-n02' not in phases, 'n07-legacy-phase')
    require(phases.count(N07_CASE_PHASE) == (1 if receipt['native_dispatched'] else 0),
            'n07-dispatch-phase-binding')
    return True

def unique(rows):
    result = {}
    for key, value in rows:
        require(key not in result, 'duplicate-json')
        result[key] = value
    return result

def sha(data):
    return hashlib.sha256(data).hexdigest()

ARCHIVE_NAMES = {'logs/n03/broker-live-after.json', 'n07-ordinary-failure.json', 'logs/n03/broker-live.json.argv', 'n07-worker.stderr', 'logs/n02-higher.credentials.json', 'n07-worker-unit.txt', 'n07-consistency.json', 'n07-account-ids.tsv', 'fixture-stderr.log', 'logs/n03/worker-live.json.argv', 'logs/n02.file-limit', 'n07-helper-result.json', 'logs/n03/worker-peer.trace', 'logs/n02.stderr', 'n07-descriptor.json', 'logs/n03/failure.json', 'logs/n02.limits', 'logs/n03/broker.events.jsonl', 'logs/n02-higher.stderr', 'n02-startup-limit-diagnostic.pending', 'logs/n02-higher.file-limit', 'n07-root-failure.json', 'retention-selection.json', 'worker-live.json', 'logs/n03/result.json', 'logs/n01.stdout', 'logs/n02.credentials.json', 'n07-helper-unit.txt', 'n02-startup-limit-diagnostic.json', 'logs/n03/broker-live-after.json.argv', 'logs/kill.log', 'logs/stop.log', 'raw-evidence-plan.json', 'logs/n02.facts.json', 'logs/n03/broker.stderr', 'logs/n02-higher.trace', 'logs/n02.stdout', 'logs/observer.stderr', 'n07-worker.stdout', 'fixture-stdout.log', 'logs/n02-higher.facts.json', 'n07-helper-live.json', 'logs/n07-helper.stderr', 'logs/n02-higher.io.trace', 'raw-evidence-manifest.json', 'logs/n03/worker-live.json', 'logs/n03/worker.stdout', 'fixture-result.json', 'final-files.sha256', 'raw-evidence-summary.json', 'logs/n02-higher.limits', 'logs/n02.io.trace', 'logs/observer.stdout', 'n07-precleanup-frame.json', 'logs/n03/worker.stderr', 'logs/n02-higher.stdout', 'n07-parent-postjoin.json', 'request-policy.sha256', 'n07-worker-raw.json', 'n07-root-terminal.txt', 'logs/n02.trace', 'logs/n01.stderr', 'logs/n07-helper.stdout', 'logs/n03/broker-live.json'}

N03_REQUIRED_ARCHIVE_NAMES = frozenset((
    'logs/n03/result.json', 'logs/n03/broker.events.jsonl', 'logs/n03/worker-peer.trace',
    'logs/n03/worker-live.json', 'logs/n03/broker-live.json', 'logs/n03/broker-live-after.json',
    'logs/n03/worker.stdout', 'logs/n03/worker.stderr', 'logs/n03/broker.stderr',
))

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

def inspect_retention(data,allowed):
    """Canonical bounded USTAR whitelist inspection only, not a native result verifier."""
    require_data(type(data) is bytes and 0<len(data)<=33619968)
    names=[];total=0;values={};entries=[]
    with tarfile.open(fileobj=io.BytesIO(data),mode='r:') as archive:
        for member in archive:
            require_data(member.name in allowed and member.name not in names and member.isfile()
                         and member.mode==0o600 and member.uid==member.gid==member.mtime==0
                         and not member.linkname and not member.pax_headers and not member.uname and not member.gname
                         and 0<=member.size<=NEGATIVE_CAPS.get(member.name,4096 if member.name in ('fixture-result.json','retention-selection.json','logs/n03/result.json','logs/n03/worker-live.json','logs/n03/broker-live.json','logs/n03/broker-live-after.json') else 8388608))
            names.append(member.name);total+=member.size;require_data(total<=33554432+4096 and len(names)<=len(allowed))
            stream=archive.extractfile(member);require_data(stream is not None)
            with stream: content=stream.read(member.size+1)
            require_data(len(content)==member.size);values[member.name]=content;entries.append((member.name,content))
    require_data(names==sorted(names) and 'retention-selection.json' in values)
    selection=decode_data(values['retention-selection.json'])
    exact_object(selection,('schema','selection_status','data_file_count','data_bytes','missing_fixed_file_count'))
    require_data(selection['schema']=='issue779-native-retention-selection-v1'
                 and selection['selection_status'] in ('parent-absent','parent-empty','one-namespace')
                 and type(selection['data_file_count']) is int and selection['data_file_count']==len(names)-1
                 and type(selection['data_bytes']) is int and selection['data_bytes']==total-len(values['retention-selection.json'])
                 and type(selection['missing_fixed_file_count']) is int and selection['missing_fixed_file_count']>=0)
    canonical=io.BytesIO()
    with tarfile.open(fileobj=canonical,mode='w:',format=tarfile.USTAR_FORMAT) as rebuilt:
        for name,content in entries:
            info=tarfile.TarInfo(name);info.size=len(content);info.mode=0o600;info.uid=info.gid=info.mtime=0
            rebuilt.addfile(info,io.BytesIO(content))
    require_data(canonical.getvalue()==data)
    return values

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

NEGATIVE_CAPS.update({'n07-worker-raw.json': 98304, 'n07-root-failure.json': 6145, 'n07-ordinary-failure.json': 1025, 'n07-root-terminal.txt': 1024, 'n07-descriptor.json': 65536, 'n07-account-ids.tsv': 128, 'n07-worker.stdout': 0, 'n07-worker.stderr': 65536, 'n07-precleanup-frame.json': 1025, 'n07-helper-result.json': 4096, 'n07-helper-live.json': 2048, 'n07-parent-postjoin.json': 4096, 'n07-consistency.json': 4096, 'logs/n07-helper.stdout': 4096, 'logs/n07-helper.stderr': 8388608, 'logs/n01.stderr': 114688, 'logs/n01.stdout': 0, 'n07-worker-unit.txt': 65536, 'n07-helper-unit.txt': 65536,
    'logs/n03/result.json': 4096, 'logs/n03/broker.events.jsonl': 4096,
    'logs/n03/worker-peer.trace': 1048576, 'logs/n03/worker-live.json': 4096,
    'logs/n03/broker-live.json': 4096, 'logs/n03/broker-live-after.json': 4096,
    'logs/n03/worker.stdout': 0, 'logs/n03/worker.stderr': 1024, 'logs/n03/broker.stderr': 0})

def inspect_archive(data):
    """Inspect bounded root-retained canonical bytes; raw data remains private."""
    raw = inspect_retention(data, ARCHIVE_NAMES)
    selected = ('fixture-result.json', 'retention-selection.json', 'logs/n03/result.json',
                'logs/n03/worker-live.json', 'logs/n03/broker-live.json', 'logs/n03/broker-live-after.json')
    require_data(all(len(raw[name]) <= 4096 for name in selected if name in raw))
    values = {name: decode_data(raw[name]) for name in selected if name in raw}
    if 'logs/n03/result.json' in raw:
        values['n03-result-sha256'] = sha(raw['logs/n03/result.json'])
    values['_retained_raw'] = raw
    return values

def validate_native_result(value):
    """Require the fixed negative result; it grants no positive custody or Trusted status."""
    fields = {'schema', 'generation', 'source_revision', 'base_revision', 'control',
              'root_launch_join_status', 'root_stdout_bytes', 'worker_terminal_from_original_monitor',
              'original_csharp_joins_required', 'account_disposition', 'native_acceptance',
              'observation_only', 'policy_sha256', 'other_controls'}
    require(type(value) is dict and set(value) == fields, 'negative-case-closed-fields')
    require(value['schema'] == 'issue779-negative-slot-fixture-v1'
            and value['source_revision'] == SOURCE and value['control'] == NEGATIVE_CASE
            and value['base_revision'] == PARENT
            and type(value['generation']) is str and re.fullmatch('[0-9a-f]{32}', value['generation'])
            and value['generation'] != '0' * 32
            and type(value['policy_sha256']) is str and re.fullmatch('[0-9a-f]{64}', value['policy_sha256'])
            and type(value['root_launch_join_status']) is int and value['root_launch_join_status'] == 1
            and type(value['root_stdout_bytes']) is int and value['root_stdout_bytes'] == 0
            and value['worker_terminal_from_original_monitor'] is True
            and value['original_csharp_joins_required'] is True
            and value['account_disposition'] == 'preserved-quarantined'
            and value['native_acceptance'] is False and value['observation_only'] is True
            and value['other_controls'] == 'not-qualified-by-this-fixture', 'negative-case-result')

def validate_negative_archive(values):
    """Bind actual captured worker bytes to the original-holder record and sealed descriptor.

    This detached parser cannot authenticate actors. Main additionally requires the
    actual fixture exit0, source-built fixed image, OS/root custody checks and joined
    external invocation. Original starttime comes from the original C# holder; no
    post-exit PID observation or caller metadata is presented as fresh kernel proof.
    """
    raw = values['_retained_raw']
    result = values['fixture-result.json']
    validate_native_result(result)
    parts = ('negative-setup.json', 'negative-post-join.json', 'negative-worker-projection.json',
             'negative-kernel-observation.json', 'negative-worker-raw.json',
             'negative-root-failure.json', 'negative-root-terminal.txt')
    needed = set(parts) | {'negative-descriptor.json', 'negative-account-ids.tsv',
                          NEGATIVE_CASE.lower() + '-worker.stdout', NEGATIVE_CASE.lower() + '-worker.stderr',
                          'logs/n01.stdout', 'logs/n01.stderr'}
    require(needed <= set(raw), 'negative-private-record-missing')
    frame = b''.join(raw[name] for name in parts)
    require(raw['logs/n01.stdout'] == b'' and raw['logs/n01.stderr'] == frame, 'negative-root-stream-binding')
    parsed = validate_fixture_frames(frame, NEGATIVE_CASE)
    stdout, stderr = parsed['stdout'], parsed['stderr']
    require(raw[NEGATIVE_CASE.lower() + '-worker.stdout'] == stdout
            and raw[NEGATIVE_CASE.lower() + '-worker.stderr'] == stderr, 'negative-full-worker-stream-binding')
    other = 'n06' if NEGATIVE_CASE == 'N05' else 'n05'
    require(other + '-worker.stdout' not in raw and other + '-worker.stderr' not in raw, 'negative-other-case-stream')
    descriptor_raw = raw['negative-descriptor.json']
    descriptor = decode_data(descriptor_raw)
    generation = result['generation']
    require(type(descriptor) is dict and descriptor.get('schema') == 'evidence-worker-linux-v1'
            and descriptor.get('run_id') == 'csharp/' + generation
            and descriptor.get('unit') == 'appsurface-evidence-worker-' + generation + '.service'
            and descriptor.get('policy_sha256') == result['policy_sha256'], 'negative-descriptor-case-binding')
    for field in ('worker_uid', 'worker_gid', 'subject_uid', 'subject_gid'):
        require(type(descriptor.get(field)) is int and 0 < descriptor[field] < 4294967295, 'negative-descriptor-identity')
    require(type(descriptor.get('worker_pid')) is int and 0 < descriptor['worker_pid'] <= 2147483647, 'negative-descriptor-pid')
    observed = parsed['kernel']
    check_kernel_record(raw['negative-kernel-observation.json'], expected_generation=generation,
        expected_uid=descriptor['worker_uid'], expected_gid=descriptor['worker_gid'],
        expected_pid=descriptor['worker_pid'], expected_starttime_ticks=observed['process']['starttime_ticks'],
        expected_descriptor_sha256=sha(descriptor_raw), expected_stdout_sha256=sha(stdout),
        expected_stdout_bytes=len(stdout), expected_stderr_sha256=sha(stderr), expected_stderr_bytes=len(stderr))
    ids = raw['negative-account-ids.tsv']
    require(re.fullmatch(rb'[1-9][0-9]*\t[1-9][0-9]*\t[1-9][0-9]*\t[1-9][0-9]*\t[1-9][0-9]*\n', ids) is not None,
            'negative-account-row')
    numbers = [int(x) for x in ids[:-1].split(b'\t')]
    require(numbers[:4] == [descriptor[k] for k in ('worker_uid', 'worker_gid', 'subject_uid', 'subject_gid')]
            and all(x < 4294967295 for x in numbers) and numbers[0] != numbers[2]
            and len(set((numbers[1], numbers[3], numbers[4]))) == 3, 'negative-account-binding')

KEYS = {"asevd402_observed", "broker_exit", "broker_pid", "control", "descriptor_supplied", "kernel_peer_bound_to_owned_worker", "native_acceptance", "owned_process_groups_joined", "ready_request_observed", "request_bytes", "schema", "sent_bytes", "source_commit", "status", "stream_custody", "systemd_worker_authority", "worker_exit", "worker_pid", "worker_stdout_bytes"}

def require_n03_result(raw, source):
    def reject():
        raise ValueError("n03-closed-data-rejected")
    def unique(rows):
        result = {}
        for key, value in rows:
            if key in result: reject()
            result[key] = value
        return result
    def invalid_constant(value):
        reject()
    if not isinstance(raw, bytes) or not 0 < len(raw) <= 4096 or raw[-1:] != b"\n": reject()
    value = json.loads(raw, object_pairs_hook=unique, parse_constant=invalid_constant)
    if not isinstance(value, dict) or set(value) != KEYS: reject()
    expected = {"schema":"issue779-n03-peer-observation-v1", "control":"N03", "status":"observed", "source_commit":source, "worker_exit":1, "broker_exit":0, "worker_stdout_bytes":0, "asevd402_observed":True, "kernel_peer_bound_to_owned_worker":True, "request_bytes":0, "sent_bytes":0, "ready_request_observed":False, "descriptor_supplied":False, "owned_process_groups_joined":True, "native_acceptance":False, "systemd_worker_authority":False, "stream_custody":"private-files-after-original-process-reap"}
    if any(type(value[k]) is not type(v) or value[k] != v for k,v in expected.items()): reject()
    if any(type(value[k]) is not int or not 0 < value[k] <= 2147483647 for k in ("worker_pid", "broker_pid")): reject()
    if value["worker_pid"] == value["broker_pid"]: reject()
    # Caller must independently require fixture numeric0, exact three case receipt,
    # authenticated archive/custody, raw peer trace and live identity bindings.
    return value

def validate_n03_archive(values):
    raw = values.get('_retained_raw')
    require(type(raw) is dict and N03_REQUIRED_ARCHIVE_NAMES <= set(raw), 'n03-required-archive-members')
    result_raw = raw['logs/n03/result.json']
    result = require_n03_result(result_raw, SOURCE)
    require(type(values.get('logs/n03/result.json')) is dict
            and values['logs/n03/result.json'] == result
            and values.get('n03-result-sha256') == sha(result_raw), 'n03-result-binding')
    require(type(values.get('fixture-result.json')) is dict
            and values['fixture-result.json'].get('n03_result_sha256') == sha(result_raw),
            'n03-fixture-result-binding')
    fields={'schema','pid','start_ticks','uid4','gid4','cgroup','exact_managed_argv','image_pinned','empty_groups','no_new_privs','effective_capabilities_zero'}
    for name,pid in [('worker-live.json',result['worker_pid']),('broker-live.json',result['broker_pid']),('broker-live-after.json',result['broker_pid'])]:
        row=values.get('logs/n03/'+name)
        require(type(row) is dict and set(row)==fields and row['schema']=='issue779-n03-owned-image-sample-v1' and type(row['pid']) is int and row['pid']==pid and type(row['start_ticks']) is str and re.fullmatch('[1-9][0-9]*',row['start_ticks']) and row['exact_managed_argv'] is True and row['image_pinned'] is True and row['empty_groups'] is True and type(row['no_new_privs']) is int and row['no_new_privs']==1 and row['effective_capabilities_zero'] is True, 'n03-live-binding')
        for field in ('uid4','gid4'):
            parts=row[field].split(':');require(len(parts)==4 and all(re.fullmatch('[1-9][0-9]*',x) for x in parts) and len(set(parts))==1,'n03-credentials')
        require(re.fullmatch(r'0::/system.slice/issue779-native-tool-[0-9a-f]{32}-[0-9]+\.service',row['cgroup']), 'n03-cgroup')
    require(values['logs/n03/broker-live.json']==values['logs/n03/broker-live-after.json'], 'n03-broker-continuity')
    worker=values['logs/n03/worker-live.json'];broker=values['logs/n03/broker-live.json']
    require(worker['uid4']!=broker['uid4'] and worker['gid4']==broker['gid4']=='65534:65534:65534:65534' and worker['cgroup']==broker['cgroup'], 'n03-selection-continuity')
    def uint(value):
        require(type(value) is int and 0 < value <= 2147483647, 'n03-event-integer')
        return value
    def unique(rows):
        obj={}
        for key,value in rows:
            require(key not in obj, 'n03-event-duplicate-key')
            obj[key]=value
        return obj
    def invalid_constant(value):
        raise ValueError('n03-event-invalid-constant')
    event_raw=raw['logs/n03/broker.events.jsonl']
    require(type(event_raw) is bytes and 0 < len(event_raw) <= 4096 and event_raw.endswith(b'\n'), 'n03-events-bounds')
    lines=event_raw.splitlines(keepends=True)
    require(len(lines)==4 and all(line.endswith(b'\n') and b'\r' not in line for line in lines), 'n03-events-lines')
    events=[]
    for line in lines:
        event=json.loads(line[:-1].decode('ascii'), object_pairs_hook=unique, parse_constant=invalid_constant)
        require(type(event) is dict, 'n03-event-object')
        events.append(event)
    expected_keys=(
        {'broker_gid','broker_pid','broker_start_ticks','broker_uid','schema','stage'},
        {'identity_kind','peer_gid','peer_pid','peer_uid','schema','stage'},
        {'peer_pid','request_bytes','schema','sent_bytes','stage'},
        {'native_acceptance','peer_pid','request_bytes','schema','sent_bytes','status'},
    )
    require(tuple(set(event) for event in events)==expected_keys, 'n03-event-closed-fields')
    worker_pid=uint(result['worker_pid']); broker_pid=uint(result['broker_pid'])
    worker_uid=int(worker['uid4'].split(':')[0]); worker_gid=int(worker['gid4'].split(':')[0])
    broker_uid=int(broker['uid4'].split(':')[0]); broker_gid=int(broker['gid4'].split(':')[0])
    ready,peer,eof,terminal=events
    def event_is(actual, expected):
        return set(actual)==set(expected) and all(type(actual[k]) is type(v) and actual[k]==v
                                                  for k,v in expected.items())
    require(event_is(ready, {'schema':'issue779-n03-managed-broker-event-v1','stage':'ready',
                             'broker_pid':broker_pid,'broker_uid':broker_uid,'broker_gid':broker_gid,
                             'broker_start_ticks':int(broker['start_ticks'])}), 'n03-ready-event-binding')
    require(event_is(peer, {'schema':'issue779-n03-managed-broker-event-v1','stage':'peer',
                            'identity_kind':'kernel-so-peercred-sample','peer_pid':worker_pid,
                            'peer_uid':worker_uid,'peer_gid':worker_gid}), 'n03-peer-event-binding')
    require(event_is(eof, {'schema':'issue779-n03-managed-broker-event-v1','stage':'eof',
                           'peer_pid':worker_pid,'request_bytes':0,'sent_bytes':0})
            and event_is(terminal, {'schema':'issue779-n03-managed-broker-result-v1','status':'observed',
                                    'peer_pid':worker_pid,'request_bytes':0,'sent_bytes':0,
                                    'native_acceptance':False}), 'n03-zero-operation-events')
    trace=raw['logs/n03/worker-peer.trace']
    require(type(trace) is bytes and 0 < len(trace) <= 1048576, 'n03-peer-trace-bounds')
    peer_pattern=(rb'getsockopt\([0-9]+, SOL_SOCKET, SO_PEERCRED, \{pid=%d, uid=%d, gid=%d\}, \[12\]\)\s+= 0'
                  % (broker_pid,broker_uid,broker_gid))
    require(re.search(peer_pattern, trace) is not None, 'n03-peer-trace-binding')
    require(raw['logs/n03/worker.stdout']==b'' and raw['logs/n03/broker.stderr']==b''
            and 0 < len(raw['logs/n03/worker.stderr']) <= 1024
            and b'ASEVD402:' in raw['logs/n03/worker.stderr'], 'n03-stream-evidence')


def validate(raw, *, source, base, entry_sha, modules):
    """Apply original detached predicates to actual retained bytes only.

    The fixed adapter loads a fresh module per call; current expected values
    cannot leak between selectors. This function starts no process or owner.
    """
    global SOURCE, PARENT, NEGATIVE_CASE, CANDIDATE_ENTRY_SHA, cancellation_archive
    SOURCE, PARENT, NEGATIVE_CASE, CANDIDATE_ENTRY_SHA = source, base, 'N07', entry_sha
    values = inspect_archive(raw)
    require('fixture-result.json' in values, 'fixture-result-missing')
    modules["n07-archive-data"].validate_n07_archive(values, records=modules["n07-record-data"], expected_source=source, expected_base=base)
    validate_n03_archive(values)
    return values
