"""Untrusted structural-data regressions only; none establishes root origin or a qualified run."""
import importlib.util
import json
from pathlib import Path
import stat
import tempfile
import unittest

SPEC = importlib.util.spec_from_file_location("qualification_result_data", Path(__file__).with_name("run-qualification.py"))
module = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(module)


class ResultDataControls(unittest.TestCase):
    def data(self):
        report = b'<coverage lines-valid="1" branches-valid="2" />'
        manifest = {"Mode": "Observation", "ExecutionVerdict": "Passed", "ClaimKind": "None", "Eligibility": "None",
                    "Metrics": {"CleanupCompleted": True}, "PlanDigest": "data-only-plan-digest",
                    "ResourceResults": [{"ResourceId": "native-http", "Outcome": "Ready"}],
                    "ProducerResults": [{"ProducerId": module.PRODUCER, "Outcome": "Passed", "SatisfiedAssertionIds": [module.ASSERTION],
                    "Artifacts": [{"LogicalName": "coverage-report", "RelativePath": "merged/coverage.cobertura.xml",
                    "MediaType": "application/xml", "LengthBytes": len(report), "Sha256": module.sha(report)}]}],
                    "ClosedObligationIds": ["qualified-risk"], "UnmediatedObligationIds": []}
        # These supplied JSON bytes are deliberately unauthenticated data, not execution evidence.
        artifacts = {"manifest.json": json.dumps(manifest).encode(), module.PRODUCER+"/merged/coverage.cobertura.xml": report}
        return artifacts, manifest

    def test_omitted_optional_null_fields_are_valid_data_but_nonnull_rejects(self):
        artifacts, manifest = self.data()
        metadata = {"plan_digest": manifest["PlanDigest"]}
        self.assertEqual(manifest, module.validate_result(artifacts, b"expected-data", "host", metadata))
        for field in ("EnvelopeAssertion", "TerminalFailureCode"):
            with self.subTest(field=field):
                artifacts, changed = self.data()
                if field == "EnvelopeAssertion": changed[field] = {"untrusted": True}
                else: changed["Metrics"][field] = "StageFailed"
                artifacts["manifest.json"] = json.dumps(changed).encode()
                with self.assertRaises(module.PreparationFailure):
                    module.validate_result(artifacts, b"expected-data", "host", metadata)

    def test_invalid_collected_data_is_retained_privately_before_rejection(self):
        artifacts, manifest = self.data()
        manifest["ExecutionVerdict"] = "Incomplete"
        artifacts["manifest.json"] = json.dumps(manifest).encode()
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            with self.assertRaises(module.PreparationFailure):
                module.persist_and_validate(root, "host", artifacts, b"expected-data", {"plan_digest": manifest["PlanDigest"]})
            for name, data in artifacts.items():
                path = root/"collected-host"/name
                self.assertEqual(data, path.read_bytes())
                self.assertEqual(0o600, stat.S_IMODE(path.stat().st_mode))
            self.assertEqual(0o700, stat.S_IMODE((root/"collected-host").stat().st_mode))


if __name__ == "__main__":
    unittest.main()
