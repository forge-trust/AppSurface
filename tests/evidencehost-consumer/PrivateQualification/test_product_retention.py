"""Actual private archive data controls; no qualification or root ownership claim."""
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import sys
import tarfile
import tempfile
import time
import unittest
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location('private_qualification_retention', Path(__file__).with_name('run-qualification.py'))
M = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(M)


class ProductRetentionControls(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.workspace, self.output = self.root/'workspace', self.root/'output'
        self.workspace.mkdir(mode=0o700)
        self.output.mkdir(mode=0o700)

    def file(self, name, data):
        path = self.workspace/name
        path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
        path.write_bytes(data)
        os.chmod(path, 0o600)
        return path

    def test_actual_closed_product_archive_preserves_bytes_and_private_modes(self):
        files = {'product-binary-cli.json': b'{}', 'product-coverage-cli/receipt.json': b'{}',
                 'product-coverage-cli/coverage.cobertura.xml': b'<coverage/>',
                 'product-coverage-cli/coverage.json': b'{}',
                 'product-coverage-cli/taskhost-stdout.log': b'private table\n',
                 'product-coverage-cli/taskhost-stderr.log': b'private warning canary\n'}
        for name, data in files.items(): self.file(name, data)
        observed = M.retain_diagnostics(self.workspace, self.output, expected_owner_uid=os.getuid())
        path = self.output/'private-diagnostics.tar'
        self.assertEqual(hashlib.sha256(path.read_bytes()).hexdigest(), observed)
        self.assertEqual(0o600, path.stat().st_mode & 0o777)
        with tarfile.open(path) as archive:
            self.assertEqual(sorted([*files, 'index.json']), archive.getnames())
            for name, data in files.items():
                member = archive.getmember(name)
                self.assertEqual((0, 0, 0, 0o600), (member.uid, member.gid, member.mtime, member.mode))
                self.assertEqual(data, archive.extractfile(member).read())

    def test_report_oversize_rejected_before_read_and_does_not_copy(self):
        path = self.file('product-coverage-cli/coverage.json', b'')
        with path.open('r+b') as stream: stream.truncate((4 << 20)+1)
        with patch.object(M.os, 'pread', side_effect=AssertionError('oversize read')):
            M.retain_diagnostics(self.workspace, self.output, expected_owner_uid=os.getuid())
        with tarfile.open(self.output/'private-diagnostics.tar') as archive:
            self.assertEqual(['index.json'], archive.getnames())
            rows = json.loads(archive.extractfile('index.json').read())
        self.assertEqual('oversize', rows[0]['state'])

    def test_original_execution_error_survives_cleanup_error_and_real_fd_closes(self):
        first = ValueError('fixed-original')
        second = OSError('fixed-cleanup')
        calls = []
        class UnacceptedCleanupObserver:
            def close(self):
                calls.append('close')
                raise second
        fd = os.open(self.workspace, os.O_RDONLY | os.O_DIRECTORY)
        def operation():
            try: raise first
            finally: M.close_entry_resources(fd, UnacceptedCleanupObserver(), sys.exc_info()[1])
        with self.assertRaises(ValueError) as caught: operation()
        self.assertIs(first, caught.exception)
        with self.assertRaises(OSError): os.fstat(fd)
        self.assertEqual(['close'], calls)
        fd = os.open(self.workspace, os.O_RDONLY | os.O_DIRECTORY)
        with self.assertRaises(OSError) as caught: M.close_entry_resources(fd, UnacceptedCleanupObserver(), None)
        self.assertIs(second, caught.exception)
        with self.assertRaises(OSError): os.fstat(fd)

    def test_final_archive_hash_crossing_deadline_cannot_return_success(self):
        self.file('product-coverage-cli/receipt.json', b'{}')
        original = M.sha
        expired = False
        now = time.monotonic()
        def hash_and_expire(data):
            nonlocal expired
            result = original(data)
            if len(data) >= 10240: expired = True
            return result
        with patch.object(M, 'sha', side_effect=hash_and_expire), patch.object(M.time, 'monotonic', side_effect=lambda: now+6 if expired else now):
            with self.assertRaises(M.PreparationFailure):
                M.retain_diagnostics(self.workspace, self.output, expected_owner_uid=os.getuid())
        self.assertTrue((self.output/'private-diagnostics.tar').exists())


if __name__ == '__main__':
    unittest.main()
