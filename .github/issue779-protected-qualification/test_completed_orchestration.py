"""Fixed completed-run orchestration wiring over authentic-shaped staged archives."""
from __future__ import annotations

from dataclasses import replace
import os
from pathlib import Path
import shutil
import sys
import tempfile
import time
import unittest
import hashlib
import errno
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / '.github/issue779-protected-qualification'))
import completed_accounts
import completed_c1
import completed_c2
import completed_c3
import completed_gate_replay
import completed_measurement_chain
import completed_native_replay
import completed_native_result
import completed_test_inventory
import completed_orchestration as subject
import completed_run
import completed_validation
import test_completed_validation


class CompletedRunOrchestrationTests(unittest.TestCase):
    def setUp(self):
        self.fixture = test_completed_validation.CompletedValidationTests()
        self.fixture.setUp()
        self.addCleanup(self.fixture.tearDown)
        required_names = sorted(set(completed_run.REQUIRED_RUNTIME_JOBS)
                                | completed_run.EXPECTED_TERMINAL_JOBS)
        self.fixture.stage = replace(
            self.fixture.stage,
            jobs=tuple(completed_run.CompletedJob(
                name, job_id, self.fixture.head, self.fixture.attempt, 'completed', 'success')
                for job_id, name in enumerate(required_names, 1)),
            provider_run=completed_run.ProviderRunConclusion(
                self.fixture.stage.repository_id, completed_run.REPOSITORY,
                self.fixture.run_id, self.fixture.attempt, self.fixture.head, 99,
                completed_run.WORKFLOW, completed_run.QUALIFICATION_WORKFLOW_NAME,
                'workflow_dispatch', 'main', 'completed', 'success'))
        self.root = Path(self.fixture.temp.name).resolve()
        self.source = self.root / 'source'
        self.native_source = self.root / 'native-source'
        self.private = self.root / 'private'
        for path in (self.source, self.native_source, self.private):
            path.mkdir(mode=0o700)
            os.chmod(path, 0o700)
        self.deadline = time.monotonic() + 600

    def _identity(self, result, *, source_field='head_sha', capture=False, tree=False):
        values = {'run_id': self.fixture.run_id, 'run_attempt': self.fixture.attempt,
                  source_field: self.fixture.head}
        if capture:
            values['source_capture_sha256'] = self.fixture.capture_sha
        if tree:
            values['source_tree'] = self.fixture.tree
        return values

    def _results(self):
        f = self.fixture
        inventory = f.inspect()
        artifact = {row.name: row for row in f.stage.artifacts}
        c2_name = f'issue779-csharp-checkpoint2-{f.run_id}-{f.attempt}'
        app_name = f'issue779-checkpoint3-application-{f.run_id}-{f.attempt}'
        life_name = f'issue779-c3sdk-lifecycle-{f.run_id}-{f.attempt}'
        aggregate_name = f'issue779-checkpoint1-current-{f.run_id}-{f.attempt}'
        tests_result = completed_test_inventory.CompletedTestInventory(
            f.run_id, f.attempt, f.head, f.capture_sha, '1' * 64, '2' * 64, '6' * 64,
            1, 1, 0, (completed_test_inventory.ProjectTestInventory(
                'Example.Tests/Example.Tests.csproj', 0, 'junit-coverage-example.xml', 'a' * 64,
                'projects/example/dotnet-test.log', 'b' * 64, 1, 0),))
        return {
            'c1': completed_c1.C1CallbackProjection(
                f.run_id, f.attempt, f.head, f.capture_sha, completed_c1.SELECTORS,
                completed_c1.CASES, 16, 0, 0, artifact[aggregate_name].sha256,
                inventory.aggregate.aggregate_sha256, inventory.aggregate.aggregate_sha256),
            'c2': completed_c2.C2ReceiptProjection(
                'c2', f.head, f.tree, f.run_id, f.attempt,
                tuple((case, 'ok', '4' * 64, '5' * 64) for case in completed_c2.CASE_NAMES),
                c2_name, artifact[c2_name].sha256,
                f.capture_sha, '7' * 64, '8' * 64, False, False),
            'c3': completed_c3.C3ReceiptProjection(
                f.head, f.tree, f.run_id, f.attempt, app_name, artifact[app_name].size,
                artifact[app_name].sha256, life_name, artifact[life_name].size,
                artifact[life_name].sha256, 'b' * 64, 'c' * 64,
                subject.APPLICATION_CASES, subject.LIFECYCLE_CASES, False, False),
            'accounts': completed_accounts.CompletedAccounts(
                f.run_id, f.attempt, f.head, tuple(completed_accounts.AccountReplay(
                    f'issue779-c4-accounts-{variant}-{f.run_id}-{f.attempt}', variant,
                    f.capture_sha, artifact[f'issue779-c4-accounts-{variant}-{f.run_id}-{f.attempt}'].sha256,
                    'e' * 64, 'f' * 64,
                    '1' * 64, '2' * 64, '3' * 64) for variant in completed_accounts.VARIANTS)),
            'measurement': completed_measurement_chain.CompletedMeasurementChainProjection(
                f.run_id, f.attempt, f.head, f.capture_sha, f'github-actions-{f.run_id}-{f.attempt}',
                completed_c1.SELECTORS, completed_c1.CASES, b'final-report',
                hashlib.sha256(b'final-report').hexdigest()),
            'tests': tests_result,
            'native': completed_native_replay.NativeReplayResult(
                'issue779-native-replay-observation-v1', f.run_id, f.attempt, f.head, f.capture_sha,
                completed_native_replay.SDK_VERSION, (), ('dotnet', 'vstest'), '4' * 64, 10,
                '5' * 64, 10, completed_native_result.TEST_METHOD, 'Passed',
                str(self.private / 'native'), str(self.private / 'native' / 'native-replay-output'),
                str(self.private / 'native' / 'native-replay-logs'),
                str(self.private / 'native' / 'native-replay-state')),
            'gate': completed_gate_replay.GateReplayResult(
                f.run_id, f.attempt, f.head, f.capture_sha,
                completed_gate_replay.completed_gate.FIXED_DIFF_BASE, '6' * 64, '7' * 64,
                '6' * 64, '6' * 64, 'a' * 64, 12, ('dotnet', 'coverage', 'gate'), 0,
                (), 'b' * 64, 'c' * 64, (), 'd' * 64, True,
                self.private / 'gate' / 'coverage-gate-replay',
                cleanup_complete=False),
        }

    def _patch_inputs(self, *, fail_phase=None, action=None, replacements=None):
        functions = {
            'c1': (completed_c1, 'run_fixed_c1_callbacks'),
            'c2': (completed_c2, 'run_fixed_c2_receipt'),
            'c3': (completed_c3, 'run_fixed_c3_receipts'),
            'accounts': (completed_accounts, 'replay_completed_accounts'),
            'measurement': (completed_measurement_chain, 'run_completed_measurement_chain'),
            'tests': (completed_test_inventory, 'inspect_completed_test_inventory'),
            'native': (completed_native_replay, 'run_native_replay'),
            'gate': (completed_gate_replay, 'replay_completed_gate'),
        }
        results = self._results()
        calls = []
        stack = []
        for phase, (module, name) in functions.items():
            def invoke(*args, _phase=phase, **kwargs):
                calls.append((_phase, args, kwargs))
                if action is not None:
                    action(_phase, args, kwargs)
                if _phase == fail_phase:
                    raise RuntimeError('fixed-test-failure-' + _phase)
                return (replacements or {}).get(_phase, results[_phase])
            stack.append(patch.object(module, name, side_effect=invoke))
        for item in stack:
            item.start()
            self.addCleanup(item.stop)
        stack.append(patch.object(completed_gate_replay, 'SOURCE_MOUNT', self.source))
        stack[-1].start()
        self.addCleanup(stack[-1].stop)
        linux = patch.object(completed_gate_replay, '_linux_supported', return_value=True)
        linux.start()
        self.addCleanup(linux.stop)
        return calls

    def _run(self, *, deadline=None):
        return subject.replay_completed_run(
            self.fixture.stage, self.fixture.stage_root, self.fixture.capture,
            self.source, self.native_source, self.private,
            deadline=self.deadline if deadline is None else deadline)

    def _patch_source(self):
        source_patch = patch.object(subject, '_source', return_value=None)
        fresh_patch = patch.object(subject, '_fresh_source', return_value=None)
        source_patch.start()
        fresh_patch.start()
        self.addCleanup(source_patch.stop)
        self.addCleanup(fresh_patch.stop)

    def test_uses_real_37_archive_inventory_and_calls_all_fixed_phases_in_order(self):
        self._patch_source()
        calls = self._patch_inputs()

        result = self._run()

        self.assertEqual(tuple(subject.PHASES), tuple(row.phase for row in result.phases))
        self.assertEqual(list(subject.PHASES), [row[0] for row in calls])
        self.assertTrue(all(call[1][0] is self.fixture.stage for call in calls))
        self.assertTrue(all(call[1][1].__class__ is completed_validation.ParsedCompletedRunInventory
                            for call in calls))
        inventory = self.fixture.inspect()
        self.assertTrue(all(call[1][1] == inventory for call in calls))
        self.assertTrue(all(call[1][2] == self.fixture.stage_root for call in calls))
        self.assertTrue(all(call[2]['deadline'] == self.deadline for call in calls))
        self.assertTrue(all(len(row.projection_json) <= subject.MAX_PROJECTION_BYTES
                            and hashlib.sha256(row.projection_json).hexdigest() == row.projection_sha256
                            for row in result.phases))
        for name in ('cleanup_complete', 'qualification_complete', 'native_acceptance',
                     'admission_created', 'gate_eligible', 'original_gate_success_proven',
                     'test_inventory_success_proven'):
            self.assertFalse(getattr(result, name))
        self.assertFalse((self.private / 'c1').exists())
        self.assertFalse((self.private / 'c2').exists())
        self.assertFalse((self.private / 'c3').exists())
        self.assertFalse((self.private / 'measurement').exists())
        self.assertTrue((self.private / 'native').is_dir())
        self.assertTrue((self.private / 'gate').is_dir())
        self.assertEqual(('c1', 'c2', 'c3', 'accounts', 'measurement', 'tests', 'native', 'gate'),
                         tuple(row.phase for row in result.phases))

    def test_phase_failure_stops_later_validators_cleans_scratch_and_preserves_primary(self):
        self._patch_source()
        for failed_index, phase in enumerate(subject.PHASES):
            with self.subTest(phase=phase):
                calls = self._patch_inputs(fail_phase=phase)
                with self.assertRaisesRegex(RuntimeError, 'fixed-test-failure-' + phase):
                    self._run()
                self.assertEqual(list(subject.PHASES[:failed_index + 1]), [row[0] for row in calls])
                for scratch_phase in subject.CALLBACK_PHASES:
                    self.assertFalse((self.private / scratch_phase).exists())
                shutil.rmtree(self.private)
                self.private.mkdir(mode=0o700)
                os.chmod(self.private, 0o700)

    def test_wrong_result_identity_type_or_claim_fails_closed(self):
        self._patch_source()
        wrong_identity = replace(self._results()['c1'], run_attempt=self.fixture.attempt + 1)
        with self.assertRaisesRegex(ValueError, 'result-attribution'):
            self._run_with_replacements({'c1': wrong_identity})
        self._reset_private()

        with self.assertRaisesRegex(ValueError, 'result-type'):
            self._run_with_replacements({'c1': object()})
        self._reset_private()

        claimed = replace(self._results()['c1'])
        object.__setattr__(claimed, 'coverage_credit', True)
        with self.assertRaisesRegex(ValueError, 'result-claim'):
            self._run_with_replacements({'c1': claimed})

    def _run_with_replacements(self, values):
        self._patch_source()
        self._patch_inputs(replacements=values)
        return self._run()

    def _reset_private(self):
        shutil.rmtree(self.private)
        self.private.mkdir(mode=0o700)
        os.chmod(self.private, 0o700)

    def test_replaced_staged_archive_is_detected_after_callbacks_and_preserved(self):
        self._patch_source()
        victim = self.fixture.stage_root / self.fixture.artifacts[0].staged_filename
        replacement = self.root / 'replacement.zip'
        replacement.write_bytes(b'foreign replacement')
        os.chmod(replacement, 0o600)

        def replace_stage(phase, _args, _kwargs):
            if phase == 'c1':
                victim.unlink()
                os.replace(replacement, victim)

        calls = self._patch_inputs(action=replace_stage)
        with self.assertRaises(ValueError):
            self._run()
        self.assertEqual(['c1', 'c2', 'c3', 'accounts', 'measurement', 'tests', 'native', 'gate'],
                         [row[0] for row in calls])
        self.assertEqual(b'foreign replacement', victim.read_bytes())

    def test_unknown_scratch_node_causes_cleanup_failure_and_is_not_deleted(self):
        self._patch_source()
        unknown = self.private / 'c1' / 'unexpected'

        def add_unknown(phase, _args, _kwargs):
            if phase == 'c1':
                unknown.write_bytes(b'foreign')

        self._patch_inputs(action=add_unknown)
        with self.assertRaises(ValueError):
            self._run()
        self.assertEqual(b'foreign', unknown.read_bytes())

    def test_invalid_roots_nonempty_parent_platform_and_deadline_reject_before_callbacks(self):
        self._patch_source()
        calls = self._patch_inputs()
        with patch.object(subject.completed_gate_replay, '_linux_supported', return_value=False):
            with self.assertRaisesRegex(ValueError, 'linux-required'):
                self._run()
        self.assertEqual([], calls)

        with patch.object(subject.completed_gate_replay, '_linux_supported', return_value=True):
            with self.assertRaisesRegex(ValueError, 'deadline-bound'):
                self._run(deadline=time.monotonic() + subject.MAX_SECONDS + 10)
        self.assertEqual([], calls)

        marker = self.private / 'existing'
        marker.write_text('owned by caller')
        with patch.object(subject.completed_gate_replay, '_linux_supported', return_value=True):
            with self.assertRaisesRegex(ValueError, 'private-parent-not-empty'):
                self._run()
        self.assertEqual([], calls)
        self.assertEqual('owned by caller', marker.read_text())

    def test_source_verification_failure_stops_before_replay(self):
        calls = self._patch_inputs()
        with patch.object(subject, '_source', side_effect=ValueError('source verification failed')):
            with self.assertRaisesRegex(ValueError, 'source verification failed'):
                self._run()
        self.assertEqual([], calls)

    def test_source_freshness_is_rechecked_between_phases_before_next_validator(self):
        calls = self._patch_inputs()
        self._patch_source()
        check_count = 0

        def fresh(root, deadline):
            nonlocal check_count
            check_count += 1
            # First two calls are the preflight source/native checks. Then c1
            # has returned and the next source freshness check rejects.
            if check_count == 3:
                raise ValueError('source became dirty after c1')

        fresh_patch = patch.object(subject, '_fresh_source', side_effect=fresh)
        fresh_patch.start()
        self.addCleanup(fresh_patch.stop)
        with self.assertRaisesRegex(ValueError, 'source became dirty after c1'):
            self._run()
        self.assertEqual(['c1'], [row[0] for row in calls])

    def test_overlapping_native_source_is_rejected_before_inventory_or_replay(self):
        self._patch_source()
        calls = self._patch_inputs()
        with patch.object(completed_validation, 'inspect_completed_run') as inspect:
            with self.assertRaisesRegex(ValueError, 'root-overlap'):
                subject.replay_completed_run(
                    self.fixture.stage, self.fixture.stage_root, self.fixture.capture,
                    self.source, self.source, self.private, deadline=self.deadline)
            inspect.assert_not_called()
        self.assertEqual([], calls)

    def test_closed_jobs_are_required_before_archive_inventory_inspection(self):
        self._patch_source()
        self._patch_inputs()
        for jobs in ((), (replace(self.fixture.stage.jobs[0], status='in_progress'),) +
                     self.fixture.stage.jobs[1:]):
            stage = replace(self.fixture.stage, jobs=jobs)
            with patch.object(completed_validation, 'inspect_completed_run') as inspect:
                with self.assertRaises(ValueError):
                    subject.replay_completed_run(
                        stage, self.fixture.stage_root, self.fixture.capture, self.source,
                        self.native_source, self.private, deadline=self.deadline)
                inspect.assert_not_called()
            self._reset_private()

    def test_provider_run_is_required_before_archive_inventory_inspection(self):
        self._patch_source()
        self._patch_inputs()
        for provider in (None, replace(self.fixture.stage.provider_run, conclusion='failure'),
                         replace(self.fixture.stage.provider_run, head_branch='feature')):
            stage = replace(self.fixture.stage, provider_run=provider)
            with patch.object(completed_validation, 'inspect_completed_run') as inspect:
                with self.assertRaises(ValueError):
                    subject.replay_completed_run(
                        stage, self.fixture.stage_root, self.fixture.capture, self.source,
                        self.native_source, self.private, deadline=self.deadline)
                inspect.assert_not_called()

    def test_unexpected_parent_sibling_is_preserved_and_rejected(self):
        self._patch_source()
        marker = self.private / 'foreign'

        def add_sibling(phase, _args, _kwargs):
            if phase == 'c1':
                marker.write_text('foreign')

        self._patch_inputs(action=add_sibling)
        with self.assertRaises(ValueError):
            self._run()
        self.assertEqual('foreign', marker.read_text())

    def test_replacing_retained_native_or_gate_root_is_rejected_and_replacement_survives(self):
        for victim_name in ('native', 'gate'):
            with self.subTest(root=victim_name):
                self._patch_source()
                victim = self.private / victim_name
                moved = self.root / (victim_name + '-original')

                def replace_root(phase, _args, _kwargs):
                    if phase == 'c1':
                        os.replace(victim, moved)
                        victim.mkdir(mode=0o700)
                        os.chmod(victim, 0o700)

                self._patch_inputs(action=replace_root)
                with self.assertRaises(ValueError):
                    self._run()
                self.assertTrue(victim.is_dir())
                self.assertTrue(moved.is_dir())
                self._reset_private()

    def test_wrong_retained_native_or_gate_result_paths_are_rejected(self):
        self._patch_source()
        bad_native = replace(self._results()['native'], retained_output_parent=str(self.private))
        with self.assertRaisesRegex(ValueError, 'native-retained-paths'):
            self._run_with_replacements({'native': bad_native})
        self._reset_private()

        bad_gate = replace(self._results()['gate'], retained_work_root=self.private / 'wrong')
        with self.assertRaisesRegex(ValueError, 'gate-retained-path'):
            self._run_with_replacements({'gate': bad_gate})

    def test_scratch_root_substitution_is_rejected_and_foreign_nodes_survive(self):
        self._patch_source()
        victim = self.private / 'c1'
        moved = self.root / 'original-c1-scratch'

        def replace_scratch(phase, _args, _kwargs):
            if phase == 'c1':
                os.replace(victim, moved)
                victim.mkdir(mode=0o700)
                os.chmod(victim, 0o700)
                (victim / 'foreign').write_text('leave me')

        calls = self._patch_inputs(action=replace_scratch)
        with self.assertRaises(ValueError):
            self._run()
        self.assertEqual(['c1'], [row[0] for row in calls])
        self.assertTrue(moved.is_dir())
        self.assertEqual('leave me', (victim / 'foreign').read_text())

    def test_callback_scratch_open_failure_cleans_owned_root_closes_fd_and_preserves_error(self):
        self._patch_source()
        self._patch_inputs()
        original_open = os.open
        parent_fds = []
        injected = False

        def fail_scratch_open(path, flags, *args, **kwargs):
            nonlocal injected
            dir_fd = kwargs.get('dir_fd')
            if path == self.private and dir_fd is None and flags & os.O_DIRECTORY:
                fd = original_open(path, flags, *args, **kwargs)
                parent_fds.append(fd)
                return fd
            if path == 'c1' and dir_fd is not None and flags & os.O_DIRECTORY and not injected:
                injected = True
                raise OSError(errno.EIO, 'injected scratch open failure')
            return original_open(path, flags, *args, **kwargs)

        with patch.object(subject.os, 'open', side_effect=fail_scratch_open):
            with self.assertRaisesRegex(OSError, 'injected scratch open failure'):
                self._run()
        self.assertTrue(injected)
        self.assertFalse((self.private / 'c1').exists())
        self.assertTrue(parent_fds)
        for fd in parent_fds:
            with self.assertRaises(OSError) as caught:
                os.fstat(fd)
            self.assertEqual(errno.EBADF, caught.exception.errno)

    def test_each_phase_rejects_its_invalid_result_before_later_phases(self):
        base = self._results()
        rows = base['c2'].case_rows
        invalid = {
            'c2': replace(base['c2'], case_rows=tuple(reversed(rows))),
            'c3': replace(base['c3'], application_cases=('normal',)),
            'accounts': replace(base['accounts'], accounts=(
                replace(base['accounts'].accounts[0], source_sha256='f' * 64),
                *base['accounts'].accounts[1:])),
            'measurement': replace(base['measurement'], final_report_sha256='f' * 64),
            'tests': replace(base['tests'], project_count=2),
            'native': replace(base['native'], outcome='Failed'),
            'gate': replace(base['gate'], base_commit='f' * 40),
        }
        self._patch_source()
        for phase, value in invalid.items():
            with self.subTest(phase=phase):
                calls = self._patch_inputs(replacements={phase: value})
                with self.assertRaisesRegex(ValueError, 'completed-orchestration'):
                    self._run()
                self.assertEqual(list(subject.PHASES[:subject.PHASES.index(phase) + 1]),
                                 [row[0] for row in calls])
                self._reset_private()

    def test_gate_coverage_must_match_retained_test_inventory(self):
        self._patch_source()
        bad_gate = replace(self._results()['gate'], replay_coverage_sha256='e' * 64)
        with self.assertRaisesRegex(ValueError, 'gate-test-coverage-binding'):
            self._run_with_replacements({'gate': bad_gate})


if __name__ == '__main__':
    unittest.main()
