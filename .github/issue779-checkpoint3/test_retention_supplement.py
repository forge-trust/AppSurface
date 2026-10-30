"""Actual private temporary-file retention boundaries; no native execution or receipt fabrication."""
from pathlib import Path
import contextlib
import io
import runpy
import sys
import tempfile
import unittest
from unittest import mock


class SupplementRetentionTests(unittest.TestCase):
    def check(self, root):
        with mock.patch.object(sys, 'argv', ['check-retention.py', str(root)]):
            with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
                runpy.run_path(str(Path(__file__).with_name('check-retention.py')), run_name='__main__')

    def test_seven_supplement_files_fit_but_a_further_leaf_rejects(self):
        with tempfile.TemporaryDirectory() as name:
            root = Path(name)
            for index in range(128):
                (root / str(index)).write_bytes(b'')
            self.check(root)
            (root / 'root-supplement.trx').write_bytes(b'bounded fixture data')
            (root / 'root-supplement-driver.log').write_bytes(b'')
            (root / 'retention-supplement-controls.log').write_bytes(b'')
            (root / 'application-bundle.trx').write_bytes(b'bounded fixture data')
            (root / 'application-bundle-driver.log').write_bytes(b'')
            (root / 'application-bundle-controls.log').write_bytes(b'')
            # Revision 104 retains the distinct four-case peer TRX in this seventh fixed slot.
            (root / 'root-file-tests').mkdir()
            (root / 'root-file-tests/peer-credentials.trx').write_bytes(b'bounded fixture data')
            self.check(root)
            self.assertEqual(0o700, root.stat().st_mode & 0o777)
            leaves = [path for path in root.rglob('*') if path.is_file()]
            self.assertEqual(135, len(leaves))
            self.assertTrue(all(path.stat().st_mode & 0o777 == 0o600 for path in leaves))
            (root / 'unexpected').write_bytes(b'')
            with self.assertRaises(SystemExit):
                self.check(root)

    def test_new_slot_does_not_bypass_existing_leaf_size_or_link_guards(self):
        with tempfile.TemporaryDirectory() as name:
            root = Path(name)
            leaf = root / 'root-supplement.trx'
            with leaf.open('wb') as stream:
                stream.truncate(8 * 1024 * 1024 + 1)
            with self.assertRaises(SystemExit):
                self.check(root)
            leaf.unlink()
            (root / 'outside').write_bytes(b'unchanged')
            leaf.symlink_to(root / 'outside')
            with self.assertRaises(SystemExit):
                self.check(root)
            self.assertEqual(b'unchanged', (root / 'outside').read_bytes())


if __name__ == '__main__':
    unittest.main()
