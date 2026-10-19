"""Actual failure-block orchestration and ordinary files; no native execution.

The extracted statements are the live execute_case try/except/finally block,
without its root prerequisites or success/acceptance path. Stub command outcomes
model failures only. Reads and archive saves use the real bounded file helpers.
"""

import ast
import copy
import importlib.util
import os
from pathlib import Path
import tempfile
import time
import types
import unittest
from unittest.mock import patch


ROOT = Path(__file__).resolve().parent


def load_bridge(family):
    path = ROOT / "procedures" / family / "bridge.py"
    spec = importlib.util.spec_from_file_location("failed_retention_" + family, path)
    bridge = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(bridge)
    return bridge, path


def failure_block(bridge, path, namespace):
    """Compile the exact live failure block, not a copied implementation."""
    tree = ast.parse(path.read_text())
    function = next(node for node in tree.body
                    if isinstance(node, ast.FunctionDef) and node.name == "execute_case")
    start = next(index for index, node in enumerate(function.body)
                 if isinstance(node, ast.Assign)
                 and ast.unparse(node.targets[0]) == "(fixture_error, archive)")
    statements = function.body[start:start + 2]
    if not isinstance(statements[1], ast.Try) or not statements[1].finalbody:
        raise AssertionError("execute_case failure block changed")
    wrapper = ast.parse("def invoke():\n    pass\n")
    wrapper.body[0].body = copy.deepcopy(statements)
    ast.fix_missing_locations(wrapper)
    scope = dict(bridge.__dict__)
    scope.update(namespace)
    exec(compile(wrapper, str(path), "exec"), scope)
    return scope["invoke"]


class FailedFixtureRetentionTests(unittest.TestCase):
    def exercise(self, family, *, save_failure=None, nonzero=False):
        bridge, path = load_bridge(family)
        with tempfile.TemporaryDirectory(prefix="c1-failed-retention-") as temporary:
            root = Path(temporary).resolve()
            output, control, reviewed = (root / name for name in ("output", "control", "reviewed"))
            for directory in (output, control, reviewed):
                directory.mkdir(mode=0o700)
                directory.chmod(0o700)
            raw = b"ordinary retained bytes\x00\xff\n"
            (control / "native.tar").write_bytes(raw)
            (reviewed / "dispatch.sh").write_bytes(b"detached command data")
            (reviewed / "retain-native.sh").write_bytes(b"detached retainer data")
            target = output / "native-private.tar"
            sentinel = b"existing evidence sentinel"
            if save_failure == "occupied":
                target.write_bytes(sentinel)
            original = RuntimeError("original fixture failure")
            now = time.monotonic()
            deadline = now + 120
            calls, saves, write_fds = [], [], []
            primary = []

            def run(argv, end, **kwargs):
                calls.append((argv, end, kwargs))
                if len(calls) == 1:
                    if nonzero:
                        return 1, b""
                    raise original
                return 0, b"NATIVE_RETENTION_COMPLETED:DATA_ONLY:FILES=1\n"

            def checked_need(ok, category):
                try:
                    return bridge.need(ok, category)
                except ValueError as error:
                    if category == "actual-fixture-exit":
                        primary.append(error)
                    raise

            def failed_write(fd, data):
                write_fds.append(fd)
                raise OSError("ordinary file write failure")

            def save(destination, data, *, deadline):
                saves.append((destination, data, deadline))
                if save_failure == "expired":
                    with patch.object(bridge.time, "monotonic", return_value=deadline):
                        return bridge.save_archive(destination, data, deadline=deadline)
                if save_failure == "io":
                    with patch.object(bridge.os, "write", side_effect=failed_write):
                        return bridge.save_archive(destination, data, deadline=deadline)
                return bridge.save_archive(destination, data, deadline=deadline)

            runner = types.SimpleNamespace(run=run)
            native = types.SimpleNamespace(ROOT_PREFIX=[], root_deadline=repr)
            invoke = failure_block(bridge, path, {
                "context": {"generation": "a" * 32}, "case": family,
                "output": output, "control": control, "reviewed": reviewed,
                "fixture": ["--execute", "--reviewed-script-sha256", "b" * 64],
                "runner": runner, "native": native, "deadline": deadline,
                "time": types.SimpleNamespace(monotonic=lambda: now),
                "save_archive": save, "need": checked_need,
            })
            with self.assertRaises(ValueError if nonzero else RuntimeError) as caught:
                invoke()
            self.assertIs(caught.exception, primary[0] if nonzero else original)
            self.assertEqual(2, len(calls))
            self.assertEqual(1, len(saves))
            self.assertEqual((target, raw, now + 30), saves[0])
            self.assertEqual(saves[0][2], calls[1][1])
            self.assertEqual([target.name] if target.exists() else [],
                             sorted(node.name for node in output.iterdir()))
            if save_failure is None:
                self.assertEqual(raw, target.read_bytes())
                self.assertEqual(0o600, target.stat().st_mode & 0o777)
            elif save_failure == "occupied":
                self.assertEqual(sentinel, target.read_bytes())
            elif save_failure == "expired":
                self.assertFalse(target.exists())
            else:
                self.assertEqual(b"", target.read_bytes())
                self.assertEqual(1, len(write_fds))
                with self.assertRaises(OSError):
                    os.fstat(write_fds[0])

    def test_original_exception_is_rethrown_after_unchanged_bytes_are_saved(self):
        for family in ("slot", "n11"):
            with self.subTest(family=family):
                self.exercise(family)

    def test_nonzero_fixture_exit_stays_failed_after_archive_retention(self):
        for family in ("slot", "n11"):
            with self.subTest(family=family):
                self.exercise(family, nonzero=True)

    def test_expired_save_preserves_original_exception_and_creates_no_archive(self):
        for family in ("slot", "n11"):
            with self.subTest(family=family):
                self.exercise(family, save_failure="expired")

    def test_occupied_archive_preserves_original_exception_and_sentinel(self):
        for family in ("slot", "n11"):
            with self.subTest(family=family):
                self.exercise(family, save_failure="occupied")

    def test_write_failure_preserves_original_exception_and_closes_the_opened_fd(self):
        for family in ("slot", "n11"):
            with self.subTest(family=family):
                self.exercise(family, save_failure="io")


if __name__ == "__main__":
    unittest.main()
