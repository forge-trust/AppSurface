"""Original N15 retained archive predicates with explicit current source metadata."""
import hashlib,io,json,re,tarfile,types
from datetime import datetime
class Rejected(ValueError):pass
def require(ok,category):
    if not ok:raise Rejected(category)
def unique(rows):
    value={}
    for k,v in rows:
        require(k not in value,"duplicate-json-key");value[k]=v
    return value
def sha(raw):return hashlib.sha256(raw).hexdigest()


import time
PARSER_SHA='d06806ea5248709c96e8d1cce0f6b97b1ae4029296609669e1cb88f3c6ec9fb3'


ARCHIVE_CAP = 33619968

DISPATCH = r'''
set -euo pipefail; umask 077
# This is the existing audited payload-file envelope, not the per-log budget.
ulimit -f 262144
fixture_stdout_pid=; fixture_stderr_pid=
fixture_stdout_fifo=; fixture_stderr_fifo=
fixture_stdout_fifo_owned=0; fixture_stderr_fifo_owned=0
finish_fixture_capture() {
 local status=$1 pump_status=0 pid
 trap - EXIT
 for pid in "$fixture_stdout_pid" "$fixture_stderr_pid"; do
  [[ -n $pid ]] || continue
  pump_status=0; wait "$pid" || pump_status=$?
  if ((status==0 && pump_status!=0)); then status=$pump_status; fi
 done
 if ((fixture_stdout_fifo_owned)); then
  rm -f -- "$fixture_stdout_fifo" || { ((status!=0)) || status=1; }
 fi
 if ((fixture_stderr_fifo_owned)); then
  rm -f -- "$fixture_stderr_fifo" || { ((status!=0)) || status=1; }
 fi
 # The prearmed root utility timer owns this entire join, including partial
 # setup. A missing/failed pump or any earlier fixture failure cannot pass.
 exit "$status"
}
capture_fixture() {
 local stdout=$1 stderr=$2 fixture_status=0
 shift 2
 fixture_stdout_fifo=$stdout.pipe; fixture_stderr_fifo=$stderr.pipe
 trap 'finish_fixture_capture "$?"' EXIT
 # Each FIFO is created exclusively in the fixed root-owned private control
 # directory. Retain every ordinary background child immediately for wait.
 mkfifo -m 600 -- "$fixture_stdout_fifo"; fixture_stdout_fifo_owned=1
 mkfifo -m 600 -- "$fixture_stderr_fifo"; fixture_stderr_fifo_owned=1
 (trap - EXIT; ulimit -f 8192; ulimit -c 0; set -C; exec cat <"$fixture_stdout_fifo" >"$stdout") &
 fixture_stdout_pid=$!
 (trap - EXIT; ulimit -f 8192; ulimit -c 0; set -C; exec cat <"$fixture_stderr_fifo" >"$stderr") &
 fixture_stderr_pid=$!
 "$@" >"$fixture_stdout_fifo" 2>"$fixture_stderr_fifo" || fixture_status=$?
 finish_fixture_capture "$fixture_status"
}
[[ $1 =~ ^[0-9a-f]{32}$ ]]; g=$1; hash=$2; shift 2
root=/var/lib/appsurface-evidence-input-$g
[[ ! -L /run/appsurface-evidence-fixture && ! -L /var/lib/appsurface-evidence-fixture ]]
[[ $(/usr/bin/stat -c '%u:%g:%a' -- /run/appsurface-evidence-fixture) == 0:0:755 ]]
[[ $(/usr/bin/stat -c '%u:%g:%a' -- /run/appsurface-evidence-fixture/$g) == 0:0:700 ]]
[[ $(/usr/bin/stat -c '%u:%g:%a' -- /var/lib/appsurface-evidence-fixture/$g) == 0:0:555 ]]
[[ $(/usr/bin/stat -c '%u:%g:%a' -- "$root/control") == 0:0:700 ]]
/usr/bin/jq -ncS --arg g "$g" '{schema:"issue779-native-dispatch-marker-v2",generation:$g,fixture_parent_prepared:true}' >"$root/control/fixture-dispatch-marker.json"
printf '%s  %s\n' "$hash" "$root/reviewed/checkpoint-n15-pending-start-death.sh" | /usr/bin/sha256sum --check --strict --status
capture_fixture "$root/control/fixture-stdout.log" "$root/control/fixture-stderr.log" /usr/bin/bash --noprofile --norc "$root/reviewed/checkpoint-n15-pending-start-death.sh" "$@"
/usr/bin/cat -- "$root/control/fixture-stdout.log"
'''

ARCHIVE_NAMES = {
 'fixture-stdout.log','fixture-stderr.log','retention-selection.json',
 'logs/n15.stdout','logs/n15.stderr','partial.json','reactivation-measurement.json','retry.stdout','retry.stderr',
 'owner-launch.stdout','worker-before.txt','worker-after.txt','owner-before.txt','retry-owner.txt','account-cleanup.json',
 'n15-final-record.json','n15-record-summary.json',
 'request.json','request-policy.sha256','logs/kill.log','logs/stop.log'}

CAPS={'fixture-stdout.log':8388608,'fixture-stderr.log':8388608,'retention-selection.json':4096,
 'logs/n15.stdout':65536,'logs/n15.stderr':114688,'partial.json':65536,'reactivation-measurement.json':4096,
 'retry.stdout':65536,'retry.stderr':114688,'owner-launch.stdout':65536,'worker-before.txt':16384,'worker-after.txt':16384,
 'owner-before.txt':16384,'retry-owner.txt':16384,'account-cleanup.json':65536,
 'n15-final-record.json':65536,'n15-record-summary.json':4096,
 'request.json':65536,'request-policy.sha256':4096,
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
    require(selection['selection_status'] in ('parent-absent','parent-empty','one-namespace') and type(selection['data_file_count']) is int and selection['data_file_count']==len(values)-1 and type(selection['data_bytes']) is int and selection['data_bytes']==sum(len(v) for k,v in values.items() if k!='retention-selection.json') and type(selection['missing_fixed_file_count']) is int and selection['missing_fixed_file_count']==len(ARCHIVE_NAMES)-1-selection['data_file_count'], 'retention-selection-values')
    return values

def decode_data(raw):
    try: return json.loads(raw.decode('utf-8'), object_pairs_hook=unique)
    except (UnicodeError, json.JSONDecodeError, ValueError): raise Rejected('retained-json')

def validate_cleanup_receipt(cleanup, partial, measurement, raw, generation, SOURCE, CAPTURE_SHA):
    keys={'schema','generation','source_commit','source_capture_sha256','partial_sha256','measurement_sha256',
          'names','ids','workspace','joined_launcher_pid','launcher_absent_after_wait','worker_before_after_sha256',
          'retry_owner_terminal_sha256','retry_stdout_sha256','retry_stderr_sha256','owner_before_sha256',
          'worker_snapshot_unchanged','worker_snapshot_kind','worker_after_sha256','retry_owner_terminal','utilities',
          'numeric_and_name_lookup_absent','native_credit'}
    require(type(cleanup) is dict and set(cleanup)==keys and cleanup['schema']=='issue779-n15-root-account-cleanup-v1'
            and cleanup['generation']==generation and cleanup['source_commit']==SOURCE
            and cleanup['source_capture_sha256']==CAPTURE_SHA and cleanup['partial_sha256']==sha(raw['partial.json'])
            and cleanup['measurement_sha256']==sha(raw['reactivation-measurement.json']), 'n15-cleanup-binding')
    accounts=partial['account_snapshot']; require(cleanup['names']==accounts['names'] and cleanup['ids']==accounts['ids'], 'n15-cleanup-accounts')
    require(type(cleanup['workspace']) is dict and set(cleanup['workspace'])=={'device','inode','uid','gid','mode'}
            and type(cleanup['workspace']['device']) is int and cleanup['workspace']['device']>=0
            and type(cleanup['workspace']['inode']) is int and cleanup['workspace']['inode']>0
            and cleanup['workspace']['uid']==cleanup['workspace']['gid']==0 and cleanup['workspace']['mode']==0o700,
            'n15-cleanup-workspace')
    require(type(cleanup['joined_launcher_pid']) is int and cleanup['joined_launcher_pid']>1
            and cleanup['launcher_absent_after_wait'] is True
            and type(cleanup['worker_snapshot_unchanged']) is bool and cleanup['worker_snapshot_unchanged']==(raw['worker-before.txt']==raw['worker-after.txt'])
            and cleanup['worker_snapshot_kind'] in ('unchanged','garbage-collected') and cleanup['retry_owner_terminal']=={
                'LoadState':'loaded','ActiveState':'failed','SubState':'failed','ExecMainCode':'1','ExecMainStatus':'1'}
            and cleanup['numeric_and_name_lookup_absent'] is True and cleanup['native_credit'] is False,
            'n15-cleanup-observations')
    digest_fields={'worker_after_sha256':'worker_after_snapshot_sha256','worker_before_after_sha256':'worker_before_after_snapshot_sha256',
                   'retry_owner_terminal_sha256':'retry_owner_terminal_snapshot_sha256',
                   'retry_stdout_sha256':'retry_stdout_sha256','retry_stderr_sha256':'retry_stderr_sha256'}
    for target,source in digest_fields.items():
        require(cleanup[target]==measurement[source] and sha(raw['worker-before.txt'])==measurement['worker_before_after_snapshot_sha256']
                and sha(raw['worker-after.txt'])==measurement['worker_after_snapshot_sha256']
                and sha(raw['retry-owner.txt'])==measurement['retry_owner_terminal_snapshot_sha256']
                and sha(raw['retry.stdout'])==measurement['retry_stdout_sha256']
                and sha(raw['retry.stderr'])==measurement['retry_stderr_sha256'], 'n15-cleanup-raw-binding')
    require(type(cleanup['owner_before_sha256']) is str and re.fullmatch('[0-9a-f]{64}',cleanup['owner_before_sha256'])
            and sha(raw['owner-before.txt'])==cleanup['owner_before_sha256'], 'n15-cleanup-owner-before')
    expected_names=[accounts['names']['worker'],accounts['names']['subject'],accounts['names']['worker'],
                    accounts['names']['subject'],accounts['names']['results']]
    require(type(cleanup['utilities']) is list and len(cleanup['utilities'])==5, 'n15-cleanup-utility-count')
    seen=[]
    for i,row in enumerate(cleanup['utilities']):
        require(type(row) is dict and row.get('name') in expected_names, 'n15-cleanup-utility-name')
        utility_key=(row.get('utility'),row['name'])
        require(utility_key not in seen, 'n15-cleanup-utility-duplicate')
        seen.append(utility_key)
        require(row.get('utility')==('userdel' if i<2 else 'groupdel'), 'n15-cleanup-utility-kind')
        if row.get('already_absent') is True:
            require(row['utility']=='groupdel' and row.get('exit') is None, 'n15-cleanup-absence-row')
        else:
            require(set(row)=={'utility','name','exit'} and type(row['exit']) is int and row['exit']==0, 'n15-cleanup-utility-exit')
    require(seen[:2]==[('userdel',expected_names[0]),('userdel',expected_names[1])], 'n15-cleanup-user-order')
    require(set(seen)=={('userdel',expected_names[0]),('userdel',expected_names[1]),
                        ('groupdel',expected_names[2]),('groupdel',expected_names[3]),
                        ('groupdel',expected_names[4])}, 'n15-cleanup-complete-coverage')
    return True

FIXTURE_POLICY = {
    'Id':'checkpoint-empty','Version':'1','ConservativeProfileId':'conservative',
    'Profiles':[{'Id':'empty','Scope':'Targeted','Resources':[],'Producers':[],'Obligations':[]},
        {'Id':'conservative','Scope':'Release','Resources':[{'Id':'build','Readiness':'completion','DeadlineSeconds':1,'Requires':[]}],'Producers':[],'Obligations':[]}],
    'Rules':[{'Id':'docs','Pattern':'docs/**','ProfileId':'empty','Precedence':0}]}

def validate_request_binding(request_raw, checksum_raw, generation, workflow, expected_deadline_utc, SOURCE, SOURCE_PARENT):
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
    require(stamp==expected_deadline_utc, 'request-original-deadline-binding')
    policy_raw=json.dumps(FIXTURE_POLICY,sort_keys=True,separators=(',',':')).encode()
    expected_checksums=(sha(request_raw)+'  /run/appsurface-evidence-fixture/'+generation+'/request.json\n'
        +sha(policy_raw)+'  '+base+'/tool/fixture-policy.json\n').encode()
    require(checksum_raw==expected_checksums, 'request-policy-binding')

def validate_n15_archive(values, parser_bytes, generation, build_sha, workflow, deadline, original_deadline_utc, SOURCE, CAPTURE_SHA, SOURCE_PARENT):
    needed=('fixture-stdout.log','fixture-stderr.log','retention-selection.json','logs/n15.stdout','logs/n15.stderr','owner-launch.stdout',
        'partial.json','reactivation-measurement.json','retry.stdout','retry.stderr','worker-before.txt','worker-after.txt',
        'owner-before.txt','retry-owner.txt','account-cleanup.json','n15-final-record.json','n15-record-summary.json',
        'request.json','request-policy.sha256')
    require(all(name in values for name in needed), 'n15-retained-required-files')
    require(values['logs/n15.stdout']==b'' and values['fixture-stderr.log']==b'', 'n15-worker-or-wrapper-stderr')
    wrapper=re.fullmatch(rb'N15_CSHARP_OWNER_DISPATCHED\nN15_FINAL_RECORD_MEASURED:NO_NATIVE_CREDIT\nN15_OWNER_LAUNCH_JOIN_MEASURED:launch=([0-9]{1,3}):observer=0:stdout=([0-9]{1,6}):phase_stderr=([1-9][0-9]{0,4})\n',values['fixture-stdout.log'])
    require(wrapper is not None and int(wrapper.group(1))<=255 and int(wrapper.group(2))==len(values['owner-launch.stdout'])
            and int(wrapper.group(3))==len(values['logs/n15.stderr']), 'n15-wrapper-output')
    require(type(parser_bytes) is bytes and sha(parser_bytes)==PARSER_SHA, 'parser-pin')
    parser=types.ModuleType('n15_pinned_parser'); exec(compile(parser_bytes,'<pinned-N15-record-parser>','exec',dont_inherit=True),parser.__dict__)
    partial=decode_data(values['partial.json']); parser.validate_partial_observation(partial,generation)
    measurement=decode_data(values['reactivation-measurement.json'])
    measurement_keys={'schema','generation','owner_unit','worker_unit','retry_exit','retry_stdout_sha256','retry_stderr_sha256',
        'worker_before_after_snapshot_sha256','worker_after_snapshot_sha256','retry_owner_terminal_snapshot_sha256','raw_logs_retained','synthetic_completion_flags'}
    require(type(measurement) is dict and set(measurement)==measurement_keys and measurement['schema']=='issue779-n15-guard-reactivation-measurement-v1'
        and measurement['generation']==generation and measurement['owner_unit']==partial['owner']['unit']
        and measurement['worker_unit']==partial['worker']['unit'] and type(measurement['retry_exit']) is int
        and measurement['retry_exit']==1 and measurement['raw_logs_retained'] is True and measurement['synthetic_completion_flags'] is False,
        'n15-reactivation-measurement')
    result=decode_data(values['n15-final-record.json']); summary=parser.validate_result(result,generation,source_commit=SOURCE,capture_sha256=CAPTURE_SHA)
    phase=parser.validate_phase(values['logs/n15.stderr'],generation)
    require(phase['phase_sha256']==result['phase_sha256'] and phase['job_path']==result['job_path']
            and phase['unit']==result['worker']['unit'], 'n15-phase-record-binding')
    completion={'schema':parser.COMPLETION_SCHEMA,'case_generation':generation,'source_commit':SOURCE,
        'source_capture_sha256':CAPTURE_SHA,'reactivation':result['reactivation']}
    parser.validate_completion_evidence(completion,generation,source_commit=SOURCE,capture_sha256=CAPTURE_SHA)
    require(all(result[k]==partial[k] for k in (parser.PARTIAL_KEYS & parser.FINAL_KEYS) - {'schema'}), 'n15-partial-final-binding')
    cleanup=decode_data(values['account-cleanup.json'])
    worker_snapshot_kind=parser.validate_worker_replay_snapshots(values['worker-before.txt'],values['worker-after.txt'],generation,partial['worker']['main_pid'])
    require(cleanup['worker_snapshot_kind']==worker_snapshot_kind, 'n15-worker-snapshot-kind')
    validate_cleanup_receipt(cleanup,partial,measurement,values,generation,SOURCE,CAPTURE_SHA)
    retained_summary=decode_data(values['n15-record-summary.json'])
    require(retained_summary==summary and summary.get('case_generation')==generation and summary.get('phase_sha256')==phase['phase_sha256'], 'n15-record-summary-binding')
    validate_request_binding(values['request.json'],values['request-policy.sha256'],generation,workflow,original_deadline_utc,SOURCE,SOURCE_PARENT)
    return result,summary,values.get('fixture-stdout.log',b'')

def fixture_arguments(root, generation, job_deadline_boot_ms):
    """Build the fixed source-owned owner launch/observer ABI under original deadlines."""
    require(re.fullmatch('[0-9a-f]{32}', generation) is not None, 'n15-generation')
    boot_now = time.clock_gettime_ns(time.CLOCK_BOOTTIME) // 1_000_000
    require(type(job_deadline_boot_ms) is int and job_deadline_boot_ms > boot_now,
            'n15-original-job-deadline-expired')
    job_deadline = job_deadline_boot_ms
    work_deadline = job_deadline - 60_000
    startup_deadline = min(work_deadline, boot_now + 30_000)
    # Legacy observer ABI name: this barrier holds StartAsync before the server
    # and its real Admission10 interval exist. Share the ORIGINAL Start30 bound.
    # No new clock sample, renewed timer, request change or extra allowance.
    admission_deadline = startup_deadline
    require(boot_now < admission_deadline <= work_deadline
            and boot_now < startup_deadline <= work_deadline < job_deadline,
            'n15-original-deadline-window')
    request = f'/run/appsurface-evidence-fixture/{generation}/request.json'
    result_dir = f'/run/appsurface-evidence-fixture/{generation}'
    phase = result_dir + '/logs/n15.stderr'
    partial = result_dir + '/partial.json'
    root_input = f'/var/lib/appsurface-evidence-fixture/{generation}'
    return ['--execute', '--case-generation', generation, '--request', request,
        '--runtime-host', root_input + '/runtime/dotnet', '--entry',
        root_input + '/tool/ForgeTrust.AppSurface.Cli.dll', '--phase-stderr', phase,
        '--partial-record', partial, '--result-directory', result_dir,
        '--startup-deadline-boot-ms', str(startup_deadline),
        '--admission-deadline-boot-ms', str(admission_deadline),
        '--job-deadline-boot-ms', str(job_deadline), '--work-deadline-boot-ms',
        str(work_deadline), '--root-input-root', root_input]
