"""N11's strict live-quarantine and post-root-custody capture contracts."""
import copy
import hashlib
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest import mock

ROOT = Path(__file__).resolve().parent
N11 = ROOT / "procedures/n11"
sys.path.insert(0, str(N11))
sys.path.insert(0, str(ROOT))

import stall_capture
import test_n11_settled_callback as settled_fixture


class N11RootCustodyTests(unittest.TestCase):
    def settled_inputs(self):
        request = settled_fixture.request_bytes()
        descriptor = settled_fixture.descriptor_bytes(request)
        record = settled_fixture.record()
        record["account_ids"] = {
            "worker_uid": settled_fixture.UID,
            "worker_gid": settled_fixture.GID,
            "subject_uid": settled_fixture.UID + 1,
            "subject_gid": settled_fixture.GID + 1,
            "results_gid": settled_fixture.GID + 2,
        }
        record["ready"]["descriptor_sha256"] = hashlib.sha256(descriptor).hexdigest()
        return record, descriptor, request

    def parse(self, record, descriptor, request):
        return stall_capture.parse_original(
            settled_fixture.framed(record), descriptor, request=request,
            generation=settled_fixture.GENERATION, source=settled_fixture.SOURCE,
            base=settled_fixture.BASE, entry_sha=settled_fixture.ENTRY_SHA,
            policy_sha=settled_fixture.POLICY_SHA, runtime_relative_host="dotnet",
            workflow="N11-test-workflow")

    def test_settled_capture_requires_exact_account_ids_and_descriptor_binding(self):
        record, descriptor, request = self.settled_inputs()
        parsed, consistency, _ = self.parse(record, descriptor, request)
        self.assertEqual(record["account_ids"]["worker_uid"], parsed["worker_uid"])
        self.assertEqual(record["account_ids"]["worker_gid"], parsed["worker_gid"])
        self.assertEqual(record["account_ids"]["subject_uid"], parsed["subject_uid"])
        self.assertEqual(record["account_ids"]["subject_gid"], parsed["subject_gid"])
        self.assertEqual("issue779-n11-settled-callback-failure-data-v1", consistency["schema"])

        cases = []
        missing = copy.deepcopy(record)
        del missing["account_ids"]["results_gid"]
        cases.append(missing)
        extra = copy.deepcopy(record)
        extra["account_ids"]["unknown"] = 7
        cases.append(extra)
        for key in ("worker_uid", "worker_gid", "subject_uid", "subject_gid", "results_gid"):
            bad = copy.deepcopy(record)
            bad["account_ids"][key] = True
            cases.append(bad)
            bad = copy.deepcopy(record)
            bad["account_ids"][key] = (1 << 32) - 1
            cases.append(bad)
        for key in ("worker_uid", "worker_gid", "subject_uid", "subject_gid"):
            bad = copy.deepcopy(record)
            bad["account_ids"][key] += 10
            cases.append(bad)
        for key in ("subject_gid", "results_gid"):
            bad = copy.deepcopy(record)
            bad["account_ids"][key] = bad["account_ids"]["worker_gid"]
            cases.append(bad)
        for changed in cases:
            with self.subTest(account_ids=changed["account_ids"]), self.assertRaisesRegex(
                    ValueError, "^stall-capture-rejected$"):
                self.parse(changed, descriptor, request)

    def test_only_exact_settled_callback_frame_selects_post_custody_layout(self):
        record, _, _ = self.settled_inputs()
        _, failure = stall_capture.parse_root_stall_frame(settled_fixture.framed(record))
        self.assertTrue(stall_capture.callback_failed_settled_branch(record, failure))

        legacy = settled_fixture.record(settled=False, stderr=b"FIXTURE_N11_DISPOSER_ARMED\n"
                                        b"FIXTURE_N11_INPUT_FACTORY_ENTERED\n")
        self.assertFalse(stall_capture.callback_failed_settled_branch(legacy, failure))

        missing_ids = copy.deepcopy(record)
        del missing_ids["account_ids"]
        with self.assertRaisesRegex(ValueError, "^stall-capture-rejected$"):
            stall_capture.callback_failed_settled_branch(missing_ids, failure)

        wrong_diagnostic = copy.deepcopy(failure)
        wrong_diagnostic["control_failure"]["stage"] = "HandlerJoin"
        with self.assertRaisesRegex(ValueError, "^stall-capture-rejected$"):
            stall_capture.callback_failed_settled_branch(record, wrong_diagnostic)

    def test_absence_requires_both_nss_lookup_forms(self):
        seen = []

        def absent(value):
            seen.append(value)
            raise KeyError(value)

        stall_capture.require_absent_by_name_and_id(absent, absent, "evw-example", 1201)
        self.assertEqual(["evw-example", 1201], seen)

        for first_present in (True, False):
            seen.clear()

            def by_name(value):
                seen.append(("name", value))
                if first_present:
                    return object()
                raise KeyError(value)

            def by_id(value):
                seen.append(("id", value))
                if not first_present:
                    return object()
                raise KeyError(value)

            with self.subTest(first_present=first_present), self.assertRaisesRegex(
                    ValueError, "^stall-capture-rejected$"):
                stall_capture.require_absent_by_name_and_id(by_name, by_id, "evw-example", 1201)
            self.assertEqual("name", seen[0][0])
            if first_present:
                self.assertEqual(1, len(seen))
            else:
                self.assertEqual([("name", "evw-example"), ("id", 1201)], seen)

    def test_native_capture_checks_real_directory_and_retained_file_identity(self):
        capture = stall_capture.NativeCapture(1)
        capture.check = lambda: None
        with tempfile.TemporaryDirectory() as temporary:
            os.mkdir(os.path.join(temporary, "workspace"), 0o700)
            parent = os.open(temporary, os.O_RDONLY | os.O_DIRECTORY)
            try:
                file_path = os.path.join(temporary, "workspace", "descriptor")
                with open(file_path, "xb") as stream:
                    stream.write(b"fixed descriptor bytes")
                os.chmod(file_path, 0o400)
                workspace, workspace_stat = capture.directory(
                    parent, "workspace", os.geteuid(), os.getegid(), 0o700)
                raw, descriptor_fd, descriptor_stat = capture.read_retained(
                    workspace, "descriptor", os.geteuid(), os.getegid(), 0o400, 65536)
                self.assertEqual(b"fixed descriptor bytes", raw)
                capture.stable_file_bytes(descriptor_fd, raw)
                capture.stable_named(workspace, "descriptor", descriptor_fd, descriptor_stat)
                capture.stable_named(parent, "workspace", workspace, workspace_stat)
                self.assertEqual(["descriptor"], capture.names(workspace, ("descriptor",)))
                with self.assertRaisesRegex(ValueError, "^stall-capture-rejected$"):
                    capture.names(workspace, ())
            finally:
                capture.close()
                os.close(parent)

    def test_native_capture_rejects_directory_owner_and_file_mode_mismatches(self):
        capture = stall_capture.NativeCapture(1)
        capture.check = lambda: None
        with tempfile.TemporaryDirectory() as temporary:
            os.mkdir(os.path.join(temporary, "workspace"), 0o710)
            parent = os.open(temporary, os.O_RDONLY | os.O_DIRECTORY)
            try:
                with self.assertRaisesRegex(ValueError, "^stall-capture-rejected$"):
                    capture.directory(parent, "workspace", os.geteuid() + 1, os.getegid(), 0o700)
                os.chmod(os.path.join(temporary, "workspace"), 0o700)
                workspace, _ = capture.directory(
                    parent, "workspace", os.geteuid(), os.getegid(), 0o700)
                self.assertTrue(workspace >= 0)
                path = os.path.join(temporary, "workspace", "descriptor")
                with open(path, "xb") as stream:
                    stream.write(b"descriptor")
                os.chmod(path, 0o600)
                with self.assertRaisesRegex(ValueError, "^stall-capture-rejected$"):
                    capture.read(workspace, "descriptor", os.geteuid(), os.getegid(), 0o400, 65536)
            finally:
                capture.close()
                os.close(parent)


if __name__ == "__main__":
    unittest.main()
