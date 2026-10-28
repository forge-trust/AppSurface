"""Credential-free replay-worker wire and custody tests."""
from __future__ import annotations

from contextlib import ExitStack, contextmanager
import errno
import fcntl
import hashlib
import os
from pathlib import Path
import stat
import sys
import tempfile
import time
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / '.github/issue779-protected-qualification'))
import completed_c1
import completed_controller
import completed_orchestration
import completed_run
import completed_source
import completed_wire
import completed_replay_worker as subject
import test_completed_controller


class CompletedReplayWorkerTests(unittest.TestCase):
    def setUp(self):
        self.controller = test_completed_controller.CompletedControllerTests()
        self.controller.setUp()
        self.addCleanup(self.controller.doCleanups)
        self.preparation = self.controller._prepared()
        self.root = Path(self.controller.temp.name).resolve()
        self.handoff = self._directory(self.root / 'handoff')
        self.capture_handoff = self._directory(self.root / 'capture-handoff')
        self.preparation_path = self.handoff / 'preparation.json'
        self.preparation_path.write_bytes(completed_wire.encode_preparation(self.preparation))
        os.chmod(self.preparation_path, 0o600)
        self.capture_path = self.capture_handoff / 'source-capture.json'
        self.capture_path.write_bytes(self.controller.fixture.capture)
        os.chmod(self.capture_path, 0o600)
        self.result_path = self.handoff / 'replay.json'
        self.deadline = time.monotonic() + subject.MAX_SECONDS - 1
        self.calls = []
        self.environment = patch.dict(os.environ, subject.ENVIRONMENT, clear=True)
        self.environment.start()
        self.addCleanup(self.environment.stop)
        self.facts = patch.object(subject, '_require_process_facts')
        self.facts.start()
        self.addCleanup(self.facts.stop)
        self.mount = patch.object(completed_source, 'SOURCE_MOUNT', self.controller.source)
        self.mount.start()
        self.addCleanup(self.mount.stop)
        strict_capture_fn = completed_source._strict_capture
        strict_capture = patch.object(
            completed_source, '_strict_capture',
            side_effect=lambda raw, head, **_kwargs: strict_capture_fn(
                raw, head, required_paths=frozenset()))
        strict_capture.start()
        self.addCleanup(strict_capture.stop)
        self.replay = patch.object(completed_orchestration, 'replay_completed_run',
                                   side_effect=self._replay)
        self.replay.start()
        self.addCleanup(self.replay.stop)

    @staticmethod
    def _directory(path):
        path.mkdir(mode=0o700)
        os.chmod(path, 0o700)
        return path

    def _replay(self, stage, stage_directory, source_capture, source_root,
                native_source_root, private_parent, *, deadline):
        self.calls.append((stage, stage_directory, source_capture, source_root,
                           native_source_root, private_parent, deadline))
        self.assertGreater(deadline, time.monotonic())
        native = self._directory(private_parent / 'native')
        gate = self._directory(private_parent / 'gate')
        jobs = completed_run.require_closed_jobs(stage)
        gate_job = next(row for row in jobs if row.name.endswith('Unchanged solution and codecov patch gates'))
        phases = tuple(completed_orchestration.PhaseObservation(name, hashlib.sha256(b'{}').hexdigest(), b'{}')
                       for name in completed_orchestration.PHASES)
        return completed_orchestration.CompletedRunReplay(
            stage.run_id, stage.run_attempt, stage.head_sha, stage.source_tree,
            stage.source_capture_sha256, phases, native, gate, gate_job.id)

    def _run(self):
        return subject.run_replay_worker(
            self.preparation_path, self.capture_path, self.controller.private,
            self.result_path, deadline=self.deadline)

    @contextmanager
    def _process_facts(self, *, linux=True, uids=(1001, 1001, 1001),
                       gids=(1002, 1002, 1002), groups=(1002,), effective_uid=1001, effective_gid=1002, status=None,
                       descriptors=(0, 1, 2), flags=None):
        status = status or {'CapEff': '0', 'CapPrm': '0', 'CapInh': '0',
                            'CapAmb': '0', 'NoNewPrivs': '1'}
        flags = {} if flags is None else flags
        with ExitStack() as stack:
            stack.enter_context(patch.object(subject.completed_native_replay, '_is_linux',
                                             return_value=linux))
            stack.enter_context(patch.object(os, 'getresuid', return_value=uids, create=True))
            stack.enter_context(patch.object(os, 'getresgid', return_value=gids, create=True))
            stack.enter_context(patch.object(os, 'geteuid', return_value=effective_uid))
            stack.enter_context(patch.object(os, 'getegid', return_value=effective_gid))
            stack.enter_context(patch.object(os, 'getgroups', return_value=list(groups)))
            stack.enter_context(patch.object(subject, '_proc_status', return_value=status))
            stack.enter_context(patch.object(subject, '_fd_numbers', return_value=descriptors))

            def getfd(fd, operation):
                self.assertEqual(fcntl.F_GETFD, operation)
                value = flags.get(fd, fcntl.FD_CLOEXEC)
                if value == 'ebadf':
                    raise OSError(errno.EBADF, 'descriptor closed during scan')
                return value

            stack.enter_context(patch.object(subject.fcntl, 'fcntl', side_effect=getfd))
            yield

    def test_process_facts_accept_valid_identity_and_ignore_transient_ebadf(self):
        self.facts.stop()
        with self._process_facts(descriptors=(0, 1, 2, 17, 18), flags={17: 'ebadf'}):
            subject._require_process_facts()

    def test_process_facts_reject_linux_identity_groups_capabilities_and_fd_variants(self):
        self.facts.stop()
        cases = (
            ('linux', {'linux': False}, 'linux-required'),
            ('uid', {'uids': (1001, 1002, 1001)}, 'single-unprivileged-identity'),
            ('gid', {'gids': (1002, 1003, 1002)}, 'single-unprivileged-identity'),
            ('root', {'uids': (0, 0, 0), 'effective_uid': 0}, 'single-unprivileged-identity'),
            ('root-group', {'gids': (0, 0, 0), 'effective_gid': 0}, 'single-unprivileged-identity'),
            ('groups', {'groups': (1002, 1003)}, 'supplementary-groups'),
            ('effective-capability', {'status': {'CapEff': '1', 'CapPrm': '0', 'CapInh': '0',
                                                  'CapAmb': '0', 'NoNewPrivs': '1'}},
             'process-privileges'),
            ('permitted-capability', {'status': {'CapEff': '0', 'CapPrm': '2', 'CapInh': '0',
                                                  'CapAmb': '0', 'NoNewPrivs': '1'}},
             'process-privileges'),
            ('inheritable-capability', {'status': {'CapEff': '0', 'CapPrm': '0', 'CapInh': '4',
                                                   'CapAmb': '0', 'NoNewPrivs': '1'}},
             'process-privileges'),
            ('ambient-capability', {'status': {'CapEff': '0', 'CapPrm': '0', 'CapInh': '0',
                                                'CapAmb': '8', 'NoNewPrivs': '1'}},
             'process-privileges'),
            ('no-new-privileges', {'status': {'CapEff': '0', 'CapPrm': '0', 'CapInh': '0',
                                               'CapAmb': '0', 'NoNewPrivs': '0'}},
             'process-privileges'),
            ('inheritable-fd', {'descriptors': (0, 1, 2, 23), 'flags': {23: 0}},
             'inheritable-descriptor'),
        )
        for label, overrides, error in cases:
            with self.subTest(label=label), self._process_facts(**overrides):
                with self.assertRaisesRegex(ValueError, error):
                    subject._require_process_facts()

    def test_real_inventory_fixed_orchestration_and_bounded_false_authority_result(self):
        result = self._run()

        self.assertEqual(1, len(self.calls))
        stage, stage_root, capture, source, native_source, private, deadline = self.calls[0]
        self.assertEqual(stage, self.preparation.stage)
        self.assertEqual((self.preparation.stage_directory, self.controller.fixture.capture,
                          self.controller.source, self.controller.native, self.controller.private),
                         (stage_root, capture, source, native_source, private))
        self.assertEqual(self.deadline, deadline)
        self.assertIsInstance(result, completed_orchestration.CompletedRunReplay)
        decoded = completed_wire.decode_replay(self.result_path.read_bytes())
        self.assertEqual(result.run_id, decoded.run_id)
        self.assertEqual(completed_orchestration.PHASES, tuple(row.phase for row in decoded.phases))
        for field in ('admission_created', 'native_acceptance', 'qualification_complete',
                      'gate_eligible', 'cleanup_complete'):
            self.assertFalse(getattr(decoded, field))
        info = self.result_path.lstat()
        self.assertTrue(stat.S_ISREG(info.st_mode))
        self.assertEqual(0o600, stat.S_IMODE(info.st_mode))
        self.assertEqual(1, info.st_nlink)
        self.assertEqual({self.preparation_path.name, self.result_path.name},
                         {path.name for path in self.handoff.iterdir()})

    def test_environment_rejected_before_any_input_or_orchestration(self):
        os.environ['GH_TOKEN'] = 'must-not-be-present'
        with self.assertRaisesRegex(ValueError, 'environment'):
            self._run()
        self.assertEqual([], self.calls)
        self.assertFalse(self.result_path.exists())

    def test_invalid_capture_binding_stops_before_orchestration(self):
        self.capture_path.write_bytes(b'{}')
        os.chmod(self.capture_path, 0o600)
        with self.assertRaises(ValueError):
            self._run()
        self.assertEqual([], self.calls)
        self.assertFalse(self.result_path.exists())

    def test_existing_result_is_preserved_and_callbacks_do_not_run(self):
        self.result_path.write_bytes(b'caller-owned')
        os.chmod(self.result_path, 0o600)
        with self.assertRaisesRegex(ValueError, 'result-exists'):
            self._run()
        self.assertEqual(b'caller-owned', self.result_path.read_bytes())
        self.assertEqual([], self.calls)

    def test_symlink_and_hardlinked_input_files_reject_before_callbacks(self):
        backup = self.handoff / 'preparation-copy.json'
        os.replace(self.preparation_path, backup)
        self.preparation_path.symlink_to(backup.name)
        with self.assertRaises(ValueError):
            self._run()
        self.preparation_path.unlink()
        os.replace(backup, self.preparation_path)
        self.assertEqual([], self.calls)

        linked = self.capture_handoff / 'linked-capture.json'
        os.link(self.capture_path, linked)
        try:
            with self.assertRaisesRegex(ValueError, 'source-capture-file-shape'):
                self._run()
            self.assertEqual([], self.calls)
        finally:
            linked.unlink()

    def test_nonempty_or_overlapping_private_parent_rejected_before_orchestration(self):
        marker = self.controller.private / 'caller-file'
        marker.write_bytes(b'keep')
        with self.assertRaisesRegex(ValueError, 'private-parent-not-empty'):
            self._run()
        self.assertEqual([], self.calls)
        self.assertEqual(b'keep', marker.read_bytes())
        marker.unlink()

        with self.assertRaises(ValueError):
            subject.run_replay_worker(self.preparation_path, self.capture_path,
                                      self.handoff, self.result_path, deadline=self.deadline)
        self.assertEqual([], self.calls)

    def test_callback_failure_preserves_primary_and_leaves_no_result(self):
        self.replay.stop()
        self.replay = patch.object(completed_orchestration, 'replay_completed_run',
                                   side_effect=RuntimeError('fixed replay failure'))
        self.replay.start()
        self.addCleanup(self.replay.stop)
        with self.assertRaisesRegex(RuntimeError, 'fixed replay failure'):
            self._run()
        self.assertFalse(self.result_path.exists())

    def test_input_mutation_during_output_write_removes_only_owned_result(self):
        original = subject._write_result

        def mutate_after_write(path, raw, *, deadline):
            identity = original(path, raw, deadline=deadline)
            self.capture_path.write_bytes(b'tampered after first custody check')
            os.chmod(self.capture_path, 0o600)
            return identity

        with patch.object(subject, '_write_result', side_effect=mutate_after_write):
            with self.assertRaisesRegex(ValueError, 'input-(fd-)?changed'):
                self._run()
        self.assertFalse(self.result_path.exists())
        self.assertEqual(1, len(self.calls))

    def test_replaced_result_after_write_is_detected_and_foreign_node_preserved(self):
        original = subject._write_result
        foreign = self.root / 'foreign-result'
        foreign.write_bytes(b'foreign-result')
        os.chmod(foreign, 0o600)

        def replace_after_write(path, raw, *, deadline):
            identity = original(path, raw, deadline=deadline)
            held = os.open(path, os.O_RDONLY | os.O_CLOEXEC)
            try:
                os.unlink(path)
                os.replace(foreign, path)
            finally:
                os.close(held)
            return identity

        with patch.object(subject, '_write_result', side_effect=replace_after_write):
            with self.assertRaisesRegex(ValueError, 'result-file-changed'):
                self._run()
        self.assertEqual(b'foreign-result', self.result_path.read_bytes())

    def test_result_write_failure_removes_only_partial_owned_file(self):
        partial = self.handoff / 'partial.json'
        with patch.object(subject.os, 'write', side_effect=OSError('fixed write failure')):
            with self.assertRaisesRegex(OSError, 'fixed write failure'):
                subject._write_result(partial, b'{}', deadline=self.deadline)
        self.assertFalse(partial.exists())

    def test_result_fd_close_failure_removes_owned_result_and_closes_parent(self):
        path = self.handoff / 'close-failure.json'
        real_open, real_close = os.open, os.close
        opened = []
        directory_open_count = 0

        def tracked_open(name, *args, **kwargs):
            nonlocal directory_open_count
            fd = real_open(name, *args, **kwargs)
            if Path(name) == self.handoff:
                directory_open_count += 1
                if directory_open_count == 2:
                    opened.append((Path(name), fd))
            elif name == path.name:
                opened.append((Path(name), fd))
            return fd

        def close_then_fail(fd):
            real_close(fd)
            if opened and fd == opened[-1][1]:
                raise OSError('fixed descriptor close failure')

        with patch.object(subject.os, 'open', side_effect=tracked_open), \
                patch.object(subject.os, 'close', side_effect=close_then_fail):
            with self.assertRaisesRegex(OSError, 'fixed descriptor close failure'):
                subject._write_result(path, b'{}', deadline=self.deadline)
        self.assertFalse(path.exists())
        self.assertGreaterEqual(len(opened), 2)
        for _name, fd in opened:
            with self.assertRaises(OSError):
                os.fstat(fd)

    def test_result_primary_write_error_survives_descriptor_close_error(self):
        path = self.handoff / 'primary-close-error.json'
        real_open, real_close = os.open, os.close
        tracked = []
        directory_open_count = 0
        close_failures = []

        def tracked_open(name, *args, **kwargs):
            nonlocal directory_open_count
            fd = real_open(name, *args, **kwargs)
            if Path(name) == self.handoff:
                directory_open_count += 1
                if directory_open_count == 2:
                    tracked.append(fd)
            elif name == path.name:
                tracked.append(fd)
            return fd

        def close_then_fail(fd):
            real_close(fd)
            if fd in tracked:
                close_failures.append(fd)
                raise OSError('secondary close error')

        with patch.object(subject.os, 'open', side_effect=tracked_open), \
                patch.object(subject.os, 'write', side_effect=OSError('primary write error')), \
                patch.object(subject.os, 'close', side_effect=close_then_fail):
            with self.assertRaisesRegex(OSError, 'primary write error') as caught:
                subject._write_result(path, b'{}', deadline=self.deadline)
        self.assertFalse(path.exists())
        self.assertEqual(2, len(tracked))
        self.assertEqual(set(tracked), set(close_failures))

    def test_read_input_preserves_read_error_and_attempts_both_closes(self):
        source = self.handoff / 'broken-input.json'
        source.write_bytes(b'{"x":1}')
        os.chmod(source, 0o600)
        real_open, real_close = os.open, os.close
        descriptors = []
        close_attempts = []

        def tracked_open(name, *args, **kwargs):
            fd = real_open(name, *args, **kwargs)
            if name in (self.handoff, source.name):
                descriptors.append(fd)
            return fd

        def close_and_fail(fd):
            close_attempts.append(fd)
            real_close(fd)
            raise OSError('secondary input close error')

        with patch.object(subject.os, 'open', side_effect=tracked_open), \
                patch.object(subject, '_remaining', side_effect=OSError('primary input read error')), \
                patch.object(subject.os, 'close', side_effect=close_and_fail):
            with self.assertRaisesRegex(OSError, 'primary input read error') as caught:
                subject._read_input(source, maximum=1024, code='test-input', deadline=self.deadline)
        self.assertEqual(2, len(descriptors))
        self.assertEqual(set(descriptors), set(close_attempts))
        for fd in descriptors:
            with self.assertRaises(OSError):
                os.fstat(fd)

    def test_pin_context_exit_failures_remove_published_owned_result(self):
        for module, name in ((completed_controller, '_pin_roots'),
                             (completed_controller, '_pin_archives')):
            with self.subTest(context=name):
                original = getattr(module, name)

                @contextmanager
                def fail_after_exit(*args, **kwargs):
                    with original(*args, **kwargs) as value:
                        yield value
                    raise RuntimeError('fixed pin context exit failure')

                with patch.object(module, name, side_effect=fail_after_exit):
                    with self.assertRaisesRegex(RuntimeError, 'fixed pin context exit failure'):
                        self._run()
                self.assertFalse(self.result_path.exists())
                self.assertEqual(1, len(self.calls))
                self.calls.clear()
                self.controller.private.joinpath('native').rmdir()
                self.controller.private.joinpath('gate').rmdir()

    def test_pin_context_exit_failure_preserves_substituted_result(self):
        original = completed_controller._pin_archives
        foreign = self.root / 'foreign-after-pin'
        foreign.write_bytes(b'foreign after result publication')
        os.chmod(foreign, 0o600)

        @contextmanager
        def replace_then_fail(*args, **kwargs):
            with original(*args, **kwargs) as value:
                yield value
            os.unlink(self.result_path)
            os.replace(foreign, self.result_path)
            raise RuntimeError('fixed pin context exit failure')

        with patch.object(completed_controller, '_pin_archives', side_effect=replace_then_fail):
            with self.assertRaisesRegex(RuntimeError, 'fixed pin context exit failure'):
                self._run()
        self.assertEqual(b'foreign after result publication', self.result_path.read_bytes())

    def test_cli_accepts_exactly_four_absolute_paths(self):
        with patch.object(subject, 'run_replay_worker') as run:
            self.assertEqual(2, subject.main(['relative', '/capture', '/private', '/result']))
            run.assert_not_called()
            before = time.monotonic()
            self.assertEqual(0, subject.main(['/prep', '/capture', '/private', '/result']))
        args, kwargs = run.call_args
        self.assertEqual(('/prep', '/capture', '/private', '/result'), args)
        self.assertGreaterEqual(kwargs['deadline'], before + subject.MAX_SECONDS)


if __name__ == '__main__':
    unittest.main()
