"""Portable descriptor-backed tests for native-preparation/staging.py."""

import hashlib
import os
from pathlib import Path
import stat
import tempfile
import unittest
from unittest.mock import patch

from staging import copy_tree


class CopyTreeTests(unittest.TestCase):
    def setUp(self):
        self._temporary = tempfile.TemporaryDirectory()
        self.root = Path(self._temporary.name).resolve()
        self.source = self.root / "source"
        self.source.mkdir(mode=0o700)
        self.destination = self.root / "fresh-copy"

    def tearDown(self):
        self._temporary.cleanup()

    def copy(self):
        return copy_tree(self.source, self.destination, owner_root=False)

    def test_actual_fd_copy_modes_hash_empty_omission_and_source_invariance(self):
        nested = self.source / "nested"
        nested.mkdir()
        data = b"source bytes remain unchanged\n"
        normal = nested / "data.txt"
        normal.write_bytes(data)
        executable = self.source / "run.sh"
        executable.write_bytes(b"#!/bin/sh\nexit 0\n")
        executable.chmod(0o751)
        (self.source / "empty.marker").touch()

        before = {str(p.relative_to(self.source)): p.read_bytes() for p in (normal, executable, self.source / "empty.marker")}
        inventory = self.copy()

        self.assertEqual(set(inventory), {"nested/data.txt", "run.sh"})
        self.assertEqual(inventory["nested/data.txt"], {
            "sha256": hashlib.sha256(data).hexdigest(), "bytes": len(data), "mode": "0644"})
        self.assertEqual(inventory["run.sh"]["mode"], "0755")
        self.assertEqual((self.destination / "nested/data.txt").read_bytes(), data)
        self.assertEqual((self.destination / "run.sh").read_bytes(), executable.read_bytes())
        self.assertFalse((self.destination / "empty.marker").exists())
        self.assertEqual(stat.S_IMODE((self.destination / "nested").stat().st_mode), 0o755)
        after = {str(p.relative_to(self.source)): p.read_bytes() for p in (normal, executable, self.source / "empty.marker")}
        self.assertEqual(before, after)

    def test_rejects_symlink_without_creating_destination(self):
        (self.source / "link").symlink_to("/etc/passwd")
        with self.assertRaises(RuntimeError):
            self.copy()
        self.assertFalse(self.destination.exists())

    def test_rejects_hardlink_without_creating_destination(self):
        file = self.source / "one"
        file.write_bytes(b"x")
        os.link(file, self.source / "two")
        with self.assertRaises(RuntimeError):
            self.copy()
        self.assertFalse(self.destination.exists())

    @unittest.skipUnless(hasattr(os, "mkfifo"), "FIFO creation is unavailable")
    def test_rejects_fifo_without_blocking_or_creating_destination(self):
        os.mkfifo(self.source / "pipe")
        with self.assertRaises(RuntimeError):
            self.copy()
        self.assertFalse(self.destination.exists())

    def test_rejects_node_bound_before_destination_creation(self):
        (self.source / "one").write_bytes(b"a")
        (self.source / "two").write_bytes(b"b")
        with patch("staging.MAX_NODES", 1):
            with self.assertRaises(RuntimeError):
                self.copy()
        self.assertFalse(self.destination.exists())

    def test_rejects_file_size_bound_before_destination_creation(self):
        (self.source / "large").write_bytes(b"1234")
        with patch("staging.MAX_FILE_BYTES", 3):
            with self.assertRaises(RuntimeError):
                self.copy()
        self.assertFalse(self.destination.exists())

    def test_rejects_symlink_in_source_ancestor(self):
        alias = self.root / "alias"
        alias.symlink_to(self.source, target_is_directory=True)
        with self.assertRaises((OSError, RuntimeError)):
            copy_tree(alias, self.destination, owner_root=False)
        self.assertFalse(self.destination.exists())

    def test_rejects_source_name_substitution_during_streaming_read(self):
        source_file = self.source / "payload"
        source_file.write_bytes(b"original payload")
        real_read = os.read
        substituted = False

        def read_with_substitution(fd, size):
            nonlocal substituted
            if not substituted:
                substituted = True
                source_file.rename(self.source / "displaced")
                source_file.write_bytes(b"replacement payload")
            return real_read(fd, size)

        with patch("staging.os.read", side_effect=read_with_substitution):
            with self.assertRaises(RuntimeError):
                self.copy()
        self.assertTrue(substituted)

    def test_rejects_existing_destination_instead_of_adopting_it(self):
        self.destination.mkdir(mode=0o700)
        with self.assertRaises(FileExistsError):
            self.copy()


if __name__ == "__main__":
    unittest.main()
