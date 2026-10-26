"""Real ordinary-file and marker controls only; no protected owner or native credit."""
import contextlib
import importlib.util
import json
import os
from pathlib import Path
import tempfile
import time
import unittest
from unittest.mock import patch


spec = importlib.util.spec_from_file_location("lifecycle_stall_data", Path(__file__).with_name("lifecycle_stalls.py"))
subject = importlib.util.module_from_spec(spec)
spec.loader.exec_module(subject)


def marker(case="configure-sync-stall"):
    return {"schema": "evidence-worker-lifecycle-stall-v1", "case": case,
            "phase": "entered", "native_acceptance": False}


@contextlib.contextmanager
def diagnostic_tree():
    with tempfile.TemporaryDirectory() as name:
        root = Path(name)
        root.chmod(0o700)
        values = {"stdout.prefix": b"", "stderr.prefix": json.dumps(marker()).encode() + b"\n",
                  "state.json": b'{"schema":"evidence-worker-failure-output-v1"}'}
        for leaf, data in values.items():
            (root / leaf).write_bytes(data)
            (root / leaf).chmod(0o600)
        fd = os.open(root, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        try:
            yield root, fd, values
        finally:
            os.close(fd)


class RetainedWorkerDiagnosticControls(unittest.TestCase):
    def read(self, fd, **changes):
        args = dict(expected_uid=os.getuid(), expected_gid=os.getgid(), deadline=time.monotonic() + 5)
        args.update(changes)
        return subject.read_prefix_directory(fd, **args)

    def test_exact_private_tree_preserves_binary_bytes_and_empty_stdout(self):
        with diagnostic_tree() as (root, fd, values):
            values["stderr.prefix"] += b"\xff\x00private-canary\n"
            (root / "stderr.prefix").write_bytes(values["stderr.prefix"])
            self.assertEqual(values, self.read(fd))
            self.assertEqual(marker(), subject.marker_from_prefix(values["stderr.prefix"], "configure-sync-stall"))
            self.assertEqual(values, {leaf: (root / leaf).read_bytes() for leaf in values})

    def test_exact_prefix_limit_is_read_but_the_next_byte_rejects(self):
        with diagnostic_tree() as (root, fd, _):
            leaf = root / "stderr.prefix"
            leaf.write_bytes(b"a" * subject.PREFIX_LIMIT)
            self.assertEqual(subject.PREFIX_LIMIT, len(self.read(fd)["stderr.prefix"]))
            with leaf.open("ab") as writer:
                writer.write(b"b")
            with self.assertRaisesRegex(RuntimeError, "^lifecycle-diagnostic-leaf$"):
                self.read(fd)
            self.assertEqual(subject.PREFIX_LIMIT + 1, leaf.stat().st_size)

    def test_missing_extra_or_public_nodes_cannot_be_adopted(self):
        for fault in ("missing", "extra", "directory-mode", "file-mode", "uid", "gid", "state"):
            with self.subTest(fault=fault), diagnostic_tree() as (root, fd, _):
                self.read(fd)
                args = {}
                if fault == "missing":
                    (root / "stdout.prefix").unlink()
                elif fault == "extra":
                    (root / "foreign").write_bytes(b"extra-private-canary")
                elif fault == "directory-mode":
                    root.chmod(0o755)
                elif fault == "file-mode":
                    (root / "stderr.prefix").chmod(0o644)
                elif fault == "uid":
                    args["expected_uid"] = os.getuid() + 1
                elif fault == "gid":
                    args["expected_gid"] = os.getgid() + 1
                else:
                    (root / "state.json").write_bytes(b'{"schema":"private-canary"}')
                with self.assertRaises(RuntimeError) as error:
                    self.read(fd, **args)
                self.assertNotIn("private-canary", str(error.exception))

    def test_symlink_hardlink_and_fifo_reject_without_changing_outside_bytes(self):
        for kind in ("symlink", "hardlink", "fifo"):
            with self.subTest(kind=kind), diagnostic_tree() as (root, fd, _), tempfile.TemporaryDirectory() as outside:
                sentinel = Path(outside) / "sentinel"
                sentinel.write_bytes(b"outside-private-canary")
                sentinel.chmod(0o600)
                leaf = root / "stderr.prefix"
                leaf.unlink()
                if kind == "symlink":
                    leaf.symlink_to(sentinel)
                elif kind == "hardlink":
                    os.link(sentinel, leaf)
                else:
                    os.mkfifo(leaf, 0o600)
                with self.assertRaises((OSError, RuntimeError)):
                    self.read(fd)
                self.assertEqual(b"outside-private-canary", sentinel.read_bytes())
                self.assertEqual(0o600, sentinel.stat().st_mode & 0o777)

    def test_expired_original_deadline_rejects_before_listing_or_reading(self):
        with diagnostic_tree() as (_, fd, _), patch.object(subject.os, "listdir") as listed, patch.object(subject.os, "read") as read:
            with self.assertRaisesRegex(RuntimeError, "^lifecycle-diagnostic-deadline$"):
                self.read(fd, deadline=time.monotonic() - 1)
            listed.assert_not_called()
            read.assert_not_called()

    def test_name_substitution_during_real_read_is_detected(self):
        with diagnostic_tree() as (root, fd, values):
            original_read = os.read
            changed = False

            def replace_after_read(handle, size):
                nonlocal changed
                data = original_read(handle, size)
                if data == values["stderr.prefix"] and not changed:
                    changed = True
                    (root / "stderr.prefix").rename(root / "original")
                    (root / "stderr.prefix").write_bytes(b"replacement")
                    (root / "stderr.prefix").chmod(0o600)
                return data

            with patch.object(subject.os, "read", side_effect=replace_after_read):
                with self.assertRaisesRegex(RuntimeError, "^lifecycle-diagnostic-name-changed$"):
                    self.read(fd)
            self.assertTrue(changed)
            self.assertEqual(values["stderr.prefix"], (root / "original").read_bytes())
            self.assertEqual(b"replacement", (root / "stderr.prefix").read_bytes())

    def test_growth_during_real_read_cannot_return_truncated_bytes(self):
        with diagnostic_tree() as (root, fd, values):
            original_read = os.read
            changed = False

            def grow_after_read(handle, size):
                nonlocal changed
                data = original_read(handle, size)
                if data == values["stderr.prefix"] and not changed:
                    changed = True
                    with (root / "stderr.prefix").open("ab") as writer:
                        writer.write(b"extra")
                return data

            with patch.object(subject.os, "read", side_effect=grow_after_read):
                with self.assertRaisesRegex(RuntimeError, "^lifecycle-diagnostic-growth$"):
                    self.read(fd)
            self.assertTrue(changed)

    def test_duplicate_state_members_reject(self):
        with diagnostic_tree() as (root, fd, _):
            (root / "state.json").write_bytes(b'{"schema":"bad","schema":"evidence-worker-failure-output-v1"}')
            with self.assertRaisesRegex(RuntimeError, "^lifecycle-diagnostic-duplicate-member$"):
                self.read(fd)

    def test_wrong_generation_rejects_before_any_run_directory_open(self):
        with patch.object(subject.os, "open") as opened:
            for generation in (None, "", "a" * 31, "A" * 32, "../" + "a" * 29):
                with self.subTest(generation=generation), self.assertRaisesRegex(RuntimeError, "^lifecycle-diagnostic-generation$"):
                    subject.read_worker_marker(generation, "configure-sync-stall", deadline=time.monotonic() + 5)
            opened.assert_not_called()


class CompileOwnedMarkerControls(unittest.TestCase):
    def test_fixed_fatal_reasons_distinguish_the_two_production_failure_paths(self):
        rows = (("configure-sync-stall", b"Owned Evidence callback or write remained active after its bounded grace.",
                 "callback-unsettled"),
                ("registered-application-disposer-stall", b"Evidence cleanup did not settle within its budget.",
                 "registered-disposer-unsettled"))
        for case, reason, label in rows:
            with self.subTest(case=case):
                prefix = json.dumps(marker(case)).encode() + b"\nProcess terminated. " + reason + b"\n"
                self.assertEqual(label, subject.require_fatal_reason(prefix, case))
                for wrong in (b"unrelated-private-canary", prefix.replace(reason, b"unrelated failure"), prefix + reason):
                    with self.assertRaisesRegex(RuntimeError, "^lifecycle-fatal-reason$"):
                        subject.require_fatal_reason(wrong, case)

    def test_both_fixed_markers_accept_only_their_own_case(self):
        for case in ("configure-sync-stall", "registered-application-disposer-stall"):
            with self.subTest(case=case):
                value = marker(case)
                self.assertEqual(value, subject.marker_from_prefix(json.dumps(value).encode(), case))
                other = "registered-application-disposer-stall" if case == "configure-sync-stall" else "configure-sync-stall"
                with self.assertRaises(RuntimeError):
                    subject.marker_from_prefix(json.dumps(value).encode(), other)

    def test_missing_duplicate_wrong_phase_acceptance_and_extra_fields_reject(self):
        for fault in ("missing", "duplicate", "phase", "acceptance", "extra", "duplicate-member"):
            with self.subTest(fault=fault):
                value = marker()
                if fault == "phase": value["phase"] = "complete"
                if fault == "acceptance": value["native_acceptance"] = True
                if fault == "extra": value["private-canary"] = "secret"
                data = json.dumps(value).encode()
                if fault == "missing": data = b"private-canary"
                if fault == "duplicate": data = data + b"\n" + data
                if fault == "duplicate-member": data = data[:-1] + b',"phase":"entered"}'
                with self.assertRaises(RuntimeError) as error:
                    subject.marker_from_prefix(data, "configure-sync-stall")
                self.assertNotIn("private-canary", str(error.exception))


class JoinedWorkerOutputControls(unittest.TestCase):
    @staticmethod
    def data(case):
        leaves = {"stdout.prefix": b"", "stderr.prefix": json.dumps(marker(case)).encode()}
        count = 1 if case == "configure-sync-stall" else 3
        pairs = [{"received_bytes": len(leaves["stderr.prefix"]), "stdout_bytes": 0,
                  "stderr_bytes": len(leaves["stderr.prefix"]), "stdout_eof": True,
                  "stderr_eof": True, "failure": "None", "quota_exceeded": False}]
        pairs += [{"received_bytes": 0, "stdout_bytes": 0, "stderr_bytes": 0, "stdout_eof": True,
                   "stderr_eof": True, "failure": "None", "quota_exceeded": False} for _ in range(count - 1)]
        value = {"schema": "evidence-coverage-output-v1", "registered_pairs": count,
                 "received_bytes": len(leaves["stderr.prefix"]), "received_byte_limit": 16 * 1024 * 1024,
                 "failure": "None", "quota_exceeded": False, "stop_signal_failed": False, "pairs": pairs}
        return value, leaves

    def test_each_fixed_case_requires_its_own_original_joined_pair_set(self):
        for case in ("configure-sync-stall", "registered-application-disposer-stall"):
            with self.subTest(case=case):
                value, leaves = self.data(case)
                self.assertIs(value, subject.require_joined_output(value, leaves, case))
                other = "registered-application-disposer-stall" if case == "configure-sync-stall" else "configure-sync-stall"
                with self.assertRaises(RuntimeError):
                    subject.require_joined_output(value, leaves, other)

    def test_each_incomplete_or_failed_accounting_fact_rejects(self):
        for fault in ("count", "bool-count", "missing-pair", "limit", "aggregate", "stdout-eof",
                      "stderr-eof", "pair-failure", "pair-quota", "stop-signal", "quota", "negative", "extra"):
            with self.subTest(fault=fault):
                value, leaves = self.data("configure-sync-stall")
                subject.require_joined_output(value, leaves, "configure-sync-stall")
                if fault == "count": value["registered_pairs"] += 1
                if fault == "bool-count": value["registered_pairs"] = True
                if fault == "missing-pair": value["pairs"] = []
                if fault == "limit": value["received_byte_limit"] += 1
                if fault == "aggregate": value["received_bytes"] += 1
                if fault == "stdout-eof": value["pairs"][0]["stdout_eof"] = False
                if fault == "stderr-eof": value["pairs"][0]["stderr_eof"] = False
                if fault == "pair-failure": value["pairs"][0]["failure"] = "Cancelled"
                if fault == "pair-quota": value["pairs"][0]["quota_exceeded"] = True
                if fault == "stop-signal": value["stop_signal_failed"] = True
                if fault == "quota": value["quota_exceeded"] = True
                if fault == "negative": value["pairs"][0]["stdout_bytes"] = -1
                if fault == "extra": value["pairs"][0]["private-canary"] = "secret"
                with self.assertRaises(RuntimeError) as error:
                    subject.require_joined_output(value, leaves, "configure-sync-stall")
                self.assertNotIn("private-canary", str(error.exception))

    def test_prefix_cannot_exceed_the_actual_received_byte_count(self):
        value, leaves = self.data("configure-sync-stall")
        subject.require_joined_output(value, leaves, "configure-sync-stall")
        leaves["stderr.prefix"] += b"private-canary"
        with self.assertRaisesRegex(RuntimeError, "^lifecycle-output-reconciliation$"):
            subject.require_joined_output(value, leaves, "configure-sync-stall")


class PostStopMarkerControls(unittest.TestCase):
    """Detached projections only; native callers separately prove the actual worker."""

    def value(self):
        return {"schema": "evidence-worker-post-stop-guards-v1", "case": "post-stop-guards",
                "phase": "completed", "active_admission": True, "current_worker": True,
                "owned_work_joined": True, "new_work_rejected": True, "write_rejected": True,
                "artifacts_unchanged": True, "native_acceptance": False}

    def test_fixture_policy_and_generated_closed_declaration_share_the_unused_positive_capacity_slot(self):
        spec = importlib.util.spec_from_file_location("post_stop_payload", Path(__file__).with_name("prepare_payload.py"))
        payload = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(payload)
        for case in payload.CASES:
            with self.subTest(case=case):
                policy = payload.policy_for(case)
                slots = policy["Profiles"][0]["Producers"][0]["ArtifactSlots"]
                self.assertEqual(2, len(slots))
                self.assertEqual({"LogicalName": "post-stop-probe", "RelativeRoot": "lifecycle-probe",
                                  "MediaType": "application/octet-stream", "Required": False,
                                  "MaximumBytes": 1}, slots[1])
                generated = payload.definition_source(case, [], policy)
                self.assertEqual(1, generated.count('new EvidenceArtifactSlot("post-stop-probe", "lifecycle-probe", "application/octet-stream", false, 1)'))
                self.assertIn('new EvidenceArtifactSlot("coverage-report", "merged", "application/xml", true, 20_971_520)', generated)

    def test_completed_event_requires_each_true_fact_and_one_private_terminal_reason(self):
        value = self.value()
        prefix = json.dumps(value).encode() + b"\nASEVD410: Private post-stop admission guards completed."
        self.assertEqual(value, subject.post_stop_marker_from_prefix(prefix))
        self.assertEqual("post-stop-guards-completed-unsuccessfully", subject.require_post_stop_terminal(prefix))
        ledger, leaves = JoinedWorkerOutputControls.data("registered-application-disposer-stall")
        self.assertIs(ledger, subject.require_joined_output(ledger, leaves, "post-stop-guards"))
        for name in ("active_admission", "current_worker", "owned_work_joined", "new_work_rejected",
                     "write_rejected", "artifacts_unchanged"):
            for bad in (False, 1, None, "private-canary"):
                with self.subTest(name=name, bad=bad), self.assertRaises(RuntimeError) as error:
                    subject.post_stop_marker_from_prefix(json.dumps(dict(value, **{name: bad})).encode())
                self.assertNotIn("private-canary", str(error.exception))

    def test_missing_duplicate_nonclosed_or_wrong_case_cannot_replace_completed_checks(self):
        value = self.value()
        for rows in ([], [value, value], [dict(value, case="configure-sync-stall")],
                     [dict(value, phase="entered")], [dict(value, native_acceptance=True)],
                     [dict(value, extra="private-canary")]):
            with self.subTest(rows=rows), self.assertRaises(RuntimeError):
                subject.post_stop_marker_from_prefix(b"\n".join(json.dumps(row).encode() for row in rows))
        for prefix in (b"unrelated-crash", b"Private post-stop admission guards completed." * 2,
                       b"x" * (subject.PREFIX_LIMIT + 1)):
            with self.subTest(length=len(prefix)), self.assertRaises(RuntimeError):
                subject.require_post_stop_terminal(prefix)


if __name__ == "__main__":
    unittest.main()
