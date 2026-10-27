"""Portable behavior tests for bounded process control, not native qualification."""
from __future__ import annotations

import os
import json
from dataclasses import FrozenInstanceError
import sys
import tempfile
import time
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parent))
import completed_native_replay as replay
import completed_native_result
import test_completed_native_inputs as native_input_test
from test_completed_native_result import _fixture as trx_fixture


def _env():
    return {'PATH': os.path.dirname(sys.executable), 'LANG': 'C.UTF-8'}


def _deadline(seconds=5):
    return time.monotonic() + seconds


class NativeReplayProcessTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name).resolve()

    @unittest.skipUnless(hasattr(os, 'waitid') and hasattr(os, 'WNOWAIT'), 'requires Linux waitid')
    def test_fixed_process_returns_bounded_stdout_and_stderr(self):
        code, stdout, stderr = replay._run_fixed_process(
            (sys.executable, '-c', 'import sys; print("ready"); print("note", file=sys.stderr)'),
            cwd=self.root, env=_env(), deadline=_deadline())

        self.assertEqual(code, 0)
        self.assertEqual(stdout, b'ready\n')
        self.assertEqual(stderr, b'note\n')

    @unittest.skipUnless(hasattr(os, 'waitid') and hasattr(os, 'WNOWAIT'), 'requires Linux waitid')
    def test_fixed_process_rejects_shell_command_string(self):
        with self.assertRaisesRegex(ValueError, 'process-argv'):
            replay._run_fixed_process('echo unsafe', cwd=self.root, env=_env(), deadline=_deadline())

    @unittest.skipUnless(hasattr(os, 'waitid') and hasattr(os, 'WNOWAIT'), 'requires Linux waitid')
    def test_fixed_process_enforces_output_limit_and_joins(self):
        with self.assertRaisesRegex(replay.NativeReplayError, 'process-output-limit'):
            replay._run_fixed_process((sys.executable, '-c', 'print("x" * 10000)'),
                cwd=self.root, env=_env(), deadline=_deadline(), output_limit=1024)

    @unittest.skipUnless(hasattr(os, 'waitid') and hasattr(os, 'WNOWAIT'), 'requires Linux waitid')
    def test_fixed_process_kills_and_joins_at_deadline(self):
        with self.assertRaisesRegex(replay.NativeReplayError, 'process-deadline'):
            replay._run_fixed_process((sys.executable, '-c', 'import time; time.sleep(30)'),
                cwd=self.root, env=_env(), deadline=time.monotonic() + 0.15)

    @unittest.skipUnless(hasattr(os, 'waitid') and hasattr(os, 'WNOWAIT'), 'requires Linux waitid')
    def test_group_cleanup_failure_preserves_primary_and_still_joins(self):
        fault = ValueError('group-scan-primary')
        original_wait = replay._wait_group_quiescent
        calls = []

        def wait_group(pgid, deadline):
            calls.append(pgid)
            if len(calls) == 1:
                raise fault
            return original_wait(pgid, deadline)

        with patch.object(replay, '_wait_group_quiescent', side_effect=wait_group):
            with self.assertRaisesRegex(ValueError, 'group-scan-primary') as caught:
                replay._run_fixed_process((sys.executable, '-c', 'pass'), cwd=self.root,
                    env=_env(), deadline=_deadline(), cleanup_deadline=_deadline(10))
        self.assertIs(fault, caught.exception)
        self.assertGreaterEqual(len(calls), 2)

    @unittest.skipUnless(hasattr(os, 'waitid') and hasattr(os, 'WNOWAIT')
                         and Path('/proc').is_dir(), 'requires Linux waitid and procfs')
    def test_linux_normal_exit_kills_child_that_closed_inherited_pipes(self):
        ready = self.root / 'child-ready'
        pid_file = self.root / 'child-pid'
        group_file = self.root / 'child-group'
        parent_file = self.root / 'parent-pid'
        child_code = (
            f'import os,pathlib,time; pathlib.Path({str(group_file)!r}).write_text(str(os.getpgrp())); '
            f'pathlib.Path({str(ready)!r}).write_text("ready"); time.sleep(60)')
        code = f'''import pathlib, subprocess, sys, time
child = subprocess.Popen([sys.executable, "-c", {child_code!r}], stdin=subprocess.DEVNULL,
                        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
pathlib.Path({str(pid_file)!r}).write_text(str(child.pid))
pathlib.Path({str(parent_file)!r}).write_text(str(__import__("os").getpid()))
deadline = time.monotonic() + 5
while not pathlib.Path({str(ready)!r}).exists() and time.monotonic() < deadline:
    time.sleep(.01)
assert pathlib.Path({str(ready)!r}).exists()
'''
        result = replay._run_fixed_process((sys.executable, '-c', code),
            cwd=self.root, env=_env(), deadline=_deadline())

        self.assertEqual(0, result[0])
        child_pid = int(pid_file.read_text())
        self.assertEqual(int(parent_file.read_text()), int(group_file.read_text()))
        proc_stat = Path(f'/proc/{child_pid}/stat')
        if proc_stat.exists():
            # The child may remain as a zombie briefly before container init
            # reaps it; it must not remain an executing process after return.
            self.assertEqual('Z', proc_stat.read_text().split()[2])

    def test_runner_rejects_expired_or_overlong_deadline_before_paths(self):
        with self.assertRaisesRegex(ValueError, 'deadline-bound'):
            replay._deadline(time.monotonic() - 1)
        with self.assertRaisesRegex(ValueError, 'deadline-bound'):
            replay._deadline(time.monotonic() + replay.MAX_DEADLINE_SECONDS + 10)

    def test_runner_rejects_non_linux_before_native_work(self):
        with patch.object(replay, '_is_linux', return_value=False):
            with self.assertRaisesRegex(ValueError, 'linux-required'):
                replay.run_native_replay(None, None, None, None, None, None, deadline=_deadline())

    def test_environment_does_not_inherit_dotnet_test_or_coverage_switches(self):
        old = {key: os.environ.get(key) for key in
               ('VSTEST_CONNECTION_TIMEOUT', 'MSBuildSDKsPath', 'EVIDENCEHOST_NATIVE_MEASUREMENT')}
        try:
            os.environ['VSTEST_CONNECTION_TIMEOUT'] = '999'
            os.environ['MSBuildSDKsPath'] = '/attacker'
            os.environ['EVIDENCEHOST_NATIVE_MEASUREMENT'] = '1'
            build = replay._controlled_environment('/opt/dotnet/dotnet', self.root)
            native = replay._controlled_environment('/opt/dotnet/dotnet', self.root, native=True)
        finally:
            for key, value in old.items():
                if value is None:
                    os.environ.pop(key, None)
                else:
                    os.environ[key] = value

        self.assertNotIn('VSTEST_CONNECTION_TIMEOUT', build)
        self.assertNotIn('MSBuildSDKsPath', build)
        self.assertNotIn('EVIDENCEHOST_NATIVE_MEASUREMENT', build)
        self.assertEqual(native['EVIDENCEHOST_NATIVE_MEASUREMENT'], '1')

    def test_public_result_projection_is_non_authorizing(self):
        result = replay.NativeReplayResult(
            'issue779-native-replay-observation-v1', 1, 1, 'a' * 40, 'b' * 64,
            replay.SDK_VERSION, (), (), 'c' * 64, 1, 'd' * 64, 1, 'fixed', 'Passed',
            str(self.root), str(self.root), str(self.root), str(self.root), False)

        self.assertFalse(result.validator_executed)
        self.assertFalse(result.native_acceptance)
        self.assertFalse(result.qualification_complete)
        self.assertFalse(result.gate_eligible)
        with self.assertRaises(FrozenInstanceError):
            result.native_acceptance = True

    def _controller_fixture(self):
        fixture = native_input_test.NativeInputTests()
        fixture.setUp()
        self.addCleanup(fixture.tearDown)
        source = fixture.base / 'source'
        source.mkdir(mode=0o700)
        os.chmod(source, 0o700)
        (source / 'ForgeTrust.AppSurface.slnx').write_text('synthetic test seam')
        project = source / completed_native_result.PROJECT
        project.parent.mkdir(parents=True, mode=0o700)
        project.write_text('synthetic controller test project')
        return fixture, source

    def _mock_controller_processes(self, fixture, source, calls, *, mutate_copy=False,
                                  sdk=b'10.0.401\n', build_code=0,
                                  unexpected_output=False):
        def run(argv, *, cwd, env, deadline, output_limit=replay.MAX_PROCESS_OUTPUT,
                cleanup_deadline=None):
            calls.append(argv)
            if argv[1] == '--version':
                return 0, sdk, b''
            if argv[1] == 'restore':
                return 0, b'restored\n', b''
            if argv[1] == 'build':
                if build_code == 0:
                    assembly = source / completed_native_result.ASSEMBLY
                    assembly.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
                    assembly.write_bytes(b'fixture assembly; no native code executes')
                return build_code, b'built\n', b''
            if argv[1] == 'vstest':
                output = Path(next(arg.split(':', 1)[1] for arg in argv
                                   if arg.startswith('--ResultsDirectory:')))
                trx = trx_fixture()
                (output / completed_native_result.RESULT_FILE).write_bytes(trx)
                if unexpected_output:
                    (output / 'unowned.txt').write_text('keep')
                if mutate_copy:
                    (source / replay.NATIVE_INPUT_ROOT / 'source.json').write_bytes(b'changed')
                return 0, b'one synthetic seam result\n', b''
            raise AssertionError('unexpected fixed command')

        return patch.multiple(
            replay,
            _is_linux=lambda: True,
            _capture_and_binding=lambda raw, root, deadline: ({
                'source': fixture.stage.head_sha,
                'tree': fixture.stage.source_tree,
            }, object()),
            _git=lambda root, *args, deadline, maximum=4096: b'',
            _dotnet_path=lambda: '/usr/share/dotnet/dotnet',
            _run_fixed_process=run,
        )

    def test_public_controller_stages_copies_rechecks_and_returns_only_observation(self):
        fixture, source = self._controller_fixture()
        calls = []
        with self._mock_controller_processes(fixture, source, calls):
            result = replay.run_native_replay(
                fixture.stage, fixture.inventory, fixture.stage_root, fixture.capture,
                source, fixture.parent, deadline=_deadline(40))

        self.assertEqual(24, len(replay.completed_native_inputs.EXPECTED_OUTPUTS))
        self.assertEqual('Passed', result.outcome)
        self.assertEqual(completed_native_result.TEST_METHOD, result.test_name)
        self.assertEqual(["--version", "restore", "build", "vstest"], [argv[1] for argv in calls])
        self.assertFalse(result.validator_executed)
        self.assertFalse(result.native_acceptance)
        self.assertFalse(result.qualification_complete)
        self.assertFalse(result.gate_eligible)
        self.assertFalse(result.cleanup_complete)
        self.assertEqual([], list((source / 'TestResults').glob('*')) if (source / 'TestResults').exists() else [])
        self.assertTrue((Path(result.retained_test_output) / completed_native_result.RESULT_FILE).is_file())
        self.assertTrue((Path(result.retained_logs) / 'test.log').is_file())

    def test_wrong_sdk_fails_before_restore_or_build_and_cleans_inputs(self):
        fixture, source = self._controller_fixture()
        calls = []
        with self._mock_controller_processes(fixture, source, calls, sdk=b'10.0.402\n'):
            with self.assertRaisesRegex(ValueError, 'sdk-version'):
                replay.run_native_replay(fixture.stage, fixture.inventory, fixture.stage_root,
                    fixture.capture, source, fixture.parent, deadline=_deadline(40))
        self.assertEqual(['--version'], [argv[1] for argv in calls])
        self.assertFalse((source / replay.NATIVE_INPUT_ROOT).exists())

    def test_build_failure_fails_closed_and_never_runs_validator(self):
        fixture, source = self._controller_fixture()
        calls = []
        with self._mock_controller_processes(fixture, source, calls, build_code=7):
            with self.assertRaisesRegex(ValueError, 'build-1-failed'):
                replay.run_native_replay(fixture.stage, fixture.inventory, fixture.stage_root,
                    fixture.capture, source, fixture.parent, deadline=_deadline(40))
        self.assertEqual(['--version', 'restore', 'build'], [argv[1] for argv in calls])
        self.assertFalse((source / replay.NATIVE_INPUT_ROOT).exists())

    def test_copied_input_mutation_is_detected_after_validator(self):
        fixture, source = self._controller_fixture()
        calls = []
        with self._mock_controller_processes(fixture, source, calls, mutate_copy=True):
            with self.assertRaisesRegex(ValueError, 'file-shape|file-changed|file-binding|file-custody'):
                replay.run_native_replay(fixture.stage, fixture.inventory, fixture.stage_root,
                    fixture.capture, source, fixture.parent, deadline=_deadline(40))

    def test_source_verification_failure_after_build_prevents_test_and_result(self):
        fixture, source = self._controller_fixture()
        calls = []
        source_check = ({'source': fixture.stage.head_sha, 'tree': fixture.stage.source_tree}, object())
        with self._mock_controller_processes(fixture, source, calls):
            with patch.object(replay, '_capture_and_binding', side_effect=[
                    source_check, ValueError('completed-capture-source-changed')]):
                with self.assertRaisesRegex(ValueError, 'source-changed'):
                    replay.run_native_replay(fixture.stage, fixture.inventory, fixture.stage_root,
                        fixture.capture, source, fixture.parent, deadline=_deadline(40))
        self.assertEqual(['--version', 'restore', 'build'], [argv[1] for argv in calls])

    def test_existing_testresults_path_is_preserved(self):
        fixture, source = self._controller_fixture()
        existing = source / 'TestResults'
        existing.mkdir()
        sentinel = existing / 'keep.txt'
        sentinel.write_text('caller file')
        calls = []
        with self._mock_controller_processes(fixture, source, calls):
            with self.assertRaisesRegex(ValueError, 'testresults-exists'):
                replay.run_native_replay(fixture.stage, fixture.inventory, fixture.stage_root,
                    fixture.capture, source, fixture.parent, deadline=_deadline(40))
        self.assertEqual('caller file', sentinel.read_text())
        self.assertEqual([], calls)

    def test_expired_caller_deadline_does_not_create_any_output(self):
        fixture, source = self._controller_fixture()
        with patch.object(replay, '_is_linux', return_value=True):
            with self.assertRaisesRegex(ValueError, 'deadline-bound'):
                replay.run_native_replay(fixture.stage, fixture.inventory, fixture.stage_root,
                    fixture.capture, source, fixture.parent, deadline=time.monotonic() - 1)
        self.assertEqual([], list(fixture.parent.iterdir()))

    def test_unowned_test_output_is_preserved_and_cleanup_does_not_adopt_it(self):
        fixture, source = self._controller_fixture()
        calls = []
        with self._mock_controller_processes(fixture, source, calls, unexpected_output=True):
            with self.assertRaisesRegex(ValueError, 'trx-inventory'):
                replay.run_native_replay(fixture.stage, fixture.inventory, fixture.stage_root,
                    fixture.capture, source, fixture.parent, deadline=_deadline(40))
        output = fixture.parent / replay.OUTPUT_NAME
        self.assertEqual('keep', (output / 'unowned.txt').read_text())



if __name__ == '__main__':
    unittest.main()
