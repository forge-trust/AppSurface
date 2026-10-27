"""Tests for the fixed public coverage-gate replay orchestration boundary."""
from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path
import sys
import tempfile
import time
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / '.github/issue779-protected-qualification'))
import completed_gate
import completed_gate_replay as subject
import completed_c1
import completed_native_replay
import completed_run


class GateReplayTests(unittest.TestCase):
    def test_expired_and_overlong_deadlines_reject_before_inspection(self):
        for deadline in (time.monotonic() - 1, time.monotonic() + subject.MAX_REPLAY_SECONDS + 1):
            with patch.object(completed_gate, 'inspect_completed_gate_archive') as inspect:
                with self.assertRaisesRegex(ValueError, 'deadline'):
                    subject.replay_completed_gate(object(), object(), Path('/stage'), b'', Path('/source'),
                        Path('/parent'), deadline=deadline)
                inspect.assert_not_called()

    def test_original_missing_or_failed_gate_rejects_before_build(self):
        stage = _stage()
        for report_passed in (False,):
            inputs = _gate_inputs(report_passed=report_passed)
            with patch.object(subject, '_linux_supported', return_value=True), \
                 patch.object(completed_gate, 'inspect_completed_gate_archive', return_value=inputs), \
                 patch.object(subject, '_source_capture') as source_capture:
                with self.assertRaisesRegex(ValueError, 'original-report-not-passing'):
                    subject.replay_completed_gate(stage, object(), Path('/stage'), b'capture',
                        Path('/source'), Path('/parent'), deadline=time.monotonic() + 600)
                source_capture.assert_not_called()
        with patch.object(subject, '_linux_supported', return_value=True), \
             patch.object(completed_gate, 'inspect_completed_gate_archive',
                          side_effect=ValueError('completed-gate-required-members')) as inspect, \
             patch.object(subject, '_run_public_cli') as run:
            with self.assertRaisesRegex(ValueError, 'required-members'):
                subject.replay_completed_gate(stage, object(), Path('/stage'), b'capture',
                    Path('/source'), Path('/parent'), deadline=time.monotonic() + 600)
            inspect.assert_called_once()
            run.assert_not_called()

    def test_fixed_replay_build_and_report_compare(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp).resolve()
            source, parent = root / 'source', root / 'private'
            source.mkdir(mode=0o700)
            parent.mkdir(mode=0o700)
            os.chmod(source, 0o700)
            os.chmod(parent, 0o700)
            stage = _stage()
            stage_root = root / 'stage'
            stage_root.mkdir(mode=0o700)
            os.chmod(stage_root, 0o700)
            diff = b'git diff --unified=0\n'
            original_report = _report(completed_gate.ORIGINAL_COVERAGE_PATH, diff)
            inputs = _gate_inputs(original_report=original_report)
            process_commands = []
            replace_coverage = {'value': False}
            extra_output = {'value': False}

            def run_process(argv, *, cwd, env, deadline, output_limit=8 * 1024 * 1024,
                            cleanup_deadline=None):
                process_commands.append(argv)
                if argv[0] == 'git' and 'diff' in argv:
                    return 0, diff, b''
                if argv[-1] == '--version':
                    return 0, b'10.0.401\n', b''
                return 0, b'fixed process output', b''

            def public_cli(source_root, coverage_path, output_path, *, deadline, cleanup_deadline=None):
                replay_report = _report(str(coverage_path), diff)
                for name, body in (
                    ('coverage-gate.json', replay_report),
                    ('coverage-gate.md', b'gate passed'),
                    ('coverage-patch-targets.json', b'{"targets": []}'),
                    ('coverage-patch-targets.md', b'no targets'),
                ):
                    path = output_path / name
                    path.write_bytes(body)
                    path.chmod(0o600)
                if replace_coverage['value']:
                    coverage_path.unlink()
                    coverage_path.write_bytes(b'<coverage />')
                    coverage_path.chmod(0o600)
                if extra_output['value']:
                    (output_path / 'unrecognized.txt').write_bytes(b'unexpected')
                    (output_path / 'unrecognized.txt').chmod(0o600)
                return (str(source_root / 'dotnet'), 'cli', 'coverage', 'gate'), (0, b'ok', b'')

            with patch.object(subject, '_linux_supported', return_value=True), \
                 patch.object(subject, 'SOURCE_MOUNT', source), \
                 patch.object(subject, '_linux_supported', return_value=True), \
                 patch.object(completed_gate, 'inspect_completed_gate_archive', return_value=inputs), \
                 patch.object(subject, '_source_capture', return_value=({'source': stage.head_sha,
                     'tree': stage.source_tree}, object())) as verify, \
                 patch.object(subject, '_git', side_effect=_git_values(stage)), \
                 patch.object(subject.completed_c1, '_load_trusted_current_binding', return_value=object()), \
                 patch.object(subject.completed_native_replay, '_dotnet_path', return_value='/usr/bin/dotnet'), \
                 patch.object(subject.completed_native_replay, '_controlled_environment', return_value={}), \
                 patch.object(subject.completed_native_replay, '_run_fixed_process', side_effect=run_process), \
                 patch.object(subject, '_run_public_cli', side_effect=public_cli):
                result = subject.replay_completed_gate(stage, object(), stage_root, b'capture',
                    source, parent, deadline=time.monotonic() + 600)
            self.assertTrue(result.reports_match)
            self.assertFalse(result.validator_executed)
            self.assertFalse(result.original_gate_success_proven)
            self.assertFalse(result.gate_eligible)
            self.assertFalse(result.admission_created)
            self.assertEqual(4, len(result.output_files))
            self.assertEqual(5, len(result.process_records))
            self.assertEqual(hashlib.sha256(diff).hexdigest(), result.diff_sha256)
            self.assertTrue(result.retained_work_root.exists())
            self.assertFalse(result.cleanup_complete)
            self.assertEqual(['--locked-mode'], [value for row in process_commands if 'restore' in row
                                                 for value in row if value == '--locked-mode'])
            verify.assert_called()
            self.assertTrue(verify.call_args_list[0].kwargs['require_no_ignored'])

            replace_coverage['value'] = True
            replacement_parent = root / 'coverage-replacement-private'
            replacement_parent.mkdir(mode=0o700)
            os.chmod(replacement_parent, 0o700)
            with patch.object(subject, '_linux_supported', return_value=True), \
                 patch.object(subject, 'SOURCE_MOUNT', source), \
                 patch.object(completed_gate, 'inspect_completed_gate_archive', return_value=inputs), \
                 patch.object(subject, '_source_capture', return_value=({'source': stage.head_sha,
                     'tree': stage.source_tree}, object())), \
                 patch.object(subject, '_git', side_effect=_git_values(stage)), \
                 patch.object(completed_c1, '_load_trusted_current_binding', return_value=object()), \
                 patch.object(completed_native_replay, '_dotnet_path', return_value='/usr/bin/dotnet'), \
                 patch.object(completed_native_replay, '_controlled_environment', return_value={}), \
                 patch.object(completed_native_replay, '_run_fixed_process', side_effect=run_process), \
                 patch.object(subject, '_run_public_cli', side_effect=public_cli):
                with self.assertRaisesRegex(ValueError, 'input-shape'):
                    subject.replay_completed_gate(stage, object(), stage_root, b'capture', source,
                        replacement_parent, deadline=time.monotonic() + 600)
            replace_coverage['value'] = False
            extra_output['value'] = True
            extra_parent = root / 'extra-output-private'
            extra_parent.mkdir(mode=0o700)
            os.chmod(extra_parent, 0o700)
            with patch.object(subject, '_linux_supported', return_value=True), \
                 patch.object(subject, 'SOURCE_MOUNT', source), \
                 patch.object(completed_gate, 'inspect_completed_gate_archive', return_value=inputs), \
                 patch.object(subject, '_source_capture', return_value=({'source': stage.head_sha,
                     'tree': stage.source_tree}, object())), \
                 patch.object(subject, '_git', side_effect=_git_values(stage)), \
                 patch.object(completed_c1, '_load_trusted_current_binding', return_value=object()), \
                 patch.object(completed_native_replay, '_dotnet_path', return_value='/usr/bin/dotnet'), \
                 patch.object(completed_native_replay, '_controlled_environment', return_value={}), \
                 patch.object(completed_native_replay, '_run_fixed_process', side_effect=run_process), \
                 patch.object(subject, '_run_public_cli', side_effect=public_cli):
                with self.assertRaisesRegex(ValueError, 'output-inventory'):
                    subject.replay_completed_gate(stage, object(), stage_root, b'capture', source,
                        extra_parent, deadline=time.monotonic() + 600)
            extra_output['value'] = False

            for mode, expected in (('sdk', 'sdk-version'), ('restore', 'restore-exit'),
                                   ('build', 'build-exit')):
                failed_parent = root / f'failed-private-{mode}'
                failed_parent.mkdir(mode=0o700)
                os.chmod(failed_parent, 0o700)
                def fail_before_cli(argv, *, cwd, env, deadline,
                                    output_limit=8 * 1024 * 1024, cleanup_deadline=None, mode=mode):
                    if mode == 'sdk' and argv[-1] == '--version':
                        return 0, b'8.0.100', b''
                    if mode == 'restore' and len(argv) > 1 and argv[1] == 'restore':
                        return 17, b'restore failed', b''
                    if mode == 'build' and len(argv) > 1 and argv[1] == 'build':
                        return 17, b'build failed', b''
                    return run_process(argv, cwd=cwd, env=env, deadline=deadline,
                                       output_limit=output_limit)
                with patch.object(subject, '_linux_supported', return_value=True), \
                     patch.object(subject, 'SOURCE_MOUNT', source), \
                     patch.object(completed_native_replay, '_dotnet_path', return_value='/usr/bin/dotnet'), \
                     patch.object(completed_native_replay, '_controlled_environment', return_value={}), \
                     patch.object(completed_gate, 'inspect_completed_gate_archive', return_value=inputs), \
                     patch.object(subject, '_source_capture', return_value=({'source': stage.head_sha,
                         'tree': stage.source_tree}, object())), \
                     patch.object(subject, '_git', side_effect=_git_values(stage)), \
                     patch.object(completed_c1, '_load_trusted_current_binding', return_value=object()), \
                     patch.object(completed_native_replay, '_run_fixed_process', side_effect=fail_before_cli), \
                     patch.object(subject, '_run_public_cli') as cli:
                    with self.assertRaisesRegex(ValueError, expected):
                        subject.replay_completed_gate(stage, object(), stage_root, b'capture', source,
                            failed_parent, deadline=time.monotonic() + 600)
                    cli.assert_not_called()

            mismatch_parent = root / 'diff-mismatch-private'
            mismatch_parent.mkdir(mode=0o700)
            os.chmod(mismatch_parent, 0o700)
            mismatched_inputs = _gate_inputs(original_report=_report(
                completed_gate.ORIGINAL_COVERAGE_PATH, b'a different original diff'))
            process_commands.clear()
            with patch.object(subject, '_linux_supported', return_value=True), \
                 patch.object(subject, 'SOURCE_MOUNT', source), \
                 patch.object(completed_native_replay, '_dotnet_path', return_value='/usr/bin/dotnet'), \
                 patch.object(completed_native_replay, '_controlled_environment', return_value={}), \
                 patch.object(completed_gate, 'inspect_completed_gate_archive',
                              return_value=mismatched_inputs), \
                 patch.object(subject, '_source_capture', return_value=({'source': stage.head_sha,
                     'tree': stage.source_tree}, object())), \
                 patch.object(subject, '_git', side_effect=_git_values(stage)), \
                 patch.object(completed_c1, '_load_trusted_current_binding', return_value=object()), \
                 patch.object(completed_native_replay, '_run_fixed_process', side_effect=run_process), \
                 patch.object(subject, '_run_public_cli') as cli:
                with self.assertRaisesRegex(ValueError, 'original-diff-binding'):
                    subject.replay_completed_gate(stage, object(), stage_root, b'capture', source,
                        mismatch_parent, deadline=time.monotonic() + 600)
                cli.assert_not_called()
            self.assertEqual(1, len(process_commands))
            self.assertIn('diff', process_commands[0])

    def test_source_private_parent_overlap_rejects_before_source_validation(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp).resolve()
            parent = root / 'private'
            parent.mkdir(mode=0o700)
            os.chmod(parent, 0o700)
            stage_root = root / 'stage'
            stage_root.mkdir(mode=0o700)
            stage = _stage()
            inputs = _gate_inputs()
            with patch.object(subject, '_linux_supported', return_value=True), \
                 patch.object(subject, 'SOURCE_MOUNT', parent), \
                 patch.object(completed_gate, 'inspect_completed_gate_archive', return_value=inputs), \
                 patch.object(subject, '_source_capture') as source_capture:
                with self.assertRaisesRegex(ValueError, 'root-overlap'):
                    subject.replay_completed_gate(stage, object(), stage_root, b'capture', parent,
                        parent, deadline=time.monotonic() + 600)
                source_capture.assert_not_called()

    def test_ignored_source_content_rejects_before_build(self):
        with tempfile.TemporaryDirectory() as temp:
            source = Path(temp).resolve()
            capture = {'source': 'a' * 40, 'tree': 'b' * 40}
            def git(_root, *args, deadline):
                if args[:2] == ('status', '--porcelain=v1') and '--ignored' in args:
                    return '!! Cli/ForgeTrust.AppSurface.Cli/obj/'
                if args == ('rev-parse', 'HEAD'):
                    return capture['source']
                if args == ('rev-parse', 'HEAD^{tree}'):
                    return capture['tree']
                return ''
            with patch.object(completed_c1, '_load_capture_and_binding',
                              return_value=(capture, object())), \
                 patch.object(subject, '_git', side_effect=git):
                with self.assertRaisesRegex(ValueError, 'source-ignored-files'):
                    subject._source_capture(b'capture', source, current_binding=object(),
                        deadline=time.monotonic() + 60, require_no_ignored=True)

    def test_public_cli_uses_exact_fixed_argv(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp).resolve()
            source = root / 'source'
            output = root / 'output'
            scratch = output / 'process-scratch'
            assembly = source / 'Cli/ForgeTrust.AppSurface.Cli/bin/Release/net10.0/ForgeTrust.AppSurface.Cli.dll'
            assembly.parent.mkdir(parents=True)
            scratch.mkdir(parents=True)
            assembly.write_bytes(b'fixture')
            coverage = root / 'coverage.xml'
            coverage.write_bytes(b'<coverage />')
            captured = {}

            def run(argv, *, cwd, env, deadline, output_limit, cleanup_deadline=None):
                captured.update(argv=argv, cwd=cwd, env=env, deadline=deadline,
                                output_limit=output_limit)
                return 0, b'', b''

            with patch.object(subject.completed_native_replay, '_dotnet_path', return_value='/sdk/dotnet'), \
                 patch.object(subject.completed_native_replay, '_controlled_environment',
                              return_value={'PATH': '/sdk'}), \
                 patch.object(subject.completed_native_replay, '_run_fixed_process', side_effect=run):
                argv, result = subject._run_public_cli(source, coverage, output, deadline=time.monotonic() + 60)
            self.assertEqual((0, b'', b''), result)
            self.assertEqual(argv, captured['argv'])
            self.assertEqual('/sdk/dotnet', argv[0])
            self.assertEqual(str(assembly), argv[1])
            self.assertEqual(('coverage', 'gate'), argv[2:4])
            self.assertEqual(('--coverage', str(coverage)), argv[4:6])
            self.assertEqual(('--repository-root', str(source)), argv[6:8])
            self.assertEqual(('--diff-base', completed_gate.FIXED_DIFF_BASE), argv[8:10])
            self.assertEqual(('--min-line', '95', '--min-branch', '85', '--min-patch-line', '95',
                              '--min-patch-branch', '85', '--patch-line-mode', 'codecov',
                              '--output', str(output), '--no-github-summary'), argv[10:])
            self.assertEqual(20 * 1024 * 1024, captured['output_limit'])
            self.assertEqual(source, captured['cwd'])

    def test_replay_metric_mismatch_preserves_private_workspace(self):
        # A covered metric difference must reject after the fixed CLI but preserve the private evidence.
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp).resolve()
            source, parent = root / 'source', root / 'private'
            source.mkdir(mode=0o700)
            parent.mkdir(mode=0o700)
            stage_root = root / 'stage'
            stage_root.mkdir(mode=0o700)
            os.chmod(source, 0o700)
            os.chmod(parent, 0o700)
            diff = b'diff'
            inputs = _gate_inputs(original_report=_report(completed_gate.ORIGINAL_COVERAGE_PATH, diff))
            def cli(source_root, coverage_path, output_path, *, deadline, cleanup_deadline=None):
                data = json.loads(_report(str(coverage_path), diff))
                data['line']['percent'] = 98
                for name in subject.GATE_OUTPUTS:
                    body = json.dumps(data).encode() if name == 'coverage-gate.json' else b'report'
                    path = output_path / name
                    path.write_bytes(body)
                    path.chmod(0o600)
                return ('dotnet', 'cli'), (0, b'', b'')
            with patch.object(subject, 'SOURCE_MOUNT', source), \
                 patch.object(subject, '_linux_supported', return_value=True), \
                 patch.object(completed_gate, 'inspect_completed_gate_archive', return_value=inputs), \
                 patch.object(subject, '_source_capture', return_value=({'source': _stage().head_sha,
                     'tree': _stage().source_tree}, object())), \
                 patch.object(subject, '_git', side_effect=_git_values(_stage())), \
                 patch.object(subject.completed_c1, '_load_trusted_current_binding', return_value=object()), \
                 patch.object(subject.completed_native_replay, '_dotnet_path', return_value='/usr/bin/dotnet'), \
                 patch.object(subject.completed_native_replay, '_controlled_environment', return_value={}), \
                 patch.object(subject.completed_native_replay, '_run_fixed_process',
                              side_effect=lambda argv, **kwargs: (0, diff if argv[0] == 'git' and 'diff' in argv
                                  else b'10.0.401' if argv[-1] == '--version' else b'', b'')), \
                 patch.object(subject, '_run_public_cli', side_effect=cli):
                with self.assertRaisesRegex(ValueError, 'report-mismatch') as caught:
                    subject.replay_completed_gate(_stage(), object(), stage_root, b'capture',
                        source, parent, deadline=time.monotonic() + 600)
            note = getattr(caught.exception, 'completed_gate_replay_workspace', None)
            self.assertTrue(note or any('workspace-preserved' in value
                                        for value in getattr(caught.exception, '__notes__', ())))
            self.assertTrue((parent / 'coverage-gate-replay').exists())


def _stage():
    return completed_run.CompletedRunStage(1, 700, 1, 'a' * 40, completed_run.WORKFLOW, (),
        'b' * 40, 'c' * 64, tuple(range(1, 38)), (), 0)


def _report(path, diff):
    value = {
        'passed': True, 'coverage': path, 'tolerancePercent': 0.5,
        'thresholds': {'line': 95, 'branch': 85, 'patchLine': 95, 'patchBranch': 85},
        'effectiveThresholds': {'line': 94.5, 'branch': 84.5, 'patchLine': 94.5, 'patchBranch': 84.5},
        'patchLineMode': 'codecov',
        'patchDiffSource': {'kind': 'git-base', 'label': completed_gate.FIXED_DIFF_BASE,
            'strictness': 'local-git', 'diffBase': completed_gate.FIXED_DIFF_BASE, 'path': None,
            'bytes': len(diff), 'sha256': hashlib.sha256(diff).hexdigest(), 'empty': not diff},
        'line': {'covered': 95, 'valid': 100, 'percent': 95},
        'branch': {'covered': 85, 'valid': 100, 'percent': 85},
        'patchLine': {'diffBase': completed_gate.FIXED_DIFF_BASE, 'changed': 2, 'measurable': 2,
            'covered': 2, 'percent': 100},
        'patchBranch': {'diffBase': completed_gate.FIXED_DIFF_BASE, 'changed': 2, 'measurable': 2,
            'covered': 2, 'percent': 100},
    }
    return json.dumps(value, separators=(',', ':')).encode()


def _gate_inputs(original_report=None, report_passed=True):
    diff = b'git diff --unified=0\n'
    original_report = original_report or _report(completed_gate.ORIGINAL_COVERAGE_PATH, diff)
    return completed_gate.GateArchiveInputs(700, 1, 'a' * 40,
        'issue779-checkpoint4-coverage-700-1', 'd' * 64, 'c' * 64, b'capture',
        completed_gate.FIXED_DIFF_BASE.encode() + b'\n', b'<coverage />', original_report,
        b'opaque transcript', (('source', 'e' * 64),), report_passed)


def _git_values(stage):
    def read(_root, *args, deadline):
        if args == ('rev-parse', 'HEAD'):
            return stage.head_sha
        if args == ('rev-parse', 'HEAD^{tree}'):
            return stage.source_tree
        return ''
    return read


if __name__ == '__main__':
    unittest.main()
