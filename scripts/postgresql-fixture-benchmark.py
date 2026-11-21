#!/usr/bin/env python3
"""Run and validate the paired PostgreSQL fixture benchmark for issue #931."""

from __future__ import annotations

import argparse
import csv
from collections import Counter
from datetime import datetime, timezone
from fractions import Fraction
import hashlib
import json
import math
import os
from pathlib import Path
import platform
import re
import shutil
import socket
import statistics
import subprocess
import sys
import time
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[1]
MANIFEST_PATH = ROOT / "artifacts/issue-931-fixture-benchmark/inventory/manifest.json"
INVENTORY_DIR = MANIFEST_PATH.parent
PROJECT_RELATIVE = Path(
    "Durable/ForgeTrust.AppSurface.Durable.PostgreSql.Tests/"
    "ForgeTrust.AppSurface.Durable.PostgreSql.Tests.csproj"
)
PROJECT = ROOT / PROJECT_RELATIVE
TEST_ASSEMBLY = PROJECT.parent / "bin/Release/net10.0/ForgeTrust.AppSurface.Durable.PostgreSql.Tests.dll"
RUNNER_CONFIG = PROJECT.parent / "xunit.runner.json"
EXPECTED_MANIFEST_SHA256 = "a0744765a334e4787c33893e0df9728bd3f0542803ae9cfcf10eac624aac9634"
EXPECTED_REVISION = "3f3126a5febff2cb94847bf8a1092b5d61657a18"
EXPECTED_SDK = "10.0.402"
EXPECTED_CLASSES = {
    "work": "ForgeTrust.AppSurface.Durable.PostgreSql.Tests.PostgreSqlDurableWorkDefinitionTests",
    "flow": "ForgeTrust.AppSurface.Durable.PostgreSql.Tests.DurableSlice4ReferenceWorkloadTests",
}
EXPECTED_CASE_COUNTS = {"work": 4, "flow": 30}
POSTGRES_TEST_NAMESPACE = "ForgeTrust.AppSurface.Durable.PostgreSql.Tests"
SERVER_METADATA_TEST = (
    f"{POSTGRES_TEST_NAMESPACE}.PostgreSqlFixtureServerMetadataTests."
    "ReportsMeasurementServerSettings"
)
SERVER_METADATA_PREFIX = "FIXTURE_SERVER_METADATA "
ACCEPTANCE_CLASSES = (
    "PostgreSqlCohortFixtureOptionsTests",
    "PostgreSqlCohortFixtureTests",
    "PostgreSqlFixtureServerMetadataTests",
    "PostgreSqlFixtureTimingCsvTests",
    "PostgreSqlIntegrationTestDatabaseCloneSchemaTests",
    "PostgreSqlIntegrationTestDatabaseTests",
    "PostgreSqlOwnedDatabaseTests",
)
ACCEPTANCE_WITNESSES = {
    "owned_database_lifecycle": (
        "PostgreSqlOwnedDatabaseTests",
        (
            "CreateOwnedAsync_WithheldCreateAcknowledgment_PreservesObservedDatabaseUntilTestCleanup",
            "CreateOwnedAsync_CanceledBeforeDispatch_DoesNotInvokeCreateObserver",
            "CreateOwnedAsync_CanceledAfterServerCreate_RegistersThenRemovesTheObservedDatabase",
            "ActivateCloneAsync_PreservesDistinctIdentitiesAndIsolatesWrites",
            "ActivateCloneAsync_FailureAfterRekey_RemovesOwnedClone",
            "DisposeAsync_ClosesPrimaryAndAdditionalPoolsAndPreservesUnrelatedSession",
            "DisposeAsync_FailedDropIsBoundedAndDuplicateDisposalIsIdempotent",
            "DisposeAsync_SameNameDatabaseReplacement_PreservesReplacementAndClockRoleDependencies",
            "DisposeAsync_SameNameClockRoleReplacement_PreservesRoleDependencyAndDatabaseSentinel",
        ),
    ),
    "clone_schema_validation": (
        "PostgreSqlIntegrationTestDatabaseCloneSchemaTests",
        (
            "PrepareTemplateAndActivateClones_RekeysAndIsolatesSiblingWrites",
            "CloneGuards_RejectWrongKindsAndCallerSuppliedTemplateIdentity",
            "PrepareTemplate_RejectsActiveEpoch",
            "CloneGuard_RejectsChecksumAndNonSingletonMetadataBeforeRekey",
            "CloneGuard_RejectsActiveEpochAndVisibleRuntimeRowsWithoutChangingMetadata",
            "CloneGuard_RejectsRlsHiddenRowsWithRestrictedCredentialAndRedactsRelationNames",
            "CloneGuard_SerializesConcurrentRekeyAndRejectsTheDuplicate",
            "ActivateClone_PostRekeyFailureDropsOnlyTheOwnedClone",
            "PrepareTemplate_RejectsARegisteredNameReplacedByAnUnrelatedEmptyDatabase",
            "TemplateDisposal_RejectsLiveClonesUntilTheyAreReleased",
        ),
    ),
}
UUID_DIRECTORY = re.compile(r"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")
PAIR_ORDER = (
    ("fresh", "template"),
    ("template", "fresh"),
    ("fresh", "template"),
    ("template", "fresh"),
    ("fresh", "template"),
)
FIXTURE_VARIABLE = "APPSURFACE_DURABLE_POSTGRES_TEST_FIXTURE"
TIMING_VARIABLE = "APPSURFACE_DURABLE_FIXTURE_TIMINGS_FILE"
CONNECTION_VARIABLE = "APPSURFACE_POSTGRES_TEST_CONNECTION"
SERVER_SETTING_KEYS = frozenset({
    "autovacuum",
    "checkpoint_timeout",
    "fsync",
    "full_page_writes",
    "max_connections",
    "max_wal_size",
    "shared_buffers",
    "synchronous_commit",
    "wal_level",
    "work_mem",
})


class BenchmarkError(Exception):
    """A configuration or evidence error that makes a run invalid."""


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="seconds")


def write_json(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + ".tmp")
    with temporary.open("w", encoding="utf-8", newline="\n") as stream:
        json.dump(value, stream, indent=2, sort_keys=True)
        stream.write("\n")
        stream.flush()
        os.fsync(stream.fileno())
    os.replace(temporary, path)


def redact(text: str, connection_string: str = "") -> str:
    if connection_string:
        text = text.replace(connection_string, "<redacted connection string>")
    text = re.sub(r"(?i)postgres(?:ql)?://[^\s\"'<>]+", "<redacted PostgreSQL URI>", text)
    text = re.sub(
        r"(?i)\b([\w.-]*(?:host|server|database|catalog|username|user\s*id|uid|password|passwd|pwd|token|secret|api[_-]?key|connection\s*string|connectionstring)\w*)\s*[:=]\s*(?:'[^']*'|\"[^\"]*\"|[^;,\s]+)",
        lambda match: f"{match.group(1)}=<omitted>",
        text,
    )
    text = re.sub(r"(?i)\bBearer\s+[^\s,;]+", "Bearer <omitted>", text)
    for private_value, replacement in (
        (str(Path.home()), "<HOME>"),
        (socket.gethostname(), "<HOST>"),
        (platform.node(), "<HOST>"),
    ):
        if private_value:
            text = text.replace(private_value, replacement)
    return text.replace(str(ROOT), "<REPOSITORY>")


def read_manifest() -> dict[str, object]:
    try:
        raw = MANIFEST_PATH.read_bytes()
        manifest = json.loads(raw)
    except (OSError, json.JSONDecodeError) as error:
        raise BenchmarkError("cannot read frozen manifest") from error
    digest = hashlib.sha256(raw).hexdigest()
    if digest != EXPECTED_MANIFEST_SHA256:
        raise BenchmarkError(f"frozen manifest SHA-256 mismatch: {digest}")
    if manifest.get("revision") != EXPECTED_REVISION:
        raise BenchmarkError("frozen manifest revision does not match the approved plan")
    if manifest.get("sdk", {}).get("version") != EXPECTED_SDK:
        raise BenchmarkError("frozen manifest SDK version does not match the approved plan")
    if manifest.get("configuration") != "Release" or manifest.get("project") != str(PROJECT_RELATIVE):
        raise BenchmarkError("frozen manifest project or configuration has changed")
    methods = manifest.get("methods")
    if not isinstance(methods, dict):
        raise BenchmarkError("frozen manifest has no method inventory")

    identities: dict[str, list[str]] = {}
    for cohort, class_name in EXPECTED_CLASSES.items():
        method_names = methods.get(class_name)
        if not isinstance(method_names, list) or len(method_names) != EXPECTED_CASE_COUNTS[cohort]:
            raise BenchmarkError(f"frozen {cohort} inventory has the wrong case count")
        if any(not isinstance(name, str) or not name for name in method_names):
            raise BenchmarkError(f"frozen {cohort} inventory contains an invalid method name")
        if len(set(method_names)) != len(method_names):
            raise BenchmarkError(f"frozen {cohort} inventory contains duplicate method names")
        identities[cohort] = sorted(f"{class_name}.{name}" for name in method_names)

    all_identities = identities["work"] + identities["flow"]
    if len(all_identities) != 34 or len(set(all_identities)) != 34:
        raise BenchmarkError("frozen manifest must contain 34 unique Work and Flow identities")
    if manifest.get("source_comparison", {}).get("database_setup", {}).get("empty_method") != (
        "FlowClient_StartRejectsANonAuthoritativeContextCodec"
    ):
        raise BenchmarkError("frozen manifest no longer identifies the empty Flow case")
    manifest["_identities"] = identities
    manifest["_sha256"] = digest
    return manifest


def parse_server_info(raw: bytes) -> dict[str, object]:
    try:
        info = json.loads(
            raw.decode("utf-8"),
            object_pairs_hook=unique_json_object,
        )
    except (UnicodeError, ValueError) as error:
        raise BenchmarkError("PostgreSQL server metadata is not valid UTF-8 JSON") from error
    if not isinstance(info, dict) or set(info) != {"version", "settings"}:
        raise BenchmarkError("server metadata must contain only version and settings")
    version = info["version"]
    settings = info["settings"]
    if not isinstance(version, str) or not re.fullmatch(r"\d+(?:\.\d+){0,2}(?:[-+][A-Za-z0-9._-]+)?", version):
        raise BenchmarkError("server metadata must contain a version such as 16.4")
    major = int(version.split(".", 1)[0])
    if major < 16:
        raise BenchmarkError(f"PostgreSQL {version} is below the required major version 16")
    if not isinstance(settings, dict) or set(settings) != SERVER_SETTING_KEYS:
        raise BenchmarkError("server metadata must contain exactly the ten approved PostgreSQL settings")
    normalized: dict[str, str] = {}
    for key, value in settings.items():
        if key not in SERVER_SETTING_KEYS:
            raise BenchmarkError("server metadata contains an unsupported setting key")
        if not isinstance(value, (str, int, float, bool)):
            raise BenchmarkError(f"server setting {key} must have a scalar value")
        value_text = str(value)
        if len(value_text) > 120 or not re.fullmatch(r"[A-Za-z0-9_.+-]+", value_text):
            raise BenchmarkError(f"server setting {key} has an invalid value")
        normalized[key] = value_text
    return {"version": version, "settings": dict(sorted(normalized.items()))}


def unique_json_object(pairs: list[tuple[str, object]]) -> dict[str, object]:
    result: dict[str, object] = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("duplicate JSON property")
        result[key] = value
    return result


def read_server_info(path: Path) -> dict[str, object]:
    try:
        return parse_server_info(path.read_bytes())
    except OSError as error:
        raise BenchmarkError("cannot read PostgreSQL server metadata JSON") from error


def preserve_server_info_input(path: Path, root: Path) -> tuple[dict[str, object], dict[str, str]]:
    try:
        raw = path.read_bytes()
    except OSError as error:
        raise BenchmarkError("cannot read PostgreSQL server metadata JSON") from error
    info = parse_server_info(raw)
    destination = root / "server-info.input.json"
    destination.write_bytes(raw)
    return info, {
        "file": destination.name,
        "origin": "caller-supplied --server-info input",
        "source_name": path.name,
        "sha256": hashlib.sha256(raw).hexdigest(),
    }


def required_acceptance_witnesses() -> dict[str, list[str]]:
    return {
        family: [f"{POSTGRES_TEST_NAMESPACE}.{class_name}.{method}" for method in methods]
        for family, (class_name, methods) in ACCEPTANCE_WITNESSES.items()
    }


def dotnet_executable(value: str | None) -> str:
    if value:
        candidate = Path(value).expanduser()
        if candidate.parent != Path(".") or candidate.is_absolute():
            if not candidate.is_file():
                raise BenchmarkError("the requested dotnet executable does not exist")
            return str(candidate.resolve())
        resolved = shutil.which(value)
    else:
        executable = "dotnet.exe" if os.name == "nt" else "dotnet"
        candidates = []
        if os.environ.get("DOTNET_ROOT"):
            candidates.append(Path(os.environ["DOTNET_ROOT"]) / executable)
        candidates.append(Path.home() / ".dotnet" / executable)
        resolved = next((str(path.resolve()) for path in candidates if path.is_file()), None)
        if not resolved:
            resolved = shutil.which("dotnet")
    if not resolved:
        raise BenchmarkError("dotnet was not found; pass --dotnet with the pinned SDK host")
    return resolved


def file_sha256(path: Path) -> str:
    try:
        digest = hashlib.sha256()
        with path.open("rb") as stream:
            for chunk in iter(lambda: stream.read(1024 * 1024), b""):
                digest.update(chunk)
        return digest.hexdigest()
    except OSError as error:
        raise BenchmarkError("the Release test assembly is unavailable") from error


def require_assembly_hash(expected: str, stage: str) -> None:
    if file_sha256(TEST_ASSEMBLY) != expected:
        raise BenchmarkError(f"Release test assembly SHA-256 changed {stage}")


def run_checked(command: list[str], *, env: dict[str, str] | None = None) -> subprocess.CompletedProcess[str]:
    try:
        return subprocess.run(
            command,
            cwd=ROOT,
            env=env,
            check=False,
            capture_output=True,
            text=True,
            timeout=120,
        )
    except (OSError, subprocess.TimeoutExpired) as error:
        raise BenchmarkError(f"could not run {Path(command[0]).name}") from error


def parse_discovery(output: str, cohort: str) -> list[str]:
    class_name = EXPECTED_CLASSES[cohort]
    return sorted(
        line.strip()
        for line in output.splitlines()
        if line.strip().startswith(class_name + ".")
    )


def class_test_filter(cohort: str) -> str:
    return f"FullyQualifiedName~{EXPECTED_CLASSES[cohort]}."


def acceptance_filter() -> str:
    return "|".join(f"FullyQualifiedName~{POSTGRES_TEST_NAMESPACE}.{name}." for name in ACCEPTANCE_CLASSES)


def discover_acceptance_rows(dotnet: str) -> list[str]:
    result = run_checked([
        dotnet, "test", str(PROJECT_RELATIVE), "--configuration", "Release",
        "--no-build", "--no-restore", "--list-tests", "--filter", acceptance_filter(),
    ])
    if result.returncode:
        raise BenchmarkError("could not discover the fixture acceptance cases in the measured Release build")
    lines = [line.strip() for line in result.stdout.splitlines()]
    rows = []
    for name in ACCEPTANCE_CLASSES:
        prefix = f"{POSTGRES_TEST_NAMESPACE}.{name}."
        cases = [line for line in lines if line.startswith(prefix)]
        if not cases:
            raise BenchmarkError(f"acceptance discovery returned no cases for {name}")
        rows.extend(cases)
    return sorted(rows)


def validate_environment(dotnet: str, manifest: dict[str, object]) -> dict[str, object]:
    if not PROJECT.is_file():
        raise BenchmarkError(f"test project is missing: {PROJECT_RELATIVE}")
    if not TEST_ASSEMBLY.is_file():
        raise BenchmarkError("Release test assembly is missing; build the PostgreSQL test project first")
    assembly_sha256 = file_sha256(TEST_ASSEMBLY)
    try:
        runner_settings = json.loads(RUNNER_CONFIG.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise BenchmarkError("cannot read xUnit runner configuration") from error
    if not isinstance(runner_settings, dict) or runner_settings.get("maxParallelThreads") != 2:
        raise BenchmarkError("xUnit worker cap must remain at the approved value of 2")

    version_result = run_checked([dotnet, "--version"])
    sdk_version = version_result.stdout.strip()
    if version_result.returncode != 0 or sdk_version != EXPECTED_SDK:
        detail = redact((version_result.stderr or version_result.stdout).strip(), os.environ.get(CONNECTION_VARIABLE, ""))
        raise BenchmarkError(f"pinned SDK check failed, expected {EXPECTED_SDK}, observed {sdk_version or 'unknown'} {detail}")

    discovery_outputs: dict[str, str] = {}
    for cohort, class_name in EXPECTED_CLASSES.items():
        command = [
            dotnet,
            "test",
            str(PROJECT_RELATIVE),
            "--configuration",
            "Release",
            "--no-build",
            "--no-restore",
            "--list-tests",
            "--filter",
            class_test_filter(cohort),
        ]
        result = run_checked(command)
        safe_output = redact(result.stdout + result.stderr, os.environ.get(CONNECTION_VARIABLE, ""))
        if result.returncode != 0:
            raise BenchmarkError(f"{cohort} discovery failed: {safe_output[-2000:]}")
        actual = parse_discovery(result.stdout, cohort)
        expected = manifest["_identities"][cohort]
        if actual != expected:
            missing = sorted(set(expected) - set(actual))
            unexpected = sorted(set(actual) - set(expected))
            raise BenchmarkError(
                f"{cohort} discovery differs from the frozen manifest, missing={missing}, unexpected={unexpected}"
            )
        discovery_outputs[cohort] = safe_output

    info_result = run_checked([dotnet, "--info"])
    if info_result.returncode != 0:
        raise BenchmarkError("dotnet --info failed while recording SDK provenance")
    safe_info = redact(info_result.stdout, os.environ.get(CONNECTION_VARIABLE, ""))
    require_assembly_hash(assembly_sha256, "during SDK and test discovery validation")
    return {
        "sdk_version": sdk_version,
        "dotnet_path": Path(dotnet).name,
        "release_assembly_sha256": assembly_sha256,
        "dotnet_info": safe_info,
        "worker_cap": 2,
        "discovery": discovery_outputs,
    }


def validate_acceptance_trx(
    path: Path,
    *,
    expected_rows: list[str],
    expected_assembly_sha256: str | None = None,
) -> dict[str, object]:
    try:
        raw = path.read_bytes()
    except OSError as error:
        raise BenchmarkError("cannot read the independent acceptance TRX receipt") from error
    try:
        root = ET.fromstring(raw)
    except ET.ParseError as error:
        raise BenchmarkError("TRX is unreadable") from error
    evidence = parse_trx_root(root)
    if evidence["passed"] == 0:
        raise BenchmarkError("acceptance TRX contains no passing test results")
    identities = set(evidence["identities"])
    witnesses = required_acceptance_witnesses()
    missing = {
        family: [identity for identity in expected if identity not in identities]
        for family, expected in witnesses.items()
    }
    missing = {family: names for family, names in missing.items() if names}
    if missing:
        details = "; ".join(f"{family}: {', '.join(names)}" for family, names in missing.items())
        raise BenchmarkError(f"acceptance TRX is missing required fixture witnesses: {details}")
    actual_rows = Counter(result.get("testName", "") for result in root.findall(".//{*}UnitTestResult"))
    if not expected_rows or actual_rows != Counter(expected_rows):
        raise BenchmarkError("acceptance TRX cases differ from the Release fixture discovery, including theory rows")
    receipt: dict[str, object] = {
        "status": "PASS",
        "source_sha256": hashlib.sha256(raw).hexdigest(),
        "passed": evidence["passed"],
        "failed": evidence["failed"],
        "skipped": evidence["skipped"],
        "witnesses": witnesses,
    }
    if expected_assembly_sha256 is not None:
        if not re.fullmatch(r"[0-9a-fA-F]{64}", expected_assembly_sha256):
            raise BenchmarkError("measured Release assembly SHA-256 is malformed")

        def local_name(element: ET.Element) -> str:
            return element.tag.rsplit("}", 1)[-1]

        definitions: dict[str, str] = {}
        for element in root.iter():
            if local_name(element) != "UnitTest":
                continue
            test_id = element.get("id")
            method = next((child for child in element if local_name(child) == "TestMethod"), None)
            class_name = method.get("className") if method is not None else None
            method_name = method.get("name") if method is not None else None
            if test_id and class_name and method_name:
                definitions[test_id] = f"{class_name}.{method_name}"

        metadata_results = [
            result
            for result in root.iter()
            if local_name(result) == "UnitTestResult"
            and definitions.get(result.get("testId", "")) == SERVER_METADATA_TEST
        ]
        if len(metadata_results) != 1:
            raise BenchmarkError("acceptance TRX must contain exactly one named server metadata result")

        fact_lines = [
            line.strip()
            for element in metadata_results[0].iter()
            if local_name(element) == "StdOut"
            for line in (element.text or "").splitlines()
        ]
        metadata_lines = [line for line in fact_lines if line.startswith(SERVER_METADATA_PREFIX)]
        assembly_lines = [line for line in fact_lines if line.startswith("FIXTURE_RELEASE_SHA256 ")]
        if len(metadata_lines) != 1:
            raise BenchmarkError("acceptance TRX must contain one named server metadata fact")
        if len(assembly_lines) != 1:
            raise BenchmarkError("acceptance TRX must contain one named Release assembly SHA-256 fact")
        try:
            observed_server = parse_server_info(
                metadata_lines[0][len(SERVER_METADATA_PREFIX):].encode("utf-8")
            )
        except BenchmarkError as error:
            raise BenchmarkError("acceptance TRX contains invalid named server metadata") from error
        observed_assembly_sha256 = assembly_lines[0].removeprefix("FIXTURE_RELEASE_SHA256 ")
        if not re.fullmatch(r"[0-9a-fA-F]{64}", observed_assembly_sha256):
            raise BenchmarkError("acceptance TRX contains a malformed Release assembly SHA-256 fact")
        if observed_assembly_sha256.casefold() != expected_assembly_sha256.casefold():
            raise BenchmarkError("acceptance TRX Release assembly SHA-256 does not match the measured binary")
        receipt["server_metadata"] = observed_server
        receipt["release_assembly_sha256"] = observed_assembly_sha256.lower()
    return receipt


def machine_details() -> dict[str, object]:
    try:
        load_1m = os.getloadavg()[0]
    except (AttributeError, OSError):
        load_1m = None
    return {
        "os": f"{platform.system()} {platform.release()}",
        "architecture": platform.machine(),
        "logical_cpu_count": os.cpu_count(),
        "load_average_1m": load_1m,
        "captured_at": utc_now(),
    }


def parse_trx(
    path: Path,
    cohort: str | None = None,
    *,
    server_metadata: dict[str, object] | None = None,
) -> dict[str, object]:
    try:
        root = ET.parse(path).getroot()
    except (OSError, ET.ParseError) as error:
        raise BenchmarkError("TRX is unreadable") from error

    return parse_trx_root(root, cohort, server_metadata=server_metadata)


def parse_trx_root(
    root: ET.Element,
    cohort: str | None = None,
    *,
    server_metadata: dict[str, object] | None = None,
) -> dict[str, object]:

    local_name = lambda element: element.tag.rsplit("}", 1)[-1]
    definitions: dict[str, str] = {}
    for element in root.iter():
        if local_name(element) != "UnitTest":
            continue
        test_id = element.get("id")
        method = next((child for child in element if local_name(child) == "TestMethod"), None)
        class_name = method.get("className") if method is not None else None
        method_name = method.get("name") if method is not None else None
        if not test_id or not class_name or not method_name or test_id in definitions:
            raise BenchmarkError("TRX contains an incomplete or duplicated test definition")
        definitions[test_id] = f"{class_name}.{method_name}"

    results = [element for element in root.iter() if local_name(element) == "UnitTestResult"]
    result_ids = [element.get("testId") for element in results]
    if any(not test_id for test_id in result_ids) or len(set(result_ids)) != len(result_ids):
        raise BenchmarkError("TRX test result IDs are missing or duplicated")
    if set(result_ids) != set(definitions):
        raise BenchmarkError("TRX test definitions and complete result ID set do not match")
    outcomes = [element.get("outcome", "") for element in results]
    if any(outcome != "Passed" for outcome in outcomes):
        raise BenchmarkError("TRX contains a failed, skipped, or incomplete outcome")
    summaries = [element for element in root.iter() if local_name(element) == "ResultSummary"]
    if len(summaries) != 1 or summaries[0].get("outcome") != "Completed":
        raise BenchmarkError("TRX must contain one completed result summary")
    counters = [element for element in summaries[0] if local_name(element) == "Counters"]
    if len(counters) != 1:
        raise BenchmarkError("TRX completed result summary has no unique counter set")
    parsed_counters: dict[str, int] = {}
    for name in ("total", "executed", "passed", "failed", "notExecuted"):
        try:
            value = int(counters[0].attrib[name])
        except (KeyError, ValueError) as error:
            raise BenchmarkError(f"TRX result summary counter {name} is missing or invalid") from error
        if value < 0:
            raise BenchmarkError("TRX result summary counters cannot be negative")
        parsed_counters[name] = value
    if (
        parsed_counters["total"] != len(definitions)
        or parsed_counters["executed"] != len(results)
        or parsed_counters["passed"] != len(results)
        or parsed_counters["failed"] != 0
        or parsed_counters["notExecuted"] != 0
    ):
        raise BenchmarkError("TRX result summary does not report a complete pass with zero failures and skips")
    for name in ("error", "timeout", "aborted", "inconclusive", "passedButRunAborted", "notRunnable", "disconnected"):
        raw_value = counters[0].get(name)
        if raw_value is not None:
            try:
                value = int(raw_value)
            except ValueError as error:
                raise BenchmarkError(f"TRX result summary counter {name} is invalid") from error
            if value != 0:
                raise BenchmarkError("TRX result summary reports a failed, aborted, or incomplete test")
    actual = sorted(definitions[test_id] for test_id in result_ids)
    evidence: dict[str, object] = {
        "passed": len(results),
        "result_ids": sorted(result_ids),
        "identities": actual,
        "failed": parsed_counters["failed"],
        "skipped": parsed_counters["notExecuted"],
    }
    if server_metadata is not None:
        if Counter(actual) != Counter([SERVER_METADATA_TEST]):
            raise BenchmarkError("server-check TRX must contain exactly one metadata probe result")
        result = results[0]
        output_lines = [
            line.strip()
            for element in result.iter()
            if local_name(element) == "StdOut"
            for line in (element.text or "").splitlines()
            if line.strip().startswith(SERVER_METADATA_PREFIX)
        ]
        if len(output_lines) != 1:
            raise BenchmarkError("server-check TRX must contain one metadata output line for the passing probe")
        try:
            observed = parse_server_info(output_lines[0][len(SERVER_METADATA_PREFIX):].encode("utf-8"))
        except BenchmarkError as error:
            raise BenchmarkError("server-check TRX contains invalid metadata probe output") from error
        if observed != server_metadata:
            raise BenchmarkError("server-check metadata does not match the supplied server metadata JSON")
        evidence["server_metadata"] = observed
        return evidence
    if cohort is None:
        return evidence

    expected = read_manifest()["_identities"][cohort]
    if Counter(actual) != Counter(expected):
        raise BenchmarkError("TRX pass class/method multiplicities do not exactly match the frozen manifest")
    collectors = [
        element
        for element in root.iter()
        if local_name(element) == "Collector"
        and "xplat code coverage" in " ".join(
            " ".join((element.get("friendlyName", ""), element.get("collectorDisplayName", "")))
            .casefold()
            .split()
        )
    ]
    deployments = [element for element in root.iter() if local_name(element) == "Deployment"]
    if len(collectors) != 1 or len(deployments) != 1:
        raise BenchmarkError("TRX must associate one XPlat Code Coverage collector and deployment root with this process")
    attachments = [element for element in collectors[0].iter() if local_name(element) == "A"]
    deployment_root = deployments[0].get("runDeploymentRoot", "")
    href = attachments[0].get("href", "") if len(attachments) == 1 else ""
    if not deployment_root or not href or "\\" in deployment_root + href:
        raise BenchmarkError("TRX collector attachment is missing or is not a safe relative path")
    deployment_path = Path(deployment_root)
    attachment_path = Path(href)
    if (
        deployment_path.is_absolute()
        or attachment_path.is_absolute()
        or ".." in deployment_path.parts
        or ".." in attachment_path.parts
        or attachment_path.name != "coverage.cobertura.xml"
    ):
        raise BenchmarkError("TRX collector attachment is missing or is not a safe relative coverage path")
    evidence["collector_attachment"] = (deployment_path / "In" / attachment_path).as_posix()
    return evidence


def validate_server_check_trx(path: Path, expected: dict[str, object]) -> dict[str, object]:
    evidence = parse_trx(path, server_metadata=expected)
    return {
        "status": "PASS",
        "source": "server-check/server-check.trx",
        "source_sha256": file_sha256(path),
        "passed": evidence["passed"],
        "identity": SERVER_METADATA_TEST,
        "observed": evidence["server_metadata"],
    }


def parse_coverage(path: Path) -> dict[str, int]:
    try:
        root = ET.parse(path).getroot()
    except (OSError, ET.ParseError) as error:
        raise BenchmarkError("Cobertura coverage output is unreadable") from error
    if root.tag.rsplit("}", 1)[-1] != "coverage":
        raise BenchmarkError("coverage output is not Cobertura XML")
    try:
        lines_valid = int(root.attrib["lines-valid"])
        lines_covered = int(root.attrib["lines-covered"])
        branches_valid = int(root.attrib.get("branches-valid", "0"))
        branches_covered = int(root.attrib.get("branches-covered", "0"))
    except (KeyError, ValueError) as error:
        raise BenchmarkError("Cobertura summary counters are missing or malformed") from error
    if min(lines_valid, lines_covered, branches_valid, branches_covered) < 0:
        raise BenchmarkError("Cobertura summary counters cannot be negative")
    if lines_covered > lines_valid or branches_covered > branches_valid:
        raise BenchmarkError("Cobertura covered counters exceed their valid totals")
    if lines_valid == 0:
        raise BenchmarkError("Cobertura output has no valid lines")
    return {
        "lines_valid": lines_valid,
        "lines_covered": lines_covered,
        "branches_valid": branches_valid,
        "branches_covered": branches_covered,
    }


def parse_timing_csv(path: Path, cohort: str, arm: str) -> dict[str, object]:
    if not path.is_file():
        raise BenchmarkError("fixture timing CSV was not created")
    try:
        with path.open("r", encoding="utf-8-sig", newline="") as stream:
            reader = csv.DictReader(stream)
            if reader.fieldnames != ["phase", "elapsed_ms"]:
                raise BenchmarkError("fixture timing CSV must have phase,elapsed_ms columns")
            rows = list(reader)
    except (OSError, UnicodeError, csv.Error) as error:
        raise BenchmarkError(f"fixture timing CSV is unreadable: {error}") from error
    if not rows:
        raise BenchmarkError("fixture timing CSV contains no phase rows")
    phases: dict[str, list[float]] = {}
    for row in rows:
        phase = row.get("phase")
        raw_elapsed = row.get("elapsed_ms")
        if phase not in {"database_setup", "database_teardown", "template_prepare", "template_teardown"}:
            raise BenchmarkError(f"fixture timing CSV contains an unknown phase: {phase}")
        try:
            elapsed = float(raw_elapsed or "")
        except ValueError as error:
            raise BenchmarkError("fixture timing CSV contains a nonnumeric elapsed_ms value") from error
        if not math.isfinite(elapsed) or elapsed < 0:
            raise BenchmarkError("fixture timing CSV elapsed_ms values must be finite and nonnegative")
        phases.setdefault(phase, []).append(elapsed)

    expected = {
        "database_setup": EXPECTED_CASE_COUNTS[cohort],
        "database_teardown": EXPECTED_CASE_COUNTS[cohort],
    }
    if arm == "template":
        expected.update({"template_prepare": 1, "template_teardown": 1})
    if {phase: len(values) for phase, values in phases.items()} != expected:
        raise BenchmarkError(
            f"fixture timing phase counts differ from the expected process shape, "
            f"expected={expected}, observed={ {phase: len(values) for phase, values in phases.items()} }"
        )
    try:
        elapsed_totals = {phase: math.fsum(values) for phase, values in phases.items()}
    except OverflowError as error:
        raise BenchmarkError("fixture timing phase totals must be finite") from error
    if any(not math.isfinite(value) for value in elapsed_totals.values()):
        raise BenchmarkError("fixture timing phase totals must be finite")
    return {
        "counts": {phase: len(values) for phase, values in sorted(phases.items())},
        "elapsed_ms": {phase: round(value, 3) for phase, value in sorted(elapsed_totals.items())},
    }


def validate_coverage_attachment(directory: Path, trx: Path, trx_data: dict[str, object]) -> tuple[Path, list[str]]:
    if trx.parent.resolve() != directory.resolve():
        raise BenchmarkError("TRX result is outside its process results directory")
    coverage = (directory / str(trx_data["collector_attachment"])).resolve()
    try:
        coverage.relative_to(directory.resolve())
    except ValueError as error:
        raise BenchmarkError("TRX collector attachment escapes the process results directory") from error
    if not coverage.is_file():
        raise BenchmarkError("TRX-associated XPlat coverage attachment is missing")

    export_copies: list[Path] = []
    for candidate in directory.rglob("coverage.cobertura.xml"):
        if candidate.resolve() == coverage:
            continue
        relative = candidate.relative_to(directory)
        if len(relative.parts) != 2 or not UUID_DIRECTORY.fullmatch(relative.parts[0]):
            raise BenchmarkError("process contains an unexpected unassociated coverage file")
        export_copies.append(candidate)
    if len(export_copies) > 1:
        raise BenchmarkError("process contains multiple UUID coverage export copies")
    if export_copies and file_sha256(export_copies[0]) != file_sha256(coverage):
        raise BenchmarkError("UUID coverage export copy differs from the TRX-associated attachment")
    return coverage, [str(path.relative_to(directory)) for path in export_copies]


def validate_process_evidence(directory: Path, cohort: str, arm: str) -> dict[str, object]:
    trx = directory / "results.trx"
    if not trx.is_file():
        raise BenchmarkError("process TRX result was not created in its results directory")
    trx_data = parse_trx(trx, cohort)
    coverage, export_copies = validate_coverage_attachment(directory, trx, trx_data)
    coverage_data = parse_coverage(coverage)
    timing_data = parse_timing_csv(directory / "fixture-timings.csv", cohort, arm)
    return {
        "trx": str(trx.relative_to(directory)),
        "coverage": str(coverage.relative_to(directory)),
        "timing_csv": "fixture-timings.csv",
        "coverage_export_copies": export_copies,
        "trx_evidence": trx_data,
        "coverage_evidence": coverage_data,
        "timing_evidence": timing_data,
    }


def test_command(dotnet: str, cohort: str, results_dir: Path) -> list[str]:
    return [
        dotnet,
        "test",
        str(PROJECT_RELATIVE),
        "--configuration",
        "Release",
        "--no-build",
        "--no-restore",
        "--filter",
        class_test_filter(cohort),
        "--results-directory",
        str(results_dir),
        "--logger",
        "trx;LogFileName=results.trx",
        "--collect",
        "XPlat Code Coverage",
    ]


def server_check_command(dotnet: str, results_dir: Path) -> list[str]:
    return [
        dotnet,
        "test",
        str(PROJECT_RELATIVE),
        "--configuration",
        "Release",
        "--no-build",
        "--no-restore",
        "--filter",
        f"FullyQualifiedName={SERVER_METADATA_TEST}",
        "--results-directory",
        str(results_dir),
        "--logger",
        "trx;LogFileName=server-check.trx",
    ]


def capture_server_metadata(
    dotnet: str,
    root: Path,
    expected: dict[str, object],
    assembly_sha256: str,
) -> dict[str, object]:
    directory = root / "server-check"
    directory.mkdir(parents=True, exist_ok=False)
    command = server_check_command(dotnet, directory)
    require_assembly_hash(assembly_sha256, "before server metadata preflight")
    environment = os.environ.copy()
    connection_string = environment.get(CONNECTION_VARIABLE, "")
    environment.pop(FIXTURE_VARIABLE, None)
    environment.pop(TIMING_VARIABLE, None)
    started_at = utc_now()
    try:
        process = subprocess.run(
            command,
            cwd=ROOT,
            env=environment,
            check=False,
            capture_output=True,
            text=True,
            timeout=120,
        )
    except (OSError, subprocess.TimeoutExpired) as error:
        raise BenchmarkError("PostgreSQL server metadata preflight could not complete") from error
    finished_at = utc_now()
    (directory / "stdout.log").write_text(redact(process.stdout, connection_string), encoding="utf-8")
    (directory / "stderr.log").write_text(redact(process.stderr, connection_string), encoding="utf-8")
    actual_hash = file_sha256(TEST_ASSEMBLY)
    if actual_hash != assembly_sha256:
        raise BenchmarkError("Release test assembly SHA-256 changed during server metadata preflight")
    if process.returncode != 0:
        detail = redact((process.stderr or process.stdout).strip(), connection_string)
        write_json(directory / "receipt.json", {
            "status": "INVALID",
            "exit_code": process.returncode,
            "started_at": started_at,
            "finished_at": finished_at,
            "release_assembly_sha256": actual_hash,
        })
        raise BenchmarkError(f"PostgreSQL server metadata preflight failed: {detail[-1000:]}")
    trx = directory / "server-check.trx"
    if not trx.is_file():
        write_json(directory / "receipt.json", {
            "status": "INVALID",
            "exit_code": process.returncode,
            "started_at": started_at,
            "finished_at": finished_at,
            "release_assembly_sha256": actual_hash,
        })
        raise BenchmarkError("PostgreSQL server metadata preflight did not create its TRX receipt")
    try:
        evidence = validate_server_check_trx(trx, expected)
    except BenchmarkError as error:
        write_json(directory / "receipt.json", {
            "status": "INVALID",
            "exit_code": process.returncode,
            "started_at": started_at,
            "finished_at": finished_at,
            "source_sha256": file_sha256(trx),
            "release_assembly_sha256": actual_hash,
        })
        raise error
    receipt = {
        **evidence,
        "origin": "StdOut from the fully qualified metadata test in its UnitTestResult",
        "started_at": started_at,
        "finished_at": finished_at,
        "exit_code": process.returncode,
        "expected": expected,
        "release_assembly_sha256": actual_hash,
    }
    write_json(directory / "receipt.json", receipt)
    return receipt


def run_process(
    dotnet: str,
    root: Path,
    phase: str,
    arm: str,
    cohort: str,
    assembly_sha256: str,
) -> dict[str, object]:
    directory = root / phase / arm / cohort
    directory.mkdir(parents=True, exist_ok=False)
    result_path = directory / "process.json"
    timing_path = directory / "fixture-timings.csv"
    results_path = directory
    environment = os.environ.copy()
    connection_string = environment.get(CONNECTION_VARIABLE, "")
    environment[FIXTURE_VARIABLE] = arm
    environment[TIMING_VARIABLE] = str(timing_path)
    command = test_command(dotnet, cohort, results_path)
    require_assembly_hash(assembly_sha256, f"before {phase}/{arm}/{cohort}")
    started_at = utc_now()
    started = time.perf_counter_ns()
    try:
        process = subprocess.run(
            command,
            cwd=ROOT,
            env=environment,
            check=False,
            capture_output=True,
            text=True,
        )
        return_code = process.returncode
        stdout = process.stdout
        stderr = process.stderr
    except OSError as error:
        return_code = 127
        stdout = ""
        stderr = str(error)
    elapsed_nanoseconds = time.perf_counter_ns() - started
    elapsed_seconds = elapsed_nanoseconds / 1_000_000_000
    assembly_after = file_sha256(TEST_ASSEMBLY)
    (directory / "stdout.log").write_text(redact(stdout, connection_string), encoding="utf-8")
    (directory / "stderr.log").write_text(redact(stderr, connection_string), encoding="utf-8")
    record: dict[str, object] = {
        "phase": phase,
        "arm": arm,
        "cohort": cohort,
        "started_at": started_at,
        "finished_at": utc_now(),
        "wall_seconds": elapsed_seconds,
        "wall_nanoseconds": elapsed_nanoseconds,
        "release_assembly_sha256_before": assembly_sha256,
        "release_assembly_sha256_after": assembly_after,
        "exit_code": return_code,
        "status": "VALID",
        "command": [Path(command[0]).name, *["<RESULTS_DIR>" if item == str(results_path) else item for item in command[1:]]],
        "result_files": {},
    }
    problems: list[str] = []
    if return_code != 0:
        problems.append(f"dotnet test exited with code {return_code}")
    if not math.isfinite(elapsed_seconds) or elapsed_seconds <= 0:
        problems.append("process wall time must be finite and greater than zero")
    if assembly_after != assembly_sha256:
        problems.append("Release test assembly SHA-256 changed during the process")
    try:
        require_assembly_hash(assembly_sha256, f"after final {phase}/{arm}/{cohort} process evidence validation")
    except BenchmarkError as error:
        problems.append(str(error))
    try:
        evidence = validate_process_evidence(directory, cohort, arm)
        record["evidence"] = evidence
        record["result_files"] = {
            name: evidence[name]
            for name in ("trx", "coverage")
        }
        record["phase_totals"] = evidence["timing_evidence"]
    except BenchmarkError as error:
        problems.append(str(error))
    if problems:
        record["status"] = "INVALID"
        record["problems"] = problems
    write_json(result_path, record)
    return record


def arm_snapshot(root: Path, phase: str, arm: str, boundary: str, assembly_sha256: str) -> None:
    require_assembly_hash(assembly_sha256, f"at {phase}/{arm} {boundary}")
    details = machine_details()
    details["release_assembly_sha256"] = assembly_sha256
    write_json(root / phase / arm / f"boundary-{boundary}.json", details)


def run_arm(
    dotnet: str,
    root: Path,
    phase: str,
    arm: str,
    assembly_sha256: str,
) -> list[dict[str, object]]:
    arm_root = root / phase / arm
    arm_root.mkdir(parents=True, exist_ok=False)
    arm_snapshot(root, phase, arm, "start", assembly_sha256)
    records = [
        run_process(dotnet, root, phase, arm, cohort, assembly_sha256)
        for cohort in ("work", "flow")
    ]
    arm_snapshot(root, phase, arm, "end", assembly_sha256)
    return records


def percentage_gain(fresh_seconds: float, template_seconds: float) -> float:
    if (
        not math.isfinite(fresh_seconds)
        or fresh_seconds <= 0
        or not math.isfinite(template_seconds)
        or template_seconds <= 0
    ):
        raise BenchmarkError("fresh and template process wall times must be finite and greater than zero")
    return 100.0 * (fresh_seconds - template_seconds) / fresh_seconds


def strictly_exceeds_twice_mad(median_gain: Fraction, mad: Fraction) -> bool:
    return median_gain > 2 * mad


def wall_time_fraction(record: dict[str, object]) -> Fraction:
    try:
        seconds = float(record["wall_seconds"])
    except (KeyError, TypeError, ValueError, OverflowError) as error:
        raise ValueError("wall_seconds must be numeric") from error
    if not math.isfinite(seconds) or seconds <= 0:
        raise ValueError("wall_seconds must be finite and positive")
    nanoseconds = record.get("wall_nanoseconds")
    if "wall_nanoseconds" in record:
        if not isinstance(nanoseconds, int) or isinstance(nanoseconds, bool) or nanoseconds <= 0:
            raise ValueError("wall_nanoseconds must be a positive integer")
        exact_seconds = Fraction(nanoseconds, 1_000_000_000)
        try:
            rounded_seconds = float(exact_seconds)
        except OverflowError as error:
            raise ValueError("wall_nanoseconds must represent a finite wall time") from error
        if not math.isfinite(rounded_seconds) or seconds != rounded_seconds:
            raise ValueError("wall_seconds does not match wall_nanoseconds")
        return exact_seconds
    return Fraction(str(record["wall_seconds"]))


def analyze_pairs(pairs: list[dict[str, object]]) -> dict[str, object]:
    if len(pairs) != 5 or any(not isinstance(pair, dict) or pair.get("status") != "VALID" for pair in pairs):
        return {"status": "INVALID", "reasons": ["five complete valid measured pairs are required"]}
    pair_rows: list[dict[str, object]] = []
    gains: list[Fraction] = []
    cohort_gains: dict[str, list[Fraction]] = {"work": [], "flow": []}
    for pair in pairs:
        arms = pair.get("arms")
        if not isinstance(arms, dict) or set(arms) != {"fresh", "template"}:
            return {"status": "INVALID", "reasons": ["each pair must contain fresh and template arms"]}
        arm_totals: dict[str, dict[str, Fraction]] = {}
        for arm in ("fresh", "template"):
            arm_records = arms[arm]
            if not isinstance(arm_records, list) or any(
                not isinstance(record, dict) or record.get("status") != "VALID" for record in arm_records
            ):
                return {"status": "INVALID", "reasons": ["a measured process did not produce complete valid evidence"]}
            if len(arm_records) != 2 or {record.get("cohort") for record in arm_records} != {"work", "flow"}:
                return {"status": "INVALID", "reasons": ["each arm must contain Work and Flow process evidence"]}
            try:
                arm_totals[arm] = {record["cohort"]: wall_time_fraction(record) for record in arm_records}
            except (ValueError, KeyError, TypeError, OverflowError):
                return {"status": "INVALID", "reasons": ["process wall times must be finite positive numbers"]}
        fresh_total_fraction = sum(arm_totals["fresh"].values(), Fraction())
        template_total_fraction = sum(arm_totals["template"].values(), Fraction())
        gain_fraction = 100 * (fresh_total_fraction - template_total_fraction) / fresh_total_fraction
        try:
            fresh_total = float(fresh_total_fraction)
            template_total = float(template_total_fraction)
            gain = float(gain_fraction)
        except OverflowError:
            return {"status": "INVALID", "reasons": ["pair wall totals and gain must be finite"]}
        if not all(math.isfinite(value) for value in (fresh_total, template_total, gain)):
            return {"status": "INVALID", "reasons": ["pair wall totals and gain must be finite"]}
        gains.append(gain_fraction)
        row: dict[str, object] = {
            "pair": pair["pair"],
            "fresh_seconds": fresh_total,
            "template_seconds": template_total,
            "seconds_saved": fresh_total - template_total,
            "gain_percent": gain,
            "cohorts": {},
        }
        for cohort in ("work", "flow"):
            cohort_gain_fraction = 100 * (
                arm_totals["fresh"][cohort] - arm_totals["template"][cohort]
            ) / arm_totals["fresh"][cohort]
            cohort_gain = float(cohort_gain_fraction)
            cohort_gains[cohort].append(cohort_gain_fraction)
            row["cohorts"][cohort] = {
                "fresh_seconds": float(arm_totals["fresh"][cohort]),
                "template_seconds": float(arm_totals["template"][cohort]),
                "gain_percent": cohort_gain,
            }
        pair_rows.append(row)

    median_gain_fraction = statistics.median(gains)
    mad_fraction = statistics.median([abs(value - median_gain_fraction) for value in gains])
    median_gain = float(median_gain_fraction)
    mad = float(mad_fraction)
    veto_counts = {
        cohort: sum(gain <= -5 for gain in values)
        for cohort, values in cohort_gains.items()
    }
    checks = {
        "median_gain_at_least_5_percent": median_gain_fraction >= 5,
        "median_gain_exceeds_twice_mad": strictly_exceeds_twice_mad(median_gain_fraction, mad_fraction),
        "no_cohort_slowdown_veto": all(count < 3 for count in veto_counts.values()),
    }
    status = "GO" if all(checks.values()) else "NO_GO"
    reasons = [name for name, passed in checks.items() if not passed]
    return {
        "status": status,
        "eligible": status == "GO",
        "pairs": pair_rows,
        "median_gain_percent": median_gain,
        "median_absolute_deviation_percent": mad,
        "cohort_veto_counts": veto_counts,
        "checks": checks,
        "reasons": reasons,
    }


def write_summary(root: Path, result: dict[str, object]) -> None:
    lines = [
        "# Issue #931 PostgreSQL fixture benchmark",
        "",
        f"Verdict: **{result.get('status', 'INVALID')}**",
        "",
        f"Eligible for adoption: **{str(bool(result.get('eligible', False))).lower()}**",
    ]
    server = result.get("server")
    if isinstance(server, dict):
        lines.extend(["", f"PostgreSQL {server['version']}", ""])
        lines.extend(f"- `{key}` {value}" for key, value in server["settings"].items())
    if "median_gain_percent" in result:
        lines.extend(
            [
                f"Median gain: **{result['median_gain_percent']:.2f}%**",
                f"Median absolute deviation: **{result['median_absolute_deviation_percent']:.2f}%**",
                "",
                "| Pair | Fresh seconds | Template seconds | Seconds saved | Gain | Work gain | Flow gain |",
                "|---:|---:|---:|---:|---:|---:|---:|",
            ]
        )
        for pair in result["pairs"]:
            lines.append(
                f"| {pair['pair']} | {pair['fresh_seconds']:.3f} | {pair['template_seconds']:.3f} | "
                f"{pair['seconds_saved']:.3f} | {pair['gain_percent']:.2f}% | "
                f"{pair['cohorts']['work']['gain_percent']:.2f}% | "
                f"{pair['cohorts']['flow']['gain_percent']:.2f}% |"
            )
        lines.extend(
            [
                "",
                f"Per-cohort slowdown veto counts: Work {result['cohort_veto_counts']['work']}/5, "
                f"Flow {result['cohort_veto_counts']['flow']}/5.",
            ]
        )
    reasons = result.get("reasons", [])
    if reasons:
        lines.extend(["", "Decision details: " + ", ".join(reasons) + "."])
    lines.extend(["", "Raw process evidence and inventory are linked from [index.md](index.md).", ""])
    (root / "summary.md").write_text("\n".join(lines), encoding="utf-8")
    write_json(root / "summary.json", result)


def build_index(root: Path, records: list[dict[str, object]], status: str) -> None:
    lines = [
        "# Issue #931 benchmark evidence index",
        "",
        f"Run status: **{status}**",
        "",
        "The copied inventory and provenance are in [inventory/](inventory/).",
        "",
        "| Phase | Arm | Cohort | Status | Wall seconds | Evidence |",
        "|---|---|---|---|---:|---|",
    ]
    for record in records:
        directory = Path(str(record["directory"]))
        links = [f"[receipt]({directory.as_posix()}/process.json)"]
        for name in ("stdout.log", "stderr.log"):
            links.append(f"[{name}]({directory.as_posix()}/{name})")
        for field, label in (("trx", "TRX"), ("coverage", "coverage")):
            evidence = record.get("result_files", {}).get(field)
            if evidence:
                links.append(f"[{label}]({directory.as_posix()}/{evidence})")
        timing_csv = record.get("evidence", {}).get("timing_csv")
        if timing_csv:
            links.append(f"[timing CSV]({directory.as_posix()}/{timing_csv})")
        lines.append(
            f"| {record['phase']} | {record['arm']} | {record['cohort']} | {record['status']} | "
            f"{record['wall_seconds']:.3f} | {' · '.join(links)} |"
        )
    run_evidence = (
        ("acceptance.trx", "acceptance TRX"),
        ("server-check/server-check.trx", "server-check TRX"),
        ("server-check/receipt.json", "server-check receipt"),
        ("server-info.input.json", "server-info input"),
        ("environment.json", "environment"),
    )
    available_evidence = [
        f"[{label}]({relative_path})"
        for relative_path, label in run_evidence
        if (root / relative_path).is_file()
    ]
    if available_evidence:
        lines.extend(["", "Run evidence", ""])
        lines.extend(f"- {link}" for link in available_evidence)
    lines.extend(["", "See [summary.md](summary.md) for the redacted decision and paired timings.", ""])
    (root / "index.md").write_text("\n".join(lines), encoding="utf-8")


def copy_inventory(root: Path, manifest: dict[str, object]) -> None:
    destination = root / "inventory"
    destination.mkdir(parents=True, exist_ok=False)
    for name in ("manifest.json", "provenance.txt", "work-discovery.txt", "flow-discovery.txt"):
        source = INVENTORY_DIR / name
        if source.is_file():
            shutil.copyfile(source, destination / name)
    write_json(destination / "manifest-verification.json", {"sha256": manifest["_sha256"], "status": "PASS"})


def protocol(args: argparse.Namespace) -> int:
    try:
        root = args.results_root.expanduser().absolute()
        root.mkdir(parents=True, exist_ok=False)
    except FileExistsError as error:
        raise BenchmarkError("results root already exists, preserving existing evidence") from error
    except (OSError, RuntimeError) as error:
        raise BenchmarkError("cannot create the results root; choose a new writable directory") from error
    records: list[dict[str, object]] = []
    status = "INVALID"
    try:
        manifest = read_manifest()
        connection_string = os.environ.get(CONNECTION_VARIABLE, "")
        if not connection_string.strip():
            raise BenchmarkError(f"{CONNECTION_VARIABLE} must be supplied through secure environment injection")
        server_info, declared_server = preserve_server_info_input(args.server_info.expanduser(), root)
        acceptance_input = args.acceptance_trx.expanduser()
        retained_acceptance = root / "acceptance.trx"
        try:
            shutil.copyfile(acceptance_input, retained_acceptance)
        except OSError as error:
            raise BenchmarkError("cannot retain the independent acceptance TRX receipt") from error
        dotnet = dotnet_executable(args.dotnet)
        environment = validate_environment(dotnet, manifest)
        assembly_sha256 = str(environment["release_assembly_sha256"])
        acceptance = validate_acceptance_trx(
            retained_acceptance,
            expected_rows=discover_acceptance_rows(dotnet),
            expected_assembly_sha256=assembly_sha256,
        )
        server_check = capture_server_metadata(dotnet, root, server_info, assembly_sha256)
        copy_inventory(root, manifest)
        write_json(root / "environment.json", {
            "machine": machine_details(),
            "sdk": {key: value for key, value in environment.items() if key != "discovery"},
            "inventory_revision": EXPECTED_REVISION,
            "server": server_info,
            "server_info_input": declared_server,
            "server_check": server_check,
            "acceptance_trx": acceptance,
            "manifest_sha256": manifest["_sha256"],
        })
        write_summary(root, {"status": "RUNNING", "eligible": False, "server": server_info})
        for cohort, output in environment["discovery"].items():
            (root / "inventory" / f"{cohort}-discovery-current.txt").write_text(output, encoding="utf-8")

        warmup_valid = True
        for arm in ("fresh", "template"):
            arm_records = run_arm(dotnet, root, "warmup", arm, assembly_sha256)
            for record in arm_records:
                record["directory"] = str(Path("warmup") / arm / str(record["cohort"]))
                records.append(record)
            build_index(root, records, "WARMUP")
            warmup_valid &= all(record["status"] == "VALID" for record in arm_records)
        if not warmup_valid:
            raise BenchmarkError("warmup evidence is incomplete; measured pairs were not started")

        pairs: list[dict[str, object]] = []
        for pair_number, order in enumerate(PAIR_ORDER, start=1):
            pair: dict[str, object] = {"pair": pair_number, "status": "VALID", "arms": {}}
            phase = f"pair-{pair_number:02d}"
            for arm in order:
                arm_records = run_arm(dotnet, root, phase, arm, assembly_sha256)
                for record in arm_records:
                    record["directory"] = str(Path(phase) / arm / str(record["cohort"]))
                    records.append(record)
                pair["arms"][arm] = arm_records
                if any(record["status"] != "VALID" for record in arm_records):
                    pair["status"] = "INVALID"
                build_index(root, records, f"RUNNING {pair_number}/5")
            pairs.append(pair)
        result = analyze_pairs(pairs)
        result["acceptance_trx"] = {
            key: acceptance[key] for key in ("source_sha256", "passed")
        }
        result["server"] = server_info
        status = str(result["status"])
        write_summary(root, result)
        return_code = 0 if status in {"GO", "NO_GO"} else 2
        return return_code
    except BenchmarkError as error:
        status = "INVALID"
        write_summary(root, {
            "status": status,
            "eligible": False,
            "reasons": [redact(str(error), os.environ.get(CONNECTION_VARIABLE, ""))],
            **({"server": server_info} if "server_info" in locals() else {}),
        })
        return 2
    finally:
        build_index(root, records, status)


def validate_command(args: argparse.Namespace) -> int:
    manifest = read_manifest()
    dotnet = dotnet_executable(args.dotnet)
    environment = validate_environment(dotnet, manifest)
    if args.server_info:
        read_server_info(args.server_info.expanduser())
    print(
        f"VALIDATION PASS sdk={environment['sdk_version']} worker_cap={environment['worker_cap']} "
        f"work={EXPECTED_CASE_COUNTS['work']} flow={EXPECTED_CASE_COUNTS['flow']} "
        f"manifest_sha256={manifest['_sha256']} assembly_sha256={environment['release_assembly_sha256']} "
        "database=not-contacted"
    )
    return 0


def print_acceptance_filter(_args: argparse.Namespace) -> int:
    print(acceptance_filter())
    return 0


def make_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Reproducible, redacted paired benchmark for the issue #931 PostgreSQL fixture experiment.",
        epilog=(
            "The test project must already be built in Release with SDK 10.0.402. The full run requires a dedicated "
            "disposable PostgreSQL 16+ server, a secure APPSURFACE_POSTGRES_TEST_CONNECTION environment value, "
            "a server metadata JSON file, and the parent-supplied independent acceptance TRX receipt. Never place the "
            "connection string in an argument or evidence file. Results roots are created once and are never reused. "
            "Use the SQL in Durable/ForgeTrust.AppSurface.Durable.PostgreSql.Tests/README.md to capture all ten "
            "required server settings. The acceptance TRX must contain the complete fixture acceptance matrix "
            "selected by acceptance-filter, with unique result IDs and zero failures, skips, or aborted results."
        ),
    )
    subparsers = parser.add_subparsers(dest="action", required=True)
    show_filter = subparsers.add_parser("acceptance-filter", help="print the VSTest filter for the complete fixture acceptance matrix")
    show_filter.set_defaults(handler=print_acceptance_filter)

    validate = subparsers.add_parser(
        "validate",
        help="verify the pinned SDK, built Release assembly, and exact frozen Work/Flow discovery without contacting PostgreSQL",
    )
    validate.add_argument("--dotnet", help="dotnet executable, defaulting to the pinned user SDK when present")
    validate.add_argument("--server-info", type=Path, help="optionally validate the nonsecret server metadata JSON")
    validate.set_defaults(handler=validate_command)

    run = subparsers.add_parser(
        "run",
        help="run one warmup per arm and the five fixed-order Work/Flow pairs",
    )
    run.add_argument("--dotnet", help="dotnet executable, defaulting to the pinned user SDK when present")
    run.add_argument("--results-root", required=True, type=Path, help="new, unique evidence directory")
    run.add_argument("--server-info", required=True, type=Path, help="JSON with PostgreSQL version and allowlisted settings")
    run.add_argument("--acceptance-trx", required=True, type=Path, help="independent TRX receipt from the isolation and cleanup acceptance matrix")
    run.set_defaults(handler=protocol)
    return parser


def main() -> int:
    parser = make_parser()
    args = parser.parse_args()
    try:
        return args.handler(args)
    except BenchmarkError as error:
        print(f"INVALID: {error}", file=sys.stderr)
        return 2
    except OSError:
        print("INVALID: cannot read or write benchmark evidence; check storage and directory permissions", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
