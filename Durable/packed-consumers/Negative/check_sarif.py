#!/usr/bin/env python3
"""Validate the deliberately narrow compiler failure for one packed fixture."""

from __future__ import annotations

import json
import sys
import tempfile
from pathlib import Path
from urllib.parse import unquote, urlparse


def _marker(source: Path) -> tuple[int, set[str]]:
    found: list[tuple[int, set[str]]] = []
    for number, line in enumerate(source.read_text(encoding="utf-8").splitlines(), 1):
        prefix = "// expected-compiler-error:"
        if prefix in line:
            codes = {code.strip() for code in line.split(prefix, 1)[1].split() if code.strip()}
            found.append((number, codes))
    if len(found) != 1 or not found[0][1]:
        raise ValueError(f"{source}: expected exactly one non-empty compiler-error marker")
    return found[0]


def validate(source: Path, sarif_path: Path) -> None:
    marked_line, allowed = _marker(source)
    try:
        document = json.loads(sarif_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise ValueError(f"malformed compiler SARIF: {sarif_path}: {error}") from error

    if not isinstance(document.get("runs"), list) or not document["runs"]:
        raise ValueError("compiler SARIF has no runs")

    errors: list[tuple[str, Path, int | None, str]] = []
    for run in document["runs"]:
        if not isinstance(run, dict) or not isinstance(run.get("results"), list):
            raise ValueError("compiler SARIF has a malformed run")
        for result in run["results"]:
            if not isinstance(result, dict):
                raise ValueError("compiler SARIF has a malformed result")
            if result.get("level", "error") != "error":
                continue
            rule = result.get("ruleId")
            message_value = result.get("message")
            locations = result.get("locations")
            if not isinstance(message_value, (dict, str)) or not isinstance(locations, list) or not locations:
                raise ValueError(f"compiler SARIF error has malformed message or locations: {result!r}")
            location = locations[0].get("physicalLocation") or locations[0]
            if not isinstance(location, dict):
                raise ValueError("compiler SARIF error has malformed physical location")
            artifact_location = location.get("artifactLocation") or location.get("resultFile")
            region = location.get("region")
            if region is None and isinstance(artifact_location, dict):
                region = artifact_location.get("region")
            if not isinstance(artifact_location, dict) or not isinstance(region, dict):
                raise ValueError("compiler SARIF error has malformed artifact location or region")
            artifact_uri = artifact_location.get("uri")
            if not isinstance(artifact_uri, str):
                raise ValueError("compiler SARIF error is missing an artifact URI")
            parsed = urlparse(unquote(artifact_uri))
            artifact = Path(parsed.path if parsed.scheme == "file" else artifact_uri).resolve()
            line = region.get("startLine")
            message = message_value if isinstance(message_value, str) else str(message_value.get("text", ""))
            errors.append((str(rule or ""), artifact, line, message))

    if not errors:
        raise ValueError("compiler SARIF contained no error diagnostics")

    source = source.resolve()
    marked = [error for error in errors if error[1] == source and error[2] == marked_line]
    if not marked:
        raise ValueError(f"no compiler error was reported at {source}:{marked_line}")

    observed = {error[0] for error in marked}
    unexpected = [error for error in errors if error[0] not in allowed or error[1] != source or error[2] != marked_line]
    if unexpected:
        details = "; ".join(f"{code or '<missing-code>'} {path}:{line}" for code, path, line, _ in unexpected)
        raise ValueError(f"unexpected compiler errors: {details}")
    missing = allowed - observed
    if missing:
        raise ValueError(f"expected compiler code(s) not observed at marked line: {', '.join(sorted(missing))}")


def self_test() -> None:
    with tempfile.TemporaryDirectory(prefix="packed-negative-check-") as directory:
        root = Path(directory)
        source = root / "Invalid.cs"
        source.write_text("class Fixture { // expected-compiler-error: CS0311\n}\n", encoding="utf-8")

        def sarif(*results: dict) -> Path:
            path = root / f"case-{len(list(root.glob('case-*.sarif')))}.sarif"
            path.write_text(json.dumps({"runs": [{"results": list(results)}]}), encoding="utf-8")
            return path

        valid = {"ruleId": "CS0311", "level": "error", "message": {"text": "expected"}, "locations": [{"physicalLocation": {"artifactLocation": {"uri": source.as_uri()}, "region": {"startLine": 1}}}]}
        validate(source, sarif(valid))
        failures = [
            sarif(),
            sarif({**valid, "ruleId": "CS0452"}),
            sarif(valid, {**valid, "ruleId": "CS1002"}),
            sarif({**valid, "locations": [{"physicalLocation": {"artifactLocation": {"uri": str(root / "other" / "Invalid.cs")}, "region": {"startLine": 1}}}]}),
            sarif({**valid, "locations": [{"physicalLocation": {"artifactLocation": {"uri": source.as_uri()}, "region": {"startLine": 2}}}]}),
            root / "missing.sarif",
            root / "invalid-json.sarif",
            root / "malformed.sarif",
        ]
        failures[-2].write_text("{", encoding="utf-8")
        failures[-1].write_text(json.dumps({"runs": [{}]}), encoding="utf-8")
        for failure in failures:
            try:
                validate(source, failure)
            except ValueError:
                continue
            raise AssertionError(f"checker accepted invalid self-test case: {failure}")
    print("Negative SARIF checker self-tests passed (success, missing, malformed, wrong-code, extra-error, wrong-file, wrong-line).")


def validate_manifest(source: Path, positive: Path, manifest_path: Path) -> None:
    try:
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise ValueError(f"malformed fixture manifest: {manifest_path}: {error}") from error
    invalid = manifest.get("invalid")
    positive_entry = manifest.get("positive")
    if not isinstance(invalid, dict) or not isinstance(positive_entry, dict):
        raise ValueError(f"fixture manifest is missing invalid or positive entries: {manifest_path}")
    marked_line, allowed = _marker(source)
    if invalid.get("file") != source.name or invalid.get("line") != marked_line:
        raise ValueError(f"fixture manifest is stale for {source}")
    if set(invalid.get("allowedDiagnostics", [])) != allowed:
        raise ValueError(f"fixture manifest diagnostics do not match {source}")
    if positive_entry.get("file") != positive.name or not positive.is_file():
        raise ValueError(f"fixture positive control is missing or stale: {positive}")


def main(argv: list[str]) -> int:
    if argv == ["--self-test"]:
        self_test()
        return 0
    if len(argv) == 4 and argv[0] == "--manifest":
        try:
            validate_manifest(Path(argv[2]), Path(argv[3]), Path(argv[1]))
        except (OSError, ValueError) as error:
            print(f"negative fixture manifest verification failed: {error}", file=sys.stderr)
            return 1
        return 0
    if len(argv) != 2:
        print("usage: check_sarif.py SOURCE SARIF | --manifest MANIFEST SOURCE POSITIVE", file=sys.stderr)
        return 2
    try:
        validate(Path(argv[0]), Path(argv[1]))
    except (OSError, ValueError) as error:
        print(f"negative fixture verification failed: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
