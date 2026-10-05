"""Portable data/real-FD controls only; never invoke the native root entry."""
import importlib.util
import json
import os
import tempfile
import time
import unittest
from pathlib import Path

p = Path(__file__).with_name('run-native-probe.py')
spec = importlib.util.spec_from_file_location('native_probe_controls', p)
m = importlib.util.module_from_spec(spec)
spec.loader.exec_module(m)


class ProbeDataControls(unittest.TestCase):
    def test_failed_stop_requires_explicit_absent_unit_not_current_empty_cgroup(self):
        inactive = dict(LoadState='loaded', ActiveState='inactive', SubState='dead', MainPID='0')
        self.assertEqual('checked-stop', m.confirmed_unit_stop(0, 0, inactive))
        absent = dict(inactive, LoadState='not-found')
        self.assertEqual('checked-absent-unit', m.confirmed_unit_stop(5, 0, absent))
        for stop, query, facts in ((1, 0, inactive), (5, 1, absent), (0, 0, dict(inactive, MainPID='123')),
                                   (0, 0, dict(inactive, ActiveState='activating')), (True, 0, inactive)):
            with self.subTest(stop=stop, query=query, facts=facts), self.assertRaises(m.Failure):
                m.confirmed_unit_stop(stop, query, facts)

    def test_public_task_prepared_packet_uses_full_path(self):
        value = {'stage': 'prepared', 'state': '/opt/run/session/abc.state', 'temp': '/opt/run/session/'}
        self.assertEqual(value, m.packet(json.dumps(value).encode(), 'prepared'))

    def test_packet_duplicate_unknown_and_wrong_stage_reject(self):
        for value in (b'{"stage":"role-ready","stage":"role-ready"}',
                      b'{"stage":"role-ready","canary":1}', b'{"stage":"prepared"}', b'not-json'):
            with self.subTest(value=value), self.assertRaises(m.Failure):
                m.packet(value, 'role-ready')

    def test_completed_requires_actual_positive_denials_and_calculation(self):
        self.assertEqual(3, m.packet(b'{"stage":"completed","denials_passed":true,"value":3}', 'completed')['value'])
        for denied, value in ((False, 3), (True, True), (True, 2)):
            with self.subTest(denied=denied, value=value), self.assertRaises(m.Failure):
                m.packet(json.dumps(dict(stage='completed', denials_passed=denied, value=value)).encode(), 'completed')

    def test_unit_numeric_and_exact_closed_fields(self):
        data = {name: '' for name in m.FIELDS}
        data.update(MainPID='123', ExecMainCode='0', ExecMainStatus='0')
        wire = '\n'.join(k + '=' + v for k, v in data.items()).encode()
        self.assertEqual(123, m.unit_data(wire)['MainPID'])
        for bad in (wire + b'\nMainPID=1', wire.replace(b'MainPID=123', b'MainPID=-1'), wire + b'\nsecret=canary'):
            with self.subTest(bad=bad), self.assertRaises(m.Failure):
                m.unit_data(bad)

    def test_worker_namespace_mount_requires_exact_path_device_and_flags(self):
        row = '42 1 0:123 / /opt/run/session rw,nosuid,nodev,noexec - tmpfs tmpfs rw,size=32768k'
        self.assertTrue(m.mount_data(row, Path('/opt/run/session'), os.makedev(0, 123))['visible'])
        for changed in (row.replace('/opt/run/session', '/opt/other'), row.replace('tmpfs tmpfs', 'ext4 disk'),
                        row.replace('nodev,', ''), row + '\n' + row):
            with self.subTest(changed=changed), self.assertRaises(m.Failure):
                m.mount_data(changed, Path('/opt/run/session'), os.makedev(0, 123))

    def test_regular_fd_bytes_and_bounds(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); (root / 'file').write_bytes(b'\x00actual\xff'); os.chmod(root / 'file', 0o600)
            fd = os.open(root, os.O_RDONLY | os.O_DIRECTORY)
            try:
                self.assertEqual(b'\x00actual\xff', m.read_file(fd, 'file', time.monotonic()+2, os.getuid(), mode=0o600)[0])
                for kwargs in ({'cap': 2}, {'uid': os.getuid()+1}, {'mode': 0o644}):
                    options = dict(uid=os.getuid(), mode=0o600); options.update(kwargs)
                    with self.subTest(options=options), self.assertRaises(m.Failure):
                        m.read_file(fd, 'file', time.monotonic()+2, **options)
            finally:
                os.close(fd)

    def test_symlink_hardlink_and_fifo_never_supply_state_bytes(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); (root/'file').write_bytes(b'actual'); os.link(root/'file', root/'hard')
            (root/'link').symlink_to('file'); os.mkfifo(root/'fifo')
            fd = os.open(root, os.O_RDONLY | os.O_DIRECTORY)
            try:
                for name in ('hard', 'link', 'fifo'):
                    with self.subTest(name=name), self.assertRaises((m.Failure, OSError)):
                        m.read_file(fd, name, time.monotonic()+2, os.getuid())
            finally:
                os.close(fd)

    def test_deadline_before_open_and_basename_traversal(self):
        with self.assertRaises(m.Failure):
            m.read_file(-1, 'file', time.monotonic()-1, os.getuid())
        for name in ('../file', 'a/b', '.', '..', 'bad\x00name'):
            with self.subTest(name=name), self.assertRaises(m.Failure):
                m.name(name)


if __name__ == '__main__':
    unittest.main()
