"""Failure-closed checks for the workflow's independent cleanup receipt."""

from __future__ import annotations

import importlib.util
import json
import os
from pathlib import Path
import tempfile
import unittest


SCRIPT = Path(__file__).resolve().parents[1] / "evidence-gate-cleanup-attestation-check.py"
SPEC = importlib.util.spec_from_file_location("evidence_gate_cleanup_attestation_check", SCRIPT)
assert SPEC is not None and SPEC.loader is not None
checker = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(checker)


class CleanupAttestationCheckTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory(prefix="cleanup-check-")
        self.root = Path(self.temporary.name).resolve()
        self.runner_temp = self.root / "runner-temp"
        self.runner_temp.mkdir(mode=0o755)
        self.state = self.runner_temp / (checker.STATE_PREFIX + "a" * 32)
        self.state.mkdir(mode=0o700)
        self.attestation = self.state / checker.ATTESTATION_NAME
        self.output = self.runner_temp / checker.OUTPUT_NAME
        self.record = {
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

    def tearDown(self) -> None:
        self.temporary.cleanup()

    def _write(self, payload: bytes | None = None) -> bytes:
        payload = payload or (json.dumps(self.record, sort_keys=True, separators=(",", ":")) + "\n").encode("ascii")
        self.attestation.write_bytes(payload)
        self.attestation.chmod(0o600)
        return payload

    def _check(self, **overrides: object) -> None:
        checker.check_cleanup(
            self.runner_temp,
            self.output,
            uid=os.geteuid(),
            wait_seconds=0,
            **overrides,
        )

    def test_complete_private_record_is_copied_byte_for_byte_from_runner_temp(self) -> None:
        payload = self._write()

        self._check()

        self.assertEqual(payload, self.output.read_bytes())
        self.assertEqual(0o600, self.output.stat().st_mode & 0o777)

    def test_missing_or_ambiguous_supervisor_state_fails_closed(self) -> None:
        self.state.rmdir()
        with self.assertRaisesRegex(checker.CleanupCheckError, "cleanup-state-ambiguous"):
            self._check()

        self.state.mkdir(mode=0o700)
        (self.runner_temp / (checker.STATE_PREFIX + "b" * 32)).mkdir(mode=0o700)
        with self.assertRaisesRegex(checker.CleanupCheckError, "cleanup-state-ambiguous"):
            self._check()

    def test_missing_attestation_times_out_without_creating_output(self) -> None:
        with self.assertRaisesRegex(checker.CleanupCheckError, "cleanup-attestation-timeout"):
            self._check()
        self.assertFalse(self.output.exists())

    def test_symlink_or_incomplete_attestation_fails_closed(self) -> None:
        target = self.root / "forged.json"
        target.write_bytes(json.dumps(self.record).encode("ascii"))
        self.attestation.symlink_to(target)
        with self.assertRaisesRegex(checker.CleanupCheckError, "cleanup-attestation-unavailable"):
            self._check()

        self.attestation.unlink()
        self.record["status"] = "incomplete"
        self._write()
        with self.assertRaisesRegex(checker.CleanupCheckError, "cleanup-incomplete"):
            self._check()
        self.assertFalse(self.output.exists())

    def test_malformed_and_oversized_records_fail_closed(self) -> None:
        self._write(b'{"status":"complete","status":"complete"}')
        with self.assertRaisesRegex(checker.CleanupCheckError, "cleanup-attestation-malformed"):
            self._check()

        self._write(b"x" * (checker.MAX_ATTESTATION_BYTES + 1))
        with self.assertRaisesRegex(checker.CleanupCheckError, "cleanup-attestation-unsafe"):
            self._check()

    def test_state_mode_and_preexisting_output_are_rejected(self) -> None:
        self._write()
        self.state.chmod(0o755)
        with self.assertRaisesRegex(checker.CleanupCheckError, "cleanup-state-unsafe"):
            self._check()

        self.state.chmod(0o700)
        self.output.write_bytes(b"old")
        with self.assertRaisesRegex(checker.CleanupCheckError, "cleanup-output-unsafe"):
            self._check()


if __name__ == "__main__":
    unittest.main()
