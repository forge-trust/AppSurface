"""N16 actual retained-output predicates; only expected current provenance is parameterized."""
import hashlib,json,re
from datetime import datetime

def require(value,category):
    if not value: raise ValueError(category)
def sha(raw): return hashlib.sha256(raw).hexdigest()
def unique(rows):
    value={}
    for key,item in rows:
        require(key not in value,"duplicate-json-key");value[key]=item
    return value
def decode_data(raw): return json.loads(raw,object_pairs_hook=unique)


def validate_fixture_result(value, generation, build_sha, source, capture_sha):
    keys={'schema','generation','source_commit','source_capture_sha256','root_projection_sha256','root_exit','root_stdout_bytes','stderr_sha256','worker_unit','worker_pid','worker_uid','worker_gid','results_gid','owner_load_state','worker_load_state','generated_passwd_group_name_checks','worker_uid_reverse_lookup_exit','worker_gid_reverse_lookup_exit','results_gid_reverse_lookup_exit','root_launch_group_absent','root_launch_fd_directory_absent','workspace_retained_verified','observation_only','native_authority','native_acceptance'}
    require(type(generation) is str and re.fullmatch('[0-9a-f]{32}',generation) is not None and type(build_sha) is str and re.fullmatch('[0-9a-f]{64}',build_sha) is not None, 'n16-fixture-expected-binding')
    require(type(value) is dict and set(value)==keys and value.get('schema')=='issue779-n16-native-fixture-observation-v2' and value.get('generation')==generation and value.get('source_commit')==source and value.get('source_capture_sha256')==capture_sha and value.get('root_projection_sha256')==build_sha, 'n16-fixture-result-shape')
    require(type(value['root_exit']) is int and value['root_exit']==1 and type(value['root_stdout_bytes']) is int and value['root_stdout_bytes']==0 and value['owner_load_state']=='not-found' and value['worker_load_state']=='not-found' and all(value[k] is True for k in ('root_launch_group_absent','root_launch_fd_directory_absent','workspace_retained_verified','observation_only')) and value['native_authority'] is False and value['native_acceptance'] is False, 'n16-fixture-terminal')
    require(re.fullmatch('[0-9a-f]{64}',value['stderr_sha256']) is not None and type(value['worker_unit']) is str and value['worker_unit']=='appsurface-evidence-worker-'+generation+'.service' and type(value['worker_pid']) is int and value['worker_pid']>1, 'n16-fixture-identity')
    require(all(type(value[k]) is int and 0<value[k]<4294967295 for k in ('worker_uid','worker_gid','results_gid')) and value['worker_gid']!=value['results_gid'] and all(type(value[k]) is int and value[k]==2 for k in ('worker_uid_reverse_lookup_exit','worker_gid_reverse_lookup_exit','results_gid_reverse_lookup_exit')) and type(value['generated_passwd_group_name_checks']) is int and value['generated_passwd_group_name_checks']==5, 'n16-fixture-os-observations')

FIXTURE_POLICY = {
    'Id':'checkpoint-empty','Version':'1','ConservativeProfileId':'conservative',
    'Profiles':[{'Id':'empty','Scope':'Targeted','Resources':[],'Producers':[],'Obligations':[]},
        {'Id':'conservative','Scope':'Release','Resources':[{'Id':'build','Readiness':'completion','DeadlineSeconds':1,'Requires':[]}],'Producers':[],'Obligations':[]}],
    'Rules':[{'Id':'docs','Pattern':'docs/**','ProfileId':'empty','Precedence':0}]}

def validate_request_binding(request_raw, checksum_raw, generation, workflow, source, base):
    """Check retained request and both checksum rows as data, never as admission authority."""
    require(type(request_raw) is bytes and 0<len(request_raw)<=65536 and type(checksum_raw) is bytes and 0<len(checksum_raw)<=4096, 'request-policy-bounds')
    request=decode_data(request_raw)
    stage='/var/lib/appsurface-evidence-fixture/'+generation
    expected={'schema':'evidence-supervisor-linux-v1','mode':'observation','tool_root':stage+'/tool',
        'runtime_root':stage+'/runtime','runtime_host':stage+'/runtime/dotnet','entry_path':stage+'/tool/ForgeTrust.AppSurface.Cli.dll',
        'policy_file':stage+'/tool/fixture-policy.json','subject_root':stage+'/subject',
        'base_revision':base,'subject_revision':source,'workflow_identity':workflow,
        'paths':['docs/designs/issue-779-csharp-supervision-core.md'],'observation_profile_ids':['empty'],
        'observation_producer_ids':[],'admission_seconds':10,'start_seconds':30,'collection_seconds':10,
        'cleanup_seconds':60,'stopping_seconds':5}
    require(type(request) is dict and set(request)==set(expected)|{'job_deadline_utc'}, 'request-shape')
    require(all(type(request[k]) is type(v) and request[k]==v for k,v in expected.items()), 'request-binding')
    stamp=request['job_deadline_utc']
    require(type(stamp) is str and re.fullmatch(r'[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z',stamp) is not None, 'request-deadline-shape')
    try: datetime.strptime(stamp,'%Y-%m-%dT%H:%M:%SZ')
    except ValueError: raise ValueError('request-deadline-shape')
    policy_raw=json.dumps(FIXTURE_POLICY,sort_keys=True,separators=(',',':')).encode()
    expected_checksums=(sha(request_raw)+'  /run/appsurface-evidence-fixture/'+generation+'/request.json\n'
        +sha(policy_raw)+'  '+stage+'/tool/fixture-policy.json\n').encode()
    require(checksum_raw==expected_checksums, 'request-policy-binding')

def validate_fixture_output(stdout, stderr, build_artifacts):
    """Bind all twelve audit output rows to the verified same-host build maps.

    These rows are audit observations, not execution authority. Every retained
    record, OS identity, joined stream and cleanup check remains separate.
    """
    require(type(stdout) is bytes and type(stderr) is bytes and stderr == b'',
            'n16-wrapper-output')
    require(type(build_artifacts) is dict and set(build_artifacts) == {'source', 'tool', 'runtime'},
            'n16-wrapper-build-shape')
    counts = {}
    for name in ('source', 'tool', 'runtime'):
        fact = build_artifacts[name]
        require(type(fact) is dict and all(type(fact.get(key)) is int
                and 0 < fact[key] <= maximum for key, maximum in
                (('file_count', 32768), ('node_count', 65536), ('total_bytes', 4294967296))),
                'n16-wrapper-build-values')
        require(fact['node_count'] >= fact['file_count'], 'n16-wrapper-build-nodes')
        counts[name] = (fact['file_count'], fact['node_count'], fact['total_bytes'])
    # The reviewed fixture adds one top-level, canonical policy file to the tool
    # tree before launch. Its node and byte deltas are derived, never wildcarded.
    policy = json.dumps(FIXTURE_POLICY, sort_keys=True, separators=(',', ':')).encode()
    files, nodes, size = counts['tool']
    # The first column counts manifest-bound input files; the added policy is
    # separately admitted by verify_tree and increases only nodes and bytes.
    counts['sealed_tool'] = (files, nodes + 1, size + len(policy))
    sequence = ('source', 'tool', 'runtime', 'sealed_tool', 'runtime', 'source',
                'source', 'tool', 'runtime', 'sealed_tool', 'runtime', 'source')
    rows = ['FULL_TREE_POINT_SAMPLES_MATCHED:%d:%d:%d\n' % counts[name] for name in sequence]
    expected = (''.join(rows) + 'N16_SOURCE_AND_OS_OBSERVATION_TERMINAL:NOT_ACCEPTANCE\n').encode()
    require(stdout == expected, 'n16-wrapper-output')
