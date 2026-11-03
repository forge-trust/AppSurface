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

def decode(data):
    return json.loads(data.decode('utf-8'), object_pairs_hook=unique)

def sha(data):
    return hashlib.sha256(data).hexdigest()

ARCHIVE_NAMES = {
    'logs/n03/broker.events.jsonl',
    'logs/n03/broker.stderr',
    'logs/n03/worker.stdout',
    'logs/n03/worker.stderr',
    'logs/n03/worker-peer.trace',
    'logs/n03/broker-live.json',
    'logs/n03/broker-live.json.argv',
    'logs/n03/broker-live-after.json',
    'logs/n03/broker-live-after.json.argv',
    'logs/n03/worker-live.json',
    'logs/n03/worker-live.json.argv',
    'logs/n03/result.json',
    'logs/n03/failure.json',

    'retention-selection.json', 'fixture-stdout.log', 'fixture-stderr.log', 'fixture-result.json',
    'raw-evidence-plan.json', 'raw-evidence-manifest.json', 'raw-evidence-summary.json', 'worker-live.json',
    'request-policy.sha256', 'final-files.sha256', 'logs/n01.stdout', 'logs/n01.stderr', 'logs/n02.stdout',
    'logs/n02.stderr', 'logs/n02.trace', 'logs/observer.stdout', 'logs/observer.stderr',
    'logs/n01-observer-terminal.json', 'logs/kill.log', 'logs/stop.log',    'logs/n02.file-limit',
    'logs/n02.limits',
    'logs/n02.facts.json',
    'logs/n02.io.trace',
    'logs/n02.credentials.json',
    'logs/n02-higher.stdout',
    'logs/n02-higher.stderr',
    'logs/n02-higher.trace',
    'logs/n02-higher.file-limit',
    'logs/n02-higher.limits',
    'logs/n02-higher.facts.json',
    'logs/n02-higher.io.trace',
    'logs/n02-higher.credentials.json',
    'n02-startup-limit-diagnostic.pending',
    'n02-startup-limit-diagnostic.json',
}

def inspect_archive(data):
    require(0 < len(data) <= 33619968, 'archive-size')
    values, total, names = {}, 0, []
    with tarfile.open(fileobj=io.BytesIO(data), mode='r:') as archive:
        for member in archive:
            require(member.name in ARCHIVE_NAMES and member.name not in names and member.isfile()
                    and member.mode == 0o600 and member.uid == 0 and member.gid == 0 and member.mtime == 0
                    and not member.pax_headers and not member.linkname and 0 <= member.size <= 8388608,
                    'archive-member')
            if member.name == 'logs/n01-observer-terminal.json':
                require(member.size <= 4096, 'observer-terminal-bound')
            names.append(member.name)
            require(len(names) <= len(ARCHIVE_NAMES), 'archive-count')
            total += member.size
            require(total <= 33554432 + 4096, 'archive-expanded-bound')
            stream = archive.extractfile(member)
            require(stream is not None, 'archive-file')
            with stream:
                content = stream.read(member.size + 1)
            require(len(content) == member.size, 'archive-short-read')
            if member.name in ('fixture-result.json', 'retention-selection.json', 'logs/n03/result.json', 'logs/n03/worker-live.json', 'logs/n03/broker-live.json', 'logs/n03/broker-live-after.json'):
                require(len(content) <= 4096, 'archive-json-size')
                values[member.name] = decode(content)
                if member.name == 'logs/n03/result.json':
                    values['n03-result-sha256'] = sha(content)
    require(names == sorted(names) and 'retention-selection.json' in values, 'archive-order-or-selection')
    selection = values['retention-selection.json']
    require(set(selection) == {'schema', 'selection_status', 'data_file_count', 'data_bytes', 'missing_fixed_file_count'}
            and selection['schema'] == 'issue779-native-retention-selection-v1'
            and selection['selection_status'] in ('parent-absent', 'parent-empty', 'one-namespace')
            and type(selection['data_file_count']) is int and selection['data_file_count'] == len(names) - 1
            and type(selection['data_bytes']) is int and 0 <= selection['data_bytes'] <= 33554432,
            'selection-schema')
    return values

def validate_native_result(value):
    require(set(value) == {'worker_unit', 'base_revision', 'file_inspection', 'n03_exit', 'canonical_verification', 'policy_sha256', 'n01_exit', 'n03_result_sha256', 'trusted_enabled', 'native_controls_executed', 'source_revision', 'generation', 'owned_process_groups_joined', 'schema', 'remaining_thirteen_controls', 'n02_exit', 'owner_unit'}, 'native-case-closed-fields')
    require(value['schema'] == 'issue779-native-n01-n02-n03-fixture-v1' and value['source_revision'] == SOURCE
            and value['base_revision'] == PARENT
            and re.fullmatch('[0-9a-f]{32}', value['generation']) and re.fullmatch('[0-9a-f]{64}', value['policy_sha256'])
            and value['owner_unit'] == 'appsurface-evidence-owner-' + value['generation'] + '.service'
            and value['worker_unit'] == 'appsurface-evidence-worker-' + value['generation'] + '.service'
            and type(value['n01_exit']) is int and value['n01_exit'] == 0
            and type(value['n02_exit']) is int and value['n02_exit'] == 1
            and type(value['n03_exit']) is int and value['n03_exit'] == 0 and re.fullmatch('[0-9a-f]{64}', value['n03_result_sha256']) and value['native_controls_executed'] == ['N02', 'N03', 'N01'] and value['owned_process_groups_joined'] is True
            and value['trusted_enabled'] is False and value['remaining_thirteen_controls'] == 'pending', 'native-case-receipt')

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
    require('logs/n03/result.json' in values, 'n03-result-missing')
    result = values['logs/n03/result.json']
    require_n03_result((json.dumps(result,separators=(',',':'))+'\n').encode(), SOURCE)
    require(values['fixture-result.json']['n03_result_sha256']==values['n03-result-sha256'], 'n03-result-binding')
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
    SOURCE, PARENT, NEGATIVE_CASE, CANDIDATE_ENTRY_SHA = source, base, 'None', entry_sha
    values = inspect_archive(raw)
    require('fixture-result.json' in values, 'fixture-result-missing')
    validate_native_result(values["fixture-result.json"])
    validate_n03_archive(values)
    return values
