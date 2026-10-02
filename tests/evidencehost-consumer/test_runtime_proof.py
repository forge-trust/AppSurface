"""Regression controls for candidate proof rejection; these grant no runtime acceptance."""
import importlib.util
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("runtime_proof", Path(__file__).with_name("runtime-proof.py"))
proof = importlib.util.module_from_spec(spec)
spec.loader.exec_module(proof)


class TrustedRejectionProofTests(unittest.TestCase):
    def test_only_the_exact_missing_proof_rejection_counts(self):
        cases = (
            (1, b"", b'{"status":"failed","diagnostic":"ASEVD407"}', True),
            (1, b"", b'{"status":"failed","diagnostic":"launcher-failed"}', False),
            (2, b"", b"unrelated setup failure", False),
            (0, b"", b'{"status":"failed","diagnostic":"ASEVD407"}', False),
            (1, b"unexpected output", b'{"status":"failed","diagnostic":"ASEVD407"}', False),
        )
        for code, stdout, stderr, expected in cases:
            with self.subTest(code=code, stdout=stdout, stderr=stderr), tempfile.TemporaryDirectory() as directory:
                parent = Path(directory)
                arguments = dict(
                    tool_root=parent, policy_file=parent / "policy.json", output_parent=parent,
                    bindings={"EVIDENCE_BASE_REVISION": "base", "EVIDENCE_SUBJECT_REVISION": "subject",
                              "EVIDENCE_WORKFLOW_IDENTITY": "workflow", "EVIDENCE_RUN_ID": "1/1"}, env={},
                )
                with patch.object(proof, "root_command", return_value=(code, stdout, stderr)):
                    if expected:
                        proof.assert_trusted_denied(**arguments)
                    else:
                        with self.assertRaises(proof.ProofFailure):
                            proof.assert_trusted_denied(**arguments)

    def test_declared_rejection_cannot_leave_an_output_anchor(self):
        with tempfile.TemporaryDirectory() as directory:
            parent = Path(directory)
            (parent / "unexpected-anchor").mkdir()
            with patch.object(proof, "root_command", return_value=(
                    1, b"", b'{"status":"failed","diagnostic":"ASEVD407"}')):
                with self.assertRaisesRegex(proof.ProofFailure, "created an output anchor"):
                    proof.assert_trusted_denied(
                        tool_root=parent, policy_file=parent / "policy.json", output_parent=parent,
                        bindings={"EVIDENCE_BASE_REVISION": "base", "EVIDENCE_SUBJECT_REVISION": "subject",
                                  "EVIDENCE_WORKFLOW_IDENTITY": "workflow", "EVIDENCE_RUN_ID": "1/1"}, env={},
                    )


if __name__ == "__main__":
    unittest.main()
