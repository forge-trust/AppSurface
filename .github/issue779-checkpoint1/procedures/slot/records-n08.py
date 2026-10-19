"""Historical detached archive predicates; current source/base are bound by the caller.

These functions establish data consistency only, never a native owner or acceptance.
The fixed root procedure and original numeric terminal are independently required.
"""
import hashlib, json, re, io, tarfile, math, os, stat, base64, types, sys
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

NEGATIVE_CAPS = {
    'negative-setup.json':1024, 'negative-post-join.json':1024,
    'negative-worker-projection.json':1024, 'negative-kernel-observation.json':4097,
    'negative-worker-raw.json':4097, 'negative-root-failure.json':1024,
    'negative-root-terminal.txt':1024, 'negative-descriptor.json':65536,
    'negative-account-ids.tsv':128,
    'n05-worker.stdout':0, 'n05-worker.stderr':2048,
    'n06-worker.stdout':0, 'n06-worker.stderr':2048,
}

ARCHIVE_NAMES = ARCHIVE_NAMES | set(['cancellation-root-join.json', 'root-cleanup.json', 'cancellation-capture.json', 'filesystem-nss-observation.json', 'joined-streams.json', 'kernel.json', 'logs/n01.stderr', 'logs/n01.stdout', 'root-failure.json', 'signal.json', 'worker-control.json', 'worker.stderr', 'worker.stdout'])

NEGATIVE_CAPS.update({'cancellation-root-join.json': 1024, 'root-cleanup.json': 1024, 'signal.json': 1024, 'kernel.json': 4097, 'joined-streams.json': 98304, 'root-failure.json': 1024, 'worker.stdout': 0, 'worker.stderr': 65536, 'worker-control.json': 65536, 'filesystem-nss-observation.json': 8192, 'cancellation-capture.json': 8192, 'logs/n01.stdout': 0, 'logs/n01.stderr': 114688})

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
    """Closed fixture result; the entire retained archive must also be verified."""
    require(type(value) is dict and value.get('schema') == 'issue779-cancellation-fixture-v1'
            and value.get('source_revision') == SOURCE and value.get('case') == NEGATIVE_CASE,
            'cancellation-fixture-identity')

def validate_negative_archive(values):
    """Data binding only, after separately checked original native terminal."""
    return cancellation_archive.check_archive(values['_retained_raw'], case=NEGATIVE_CASE,
        source=SOURCE, base=PARENT,
        entry_sha=CANDIDATE_ENTRY_SHA)

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
    SOURCE, PARENT, NEGATIVE_CASE, CANDIDATE_ENTRY_SHA = source, base, 'N08', entry_sha
    values = inspect_archive(raw)
    require('fixture-result.json' in values, 'fixture-result-missing')
    cancellation_archive = modules["check_cancellation_archive"]
    validate_negative_archive(values)
    validate_n03_archive(values)
    return values
