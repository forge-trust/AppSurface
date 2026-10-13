"""Synthetic detached-data controls only; never native evidence or an execution claim."""
import base64
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import unittest
from unittest import mock

RUNNER = Path(__file__).with_name('run-native.py')
SPEC = importlib.util.spec_from_file_location('issue779_slot_archive_runner', RUNNER)
NATIVE = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = NATIVE
SPEC.loader.exec_module(NATIVE)

GENERATION = 'a' * 32
WORKER_UID = 65010
WORKER_GID = 65011
SUBJECT_UID = 65012
SUBJECT_GID = 65013
RESULTS_GID = 65014
WORKER_PID = 1234
BROKER_PID = 1235
ACCOUNT_TSV = f'{WORKER_UID}\t{WORKER_GID}\t{SUBJECT_UID}\t{SUBJECT_GID}\t{RESULTS_GID}\n'.encode('ascii')

ALLOCATION = {
    'schema': 'evidence-allocation-failure-v1',
    'phase': 'Allocation',
    'operation': 'CreateSlot',
    'stageOutcome': 'Failed',
    'terminalCode': 'StageFailed',
    'errorClass': 'Io',
    'nativeErrno': 17,
}
STDERR = (json.dumps(ALLOCATION, separators=(',', ':')) + '\n').encode() + NATIVE.WORKER_TERMINAL


def encode(value):
    return json.dumps(value, separators=(',', ':'), ensure_ascii=True).encode('ascii') + b'\n'


def identity(inode, uid, gid, mode, links, length, dev=(8, 1)):
    return {
        'device_major': dev[0], 'device_minor': dev[1], 'inode': inode,
        'uid': uid, 'gid': gid, 'mode': mode, 'links': links, 'length': length,
    }


def descriptor(parent):
    unit = 'appsurface-evidence-worker-' + GENERATION + '.service'
    root = '/run/appsurface-evidence-' + GENERATION
    return {
        'schema': 'evidence-worker-linux-v1', 'run_id': 'csharp/' + GENERATION,
        'worker_pid': WORKER_PID, 'broker_pid': BROKER_PID,
        'worker_uid': WORKER_UID, 'worker_gid': WORKER_GID,
        'subject_uid': SUBJECT_UID, 'subject_gid': SUBJECT_GID,
        'unit': unit, 'cgroup': '/system.slice/' + unit,
        'job_deadline_utc': '2026-10-13T10:00:00Z',
        'tool_root': '/work/tool', 'subject_root': '/work/subject',
        'output_parent': root + '/output',
        'output_slot': 'evidence', 'dotnet_path': '/usr/bin/dotnet',
        'test_output_root': root + '/raw-results',
        'policy_file': root + '/worker/policy.json',
        'mode': 'observation', 'socket_path': root + '/worker/broker/control.sock',
        'descriptor_path': root + '/worker/worker-control.json',
        'entry_sha256': 'b' * 64, 'policy_sha256': 'c' * 64,
        'base_revision': 'd' * 40, 'subject_revision': 'e' * 40,
        'workflow_identity': 'github-actions-123-1',
        'provider': 'github-actions', 'platform': 'linux-x64', 'proof_digest': '',
        'output_parent_identity': {key: parent[key] for key in ('device_major', 'device_minor', 'inode', 'uid', 'gid')},
        'observation_profile_ids': [], 'observation_producer_ids': [], 'paths': [],
        'admission_seconds': 30, 'start_seconds': 60, 'collection_seconds': 60,
        'cleanup_seconds': 60, 'stopping_seconds': 30,
        'diff_file': None, 'diff_sha256': None, 'solution': None,
    }


def archive(case):
    parent = identity(100, WORKER_UID, WORKER_GID, 0x41c0, 2, 64)
    if case == 'N05':
        occupied = identity(101, WORKER_UID, WORKER_GID, 0x41c0, 2, 64)
        sentinel = identity(102, WORKER_UID, WORKER_GID, 0x8180, 1, len(NATIVE.SLOT_SENTINEL_BYTES))
        setup_fields = {
            'schema': 'issue779-n05-slot-inspection-v1', 'parent': parent,
            'slot': occupied, 'sentinel': sentinel,
            'sha256': NATIVE.SLOT_SENTINEL_SHA256,
        }
    else:
        target = identity(103, SUBJECT_UID, RESULTS_GID, 0x41c8, 2, 64)
        link = identity(104, 0, 0, 0xa1ff, 1, len(b'../raw-results'))
        sentinel = identity(105, 0, 0, 0x8180, 1, len(NATIVE.SLOT_SENTINEL_BYTES))
        setup_fields = {
            'schema': 'issue779-n06-symlink-inspection-v1', 'parent': parent,
            'target': target, 'link': link, 'sentinel': sentinel,
            'target_sha256': NATIVE.SLOT_SENTINEL_SHA256,
            'sha256': NATIVE.SLOT_SENTINEL_SHA256,
        }
    setup = dict(setup_fields, phase='setup')
    after = dict(setup_fields, phase='post_join')
    prefix = 'issue779-' + case.lower()
    projection = {
        'schema': prefix + '-joined-worker-output-v1', 'origin': 'joined-worker-output',
        'allocation': ALLOCATION, 'terminal_diagnostic': 'ASEVD409',
        'stdout_bytes': 0, 'stderr_bytes': len(STDERR), 'native_authority': False,
    }
    worker_raw = {
        'schema': prefix + '-joined-worker-raw-v1',
        'stdout_base64': '', 'stderr_base64': base64.b64encode(STDERR).decode('ascii'),
        'stdout_bytes': 0, 'stderr_bytes': len(STDERR), 'native_authority': False,
    }
    desc_raw = encode(descriptor(parent))[:-1]
    stdout = b''
    kernel = {
        'schema': 'issue779-negative-kernel-observation-v1',
        'generation': GENERATION,
        'worker_unit': 'appsurface-evidence-worker-' + GENERATION + '.service',
        'process': {
            'pid': WORKER_PID, 'starttime_ticks': 987654,
            'uid4': [WORKER_UID] * 4, 'gid4': [WORKER_GID] * 4,
            'control_group': '/system.slice/appsurface-evidence-worker-' + GENERATION + '.service',
        },
        'ready': {
            'committed': True,
            'descriptor_sha256': hashlib.sha256(desc_raw).hexdigest(),
        },
        'terminal': {
            'exec_main_pid': WORKER_PID, 'exec_main_code': 1, 'exec_main_status': 1,
            'active_state': 'inactive', 'sub_state': 'dead',
        },
        'cgroup': {
            'exists': False, 'populated': None, 'frozen': None,
            'device_major': None, 'device_minor': None, 'inode': None,
        },
        'pumps': {
            'stdout': {
                'received_bytes': 0, 'retained_bytes': 0, 'discarded_bytes': 0,
                'eof': True, 'failure': 'None', 'sha256': hashlib.sha256(stdout).hexdigest(),
            },
            'stderr': {
                'received_bytes': len(STDERR), 'retained_bytes': len(STDERR), 'discarded_bytes': 0,
                'eof': True, 'failure': 'None', 'sha256': hashlib.sha256(STDERR).hexdigest(),
            },
            'received_bytes': len(STDERR), 'received_byte_limit': 65536,
            'failure': 'None', 'discarded_bytes': 0,
        },
        'joins': {'startup': True, 'pending_stop': True, 'monitor': True, 'server': True, 'pumps': True},
        'observation_only': True, 'native_authority': False, 'native_acceptance': False,
    }
    failure = {
        'schema': 'evidence-native-observation-failure-v4',
        'phase': 'ResultCheck', 'error_kind': 'Admission', 'diagnostic_code': 'ASEVD410',
        'account_failure': None, 'control_failure': None, 'custody_failure': None,
    }
    return {
        'negative-setup.json': encode(setup),
        'negative-post-join.json': encode(after),
        'negative-worker-projection.json': encode(projection),
        'negative-kernel-observation.json': encode(kernel),
        'negative-worker-raw.json': encode(worker_raw),
        'negative-root-failure.json': encode(failure),
        'negative-root-terminal.txt': NATIVE.ROOT_TERMINAL,
        'negative-descriptor.json': desc_raw,
        'negative-account-ids.tsv': ACCOUNT_TSV,
        case.lower() + '-worker.stdout': stdout,
        case.lower() + '-worker.stderr': STDERR,
    }


def reject(test, values, case='N05', generation=GENERATION, uid=WORKER_UID, gid=WORKER_GID,
           subject_uid=SUBJECT_UID, subject_gid=SUBJECT_GID):
    with test.assertRaises(ValueError) as caught:
        NATIVE.validate_slot_archive(values, case, generation, uid, gid, subject_uid, subject_gid)
    test.assertEqual('slot-archive-data-rejected', str(caught.exception))
    test.assertIsNone(caught.exception.__cause__)


class SlotArchiveValidatorTests(unittest.TestCase):
    def test_synthetic_n05_fixed_records_are_consistent(self):
        self.assertIs(NATIVE.validate_slot_archive(archive('N05'), 'N05', GENERATION,
            WORKER_UID, WORKER_GID, SUBJECT_UID, SUBJECT_GID), True)

    def test_synthetic_n06_fixed_records_are_consistent(self):
        self.assertIs(NATIVE.validate_slot_archive(archive('N06'), 'N06', GENERATION,
            WORKER_UID, WORKER_GID, SUBJECT_UID, SUBJECT_GID), True)

    def test_calls_both_existing_data_validators(self):
        values = archive('N05')
        with mock.patch.object(NATIVE, 'validate_fixture_frames', wraps=NATIVE.validate_fixture_frames) as frames, \
             mock.patch.object(NATIVE, 'check_kernel_record', wraps=NATIVE.check_kernel_record) as kernel:
            self.assertIs(NATIVE.validate_slot_archive(values, 'N05', GENERATION,
                WORKER_UID, WORKER_GID, SUBJECT_UID, SUBJECT_GID), True)
            frames.assert_called_once()
            kernel.assert_called_once()

    def test_signature_has_no_override_or_alias_parameters(self):
        import inspect
        self.assertEqual(
            ('values', 'case', 'expected_generation', 'expected_uid', 'expected_gid',
             'expected_subject_uid', 'expected_subject_gid'),
            tuple(inspect.signature(NATIVE.validate_slot_archive).parameters),
        )

    def test_case_alias_rejects(self):
        reject(self, archive('N05'), case='n05')

    def test_generation_and_each_external_identity_are_bound(self):
        values = archive('N05')
        reject(self, values, generation='f' * 32)
        reject(self, values, uid=WORKER_UID + 10)
        reject(self, values, gid=WORKER_GID + 10)
        reject(self, values, subject_uid=SUBJECT_UID + 10)
        reject(self, values, subject_gid=SUBJECT_GID + 10)

    def test_descriptor_digest_is_bound_to_kernel_ready_record(self):
        values = archive('N05')
        values['negative-descriptor.json'] = values['negative-descriptor.json'].replace(b'/work/tool', b'/work/evil')
        reject(self, values)

    def test_exact_account_id_tsv_is_bound(self):
        values = archive('N06')
        values['negative-account-ids.tsv'] = values['negative-account-ids.tsv'].replace(b'65013', b'65014')
        reject(self, values)

    def test_case_specific_raw_stdout_stderr_are_byte_bound(self):
        values = archive('N05')
        values['n05-worker.stdout'] = b'not-empty'
        reject(self, values)
        values = archive('N06')
        values['n06-worker.stderr'] += b'changed'
        reject(self, values)

    def test_setup_and_postjoin_metadata_must_match(self):
        values = archive('N05')
        changed = json.loads(values['negative-post-join.json'])
        changed['sentinel']['inode'] += 1
        values['negative-post-join.json'] = encode(changed)
        reject(self, values)

    def test_fixed_sentinel_and_symlink_target_metadata_are_checked(self):
        values = archive('N06')
        changed = json.loads(values['negative-setup.json'])
        changed['target_sha256'] = 'f' * 64
        values['negative-setup.json'] = encode(changed)
        reject(self, values)
        values = archive('N05')
        changed = json.loads(values['negative-setup.json'])
        changed['sentinel']['length'] += 1
        values['negative-setup.json'] = encode(changed)
        reject(self, values)

    def test_original_kernel_join_and_group_guards_remain_required(self):
        values = archive('N05')
        kernel = json.loads(values['negative-kernel-observation.json'])
        kernel['joins']['pending_stop'] = False
        values['negative-kernel-observation.json'] = encode(kernel)
        reject(self, values)
        values = archive('N06')
        kernel = json.loads(values['negative-kernel-observation.json'])
        kernel['cgroup']['exists'] = True
        kernel['cgroup']['populated'] = True
        kernel['cgroup']['frozen'] = False
        kernel['cgroup']['device_major'] = 8
        kernel['cgroup']['device_minor'] = 1
        kernel['cgroup']['inode'] = 33
        values['negative-kernel-observation.json'] = encode(kernel)
        reject(self, values)

    def test_wrong_case_archive_members_and_missing_frame_reject(self):
        values = archive('N05')
        values['n06-worker.stdout'] = b''
        reject(self, values)
        values = archive('N06')
        del values['negative-root-failure.json']
        reject(self, values)

    def test_calls_are_detached_data_only(self):
        values = archive('N05')
        with mock.patch.object(subprocess, 'run', side_effect=AssertionError('external action forbidden')) as run, \
             mock.patch.object(subprocess, 'Popen', side_effect=AssertionError('external action forbidden')) as popen:
            self.assertIs(NATIVE.validate_slot_archive(values, 'N05', GENERATION,
                WORKER_UID, WORKER_GID, SUBJECT_UID, SUBJECT_GID), True)
            run.assert_not_called()
            popen.assert_not_called()


if __name__ == '__main__':
    unittest.main(verbosity=2)
