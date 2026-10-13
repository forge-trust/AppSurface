"""DEFINED ONLY: detached data controls; no holder, root, socket, process or native authority."""
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import unittest

spec=importlib.util.spec_from_file_location('n04_data',Path(__file__).with_name('n04-record-data.py'))
data=importlib.util.module_from_spec(spec)
spec.loader.exec_module(data)


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


def invoke(values,*,root=None,helper=None,live=None,descriptor=None,helper_stderr=b''):
    g=values['g']
    return data.check_n04_records(root or (encode(values['kernel'])+encode(values['failure'])+data.ROOT_TERMINAL),
        helper or encode(values['helper']),helper_stderr,live or encode(values['live']),
        descriptor or encode(values['descriptor']),generation=g,request_sha256='a'*64,
        expected_helper_unit='appsurface-evidence-n04-coordinator-'+g+'.service')


class N04DetachedControls(unittest.TestCase):
    def reject(self,values,**kwargs):
        with self.assertRaises(ValueError):invoke(values,**kwargs)
    def test_valid_neighbor_remains_observation_not_authority(self):
        result=invoke(fixture_data());self.assertFalse(result['native_authority']);self.assertFalse(result['native_acceptance'])
        self.assertFalse(result['independent_raw_worker_capture'])
    def test_missing_or_extra_root_frame(self):
        v=fixture_data();self.reject(v,root=encode(v['kernel'])+data.ROOT_TERMINAL)
        self.reject(v,root=encode(v['kernel'])+encode(v['failure'])+data.ROOT_TERMINAL+b'extra\n')
    def test_duplicate_and_case_duplicate_json(self):
        v=fixture_data();raw=encode(v['helper'])[:-2]
        self.reject(v,helper=raw+b',"status":"observed"}\n')
        self.reject(v,helper=raw+b',"STATUS":"observed"}\n')
    def test_unexpected_root_summary_or_terminal(self):
        v=fixture_data();self.reject(v,root=encode(v['kernel'])+encode(v['failure'])+b'ASEVD402\n')
        v['failure']['phase']='Custody';self.reject(v)
    def test_helper_positive_flags_cannot_be_omitted(self):
        for key in ('worker_stop_observed','ready_committed_notification','replacement_peer_tuple_matched',
                    'peer_live_identity_rechecked','replacement_write_shutdown'):
            with self.subTest(key=key):
                v=fixture_data();v['helper'][key]=False;self.reject(v)
    def test_application_bytes_or_boolean_zero_rejected(self):
        for key in ('replacement_request_bytes','replacement_received_bytes'):
            for value in (1,False):
                with self.subTest(key=key,value=value):
                    v=fixture_data();v['helper'][key]=value;self.reject(v)
    def test_same_broker_pid_or_wrong_peer_uid_rejected(self):
        v=fixture_data();v['live']['pid']=v['helper']['owner_pid'];self.reject(v)
        v=fixture_data();v['helper']['worker_uid4'][2]+=1;self.reject(v)
    def test_descriptor_full_byte_hash_cannot_be_rebound(self):
        v=fixture_data();dr=encode(v['descriptor']);self.reject(v,descriptor=dr[:-1]+b' \n')
    def test_hash_count_eof_quota_and_original_join_matrix(self):
        mutations=(('stderr','sha256','0'*64),('stderr','received_bytes',1),('stderr','eof',False),
                   ('stdout','discarded_bytes',1))
        for stream,key,value in mutations:
            v=fixture_data();v['kernel']['pumps'][stream][key]=value;self.reject(v)
        for key in ('startup','pending_stop','monitor','server','pumps'):
            v=fixture_data();v['kernel']['joins'][key]=False;self.reject(v)
    def test_wrong_terminal_or_group_and_missing_ready(self):
        v=fixture_data();v['kernel']['terminal']['exec_main_status']=0;self.reject(v)
        v=fixture_data();v['kernel']['cgroup'].update(exists=True,populated=True,frozen=False,device_major=0,device_minor=1,inode=9);self.reject(v)
        v=fixture_data();v['kernel']['ready']['committed']=False;self.reject(v)
    def test_canary_unknown_members_and_helper_stderr_reject(self):
        v=fixture_data();v['helper']['CANARY_SECRET']='not-output';self.reject(v)
        self.reject(fixture_data(),helper_stderr=b'private-canary')
    def test_helper_policy_matches_fixed_root_unit_only(self):
        for key in ('no_new_privileges','cap_inheritable','cap_permitted','cap_effective','cap_bounding','cap_ambient'):
            for bad in (None,False,'524333',-1,2**64):
                with self.subTest(key=key,bad=bad):
                    v=fixture_data();v['live'][key]=bad;self.reject(v)
            v=fixture_data();v['live'][key]+=1;self.reject(v)
    def test_helper_policy_fields_are_required_and_closed(self):
        v=fixture_data();del v['live']['cap_effective'];self.reject(v)
        v=fixture_data();v['live']['cap_effective_raw']='private-canary';self.reject(v)
    def test_each_frame_bound_and_bad_encoding(self):
        v=fixture_data();self.reject(v,root=b'x'*6145+b'\n');self.reject(v,helper=b'x'*4097+b'\n')
        self.reject(v,helper=b'\xff\n')


if __name__=='__main__':unittest.main()
