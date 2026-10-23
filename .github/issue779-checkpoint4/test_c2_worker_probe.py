"""Private measurement procedure/data controls, never a native worker or seven-case proof."""
from pathlib import Path
from unittest import mock
import copy
import hashlib
import json
import os
import signal
import subprocess
import sys
import tempfile
import time
import unittest

import c2_worker_probe as probe


class PrivateWorkerProbeDataTests(unittest.TestCase):
    def fixture(self):
        descriptor = {'worker_pid': 123, 'worker_uid': 1001, 'worker_gid': 1002,
                      'cgroup': '/system.slice/appsurface-evidence-worker-' + 'a' * 32 + '.service'}
        hashes = {name + suffix: hashlib.sha256((name + suffix).encode()).hexdigest()
                  for name in probe.MODULES for suffix in ('.dll', '.pdb')}
        rows = [{'module': name + '.dll', 'outcome': 'unchanged-worker-handoff', 'candidates': 2,
                 'worker_sha256': 'b' * 64, 'host_sha256': 'b' * 64,
                 'module_sha256': hashes[name + '.dll']} for name in probe.MODULES]
        value = {'schema': 'evidence-worker-measurement-v1', 'pid': 123, 'start_time': 456,
                 'uid': 1001, 'gid': 1002, 'cgroup': descriptor['cgroup'],
                 'original_pidfd_exit': True, 'modules': rows, 'native_acceptance': False}
        return value, descriptor, hashes

    def test_unchanged_worker_rows_are_data_only_and_optional_unused_module_is_absent(self):
        value, descriptor, hashes = self.fixture()
        self.assertEqual([name + '.dll' for name in probe.MODULES], probe.validate_handoff(value, descriptor, hashes))
        row = value['modules'][4]
        value['modules'][4] = {key: item for key, item in row.items()
                               if key in ('module', 'module_sha256')}
        value['modules'][4]['outcome'] = 'worker-file-absent'
        self.assertNotIn(probe.MODULES[4] + '.dll', probe.validate_handoff(value, descriptor, hashes))
        self.assertIs(value['native_acceptance'], False)

    def test_module_names_require_exact_actual_dll_filenames(self):
        value, descriptor, hashes = self.fixture()
        self.assertEqual(probe.MODULES[0] + '.dll',
                         probe.validate_handoff(value, descriptor, hashes)[0])
        value['modules'][0]['module'] = probe.MODULES[0]
        with self.assertRaisesRegex(ValueError, 'module-list'):
            probe.validate_handoff(value, descriptor, hashes)

    def test_one_changed_identity_or_terminal_field_rejects(self):
        for key, bad in [('pid', 124), ('uid', 1003), ('gid', 1004), ('start_time', True),
                         ('cgroup', '/system.slice/foreign.service'),
                         ('original_pidfd_exit', False), ('native_acceptance', True),
                         ('schema', 'evidence-worker-measurement-v2')]:
            with self.subTest(key=key):
                value, descriptor, hashes = self.fixture()
                value[key] = bad
                with self.assertRaises(ValueError):
                    probe.validate_handoff(value, descriptor, hashes)

    def test_changed_hits_image_or_missing_required_module_rejects(self):
        for change in ('digest', 'image', 'prior', 'missing', 'bool-count', 'too-large', 'duplicate'):
            with self.subTest(change=change):
                value, descriptor, hashes = self.fixture()
                row = value['modules'][0]
                if change == 'digest':
                    row['host_sha256'] = 'c' * 64
                elif change == 'image':
                    row['module_sha256'] = 'c' * 64
                elif change == 'prior':
                    value['modules'][0] = {'module': row['module'], 'outcome': 'prior-root-file-unchanged',
                                           'module_sha256': row['module_sha256']}
                elif change == 'missing':
                    value['modules'][0] = {'module': row['module'], 'outcome': 'worker-file-absent',
                                           'module_sha256': row['module_sha256']}
                elif change == 'bool-count':
                    row['candidates'] = True
                elif change == 'too-large':
                    row['candidates'] = 1048576
                else:
                    value['modules'][1] = copy.deepcopy(row)
                with self.assertRaises(ValueError):
                    probe.validate_handoff(value, descriptor, hashes)

    def test_owner_log_needs_one_exact_normal_witness(self):
        value, _, _ = self.fixture()
        raw = json.dumps(value).encode()
        self.assertEqual(value, probe.closed_events(b'non-json private message\n' + raw + b'\n'))
        for bad in (b'', b'{', raw + b'\n' + raw,
                    raw.replace(b'"pid": 123', b'"pid": 1, "pid": 123'),
                    b'x' * (probe.MAX_LOG + 1)):
            with self.subTest(length=len(bad)), self.assertRaises(ValueError):
                probe.closed_events(bad)

    def test_real_collector_process_is_joined_and_deadline_failure_stays_failure(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.assertEqual(0, probe.wait_collector(
                [sys.executable, '-c', 'print("ordinary process only")'],
                root / 'success.log', time.monotonic() + 5, time.monotonic() + 10))
            self.assertIn(b'ordinary process only', (root / 'success.log').read_bytes())
            with self.assertRaisesRegex(ValueError, 'collector-deadline'):
                probe.wait_collector([sys.executable, '-c', 'import time; time.sleep(10)'],
                                     root / 'deadline.log', time.monotonic() + 0.1, time.monotonic() + 5)

    def test_output_failure_allows_finally_to_join_a_real_separate_session_child(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            script = root / 'collector.py'
            pid_file, joined = root / 'child.pid', root / 'joined'
            script.write_text('''import subprocess,sys,signal,time
from pathlib import Path
child=subprocess.Popen([sys.executable,'-c','import time; time.sleep(30)'],start_new_session=True)
def stop(signum,frame):
    child.terminate()
    child.wait(timeout=5)
    Path(sys.argv[2]).write_text('child joined')
    raise SystemExit(143)
signal.signal(signal.SIGTERM,stop)
Path(sys.argv[1]).write_text(str(child.pid))
print('x'*2048,flush=True)
while True:time.sleep(1)
''')
            try:
                with mock.patch.object(probe, 'MAX_LOG', 1024):
                    with self.assertRaisesRegex(ValueError, 'collector-output-bound'):
                        probe.wait_collector([sys.executable, str(script), str(pid_file), str(joined)],
                                             root / 'limited.log', time.monotonic() + 5,
                                             time.monotonic() + 10)
                self.assertEqual('child joined', joined.read_text())
                with self.assertRaises(ProcessLookupError):
                    os.kill(int(pid_file.read_text()), 0)
            finally:
                if pid_file.exists():
                    try:
                        os.kill(int(pid_file.read_text()), signal.SIGKILL)
                    except ProcessLookupError:
                        pass

    def test_started_native_procedure_cannot_be_retained_without_settlement(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / 'native-started.json').write_text('{}')
            with self.assertRaises(FileNotFoundError):
                probe.retain(root)

    def test_actual_case_generation_and_groups_are_rechecked_after_original_finally(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / 'native').mkdir()
            result = root / 'native/normal.json'
            result.write_text(json.dumps({'generation': 'a' * 32, 'case': 'normal',
                                          'passed': False, 'final_groups_empty': True}))
            native = mock.Mock()
            native.groups_empty.return_value = False
            with self.assertRaisesRegex(ValueError, 'native-groups-empty'):
                probe.settle_native(root, native, time.monotonic() + 5)
            self.assertFalse((root / 'native-settlement.json').exists())
            native.groups_empty.return_value = True
            probe.settle_native(root, native, time.monotonic() + 5)
            receipt = json.loads((root / 'native-settlement.json').read_text())
            self.assertIs(receipt['native_acceptance'], False)
            self.assertFalse(json.loads(result.read_text())['passed'])
            self.assertEqual(['appsurface-evidence-' + role + '-' + 'a' * 32 + '.service'
                              for role in ('owner', 'worker', 'producer')], receipt['units'])

    def test_real_retention_nodes_modes_and_links_are_checked(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / 'normal-report.json').write_bytes(b'{}')
            (root / 'collector.log').write_bytes(b'')
            probe.retain(root)
            self.assertEqual(0o700, root.stat().st_mode & 0o777)
            self.assertEqual(0o600, (root / 'collector.log').stat().st_mode & 0o777)
            (root / 'linked.log').symlink_to(root / 'collector.log')
            with self.assertRaisesRegex(ValueError, 'retention-link'):
                probe.retain(root)
            (root / 'linked.log').unlink()
            os.link(root / 'collector.log', root / 'hard.log')
            with self.assertRaisesRegex(ValueError, 'retention-leaf'):
                probe.retain(root)

    def test_report_bound_is_specific_and_other_leaves_keep_smaller_limit(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            report = root / 'normal-report.json'
            with report.open('wb') as stream:
                stream.truncate(probe.MAX_LOG + 1)
            probe.retain(root)
            report.rename(root / 'unselected.json')
            with self.assertRaisesRegex(ValueError, 'retention-leaf'):
                probe.retain(root)


if __name__ == '__main__':
    unittest.main(verbosity=2)
