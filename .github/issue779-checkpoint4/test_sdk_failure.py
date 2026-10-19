"""Real small-file reader and closed argument data only; no collector, runtime owner or native claim."""
from pathlib import Path
import json
import io
import os
import tempfile
import time
import unittest
from contextlib import redirect_stdout
from unittest.mock import patch

import measurement_arguments as arguments
import sdk_failure as subject

FAILED_ROW = (b'[xUnit.net 00:00:01] NativePreparation.Subject.Tests.SubjectTests.'
              b'Calculator_branches_are_exercised(left: 1, right: 2, operation: 0, expected: 3) [FAIL]\n')


class FailureDiagnosticDataTests(unittest.TestCase):
    generation = 'a' * 32

    def test_failed_controller_projection_preserves_failure_and_discards_arbitrary_text(self):
        result = {'passed': False, 'root_exit': 1, 'groups_empty': True, 'final_groups_empty': True,
                  'failure_category': 'KeyError', 'exception': 'private-canary', 'raw_stdout': 'private-canary'}
        projected = subject.closed_case_result(result)
        self.assertFalse(projected['passed'])
        self.assertEqual('KeyError', projected['failure_category'])
        self.assertTrue(projected['groups_empty'])
        self.assertNotIn('private-canary', json.dumps(projected))
        result['failure_category'] = 'private-canary'; result['root_exit'] = True
        projected = subject.closed_case_result(result)
        self.assertEqual('other', projected['failure_category'])
        self.assertIsNone(projected['root_exit'])
        self.assertFalse(projected['native_acceptance'])

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
        self.save(root / 'stderr.prefix', FAILED_ROW)
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

    def test_summary_without_the_actual_quiet_failed_case_row_rejects(self):
        with tempfile.TemporaryDirectory() as temp:
            parent = Path(temp).resolve()
            root, _ = self.fixture(parent)
            self.save(root / 'stderr.prefix', b'other failure')
            with self.assertRaises(RuntimeError): self.read(parent)

    def test_actual_durable_marker_requires_sealed_private_root_ownership_and_exact_bytes(self):
        for mutation in ('valid', 'wrong-bytes', 'wrong-mode', 'hardlink', 'symlink', 'fifo', 'missing'):
            with self.subTest(mutation=mutation), tempfile.TemporaryDirectory() as temp:
                parent = Path(temp).resolve()
                root = parent / ('appsurface-evidence-producer-data-' + self.generation)
                root.mkdir(mode=0o711)
                scratch = root / 'scratch'; scratch.mkdir(mode=0o700)
                nested = scratch / 'tmp'; nested.mkdir(mode=0o700)
                marker = nested / 'checkpoint2-case-entered'
                marker.write_bytes(b'test-failure'); marker.chmod(0o600)
                if mutation == 'wrong-bytes':
                    marker.write_bytes(b'normal')
                if mutation == 'wrong-mode': marker.chmod(0o400)
                if mutation == 'hardlink': os.link(marker, parent / 'owned-other-link')
                if mutation == 'symlink': marker.unlink(); marker.symlink_to(parent / 'missing')
                if mutation == 'fifo': marker.unlink(); os.mkfifo(marker, 0o400)
                if mutation == 'missing': marker.unlink()
                read = lambda: subject.read_case_marker(self.generation, time.monotonic() + 5,
                    parent=parent, uid=os.getuid(), gid=os.getgid())
                if mutation == 'valid':
                    result = read()
                    self.assertEqual(12, result['bytes'])
                    self.assertTrue(result['sealed_root_owned'])
                    self.assertEqual(subject.hashlib.sha256(b'test-failure').hexdigest(), result['sha256'])
                else:
                    with self.assertRaises((RuntimeError, OSError)): read()

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

    def test_actual_support_binding_rejects_missing_unsafe_or_changed_private_measurement_bytes(self):
        for mutation in ('valid', 'missing', 'symlink', 'hardlink', 'fifo', 'empty', 'oversize', 'changed'):
            with self.subTest(mutation=mutation), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                for name in subject.SUPPORT_NAMES: (root / name).write_bytes(b'uninstrumented support sample')
                original = subject.support_rows(root)
                path = root / subject.SUPPORT_NAMES[0]
                if mutation == 'missing': path.unlink()
                if mutation == 'symlink': path.unlink(); path.symlink_to(root / subject.SUPPORT_NAMES[1])
                if mutation == 'hardlink': os.link(path, root / 'other')
                if mutation == 'fifo': path.unlink(); os.mkfifo(path, 0o600)
                if mutation == 'empty': path.write_bytes(b'')
                if mutation == 'oversize':
                    with path.open('r+b') as stream: stream.truncate(1024 * 1024 + 1)
                if mutation == 'changed':
                    path.write_bytes(b'different support bytes')
                    self.assertNotEqual(original, subject.support_rows(root))
                elif mutation == 'valid': self.assertEqual(original, subject.support_rows(root))
                else:
                    with self.assertRaises((RuntimeError, OSError)): subject.support_rows(root)

    def test_required_worker_handoffs_bind_to_this_failed_image_before_report_consumption(self):
        images = {name + '.dll': 'a' * 64 for name in arguments.MODULES}
        expected = {'worker_pid': 10, 'worker_uid': 11, 'worker_gid': 12, 'cgroup': '/system.slice/fixture.service'}
        worker = {'schema': 'evidence-worker-measurement-v3', 'normal_exit_code': 1,
                  'original_pidfd_exit': True, 'native_acceptance': False, 'start_time': 123,
                  'capture_phase': 'pre_exit_after_original_handlers', 'cached_original_pid_live': True,
                  'acknowledged_before_original_exit': True, 'barrier_nonce_sha256': 'c' * 64,
                  'pid': 10, 'uid': 11, 'gid': 12, 'cgroup': expected['cgroup'],
                  'modules': [{'module': name, 'module_sha256': digest, 'outcome': 'unchanged-worker-handoff',
                               'worker_sha256': 'b' * 64, 'cached_sha256': 'b' * 64, 'host_sha256': 'b' * 64} for name, digest in images.items()]}
        subject.require_worker(worker, expected, images)
        for field, value in [('pid', 99), ('normal_exit_code', 0), ('original_pidfd_exit', False),
                             ('schema', 'evidence-worker-measurement-v2'), ('capture_phase', 'terminal'),
                             ('cached_original_pid_live', False), ('acknowledged_before_original_exit', False),
                             ('barrier_nonce_sha256', 'canary')]:
            candidate = json.loads(json.dumps(worker)); candidate[field] = value
            with self.subTest(field=field), self.assertRaises(RuntimeError): subject.require_worker(candidate, expected, images)
        for field, value in [('module_sha256', 'c' * 64), ('host_sha256', 'c' * 64), ('cached_sha256', 'c' * 64), ('outcome', 'worker-file-absent')]:
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


class PassiveTmpEventDataTests(unittest.TestCase):
    watch = 7
    tracker = arguments.MODULES[0].encode('ascii') + b'_01234567-89ab-cdef-0123-456789abcdef'

    @staticmethod
    def record(name=b'', mask=0x100, watch=7, cookie=0):
        payload = name + b'\0' if name else b''
        payload += b'\0' * (-len(payload) % 4)
        return subject.EVENT_HEADER.pack(watch, mask, cookie, len(payload)) + payload

    def valid(self):
        data = subject.PassiveTmpEvents()
        data.feed(self.record(self.tracker, 0x100) + self.record(self.tracker, 0x8)
                  + self.record(self.tracker, 0x200), self.watch)
        self.assertFalse(data.parse_error)
        self.assertEqual((1, 1, 1), tuple(data.rows[0][key] for key in ('create', 'close_write', 'delete')))
        return data

    def test_module_prefix_guid_events_count_without_retaining_names_or_counter_bytes(self):
        data = self.valid()
        for i, module in enumerate(arguments.MODULES[1:], 1):
            name = module.encode('ascii') + b'_aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee'
            data.feed(self.record(name, 0x108), self.watch)
            self.assertEqual((1, 1, 0), tuple(data.rows[i][key] for key in ('create', 'close_write', 'delete')))
        self.assertNotIn('01234567', json.dumps(data.rows))
        self.assertEqual(8, data.events)

    def test_unknown_names_paths_uppercase_guids_and_directories_are_not_file_counts(self):
        self.valid()
        data = subject.PassiveTmpEvents()
        names = [b'private-canary', b'/tmp/' + self.tracker, self.tracker + b'.extra',
                 self.tracker.upper(), self.tracker.replace(b'01234567-', b'0123456-')]
        for name in names:
            data.feed(self.record(name), self.watch)
        data.feed(self.record(self.tracker, 0x40000100), self.watch)
        self.assertEqual(6, data.ignored_events)
        self.assertFalse(data.parse_error)
        self.assertTrue(all(row['create'] == row['close_write'] == row['delete'] == 0 for row in data.rows))
        self.assertNotIn('private-canary', json.dumps(data.__dict__))

    def test_overflow_is_explicit_and_later_records_do_not_become_absence_evidence(self):
        data = self.valid()
        data.feed(self.record(mask=0x4000, watch=-1) + self.record(self.tracker), self.watch)
        self.assertTrue(data.overflow)
        self.assertEqual(1, data.rows[0]['create'])
        data.feed(self.record(self.tracker), self.watch)
        self.assertEqual(1, data.rows[0]['create'])

    def test_kernel_watch_removal_closes_observation_without_a_delete_claim(self):
        data = self.valid()
        data.feed(self.record(mask=0x8000), self.watch)
        self.assertTrue(data.watch_lost)
        self.assertEqual(1, data.rows[0]['delete'])

    def test_malformed_headers_lengths_terminators_and_foreign_watch_close_parser(self):
        self.valid()
        invalid = [b'', b'x' * 15, b'x' * (subject.EVENT_BATCH_BYTES + 1),
                   subject.EVENT_HEADER.pack(self.watch, 0x100, 0, 4) + b'abc',
                   subject.EVENT_HEADER.pack(self.watch, 0x100, 0, 3) + b'a\0\0',
                   subject.EVENT_HEADER.pack(self.watch, 0x100, 0, 260) + b'\0' * 260,
                   subject.EVENT_HEADER.pack(self.watch, 0x100, 0, 4) + b'abcd',
                   subject.EVENT_HEADER.pack(self.watch, 0x100, 0, 4) + b'\0' * 4,
                   subject.EVENT_HEADER.pack(self.watch, 0x100, 0, 4) + b'a\0x\0',
                   self.record(self.tracker, watch=8), self.record(self.tracker, cookie=1),
                   self.record(self.tracker, mask=0x2), self.record(mask=0x100)]
        for payload in invalid:
            with self.subTest(size=len(payload)):
                data = subject.PassiveTmpEvents()
                data.feed(payload, self.watch)
                self.assertTrue(data.parse_error)
                data.feed(self.record(self.tracker), self.watch)
                self.assertEqual(0, data.rows[0]['create'])

    def test_batch_and_event_limits_are_lifetime_bounds(self):
        self.valid()
        data = subject.PassiveTmpEvents()
        for _ in range(subject.EVENT_BATCH_LIMIT + 1):
            data.feed(self.record(b'unknown'), self.watch)
        self.assertEqual(8, data.batches)
        self.assertTrue(data.limit_reached)
        data = subject.PassiveTmpEvents()
        batch = self.record(b'unknown') * 1024
        data.feed(batch, self.watch)
        self.assertFalse(data.limit_reached)
        data.feed(batch, self.watch)
        self.assertEqual(2048, data.events)
        self.assertTrue(data.limit_reached)
        data.feed(self.record(self.tracker), self.watch)
        self.assertEqual(2048, data.events)
        self.assertEqual(0, data.rows[0]['create'])

    def test_expired_original_deadline_reads_nothing_and_packet_never_proves_absence(self):
        observer = subject.PassiveTmpObserver()
        observer.armed, observer.unavailable, observer.end = True, False, 42
        output = io.StringIO()
        with patch.object(subject.time, 'monotonic', return_value=42), patch.object(subject.os, 'read') as read:
            with redirect_stdout(output):
                observer.finish()
        read.assert_not_called()
        packet = json.loads(output.getvalue())
        self.assertTrue(packet['deadline_reached'])
        for field in ('absence_proof', 'exact_tracker_binding', 'native_acceptance'):
            self.assertIs(packet[field], False)

    def test_unavailable_observer_emits_one_closed_packet_without_exception_text(self):
        observer = subject.PassiveTmpObserver()
        descriptor = {'worker_pid': 123, 'worker_uid': 10, 'worker_gid': 11, 'cgroup': 'private-canary'}
        attempts = []

        def unavailable_sample(*args):
            attempts.append(args)
            raise ValueError('private-canary')

        with patch.object(subject.platform, 'system', return_value='Linux'), patch.object(subject.os, 'open') as opened:
            observer.arm_once(descriptor, 'fixture.service', unavailable_sample, time.monotonic() + 1)
            observer.arm_once(descriptor, 'fixture.service', unavailable_sample, time.monotonic() + 1)
        opened.assert_not_called()
        self.assertEqual(1, len(attempts))
        output = io.StringIO()
        with redirect_stdout(output):
            observer.finish()
            observer.finish()
        self.assertEqual(1, len(output.getvalue().splitlines()))
        packet = json.loads(output.getvalue())
        self.assertTrue(packet['unavailable'])
        self.assertFalse(packet['armed'])
        self.assertNotIn('private-canary', output.getvalue())
        self.assertFalse(packet['absence_proof'])

    def test_close_failure_attempts_both_real_pipe_fds_and_does_not_echo_error(self):
        observer = subject.PassiveTmpObserver()
        read_fd, write_fd = os.pipe()
        observer.notification, observer.temporary = read_fd, write_fd
        close = os.close
        calls = []

        def fail_first(fd):
            calls.append(fd)
            if fd == read_fd:
                raise OSError('private-canary')
            close(fd)

        output = io.StringIO()
        try:
            with patch.object(subject.os, 'close', side_effect=fail_first), redirect_stdout(output):
                observer.finish()
            self.assertEqual([read_fd, write_fd], calls)
            self.assertTrue(json.loads(output.getvalue())['close_error'])
            self.assertNotIn('private-canary', output.getvalue())
        finally:
            close(read_fd)


if __name__ == '__main__':
    unittest.main()
