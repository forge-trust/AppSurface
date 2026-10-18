"""Portable guard-fixture data checks only; no root, account, native owner or accepted proof."""
from pathlib import Path
import importlib.util
import hashlib
import tempfile
import types
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('guard_controls', Path(__file__).with_name('workspace_guards.py'))
guards = importlib.util.module_from_spec(spec)
spec.loader.exec_module(guards)


class WorkspaceFixtureDataTests(unittest.TestCase):
    def test_each_bundle_mutation_changes_only_its_selected_real_fixture_node(self):
        for case in guards.APPLICATION_CASES:
            with self.subTest(case=case), tempfile.TemporaryDirectory() as temporary:
                tool = Path(temporary)
                (tool / 'application/inputs').mkdir(parents=True)
                selected = tool / 'application/inputs/declared-input.txt'
                selected.write_bytes(b'declared-native-input\n')
                selected.chmod(0o444)
                entry = tool / 'entry-sentinel'
                entry.write_bytes(b'compiled entry unchanged')
                original = guards.identity(entry)
                result = guards.mutate_bundle(tool, case)
                self.assertEqual(original, guards.identity(entry))
                self.assertEqual(b'compiled entry unchanged', entry.read_bytes())
                if case == 'bundle-extra-directory':
                    directory = tool / 'application/fixture-extra'
                    self.assertTrue(directory.is_dir())
                    self.assertEqual([], list(directory.iterdir()))
                    self.assertEqual(0o555, result['mode'])
                    self.assertIsNone(result['sha256'])
                    self.assertEqual(b'declared-native-input\n', selected.read_bytes())
                    directory.chmod(0o700)
                elif case == 'bundle-input-bytes':
                    self.assertEqual(b'Declared-native-input\n', selected.read_bytes())
                    self.assertEqual(0o444, result['mode'])
                    self.assertEqual(len(b'declared-native-input\n'), result['bytes'])
                else:
                    self.assertEqual(b'declared-native-input\n', selected.read_bytes())
                    self.assertEqual(0o555, result['mode'])
                    self.assertEqual(hashlib.sha256(selected.read_bytes()).hexdigest(), result['sha256'])
                selected.chmod(0o600)

    def test_unknown_mutation_rejects_before_any_fixture_file_io(self):
        with patch.object(guards.Path, 'lstat', side_effect=AssertionError('unexpected read')):
            with self.assertRaisesRegex(RuntimeError, '^workspace-guard-case$'):
                guards.mutate_bundle(Path('/not-selected'), 'raw-canary')

    def test_fixed_request_and_owner_recipe_preserve_policy_and_runtime_arguments(self):
        args = types.SimpleNamespace(source='a' * 40, base='b' * 40)
        request = guards.r.request_data(args, Path('/tool'), Path('/sdk'), {'application_id': 'fixture'},
                                       'c' * 64, Path('/subject'), 'normal')
        self.assertEqual('/sdk/dotnet', request['runtime_host'])
        self.assertEqual('/subject/Subject.csproj', request['solution'])
        self.assertEqual('c' * 64, request['application_entry_digest'])
        self.assertEqual([30, 120, 60, 120, 10], [request[key] for key in
                         ('admission_seconds', 'start_seconds', 'collection_seconds', 'cleanup_seconds', 'stopping_seconds')])
        command = guards.r.owner_command(Path('/sdk'), Path('/tool'), 'owned.service', Path('/guard/armed'), Path('/request.json'))
        self.assertIn('--property=User=0', command)
        self.assertIn('--property=Group=0', command)
        self.assertIn('--property=KillMode=control-group', command)
        self.assertIn('--property=TimeoutStopSec=10', command)
        self.assertIn('--property=ConditionPathExists=/guard/armed', command)
        position = command.index('/usr/bin/env')
        self.assertEqual(['/usr/bin/env', '-i', *guards.r.c2.FIXED, '/sdk/dotnet',
                          '/tool/ForgeTrust.AppSurface.Cli.dll', 'evidence', 'supervise', '--request', '/request.json'], command[position:])

    def test_rejection_requires_the_original_single_closed_failure(self):
        valid = {'phase': 'WorkspaceCreate', 'diagnostic_code': 'ASEVD410', 'error_kind': 'Admission'}
        with patch.object(guards.r, 'events', return_value=[valid]):
            self.assertIs(valid, guards.failure(Path('/unused'), 'WorkspaceCreate', 'ASEVD410'))
        for rows in ([], [valid, valid], [dict(valid, phase='ProducerSeal')],
                     [dict(valid, diagnostic_code='ASEVD402')], [dict(valid, error_kind='Io')]):
            with self.subTest(rows=rows), patch.object(guards.r, 'events', return_value=rows):
                with self.assertRaises(RuntimeError):
                    guards.failure(Path('/unused'), 'WorkspaceCreate', 'ASEVD410')

    def test_native_sequence_stops_on_original_failure_without_starting_later_controls(self):
        calls = []
        failed = {'passed': False, 'case': guards.APPLICATION_CASES[0]}
        with patch.object(guards, 'application_case', side_effect=lambda *args: calls.append(args[1]) or failed), \
             patch.object(guards.r, 'execute') as producer:
            self.assertEqual([failed], guards.run(None, None, None, 1))
            self.assertEqual([guards.APPLICATION_CASES[0]], calls)
            producer.assert_not_called()


class ContainmentReserveTests(unittest.TestCase):
    def test_launch_refuses_to_consume_the_original_containment_reserve(self):
        with patch.object(guards.time, 'monotonic', return_value=100):
            self.assertEqual(101, guards.observation_deadline(161, 180))
            self.assertEqual(280, guards.observation_deadline(1000, 180))
            for end in (99, 100, 159, 160):
                with self.subTest(end=end), self.assertRaisesRegex(RuntimeError, '^workspace-guard-containment-reserve$'):
                    guards.observation_deadline(end, 180)

    def test_producer_final_group_failure_stays_failed_and_stops_following_controls(self):
        result = {'passed': True, 'final_groups_empty': False, 'case': guards.PRODUCER_CASES[0]}
        with patch.object(guards, 'application_case', side_effect=lambda *args: {'passed': True}), \
             patch.object(guards.r, 'execute', return_value=result) as execute:
            rows = guards.run(None, None, None, 1000)
        self.assertEqual(4, len(rows))
        self.assertFalse(rows[-1]['passed'])
        self.assertEqual('workspace-guard-final-groups-not-empty', rows[-1]['failure_category'])
        execute.assert_called_once_with(None, guards.PRODUCER_CASES[0], None, None, 1000)

    def test_failed_stop_cannot_skip_any_manager_kill_and_client_join(self):
        calls = []
        process = types.SimpleNamespace(pid=12345, poll=lambda: None, wait=lambda **kwargs: calls.append(('join', kwargs)))
        def command(argv, end, **kwargs):
            calls.append((argv, end))
            if argv[1] == 'stop':
                raise TimeoutError('portable-stop-canary')
        with patch.object(guards.time, 'monotonic', return_value=100), \
             patch.object(guards.r, 'command', side_effect=command), \
             patch.object(guards.os, 'killpg') as killed:
            guards.contain_units(['owner.service', 'worker.service', 'producer.service', 'app.service'], process, 160)
            killed.assert_called_once_with(12345, guards.signal.SIGKILL)
        self.assertEqual(9, len(calls))
        for index, unit in enumerate(('owner.service', 'worker.service', 'producer.service', 'app.service')):
            self.assertEqual((['/usr/bin/systemctl', 'stop', unit], 105), calls[index * 2])
            self.assertEqual((['/usr/bin/systemctl', 'kill', '--kill-whom=all', '--signal=KILL', unit], 102), calls[index * 2 + 1])
        self.assertEqual(('join', {'timeout': 5}), calls[-1])


if __name__ == '__main__':
    unittest.main()
