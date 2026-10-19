"""Actual sparse-file artifact boundaries only; no native execution or coverage claim."""
from pathlib import Path
import importlib.util
import os
import stat
import sys
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('producer_retention', Path(__file__).resolve().parents[1] / 'issue779-checkpoint2/check-retention.py')
subject = importlib.util.module_from_spec(spec)
spec.loader.exec_module(subject)
sys.path.insert(0, str(Path(__file__).resolve().parent))
import producer_roots


class ProducerReportRetentionTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)

    def sparse(self, name, length):
        path = self.root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        with path.open('xb') as output:
            output.truncate(length)
        return path

    def test_original_mode_still_rejects_either_report_above_eight_mib(self):
        for name in subject.ROOT_REPORTS:
            with self.subTest(name=name):
                path = self.sparse(name, 8 * subject.MIB)
                subject.check(self.root)
                with path.open('r+b') as output: output.truncate(8 * subject.MIB + 1)
                with self.assertRaises(AssertionError): subject.check(self.root)
                path.unlink()

    def test_explicit_mode_accepts_only_fixed_root_reports_to_same_total_bound(self):
        for name in subject.ROOT_REPORTS:
            with self.subTest(name=name):
                path = self.sparse(name, 32 * subject.MIB)
                subject.check(self.root, producer_roots=True)
                producer_roots.retention(self.root)
                self.assertEqual(0o600, stat.S_IMODE(path.stat().st_mode))
                self.assertEqual(0o700, stat.S_IMODE(self.root.stat().st_mode))
                with path.open('r+b') as output: output.truncate(32 * subject.MIB + 1)
                with self.assertRaises(AssertionError): subject.check(self.root, producer_roots=True)
                with self.assertRaises(ValueError): producer_roots.retention(self.root)
                path.unlink()

    def test_other_and_nested_same_named_leaves_keep_eight_mib_limit(self):
        for name in ('native-driver.log', 'native/report.json', 'native/native-producer-roots.json'):
            with self.subTest(name=name):
                path = self.sparse(name, 8 * subject.MIB)
                subject.check(self.root, producer_roots=True)
                with path.open('r+b') as output: output.truncate(8 * subject.MIB + 1)
                with self.assertRaises(AssertionError): subject.check(self.root, producer_roots=True)
                with self.assertRaises(ValueError): producer_roots.retention(self.root)
                path.unlink()

    def test_report_allowance_does_not_increase_total_or_file_count(self):
        path = self.sparse('native-producer-roots.json', 32 * subject.MIB)
        extra = self.sparse('source.json', 1)
        with self.assertRaises(AssertionError): subject.check(self.root, producer_roots=True)
        path.unlink(); extra.unlink()
        for index in range(128): self.sparse(str(index), 0)
        subject.check(self.root, producer_roots=True)
        self.sparse('one-too-many', 0)
        with self.assertRaises(AssertionError): subject.check(self.root, producer_roots=True)

    def test_report_name_does_not_admit_links_fifo_or_symlink_root(self):
        path = self.sparse('native-producer-roots.json', 1)
        link = self.root / 'other'; os.link(path, link)
        with self.assertRaises(AssertionError): subject.check(self.root, producer_roots=True)
        link.unlink(); path.unlink(); path.symlink_to('missing')
        with self.assertRaises(AssertionError): subject.check(self.root, producer_roots=True)
        path.unlink(); os.mkfifo(path)
        with self.assertRaises(AssertionError): subject.check(self.root, producer_roots=True)
        path.unlink(); path.symlink_to(self.root, target_is_directory=True)
        with self.assertRaises(AssertionError): subject.check(path, producer_roots=True)


if __name__ == '__main__':
    unittest.main()
