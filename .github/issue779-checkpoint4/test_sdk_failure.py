"""Real small-file reader and closed argument data only; no collector, runtime owner or native claim."""
from pathlib import Path
import json
import os
import tempfile
import time
import unittest
from unittest.mock import patch

import measurement_arguments as arguments
import sdk_failure as subject


class FailureDiagnosticDataTests(unittest.TestCase):
    generation = 'a' * 32

    def fixture(self, parent):
        root = parent / ('appsurface-evidence-producer-diagnostics-' + self.generation)
        root.mkdir(mode=0o700)
        state = {name: None for name in subject.STATE_FIELDS}
        state.update(schema='evidence-producer-failure-state-v1', stage='settlement', normal_code=1,
                     normal_status=1, captured_pid=123, cleanup_failed=False, pending_failure='None')
        for key in ('start_reserved', 'start_joined', 'stop_joined', 'physically_settled',
                    'group_empty_twice', 'pump_joined', 'output_drained'):
            state[key] = True
        self.save(root / 'state.json', json.dumps(state).encode())
        self.save(root / 'stdout.prefix', b'Failed! - Failed: 1, Passed: 5, Skipped: 0, Total: 6, Duration: 1ms\nprivate-canary')
        self.save(root / 'stderr.prefix', b'Root-selected test-failure scenario.\n')
        return root, state

    @staticmethod
    def save(path, data):
        path.write_bytes(data)
        path.chmod(0o600)

    def read(self, parent, **kwargs):
        return subject.read_failure(self.generation, 123, time.monotonic() + 10,
                                    parent=parent, uid=os.getuid(), gid=os.getgid(), **kwargs)

    def test_actual_files_project_only_counts_flags_lengths_and_hashes(self):
        with tempfile.TemporaryDirectory() as temp:
            parent = Path(temp).resolve()
            root, _ = self.fixture(parent)
            result = self.read(parent)
            self.assertEqual({'failed': 1, 'passed': 5, 'skipped': 0, 'total': 6}, result['tests'])
            self.assertFalse(result['native_acceptance'])
            self.assertEqual((root / 'stdout.prefix').stat().st_size, result['files']['stdout.prefix']['bytes'])
            self.assertNotIn('private-canary', json.dumps(result))

    def test_each_required_original_exit_or_join_fact_is_independently_required(self):
        for field, value in [('normal_code', 0), ('normal_status', 0), ('normal_status', True),
                             ('captured_pid', 124), ('cleanup_failed', True), ('pending_failure', 'canary'),
                             *[(name, False) for name in ('start_reserved', 'start_joined', 'stop_joined',
                                 'physically_settled', 'group_empty_twice', 'pump_joined', 'output_drained')]]:
            with self.subTest(field=field), tempfile.TemporaryDirectory() as temp:
                parent = Path(temp).resolve()
                root, state = self.fixture(parent)
                self.read(parent)
                state[field] = value
                self.save(root / 'state.json', json.dumps(state).encode())
                with self.assertRaises(RuntimeError):
                    self.read(parent)

    def test_duplicate_case_alias_unknown_and_missing_state_members_reject(self):
        for mutation in ('duplicate', 'alias', 'extra', 'missing'):
            with self.subTest(mutation=mutation), tempfile.TemporaryDirectory() as temp:
                parent = Path(temp).resolve()
                root, state = self.fixture(parent)
                body = json.dumps(state)
                if mutation == 'duplicate': body = body[:-1] + ',"normal_status":1}'
                if mutation == 'alias': body = body[:-1] + ',"Normal_Status":1}'
                if mutation == 'extra': state['canary'] = 'private-canary'; body = json.dumps(state)
                if mutation == 'missing': state.pop('captured_pid'); body = json.dumps(state)
                self.save(root / 'state.json', body.encode())
                with self.assertRaises(RuntimeError): self.read(parent)

    def test_wrong_missing_or_repeated_actual_test_summaries_cannot_pass(self):
        for body in (b'', b'Test Run Aborted.', b'Failed! - Failed: 2, Passed: 4, Skipped: 0, Total: 6,',
                     b'Failed! - Failed: 1, Passed: 5, Skipped: 1, Total: 7,',
                     b'Failed! - Failed: 1, Passed: 5, Skipped: 0, Total: 6,\n' * 2):
            with self.subTest(body=body[:16]), tempfile.TemporaryDirectory() as temp:
                parent = Path(temp).resolve()
                root, _ = self.fixture(parent)
                self.save(root / 'stdout.prefix', body)
                with self.assertRaises(RuntimeError): self.read(parent)

    def test_summary_without_actual_fixed_failure_message_rejects(self):
        with tempfile.TemporaryDirectory() as temp:
            parent = Path(temp).resolve()
            root, _ = self.fixture(parent)
            self.save(root / 'stderr.prefix', b'other failure')
            with self.assertRaises(RuntimeError): self.read(parent)

    def test_actual_link_fifo_writable_missing_and_extra_nodes_reject_without_reading_the_fifo(self):
        for mutation in ('symlink', 'hardlink', 'fifo', 'mode', 'missing', 'extra', 'oversize'):
            with self.subTest(mutation=mutation), tempfile.TemporaryDirectory() as temp:
                parent = Path(temp).resolve()
                root, _ = self.fixture(parent)
                selected = root / 'stdout.prefix'
                if mutation == 'symlink':
                    selected.unlink(); selected.symlink_to(root / 'stderr.prefix')
                if mutation == 'hardlink': os.link(selected, parent / 'outside-link')
                if mutation == 'fifo': selected.unlink(); os.mkfifo(selected, 0o600)
                if mutation == 'mode': selected.chmod(0o644)
                if mutation == 'missing': selected.unlink()
                if mutation == 'extra': self.save(root / 'canary', b'private-canary')
                if mutation == 'oversize':
                    with selected.open('r+b') as stream: stream.truncate(subject.PREFIX_LIMIT + 1)
                with self.assertRaises((RuntimeError, OSError)): self.read(parent)

    def test_unsafe_root_and_expired_deadline_reject(self):
        with tempfile.TemporaryDirectory() as temp:
            parent = Path(temp).resolve()
            root, _ = self.fixture(parent)
            root.chmod(0o755)
            with self.assertRaises(RuntimeError): self.read(parent)
            root.chmod(0o700)
            with self.assertRaises(RuntimeError):
                subject.read_failure(self.generation, 123, time.monotonic() - 1,
                                     parent=parent, uid=os.getuid(), gid=os.getgid())

    def test_bad_generation_and_pid_reject_before_open(self):
        for generation, pid in [('../canary', 123), ('a' * 31, 123), ('A' * 32, 123), (self.generation, 0), (self.generation, True)]:
            with self.subTest(generation=generation, pid=pid), patch.object(subject.os, 'open') as opened:
                with self.assertRaises(RuntimeError): subject.read_failure(generation, pid, time.monotonic() + 1)
                opened.assert_not_called()

    def test_new_fixed_partition_and_existing_matrix_use_separate_standard_reports(self):
        values = ['/evidence', '/tool', '/sdk', '/runtime', '/prepared', '/workspace', 'a' * 40, '/collector/coverlet.dll']
        target = arguments.failure_target(*values)
        self.assertEqual(['-B', '.github/issue779-checkpoint4/sdk_failure.py', 'run'], target[:3])
        self.assertNotIn('--case', target)
        original = arguments.build(*values)
        self.assertEqual('/evidence/native-sdk-failure.json', original[original.index('--merge-with') + 1])
        self.assertEqual('/evidence/native-sdk', original[-1])
        with self.assertRaises(ValueError): arguments.failure_target(*['relative', *values[1:]])

    def test_required_worker_handoffs_bind_to_this_failed_image_before_report_consumption(self):
        images = {name + '.dll': 'a' * 64 for name in arguments.MODULES}
        expected = {'worker_pid': 10, 'worker_uid': 11, 'worker_gid': 12, 'cgroup': '/system.slice/fixture.service'}
        worker = {'schema': 'evidence-worker-measurement-v2', 'normal_exit_code': 1,
                  'original_pidfd_exit': True, 'native_acceptance': False, 'start_time': 123,
                  'pid': 10, 'uid': 11, 'gid': 12, 'cgroup': expected['cgroup'],
                  'modules': [{'module': name, 'module_sha256': digest, 'outcome': 'unchanged-worker-handoff',
                               'worker_sha256': 'b' * 64, 'host_sha256': 'b' * 64} for name, digest in images.items()]}
        subject.require_worker(worker, expected, images)
        for field, value in [('pid', 99), ('normal_exit_code', 0), ('original_pidfd_exit', False)]:
            candidate = json.loads(json.dumps(worker)); candidate[field] = value
            with self.subTest(field=field), self.assertRaises(RuntimeError): subject.require_worker(candidate, expected, images)
        for field, value in [('module_sha256', 'c' * 64), ('host_sha256', 'c' * 64), ('outcome', 'worker-file-absent')]:
            candidate = json.loads(json.dumps(worker)); candidate['modules'][0][field] = value
            with self.subTest(field=field), self.assertRaises(RuntimeError): subject.require_worker(candidate, expected, images)

    def test_separate_report_requires_current_source_bound_failure_arm_before_it_can_be_merged(self):
        with tempfile.TemporaryDirectory() as temp:
            workspace = Path(temp).resolve()
            relative = 'Evidence/ForgeTrust.AppSurface.Evidence.Coverage/EvidenceRestrictedCoverageProducer.cs'
            path = workspace / relative; path.parent.mkdir(parents=True)
            body = b'first\nresult = CreateResult(producer, EvidenceProducerOutcome.Failed,\nThe restricted coverage process exited with code {process.ExitCode'
            path.write_bytes(body)
            capture = {'files': {relative: {'sha256': subject.hashlib.sha256(body).hexdigest()}}}
            report = {name + '.dll': {} for name in arguments.MODULES}
            report['ForgeTrust.AppSurface.Evidence.Coverage.dll'] = {'EvidenceRestrictedCoverageProducer.cs': {'Producer': {'RunAsync': {'Lines': {'2': 1}}}}}
            subject.require_failed_report(json.dumps(report).encode(), workspace, capture)
            report['ForgeTrust.AppSurface.Evidence.Coverage.dll']['EvidenceRestrictedCoverageProducer.cs']['Producer']['RunAsync']['Lines']['2'] = 0
            with self.assertRaises(RuntimeError): subject.require_failed_report(json.dumps(report).encode(), workspace, capture)
            report['ForgeTrust.AppSurface.Evidence.Coverage.dll']['EvidenceRestrictedCoverageProducer.cs']['Producer']['RunAsync']['Lines']['2'] = 1
            path.write_bytes(body + b'changed')
            with self.assertRaises(RuntimeError): subject.require_failed_report(json.dumps(report).encode(), workspace, capture)


if __name__ == '__main__':
    unittest.main()
