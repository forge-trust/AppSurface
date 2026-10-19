"""Original N10 canonical archive and retained-data predicates; no native factory."""
import hashlib,io,json,re,tarfile,types
from datetime import datetime
class Rejected(ValueError):pass
def require(ok,category):
    if not ok:raise Rejected(category)
def unique(rows):
    value={}
    for key,item in rows:
        require(key not in value,"duplicate-json");value[key]=item
    return value
def sha(raw):return hashlib.sha256(raw).hexdigest()


ARCHIVE_CAP = 33619968

ARCHIVE_NAMES = {
 'fixture-stdout.log','fixture-stderr.log','retention-selection.json',
 'logs/n10.stdout','logs/n10.stderr','n10-record-summary.json','n10-fixture-result.json','n10-unit-collision.json',
 'request.json','request-policy.sha256','logs/kill.log','logs/stop.log'}

CAPS={'fixture-stdout.log':8388608,'fixture-stderr.log':8388608,'retention-selection.json':4096,
 'logs/n10.stdout':0,'logs/n10.stderr':114688,'n10-record-summary.json':4096,
 'n10-fixture-result.json':4096,'n10-unit-collision.json':4096,'request.json':65536,'request-policy.sha256':4096,
 'logs/kill.log':8388608,'logs/stop.log':8388608}

def inspect_archive(data):
    require(type(data) is bytes and 0 < len(data) <= ARCHIVE_CAP, 'archive-size')
    values={}; total=0
    try:
        with tarfile.open(fileobj=io.BytesIO(data),mode='r:') as tar:
            members=tar.getmembers()
            require(0 < len(members) <= len(ARCHIVE_NAMES), 'archive-entry-count')
            names=[]; entries=[]
            for member in members:
                name=member.name
                require(name in ARCHIVE_NAMES and name not in values and member.isfile() and member.mode==0o600 and member.uid==member.gid==member.mtime==0 and not member.issym() and not member.islnk() and not member.linkname and not member.pax_headers and not member.uname and not member.gname and member.size<=CAPS[name], 'archive-member')
                names.append(name)
                stream=tar.extractfile(member); require(stream is not None, 'archive-read')
                raw=stream.read(CAPS[name]+1); require(len(raw)==member.size and len(raw)<=CAPS[name], 'archive-member-size')
                total+=len(raw); require(total<=32*1024*1024, 'archive-expanded-size')
                values[name]=raw; entries.append((name,raw))
            require(names==sorted(names), 'archive-member-order')
            canonical=io.BytesIO()
            with tarfile.open(fileobj=canonical,mode='w:',format=tarfile.USTAR_FORMAT) as rebuilt:
                for name,content in entries:
                    info=tarfile.TarInfo(name); info.size=len(content); info.mode=0o600; info.uid=info.gid=info.mtime=0
                    rebuilt.addfile(info,io.BytesIO(content))
            require(canonical.getvalue()==data, 'archive-noncanonical')
    except (tarfile.TarError, OSError, EOFError):
        raise Rejected('archive-invalid')
    require('retention-selection.json' in values, 'retention-selection-missing')
    selection=decode_data(values['retention-selection.json'])
    require(type(selection) is dict and set(selection)=={'schema','selection_status','data_file_count','data_bytes','missing_fixed_file_count'} and selection.get('schema')=='issue779-native-retention-selection-v1', 'retention-selection-shape')
    require(selection['selection_status'] in ('parent-absent','parent-empty','one-namespace') and type(selection['data_file_count']) is int and selection['data_file_count']==len(values)-1 and type(selection['data_bytes']) is int and selection['data_bytes']==sum(len(v) for k,v in values.items() if k!='retention-selection.json') and type(selection['missing_fixed_file_count']) is int and selection['missing_fixed_file_count']==11-selection['data_file_count'], 'retention-selection-values')
    if 'n10-unit-collision.json' in values:
        validate_unit_collision_diagnostic(values['n10-unit-collision.json'])
    return values

def decode_data(raw):
    try: return json.loads(raw.decode('utf-8'), object_pairs_hook=unique)
    except (UnicodeError, json.JSONDecodeError, ValueError): raise Rejected('retained-json')

def validate_unit_collision_diagnostic(raw):
    value=decode_data(raw)
    require(type(value) is dict and set(value)=={'schema','role','load_state','query_exit'} and
        value['schema']=='issue779-n10-unit-collision-v1' and value['role'] in ('owner','worker') and
        value['load_state'] in ('loaded','not-found','error','masked','unknown') and
        type(value['query_exit']) is int and 0<=value['query_exit']<=255,
        'n10-unit-collision-diagnostic')
    return value

def validate_fixture_result(value, generation, build_sha, SOURCE, CAPTURE_SHA):
    keys={"schema","generation","source_commit","source_capture_sha256","root_projection_sha256","root_exit","worker_unit","job_path",
          "worker_pid","worker_uid","worker_gid","worker_starttime_ticks","root_owner_pid","root_owner_starttime_ticks","worker_passwd_lookup_exit","worker_group_lookup_exit","generated_name_checks","generated_names_present","stderr_sha256",
          "pending_start_failure","original_stop_delegate_calls","physically_settled","unit_stopped",
          "final_group_sampled_after_pumps","final_group_empty","pumps_joined","stdout_eof","stderr_eof",
          "discarded_bytes","staging_path_retained","private_path_retained","root_custody_completed",
          "quarantined_workspace_retained","owner_terminal_verified","worker_terminal_verified",
          "native_authority","native_acceptance"}
    require(type(generation) is str and re.fullmatch("[0-9a-f]{32}",generation) is not None and
            type(build_sha) is str and re.fullmatch("[0-9a-f]{64}",build_sha) is not None,"n10-fixture-expected-binding")
    require(type(value) is dict and set(value)==keys and value.get("schema")=="issue779-n10-native-fixture-observation-v1" and
            value.get("generation")==generation and value.get("source_commit")==SOURCE and
            value.get("source_capture_sha256")==CAPTURE_SHA and value.get("root_projection_sha256")==build_sha and type(value.get("stderr_sha256")) is str and
            re.fullmatch("[0-9a-f]{64}",value["stderr_sha256"]) is not None,"n10-fixture-result-shape")
    require(type(value["root_exit"]) is int and value["root_exit"]==1 and
            value["worker_unit"]=="appsurface-evidence-worker-"+generation+".service" and
            type(value["job_path"]) is str and value["job_path"].startswith("/org/freedesktop/systemd1/job/") and
            type(value["worker_pid"]) is int and value["worker_pid"]>1 and type(value["worker_starttime_ticks"]) is int and value["worker_starttime_ticks"]>0 and
            type(value["root_owner_pid"]) is int and value["root_owner_pid"]>1 and type(value["root_owner_starttime_ticks"]) is int and value["root_owner_starttime_ticks"]>0 and
            all(type(value[k]) is int and 0<value[k]<4294967295 for k in ("worker_uid","worker_gid")) and
            value["worker_passwd_lookup_exit"] in (0,2) and value["worker_group_lookup_exit"] in (0,2) and value["generated_name_checks"]==5 and type(value["generated_names_present"]) is int and 0<=value["generated_names_present"]<=5,
            "n10-fixture-live-identity")
    require(value["pending_start_failure"]=="StartCancelled" and value["original_stop_delegate_calls"]==2 and
            value["physically_settled"] is False and value["unit_stopped"] is True and
            value["final_group_sampled_after_pumps"] is True and value["final_group_empty"] is True and
            value["pumps_joined"] is True and value["stdout_eof"] is True and value["stderr_eof"] is True and
            value["discarded_bytes"]==0 and value["root_custody_completed"] is False and
            value["native_authority"] is False and value["native_acceptance"] is False,
            "n10-original-join-and-failure")
    require(type(value["staging_path_retained"]) is bool and type(value["private_path_retained"]) is bool and
            type(value["quarantined_workspace_retained"]) is bool and
            value["owner_terminal_verified"] is True and value["worker_terminal_verified"] is True,
            "n10-quarantine-disposition")

FIXTURE_POLICY = {
    'Id':'checkpoint-empty','Version':'1','ConservativeProfileId':'conservative',
    'Profiles':[{'Id':'empty','Scope':'Targeted','Resources':[],'Producers':[],'Obligations':[]},
        {'Id':'conservative','Scope':'Release','Resources':[{'Id':'build','Readiness':'completion','DeadlineSeconds':1,'Requires':[]}],'Producers':[],'Obligations':[]}],
    'Rules':[{'Id':'docs','Pattern':'docs/**','ProfileId':'empty','Precedence':0}]}

def validate_request_binding(request_raw, checksum_raw, generation, workflow, SOURCE, SOURCE_PARENT):
    """Check retained request and both checksum rows as data, never as admission authority."""
    require(type(request_raw) is bytes and 0<len(request_raw)<=65536 and type(checksum_raw) is bytes and 0<len(checksum_raw)<=4096, 'request-policy-bounds')
    request=decode_data(request_raw)
    base='/var/lib/appsurface-evidence-fixture/'+generation
    expected={'schema':'evidence-supervisor-linux-v1','mode':'observation','tool_root':base+'/tool',
        'runtime_root':base+'/runtime','runtime_host':base+'/runtime/dotnet','entry_path':base+'/tool/ForgeTrust.AppSurface.Cli.dll',
        'policy_file':base+'/tool/fixture-policy.json','subject_root':base+'/subject',
        'base_revision':SOURCE_PARENT,'subject_revision':SOURCE,'workflow_identity':workflow,
        'paths':['docs/designs/issue-779-csharp-supervision-core.md'],'observation_profile_ids':['empty'],
        'observation_producer_ids':[],'admission_seconds':10,'start_seconds':30,'collection_seconds':10,
        'cleanup_seconds':60,'stopping_seconds':5}
    require(type(request) is dict and set(request)==set(expected)|{'job_deadline_utc'}, 'request-shape')
    require(all(type(request[k]) is type(v) and request[k]==v for k,v in expected.items()), 'request-binding')
    stamp=request['job_deadline_utc']
    require(type(stamp) is str and re.fullmatch(r'[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z',stamp) is not None, 'request-deadline-shape')
    try: datetime.strptime(stamp,'%Y-%m-%dT%H:%M:%SZ')
    except ValueError: raise Rejected('request-deadline-shape')
    policy_raw=json.dumps(FIXTURE_POLICY,sort_keys=True,separators=(',',':')).encode()
    expected_checksums=(sha(request_raw)+'  /run/appsurface-evidence-fixture/'+generation+'/request.json\n'
        +sha(policy_raw)+'  '+base+'/tool/fixture-policy.json\n').encode()
    require(checksum_raw==expected_checksums, 'request-policy-binding')

def validate_fixture_stdout(raw, artifacts):
    """Bind each fixed audit progress line to the reviewed build inventories.

    The fixture checks three original trees, three sealed trees, the three
    originals again, and the three sealed trees after the pending-start control.
    The sealed tool adds exactly the fixed canonical fixture policy. These
    diagnostic lines provide no runtime identity, join, or custody authority.
    """
    require(type(raw) is bytes and type(artifacts) is dict and
            set(artifacts) == {'source', 'tool', 'runtime'}, 'n10-wrapper-output')
    counts = {}
    for name in ('source', 'tool', 'runtime'):
        row = artifacts[name]
        require(type(row) is dict and all(type(row.get(key)) is int and row[key] > 0
                for key in ('file_count', 'node_count', 'total_bytes')),
                'n10-wrapper-inventory')
        counts[name] = (row['file_count'], row['node_count'], row['total_bytes'])
    policy_bytes = len(json.dumps(FIXTURE_POLICY, sort_keys=True, separators=(',', ':')).encode())
    def line(name, with_policy=False):
        files, nodes, size = counts[name]
        return ('FULL_TREE_POINT_SAMPLES_MATCHED:%d:%d:%d\n' %
                (files, nodes + int(with_policy), size + (policy_bytes if with_policy else 0))).encode()
    original = line('source') + line('tool') + line('runtime')
    sealed = line('tool', True) + line('runtime') + line('source')
    expected = original + sealed + original + sealed + b'N10_SOURCE_AND_OS_OBSERVATION_TERMINAL:NOT_ACCEPTANCE\n'
    require(raw == expected, 'n10-wrapper-output')

def validate_n10_archive(values, parser_bytes, generation, build_sha, workflow, artifacts, SOURCE, SOURCE_PARENT, CAPTURE_SHA, PARSER_SHA):
    required=("fixture-stdout.log","fixture-stderr.log","retention-selection.json","logs/n10.stdout",
              "logs/n10.stderr","n10-record-summary.json","n10-fixture-result.json","request.json","request-policy.sha256")
    require(all(name in values for name in required),"n10-retained-required-files")
    require(values["logs/n10.stdout"]==b"","n10-worker-stdout")
    result=decode_data(values["n10-fixture-result.json"]); validate_fixture_result(result,generation,build_sha,SOURCE,CAPTURE_SHA)
    validate_fixture_stdout(values["fixture-stdout.log"], artifacts)
    require(values["fixture-stderr.log"] == b"", "n10-wrapper-output")
    require(type(parser_bytes) is bytes and sha(parser_bytes)==PARSER_SHA,"parser-pin")
    parser=types.ModuleType("n10_pinned_parser")
    exec(compile(parser_bytes,"<pinned-N10-record-parser>","exec",dont_inherit=True),parser.__dict__)
    summary=parser.validate_bytes(values["logs/n10.stderr"],"joined")
    retained_summary=decode_data(values["n10-record-summary.json"])
    require(retained_summary==summary and summary.get("stderr_sha256")==result["stderr_sha256"] and
            summary.get("generation")==generation and summary.get("worker_unit")==result["worker_unit"] and
            summary.get("job_path")==result["job_path"],"n10-record-summary-binding")
    validate_request_binding(values["request.json"],values["request-policy.sha256"],generation,workflow,SOURCE,SOURCE_PARENT)
    return result,summary,values["fixture-stdout.log"]
