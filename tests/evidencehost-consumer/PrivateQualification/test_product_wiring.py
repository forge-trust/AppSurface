"""Failure-only launcher wiring controls; no product owner or admission is created.

These definitions use ordinary fixture-owned FDs and unaccepted failure observers.
The closed-receipt predicate is data-only; a matching dictionary is not authority.
This source handoff does not claim that any control has executed.
"""
import errno
import importlib.util
import os
from pathlib import Path
import socket
import tempfile
import unittest
from unittest.mock import patch


spec = importlib.util.spec_from_file_location(
    "evidencehost_product_wiring_launcher",
    Path(__file__).parents[3] / "scripts" / "evidencehost-linux-launcher.py")
launcher = importlib.util.module_from_spec(spec)
spec.loader.exec_module(launcher)


class AbortObserver:
    """Explicitly unaccepted failure observer, never a valid root owner or lease."""
    def __init__(self, *, fail=False):
        self.calls = 0
        self.fail = fail

    def abort_after_owned_exit(self):
        self.calls += 1
        if self.fail:
            raise RuntimeError("fixed-abort-failure")


def completion_fixture(root):
    """Create ordinary owned directory handles with no protected context or authority."""
    output = root / "output"
    output.mkdir()
    scratch = root / "scratch"
    scratch.mkdir()
    control = root / "control"
    control.mkdir()
    output_fd = os.open(output, os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC)
    try:
        parent_fd = os.open(root, os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC)
    except BaseException:
        os.close(output_fd)
        raise
    try:
        completion = launcher._LaunchCompletion(output, {}, output_fd, parent_fd, {}, {}, ())
    except BaseException:
        os.close(output_fd)
        os.close(parent_fd)
        raise
    return completion, output_fd, parent_fd, control, scratch


def close_completion_for_fixture(completion):
    """Always attempt cleanup while preserving a control's already asserted failure."""
    try:
        completion.close()
    except BaseException:
        pass


class ProductWiringFailureControls(unittest.TestCase):
    def assert_closed(self, *descriptors):
        for fd in descriptors:
            with self.assertRaises(OSError) as caught:
                os.fstat(fd)
            self.assertEqual(caught.exception.errno, errno.EBADF)

    def test_channel_failure_aborts_unattached_observer_and_preserves_first_error(self):
        for abort_fails in (False, True):
            with self.subTest(abort_fails=abort_fails), tempfile.TemporaryDirectory() as temp:
                completion, output_fd, parent_fd, control, scratch = completion_fixture(Path(temp))
                channel = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
                first = OSError(errno.EIO, "fixed-channel-failure")
                observer = AbortObserver(fail=abort_fails)

                class FailedListener:
                    def close(self):
                        channel.close()
                        raise first

                try:
                    with patch.object(launcher, "_delete_run_accounts") as delete:
                        with self.assertRaises(OSError) as caught:
                            launcher._finish_launch_transfer(
                                completion, ["fixture-worker"], ["fixture-group"],
                                FailedListener(), [], None, -1, control, scratch,
                                product_coverage_owner=observer, execution_map={})
                        self.assertIs(caught.exception, first)
                        delete.assert_not_called()
                    self.assertEqual(observer.calls, 1)
                    self.assertEqual(channel.fileno(), -1)
                    self.assertIsNone(completion._run_accounts)
                    self.assertIsNone(completion._product_coverage_owner)
                    self.assert_closed(output_fd, parent_fd)
                finally:
                    channel.close()
                    close_completion_for_fixture(completion)

    def test_unaccepted_observer_cannot_transfer_accounts_and_is_aborted(self):
        with tempfile.TemporaryDirectory() as temp:
            completion, output_fd, parent_fd, control, scratch = completion_fixture(Path(temp))
            observer = AbortObserver()
            try:
                with patch.object(launcher, "_delete_run_accounts") as delete:
                    with self.assertRaises(launcher.LauncherError) as caught:
                        launcher._finish_launch_transfer(
                            completion, ["fixture-worker"], ["fixture-group"],
                            None, [], None, -1, control, scratch,
                            product_coverage_owner=observer, execution_map={})
                    self.assertEqual(str(caught.exception), "product-coverage-owner-invalid")
                    delete.assert_not_called()
                self.assertEqual(observer.calls, 1)
                self.assertIsNone(completion._run_accounts)
                self.assertIsNone(completion._product_coverage_owner)
                self.assert_closed(output_fd, parent_fd)
            finally:
                close_completion_for_fixture(completion)

    def test_actual_fd_close_error_closes_other_fd_and_retains_accounts(self):
        with tempfile.TemporaryDirectory() as temp:
            completion, output_fd, parent_fd, _, _ = completion_fixture(Path(temp))
            completion._retain_run_accounts(["fixture-worker"], ["fixture-group"])
            # Actual EBADF on the first owned FD; no syscall replacement or fake owner.
            os.close(output_fd)
            try:
                with patch.object(launcher, "_delete_run_accounts") as delete:
                    with self.assertRaises(OSError) as first:
                        completion.close()
                    self.assertEqual(first.exception.errno, errno.EBADF)
                    self.assert_closed(parent_fd)
                    with self.assertRaises(OSError) as again:
                        completion.close()
                    self.assertIs(again.exception, first.exception)
                    delete.assert_not_called()
                self.assertEqual(completion._run_accounts,
                                 (("fixture-worker",), ("fixture-group",)))
            finally:
                close_completion_for_fixture(completion)

    def test_closed_receipt_single_bad_fields_reject_beside_valid_data_neighbor(self):
        valid = {"schema": "issue779-private-product-coverage-close-v1",
                 "account_cleanup_allowed": True, "cleanup_complete": True,
                 "consumer_exit_confirmed": True, "workspace_quarantined": False,
                 "mount_retained": False, "failure_category": None}
        negatives = {"schema": (None, "other"),
                     "account_cleanup_allowed": (False, 1, None),
                     "cleanup_complete": (False, 1, None),
                     "consumer_exit_confirmed": (False, 1, None),
                     "workspace_quarantined": (True, 0, None),
                     "mount_retained": (True, 0, None),
                     "failure_category": ("fixed-failure", False)}
        with patch.object(launcher, "_delete_run_accounts") as delete:
            for field, values in negatives.items():
                for value in values:
                    with self.subTest(field=field, value=value):
                        self.assertTrue(launcher._product_close_receipt_allows_accounts(dict(valid)))
                        wrong = dict(valid)
                        wrong[field] = value
                        self.assertFalse(launcher._product_close_receipt_allows_accounts(wrong))
                with self.subTest(missing=field):
                    wrong = dict(valid)
                    del wrong[field]
                    self.assertFalse(launcher._product_close_receipt_allows_accounts(wrong))
            for wrong in (None, [], True):
                self.assertFalse(launcher._product_close_receipt_allows_accounts(wrong))
            delete.assert_not_called()


if __name__ == "__main__":
    unittest.main()
