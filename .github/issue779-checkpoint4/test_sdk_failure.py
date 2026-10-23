"""Real small-file reader and closed argument data only; no collector, runtime owner or native claim."""
from pathlib import Path
import json
import io
import os
import tempfile
import time
import unittest
from contextlib import redirect_stdout, redirect_stderr
from types import SimpleNamespace
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
        shared = 'ForgeTrust.AppSurface.Evidence.Cli.dll'
        self.assertEqual(7, len(worker['modules']))
        for mutation in ('missing', 'duplicate', 'alias', 'image', 'host', 'cached', 'absent', 'prior'):
            candidate = json.loads(json.dumps(worker))
            row = next(item for item in candidate['modules'] if item['module'] == shared)
            if mutation == 'missing':
                candidate['modules'].remove(row)
            elif mutation == 'duplicate':
                row.update(candidate['modules'][0])
            elif mutation == 'alias':
                row['module'] = shared[:-4]
            elif mutation in ('image', 'host', 'cached'):
                row[{'image': 'module_sha256', 'host': 'host_sha256', 'cached': 'cached_sha256'}[mutation]] = 'd' * 64
            else:
                row['outcome'] = 'worker-file-absent' if mutation == 'absent' else 'prior-root-file-unchanged'
            with self.subTest(shared_cli=mutation), self.assertRaises(RuntimeError):
                subject.require_worker(candidate, expected, images)

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
            shared = 'ForgeTrust.AppSurface.Evidence.Cli.dll'
            for mutation in ('missing', 'duplicate-basename', 'alias'):
                changed = json.loads(json.dumps(report))
                del changed[shared]
                if mutation == 'duplicate-basename':
                    changed['/other/ForgeTrust.AppSurface.Cli.dll'] = {}
                elif mutation == 'alias':
                    changed[shared[:-4]] = {}
                with self.subTest(shared_cli=mutation), self.assertRaisesRegex(RuntimeError, 'report-modules'):
                    subject.require_failed_report(json.dumps(changed).encode(), workspace, capture)
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
        self.assertEqual(9, data.events)

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


class WorkerStartupDiagnosticDataTests(unittest.TestCase):
    """Real temporary diagnostic bytes only; no worker, account, descriptor or owner is constructed."""
    generation = 'a' * 32
    payloads = {'stderr.prefix': b'worker-private-canary', 'stdout.prefix': b'worker stdout', 'state.json': b'{"closed":"data"}'}

    @staticmethod
    def save(path, body):
        path.write_bytes(body); path.chmod(0o600)

    def fixture(self, temporary):
        directory = temporary / 'worker-diagnostics'; directory.mkdir(mode=0o700)
        for name, body in self.payloads.items():
            self.save(directory / ('sdk-test-failure-worker-' + name), body)
        leaves = subject.read_worker_stage(directory, uid=os.getuid(), gid=os.getgid())
        packet = {'schema': subject.WORKER_DIAGNOSTIC_SCHEMA, 'files': {
            name: {'bytes': len(body), 'sha256': subject.hashlib.sha256(body).hexdigest()} for name, body in leaves.items()}}
        self.save(directory / 'complete.json', json.dumps(packet).encode())
        return directory

    def append(self, temporary, log, maximum=8 * 1024 * 1024):
        subject.append_worker_diagnostics(temporary, log, maximum, uid=os.getuid(), gid=os.getgid())

    def test_exact_returned_generation_and_worker_role_preserve_the_original_result(self):
        with tempfile.TemporaryDirectory() as value:
            temporary = Path(value); calls = []
            result = {'generation': self.generation, 'passed': False, 'failure_category': 'original-private-canary'}
            original = json.dumps(result, sort_keys=True)
            def retain(generation, output, case, role):
                calls.append((generation, case, role))
                for name, body in self.payloads.items(): self.save(output / (case + '-' + role + '-' + name), body)
                return True
            r = SimpleNamespace(c2=SimpleNamespace(retain_producer_prefixes=retain, write=self.save))
            subject.retain_worker_diagnostics(r, result, temporary, time.monotonic() + 10, uid=os.getuid(), gid=os.getgid())
            self.assertEqual([(self.generation, 'sdk-test-failure', 'worker')], calls)
            self.assertEqual(original, json.dumps(result, sort_keys=True))
            self.assertEqual(self.payloads, subject.read_worker_stage(temporary / 'worker-diagnostics', uid=os.getuid(), gid=os.getgid(), complete=True))

    def test_invalid_missing_and_expired_generation_selection_never_calls_the_reader(self):
        for result, end in (({}, time.monotonic() + 10), ({'generation': '../private-canary'}, time.monotonic() + 10),
                            ({'generation': 'A' * 32}, time.monotonic() + 10), ({'generation': self.generation}, time.monotonic() - 1)):
            with self.subTest(result=result), tempfile.TemporaryDirectory() as value:
                with patch.object(subject, 'read_worker_stage') as copied:
                    r = SimpleNamespace(c2=SimpleNamespace(retain_producer_prefixes=unittest.mock.Mock()))
                    subject.retain_worker_diagnostics(r, result, Path(value), end)
                r.c2.retain_producer_prefixes.assert_not_called(); copied.assert_not_called()

    def test_partial_copy_or_reader_error_is_not_appended_or_echoed(self):
        with tempfile.TemporaryDirectory() as value:
            temporary = Path(value); log = temporary / 'combined.log'; self.save(log, b'original')
            result = {'generation': self.generation, 'passed': False, 'root_exit': 1, 'failure_category': 'KeyError'}
            before = subject.closed_case_result(result)
            def partial(generation, output, case, role):
                self.save(output / (case + '-' + role + '-stderr.prefix'), b'private-canary')
                raise OSError('private-canary')
            r = SimpleNamespace(c2=SimpleNamespace(retain_producer_prefixes=partial, write=self.save))
            public = io.StringIO()
            with redirect_stdout(public), redirect_stderr(public):
                subject.retain_worker_diagnostics(r, result, temporary, time.monotonic() + 10, uid=os.getuid(), gid=os.getgid())
                self.append(temporary, log)
            self.assertEqual(b'original', log.read_bytes()); self.assertEqual('', public.getvalue())
            self.assertEqual(before, subject.closed_case_result(result))

    def test_three_fixed_private_sections_add_no_retained_files_or_public_canary(self):
        with tempfile.TemporaryDirectory() as value:
            temporary = Path(value); self.fixture(temporary)
            evidence = temporary / 'evidence'; evidence.mkdir(mode=0o700)
            log = evidence / 'sdk-test-failure-diagnostics.log'; self.save(log, b'collector plus owner')
            public = io.StringIO()
            with redirect_stdout(public), redirect_stderr(public): self.append(temporary, log)
            body = log.read_bytes()
            self.assertTrue(body.startswith(b'collector plus owner'))
            for name, payload in self.payloads.items():
                self.assertEqual(1, body.count(('Original CSharp worker ' + name + ' (private):').encode()))
                self.assertIn(payload, body)
            self.assertEqual('', public.getvalue()); self.assertEqual([log], list(evidence.iterdir()))

    def test_one_bad_copied_node_or_completion_field_never_enters_log(self):
        for mutation in ('mode', 'hardlink', 'symlink', 'fifo', 'extra', 'missing', 'digest', 'schema'):
            with self.subTest(mutation=mutation), tempfile.TemporaryDirectory() as value:
                temporary = Path(value); directory = self.fixture(temporary)
                subject.read_worker_stage(directory, uid=os.getuid(), gid=os.getgid(), complete=True)
                log = temporary / 'combined.log'; self.save(log, b'original')
                leaf = directory / 'sdk-test-failure-worker-stderr.prefix'
                if mutation == 'mode': leaf.chmod(0o644)
                elif mutation == 'hardlink': os.link(leaf, temporary / 'outside')
                elif mutation == 'symlink': leaf.unlink(); leaf.symlink_to(log)
                elif mutation == 'fifo': leaf.unlink(); os.mkfifo(leaf)
                elif mutation == 'extra': self.save(directory / 'extra', b'private-canary')
                elif mutation == 'missing': leaf.unlink()
                else:
                    path = directory / 'complete.json'; packet = json.loads(path.read_bytes())
                    if mutation == 'digest': packet['files']['stderr.prefix']['sha256'] = '0' * 64
                    else: packet['schema'] = 'private-canary'
                    self.save(path, json.dumps(packet).encode())
                self.append(temporary, log); self.assertEqual(b'original', log.read_bytes())

    def test_combined_ceiling_truncates_explicitly_or_leaves_a_full_log_unchanged(self):
        with tempfile.TemporaryDirectory() as value:
            temporary = Path(value); self.fixture(temporary)
            log = temporary / 'combined.log'; self.save(log, b'o' * 100)
            self.append(temporary, log, 256)
            body = log.read_bytes(); self.assertEqual(256, len(body))
            self.assertTrue(body.endswith(b'Worker diagnostics truncated at existing combined-log bound.\n'))
            self.append(temporary, log, 256); self.assertEqual(body, log.read_bytes())

    def test_unsafe_destination_is_not_written_or_echoed(self):
        for mutation in ('symlink', 'hardlink', 'fifo'):
            with self.subTest(mutation=mutation), tempfile.TemporaryDirectory() as value:
                temporary = Path(value); self.fixture(temporary)
                outside = temporary / 'outside'; self.save(outside, b'outside-canary')
                log = temporary / 'combined.log'
                if mutation == 'symlink': log.symlink_to(outside)
                elif mutation == 'hardlink': os.link(outside, log)
                else: os.mkfifo(log)
                public = io.StringIO()
                with redirect_stdout(public), redirect_stderr(public): self.append(temporary, log)
                self.assertEqual(b'outside-canary', outside.read_bytes()); self.assertEqual('', public.getvalue())

    def test_finalize_retains_worker_bytes_before_preserving_the_original_collector_failure(self):
        with tempfile.TemporaryDirectory() as value:
            parent = Path(value); temporary = parent / 'checkpoint3-sdk-failure-work'; temporary.mkdir(mode=0o700)
            self.fixture(temporary)
            self.save(temporary / 'sdk-test-failure-owner.stderr', b'owner diagnostics')
            evidence = parent / 'evidence'; evidence.mkdir(mode=0o700)
            log = evidence / 'sdk-test-failure-diagnostics.log'; self.save(log, b'collector diagnostics')
            append = subject.append_worker_diagnostics
            def portable_append(work, destination, maximum):
                append(work, destination, maximum, uid=os.getuid(), gid=os.getgid())
            with patch.object(subject, 'driver', return_value=SimpleNamespace(MAX_LOG=8 * 1024 * 1024)), patch.object(subject, 'append_worker_diagnostics', side_effect=portable_append):
                with self.assertRaisesRegex(RuntimeError, '^sdk-failure-collector-exit$'):
                    subject.finalize(SimpleNamespace(evidence=evidence, collector_exit=1))
            self.assertIn(b'worker-private-canary', log.read_bytes())
            self.assertFalse((evidence / 'native').exists())



class SupportDependencyDataTests(unittest.TestCase):
    """Actual small files with the published CLI graph shape; no CLR loading or native authority."""

    @staticmethod
    def fixture():
        helper = subject.SUPPORT_LIBRARY
        deps = {'runtimeTarget': {'name': '.NETCoreApp,Version=v10.0', 'signature': ''},
                'targets': {'.NETCoreApp,Version=v10.0': {
                    'ForgeTrust.AppSurface.Cli/0.1.0': {'dependencies': {subject.SUPPORT_ASSEMBLY: '0.2.0.0'}},
                    helper: {'runtime': {subject.SUPPORT_ASSEMBLY + '.dll': {
                        'assemblyVersion': '0.2.0.0', 'fileVersion': '0.2.0.0'}},
                        'compile': {subject.SUPPORT_ASSEMBLY + '.dll': {}}},
                    'Other/1.0.0': {'runtime': {'Case.dll': {}, 'case.dll': {}}}}},
                'libraries': {'ForgeTrust.AppSurface.Cli/0.1.0': {'type': 'project'},
                              helper: {'type': 'reference', 'serviceable': False, 'sha512': ''},
                              'Other/1.0.0': {'type': 'package'}},
                'private-canary': 'discarded'}
        config = {'runtimeOptions': {'tfm': 'net10.0', 'frameworks': [
            {'name': 'Microsoft.NETCore.App', 'version': '10.0.0'},
            {'name': 'Microsoft.AspNetCore.App', 'version': '10.0.0'}],
            'configProperties': {'System.Reflection.Metadata.MetadataUpdater.IsSupported': False}}}
        return deps, config

    @staticmethod
    def save(root, deps, config):
        for name, value in zip(subject.SUPPORT_RUNTIME_FILES, (deps, config)):
            (root / name).write_bytes(json.dumps(value).encode())

    def test_real_files_return_only_fixed_metadata_and_exact_byte_bindings(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); self.save(root, *self.fixture())
            with redirect_stdout(io.StringIO()) as out, redirect_stderr(io.StringIO()) as err:
                result = subject.support_dependency_data(root)
            self.assertEqual('', out.getvalue() + err.getvalue())
            self.assertEqual('issue779-sdk-support-dependency-v1', result['schema'])
            self.assertEqual(subject.SUPPORT_LIBRARY, result['helper_library'])
            self.assertFalse(result['native_acceptance'])
            self.assertNotIn('private-canary', json.dumps(result))
            for name, row in result['files'].items():
                body = (root / name).read_bytes()
                self.assertEqual({'bytes': len(body), 'sha256': subject.hashlib.sha256(body).hexdigest()}, row)

    def test_each_single_graph_mutation_rejects_after_the_valid_neighbor(self):
        for mutation in ('cli-dependency', 'cli-dependency-version', 'runtime-entry', 'library',
                         'reference-type', 'assembly-version', 'file-version', 'runtime-target',
                         'cli-root-missing', 'cli-root-duplicate', 'helper-version-duplicate',
                         'runtime-entry-type', 'graph-type', 'tfm', 'framework-version',
                         'framework-duplicate', 'framework-type', 'runtime-options-type'):
            with self.subTest(mutation=mutation), tempfile.TemporaryDirectory() as temp:
                root = Path(temp); deps, config = self.fixture(); self.save(root, deps, config)
                subject.support_dependency_data(root)
                target = deps['targets']['.NETCoreApp,Version=v10.0']
                helper = target[subject.SUPPORT_LIBRARY]
                if mutation == 'cli-dependency': target['ForgeTrust.AppSurface.Cli/0.1.0']['dependencies'].pop(subject.SUPPORT_ASSEMBLY)
                if mutation == 'cli-dependency-version': target['ForgeTrust.AppSurface.Cli/0.1.0']['dependencies'][subject.SUPPORT_ASSEMBLY] = '0.1.0.0'
                if mutation == 'runtime-entry': helper['runtime'].pop(subject.SUPPORT_ASSEMBLY + '.dll')
                if mutation == 'library': deps['libraries'].pop(subject.SUPPORT_LIBRARY)
                if mutation == 'reference-type': deps['libraries'][subject.SUPPORT_LIBRARY]['type'] = 'package'
                if mutation == 'assembly-version': helper['runtime'][subject.SUPPORT_ASSEMBLY + '.dll']['assemblyVersion'] = '0.1.0.0'
                if mutation == 'file-version': helper['runtime'][subject.SUPPORT_ASSEMBLY + '.dll']['fileVersion'] = '0.1.0.0'
                if mutation == 'runtime-target': deps['runtimeTarget']['name'] = 'private-canary'
                if mutation == 'cli-root-missing': deps['libraries'].pop('ForgeTrust.AppSurface.Cli/0.1.0')
                if mutation == 'cli-root-duplicate': deps['libraries']['ForgeTrust.AppSurface.Cli/0.2.0'] = {'type': 'project'}
                if mutation == 'helper-version-duplicate': deps['libraries'][subject.SUPPORT_ASSEMBLY + '/0.1.0.0'] = {'type': 'reference'}
                if mutation == 'runtime-entry-type': helper['runtime'] = []
                if mutation == 'graph-type': deps['targets'] = []
                if mutation == 'tfm': config['runtimeOptions']['tfm'] = 'net9.0'
                if mutation == 'framework-version': config['runtimeOptions']['frameworks'][0]['version'] = '9.0.0'
                if mutation == 'framework-duplicate': config['runtimeOptions']['frameworks'][1] = config['runtimeOptions']['frameworks'][0]
                if mutation == 'framework-type': config['runtimeOptions']['frameworks'] = {}
                if mutation == 'runtime-options-type': config['runtimeOptions'] = []
                self.save(root, deps, config)
                with self.assertRaisesRegex(RuntimeError, '^sdk-failure-support-runtime-') as error:
                    subject.support_dependency_data(root)
                self.assertNotIn('private-canary', str(error.exception))

    def test_duplicate_exact_members_and_malformed_json_reject_without_casefolding_assets(self):
        for name in subject.SUPPORT_RUNTIME_FILES:
            for mutation in ('duplicate-root', 'duplicate-nested', 'malformed', 'constant', 'wrong-root'):
                with self.subTest(name=name, mutation=mutation), tempfile.TemporaryDirectory() as temp:
                    root = Path(temp); self.save(root, *self.fixture()); subject.support_dependency_data(root)
                    path = root / name; body = path.read_bytes()
                    if mutation == 'duplicate-root': body = b'{"same":1,"same":2}'
                    if mutation == 'duplicate-nested': body = b'{"nested":{"same":1,"same":2}}'
                    if mutation == 'malformed': body = b'{private-canary'
                    if mutation == 'constant': body = b'{"other":NaN}'
                    if mutation == 'wrong-root': body = b'[]'
                    path.write_bytes(body)
                    with self.assertRaisesRegex(RuntimeError, '^sdk-failure-support-runtime-'):
                        subject.support_dependency_data(root)

    def test_nonregular_link_missing_empty_and_oversize_files_reject_before_reading_fifo(self):
        for name, maximum in subject.SUPPORT_RUNTIME_FILES.items():
            for mutation in ('directory', 'symlink', 'hardlink', 'fifo', 'missing', 'empty', 'oversize'):
                with self.subTest(name=name, mutation=mutation), tempfile.TemporaryDirectory() as temp:
                    root = Path(temp); self.save(root, *self.fixture()); subject.support_dependency_data(root)
                    path = root / name
                    if mutation == 'directory': path.unlink(); path.mkdir()
                    if mutation == 'symlink': path.unlink(); path.symlink_to(root / 'private-canary')
                    if mutation == 'hardlink': os.link(path, root / 'private-canary')
                    if mutation == 'fifo': path.unlink(); os.mkfifo(path)
                    if mutation == 'missing': path.unlink()
                    if mutation == 'empty': path.write_bytes(b'')
                    if mutation == 'oversize':
                        with path.open('r+b') as stream: stream.truncate(maximum + 1)
                    with self.assertRaisesRegex(RuntimeError, '^sdk-failure-support-runtime-file$') as error:
                        subject.support_dependency_data(root)
                    self.assertIsNone(error.exception.__cause__)

    def test_actual_midread_growth_or_named_replacement_rejects_and_closes_read_fds(self):
        for mutation in ('growth', 'replacement'):
            with self.subTest(mutation=mutation), tempfile.TemporaryDirectory() as temp:
                root = Path(temp); self.save(root, *self.fixture()); subject.support_dependency_data(root)
                original_read = os.read; fds = set(); changed = False
                path = root / 'ForgeTrust.AppSurface.Cli.deps.json'
                def read(fd, count):
                    nonlocal changed
                    body = original_read(fd, count); fds.add(fd)
                    if body and not changed:
                        changed = True
                        if mutation == 'growth':
                            with path.open('ab') as stream: stream.write(b' ')
                        else:
                            replacement = root / 'replacement'; replacement.write_bytes(path.read_bytes())
                            replacement.replace(path)
                    return body
                with patch.object(subject.os, 'read', side_effect=read):
                    with self.assertRaisesRegex(RuntimeError, '^sdk-failure-support-runtime-stability$'):
                        subject.support_dependency_data(root)
                self.assertTrue(changed)
                for fd in fds:
                    with self.assertRaises(OSError): os.fstat(fd)

    def test_second_file_read_cannot_hide_a_change_to_the_first_file(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); self.save(root, *self.fixture()); subject.support_dependency_data(root)
            original = subject._read_dependency_leaf
            def read(fd, name, maximum):
                result = original(fd, name, maximum)
                if name.endswith('runtimeconfig.json'):
                    path = root / 'ForgeTrust.AppSurface.Cli.deps.json'
                    path.write_bytes(path.read_bytes() + b' ')
                return result
            with patch.object(subject, '_read_dependency_leaf', side_effect=read):
                with self.assertRaisesRegex(RuntimeError, '^sdk-failure-support-runtime-stability$'):
                    subject.support_dependency_data(root)

    def test_changed_valid_bytes_change_binding_without_rewriting_either_file(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); deps, config = self.fixture(); self.save(root, deps, config)
            original = subject.support_dependency_data(root)
            config['runtimeOptions']['configProperties']['unrelated'] = True
            self.save(root, deps, config)
            before = {name: (root / name).read_bytes() for name in subject.SUPPORT_RUNTIME_FILES}
            changed = subject.support_dependency_data(root)
            self.assertNotEqual(original, changed)
            self.assertEqual(before, {name: (root / name).read_bytes() for name in subject.SUPPORT_RUNTIME_FILES})


if __name__ == '__main__':
    unittest.main()
