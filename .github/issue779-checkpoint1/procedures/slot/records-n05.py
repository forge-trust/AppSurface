"""Historical detached archive predicates; current source/base are bound by the caller.

These functions establish data consistency only, never a native owner or acceptance.
The fixed root procedure and original numeric terminal are independently required.
"""
import hashlib, json, re, io, tarfile, math, os, stat, base64, types, sys
_slot_os = os
_slot_os = os

class Rejected(ValueError):
    def __init__(self, category):
        super().__init__('native-test-rejected')
        self.category = category

def require(value, category):
    if not value:
        raise Rejected(category)

def unique(rows):
    result = {}
    for key, value in rows:
        require(key not in result, 'duplicate-json')
        result[key] = value
    return result

def sha(data):
    return hashlib.sha256(data).hexdigest()

ARCHIVE_NAMES = {'logs/n02-higher.file-limit', 'logs/n03/worker.stderr', 'negative-setup.json', 'logs/n02.credentials.json', 'logs/n03/worker-live.json', 'logs/n02-higher.stderr', 'logs/n03/result.json', 'worker-live.json', 'logs/n03/broker.stderr', 'logs/n02.stderr', 'logs/n03/worker.stdout', 'fixture-stderr.log', 'negative-kernel-observation.json', 'n05-worker.stderr', 'logs/kill.log', 'final-files.sha256', 'fixture-result.json', 'logs/observer.stdout', 'negative-post-join.json', 'fixture-stdout.log', 'logs/n02-higher.credentials.json', 'logs/n03/broker-live.json.argv', 'logs/n03/worker-peer.trace', 'n02-startup-limit-diagnostic.pending', 'negative-root-terminal.txt', 'logs/n03/broker.events.jsonl', 'logs/n03/failure.json', 'raw-evidence-plan.json', 'logs/n02-higher.io.trace', 'logs/n02.stdout', 'n02-startup-limit-diagnostic.json', 'raw-evidence-manifest.json', 'n05-worker.stdout', 'negative-account-ids.tsv', 'logs/n03/broker-live.json', 'logs/n01.stderr', 'logs/n02.io.trace', 'logs/n02-higher.limits', 'logs/n02.facts.json', 'logs/n03/broker-live-after.json', 'negative-root-failure.json', 'logs/n02-higher.trace', 'raw-evidence-summary.json', 'request-policy.sha256', 'logs/n03/broker-live-after.json.argv', 'logs/n02-higher.facts.json', 'logs/n02.trace', 'logs/observer.stderr', 'logs/n03/worker-live.json.argv', 'logs/n02.file-limit', 'negative-descriptor.json', 'negative-worker-raw.json', 'negative-worker-projection.json', 'logs/n02-higher.stdout', 'n06-worker.stderr', 'logs/stop.log', 'retention-selection.json', 'logs/n01.stdout', 'n06-worker.stdout', 'logs/n02.limits'}

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

def validate_negative_archive(values):
    """Bind the fixture result, original root stream, slot metadata and kernel record."""
    raw=values['_retained_raw']; result=values['fixture-result.json']
    validate_native_result(result)
    require(result['source_revision']==SOURCE and result['base_revision']==PARENT
            and result['control']==NEGATIVE_CASE and result['root_launch_join_status']==1
            and result['native_acceptance'] is False and result['observation_only'] is True, 'slot-fixture-result-binding')
    parts=tuple(SLOT_FRAME_MEMBERS)
    needed=set(parts)|{'negative-descriptor.json','negative-account-ids.tsv',
        NEGATIVE_CASE.lower()+'-worker.stdout',NEGATIVE_CASE.lower()+'-worker.stderr','logs/n01.stdout','logs/n01.stderr'}
    require(needed <= set(raw), 'slot-private-record-missing')
    frame=b''.join(raw[name] for name in parts)
    require(raw['logs/n01.stdout']==b'' and raw['logs/n01.stderr']==frame, 'slot-original-root-stream-binding')
    descriptor=decode_data(raw['negative-descriptor.json'])
    require(descriptor.get('policy_sha256')==result['policy_sha256'], 'slot-policy-binding')
    require(validate_slot_archive(raw, NEGATIVE_CASE, result['generation'], descriptor['worker_uid'],
        descriptor['worker_gid'], descriptor['subject_uid'], descriptor['subject_gid']) is True, 'slot-archive')

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
    # These negative fixture schemas have no enclosing n03_result_sha256.
    # Bind the actual retained member instead; the full original archive and
    # closed enclosing fixture are independently checked by validate().
    raw = values.get('_retained_raw')
    require(type(raw) is dict and 'logs/n03/result.json' in raw, 'n03-result-missing')
    result_raw = raw['logs/n03/result.json']
    result = require_n03_result(result_raw, SOURCE)
    require(values.get('logs/n03/result.json') == result
            and values.get('n03-result-sha256') == sha(result_raw), 'n03-result-binding')
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


def validate(raw, *, source, base, entry_sha, modules):
    """Apply original detached predicates to actual retained bytes only.

    The fixed adapter loads a fresh module per call; current expected values
    cannot leak between selectors. This function starts no process or owner.
    """
    global SOURCE, PARENT, NEGATIVE_CASE, CANDIDATE_ENTRY_SHA, cancellation_archive
    SOURCE, PARENT, NEGATIVE_CASE, CANDIDATE_ENTRY_SHA = source, base, 'N05', entry_sha
    values = inspect_archive(raw)
    require('fixture-result.json' in values, 'fixture-result-missing')
    validate_negative_archive(values)
    validate_n03_archive(values)
    return values
