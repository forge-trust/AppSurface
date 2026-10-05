"""Portable real-FD procedure controls, not executable .NET/native proof."""
import importlib.util
import json
import os
from pathlib import Path
import stat
import tempfile
import time
import unittest
from unittest import mock

spec = importlib.util.spec_from_file_location('sealed_runtime_controls', Path(__file__).with_name('sealed-runtime.py'))
m = importlib.util.module_from_spec(spec)
spec.loader.exec_module(m)


class SealedRuntimeControls(unittest.TestCase):
    def fixture(self, root):
        source = root / 'input'
        source.mkdir(mode=0o700)
        (source / 'dotnet').write_bytes(b'\x7fELFportable-marker-host')
        for version in ('10.0.2', '10.0.10'):
            fxr = source / 'host' / 'fxr' / version
            fxr.mkdir(parents=True)
            (fxr / 'libhostfxr.so').write_bytes(b'\x7fELFportable-fxr')
            framework = source / 'shared' / 'Microsoft.NETCore.App' / version
            framework.mkdir(parents=True)
            for name, content in {'System.Private.CoreLib.dll': b'portable-dll', 'libhostpolicy.so': b'\x7fELFportable-policy', 'Microsoft.NETCore.App.deps.json': b'{}', 'Microsoft.NETCore.App.runtimeconfig.json': b'{}'}.items():
                (framework / name).write_bytes(content)
        (source / 'sdk').mkdir()
        (source / 'sdk' / 'never-copy').write_bytes(b'not runtime')
        return source

    def seal(self, source, destination, deadline=None):
        return m.seal_runtime(source / 'dotnet', destination, deadline or time.monotonic() + 5, os.getuid(), os.getgid())

    def test_real_copy_selects_numeric_versions_is_immutable_and_exactly_cleans(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp).resolve()
            source = self.fixture(root)
            original_mode = stat.S_IMODE((source / 'dotnet').stat().st_mode)
            runtime = self.seal(source, root / 'sealed')
            self.assertEqual('10.0.10', runtime.record['framework_version'])
            self.assertEqual('10.0.10', runtime.record['hostfxr_version'])
            self.assertEqual(6, runtime.record['file_count'])
            self.assertLessEqual(len(json.dumps(runtime.record).encode()), 128 << 10)
            self.assertFalse((root / 'sealed' / 'sdk').exists())
            self.assertEqual(0o755, stat.S_IMODE(runtime.dotnet.stat().st_mode))
            self.assertEqual(original_mode, stat.S_IMODE((source / 'dotnet').stat().st_mode))
            for relative, row in runtime.record['files'].items():
                path = root / 'sealed' / relative
                self.assertEqual(os.getuid(), path.stat().st_uid)
                self.assertEqual(os.getgid(), path.stat().st_gid)
                self.assertEqual(int(row['mode'], 8), stat.S_IMODE(path.stat().st_mode))
            (source / 'dotnet').write_bytes(b'later source change')
            self.assertEqual(b'\x7fELFportable-marker-host', runtime.dotnet.read_bytes())
            runtime.cleanup(time.monotonic() + 5)
            self.assertFalse((root / 'sealed').exists())
            self.assertTrue((source / 'sdk' / 'never-copy').exists())

    def test_unsafe_source_shapes_versions_and_bounds_fail_closed(self):
        cases = ('symlink', 'hardlink', 'fifo', 'invalid-version', 'file-bound', 'total-bound', 'deadline', 'kind', 'elf', 'parent-mode', 'collision')
        for case in cases:
            with self.subTest(case=case), tempfile.TemporaryDirectory() as temp:
                root = Path(temp).resolve()
                source = self.fixture(root)
                selected = source / 'shared' / 'Microsoft.NETCore.App' / '10.0.10' / 'System.Private.CoreLib.dll'
                if case == 'symlink':
                    selected.unlink(); selected.symlink_to(source / 'dotnet')
                elif case == 'hardlink':
                    os.link(selected, source / 'other-link')
                elif case == 'fifo':
                    selected.unlink(); os.mkfifo(selected)
                elif case == 'invalid-version':
                    (source / 'host' / 'fxr' / '10.0.11-preview').mkdir()
                elif case == 'kind':
                    selected.with_name('unexpected.bin').write_bytes(b'unknown')
                elif case == 'elf':
                    (source / 'dotnet').write_bytes(b'NOT-ELF')
                elif case == 'parent-mode':
                    root.chmod(0o777)
                elif case == 'collision':
                    (root / 'sealed').mkdir(mode=0o700)
                    (root / 'sealed' / 'foreign').write_bytes(b'canary')
                deadline = time.monotonic() - 1 if case == 'deadline' else time.monotonic() + 5
                with mock.patch.object(m, 'FILE_LIMIT', 4 if case == 'file-bound' else m.FILE_LIMIT), mock.patch.object(m, 'TOTAL_LIMIT', 8 if case == 'total-bound' else m.TOTAL_LIMIT):
                    with self.assertRaises(m.SealError) as caught:
                        self.seal(source, root / 'sealed', deadline)
                if caught.exception.quarantine_required:
                    self.assertEqual(root / 'sealed', caught.exception.destination)
                    self.assertEqual(0o700, stat.S_IMODE((root / 'sealed').stat().st_mode))
                if case == 'deadline':
                    self.assertFalse((root / 'sealed').exists())
                if case == 'collision':
                    self.assertFalse(caught.exception.quarantine_required)
                    self.assertEqual(b'canary', (root / 'sealed' / 'foreign').read_bytes())
                root.chmod(0o700)

    def test_mutation_and_cleanup_substitution_preserve_foreign_entries(self):
        for case in ('source-growth', 'source-substitution', 'source-directory-substitution', 'cleanup-growth', 'cleanup-substitution', 'cleanup-extra', 'cleanup-deadline'):
            with self.subTest(case=case), tempfile.TemporaryDirectory() as temp:
                root = Path(temp).resolve()
                source = self.fixture(root)
                selected = source / 'dotnet'
                if case.startswith('source-'):
                    actual_read = os.read
                    changed = False

                    def changing_read(fd, count):
                        nonlocal changed
                        data = actual_read(fd, count)
                        if not changed and data.startswith(b'\x7fELFportable-marker-host'):
                            changed = True
                            if case == 'source-growth':
                                with selected.open('ab') as stream:
                                    stream.write(b'extra')
                            elif case == 'source-directory-substitution':
                                (source / 'host').rename(source / 'held-host')
                                (source / 'host').mkdir()
                            else:
                                selected.rename(source / 'held-original')
                                selected.write_bytes(b'\x7fELFreplacement')
                        return data

                    with mock.patch.object(m.os, 'read', side_effect=changing_read), self.assertRaises(m.SealError) as caught:
                        self.seal(source, root / 'sealed')
                    self.assertTrue(changed)
                    self.assertTrue(caught.exception.quarantine_required)
                    continue
                runtime = self.seal(source, root / 'sealed')
                if case == 'cleanup-growth':
                    runtime.dotnet.chmod(0o755)
                    with runtime.dotnet.open('ab') as stream:
                        stream.write(b'changed')
                elif case == 'cleanup-substitution':
                    runtime.dotnet.rename(root / 'sealed' / 'held-original')
                    runtime.dotnet.write_bytes(b'foreign')
                elif case == 'cleanup-extra':
                    (root / 'sealed' / 'foreign').write_bytes(b'canary')
                deadline = time.monotonic() - 1 if case == 'cleanup-deadline' else time.monotonic() + 5
                with self.assertRaises(m.SealError) as caught:
                    runtime.cleanup(deadline)
                self.assertTrue(caught.exception.quarantine_required)
                self.assertTrue(runtime.dotnet.exists())
                self.assertTrue((root / 'sealed' / 'shared').is_dir())

    def test_growth_between_preliminary_and_pinned_snapshots_respects_remaining_total(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp).resolve()
            source = self.fixture(root)
            framework = source / 'shared' / 'Microsoft.NETCore.App' / '10.0.10'
            selected = framework / 'libhostpolicy.so'
            original_size = selected.stat().st_size
            aggregate_cap = (source / 'dotnet').stat().st_size + (source / 'host' / 'fxr' / '10.0.10' / 'libhostfxr.so').stat().st_size
            aggregate_cap += sum(path.stat().st_size for path in framework.iterdir())
            actual_stat = os.stat
            snapshots = []

            def grow_after_preliminary_stat(path, *args, **kwargs):
                observed = actual_stat(path, *args, **kwargs)
                if path == 'libhostpolicy.so' and kwargs.get('dir_fd') is not None:
                    snapshots.append(observed.st_size)
                    if len(snapshots) == 1:
                        with selected.open('ab') as stream:
                            stream.write(b'x')
                return observed

            with mock.patch.object(m, 'TOTAL_LIMIT', aggregate_cap), mock.patch.object(m.os, 'stat', side_effect=grow_after_preliminary_stat):
                with self.assertRaises(m.SealError) as caught:
                    self.seal(source, root / 'sealed')
            self.assertEqual([original_size, original_size + 1], snapshots)
            self.assertEqual('source-file', caught.exception.category)
            self.assertTrue(caught.exception.quarantine_required)
            self.assertEqual(root / 'sealed', caught.exception.destination)
            copied_framework = root / 'sealed' / 'shared' / 'Microsoft.NETCore.App' / '10.0.10'
            self.assertTrue((copied_framework / 'System.Private.CoreLib.dll').is_file())
            self.assertFalse((copied_framework / 'libhostpolicy.so').exists())
            self.assertEqual(0o700, stat.S_IMODE((root / 'sealed').stat().st_mode))


if __name__ == '__main__':
    unittest.main()
