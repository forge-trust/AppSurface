"""Defined-only detached DATA controls. No unit, root role, helper or native process runs here."""
import base64
import copy
import importlib.util
import json
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location('n07_record_data', Path(__file__).with_name('n07-record-data.py'))
m = importlib.util.module_from_spec(spec)
spec.loader.exec_module(m)

G = '00000000000000000000000000000001'
SOURCE = 'a' * 40
POLICY = 'b' * 64
REQUEST = 'c' * 64
UNIT = 'appsurface-evidence-worker-' + G + '.service'
HELPER = 'appsurface-evidence-n07-coordinator-' + G + '.service'

def line(value):
    return json.dumps(value, separators=(',', ':')).encode() + b'\n'

def valid_data():
    frame = dict(schema='issue779-n07-precleanup-allocation-fault-v1', phase='Allocation',
        operation='CheckParentIdentity', error_family='Admission', capture_point='AllocateCatchBeforeCallbackRethrow',
        terminal_observed=False, observation_only=True, native_authority=False, native_acceptance=False)
    stderr = line(frame) + b'private-fatal-canary\x00\xff'
    descriptor = {key: None for key in m.DESCRIPTOR_KEYS}
    descriptor.update(schema='evidence-worker-linux-v1', run_id='csharp/' + G, worker_pid=123, broker_pid=124,
        worker_uid=999, worker_gid=987, subject_uid=998, subject_gid=986, unit=UNIT,
        cgroup='/system.slice/' + UNIT, mode='observation', proof_digest='', policy_sha256=POLICY,
        subject_revision=SOURCE, output_parent='/run/appsurface-evidence-' + G + '/output', output_slot='evidence',
        descriptor_path='/run/appsurface-evidence-' + G + '/worker/worker-control.json',
        output_parent_identity=dict(device_major=0, device_minor=14, inode=32, uid=999, gid=987))
    descriptor_raw = line(descriptor)
    def stream(data):
        return dict(received_bytes=len(data), retained_bytes=len(data), discarded_bytes=0,
                    eof=True, failure='None', sha256=m.sha(data))
    settlement = dict(schema='issue779-n07-failure-settlement-v1', generation=G, worker_unit=UNIT,
        process=dict(pid=123, starttime_ticks=42, uid4=[999]*4, gid4=[987]*4, control_group='/system.slice/' + UNIT),
        terminal=dict(exec_main_pid=123, exec_main_code=2, exec_main_status=6, active_state='failed',
                      sub_state='failed', source='original-final-selected-unit-read'),
        monitor=dict(completed=True, status='Faulted', completed_successfully=False),
        joins=dict(startup=True, pending_stop=True, selected_unit=True, group=True, monitor=True, pumps=True),
        cgroup=dict(exists=True, populated=False, frozen=False, device_major=0, device_minor=14, inode=99),
        pumps=dict(stdout=stream(b''), stderr=stream(stderr), received_bytes=len(stderr),
                   received_byte_limit=131072, discarded_bytes=0, failure='None'),
        observation_only=True, native_authority=False, native_acceptance=False)
    root = dict(schema='issue779-n07-root-failure-observation-v1', ready_descriptor_sha256=m.sha(descriptor_raw),
        server=dict(completed=True, status='Faulted', completed_successfully=False), failure_settlement=settlement,
        observation_only=True, native_authority=False, native_acceptance=False)
    raw = dict(schema='issue779-n07-joined-worker-raw-v1', stdout_base64='', stderr_base64=base64.b64encode(stderr).decode(),
               stdout_bytes=0, stderr_bytes=len(stderr), native_authority=False)
    parent = dict(device_major=0, device_minor=14, inode=32, uid=999, gid=987, mode=0o40700, nlink=2)
    helper = dict(schema='issue779-n07-root-coordinator-result-v1', status='parent-substitution-observed', generation=G,
        owner_pid=124, owner_start_ticks=43, owner_uid4=[0]*4, owner_gid4=[0]*4, owner_groups=[],
        owner_cgroup='/system.slice/appsurface-evidence-owner-' + G + '.service',
        worker_pid=123, worker_start_ticks=42, worker_uid4=[999]*4, worker_gid4=[987]*4, worker_groups=[],
        worker_cgroup='/system.slice/' + UNIT, worker_cap_eff='0000000000000000', worker_no_new_privileges='1',
        worker_stop_observed=True, ready_committed_notification=True, original_worker_resumed=True,
        original_parent=parent, replacement_parent=dict(parent, inode=33), descriptor_sha256=m.sha(descriptor_raw),
        request_sha256=REQUEST, allocation_observed=False, native_authority=False, native_acceptance=False)
    live = dict(schema='issue779-n07-helper-live-v2', pid=125, starttime_ticks=44, uid4=[0]*4, gid4=[0]*4, groups=[],
        control_group='/system.slice/' + HELPER, native_executable_matches=True, managed_argv_matches=True,
        native_authority=False, no_new_privileges=1, cap_inheritable=0, cap_permitted=m.CAPS,
        cap_effective=m.CAPS, cap_bounding=m.CAPS, cap_ambient=0)
    failure = dict(schema='evidence-native-observation-failure-v4', phase='ServerRun', error_kind='Admission',
        diagnostic_code='ASEVD410', account_failure=None, control_failure=None, custody_failure=None)
    return dict(raw=raw, root=root, helper=helper, live=live, descriptor=descriptor_raw, failure=failure, stderr=stderr)

def check(v, **overrides):
    args = dict(generation=G, request_sha256=REQUEST, helper_unit=HELPER,
                expected_policy_sha256=POLICY, expected_source=SOURCE)
    args.update(overrides)
    return m.check_records(line(v['raw']) + line(v['root']) + line(v['failure']) + m.ROOT_TERMINAL,
                           line(v['helper']), line(v['live']), v['descriptor'], **args)

class N07DetachedDataControls(unittest.TestCase):
    def rejected(self, mutate):
        v = valid_data()
        mutate(v)
        with self.assertRaises((ValueError, TypeError, KeyError)):
            check(v)

    def test_signal_six_faulted_tasks_preserved_without_normal_one(self):
        result, _, stdout, stderr, *_ = check(valid_data())
        self.assertEqual((2, 6, 'Faulted', b''), (result['worker_exec_main_code'], result['worker_exec_main_status'], result['monitor_status'], stdout))
        self.assertIn(b'private-fatal-canary', stderr)
        self.assertFalse(result['native_authority'])
        self.assertFalse(result['native_acceptance'])

    def test_cancelled_task_not_success_and_normal_code_preserved(self):
        v=valid_data()
        v['root']['server']['status']='Canceled'
        v['root']['failure_settlement']['terminal'].update(exec_main_code=1, exec_main_status=1)
        result=check(v)[0]
        self.assertEqual(('Canceled',1,1),(result['server_status'],result['worker_exec_main_code'],result['worker_exec_main_status']))

    def test_task_success_bool_cannot_upgrade_faulted(self):
        self.rejected(lambda v:v['root']['server'].update(completed_successfully=True))

    def test_terminal_boolean_not_integer(self):
        self.rejected(lambda v:v['root']['failure_settlement']['terminal'].update(exec_main_code=True))

    def test_two_eof_and_full_received_retained_required(self):
        self.rejected(lambda v:v['root']['failure_settlement']['pumps']['stderr'].update(eof=False))
        self.rejected(lambda v:v['root']['failure_settlement']['pumps']['stderr'].update(retained_bytes=1))

    def test_no_discard_error_or_quota_upgrade(self):
        self.rejected(lambda v:v['root']['failure_settlement']['pumps'].update(discarded_bytes=1))
        self.rejected(lambda v:v['root']['failure_settlement']['pumps'].update(failure='QuotaExceeded'))
        self.rejected(lambda v:v['root']['failure_settlement']['pumps'].update(received_byte_limit=1))

    def test_full_raw_hash_and_count_must_match(self):
        self.rejected(lambda v:v['root']['failure_settlement']['pumps']['stderr'].update(sha256='0'*64))
        self.rejected(lambda v:v['raw'].update(stderr_bytes=1))

    def test_base64_noncanonical_or_invalid_rejects(self):
        self.rejected(lambda v:v['raw'].update(stderr_base64='%%'))
        self.rejected(lambda v:v['raw'].update(stderr_base64=v['raw']['stderr_base64']+'\n'))

    def test_raw_64k_exact_including_nontext_and_65k_reject(self):
        frame=line(dict(schema='issue779-n07-precleanup-allocation-fault-v1',phase='Allocation',operation='CheckParentIdentity',
            error_family='Admission',capture_point='AllocateCatchBeforeCallbackRethrow',terminal_observed=False,
            observation_only=True,native_authority=False,native_acceptance=False))
        data=frame+b'\xfb'*(m.MAX_STDERR-len(frame))
        raw=line(dict(schema='issue779-n07-joined-worker-raw-v1',stdout_base64='',stderr_base64=base64.b64encode(data).decode(),
            stdout_bytes=0,stderr_bytes=len(data),native_authority=False))
        self.assertEqual(data,m.raw_pair(raw)[1])
        bad=json.loads(raw)
        bad.update(stderr_bytes=m.MAX_STDERR+1,stderr_base64=base64.b64encode(data+b'x').decode())
        with self.assertRaises(ValueError):m.raw_pair(line(bad))

    def test_raw_line_96k_bound_and_framing(self):
        with self.assertRaises(ValueError):m.raw_pair(b'x'*(m.MAX_RAW_LINE+1))
        with self.assertRaises(ValueError):m.raw_pair(line(valid_data()['raw']).rstrip(b'\n'))

    def test_actual_allocate_first_frame_only(self):
        frame=json.loads(line(valid_data()['raw']))
        original=base64.b64decode(frame['stderr_base64'])
        with self.assertRaises(ValueError):m.first_frame(b'earlier guard\n'+original)
        with self.assertRaises(ValueError):m.first_frame(original+b'issue779-n07-precleanup-allocation-fault-v1')
        bad=json.loads(original.split(b'\n',1)[0]);bad['operation']='CreateSlot'
        with self.assertRaises(ValueError):m.first_frame(line(bad))

    def test_duplicate_casealias_unknown_and_multiple_object_reject(self):
        for raw in (b'{"schema":1,"schema":2}',b'{"schema":1,"Schema":2}',b'{}{}'):
            with self.assertRaises(ValueError):m.decode(raw,1024)
        self.rejected(lambda v:v['helper'].update(unexpected=1))

    def test_descriptor_request_source_binding(self):
        with self.assertRaises(ValueError):check(valid_data(),request_sha256='0'*64)
        with self.assertRaises(ValueError):check(valid_data(),expected_source='0'*40)
        self.rejected(lambda v:v['root'].update(ready_descriptor_sha256='0'*64))

    def test_ids_start_and_original_peer_cannot_combine(self):
        self.rejected(lambda v:v['helper'].update(worker_pid=321))
        self.rejected(lambda v:v['helper'].update(worker_start_ticks=43))
        self.rejected(lambda v:v['root']['failure_settlement']['process'].update(uid4=[999,998,999,999]))

    def test_bool_root_ids_or_caps_reject(self):
        self.rejected(lambda v:v['live'].update(uid4=[False]*4))
        self.rejected(lambda v:v['live'].update(no_new_privileges=True))
        self.rejected(lambda v:v['live'].update(cap_effective=m.CAPS|(1<<1)))

    def test_same_parent_inode_and_false_resume_reject(self):
        self.rejected(lambda v:v['helper']['replacement_parent'].update(inode=32))
        self.rejected(lambda v:v['helper'].update(original_worker_resumed=False))

    def test_cgroup_absent_null_and_present_empty(self):
        v=valid_data();v['root']['failure_settlement']['cgroup'].update(exists=False,populated=None,frozen=None,device_major=None,device_minor=None,inode=None)
        self.assertTrue(check(v)[0]['data_consistent'])
        self.rejected(lambda v:v['root']['failure_settlement']['cgroup'].update(populated=True))

    def test_joins_and_authority_not_truthiness(self):
        self.rejected(lambda v:v['root']['failure_settlement']['joins'].update(pumps=1))
        self.rejected(lambda v:v['root'].update(native_acceptance=True))
        self.rejected(lambda v:v['helper'].update(allocation_observed=True))

    def test_physical_query_complete_zero_settlement_data_only(self):
        values=dict(Id=UNIT,LoadState='loaded',ActiveState='failed',SubState='failed',Job='',MainPID='0',ControlGroup='',
            ExecMainPID='123',ExecMainCode='2',ExecMainStatus='6')
        raw=''.join(k+'='+v+'\n' for k,v in values.items()).encode()
        terminal=valid_data()['root']['failure_settlement']['terminal']
        self.assertEqual(values,m.selected_unit(raw,UNIT,terminal))
        with self.assertRaises(ValueError):m.selected_unit(raw+b'Job=queued\n',UNIT,terminal)
        with self.assertRaises(ValueError):m.selected_unit(raw.replace(b'MainPID=0',b'MainPID=123'),UNIT,terminal)

    def test_no_public_canary_from_data_projection(self):
        result=check(valid_data())[0]
        self.assertNotIn(b'private-fatal-canary',line(result))
        self.assertEqual(False,result['native_authority'])
        self.assertEqual(False,result['native_acceptance'])

if __name__ == '__main__':
    unittest.main()
