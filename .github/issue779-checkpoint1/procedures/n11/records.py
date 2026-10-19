"""Readable original native-record predicates; detached data issues no authority.

Only expected current source/base/entry values are supplied by the outer binding.
The original record/identity/EOF/stop/NSS predicates below are retained verbatim.
"""
import hashlib, io, json, re, tarfile

class Rejected(ValueError):
    pass

def require(value, category):
    if not value:
        raise Rejected(category)

def sha(raw):
    return hashlib.sha256(raw).hexdigest()

def unique(rows):
    result = {}
    for key, value in rows:
        require(key not in result, "duplicate-json")
        result[key] = value
    return result

def decode(raw):
    return json.loads(raw, object_pairs_hook=unique)

ARCHIVE_NAMES = {'logs/n02-higher.file-limit', 'logs/n03/worker.stderr', 'negative-setup.json', 'logs/n02.credentials.json', 'logs/n03/worker-live.json', 'logs/n02-higher.stderr', 'logs/n03/result.json', 'worker-live.json', 'logs/n03/broker.stderr', 'logs/n02.stderr', 'logs/n03/worker.stdout', 'fixture-stderr.log', 'negative-kernel-observation.json', 'n05-worker.stderr', 'logs/kill.log', 'final-files.sha256', 'fixture-result.json', 'logs/observer.stdout', 'negative-post-join.json', 'fixture-stdout.log', 'logs/n02-higher.credentials.json', 'logs/n03/broker-live.json.argv', 'logs/n03/worker-peer.trace', 'n02-startup-limit-diagnostic.pending', 'negative-root-terminal.txt', 'logs/n03/broker.events.jsonl', 'logs/n03/failure.json', 'raw-evidence-plan.json', 'logs/n02-higher.io.trace', 'logs/n02.stdout', 'n02-startup-limit-diagnostic.json', 'raw-evidence-manifest.json', 'n05-worker.stdout', 'negative-account-ids.tsv', 'logs/n03/broker-live.json', 'logs/n01.stderr', 'logs/n02.io.trace', 'logs/n02-higher.limits', 'logs/n02.facts.json', 'logs/n03/broker-live-after.json', 'negative-root-failure.json', 'logs/n02-higher.trace', 'raw-evidence-summary.json', 'request-policy.sha256', 'logs/n03/broker-live-after.json.argv', 'logs/n02-higher.facts.json', 'logs/n02.trace', 'logs/observer.stderr', 'logs/n03/worker-live.json.argv', 'logs/n02.file-limit', 'negative-descriptor.json', 'negative-worker-raw.json', 'negative-worker-projection.json', 'logs/n02-higher.stdout', 'n06-worker.stderr', 'logs/stop.log', 'retention-selection.json', 'logs/n01.stdout', 'n06-worker.stdout', 'logs/n02.limits'}

ARCHIVE_NAMES = ARCHIVE_NAMES | set(['logs/n11-launch-timing.json', 'cancellation-capture.json', 'stall-capture.json', 'stall-capture.stderr', 'original-failed-settlement.json', 'original-request.json', 'root.stderr', 'filesystem-nss-observation.json', 'joined-streams.json', 'kernel.json', 'logs/n01.stderr', 'logs/n01.stdout', 'root-failure.json', 'signal.json', 'worker-control.json', 'worker.stderr', 'worker.stdout'])
NEGATIVE_CAPS = {
    'negative-setup.json':1024, 'negative-post-join.json':1024,
    'negative-worker-projection.json':1024, 'negative-kernel-observation.json':4097,
    'negative-worker-raw.json':4097, 'negative-root-failure.json':1024,
    'negative-root-terminal.txt':1024, 'negative-descriptor.json':65536,
    'negative-account-ids.tsv':128,
    'n05-worker.stdout':0, 'n05-worker.stderr':2048,
    'n06-worker.stdout':0, 'n06-worker.stderr':2048,
}

KEYS = {"asevd402_observed", "broker_exit", "broker_pid", "control", "descriptor_supplied", "kernel_peer_bound_to_owned_worker", "native_acceptance", "owned_process_groups_joined", "ready_request_observed", "request_bytes", "schema", "sent_bytes", "source_commit", "status", "stream_custody", "systemd_worker_authority", "worker_exit", "worker_pid", "worker_stdout_bytes"}
NEGATIVE_CAPS.update({'logs/n11-launch-timing.json': 1024, 'signal.json': 1024, 'kernel.json': 4097, 'joined-streams.json': 98304, 'root-failure.json': 1024, 'worker.stdout': 0, 'worker.stderr': 65536, 'worker-control.json': 65536, 'filesystem-nss-observation.json': 8192, 'cancellation-capture.json': 8192, 'stall-capture.json': 8192, 'stall-capture.stderr': 1024, 'original-failed-settlement.json': 65536, 'original-request.json': 8192, 'root.stderr': 71680, 'logs/n01.stdout': 0, 'logs/n01.stderr': 114688})

def validate(raw, *, source, base, entry_sha, record_checker, stall_capture):
    SOURCE=source
    FIXTURE_BASE_REVISION=base
    CANDIDATE_ENTRY_SHA=entry_sha
    NEGATIVE_CASE="N11"
    n11_record_checker=record_checker
    n11_stall_capture=stall_capture

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
        selected = ('fixture-result.json', 'retention-selection.json', 'stall-capture.json', 'logs/n03/result.json',
                    'logs/n03/worker-live.json', 'logs/n03/broker-live.json', 'logs/n03/broker-live-after.json')
        require_data(all(len(raw[name]) <= (8192 if name == 'stall-capture.json' else 4096) for name in selected if name in raw))
        values = {name: decode_data(raw[name]) for name in selected if name in raw}
        if 'logs/n03/result.json' in raw:
            values['n03-result-sha256'] = sha(raw['logs/n03/result.json'])
        values['_retained_raw'] = raw
        return values

    def validate_native_result(value):
        """Closed N11 result; this is data consistency, not native acceptance."""
        fields = {'schema','generation','case','source_revision','base_revision','capture_sha256',
                  'n03_result_sha256','root_launch_exit','fixture_processes_joined','fresh_generated_groups_empty',
                  'account_disposition','native_acceptance','trusted'}
        require(type(value) is dict and set(value) == fields
                and value['schema'] == 'issue779-n11-stall-fixture-v1'
                and value['source_revision'] == SOURCE and value['base_revision'] == FIXTURE_BASE_REVISION
                and value['case'] == 'N11' and re.fullmatch('[0-9a-f]{32}', value['generation'])
                and type(value['root_launch_exit']) is int and value['root_launch_exit'] == 1
                and value['fixture_processes_joined'] is True and value['fresh_generated_groups_empty'] is True
                and value['account_disposition'] == 'preserved-quarantined'
                and value['native_acceptance'] is False and value['trusted'] is False
                and re.fullmatch('[0-9a-f]{64}', value['capture_sha256'])
                and type(value['n03_result_sha256']) is str
                and re.fullmatch('[0-9a-f]{64}', value['n03_result_sha256']), 'n11-fixture-identity')

    def validate_negative_archive(values):
        """Bind the N11 retained archive as detached data; preserve the failed execution.

        N11 deliberately preserves quarantined accounts. It must never be routed through
        the N08/N09 cancellation validator, which requires removed accounts/root custody.
        Canonical USTAR and member byte caps were checked by inspect_archive before this call.
        """
        require(NEGATIVE_CASE == 'N11', 'negative-archive-case')
        raw = values.get('_retained_raw')
        require(type(raw) is dict and 'fixture-result.json' in raw and 'stall-capture.json' in raw,
                'n11-archive-required-records')
        result = values.get('fixture-result.json')
        validate_native_result(result)
        require(type(raw['stall-capture.json']) is bytes and 0 < len(raw['stall-capture.json']) <= 8192
                and result['capture_sha256'] == sha(raw['stall-capture.json'])
                and decode_data(raw['fixture-result.json']) == result, 'n11-result-capture-digest')
        return {'case': 'N11', 'generation': result['generation'], 'source': SOURCE,
                'capture_digest_matched': True, 'account_disposition': 'preserved-quarantined',
                'native_authority': False, 'native_acceptance': False}

    def validate_n11_capture(values):
        """Recompute the helper's structured consistency and every retained observation."""
        raw = values['_retained_raw']
        capture = values.get('stall-capture.json')
        require(type(capture) is dict, 'n11-capture-missing')
        fields = {'schema','generation','source','entry_sha256','policy_sha256','record_files',
                  'job_deadline_utc','observer_deadline_kind','observer_end_boottime_ms','consistency',
                  'observation_only','native_acceptance','native_authority'}
        result = values['fixture-result.json']
        require(set(capture) == fields and capture['schema'] == 'issue779-n11-private-capture-v1'
                and capture['generation'] == result['generation'] and capture['source'] == SOURCE
                and capture['entry_sha256'] == CANDIDATE_ENTRY_SHA
                and type(capture['policy_sha256']) is str
                and re.fullmatch('[0-9a-f]{64}', capture['policy_sha256'])
                and capture['observer_deadline_kind'] == 'fixture-observer-boottime'
                and type(capture['observer_end_boottime_ms']) is int and capture['observer_end_boottime_ms'] > 0
                and capture['observation_only'] is True
                and capture['native_acceptance'] is False and capture['native_authority'] is False,
                'n11-capture-schema')
        names = {'filesystem-nss-observation.json','original-failed-settlement.json','original-request.json',
                 'root-failure.json','root.stderr','worker-control.json','worker.stderr','worker.stdout'}
        require(type(capture['record_files']) is dict and set(capture['record_files']) == names,
                'n11-record-set')
        for name, fact in capture['record_files'].items():
            require(name in raw and type(raw[name]) is bytes and type(fact) is dict
                    and set(fact) == {'bytes','sha256'} and type(fact['bytes']) is int
                    and fact['bytes'] == len(raw[name]) and fact['sha256'] == sha(raw[name]), 'n11-record-binding')
        request = decode_data(raw['original-request.json'])
        require(type(request) is dict, 'n11-original-request')
        require(n11_record_checker is not None and 'logs/n11-launch-timing.json' in raw,
                'n11-launch-timing-required')
        n11_record_checker.check_launch_timing(raw['logs/n11-launch-timing.json'],
            expected_generation=result['generation'], expected_source=SOURCE,
            expected_request_sha256=sha(raw['original-request.json']),
            expected_job_deadline_utc=request.get('job_deadline_utc'))
        require(n11_stall_capture is not None, 'n11-verified-validator-missing')
        descriptor, checked, original_records = n11_stall_capture.parse_original(
            raw['root.stderr'], raw['worker-control.json'], request=raw['original-request.json'],
            generation=result['generation'], source=SOURCE, base=FIXTURE_BASE_REVISION, entry_sha=CANDIDATE_ENTRY_SHA,
            policy_sha=capture['policy_sha256'], runtime_relative_host='dotnet',
            workflow=request.get('workflow_identity'))
        consistency = capture['consistency']
        if checked.get('schema') == 'issue779-n11-detached-data-consistency-v1':
            # Preserve the historical failed/false-settlement contract verbatim.
            require(type(consistency) is dict and set(consistency) ==
                    {'schema','streams_matched','monitor','physically_settled','stop_timing','native_authority','native_acceptance'}
                    and consistency['schema'] == 'issue779-n11-detached-data-consistency-v1'
                    and consistency['streams_matched'] is True
                    and consistency['monitor'] in ('Faulted','Cancelled')
                    and consistency['physically_settled'] is False
                    and type(consistency['stop_timing']) is dict
                    and set(consistency['stop_timing']) == {'present','proof','schema','missing_fields','clock_origin',
                                                             'bounded_failed_stop','failed_stop_missing_fields'}
                    and type(consistency['stop_timing']['present']) is bool
                    and type(consistency['stop_timing']['proof']) is bool
                    and consistency['stop_timing']['proof'] is False
                    and consistency['stop_timing']['bounded_failed_stop'] is True
                    and type(consistency['stop_timing']['missing_fields']) is list
                    and all(type(name) is str for name in consistency['stop_timing']['missing_fields'])
                    and 'settlement_published_ms' in consistency['stop_timing']['missing_fields']
                    and consistency['stop_timing']['failed_stop_missing_fields'] == []
                    and consistency['stop_timing']['clock_origin'] == 'LinuxWorkerStopTiming.Begin'
                    and (consistency['stop_timing']['schema'] == 'issue779-original-worker-stop-timing-v1'
                         if consistency['stop_timing']['present'] else consistency['stop_timing']['schema'] is None)
                    and consistency['native_authority'] is False and consistency['native_acceptance'] is False
                    and consistency == checked, 'n11-recomputed-consistency')
        elif checked.get('schema') == 'issue779-n11-settled-callback-failure-data-v1':
            settled_record = decode_data(original_records['original-failed-settlement.json'])
            lifetime = settled_record['lifetime']
            timing = settled_record['stop_timing']
            require(type(consistency) is dict and set(consistency) ==
                    {'schema','outcome','callback_failed','streams_matched','monitor','physically_settled',
                     'settlement_published_ms','stop_timing','native_authority','native_acceptance'}
                    and consistency['schema'] == 'issue779-n11-settled-callback-failure-data-v1'
                    and consistency['outcome'] == 'worker lifecycle fail-stop, then root physical settlement'
                    and consistency['callback_failed'] is True
                    and consistency['streams_matched'] is True
                    and consistency['monitor'] == 'Completed'
                    and lifetime['failed'] is False and lifetime['physically_settled'] is True
                    and consistency['physically_settled'] is True
                    and type(consistency['settlement_published_ms']) is int
                    and consistency['settlement_published_ms'] == timing['settlement_published_ms']
                    and type(consistency['stop_timing']) is dict
                    and set(consistency['stop_timing']) == {'present','proof','schema','missing_fields','clock_origin',
                                                             'bounded_failed_stop','failed_stop_missing_fields'}
                    and consistency['stop_timing']['present'] is True
                    and consistency['stop_timing']['proof'] is True
                    and consistency['stop_timing']['bounded_failed_stop'] is True
                    and consistency['stop_timing']['missing_fields'] == []
                    and consistency['stop_timing']['failed_stop_missing_fields'] == []
                    and consistency['stop_timing']['clock_origin'] == 'LinuxWorkerStopTiming.Begin'
                    and consistency['stop_timing']['schema'] == 'issue779-original-worker-stop-timing-v1'
                    and timing['stop_joined_ms'] <= timing['stopping_ms']
                    and timing['stop_joined_ms'] <= timing['job_remaining_ms']
                    and consistency['native_authority'] is False and consistency['native_acceptance'] is False
                    and consistency == checked, 'n11-recomputed-settled-callback-consistency')
        else:
            require(False, 'n11-recomputed-consistency-schema')
        require(all(name in raw and raw[name] == content for name, content in original_records.items()),
                'n11-original-framed-record-binding')
        require(capture['job_deadline_utc'] == request.get('job_deadline_utc'), 'n11-original-deadline-binding')
        observation = decode_data(raw['filesystem-nss-observation.json'])
        observation_keys = {'schema','generation','output_parent_descriptor_identity_match','output_slot_present',
            'raw_results_empty','account_disposition','users','groups','original_worker_pid_absent',
            'original_broker_pid_absent','fresh_generated_worker_group','fresh_generated_owner_group',
            'native_acceptance','native_authority'}
        require(type(observation) is dict and set(observation) == observation_keys
                and observation['schema'] == 'issue779-n11-filesystem-nss-v1'
                and observation['generation'] == result['generation']
                and observation['output_parent_descriptor_identity_match'] is True
                and observation['output_slot_present'] is False and observation['raw_results_empty'] is True
                and observation['account_disposition'] == 'preserved-quarantined'
                and observation['original_worker_pid_absent'] is True and observation['original_broker_pid_absent'] is True
                and observation['native_acceptance'] is False and observation['native_authority'] is False,
                'n11-filesystem-nss-observation')
        for key in ('fresh_generated_worker_group','fresh_generated_owner_group'):
            group = observation[key]
            require(type(group) is dict and set(group) == {'exists','populated','frozen','inode'}
                    and type(group['exists']) is bool, 'n11-observed-group-shape')
            if group['exists']:
                require(group['populated'] is False and group['frozen'] is False
                        and type(group['inode']) is int and 0 < group['inode'] < 2**64,
                        'n11-observed-group-not-empty')
            else:
                require(all(group[k] is None for k in ('populated','frozen','inode')), 'n11-observed-group-absence')
        users, groups = observation['users'], observation['groups']
        require(type(users) is list and len(users) == 2 and type(groups) is list and len(groups) == 3,
                'n11-observed-accounts-count')
        prefix = result['generation'][:28]
        for row, name, role in zip(users, ('evw','evs'), ('worker','subject')):
            require(type(row) is dict and set(row) == {'name','uid','gid','retained'}
                    and row['name'] == name + prefix and row['retained'] is True
                    and type(row['uid']) is int and row['uid'] == descriptor[role+'_uid']
                    and type(row['gid']) is int and row['gid'] == descriptor[role+'_gid'], 'n11-observed-user-binding')
        group_ids = []
        for row, name in zip(groups, ('evw','evs','evr')):
            require(type(row) is dict and set(row) == {'name','gid','retained'}
                    and row['name'] == name + prefix and row['retained'] is True
                    and type(row['gid']) is int and 0 < row['gid'] < 2**32-1, 'n11-observed-group-binding')
            group_ids.append(row['gid'])
        require(group_ids[:2] == [descriptor['worker_gid'],descriptor['subject_gid']]
                and len(set(group_ids)) == 3, 'n11-observed-group-identities')
        return True

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
        require(type(raw) is dict and type(raw.get('logs/n03/result.json')) is bytes
                and 'logs/n03/result.json' in values, 'n03-result-missing')
        original = raw['logs/n03/result.json']
        result = require_n03_result(original, SOURCE)
        require(result == values['logs/n03/result.json']
                and values['fixture-result.json']['n03_result_sha256']
                == values.get('n03-result-sha256') == sha(original), 'n03-result-binding')
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

    def validate_required_archive(values):
        """Validate every required retained control before recording an N11 case result.

        The N03 digest binds the original retained bytes; its independent identity
        checks must succeed alongside the N11 failure/capture checks. This function
        validates detached test data and issues no protected runtime authority.
        """
        validate_native_result(values['fixture-result.json'])
        validate_negative_archive(values)
        validate_n03_archive(values)
        validate_n11_capture(values)

    values=inspect_archive(raw)
    validate_required_archive(values)
    return {"generation":values["fixture-result.json"]["generation"],"native_acceptance":False}
