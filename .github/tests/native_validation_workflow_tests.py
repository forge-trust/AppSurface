#!/usr/bin/env python3
"""Static contract checks for the portable native-validation procedure workflow.

Run: python3 .github/tests/native_validation_workflow_tests.py
No build, restore, network, or native acceptance journey is performed.
"""

from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[2]
WORKFLOW = ROOT / ".github/workflows/native-evidence-validation.yml"
README = ROOT / "tools/ForgeTrust.AppSurface.Evidence.Validation/README.md"
DESIGN = ROOT / "docs/designs/native-evidence-validation.md"


class NativeValidationProcedureWorkflowTests(unittest.TestCase):
    def test_workflow_runs_and_validates_only_the_explicit_procedure_report(self) -> None:
        source = WORKFLOW.read_text(encoding="utf-8")
        for required in (
            "name: Native Evidence validation procedure",
            "contents: read",
            "id: build",
            "timeout-minutes: 25",
            "matrix:\n        os: [ubuntu-latest, windows-latest, macos-latest]\n        mode: [storage, consumer]",
            "${{ matrix.mode }}",
            "--operation procedure --mode '${{ matrix.mode }}'",
            "procedure-plan.json",
            "native-validation-procedure.json",
            "appsurface-native-validation-procedure-v1",
            "$report.procedurePassed -ne $true",
            "$report.cleanupCompleted -ne $true",
            "$report.protectedAdmission -ne $false",
            "$report.nativeAcceptance -ne $false",
            "$report.gateEligible -ne $false",
            "evidence-manifest.json",
            "if (Test-Path -LiteralPath (Join-Path $proof 'evidence-manifest.json'))",
            "Evidence procedure (${{ matrix.os }}, ${{ matrix.mode }})",
            "name: Test native lifecycle, tamper, and fan-in contracts",
            "FullyQualifiedName~NativeValidationJsonTests",
            "FullyQualifiedName~NativeEvidenceProgramTests",
            "FullyQualifiedName~NativeValidationProcedureTests",
            "name: evidence-procedure-${{ matrix.os }}-${{ matrix.mode }}-${{ github.run_attempt }}",
            "if-no-files-found: warn",
        ):
            with self.subTest(required=required):
                self.assertIn(required, source)
        self.assertNotIn("--operation protected", source)
        self.assertNotRegex(source, r"(?im)^\s+[A-Za-z_-]+:\s*write(?:-all)?\s*$")
        self.assertNotRegex(source, r"(?i)--(?:minimum-line|minimum-branch|coverage-tolerance|maximum-scratch-bytes|minimum-free-bytes)")

    def test_protected_acceptance_workflows_are_not_relabelled_or_modified(self) -> None:
        # These independently authorized workflows remain the only locations for their
        # protected evidence producer/consumer paths; this procedure cannot invoke them.
        protected = [
            ROOT / ".github/workflows/evidencehost-runtime-proof.yml",
            ROOT / ".github/workflows/evidencehost-protected-qualification.yml",
            ROOT / ".github/workflows/evidencehost-protected-parent.yml",
        ]
        for path in protected:
            with self.subTest(workflow=path.name):
                self.assertTrue(path.is_file())
                text = path.read_text(encoding="utf-8")
                self.assertNotIn("--operation procedure", text)
                self.assertNotIn("appsurface-native-validation-procedure-v1", text)
                self.assertIn(f"../../.github/workflows/{path.name}", README.read_text(encoding="utf-8"))
                self.assertIn(f"../../.github/workflows/{path.name}", DESIGN.read_text(encoding="utf-8"))

    def test_docs_describe_report_shape_operation_choice_and_unchanged_gates(self) -> None:
        readme = README.read_text(encoding="utf-8")
        design = DESIGN.read_text(encoding="utf-8")
        for token in (
            "--operation procedure",
            "--operation protected",
            "ASEVD401",
            "ASEVD404",
            "procedure-plan.json",
            "native-validation-procedure.json",
            "appsurface-native-validation-procedure-v1",
            "procedurePassed",
            "cleanupCompleted",
            "producerResults",
            "protectedAdmission",
            "nativeAcceptance",
            "gateEligible",
            "95% line",
            "85% branch",
            "25-minute",
            "Issue 779",
        ):
            with self.subTest(token=token):
                self.assertIn(token, readme + design)
        self.assertIn("Not evidence of protected worker admission or custody", design)
        self.assertIn("Not proof that the captures came from a protected current run", design)
        self.assertIn("at most three producer results", readme)
        self.assertIn("four declared artifact metadata records", readme)


if __name__ == "__main__":
    unittest.main()
