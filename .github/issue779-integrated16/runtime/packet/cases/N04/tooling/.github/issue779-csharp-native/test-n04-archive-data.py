"""DEFINED ONLY: detached DATA controls; no USTAR reader, helper, proc/NSS or native invocation."""
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('n04_adapter_data', Path(__file__).with_name('n04-archive-data.py'))
adapter = importlib.util.module_from_spec(spec)
spec.loader.exec_module(adapter)
data = adapter.record_data

def encode(value):return json.dumps(value,separators=(',',':'),sort_keys=True).encode()+b'\n'

def fixture_data():
    g='1'*32;worker=321;broker=123;helper=222;uid=999;gid=987
    unit='appsurface-evidence-worker-'+g+'.service'
    descriptor={key:None for key in data.DESCRIPTOR_KEYS}
    descriptor.update(schema='evidence-worker-linux-v1',run_id='csharp/'+g,worker_pid=worker,broker_pid=broker,
        worker_uid=uid,worker_gid=gid,subject_uid=998,subject_gid=986,unit=unit,cgroup='/system.slice/'+unit,
        descriptor_path='/run/appsurface-evidence-'+g+'/worker/worker-control.json',
        socket_path='/run/appsurface-evidence-'+g+'/worker/broker/control.sock',
        output_parent='/run/appsurface-evidence-'+g+'/output',output_slot='evidence',
        test_output_root='/run/appsurface-evidence-'+g+'/raw-results',provider='github-actions',platform='linux-x64',proof_digest='')
    dr=encode(descriptor)
    helper_data=dict(schema='issue779-n04-root-coordinator-result-v2',status='observed',generation=g,
        owner_pid=broker,owner_start_ticks=101,owner_uid4=[0]*4,owner_gid4=[0]*4,owner_groups=[],
        worker_pid=worker,worker_start_ticks=202,worker_uid4=[uid]*4,worker_gid4=[gid]*4,worker_groups=[gid],
        worker_stop_observed=True,ready_committed_notification=True,replacement_peer_tuple_matched=True,
        replacement_request_bytes=0,replacement_received_bytes=0,peer_live_identity_rechecked=True,
        replacement_write_shutdown=True,native_authority=False)
    live=dict(schema='issue779-n04-helper-live-v2',pid=helper,starttime_ticks=303,uid4=[0]*4,gid4=[0]*4,groups=[],
        control_group='/system.slice/appsurface-evidence-n04-coordinator-'+g+'.service',
        native_executable_matches=True,managed_argv_matches=True,native_authority=False,
        no_new_privileges=1,cap_inheritable=0,cap_permitted=524333,cap_effective=524333,cap_bounding=524333,cap_ambient=0)
    def stream(raw):return dict(received_bytes=len(raw),retained_bytes=len(raw),discarded_bytes=0,eof=True,
        failure='None',sha256=hashlib.sha256(raw).hexdigest())
    kernel=dict(schema='issue779-negative-kernel-observation-v1',generation=g,worker_unit=unit,
        process=dict(pid=worker,starttime_ticks=202,uid4=[uid]*4,gid4=[gid]*4,control_group='/system.slice/'+unit),
        ready=dict(committed=True,descriptor_sha256=hashlib.sha256(dr).hexdigest()),
        terminal=dict(exec_main_pid=worker,exec_main_code=1,exec_main_status=1,active_state='failed',sub_state='failed'),
        cgroup=dict(exists=False,populated=None,frozen=None,device_major=None,device_minor=None,inode=None),
        pumps=dict(stdout=stream(b''),stderr=stream(data.WORKER_402),received_bytes=len(data.WORKER_402),
            received_byte_limit=1048576,failure='None',discarded_bytes=0),
        joins=dict(startup=True,pending_stop=True,monitor=True,server=True,pumps=True),observation_only=True,
        native_authority=False,native_acceptance=False)
    failure=dict(schema='evidence-native-observation-failure-v4',phase='ServerRun',error_kind='Admission',
        diagnostic_code='ASEVD410',account_failure=None,control_failure=None,custody_failure=None)
    return dict(g=g,descriptor=descriptor,helper=helper_data,live=live,kernel=kernel,failure=failure)

POLICY = 'b' * 64
REQUEST = 'a' * 64

def archive_data():
    """One fully valid detached neighbor; copied parser test facts are NOT observed native facts."""
    v = fixture_data()
    v['descriptor']['policy_sha256'] = POLICY
    descriptor = encode(v['descriptor'])
    v['kernel']['ready']['descriptor_sha256'] = hashlib.sha256(descriptor).hexdigest()
    root = encode(v['kernel']) + encode(v['failure']) + data.ROOT_TERMINAL
    helper = encode(v['helper'])
    consistency = {
        'schema':'issue779-n04-detached-consistency-v1', 'control':'N04', 'generation':v['g'],
        'worker_pid':321, 'worker_starttime_ticks':202, 'replacement_pid':222, 'original_broker_pid':123,
        'descriptor_sha256':hashlib.sha256(descriptor).hexdigest(), 'request_sha256':REQUEST,
        'worker_full_output_matches_fixed402_via_original_holder':True,
        'independent_raw_worker_capture':False, 'observation_only':True,
        'native_authority':False, 'native_acceptance':False,
    }
    result = {
        'schema':'issue779-n04-peer-fixture-v1', 'generation':v['g'],
        'source_revision':adapter.SOURCE, 'base_revision':adapter.BASE, 'control':'N04',
        'root_launch_join_status':1, 'helper_launch_join_status':0, 'root_stdout_bytes':0,
        'account_disposition':'preserved-quarantined', 'native_acceptance':False,
        'observation_only':True, 'policy_sha256':POLICY, 'independent_raw_worker_capture':False,
        'other_controls':'not-qualified-by-this-fixture',
    }
    raw = {
        'fixture-result.json':encode(result), 'logs/n01.stderr':root, 'logs/n01.stdout':b'',
        'logs/n04-helper.stdout':helper, 'logs/n04-helper.stderr':b'',
        'n04-account-ids.tsv':b'999\t987\t998\t986\t985\n',
        'n04-consistency.json':encode(consistency), 'n04-descriptor.json':descriptor,
        'n04-helper-live.json':encode(v['live']), 'n04-helper-result.json':helper,
        'n04-kernel-observation.json':encode(v['kernel']),
        'n04-root-failure.json':encode(v['failure']), 'n04-root-terminal.txt':data.ROOT_TERMINAL,
    }
    return {'_retained_raw':raw, **{name:json.loads(raw[name]) for name in adapter.PARSED_NAMES}}

def replace_json(values, name, value):
    values[name] = value
    values['_retained_raw'][name] = encode(value)

class N04ArchiveDataControls(unittest.TestCase):
    def reject(self, values):
        with self.assertRaisesRegex(ValueError, r'^N04 canonical archive data rejected\.$') as caught:
            adapter.validate_n04_archive(values)
        self.assertIsNone(caught.exception.__cause__)

    def test_complete_valid_neighbor_is_observation_only(self):
        result = adapter.validate_n04_archive(archive_data())
        self.assertEqual(result['selected_members'], 13)
        self.assertFalse(result['native_authority']); self.assertFalse(result['native_acceptance'])
        self.assertTrue(result['observation_only']); self.assertFalse(result['independent_raw_worker_capture'])

    def test_each_fixture_member_required_and_extra_closed(self):
        for key in adapter.FIXTURE_KEYS:
            with self.subTest(key=key):
                v=archive_data(); result=dict(v['fixture-result.json']); del result[key]
                replace_json(v,'fixture-result.json',result); self.reject(v)
        v=archive_data();result=dict(v['fixture-result.json']);result['CANARY']='secret'
        replace_json(v,'fixture-result.json',result);self.reject(v)

    def test_single_fixture_field_changes_reject(self):
        changes={'schema':'other','generation':'0'*32,'source_revision':'0'*40,'base_revision':'0'*40,
                 'control':'N05','root_launch_join_status':0,'helper_launch_join_status':1,
                 'root_stdout_bytes':1,'account_disposition':'absent','native_acceptance':True,
                 'observation_only':False,'policy_sha256':'bad','independent_raw_worker_capture':True,
                 'other_controls':'all-qualified'}
        for key,value in changes.items():
            with self.subTest(key=key):
                v=archive_data();result=dict(v['fixture-result.json']);result[key]=value
                replace_json(v,'fixture-result.json',result);self.reject(v)
        for key in ('root_launch_join_status','helper_launch_join_status','root_stdout_bytes'):
            v=archive_data();result=dict(v['fixture-result.json']);result[key]=bool(result[key])
            replace_json(v,'fixture-result.json',result);self.reject(v)

    def test_duplicate_and_case_duplicate_json_reject(self):
        for key in ('control','CONTROL'):
            v=archive_data();raw=v['_retained_raw']['fixture-result.json']
            v['_retained_raw']['fixture-result.json']=raw[:-2]+b',"'+key.encode()+b'":"N04"}\n'
            self.reject(v)

    def test_every_selected_member_is_mandatory(self):
        for name in adapter.CAPS:
            with self.subTest(name=name):
                v=archive_data();del v['_retained_raw'][name];self.reject(v)

    def test_each_cap_rejects_before_any_decode(self):
        for name,cap in adapter.CAPS.items():
            with self.subTest(name=name):
                v=archive_data();v['_retained_raw'][name]=b'x'*(cap+1)
                with patch.object(adapter.record_data,'decode_data',side_effect=AssertionError('unexpected decode')) as decode:
                    self.reject(v);decode.assert_not_called()

    def test_stale_runner_decoded_value_rejects(self):
        v=archive_data();v['fixture-result.json']['control']='N06';self.reject(v)

    def test_copied_root_frames_and_helper_stdout_must_match(self):
        for name in ('n04-kernel-observation.json','n04-root-failure.json','n04-root-terminal.txt'):
            v=archive_data();v['_retained_raw'][name]+=b' ';self.reject(v)
        v=archive_data();v['_retained_raw']['logs/n04-helper.stdout']+=b' ';self.reject(v)
        v=archive_data();v['_retained_raw']['logs/n01.stdout']=b'x';self.reject(v)
        v=archive_data();v['_retained_raw']['logs/n04-helper.stderr']=b'x';self.reject(v)

    def test_descriptor_policy_binding_rejects(self):
        v=archive_data();desc=dict(v['n04-descriptor.json']);desc['policy_sha256']='c'*64
        replace_json(v,'n04-descriptor.json',desc);self.reject(v)

    def test_account_row_grammar_and_numeric_bounds(self):
        for raw in (b'999\t987\t998\t986\t985', b'0999\t987\t998\t986\t985\n',
                    b'999\t987\t998\t986\t985\nextra\n', b'999\t987\t998\t986\t0\n',
                    b'999\t987\t998\t986\t4294967295\n'):
            v=archive_data();v['_retained_raw']['n04-account-ids.tsv']=raw;self.reject(v)

    def test_each_descriptor_account_id_binding_and_results_group_distinctness(self):
        for index in range(4):
            values=[999,987,998,986,985];values[index]+=10
            v=archive_data();v['_retained_raw']['n04-account-ids.tsv']=('\t'.join(map(str,values))+'\n').encode()
            self.reject(v)
        v=archive_data();v['_retained_raw']['n04-account-ids.tsv']=b'999\t987\t998\t986\t987\n';self.reject(v)
        # Rebind dependent detached hashes so duplicate-user/group cases reach the final
        # distinctness guard instead of failing an earlier descriptor/READY digest check.
        for field,new_id,row in (
            ('subject_uid',999,b'999\t987\t999\t986\t985\n'),
            ('subject_gid',987,b'999\t987\t998\t987\t985\n'),
        ):
            v=archive_data();desc=dict(v['n04-descriptor.json']);desc[field]=new_id
            replace_json(v,'n04-descriptor.json',desc)
            digest=hashlib.sha256(v['_retained_raw']['n04-descriptor.json']).hexdigest()
            kernel=copy.deepcopy(v['n04-kernel-observation.json']);kernel['ready']['descriptor_sha256']=digest
            replace_json(v,'n04-kernel-observation.json',kernel)
            v['_retained_raw']['logs/n01.stderr']=encode(kernel)+v['_retained_raw']['n04-root-failure.json']+v['_retained_raw']['n04-root-terminal.txt']
            consistency=dict(v['n04-consistency.json']);consistency['descriptor_sha256']=digest
            replace_json(v,'n04-consistency.json',consistency)
            v['_retained_raw']['n04-account-ids.tsv']=row;self.reject(v)

    def test_consistency_is_exact_replay_not_advisory(self):
        for key,bad in (('worker_pid',1),('worker_starttime_ticks',203),('descriptor_sha256','0'*64),
                        ('independent_raw_worker_capture',True),('native_authority',True)):
            v=archive_data();c=dict(v['n04-consistency.json']);c[key]=bad
            replace_json(v,'n04-consistency.json',c);self.reject(v)
        v=archive_data();c=dict(v['n04-consistency.json']);c['extra']='canary'
        replace_json(v,'n04-consistency.json',c);self.reject(v)

    def test_live_policy_and_original_holder_output_guards_reject(self):
        for key in ('no_new_privileges','cap_inheritable','cap_permitted','cap_effective','cap_bounding','cap_ambient'):
            v=archive_data();live=dict(v['n04-helper-live.json']);live[key]+=1
            replace_json(v,'n04-helper-live.json',live);self.reject(v)
        v=archive_data();live=dict(v['n04-helper-live.json']);live['no_new_privileges']=True
        replace_json(v,'n04-helper-live.json',live);self.reject(v)
        v=archive_data();kernel=copy.deepcopy(v['n04-kernel-observation.json']);kernel['pumps']['stderr']['eof']=False
        replace_json(v,'n04-kernel-observation.json',kernel)
        v['_retained_raw']['logs/n01.stderr']=encode(kernel)+v['_retained_raw']['n04-root-failure.json']+v['_retained_raw']['n04-root-terminal.txt']
        self.reject(v)

    def test_other_negative_case_raw_projections_reject(self):
        for name in ('n05-worker.stderr','n06-worker.stdout','negative-setup.json','negative-worker-projection.json'):
            v=archive_data();v['_retained_raw'][name]=b'';self.reject(v)
            v=archive_data();v[name]={};self.reject(v)

    def test_request_digest_is_only_detached_consistency_not_raw_verified(self):
        v=archive_data();c=dict(v['n04-consistency.json']);c['request_sha256']='d'*64
        replace_json(v,'n04-consistency.json',c)
        v['_retained_raw']['request-policy.sha256']=b'opaque retained OS output, not request bytes\n'
        result=adapter.validate_n04_archive(v)
        self.assertEqual(result['request_digest_basis'],'detached-consistency-and-external-fixture-provenance')
        self.assertFalse(result['independent_raw_worker_capture'])
        for bad in (None,False,'bad'):
            v=archive_data();c=dict(v['n04-consistency.json']);c['request_sha256']=bad
            replace_json(v,'n04-consistency.json',c);self.reject(v)

    def test_rejection_never_echoes_canary_or_decoder_message(self):
        v=archive_data();v['_retained_raw']['fixture-result.json']=b'CANARY_PRIVATE_INVALID_JSON'
        self.reject(v)

if __name__ == '__main__':
    unittest.main()
