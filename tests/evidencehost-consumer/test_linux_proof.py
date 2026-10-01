"""Regression checks for the provisional fixture's subject-evidence verifier."""
import importlib.util
import unittest
from pathlib import Path

spec = importlib.util.spec_from_file_location("linux_proof", Path(__file__).with_name("linux-proof.py"))
proof = importlib.util.module_from_spec(spec)
spec.loader.exec_module(proof)


class SubjectEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.results = dict.fromkeys({
            "allowed-input", "cgroup-escape", "no-new-privileges", "output-write",
            "registration-read", "supervisor-read", "verifier-read", "worker-environment-read",
            "worker-fd-read", "worker-ptrace", "worker-signal", "zero-secret-projection",
        }, True)

    def test_complete_boolean_observations_pass(self):
        proof.verify_subject_results(self.results)

    def test_each_missing_observation_rejects(self):
        for key in self.results:
            with self.subTest(key=key):
                incomplete = self.results.copy()
                del incomplete[key]
                with self.assertRaises(proof.ProofFailure):
                    proof.verify_subject_results(incomplete)

    def test_false_or_truthy_non_boolean_observation_rejects(self):
        for value in (False, 1, "true", [], None):
            with self.subTest(value=value):
                with self.assertRaises(proof.ProofFailure):
                    proof.verify_subject_results(dict(self.results, **{"worker-ptrace": value}))

    def test_unknown_or_wrong_shape_evidence_rejects(self):
        for value in ({}, [], True, dict(self.results, extra=True)):
            with self.subTest(value=value):
                with self.assertRaises(proof.ProofFailure):
                    proof.verify_subject_results(value)


if __name__ == "__main__":
    unittest.main()
