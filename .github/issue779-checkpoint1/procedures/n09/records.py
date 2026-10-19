"""Original N09 closed archive/projection predicates, current identity supplied by caller.

These are data checks only. The root dispatch and actual numeric terminal are
independently required; no detached record creates a native owner or admission.
"""
import base64, hashlib, io, json, math, os, re, stat, sys, tarfile, types

n09_record_parser = None

SOURCE = '0' * 40

N09_BASE_REVISION = '0' * 40

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

ARCHIVE_NAMES = {'logs/n02-higher.file-limit', 'negative-setup.json', 'logs/n02.credentials.json', 'logs/n02-higher.stderr', 'worker-live.json', 'logs/n02.stderr', 'fixture-stderr.log', 'negative-kernel-observation.json', 'n05-worker.stderr', 'logs/kill.log', 'final-files.sha256', 'fixture-result.json', 'logs/observer.stdout', 'negative-post-join.json', 'fixture-stdout.log', 'logs/n02-higher.credentials.json', 'n02-startup-limit-diagnostic.pending', 'negative-root-terminal.txt', 'raw-evidence-plan.json', 'logs/n02-higher.io.trace', 'logs/n02.stdout', 'n02-startup-limit-diagnostic.json', 'raw-evidence-manifest.json', 'n05-worker.stdout', 'negative-account-ids.tsv', 'logs/n01.stderr', 'logs/n02.io.trace', 'logs/n02-higher.limits', 'logs/n02.facts.json', 'negative-root-failure.json', 'logs/n02-higher.trace', 'raw-evidence-summary.json', 'request-policy.sha256', 'logs/n02-higher.facts.json', 'logs/n02.trace', 'logs/observer.stderr', 'logs/n02.file-limit', 'negative-descriptor.json', 'negative-worker-raw.json', 'negative-worker-projection.json', 'logs/n02-higher.stdout', 'n06-worker.stderr', 'logs/stop.log', 'retention-selection.json', 'logs/n01.stdout', 'n06-worker.stdout', 'logs/n02.limits'}

NEGATIVE_CAPS = {
    'negative-setup.json':1024, 'negative-post-join.json':1024,
    'negative-worker-projection.json':1024, 'negative-kernel-observation.json':4097,
    'negative-worker-raw.json':4097, 'negative-root-failure.json':1024,
    'negative-root-terminal.txt':1024, 'negative-descriptor.json':65536,
    'negative-account-ids.tsv':128,
    'n05-worker.stdout':0, 'n05-worker.stderr':2048,
    'n06-worker.stdout':0, 'n06-worker.stderr':2048,
}

ARCHIVE_NAMES = ARCHIVE_NAMES | set(['cancellation-capture.json', 'filesystem-nss-observation.json', 'joined-streams.json', 'kernel.json', 'logs/n01.stderr', 'logs/n01.stdout', 'root-failure.json', 'signal.json', 'worker-control.json', 'worker.stderr', 'worker.stdout', 'n09-observation.json'])

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
                         and 0<=member.size<=NEGATIVE_CAPS.get(member.name,4096 if member.name in ('fixture-result.json','retention-selection.json') else 8388608))
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
    """Inspect bounded root-retained bytes; decode JSON members and preserve log bytes."""
    raw = inspect_retention(data, ARCHIVE_NAMES)
    selected = ('fixture-result.json', 'n09-observation.json', 'retention-selection.json')
    require_data(all(len(raw[name]) <= 4096 for name in ('fixture-result.json', 'retention-selection.json') if name in raw)
                 and ('logs/n01.stderr' not in raw or len(raw['logs/n01.stderr']) <= 114688))
    values = {name: decode_data(raw[name]) for name in selected if name in raw}
    values['_retained_raw'] = raw
    return values

def validate_native_result(value, expected_base_revision=N09_BASE_REVISION):
    """Validate the exact frozen N09 fixture result; it is consistency data only."""
    fields = {'schema','generation','case','source_revision','base_revision','observation_sha256',
              'root_launch_exit','fixture_processes_joined','fresh_generated_groups_empty',
              'account_disposition','publication','native_acceptance','trusted'}
    require(type(value) is dict and set(value) == fields
            and value['schema'] == 'issue779-n09-retained-slot-fixture-v1'
            and value['case'] == 'N09' and value['source_revision'] == SOURCE
            and re.fullmatch('[0-9a-f]{32}', value['generation']) is not None
            and type(expected_base_revision) is str and re.fullmatch('[0-9a-f]{40}', expected_base_revision) is not None
            and value['base_revision'] == expected_base_revision
            and re.fullmatch('[0-9a-f]{64}', value['observation_sha256']) is not None
            and type(value['root_launch_exit']) is int and value['root_launch_exit'] == 1
            and type(value['fixture_processes_joined']) is bool and value['fixture_processes_joined'] is True
            and type(value['fresh_generated_groups_empty']) is bool and value['fresh_generated_groups_empty'] is True
            and value['account_disposition'] == 'closed-and-nss-absent'
            and type(value['publication']) is bool and value['publication'] is False
            and type(value['native_acceptance']) is bool and value['native_acceptance'] is False
            and type(value['trusted']) is bool and value['trusted'] is False, 'n09-fixture-result')

def validate_n09_observation(values, fixture_result):
    """Bind the fixture's observation digest to its bounded retained archive bytes."""
    raw = values.get('_retained_raw', {}).get('n09-observation.json')
    require(type(raw) is bytes and 0 < len(raw) <= 4096, 'n09-observation-missing-or-bound')
    require(sha(raw) == fixture_result.get('observation_sha256'), 'n09-observation-digest')
    observation = decode(raw)
    fields = {'schema','case','generation','source_revision','base_revision',
              'allocation_record_sha256','cleanup_record_sha256','results_gid',
              'account_ids_absent','worker_cgroup_settled','original_projection_verified',
              'path_comparison','observation_only','native_authority','native_acceptance'}
    require(type(observation) is dict and set(observation) == fields
            and observation['schema'] == 'issue779-n09-retained-slot-observation-v1'
            and observation['case'] == 'N09'
            and observation['generation'] == fixture_result.get('generation')
            and observation['source_revision'] == SOURCE
            and observation['base_revision'] == N09_BASE_REVISION
            and re.fullmatch('[0-9a-f]{64}', observation['allocation_record_sha256']) is not None
            and re.fullmatch('[0-9a-f]{64}', observation['cleanup_record_sha256']) is not None
            and type(observation['results_gid']) is int and observation['results_gid'] > 0
            and observation['account_ids_absent'] is True
            and observation['worker_cgroup_settled'] is True
            and observation['original_projection_verified'] is True
            and observation['observation_only'] is True
            and observation['native_authority'] is False
            and observation['native_acceptance'] is False, 'n09-observation-schema-or-facts')
    path = observation['path_comparison']
    require(type(path) is dict and set(path) == {'schema','parent_identity_matches','slot_identity_matches','slot_empty','native_authority','native_acceptance'}
            and path['schema'] == 'issue779-n09-post-settlement-path-comparison-v1'
            and path['parent_identity_matches'] is True and path['slot_identity_matches'] is True
            and path['slot_empty'] is True and path['native_authority'] is False
            and path['native_acceptance'] is False, 'n09-observation-path-comparison')
    return True

def validate_negative_archive(values):
    """Check retained N09 observation bytes and the seven-line root projection."""
    require(n09_record_parser is not None and 'fixture-result.json' in values
            and 'logs/n01.stderr' in values['_retained_raw'], 'n09-archive-records')
    result = values['fixture-result.json']
    validate_native_result(result, expected_base_revision=N09_BASE_REVISION)
    validate_n09_observation(values, result)
    parsed = n09_record_parser.split_root_stderr(values['_retained_raw']['logs/n01.stderr'],
                                                  generation=result['generation'])
    cleanup = parsed['cleanup_record']
    # Bind summary facts to the actual retained source-owned record bytes, including LF.
    # The parser has already required exactly seven newline-terminated records.
    records = values['_retained_raw']['logs/n01.stderr'].splitlines(keepends=True)
    observation = values['n09-observation.json']
    require(observation['allocation_record_sha256'] == sha(records[0])
            and observation['cleanup_record_sha256'] == sha(records[5])
            and observation['results_gid'] == cleanup['results_gid'], 'n09-observation-record-binding')
    require(cleanup['generation'] == result['generation']
            and cleanup['schema'] == 'issue779-cancellation-root-cleanup-v1'
            and cleanup['accounts_closed'] is True and cleanup['root_custody_closed'] is True
            and cleanup['original_owners_closed'] is True and cleanup['native_authority'] is False
            and cleanup['native_acceptance'] is False, 'n09-cleanup-record')
    return {'n09_seven_line_projection_valid': True, 'results_gid': cleanup['results_gid'],
            'native_authority': False, 'native_acceptance': False}


def validate(raw, *, source, base, entry_sha, parser):
    """Check actual retained members with the fixed current source/base/parser."""
    global SOURCE, N09_BASE_REVISION, CANDIDATE_ENTRY_SHA, n09_record_parser
    SOURCE, N09_BASE_REVISION, CANDIDATE_ENTRY_SHA = source, base, entry_sha
    n09_record_parser = parser
    values = inspect_archive(raw)
    validate_negative_archive(values)
    return values
