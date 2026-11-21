"""Deterministic evidence and decision tests for issue #931 benchmark runner."""

from __future__ import annotations

import csv
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
import xml.etree.ElementTree as ET


SCRIPT = Path(__file__).resolve().parents[1] / "postgresql-fixture-benchmark.py"
SPEC = importlib.util.spec_from_file_location("postgresql_fixture_benchmark", SCRIPT)
BENCHMARK = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(BENCHMARK)
MANIFEST = BENCHMARK.read_manifest()
SERVER_SETTINGS = {
    "autovacuum": "on", "checkpoint_timeout": "5min", "fsync": "on", "full_page_writes": "on",
    "max_connections": "100", "max_wal_size": "1GB", "shared_buffers": "128MB",
    "synchronous_commit": "on", "wal_level": "replica", "work_mem": "4MB",
}


def write_trx(path: Path, cohort: str, outcome: str = "Passed", include_collector: bool = True,
              *, identities: list[str] | None = None, run_outcome: str = "Completed") -> None:
    namespace = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"
    root = ET.Element(f"{{{namespace}}}TestRun")
    definitions = ET.SubElement(root, f"{{{namespace}}}TestDefinitions")
    results = ET.SubElement(root, f"{{{namespace}}}Results")
    if identities is None:
        identities = MANIFEST["_identities"][cohort]
    for index, identity in enumerate(identities):
        test_id = f"test-{cohort}-{index}"
        class_name, method_name = identity.rsplit(".", 1)
        definition = ET.SubElement(definitions, f"{{{namespace}}}UnitTest", {"id": test_id})
        ET.SubElement(
            definition,
            f"{{{namespace}}}TestMethod",
            {"className": class_name, "name": method_name},
        )
        ET.SubElement(
            results,
            f"{{{namespace}}}UnitTestResult",
            {"testId": test_id, "testName": identity, "outcome": outcome},
        )
    summary = ET.SubElement(root, f"{{{namespace}}}ResultSummary", {"outcome": run_outcome})
    count = str(len(identities))
    ET.SubElement(summary, f"{{{namespace}}}Counters", {
        "total": count, "executed": count, "passed": count if outcome == "Passed" else "0",
        "failed": count if outcome == "Failed" else "0", "notExecuted": count if outcome == "NotExecuted" else "0",
    })
    if include_collector:
        ET.SubElement(root, f"{{{namespace}}}Deployment", {"runDeploymentRoot": "results"})
        collectors = ET.SubElement(summary, f"{{{namespace}}}Collectors")
        collector = ET.SubElement(
            collectors,
            f"{{{namespace}}}Collector",
            {"friendlyName": "XPlat Code Coverage", "uri": "datacollector://Microsoft/CoverletCodeCoverage/1.0"},
        )
        uri_attachments = ET.SubElement(collector, f"{{{namespace}}}UriAttachments")
        uri_attachment = ET.SubElement(uri_attachments, f"{{{namespace}}}UriAttachment")
        ET.SubElement(uri_attachment, f"{{{namespace}}}A", {"href": "coverage.cobertura.xml"})
    ET.ElementTree(root).write(path, encoding="utf-8", xml_declaration=True)


def write_coverage(path: Path, *, lines_valid: str = "100", lines_covered: str = "75") -> None:
    path.write_text(
        f'<coverage lines-valid="{lines_valid}" lines-covered="{lines_covered}" '
        'branches-valid="20" branches-covered="12"/>',
        encoding="utf-8",
    )


def write_timings(path: Path, cohort: str, arm: str, *, elapsed_ms: str = "4.5") -> None:
    with path.open("w", encoding="utf-8", newline="") as stream:
        writer = csv.writer(stream)
        writer.writerow(["phase", "elapsed_ms"])
        for _ in range(BENCHMARK.EXPECTED_CASE_COUNTS[cohort]):
            writer.writerow(["database_setup", elapsed_ms])
            writer.writerow(["database_teardown", "0"])
        if arm == "template":
            writer.writerow(["template_prepare", "1"])
            writer.writerow(["template_teardown", "0"])


def valid_arm_records(work_seconds: float, flow_seconds: float) -> list[dict[str, object]]:
    return [
        {"status": "VALID", "cohort": "work", "wall_seconds": work_seconds},
        {"status": "VALID", "cohort": "flow", "wall_seconds": flow_seconds},
    ]


def build_pairs(work_gains: list[float], flow_gains: list[float]) -> list[dict[str, object]]:
    pairs = []
    for index, (work_gain, flow_gain) in enumerate(zip(work_gains, flow_gains), start=1):
        fresh = valid_arm_records(100.0, 100.0)
        template = valid_arm_records(100.0 * (1.0 - work_gain / 100.0), 100.0 * (1.0 - flow_gain / 100.0))
        pairs.append({"pair": index, "status": "VALID", "arms": {"fresh": fresh, "template": template}})
    return pairs


class ManifestTests(unittest.TestCase):
    def test_frozen_manifest_contains_34_unique_cases_and_one_empty_flow_case(self):
        self.assertEqual(4, len(MANIFEST["_identities"]["work"]))
        self.assertEqual(30, len(MANIFEST["_identities"]["flow"]))
        self.assertEqual(34, len(set(MANIFEST["_identities"]["work"] + MANIFEST["_identities"]["flow"])))
        self.assertEqual(
            "FlowClient_StartRejectsANonAuthoritativeContextCodec",
            MANIFEST["source_comparison"]["database_setup"]["empty_method"],
        )


class RedactionTests(unittest.TestCase):
    def test_redacts_connection_strings_and_common_secret_fields(self):
        secret = "Host=pg.internal;Username=sentinel-user;Password=sentinel-password;Database=fixture"
        output = BENCHMARK.redact(
            f"connection={secret}; pwd=sentinel-pwd user id='sentinel-id' postgres://sentinel:url@pg.internal/db",
            secret,
        )
        for sentinel in ("sentinel-user", "sentinel-password", "sentinel-pwd", "sentinel-id", "sentinel:url"):
            with self.subTest(sentinel=sentinel):
                self.assertNotIn(sentinel, output)
        self.assertIn("<redacted connection string>", output)


class ServerMetadataTests(unittest.TestCase):
    def test_acceptance_uses_the_observation_from_the_named_database_probe(self):
        expected = {"version": "17.12", "settings": SERVER_SETTINGS}
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "server-check.trx"
            write_trx(path, "server", identities=[BENCHMARK.SERVER_METADATA_TEST], include_collector=False)
            tree = ET.parse(path)
            result = tree.find(".//{*}UnitTestResult")
            output = ET.SubElement(result, "Output")
            stdout = ET.SubElement(output, "StdOut")
            stdout.text = BENCHMARK.SERVER_METADATA_PREFIX + json.dumps(expected)
            tree.write(path, encoding="utf-8")
            receipt = BENCHMARK.validate_server_check_trx(path, expected)
            self.assertEqual(expected, receipt["observed"])
            with self.assertRaisesRegex(BENCHMARK.BenchmarkError, "does not match"):
                BENCHMARK.validate_server_check_trx(path, {"version": "16.4", "settings": SERVER_SETTINGS})
            stdout.text += "\n" + stdout.text
            tree.write(path, encoding="utf-8")
            with self.assertRaises(BENCHMARK.BenchmarkError):
                BENCHMARK.validate_server_check_trx(path, expected)

    def test_accepts_supported_version_and_safe_scalar_settings(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "server.json"
            path.write_text(json.dumps({"version": "16.4", "settings": SERVER_SETTINGS}))
            self.assertEqual(
                {"version": "16.4", "settings": SERVER_SETTINGS},
                BENCHMARK.read_server_info(path),
            )

    def test_rejects_unsupported_version_unknown_setting_and_secret_key(self):
        invalid_values = [
            {"version": "15.9", "settings": {"max_connections": 100}},
            {"version": "16.4", "settings": {"password": "sentinel"}},
            {"version": "16.4", "settings": {"max_connections": [100]}},
        ]
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "server.json"
            for value in invalid_values:
                with self.subTest(value=value):
                    path.write_text(json.dumps(value), encoding="utf-8")
                    with self.assertRaises(BENCHMARK.BenchmarkError):
                        BENCHMARK.read_server_info(path)


class TrxEvidenceTests(unittest.TestCase):
    def test_passing_cases_from_an_aborted_host_are_ineligible(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "results.trx"
            write_trx(path, "work", run_outcome="Aborted")
            with self.assertRaisesRegex(BENCHMARK.BenchmarkError, "completed result summary"):
                BENCHMARK.parse_trx(path, "work")

    def test_acceptance_requires_the_named_lifecycle_and_guard_methods(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "acceptance.trx"
            # An exact, passing Work cohort alone provides no ownership or fault evidence.
            write_trx(path, "work")
            with self.assertRaisesRegex(BENCHMARK.BenchmarkError, "witness"):
                BENCHMARK.validate_acceptance_trx(path)
            identities = [f"{BENCHMARK.POSTGRES_TEST_NAMESPACE}.{class_name}.{method}"
                          for class_name, methods in BENCHMARK.ACCEPTANCE_WITNESSES.values()
                          for method in methods]
            write_trx(path, "matrix", identities=identities, include_collector=False)
            self.assertEqual(len(identities), BENCHMARK.validate_acceptance_trx(path)["passed"])
            write_trx(path, "matrix", identities=identities[1:], include_collector=False)
            with self.assertRaises(BENCHMARK.BenchmarkError):
                BENCHMARK.validate_acceptance_trx(path)

    def test_acceptance_build_marker_must_match_the_measured_release(self):
        digest = "ab" * 32
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "acceptance.trx"
            identities = [name for family in BENCHMARK.required_acceptance_witnesses().values() for name in family]
            identities = list(dict.fromkeys([*identities, BENCHMARK.SERVER_METADATA_TEST]))
            write_trx(path, "matrix", identities=identities, include_collector=False)
            tree = ET.parse(path)
            result = next(r for r in tree.findall(".//{*}UnitTestResult")
                          if r.get("testName") == BENCHMARK.SERVER_METADATA_TEST)
            output = ET.SubElement(result, "Output")
            ET.SubElement(output, "StdOut").text = (
                BENCHMARK.SERVER_METADATA_PREFIX + json.dumps({"version": "17.12", "settings": SERVER_SETTINGS})
                + f"\nFIXTURE_RELEASE_SHA256 {digest}"
            )
            tree.write(path, encoding="utf-8")
            self.assertEqual(len(identities), BENCHMARK.validate_acceptance_trx(
                path, expected_assembly_sha256=digest)["passed"])
            with self.assertRaises(BENCHMARK.BenchmarkError):
                BENCHMARK.validate_acceptance_trx(path, expected_assembly_sha256="11" * 32)

    def test_requires_exact_pass_identity_set_and_xplat_collector(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "results.trx"
            write_trx(path, "work")
            evidence = BENCHMARK.parse_trx(path, "work")
            self.assertEqual(4, evidence["passed"])
            self.assertEqual(4, len(evidence["result_ids"]))
            self.assertEqual("results/In/coverage.cobertura.xml", evidence["collector_attachment"])

            write_trx(path, "work", outcome="NotExecuted")
            with self.assertRaisesRegex(BENCHMARK.BenchmarkError, "outcome"):
                BENCHMARK.parse_trx(path, "work")

            write_trx(path, "work", include_collector=False)
            with self.assertRaisesRegex(BENCHMARK.BenchmarkError, "collector"):
                BENCHMARK.parse_trx(path, "work")

    def test_rejects_missing_and_unexpected_test_identities(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "results.trx"
            write_trx(path, "work")
            root = ET.parse(path).getroot()
            results = next(element for element in root.iter() if element.tag.endswith("Results"))
            results.remove(list(results)[0])
            ET.ElementTree(root).write(path, encoding="utf-8")
            with self.assertRaisesRegex(BENCHMARK.BenchmarkError, "definitions and complete result ID"):
                BENCHMARK.parse_trx(path, "work")


class CoverageEvidenceTests(unittest.TestCase):
    def test_validates_the_trx_attachment_and_matching_export_copy(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory).resolve()
            write_trx(root / "results.trx", "work")
            write_timings(root / "fixture-timings.csv", "work", "fresh")
            attachment = root / "results/In/coverage.cobertura.xml"
            exported = root / "6e125b5b-e73b-45b4-b637-9e7adc046c86/coverage.cobertura.xml"
            for path in (attachment, exported):
                path.parent.mkdir(parents=True)
                write_coverage(path)
            evidence = BENCHMARK.validate_process_evidence(root, "work", "fresh")
            self.assertEqual([str(exported.relative_to(root))], evidence["coverage_export_copies"])
            write_coverage(exported, lines_covered="1")
            with self.assertRaisesRegex(BENCHMARK.BenchmarkError, "differs"):
                BENCHMARK.validate_process_evidence(root, "work", "fresh")

    def test_accepts_nonempty_cobertura_and_rejects_invalid_counters(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "coverage.cobertura.xml"
            write_coverage(path)
            self.assertEqual(100, BENCHMARK.parse_coverage(path)["lines_valid"])
            for valid, covered in (("0", "0"), ("10", "11"), ("-1", "0"), ("x", "1")):
                with self.subTest(valid=valid, covered=covered):
                    write_coverage(path, lines_valid=valid, lines_covered=covered)
                    with self.assertRaises(BENCHMARK.BenchmarkError):
                        BENCHMARK.parse_coverage(path)


class TimingEvidenceTests(unittest.TestCase):
    def test_validates_fresh_and_template_phase_counts_and_boundary_values(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "timings.csv"
            write_timings(path, "work", "fresh", elapsed_ms="0")
            fresh = BENCHMARK.parse_timing_csv(path, "work", "fresh")
            self.assertEqual(4, fresh["counts"]["database_setup"])
            self.assertEqual(0.0, fresh["elapsed_ms"]["database_setup"])

            write_timings(path, "flow", "template")
            template = BENCHMARK.parse_timing_csv(path, "flow", "template")
            self.assertEqual(30, template["counts"]["database_teardown"])
            self.assertEqual(1, template["counts"]["template_prepare"])
            self.assertEqual(1, template["counts"]["template_teardown"])

    def test_rejects_nan_negative_missing_and_wrong_phase_counts(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "timings.csv"
            write_timings(path, "work", "fresh", elapsed_ms="nan")
            with self.assertRaisesRegex(BENCHMARK.BenchmarkError, "finite and nonnegative"):
                BENCHMARK.parse_timing_csv(path, "work", "fresh")
            write_timings(path, "work", "fresh", elapsed_ms="-0.1")
            with self.assertRaises(BENCHMARK.BenchmarkError):
                BENCHMARK.parse_timing_csv(path, "work", "fresh")
            path.unlink()
            with self.assertRaisesRegex(BENCHMARK.BenchmarkError, "not created"):
                BENCHMARK.parse_timing_csv(path, "work", "fresh")

            write_timings(path, "work", "fresh")
            with path.open("a", encoding="utf-8") as stream:
                stream.write("database_setup,1\n")
            with self.assertRaisesRegex(BENCHMARK.BenchmarkError, "phase counts"):
                BENCHMARK.parse_timing_csv(path, "work", "fresh")


class DecisionTests(unittest.TestCase):
    def test_exact_five_percent_threshold_is_go_and_just_below_is_no_go(self):
        at_boundary = BENCHMARK.analyze_pairs(build_pairs([5.0] * 5, [5.0] * 5))
        self.assertEqual("GO", at_boundary["status"])
        below = BENCHMARK.analyze_pairs(build_pairs([4.999] * 5, [4.999] * 5))
        self.assertEqual("NO_GO", below["status"])
        self.assertFalse(below["checks"]["median_gain_at_least_5_percent"])

    def test_median_gain_must_strictly_exceed_twice_mad(self):
        result = BENCHMARK.analyze_pairs(build_pairs([0, 0, 10, 20, 20], [0, 0, 10, 20, 20]))
        self.assertEqual("NO_GO", result["status"])
        self.assertEqual(10.0, result["median_gain_percent"])
        self.assertEqual(10.0, result["median_absolute_deviation_percent"])
        self.assertFalse(result["checks"]["median_gain_exceeds_twice_mad"])

    def test_slowdown_veto_boundary_is_two_versus_three_pairs(self):
        two_vetoes = BENCHMARK.analyze_pairs(build_pairs([-5, -5, 20, 20, 20], [20] * 5))
        self.assertEqual("GO", two_vetoes["status"])
        self.assertEqual(2, two_vetoes["cohort_veto_counts"]["work"])

        three_vetoes = BENCHMARK.analyze_pairs(build_pairs([-5, -5, -5, 20, 20], [20] * 5))
        self.assertEqual("NO_GO", three_vetoes["status"])
        self.assertEqual(3, three_vetoes["cohort_veto_counts"]["work"])
        self.assertFalse(three_vetoes["checks"]["no_cohort_slowdown_veto"])

    def test_incomplete_is_invalid_and_zero_fresh_time_is_rejected(self):
        self.assertEqual("INVALID", BENCHMARK.analyze_pairs([])["status"])
        with self.assertRaises(BENCHMARK.BenchmarkError):
            BENCHMARK.percentage_gain(0, 1)


if __name__ == "__main__":
    unittest.main()
