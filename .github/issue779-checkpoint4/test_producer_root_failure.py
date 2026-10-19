"""Data and real small-FD tests only: no collector, service or native acceptance."""
import contextlib
import io
import itertools
import os
from pathlib import Path
import stat
import tempfile
import unittest
from unittest.mock import patch

import producer_root_failure as subject


class ProducerRootFailureTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.evidence = self.root / 'evidence'
        self.evidence.mkdir(mode=0o700)
        self.output = self.root / 'diagnostics'

    def make(self, name, data=b'{}', mode=0o600):
        path = self.evidence / name
        path.write_bytes(data)
        path.chmod(mode)
        return path

    def collect(self):
        return subject.collect(self.evidence, self.output)

    def row(self, value, name):
        return next(row for row in value['records'] if row['name'] == name)

    def test_valid_neighbor_modes_ids_and_exact_two_private_outputs(self):
        source = self.make(subject.REPORT_NAMES[0], mode=0o640)
        self.make(subject.LOG_NAME, b'joined traceback\n')
        value = self.collect()
        row = self.row(value, source.name)
        self.assertEqual(('regular', 1, '0640', 2, os.geteuid(), os.getegid()),
                         tuple(row[k] for k in ('type', 'links', 'mode', 'bytes', 'uid', 'gid')))
        self.assertTrue(row['available'])
        self.assertFalse(value['namespace_authority'])
        self.assertFalse(value['native_acceptance'])
        self.assertEqual({'metadata.json', 'native-driver-slices.log'}, {p.name for p in self.output.iterdir()})
        self.assertEqual(0o700, stat.S_IMODE(self.output.stat().st_mode))
        for leaf in self.output.iterdir():
            self.assertEqual(0o600, stat.S_IMODE(leaf.stat().st_mode))
        self.assertLessEqual(sum(p.stat().st_size for p in self.output.iterdir()), subject.TOTAL_LIMIT)

    def test_oversize_sparse_report_gets_metadata_without_read(self):
        path = self.make(subject.REPORT_NAMES[0])
        with path.open('r+b') as target:
            target.truncate(9 * 1024 * 1024)
        with patch.object(subject.os, 'pread', side_effect=AssertionError('reports-must-not-be-read')):
            value = self.collect()
        self.assertEqual(9 * 1024 * 1024, self.row(value, path.name)['bytes'])
        self.assertTrue(self.row(value, path.name)['stable'])

    def test_log_64k_valid_control_is_read_once_and_marked_complete(self):
        body = b'X' * 65536
        self.make(subject.LOG_NAME, body)
        real = os.pread
        with patch.object(subject.os, 'pread', wraps=real) as calls:
            value = self.collect()
        self.assertEqual(1, calls.call_count)
        row = self.row(value, subject.LOG_NAME)
        self.assertTrue(row['complete'])
        self.assertEqual('full', row['capture'])
        self.assertEqual(body, (self.output / 'native-driver-slices.log').read_bytes())

    def test_large_log_slices_are_bounded_and_never_labeled_complete(self):
        body = b'A' * 32768 + b'not retained middle' + b'Z' * 32768
        self.make(subject.LOG_NAME, body)
        value = self.collect()
        row = self.row(value, subject.LOG_NAME)
        self.assertFalse(row['complete'])
        self.assertEqual('first-and-last-slices', row['capture'])
        self.assertEqual([{'offset': 0, 'bytes': 32768},
                          {'offset': len(body) - 32768, 'bytes': 32768}], row['segments'])
        self.assertEqual(b'A' * 32768 + b'Z' * 32768,
                         (self.output / 'native-driver-slices.log').read_bytes())

    def test_symlink_hardlink_fifo_directory_closed_without_reading_or_blocking(self):
        target = self.make('not-allowed', b'private-canary')
        names = subject.REPORT_NAMES[:4]
        (self.evidence / names[0]).symlink_to(target)
        os.link(target, self.evidence / names[1])
        os.mkfifo(self.evidence / names[2])
        (self.evidence / names[3]).mkdir()
        value = self.collect()
        for name, kind, reason in zip(names, ('symlink', 'regular', 'fifo', 'directory'),
                                      ('not-regular', 'not-single-link', 'not-regular', 'not-regular')):
            row = self.row(value, name)
            self.assertEqual((kind, reason, False), (row['type'], row['unavailable'], row['available']))

    def test_log_link_fifo_and_directory_unavailable_with_empty_output(self):
        for kind in ('symlink', 'hardlink', 'fifo', 'directory'):
            with self.subTest(kind=kind):
                path = self.evidence / subject.LOG_NAME
                target = self.make('target', b'private-canary')
                if kind == 'symlink': path.symlink_to(target)
                elif kind == 'hardlink': os.link(target, path)
                elif kind == 'fifo': os.mkfifo(path)
                else: path.mkdir()
                self.output = self.root / ('output-' + kind)
                value = self.collect()
                self.assertFalse(self.row(value, subject.LOG_NAME)['available'])
                self.assertEqual(b'', (self.output / 'native-driver-slices.log').read_bytes())
                if path.is_dir(): path.rmdir()
                else: path.unlink()

    def test_unknown_names_are_not_enumerated_or_echoed(self):
        self.make('arbitrary-private-canary', b'do not retain')
        value = self.collect()
        self.assertEqual([*subject.REPORT_NAMES, subject.LOG_NAME], [r['name'] for r in value['records']])
        self.assertNotIn(b'private-canary', (self.output / 'metadata.json').read_bytes())

    def test_real_mutation_during_read_discards_all_log_slices(self):
        path = self.make(subject.LOG_NAME, b'A' * 100)
        real = os.pread
        def change(fd, size, offset):
            data = real(fd, size, offset)
            path.write_bytes(b'B' * 101)
            return data
        with patch.object(subject.os, 'pread', side_effect=change):
            value = self.collect()
        self.assertEqual('changed', self.row(value, subject.LOG_NAME)['unavailable'])
        self.assertEqual(b'', (self.output / 'native-driver-slices.log').read_bytes())

    def test_real_named_replacement_during_read_is_unavailable(self):
        path = self.make(subject.LOG_NAME, b'A' * 100)
        real = os.pread
        def replace(fd, size, offset):
            data = real(fd, size, offset)
            path.rename(self.evidence / 'original-retained')
            path.write_bytes(b'A' * 100)
            return data
        with patch.object(subject.os, 'pread', side_effect=replace):
            value = self.collect()
        self.assertEqual('changed', self.row(value, subject.LOG_NAME)['unavailable'])

    def test_missing_or_symlink_root_never_followed(self):
        value = subject.collect(self.root / 'missing', self.output)
        self.assertTrue(all(r['unavailable'] == 'root-missing' for r in value['records']))
        link = self.root / 'root-link'; link.symlink_to(self.evidence, target_is_directory=True)
        value = subject.collect(link, self.root / 'link-output')
        self.assertTrue(all(not r['available'] for r in value['records']))

    def test_permission_error_is_closed_without_exception_or_path_echo(self):
        self.make(subject.REPORT_NAMES[0])
        real = os.open
        def permission(path, *args, **kwargs):
            if path == subject.REPORT_NAMES[0]:
                raise PermissionError('private-exception-canary')
            return real(path, *args, **kwargs)
        with patch.object(subject.os, 'open', side_effect=permission):
            value = self.collect()
        self.assertEqual('permission', self.row(value, subject.REPORT_NAMES[0])['unavailable'])
        self.assertNotIn(b'private-exception-canary', (self.output / 'metadata.json').read_bytes())

    def test_existing_output_and_input_bytes_modes_are_preserved(self):
        source = self.make(subject.REPORT_NAMES[0], b'original', 0o640)
        self.output.mkdir(mode=0o700)
        sentinel = self.output / 'sentinel'; sentinel.write_bytes(b'unchanged')
        with self.assertRaisesRegex(ValueError, '^producer-root-failure-output-unavailable$'): self.collect()
        self.assertEqual(b'unchanged', sentinel.read_bytes())
        self.assertEqual(b'original', source.read_bytes())
        self.assertEqual(0o640, stat.S_IMODE(source.stat().st_mode))

    def test_no_stdout_or_stderr_echo_of_private_log(self):
        self.make(subject.LOG_NAME, b'private-log-canary')
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            value = self.collect()
        self.assertEqual(('', ''), (out.getvalue(), err.getvalue()))
        self.assertTrue(value['original_outcome_unchanged'])

    def test_expired_observation_budget_keeps_publication_inside_original_five_seconds(self):
        clock = itertools.chain((0,), itertools.repeat(4.6))
        with patch.object(subject.time, 'monotonic', side_effect=lambda: next(clock)):
            value = self.collect()
        self.assertTrue(all(r['unavailable'] == 'root-deadline' for r in value['records']))
        self.assertEqual(b'', (self.output / 'native-driver-slices.log').read_bytes())

    def test_short_regular_log_read_discards_incomplete_bytes(self):
        self.make(subject.LOG_NAME, b'original')
        real = os.pread
        with patch.object(subject.os, 'pread', side_effect=lambda fd, size, offset: real(fd, size, offset)[:-1]):
            value = self.collect()
        self.assertEqual('changed', self.row(value, subject.LOG_NAME)['unavailable'])
        self.assertEqual(b'', (self.output / 'native-driver-slices.log').read_bytes())

    def test_partial_publication_failure_removes_only_own_leaves_and_preserves_source(self):
        source = self.make(subject.LOG_NAME, b'original-failure-log')
        real, count = os.write, 0
        def fail_metadata(fd, data):
            nonlocal count
            count += 1
            if count == 2:
                raise OSError('private-write-exception-canary')
            return real(fd, data)
        with patch.object(subject.os, 'write', side_effect=fail_metadata):
            with self.assertRaisesRegex(ValueError, '^producer-root-failure-output-unavailable$'):
                self.collect()
        self.assertEqual([], list(self.output.iterdir()))
        self.assertEqual(b'original-failure-log', source.read_bytes())

    def test_existing_output_symlink_cannot_redirect_publication_or_cleanup(self):
        target = self.root / 'preserved'; target.mkdir()
        sentinel = target / 'sentinel'; sentinel.write_bytes(b'preserve')
        self.output.symlink_to(target, target_is_directory=True)
        with self.assertRaisesRegex(ValueError, '^producer-root-failure-output-unavailable$'): self.collect()
        self.assertEqual(b'preserve', sentinel.read_bytes())
        self.assertEqual({'sentinel'}, {p.name for p in target.iterdir()})


if __name__ == '__main__':
    unittest.main()
