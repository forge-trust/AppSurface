"""Portable data/real-FD controls only; never invoke the native root entry."""
import importlib.util
import hashlib
import json
import os
import tempfile
import time
import unittest
from unittest import mock
from pathlib import Path

p = Path(__file__).with_name('run-native-probe.py')
spec = importlib.util.spec_from_file_location('native_probe_controls', p)
m = importlib.util.module_from_spec(spec)
spec.loader.exec_module(m)


class ProbeDataControls(unittest.TestCase):
    def test_fresh_private_directory_has_explicit_owner_and_mode_under_parent_bits(self):
        with tempfile.TemporaryDirectory() as temp:
            parent=Path(temp); os.chmod(parent,0o2750); previous=os.umask(0o077)
            try:
                facts=m.create_private_directory(parent/'owned',os.getuid(),os.getgid())
            finally:
                os.umask(previous)
            self.assertEqual({'uid':os.getuid(),'gid':os.getgid(),'mode':'0700',
                              'directory':True,'symlink':False},facts['after'])
            fd=m.directory(parent/'owned',os.getuid(),os.getgid(),0o700)
            os.close(fd)

    def test_private_directory_creation_never_reowns_existing_directory_or_link(self):
        with tempfile.TemporaryDirectory() as temp:
            parent=Path(temp); owned=parent/'existing'; owned.mkdir(mode=0o750)
            (owned/'sentinel').write_bytes(b'unchanged'); (parent/'link').symlink_to(owned)
            before=m.directory_facts(owned.lstat())
            for path in (owned,parent/'link'):
                with self.subTest(path=path),self.assertRaises(FileExistsError):
                    m.create_private_directory(path,os.getuid(),os.getgid())
            self.assertEqual(before,m.directory_facts(owned.lstat()))
            self.assertEqual(b'unchanged',(owned/'sentinel').read_bytes())

    def test_directory_substitution_or_writable_parent_never_changes_metadata(self):
        with tempfile.TemporaryDirectory() as temp:
            parent=Path(temp); replacement=parent/'replacement'; replacement.mkdir(mode=0o700)
            (replacement/'sentinel').write_bytes(b'unchanged'); real_open=os.open
            def substitute(path,flags,*args,**kwargs):
                if path=='owned':
                    (parent/'owned').rename(parent/'created-held'); replacement.rename(parent/'owned')
                return real_open(path,flags,*args,**kwargs)
            with mock.patch.object(m.os,'open',side_effect=substitute),mock.patch.object(m.os,'fchown')as chown,mock.patch.object(m.os,'fchmod')as chmod:
                with self.assertRaisesRegex(m.Failure,'directory-created'):
                    m.create_private_directory(parent/'owned',os.getuid(),os.getgid())
                chown.assert_not_called(); chmod.assert_not_called()
            self.assertEqual(b'unchanged',(parent/'owned'/'sentinel').read_bytes())
            self.assertEqual(0o700,(parent/'owned').stat().st_mode&0o777)
            os.chmod(parent,0o777)
            with mock.patch.object(m.os,'fchown')as chown,mock.patch.object(m.os,'fchmod')as chmod:
                with self.assertRaisesRegex(m.Failure,'directory-parent'):
                    m.create_private_directory(parent/'never-created',os.getuid(),os.getgid())
                chown.assert_not_called(); chmod.assert_not_called()
            self.assertFalse((parent/'never-created').exists())

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


class BuildCopyControls(unittest.TestCase):
    """Actual small FD copies under caller ownership; no root/runtime claim."""

    RESOURCE = 'Microsoft.Build.Utilities.Core.resources.dll'
    REQUIRED = ('CounterFixture.dll', 'CounterFixture.pdb', 'CounterFixture.runtimeconfig.json',
                'CounterFixture.deps.json', 'OfficialTaskHost.dll',
                'OfficialTaskHost.runtimeconfig.json', 'OfficialTaskHost.deps.json')

    def fixture(self, root):
        build, tool = root / 'build', root / 'tool'
        build.mkdir(mode=0o700); tool.mkdir(mode=0o700)
        expected = {name: b'tiny-metadata-' + name.encode() for name in self.REQUIRED}
        expected['cs/' + self.RESOURCE] = b'\x00tiny-culture-marker\xff'
        for relative, content in expected.items():
            path = build / relative
            if path.parent != build:
                path.parent.mkdir(mode=0o700, exist_ok=True)
            path.write_bytes(content)
        outside = root / 'outside'
        outside.mkdir(mode=0o700)
        (outside / 'sentinel').write_bytes(b'outside-unchanged')
        return build, tool, expected, outside

    def test_closed_culture_copy_preserves_exact_bytes_modes_and_manifest_cleanup(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp).resolve()
            build, tool, expected, outside = self.fixture(root)
            result = m.copy_build(build, tool, time.monotonic() + 5)
            self.assertEqual({'files', 'directories', 'inventory', 'total_bytes'}, set(result))
            self.assertEqual(sorted(expected), sorted(result['files']))
            self.assertEqual(['cs'], result['directories'])
            self.assertIsInstance(result['inventory'], list)
            self.assertEqual(len(expected), len(result['inventory']))
            self.assertEqual(sum(len(content) for content in expected.values()), result['total_bytes'])
            inventory = {row['name']: row for row in result['inventory']}
            self.assertEqual(set(expected), set(inventory))
            for relative, content in expected.items():
                self.assertEqual({'name': relative, 'kind': 'file', 'bytes': len(content),
                                  'sha256': hashlib.sha256(content).hexdigest()}, inventory[relative])
            self.assertEqual({'cs', *self.REQUIRED}, {path.name for path in tool.iterdir()})
            self.assertEqual([self.RESOURCE], [path.name for path in (tool / 'cs').iterdir()])
            self.assertEqual(0o555, (tool / 'cs').stat().st_mode & 0o777)
            for relative, content in expected.items():
                path = tool / relative
                self.assertEqual(content, path.read_bytes())
                self.assertEqual(0o444, path.stat().st_mode & 0o777)
                self.assertEqual(os.getuid(), path.stat().st_uid)
                self.assertEqual(content, (build / relative).read_bytes())
            # Portable caller needs directory write permission to exercise exact
            # manifest removal; actual controller cleanup is root-owned.
            (tool / 'cs').chmod(0o700)
            for relative in result['files']:
                (tool / relative).unlink()
            for relative in result['directories']:
                (tool / relative).rmdir()
            tool.rmdir()
            self.assertFalse(tool.exists())
            self.assertEqual(b'outside-unchanged', (outside / 'sentinel').read_bytes())

    def test_unknown_or_unsafe_culture_entries_never_supply_external_bytes(self):
        for case in ('unknown-culture', 'extra-resource', 'nested-directory', 'culture-symlink',
                     'child-symlink', 'hardlink', 'fifo'):
            with self.subTest(case=case), tempfile.TemporaryDirectory() as temp:
                root = Path(temp).resolve()
                build, tool, expected, outside = self.fixture(root)
                culture = build / 'cs'; child = culture / self.RESOURCE
                if case == 'unknown-culture':
                    culture.rename(build / 'unknown-Culture')
                elif case == 'extra-resource':
                    (culture / 'unexpected.resources.dll').write_bytes(b'not-selected')
                elif case == 'nested-directory':
                    child.unlink(); child.mkdir()
                    (child / 'nested').write_bytes(b'not-selected')
                elif case == 'culture-symlink':
                    culture.rename(outside / 'held-culture'); culture.symlink_to(outside / 'held-culture', target_is_directory=True)
                elif case == 'child-symlink':
                    child.unlink(); child.symlink_to(outside / 'sentinel')
                elif case == 'hardlink':
                    os.link(child, outside / 'second-link')
                elif case == 'fifo':
                    child.unlink(); os.mkfifo(child)
                with self.assertRaises((m.Failure, OSError)):
                    m.copy_build(build, tool, time.monotonic() + 5)
                self.assertFalse((tool / 'cs' / self.RESOURCE).exists())
                self.assertEqual(b'outside-unchanged', (outside / 'sentinel').read_bytes())

    def test_source_child_substitution_and_write_fault_fail_without_touching_outside(self):
        for case in ('directory-substitution', 'substitution', 'write-fault'):
            with self.subTest(case=case), tempfile.TemporaryDirectory() as temp:
                root = Path(temp).resolve()
                build, tool, expected, outside = self.fixture(root)
                child = build / 'cs' / self.RESOURCE
                real_open, real_write = os.open, os.write
                triggered = False

                def substituted_open(path, flags, *args, **kwargs):
                    nonlocal triggered
                    if case == 'directory-substitution' and not triggered and path == 'cs':
                        triggered = True
                        (build / 'cs').rename(outside / 'held-directory')
                        (build / 'cs').mkdir()
                        child.write_bytes(expected['cs/' + self.RESOURCE])
                    if case == 'substitution' and not triggered and path == self.RESOURCE and not flags & (os.O_CREAT | os.O_WRONLY | os.O_RDWR):
                        triggered = True
                        child.rename(outside / 'held-original')
                        child.write_bytes(b'actual-replacement')
                    return real_open(path, flags, *args, **kwargs)

                def faulted_write(fd, content):
                    nonlocal triggered
                    if case == 'write-fault':
                        triggered = True
                        raise OSError('portable-injected-write-fault')
                    return real_write(fd, content)

                with mock.patch.object(m.os, 'open', side_effect=substituted_open), mock.patch.object(m.os, 'write', side_effect=faulted_write):
                    if case == 'directory-substitution':
                        with self.assertRaisesRegex(m.Failure, 'build-directory-changed'):
                            m.copy_build(build, tool, time.monotonic() + 5)
                    else:
                        with self.assertRaises((m.Failure, OSError)):
                            m.copy_build(build, tool, time.monotonic() + 5)
                self.assertTrue(triggered)
                self.assertEqual(b'outside-unchanged', (outside / 'sentinel').read_bytes())
                if case == 'substitution':
                    self.assertEqual(expected['cs/' + self.RESOURCE], (outside / 'held-original').read_bytes())
                if case == 'directory-substitution':
                    self.assertFalse((tool / 'cs').exists())
                    self.assertEqual(expected['cs/' + self.RESOURCE], (outside / 'held-directory' / self.RESOURCE).read_bytes())
    def test_mixed_closed_resources_copy_and_unknown_fourth_retains_rejected_inventory(self):
        resource_names = (self.RESOURCE, 'Microsoft.Build.Framework.resources.dll',
                          'Microsoft.NET.StringTools.resources.dll')
        for count in (2, 3, 4):
            with self.subTest(resource_count=count), tempfile.TemporaryDirectory() as temp:
                root = Path(temp).resolve()
                build, tool, expected, outside = self.fixture(root)
                for resource in resource_names[1:min(count, 3)]:
                    relative = 'cs/' + resource
                    content = b'\x00mixed-resource-' + resource.encode() + b'\xff'
                    (build / relative).write_bytes(content)
                    expected[relative] = content
                if count == 4:
                    unknown = 'Unknown.Fourth.resources.dll'
                    (build / 'cs' / unknown).write_bytes(b'not-admitted')
                diagnostics = []
                if count == 4:
                    with self.assertRaisesRegex(m.Failure, '^build-resource-inventory$'):
                        m.copy_build(build, tool, time.monotonic() + 5, diagnostics)
                    self.assertEqual([{'culture': 'cs', 'names': sorted((*resource_names, unknown))}], diagnostics)
                    self.assertEqual(4, len(diagnostics[0]['names']))
                    self.assertFalse((tool / 'cs').exists())
                    self.assertTrue((build / 'cs' / unknown).is_file())
                else:
                    result = m.copy_build(build, tool, time.monotonic() + 5, diagnostics)
                    selected = sorted(resource_names[:count])
                    self.assertEqual([{'culture': 'cs', 'names': selected}], diagnostics)
                    self.assertEqual(['cs'], result['directories'])
                    self.assertEqual(sorted(expected), sorted(result['files']))
                    self.assertEqual(selected, sorted(path.name for path in (tool / 'cs').iterdir()))
                    self.assertEqual(0o555, (tool / 'cs').stat().st_mode & 0o777)
                    self.assertEqual(sum(len(content) for content in expected.values()), result['total_bytes'])
                    inventory = {row['name']: row for row in result['inventory']}
                    self.assertEqual(len(expected), len(result['inventory']))
                    self.assertEqual(set(expected), set(inventory))
                    for relative, content in expected.items():
                        path = tool / relative
                        self.assertEqual(content, path.read_bytes())
                        self.assertEqual(content, (build / relative).read_bytes())
                        self.assertEqual(0o444, path.stat().st_mode & 0o777)
                        self.assertEqual(os.getuid(), path.stat().st_uid)
                        self.assertEqual({'name': relative, 'kind': 'file', 'bytes': len(content),
                                          'sha256': hashlib.sha256(content).hexdigest()}, inventory[relative])
                self.assertEqual(b'outside-unchanged', (outside / 'sentinel').read_bytes())


if __name__ == '__main__':
    unittest.main()
