"""Fixed credentialed entrypoint tests; all provider/controller work is mocked."""
from __future__ import annotations

import contextlib
import io
import json
import os
from pathlib import Path
import shutil
import sys
import tempfile
import time
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / '.github/issue779-protected-qualification'))
import completed_controller
import completed_entrypoint as subject
import completed_run
import completed_wire
import test_completed_controller
import test_completed_run


class CompletedEntrypointTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name).resolve()
        self.inputs = self._directory('inputs')
        self.output = self._directory('output')
        self.event = test_completed_run.CompletedRunDataTests()
        self.event.setUp()
        self.addCleanup(self.event.doCleanups)
        self.event_raw = json.dumps(self.event.event, separators=(',', ':')).encode()
        self.context = (self.event.repo_id, self.event.run_id, self.event.attempt,
                        self.event.head, 12345, 'c' * 40)
        self.environment = {
            'GITHUB_ACTIONS': 'true', 'GITHUB_EVENT_NAME': 'workflow_run',
            'GITHUB_REPOSITORY': completed_run.REPOSITORY,
            'GITHUB_REPOSITORY_ID': str(self.event.repo_id), 'GITHUB_REF': 'refs/heads/main',
            'GITHUB_REF_PROTECTED': 'true',
            'GITHUB_WORKFLOW_REF': completed_run.REPOSITORY + '/'
            + '.github/workflows/evidencehost-completed-validation.yml@refs/heads/main',
            'GITHUB_SHA': 'a' * 40, 'GITHUB_WORKFLOW_SHA': 'a' * 40,
            'GH_TOKEN': 'token-secret-must-never-be-printed',
        }
        self.event_path = self._write('event.json', self.event_raw, 0o644)
        self.validator_path = self._write('validator.json', b'validator-capture', 0o600)
        self.source_path = self._write('source.json', b'source-capture', 0o600)
        self.controller_fixture = test_completed_controller.CompletedControllerTests()
        self.controller_fixture.setUp()
        self.addCleanup(self.controller_fixture.doCleanups)
        self.preparation = self.controller_fixture._prepared()
        stage = self.preparation.stage
        for repository in (self.event.event['repository'],
                           self.event.event['workflow_run']['repository'],
                           self.event.event['workflow_run']['head_repository']):
            repository['id'] = stage.repository_id
            repository['full_name'] = completed_run.REPOSITORY
        run = self.event.event['workflow_run']
        run.update({'id': stage.run_id, 'run_attempt': stage.run_attempt,
                    'head_sha': stage.head_sha, 'workflow_id': stage.provider_run.workflow_id})
        self.event_raw = json.dumps(self.event.event, separators=(',', ':')).encode()
        self.event_path.write_bytes(self.event_raw)
        os.chmod(self.event_path, 0o644)
        self.context = (stage.repository_id, stage.run_id, stage.run_attempt, stage.head_sha,
                        stage.provider_run.workflow_id,
                        self.preparation.source_binding.validator_head_sha)
        self.replay = self.controller_fixture._replay(self.preparation)
        self.preparation_path = self._write(
            'preparation.json', completed_wire.encode_preparation(self.preparation), 0o600)
        self.replay_path = self._write('replay.json', completed_wire.encode_replay(self.replay), 0o600)
        self.deadline = time.monotonic() + 180

    def _directory(self, name):
        path = self.root / name
        path.mkdir(mode=0o700)
        os.chmod(path, 0o700)
        return path

    def _write(self, name, body, mode):
        path = self.inputs / name
        path.write_bytes(body)
        os.chmod(path, mode)
        return path

    def _observation(self):
        prep = self.preparation
        return completed_controller.CompletedValidationObservation(
            prep.stage.repository_id, prep.stage.run_id, prep.stage.run_attempt, prep.stage.head_sha,
            prep.source_binding.validator_head_sha, prep.source_binding.source_capture_sha256,
            prep.source_binding.validator_capture_sha256, '1' * 64, '2' * 64, '3' * 64,
            tuple((name, '4' * 64) for name in ('c1', 'c2', 'c3', 'accounts', 'measurement',
                                                'tests', 'native', 'gate')))

    def test_prepare_writes_canonical_wire_only_after_controller_returns(self):
        output = self.output / 'prepared.json'
        order = []

        def prepare(*args, **_kwargs):
            order.append(('controller', args[7]))
            return self.preparation

        with patch.object(subject, 'guard_completed_entrypoint',
                          side_effect=lambda *_a, **_k: (order.append(('guard',)) or self.context)), \
                patch.object(subject.completed_controller, 'prepare_completed_run', side_effect=prepare):
            result = subject.prepare_entrypoint(
                self.event_path, self.environment, self.validator_path, self.source_path,
                self.preparation.source_root, self.preparation.native_source_root,
                self.preparation.stage_directory, output, deadline=self.deadline)
        self.assertIs(result, self.preparation)
        self.assertEqual(['guard', 'controller'], [row[0] for row in order])
        self.assertEqual('token-secret-must-never-be-printed', order[1][1])
        self.assertEqual(0o600, output.stat().st_mode & 0o777)
        self.assertEqual(self.preparation.stage.run_id,
                         completed_wire.decode_preparation(output.read_bytes()).stage.run_id)

    def test_prepare_wrong_protected_context_rejects_before_controller(self):
        environment = dict(self.environment, GITHUB_EVENT_NAME='push')
        with patch.object(subject.completed_source, '_git') as git_call, \
                patch.object(subject.completed_controller, 'prepare_completed_run') as controller_call:
            with self.assertRaisesRegex(ValueError, 'protected-context'):
                subject.prepare_entrypoint(
                    self.event_path, environment, self.validator_path, self.source_path,
                    self.preparation.source_root, self.preparation.native_source_root,
                    self.preparation.stage_directory, self.output / 'prepared.json',
                    deadline=self.deadline)
        git_call.assert_not_called()
        controller_call.assert_not_called()
        self.assertFalse((self.output / 'prepared.json').exists())

    def test_prepare_detects_input_replacement_during_controller_and_publishes_nothing(self):
        output = self.output / 'prepared.json'

        def replace_event(*_args, **_kwargs):
            replacement = self.inputs / 'event.new'
            replacement.write_bytes(self.event_raw)
            os.chmod(replacement, 0o644)
            os.replace(replacement, self.event_path)
            return self.preparation

        with patch.object(subject, 'guard_completed_entrypoint', return_value=self.context), \
                patch.object(subject.completed_controller, 'prepare_completed_run',
                             side_effect=replace_event):
            with self.assertRaisesRegex(ValueError, 'input-custody'):
                subject.prepare_entrypoint(
                    self.event_path, self.environment, self.validator_path, self.source_path,
                    self.preparation.source_root, self.preparation.native_source_root,
                    self.preparation.stage_directory, output, deadline=self.deadline)
        self.assertFalse(output.exists())

    def test_prepare_expired_deadline_and_existing_output_reject_before_controller(self):
        with patch.object(subject.completed_controller, 'prepare_completed_run') as controller_call:
            with self.assertRaisesRegex(ValueError, 'deadline'):
                subject.prepare_entrypoint(
                    self.event_path, self.environment, self.validator_path, self.source_path,
                    self.preparation.source_root, self.preparation.native_source_root,
                    self.preparation.stage_directory, self.output / 'expired.json',
                    deadline=time.monotonic() - 1)
            existing = self.output / 'existing.json'
            existing.write_bytes(b'keep')
            os.chmod(existing, 0o600)
            with self.assertRaisesRegex(ValueError, 'output-path'):
                subject.prepare_entrypoint(
                    self.event_path, self.environment, self.validator_path, self.source_path,
                    self.preparation.source_root, self.preparation.native_source_root,
                    self.preparation.stage_directory, existing, deadline=self.deadline)
            victim = self.output / 'victim.json'
            victim.write_bytes(b'preserve foreign target')
            os.chmod(victim, 0o600)
            link = self.output / 'linked.json'
            link.symlink_to(victim)
            with self.assertRaisesRegex(ValueError, 'output-path'):
                subject.prepare_entrypoint(
                    self.event_path, self.environment, self.validator_path, self.source_path,
                    self.preparation.source_root, self.preparation.native_source_root,
                    self.preparation.stage_directory, link, deadline=self.deadline)
        controller_call.assert_not_called()
        self.assertEqual(b'keep', existing.read_bytes())
        self.assertEqual(b'preserve foreign target', victim.read_bytes())

    def test_close_writes_false_authority_observation_from_bound_context(self):
        output = self.output / 'report.json'
        observation = self._observation()
        with patch.object(subject, 'guard_completed_entrypoint', return_value=self.context), \
                patch.object(subject.completed_controller, 'close_completed_run',
                             return_value=observation) as close:
            result = subject.close_entrypoint(
                self.event_path, self.environment, self.validator_path, self.source_path,
                self.preparation_path, self.replay_path, output, deadline=self.deadline)
        self.assertIs(result, observation)
        close.assert_called_once()
        self.assertEqual(0o600, output.stat().st_mode & 0o777)
        report = json.loads(output.read_bytes())
        self.assertEqual('issue779-completed-validation-observation-v1', report['schema'])
        for name in ('cleanup_complete', 'admission_created', 'native_acceptance',
                     'qualification_complete', 'gate_eligible', 'original_gate_success_proven',
                     'test_inventory_success_proven'):
            self.assertFalse(report['observation'][name])

    def test_close_context_mismatch_rejects_before_controller_and_output(self):
        variants = []
        for index, replacement in ((1, self.context[1] + 1),
                                   (2, self.context[2] + 1),
                                   (5, 'f' * 40)):
            wrong = list(self.context)
            wrong[index] = replacement
            variants.append(tuple(wrong))
        with patch.object(subject.completed_controller, 'close_completed_run') as close:
            for index, wrong in enumerate(variants):
                output = self.output / f'report-{index}.json'
                with self.subTest(context=wrong), \
                        patch.object(subject, 'guard_completed_entrypoint', return_value=wrong):
                    with self.assertRaisesRegex(ValueError, 'preparation-context'):
                        subject.close_entrypoint(
                            self.event_path, self.environment, self.validator_path, self.source_path,
                            self.preparation_path, self.replay_path, output, deadline=self.deadline)
                    self.assertFalse(output.exists())
        close.assert_not_called()

    def test_close_detects_replaced_replay_input_after_controller_returns(self):
        output = self.output / 'report.json'
        observation = self._observation()

        def replace_replay(*_args, **_kwargs):
            replacement = self.inputs / 'replay.new'
            replacement.write_bytes(self.replay_path.read_bytes())
            os.chmod(replacement, 0o600)
            os.replace(replacement, self.replay_path)
            return observation

        with patch.object(subject, 'guard_completed_entrypoint', return_value=self.context), \
                patch.object(subject.completed_controller, 'close_completed_run',
                             side_effect=replace_replay):
            with self.assertRaisesRegex(ValueError, 'input-custody'):
                subject.close_entrypoint(
                    self.event_path, self.environment, self.validator_path, self.source_path,
                    self.preparation_path, self.replay_path, output, deadline=self.deadline)
        self.assertFalse(output.exists())

    def test_output_readback_failure_removes_only_the_created_output(self):
        output = self.output / 'prepared.json'
        original = subject.completed_c1._open_bound_file

        def fail_output(path, *args, **kwargs):
            if Path(path) == output:
                raise ValueError('readback-failure')
            return original(path, *args, **kwargs)

        with patch.object(subject, 'guard_completed_entrypoint', return_value=self.context), \
                patch.object(subject.completed_controller, 'prepare_completed_run',
                             return_value=self.preparation), \
                patch.object(subject.completed_c1, '_open_bound_file', side_effect=fail_output):
            with self.assertRaisesRegex(ValueError, 'readback-failure'):
                subject.prepare_entrypoint(
                    self.event_path, self.environment, self.validator_path, self.source_path,
                    self.preparation.source_root, self.preparation.native_source_root,
                    self.preparation.stage_directory, output, deadline=self.deadline)
        self.assertFalse(output.exists())

    def test_guard_binds_repository_and_validator_head_before_controller(self):
        environment = dict(self.environment)
        environment.update(GITHUB_SHA=self.context[5], GITHUB_WORKFLOW_SHA=self.context[5],
                           GITHUB_REPOSITORY_ID=str(self.context[0]))
        with patch.object(subject.completed_source, '_git', return_value=self.context[5]):
            self.assertEqual(self.context, subject.guard_completed_entrypoint(
                self.event_raw, environment, deadline=self.deadline))
            for name, wrong in (('GITHUB_REPOSITORY_ID', '1'), ('GITHUB_SHA', 'f' * 40),
                                ('GITHUB_WORKFLOW_SHA', 'f' * 40),
                                ('GITHUB_REF_PROTECTED', 'false'),
                                ('GITHUB_WORKFLOW_REF', 'foreign/workflow@refs/heads/main')):
                with self.subTest(name=name), self.assertRaises(ValueError):
                    subject.guard_completed_entrypoint(
                        self.event_raw, dict(environment, **{name: wrong}), deadline=self.deadline)

    def test_deadline_bounds_reject_before_files_are_opened(self):
        for entrypoint, maximum, args in (
                (subject.prepare_entrypoint, subject.PREPARE_SECONDS,
                 (self.preparation.source_root, self.preparation.native_source_root,
                  self.preparation.stage_directory, self.output / 'prepare.json')),
                (subject.close_entrypoint, subject.CLOSE_SECONDS,
                 (self.preparation_path, self.replay_path, self.output / 'report.json'))):
            for deadline in (True, float('nan'), float('inf'), time.monotonic() - 1,
                             time.monotonic() + maximum + 60):
                with self.subTest(entrypoint=entrypoint.__name__, deadline=deadline), \
                        patch.object(subject, '_output_parent') as output, \
                        self.assertRaisesRegex(ValueError, 'deadline'):
                    entrypoint(self.event_path, self.environment, self.validator_path,
                               self.source_path, *args, deadline=deadline)
                output.assert_not_called()

    def test_final_root_custody_failure_removes_owned_output_and_preserves_foreign_output(self):
        original_pins = subject.completed_controller._pin_roots

        for replace in (False, True):
            output = self.output / ('foreign.json' if replace else 'owned.json')

            @contextlib.contextmanager
            def failed_exit(roots):
                with original_pins(roots):
                    yield
                    if replace:
                        displaced = self.output / 'displaced.json'
                        os.replace(output, displaced)
                        output.write_bytes(b'foreign output')
                        os.chmod(output, 0o600)
                    raise ValueError('late-custody-failure')

            with self.subTest(replace=replace), \
                    patch.object(subject, 'guard_completed_entrypoint', return_value=self.context), \
                    patch.object(subject.completed_controller, 'prepare_completed_run',
                                 return_value=self.preparation), \
                    patch.object(subject.completed_controller, '_pin_roots', side_effect=failed_exit), \
                    self.assertRaisesRegex(ValueError, 'late-custody-failure'):
                subject.prepare_entrypoint(
                    self.event_path, self.environment, self.validator_path, self.source_path,
                    self.preparation.source_root, self.preparation.native_source_root,
                    self.preparation.stage_directory, output, deadline=self.deadline)
            if replace:
                self.assertEqual(b'foreign output', output.read_bytes())
            else:
                self.assertFalse(output.exists())

    def test_publication_short_write_or_late_deadline_removes_owned_output(self):
        for late in (False, True):
            output = self.output / ('late.json' if late else 'short.json')
            if late:
                deadline = time.monotonic() + 5
                with patch.object(subject.time, 'monotonic', return_value=deadline - 1):
                    with self.assertRaisesRegex(ValueError, 'deadline'):
                        with subject._publication(output, deadline) as publish:
                            publish(b'{}')
                            with patch.object(subject.time, 'monotonic', return_value=deadline + 1):
                                subject._remaining(deadline)
            else:
                with patch.object(subject.os, 'write', return_value=0), \
                        self.assertRaisesRegex(ValueError, 'output-short-write'):
                    with subject._publication(output, self.deadline) as publish:
                        publish(b'{}')
            self.assertFalse(output.exists())

    def test_publication_closes_parent_descriptor_when_initial_fstat_fails(self):
        output = self.output / 'fstat-failure.json'
        real_open, real_fstat = os.open, os.fstat
        parent_fds = []

        def tracked_open(path, *args, **kwargs):
            fd = real_open(path, *args, **kwargs)
            if Path(path) == self.output:
                parent_fds.append(fd)
            return fd

        def failing_fstat(fd):
            if parent_fds and fd == parent_fds[0]:
                raise OSError('initial parent fstat failure')
            return real_fstat(fd)

        with patch.object(subject.os, 'open', side_effect=tracked_open), \
                patch.object(subject.os, 'fstat', side_effect=failing_fstat):
            with self.assertRaisesRegex(OSError, 'initial parent fstat failure'):
                with subject._publication(output, self.deadline):
                    self.fail('publication body must not run')
        self.assertEqual(1, len(parent_fds))
        with self.assertRaises(OSError):
            real_fstat(parent_fds[0])

    def test_main_error_is_fixed_and_does_not_disclose_token_or_paths(self):
        output = io.StringIO()
        environment = dict(self.environment, GITHUB_EVENT_PATH=str(self.event_path))
        with patch.dict(os.environ, environment, clear=True), \
                contextlib.redirect_stderr(output), contextlib.redirect_stdout(io.StringIO()):
            code = subject.main(['prepare', '/private/credential/path'])
        self.assertEqual(2, code)
        self.assertEqual('EVIDENCE_COMPLETED_INPUT_REJECTED\n', output.getvalue())
        self.assertNotIn('token-secret', output.getvalue())
        self.assertNotIn(str(self.event_path), output.getvalue())


if __name__ == '__main__':
    unittest.main()
