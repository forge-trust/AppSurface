"""Regression tests for the base-owned GitHub gate verdict publisher."""

from __future__ import annotations

import contextlib
import importlib.util
import io
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


SCRIPT = Path(__file__).resolve().parents[1] / "evidence-gate-finalize.py"
SPEC = importlib.util.spec_from_file_location("evidence_gate_finalize", SCRIPT)
assert SPEC is not None and SPEC.loader is not None
publisher = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(publisher)


class GateVerdictPublisherTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory(prefix="trusted-gate-finalize-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.verdict = self.root / "verdict"
        self.verdict.mkdir()
        self.step_summary = self.root / "step-summary.md"

    def _write_verdict(self, eligible: bool = True) -> None:
        code = "ASEVG000" if eligible else "ASEVG006"
        diagnostic = "The current pull request has a verified result."
        plan_summary = {"ProfileHasEvidence": False} if eligible else None
        machine = {
            "IsEligible": eligible,
            "Code": code,
            "Diagnostic": diagnostic,
            "Summary": plan_summary,
        }
        summary = {
            "eligible": eligible,
            "code": code,
            "diagnostic": diagnostic,
            "summary": plan_summary,
        }
        claim = "NoEvidenceRequired" if eligible else "None"
        markdown = (
            "## AppSurface evidence gate\n\n"
            f"- Verdict: **{'eligible' if eligible else 'ineligible'}** (`{code}`)\n"
            f"- Verified claim: `{claim}`\n"
            f"- Diagnostic: {diagnostic}\n"
        )
        (self.verdict / "evidence-gate-verification.json").write_text(json.dumps(machine), encoding="utf-8")
        (self.verdict / "evidence-gate-summary.json").write_text(json.dumps(summary), encoding="utf-8")
        (self.verdict / "evidence-gate-summary.md").write_text(markdown, encoding="utf-8")

    def _finalize(
        self, controller: str = "success", subject: str = "success", verifier: str = "success"
    ) -> tuple[int, str]:
        output = io.StringIO()
        with contextlib.redirect_stdout(output), contextlib.redirect_stderr(io.StringIO()):
            result = publisher.finalize(self.verdict, self.step_summary, controller, subject, verifier)
        return result, output.getvalue()

    def test_verified_empty_profile_passes_and_publishes_same_markdown(self) -> None:
        self._write_verdict()
        expected = (self.verdict / "evidence-gate-summary.md").read_bytes()

        result, output = self._finalize()

        self.assertEqual(0, result)
        self.assertEqual("gate=eligible; code=ASEVG000; claim=NoEvidenceRequired\n", output)
        self.assertEqual(expected, self.step_summary.read_bytes())

    def test_cli_exits_with_the_verified_result(self) -> None:
        self._write_verdict()
        command = [
            sys.executable,
            str(SCRIPT),
            "--verdict-directory", str(self.verdict),
            "--step-summary", str(self.step_summary),
            "--controller-outcome", "success",
            "--subject-outcome", "success",
            "--verifier-outcome", "success",
        ]

        completed = subprocess.run(command, check=False, capture_output=True, text=True)
        self.assertEqual(0, completed.returncode, completed.stderr)
        self.assertIn("code=ASEVG000", completed.stdout)

        command[-1] = "failure"
        completed = subprocess.run(command, check=False, capture_output=True, text=True)
        self.assertEqual(2, completed.returncode)
        self.assertIn("code=ASEGH003", completed.stdout)

    def test_ineligible_verifier_result_fails_with_its_diagnostic_code(self) -> None:
        self._write_verdict(eligible=False)

        result, output = self._finalize()

        self.assertEqual(2, result)
        self.assertIn("code=ASEVG006; claim=None", output)
        self.assertIn(b"**ineligible** (`ASEVG006`)", self.step_summary.read_bytes())

    def test_missing_or_inconsistent_output_falls_back_to_fixed_failure(self) -> None:
        result, output = self._finalize()
        self.assertEqual(2, result)
        self.assertIn("code=ASEGH003", output)

        self._write_verdict()
        machine = self.verdict / "evidence-gate-verification.json"
        machine.write_bytes(b'{"IsEligible":true,"IsEligible":true}')
        result, output = self._finalize()
        self.assertEqual(2, result)
        self.assertIn("code=ASEGH003", output)

        self._write_verdict()
        machine.write_bytes(b'{"IsEligible":NaN}')
        result, output = self._finalize()
        self.assertEqual(2, result)
        self.assertIn("code=ASEGH003", output)

        self._write_verdict()
        raw = json.loads(machine.read_text(encoding="utf-8"))
        raw["Unexpected"] = "ignored only by an unsafe publisher"
        machine.write_text(json.dumps(raw), encoding="utf-8")
        result, output = self._finalize()
        self.assertEqual(2, result)
        self.assertIn("code=ASEGH003", output)

        self._write_verdict()
        markdown = self.verdict / "evidence-gate-summary.md"
        markdown.write_bytes(markdown.read_bytes() + b"\xff")
        result, output = self._finalize()
        self.assertEqual(2, result)
        self.assertIn("code=ASEGH003", output)

        self._write_verdict()
        summary = self.verdict / "evidence-gate-summary.json"
        summary.write_text(summary.read_text(encoding="utf-8").replace("ASEVG000", "ASEVG006"), encoding="utf-8")
        result, output = self._finalize()
        self.assertEqual(2, result)
        self.assertIn("code=ASEGH003", output)

        self._write_verdict()
        markdown = self.verdict / "evidence-gate-summary.md"
        markdown.write_text(
            markdown.read_text(encoding="utf-8").replace("NoEvidenceRequired", "TargetedComplete"),
            encoding="utf-8",
        )
        result, output = self._finalize()
        self.assertEqual(2, result)
        self.assertIn("code=ASEGH003", output)

    def test_upstream_failure_prevents_success_even_with_eligible_result(self) -> None:
        self._write_verdict()
        outcomes_to_check = (
            ("failure", "success", "success"),
            ("success", "skipped", "success"),
            ("success", "success", "failure"),
        )
        for outcomes in outcomes_to_check:
            with self.subTest(outcomes=outcomes):
                self.step_summary.write_bytes(b"")
                result, output = self._finalize(*outcomes)
                self.assertEqual(2, result)
                self.assertIn("code=ASEGH003; claim=None", output)
                self.assertIn(b"**ineligible** (`ASEGH003`)", self.step_summary.read_bytes())

    def test_linked_or_oversized_results_and_summary_write_failure_fail_closed(self) -> None:
        self._write_verdict()
        machine = self.verdict / "evidence-gate-verification.json"
        machine.unlink()
        machine.symlink_to(self.verdict / "evidence-gate-summary.json")
        result, _ = self._finalize()
        self.assertEqual(2, result)

        machine.unlink()
        machine.write_bytes(b"x" * (publisher.MAX_RESULT_BYTES + 1))
        result, _ = self._finalize()
        self.assertEqual(2, result)

        self._write_verdict()
        self.step_summary.unlink()
        self.step_summary.symlink_to(self.root / "elsewhere.md")
        result, output = self._finalize()
        self.assertEqual(2, result)
        self.assertIn("code=ASEGH003; claim=None", output)
        self.assertFalse((self.root / "elsewhere.md").exists())


if __name__ == "__main__":
    unittest.main()
