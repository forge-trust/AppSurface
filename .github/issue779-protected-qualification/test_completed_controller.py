"""Controller preparation/closeout tests; all provider and source seams are local mocks."""
from __future__ import annotations

from dataclasses import replace
import hashlib
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
import completed_c1
import completed_controller as subject
import completed_orchestration
import completed_run
import completed_source
import completed_validation
import test_completed_orchestration
import test_completed_validation


class CompletedControllerTests(unittest.TestCase):
    def setUp(self):
        self.fixture = test_completed_validation.CompletedValidationTests()
        self.fixture.setUp()
        self.addCleanup(self.fixture.tearDown)
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        root = Path(self.temp.name).resolve()
        self.stage = root / 'stage'
        self.stage.mkdir(mode=0o700)
        os.chmod(self.stage, 0o700)
        self.source = root / 'source'
        self.native = root / 'native'
        self.private = root / 'private'
        for path in (self.source, self.native, self.private):
            path.mkdir(mode=0o700)
            os.chmod(path, 0o700)
        self.validator = root / 'validator'
        self.validator.mkdir(mode=0o700)
        os.chmod(self.validator, 0o700)
        self.validator_capture = b'validator-capture-bytes'
        self.deadline = time.monotonic() + 600
        self.close_deadline = time.monotonic() + 150
        self.stage_value = self._stage()
        self.binding = completed_source.SourceBinding(
            self.stage_value.repository_id, self.stage_value.run_id, self.stage_value.run_attempt,
            self.stage_value.head_sha, self.stage_value.source_tree,
            self.stage_value.source_capture_sha256, 'c' * 40, 'd' * 40,
            hashlib.sha256(self.validator_capture).hexdigest())

    def _stage(self):
        f = self.fixture
        required = sorted(set(completed_run.REQUIRED_RUNTIME_JOBS)
                          | completed_run.EXPECTED_TERMINAL_JOBS)
        jobs = tuple(completed_run.CompletedJob(name, index + 1, f.head, f.attempt,
                                                 'completed', 'success')
                     for index, name in enumerate(required))
        provider = completed_run.ProviderRunConclusion(
            f.stage.repository_id, completed_run.REPOSITORY, f.run_id, f.attempt, f.head, 99,
            completed_run.WORKFLOW, completed_run.QUALIFICATION_WORKFLOW_NAME,
            'workflow_dispatch', 'main', 'completed', 'success')
        return replace(f.stage, jobs=jobs, provider_run=provider)

    def _resolver(self, *, mismatch=False, calls=None):
        f = self.fixture

        def resolve(_event, _environment, _head, _capture, _workflows, _token,
                    destination, *, total_timeout):
            if calls is not None:
                calls.append(('resolver', total_timeout))
            self.assertGreater(total_timeout, 0)
            for binding in f.stage.artifacts:
                shutil.copyfile(f.stage_root / binding.staged_filename,
                                destination / binding.staged_filename)
                os.chmod(destination / binding.staged_filename, 0o600)
            stage = self.stage_value
            if mismatch:
                head = 'f' * 40
                stage = replace(
                    stage, head_sha=head,
                    provider_run=replace(stage.provider_run, head_sha=head),
                    jobs=tuple(replace(job, head_sha=head) for job in stage.jobs))
            return stage
        return resolve

    def _prepared(self, *, calls=None):
        order = calls if calls is not None else []
        source_patch = patch.object(
            completed_source, 'validate_completed_source',
            side_effect=lambda *_args, **_kwargs: (order.append(('source',)) or self.binding))
        resolver_patch = patch.object(subject.completed_run, 'resolve_completed_run',
                                      side_effect=self._resolver(calls=order))
        def workflow_bytes(path, **_kwargs):
            suffix = next(name for name in self.fixture.workflow_bytes if str(path).endswith(name))
            return self.fixture.workflow_bytes[suffix]

        workflow_read = patch.object(completed_c1, '_read_trusted_source_file',
                                     side_effect=workflow_bytes)
        source_patch.start()
        resolver_patch.start()
        workflow_read.start()
        self.addCleanup(source_patch.stop)
        self.addCleanup(resolver_patch.stop)
        self.addCleanup(workflow_read.stop)
        prep = subject.prepare_completed_run(
            b'event', {'test': 'local'}, self.validator_capture, self.fixture.capture,
            self.source, self.native, self.stage, 'local-test-token', deadline=self.deadline)
        return prep

    def test_prepare_orders_source_binding_before_fixed_stage_resolution(self):
        calls = []
        prep = self._prepared(calls=calls)
        self.assertEqual(['source', 'resolver'], [row[0] for row in calls])
        self.assertIs(prep.stage, self.stage_value)
        self.assertEqual(self.binding, prep.source_binding)
        self.assertIsInstance(prep.inventory, completed_validation.ParsedCompletedRunInventory)
        self.assertEqual((self.stage, self.source, self.native),
                         (prep.stage_directory, prep.source_root, prep.native_source_root))
        self.assertEqual(4, len(prep.root_identities))
        self.assertFalse(prep.source_binding.qualification_complete)

    def test_prepare_expired_deadline_rejects_before_source_or_provider_calls(self):
        with patch.object(completed_source, 'validate_completed_source') as source_call, \
                patch.object(subject.completed_run, 'resolve_completed_run') as resolver_call:
            with self.assertRaisesRegex(ValueError, 'deadline'):
                subject.prepare_completed_run(
                    b'event', {}, self.validator_capture, self.fixture.capture,
                    self.source, self.native, self.stage, 'token', deadline=time.monotonic() - 1)
        source_call.assert_not_called()
        resolver_call.assert_not_called()
        self.assertEqual([], list(self.stage.iterdir()))

    def test_prepare_stage_binding_failure_stops_before_archive_inspection(self):
        with patch.object(completed_source, 'validate_completed_source', return_value=self.binding), \
                patch.object(completed_c1, '_read_trusted_source_file',
                             return_value=b'fixed workflow bytes'), \
                patch.object(subject.completed_run, 'resolve_completed_run',
                             side_effect=self._resolver(mismatch=True)), \
                patch.object(subject.completed_validation, 'inspect_completed_run') as inspect:
            with self.assertRaisesRegex(ValueError, 'source-stage-binding'):
                subject.prepare_completed_run(
                    b'event', {}, self.validator_capture, self.fixture.capture,
                    self.source, self.native, self.stage, 'token', deadline=self.deadline)
        inspect.assert_not_called()

    def _replay(self, preparation):
        orchestration = test_completed_orchestration.CompletedRunOrchestrationTests()
        orchestration.setUp()
        self.addCleanup(orchestration.doCleanups)
        orchestration.fixture.stage = preparation.stage
        orchestration.fixture.stage_root = preparation.stage_directory
        orchestration.fixture.capture = self.fixture.capture
        orchestration.fixture.capture_sha = self.fixture.capture_sha
        orchestration.source = preparation.source_root
        orchestration.native_source = preparation.native_source_root
        orchestration.private = self.private
        orchestration.deadline = self.deadline
        orchestration._patch_source()
        orchestration._patch_inputs()
        return orchestration._run()

    def _close_provider(self, stage):
        provider = stage.provider_run
        artifact_ids = tuple(sorted((row.name, row.artifact_id, row.size, row.sha256)
                                    for row in stage.artifacts))
        return completed_run.ProviderRevalidation(
            stage.repository_id, stage.run_id, stage.run_attempt, stage.head_sha,
            provider.workflow_id, provider, stage.jobs, stage.job_ids,
            artifact_ids, stage.workflow_sha256s)

    def test_canonical_projection_stops_at_observation_byte_limit(self):
        with patch.object(subject, 'MAX_OBSERVATION_BYTES', 32):
            with self.assertRaisesRegex(ValueError, 'observation-size'):
                subject._canonical(completed_orchestration.PhaseObservation(
                    'x' * 64, 'a' * 64, b'bounded projection bytes'))

    @staticmethod
    def _alter_phase(replay, phase_name, mutation):
        phases = list(replay.phases)
        index = next(index for index, item in enumerate(phases) if item.phase == phase_name)
        phase = phases[index]
        value = json.loads(phase.projection_json)
        mutation(value)
        raw = json.dumps(value, sort_keys=True, separators=(',', ':')).encode()
        phases[index] = replace(phase, projection_json=raw,
                                projection_sha256=hashlib.sha256(raw).hexdigest())
        return replace(replay, phases=tuple(phases))

    def test_close_binds_all_replay_phases_rechecks_source_inventory_and_provider(self):
        prep = self._prepared()
        replay = self._replay(prep)
        order = []
        with patch.object(completed_source, 'verify_completed_source',
                          side_effect=lambda *_args, **_kwargs: order.append('source')), \
                patch.object(subject.completed_run, 'revalidate_completed_run',
                             side_effect=lambda *_args, **_kwargs:
                             (order.append('provider') or self._close_provider(prep.stage))):
            result = subject.close_completed_run(
                prep, replay, self.validator_capture, self.fixture.capture, 'local-token',
                deadline=self.close_deadline)
        self.assertEqual(['source', 'provider', 'source'], order)
        self.assertEqual((prep.stage.run_id, prep.stage.run_attempt, prep.stage.head_sha),
                         (result.run_id, result.run_attempt, result.head_sha))
        self.assertEqual(tuple((phase.phase, phase.projection_sha256) for phase in replay.phases),
                         result.phase_sha256s)
        for name in ('cleanup_complete', 'admission_created', 'native_acceptance',
                     'qualification_complete', 'gate_eligible', 'original_gate_success_proven',
                     'test_inventory_success_proven'):
            self.assertFalse(getattr(result, name))

    def test_close_rejects_bad_replay_projection_before_source_or_provider_recheck(self):
        prep = self._prepared()
        replay = self._replay(prep)
        changed = list(replay.phases)
        first = changed[0]
        payload = first.projection_json.replace(prep.stage.head_sha.encode(), b'f' * 40)
        changed[0] = replace(first, projection_json=payload,
                             projection_sha256=hashlib.sha256(payload).hexdigest())
        replay = replace(replay, phases=tuple(changed))
        with patch.object(completed_source, 'verify_completed_source') as source_call, \
                patch.object(subject.completed_run, 'revalidate_completed_run') as provider_call:
            with self.assertRaisesRegex(ValueError, 'projection-identity'):
                subject.close_completed_run(
                    prep, replay, self.validator_capture, self.fixture.capture, 'token',
                    deadline=self.close_deadline)
        source_call.assert_not_called()
        provider_call.assert_not_called()

    def test_close_rejects_projection_digest_mismatch_before_source_or_provider_recheck(self):
        prep = self._prepared()
        replay = self._replay(prep)
        changed = list(replay.phases)
        changed[0] = replace(changed[0], projection_json=changed[0].projection_json + b' ')
        replay = replace(replay, phases=tuple(changed))
        with patch.object(completed_source, 'verify_completed_source') as source_call, \
                patch.object(subject.completed_run, 'revalidate_completed_run') as provider_call:
            with self.assertRaisesRegex(ValueError, 'projection-digest'):
                subject.close_completed_run(
                    prep, replay, self.validator_capture, self.fixture.capture, 'token',
                    deadline=self.close_deadline)
        source_call.assert_not_called()
        provider_call.assert_not_called()

    def test_close_rejects_true_authority_claim_inside_projection(self):
        prep = self._prepared()
        replay = self._replay(prep)
        changed = list(replay.phases)
        native_index = next(index for index, phase in enumerate(changed) if phase.phase == 'native')
        phase = changed[native_index]
        payload = json.loads(phase.projection_json)
        payload['validator_executed'] = True
        raw = json.dumps(payload, sort_keys=True, separators=(',', ':')).encode()
        changed[native_index] = replace(phase, projection_json=raw,
                                        projection_sha256=hashlib.sha256(raw).hexdigest())
        replay = replace(replay, phases=tuple(changed))
        with patch.object(completed_source, 'verify_completed_source') as source_call, \
                patch.object(subject.completed_run, 'revalidate_completed_run') as provider_call:
            with self.assertRaisesRegex(ValueError, 'replay-claim'):
                subject.close_completed_run(
                    prep, replay, self.validator_capture, self.fixture.capture, 'token',
                    deadline=self.close_deadline)
        source_call.assert_not_called()
        provider_call.assert_not_called()

    def test_close_rejects_coherent_phase_semantic_drift(self):
        prep = self._prepared()
        replay = self._replay(prep)
        mutations = (
            ('c1', lambda value: value.update(source_capture_sha256='f' * 64),
             'projection-capture'),
            ('c2', lambda value: value.update(source_tree='f' * 40), 'projection-tree'),
            ('measurement', lambda value: value.update(cases=[]), 'measurement-result'),
            ('c2', lambda value: value['case_rows'][0].__setitem__(0, 'wrong-case'), 'c2-cases'),
            ('c3', lambda value: value.update(application_cases=[]), 'c3-cases'),
            ('accounts', lambda value: value['accounts'][0].update(source_sha256='f' * 64),
             'account-capture'),
            ('tests', lambda value: value.update(project_count=value['project_count'] + 1),
             'test-inventory-result'),
            ('native', lambda value: value.update(outcome='Failed'), 'native-result'),
            ('gate', lambda value: value.update(base_commit='f' * 40), 'gate-result'),
            ('native', lambda value: value.pop('native_acceptance'), 'projection-schema'),
        )
        with patch.object(completed_source, 'verify_completed_source') as source_call, \
                patch.object(subject.completed_run, 'revalidate_completed_run') as provider_call:
            for phase_name, mutate, reason in mutations:
                with self.subTest(phase=phase_name, reason=reason):
                    altered = self._alter_phase(replay, phase_name, mutate)
                    with self.assertRaisesRegex(ValueError, reason):
                        subject.close_completed_run(
                            prep, altered, self.validator_capture, self.fixture.capture, 'token',
                            deadline=self.close_deadline)
        source_call.assert_not_called()
        provider_call.assert_not_called()

    def test_close_rejects_every_native_retained_path_drift(self):
        prep = self._prepared()
        replay = self._replay(prep)
        paths = ('retained_output_parent', 'retained_test_output',
                 'retained_logs', 'retained_work_root')
        with patch.object(completed_source, 'verify_completed_source') as source_call, \
                patch.object(subject.completed_run, 'revalidate_completed_run') as provider_call:
            for name in paths:
                with self.subTest(path=name):
                    altered = self._alter_phase(
                        replay, 'native', lambda value, field=name:
                        value.update({field: str(self.private / 'foreign')}))
                    with self.assertRaisesRegex(ValueError, 'projection-retained-paths'):
                        subject.close_completed_run(
                            prep, altered, self.validator_capture, self.fixture.capture, 'token',
                            deadline=self.close_deadline)
        source_call.assert_not_called()
        provider_call.assert_not_called()

    def test_close_detects_original_archive_mutation_before_provider_revalidation(self):
        prep = self._prepared()
        replay = self._replay(prep)
        binding = prep.stage.artifacts[0]
        path = prep.stage_directory / binding.staged_filename
        raw = bytearray(path.read_bytes())
        raw[-1] ^= 1
        path.write_bytes(raw)
        with patch.object(completed_source, 'verify_completed_source'), \
                patch.object(subject.completed_run, 'revalidate_completed_run') as provider_call:
            with self.assertRaises(ValueError):
                subject.close_completed_run(
                    prep, replay, self.validator_capture, self.fixture.capture, 'token',
                    deadline=self.close_deadline)
        provider_call.assert_not_called()

    def test_close_detects_same_bytes_archive_replacement_during_provider_revalidation(self):
        prep = self._prepared()
        replay = self._replay(prep)
        binding = prep.stage.artifacts[0]
        path = prep.stage_directory / binding.staged_filename
        original = path.read_bytes()

        def replace_during_provider(*_args, **_kwargs):
            path.unlink()
            path.write_bytes(original)
            os.chmod(path, 0o600)
            return self._close_provider(prep.stage)

        with patch.object(completed_source, 'verify_completed_source'), \
                patch.object(subject.completed_run, 'revalidate_completed_run',
                             side_effect=replace_during_provider) as provider_call:
            with self.assertRaises(ValueError):
                subject.close_completed_run(
                    prep, replay, self.validator_capture, self.fixture.capture, 'token',
                    deadline=self.close_deadline)
        provider_call.assert_called_once()
        self.assertEqual(original, path.read_bytes())

    def test_close_revalidates_source_after_provider_and_propagates_second_failure(self):
        prep = self._prepared()
        replay = self._replay(prep)
        calls = []

        def verify(*_args, **_kwargs):
            calls.append('source')
            if len(calls) == 2:
                raise ValueError('post-provider-source-drift')

        with patch.object(completed_source, 'verify_completed_source', side_effect=verify), \
                patch.object(subject.completed_run, 'revalidate_completed_run',
                             side_effect=lambda *_args, **_kwargs:
                             self._close_provider(prep.stage)) as provider_call:
            with self.assertRaisesRegex(ValueError, 'post-provider-source-drift'):
                subject.close_completed_run(
                    prep, replay, self.validator_capture, self.fixture.capture, 'token',
                    deadline=self.close_deadline)
        self.assertEqual(['source', 'source'], calls)
        provider_call.assert_called_once()

    def test_close_rejects_replaced_prepared_root_before_provider_revalidation(self):
        prep = self._prepared()
        replay = self._replay(prep)
        old = self.source.with_name('source-original')
        self.source.rename(old)
        self.source.mkdir(mode=0o700)
        os.chmod(self.source, 0o700)
        with patch.object(completed_run, 'revalidate_completed_run') as provider_call:
            with self.assertRaisesRegex(ValueError, 'preparation-roots-changed'):
                subject.close_completed_run(
                    prep, replay, self.validator_capture, self.fixture.capture, 'token',
                    deadline=self.close_deadline)
        provider_call.assert_not_called()

    def test_close_rejects_mismatched_provider_revalidation(self):
        prep = self._prepared()
        replay = self._replay(prep)
        stale = replace(self._close_provider(prep.stage), run_attempt=prep.stage.run_attempt + 1)
        with patch.object(completed_source, 'verify_completed_source'), \
                patch.object(subject.completed_run, 'revalidate_completed_run', return_value=stale):
            with self.assertRaisesRegex(ValueError, 'provider-revalidation-binding'):
                subject.close_completed_run(
                    prep, replay, self.validator_capture, self.fixture.capture, 'token',
                    deadline=self.close_deadline)

    def test_close_rejects_provider_artifact_identifier_drift(self):
        prep = self._prepared()
        replay = self._replay(prep)
        stale = replace(self._close_provider(prep.stage), artifact_ids=())
        with patch.object(completed_source, 'verify_completed_source'), \
                patch.object(subject.completed_run, 'revalidate_completed_run', return_value=stale):
            with self.assertRaisesRegex(ValueError, 'provider-revalidation-facts'):
                subject.close_completed_run(
                    prep, replay, self.validator_capture, self.fixture.capture, 'token',
                    deadline=self.close_deadline)


if __name__ == '__main__':
    unittest.main()
