"""Trusted verifier checks for the subject cleanup handoff."""

from __future__ import annotations

import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


SCRIPT = Path(__file__).resolve().parents[1] / "evidence-gate-verify-cleanup.py"
SPEC = importlib.util.spec_from_file_location("evidence_gate_verify_cleanup", SCRIPT)
assert SPEC is not None and SPEC.loader is not None
verifier = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(verifier)


class TrustedCleanupVerificationTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory(prefix="trusted-cleanup-verifier-")
        self.root = Path(self.temporary.name).resolve()
        self.plan = self.root / "evidence-plan.json"
        self.attestation = self.root / "evidence-gate-cleanup-attestation.json"
        self.record = self._complete_record()
        self._profile("code-coverage")

    def tearDown(self) -> None:
        self.temporary.cleanup()

    def _profile(self, profile_id: str) -> None:
        self.plan.write_text(json.dumps({"Profile": {"Id": profile_id}}), encoding="ascii")

    def _record(self) -> None:
        self.attestation.write_bytes(
            (json.dumps(self.record, sort_keys=True, separators=(",", ":")) + "\n").encode("ascii")
        )

    @staticmethod
    def _complete_record() -> dict[str, object]:
        return {
            "schemaVersion": 1,
            "claimEligible": False,
            "published": False,
            "status": "complete",
            "trigger": "request",
            "containerStatus": "removed",
            "mountStatus": "unmounted",
            "scratchStatus": "removed",
            "failureCode": "none",
            "cleanupDeadlineSeconds": 600,
        }

    def test_coverage_requires_complete_canonical_cleanup_record(self) -> None:
        self._record()
        verifier.verify_cleanup(self.plan, self.attestation)

        self.record["trigger"] = "parent-exit"
        self.record["containerStatus"] = "absent"
        self.record["mountStatus"] = "absent"
        self.record["scratchStatus"] = "absent"
        self._record()
        verifier.verify_cleanup(self.plan, self.attestation)

    def test_empty_documentation_profile_rejects_unexpected_cleanup_artifact(self) -> None:
        self._profile("documentation-only")
        verifier.verify_cleanup(self.plan, self.attestation)

        self._record()
        with self.assertRaisesRegex(verifier.CleanupVerificationError, "ASEGC004"):
            verifier.verify_cleanup(self.plan, self.attestation)

    def test_coverage_rejects_missing_unsafe_and_incomplete_records(self) -> None:
        with self.assertRaisesRegex(verifier.CleanupVerificationError, "ASEGC002"):
            verifier.verify_cleanup(self.plan, self.attestation)

        target = self.root / "target.json"
        target.write_bytes(b"{}\n")
        self.attestation.symlink_to(target)
        with self.assertRaisesRegex(verifier.CleanupVerificationError, "ASEGC002"):
            verifier.verify_cleanup(self.plan, self.attestation)
        self.attestation.unlink()

        self.record["status"] = "incomplete"
        self._record()
        with self.assertRaisesRegex(verifier.CleanupVerificationError, "ASEGC003"):
            verifier.verify_cleanup(self.plan, self.attestation)

    def test_coverage_rejects_duplicate_extra_and_noncanonical_json(self) -> None:
        self.attestation.write_bytes(b'{"status":"complete","status":"complete"}\n')
        with self.assertRaisesRegex(verifier.CleanupVerificationError, "ASEGC002"):
            verifier.verify_cleanup(self.plan, self.attestation)

        self.record["subjectOwnedClaim"] = True
        self._record()
        with self.assertRaisesRegex(verifier.CleanupVerificationError, "ASEGC002"):
            verifier.verify_cleanup(self.plan, self.attestation)
        del self.record["subjectOwnedClaim"]

        self.attestation.write_bytes(json.dumps(self.record).encode("ascii"))
        with self.assertRaisesRegex(verifier.CleanupVerificationError, "ASEGC002"):
            verifier.verify_cleanup(self.plan, self.attestation)

    def test_coverage_rejects_each_incomplete_cleanup_condition(self) -> None:
        invalid_values = {
            "schemaVersion": [True, 2],
            "claimEligible": [True],
            "published": [True],
            "status": ["incomplete"],
            "trigger": ["unknown"],
            "containerStatus": ["failed"],
            "mountStatus": ["mounted"],
            "scratchStatus": ["present"],
            "failureCode": ["resource-cleanup-incomplete"],
            "cleanupDeadlineSeconds": [True, 601],
        }
        for field, values in invalid_values.items():
            for value in values:
                with self.subTest(field=field, value=value):
                    self.record[field] = value
                    self._record()
                    with self.assertRaisesRegex(verifier.CleanupVerificationError, "ASEGC003"):
                        verifier.verify_cleanup(self.plan, self.attestation)
            self.record[field] = self._complete_record()[field]

    def test_rejects_oversized_record_and_unsupported_profile(self) -> None:
        self.attestation.write_bytes(b"x" * (verifier.MAX_ATTESTATION_BYTES + 1))
        with self.assertRaisesRegex(verifier.CleanupVerificationError, "ASEGC002"):
            verifier.verify_cleanup(self.plan, self.attestation)

        self._profile("postgresql-integration")
        with self.assertRaisesRegex(verifier.CleanupVerificationError, "ASEGC004"):
            verifier.verify_cleanup(self.plan, self.attestation)

    def test_rejects_symlinked_parent_and_malformed_plan(self) -> None:
        self._record()
        linked_parent = self.root / "linked-parent"
        linked_parent.symlink_to(self.root, target_is_directory=True)
        with self.assertRaisesRegex(verifier.CleanupVerificationError, "ASEGC002"):
            verifier.verify_cleanup(self.plan, linked_parent / self.attestation.name)

        self.plan.write_bytes(b'{"Profile":{"Id":"code-coverage"},"Profile":{}}')
        with self.assertRaisesRegex(verifier.CleanupVerificationError, "ASEGC001"):
            verifier.verify_cleanup(self.plan, self.attestation)

    def test_rejects_hardlinked_record_and_nonregular_plan(self) -> None:
        self._record()
        self.attestation.unlink()
        target = self.root / "linked-record.json"
        target.write_bytes(b"{}\n")
        os.link(target, self.attestation)
        with self.assertRaisesRegex(verifier.CleanupVerificationError, "ASEGC002"):
            verifier.verify_cleanup(self.plan, self.attestation)

        self.attestation.unlink()
        self.plan.unlink()
        self.plan.mkdir()
        with self.assertRaisesRegex(verifier.CleanupVerificationError, "ASEGC001"):
            verifier.verify_cleanup(self.plan, self.attestation)

    @unittest.skipUnless(hasattr(os, "mkfifo"), "requires POSIX FIFOs")
    def test_rejects_fifo_without_waiting_for_a_writer(self) -> None:
        os.mkfifo(self.attestation)
        with self.assertRaisesRegex(verifier.CleanupVerificationError, "ASEGC002"):
            verifier.verify_cleanup(self.plan, self.attestation)

    def test_cli_exits_nonzero_without_disclosing_record_bytes(self) -> None:
        self._record()
        arguments = [
            sys.executable,
            str(SCRIPT),
            "--plan", str(self.plan),
            "--attestation", str(self.attestation),
        ]
        complete = subprocess.run(arguments, capture_output=True, text=True, check=False)
        self.assertEqual(0, complete.returncode)

        secret_marker = "SUBJECT-RAW-SECRET-MARKER"
        self.attestation.write_text(secret_marker, encoding="ascii")
        rejected = subprocess.run(arguments, capture_output=True, text=True, check=False)
        self.assertEqual(2, rejected.returncode)
        self.assertIn("ASEGC002", rejected.stderr)
        self.assertNotIn(secret_marker, rejected.stdout + rejected.stderr)


if __name__ == "__main__":
    unittest.main()
