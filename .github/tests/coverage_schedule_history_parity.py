#!/usr/bin/env python3
"""Compare the temporary Python and C# coverage-history entry points offline."""

from __future__ import annotations

import argparse
import base64
import hashlib
import importlib.util
import json
import os
import platform
import shutil
import stat
import subprocess
import sys
import tempfile
import unicodedata
import zipfile
from dataclasses import dataclass
from pathlib import Path
from typing import Any


ROOT = Path(__file__).resolve().parents[2]
PYTHON_HELPER = ROOT / ".github/scripts/coverage-schedule-history.py"
CSHARP_APP = ROOT / ".github/scripts/coverage-schedule-history.cs"
BASELINE_TESTS = ROOT / ".github/tests/coverage_schedule_history_tests.py"
REPOSITORY = "forge-trust/AppSurface"
DEFAULT_BRANCH = "main"
MAX_JSON_BYTES = 2 * 1024 * 1024
MAX_ZIP_BYTES = MAX_JSON_BYTES + 64 * 1024
MAX_PYTHON_INTEGER_DIGITS = 4300
LARGE_STDERR_BYTES = 128 * 1024
CANONICAL_TIMINGS = ROOT / "tests/coverage-schedule-history/fixtures/timings.json"
FAKE_GH_SOURCE = r'''#!/usr/bin/env python3
from __future__ import annotations

import base64
import json
import os
import pathlib
import sys


case_file = pathlib.Path(os.environ["FAKE_GH_RESPONSES"])
call_log = pathlib.Path(os.environ["FAKE_GH_CALL_LOG"])
arguments = sys.argv[1:]
method = arguments[2] if len(arguments) >= 3 else None
endpoint = arguments[3] if len(arguments) >= 4 else None
event = {
    "method": method,
    "endpoint": endpoint,
    "arguments_valid": arguments[:3] == ["api", "--method", "GET"] and len(arguments) == 4,
    "credentials_present": bool(os.environ.get("GH_TOKEN") or os.environ.get("GITHUB_TOKEN")),
}
with call_log.open("a", encoding="utf-8") as output:
    output.write(json.dumps(event, separators=(",", ":")) + "\n")

if event["credentials_present"]:
    sys.stderr.write("fake gh must not receive GitHub credentials\n")
    raise SystemExit(98)

collision = os.environ.get("FAKE_GH_CREATE_COLLISION_PATH")
if collision and endpoint and endpoint.endswith("/zip"):
    collision_path = pathlib.Path(collision)
    collision_path.parent.mkdir(parents=True, exist_ok=True)
    if not os.path.lexists(collision_path):
        collision_path.write_bytes(b"winner-created-by-fake-gh")

with case_file.open("r", encoding="utf-8") as response_file:
    responses = json.load(response_file)
response = responses.get(endpoint)
if response is None:
    sys.stderr.write("unexpected fake gh endpoint\n")
    raise SystemExit(97)

if response.get("stderr_bytes"):
    sys.stderr.buffer.write(b"x" * response["stderr_bytes"])
    sys.stderr.buffer.flush()

body = base64.b64decode(response["body"])
sys.stdout.buffer.write(body)
sys.stdout.buffer.flush()

status = response.get("status", 0)
if status != 0:
    sys.stderr.write("synthetic gh failure\n")
    sys.stderr.flush()
raise SystemExit(status)
'''

PYTHON_SITE_CUSTOMIZE_SOURCE = r'''import json
import sys


MAX_DIGITS = 4300
if hasattr(sys, "set_int_max_str_digits"):
    sys.set_int_max_str_digits(MAX_DIGITS)
else:
    original_loads = json.loads

    def bounded_integer(token):
        digits = token[1:] if token.startswith("-") else token
        if len(digits) > MAX_DIGITS:
            raise ValueError("integer string exceeds the 4,300 digit limit")
        return int(token)

    def bounded_loads(*args, **kwargs):
        kwargs.setdefault("parse_int", bounded_integer)
        return original_loads(*args, **kwargs)

    json.loads = bounded_loads
'''


@dataclass(frozen=True)
class Response:
    body: bytes
    status: int = 0
    stderr_bytes: int = 0


@dataclass(frozen=True)
class Scenario:
    name: str
    responses: dict[str, Response]
    endpoints: tuple[str, ...]
    reason: str
    output_mode: str = "nested"
    environment: dict[str, str | None] | None = None
    collision: bool = False
    expected_exit: int = 0
    expected_output_kind: str = "absent"
    expected_output: bytes | None = None
    expected_parent_kind: str = "absent"
    expected_parent_bytes: bytes | None = None


@dataclass
class Invocation:
    implementation: str
    return_code: int
    stdout: bytes
    stderr: bytes
    calls: list[dict[str, Any]]
    credentials_present: bool
    output_exists: bool
    output_kind: str
    output_bytes: bytes | None
    parent_exists: bool
    parent_kind: str
    parent_bytes: bytes | None


def load_baseline_helpers():
    spec = importlib.util.spec_from_file_location("coverage_schedule_history_tests", BASELINE_TESTS)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"could not load baseline fixtures from {BASELINE_TESTS}")
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


BASELINE = load_baseline_helpers()
RUNS_ENDPOINT = (
    f"repos/{REPOSITORY}/actions/workflows/build.yml/runs"
    f"?branch={DEFAULT_BRANCH}&event=push&status=success&per_page=10"
)


def zip_bytes(payload: bytes, compression: int = zipfile.ZIP_DEFLATED) -> bytes:
    from io import BytesIO

    output = BytesIO()
    with zipfile.ZipFile(output, "w", compression=compression) as archive:
        archive.writestr("timings.json", payload)
    return output.getvalue()


def corrupt_crc(archive: bytes) -> bytes:
    data = bytearray(archive)
    central_header = data.index(b"PK\x01\x02")
    local_header = data.index(b"PK\x03\x04")
    data[central_header + 16] ^= 0x01
    data[local_header + 14] ^= 0x01
    return bytes(data)


def method9_fixture_bytes(payload: bytes) -> bytes:
    from io import BytesIO

    output = BytesIO()
    with zipfile.ZipFile(
        output,
        "w",
        compression=zipfile.ZIP_DEFLATED,
        compresslevel=0,
    ) as archive:
        archive.writestr("timings.json", payload)
    data = bytearray(output.getvalue())
    with zipfile.ZipFile(BytesIO(data), "r") as archive:
        entry = archive.getinfo("timings.json")
        offset = entry.header_offset
        compressed_start = offset + 30 + len(entry.filename.encode()) + len(entry.extra)
        compressed = bytes(data[compressed_start : compressed_start + entry.compress_size])
        if len(compressed) != len(payload) + 5 or compressed[0] & 0b111 != 0b001:
            raise AssertionError("generated level-0 DEFLATE stream is not a single stored block")
        length = int.from_bytes(compressed[1:3], "little")
        inverse_length = int.from_bytes(compressed[3:5], "little")
        if length != len(payload) or inverse_length != (length ^ 0xFFFF) or compressed[5:] != payload:
            raise AssertionError("generated method 9 fixture has an invalid stored block")
        central_header = data.index(b"PK\x01\x02")
    data[offset + 8 : offset + 10] = (9).to_bytes(2, "little")
    data[central_header + 10 : central_header + 12] = (9).to_bytes(2, "little")
    return bytes(data)


def marked_encrypted(archive: bytes) -> bytes:
    data = bytearray(archive)
    local_header = data.index(b"PK\x03\x04")
    central_header = data.index(b"PK\x01\x02")
    local_flags = int.from_bytes(data[local_header + 6 : local_header + 8], "little")
    central_flags = int.from_bytes(data[central_header + 8 : central_header + 10], "little")
    data[local_header + 6 : local_header + 8] = (local_flags | 1).to_bytes(2, "little")
    data[central_header + 8 : central_header + 10] = (central_flags | 1).to_bytes(2, "little")
    return bytes(data)


def response(body: bytes, status: int = 0, stderr_bytes: int = 0) -> Response:
    return Response(body, status, stderr_bytes)


def encoded_responses(values: dict[str, Response]) -> dict[str, dict[str, Any]]:
    return {
        endpoint: {
            "body": base64.b64encode(item.body).decode("ascii"),
            "status": item.status,
            "stderr_bytes": item.stderr_bytes,
        }
        for endpoint, item in values.items()
    }


def response_json(value: object, stderr_bytes: int = 0) -> Response:
    return response(json.dumps(value, separators=(",", ":")).encode("utf-8"), stderr_bytes=stderr_bytes)


def run_record(run_id: int, *, created_at: str = "2026-11-09T12:00:00Z", **changes: object):
    return BASELINE.run_record(run_id, created_at=created_at, **changes)


def artifact_response(*artifacts: dict[str, object]) -> Response:
    return response_json({"artifacts": list(artifacts)})


def run_response(*runs: dict[str, object]) -> Response:
    return response_json({"workflow_runs": list(runs)})


def run_artifacts_endpoint(run_id: int) -> str:
    return f"repos/{REPOSITORY}/actions/runs/{run_id}/artifacts?per_page=100"


def artifact_zip_endpoint(artifact_id: int) -> str:
    return f"repos/{REPOSITORY}/actions/artifacts/{artifact_id}/zip"


def build_scenarios() -> list[Scenario]:
    canonical = CANONICAL_TIMINGS.read_bytes()
    basic_valid = zip_bytes(canonical)
    invalid_json_zip = zip_bytes(b"not JSON")
    empty_projects_zip = zip_bytes(b'{"projects":[]}')
    older_payload = b'{ "projects" : [ { "project" : "src/Older/Older.csproj", "seconds" : 31 } ] }\n'
    older_zip = zip_bytes(older_payload)
    crc_invalid = corrupt_crc(zip_bytes(canonical))
    corrupt_deflate = BASELINE.make_corrupt_deflate_zip()
    encrypted = marked_encrypted(zip_bytes(canonical))
    duplicate_root = BASELINE.make_zip(
        [("timings.json", canonical), ("timings.json", canonical)]
    )
    oversize_entry = zip_bytes(b" " * (MAX_JSON_BYTES + 1))
    bzip2 = zip_bytes(canonical, zipfile.ZIP_BZIP2)
    lzma = zip_bytes(canonical, zipfile.ZIP_LZMA)
    method9 = method9_fixture_bytes(canonical)

    runs40 = run_artifacts_endpoint(40)
    zip93 = artifact_zip_endpoint(93)
    runs20 = run_artifacts_endpoint(20)
    zip120 = artifact_zip_endpoint(120)
    runs19 = run_artifacts_endpoint(19)
    zip119 = artifact_zip_endpoint(119)
    runs8 = run_artifacts_endpoint(8)
    zip18 = artifact_zip_endpoint(18)
    runs21 = run_artifacts_endpoint(21)

    valid_runs = [
        run_record(99, branch="feature", created_at="2026-11-10T12:00:00Z"),
        run_record(98, status="in_progress", created_at="2026-11-10T11:00:00Z"),
        run_record(97, conclusion="failure", created_at="2026-11-10T10:00:00Z"),
        run_record(
            40,
            head_repository=f"{REPOSITORY.upper()}",
            created_at="2026-11-08T12:00:00Z",
        ),
    ]

    scenarios = [
        Scenario(
            "valid-exact-bytes-and-artifact-selection",
            {
                RUNS_ENDPOINT: run_response(*valid_runs),
                runs40: artifact_response(
                    BASELINE.artifact(91, expired=True),
                    BASELINE.artifact(92, name="coverage-schedule-timings-extra"),
                    BASELINE.artifact(93),
                ),
                zip93: response(basic_valid),
            },
            (RUNS_ENDPOINT, runs40, zip93),
            "",
            expected_output_kind="file",
            expected_output=canonical,
            expected_parent_kind="directory",
        ),
        Scenario(
            "fallback-after-invalid-candidate",
            {
                RUNS_ENDPOINT: run_response(
                    run_record(20, created_at="2026-11-10T12:00:00Z"),
                    run_record(19, created_at="2026-11-09T12:00:00Z"),
                ),
                runs20: artifact_response(BASELINE.artifact(120)),
                zip120: response(empty_projects_zip),
                runs19: artifact_response(BASELINE.artifact(119)),
                zip119: response(older_zip),
            },
            (RUNS_ENDPOINT, runs20, zip120, runs19, zip119),
            "",
            expected_output_kind="file",
            expected_output=older_payload,
            expected_parent_kind="directory",
        ),
        Scenario(
            "no-history-no-output",
            {RUNS_ENDPOINT: run_response()},
            (RUNS_ENDPOINT,),
            "no successful completed build runs",
        ),
        Scenario(
            "workflow-api-failure",
            {RUNS_ENDPOINT: response(b"authentication failed", status=47)},
            (RUNS_ENDPOINT,),
            "GitHub workflow history is unavailable",
        ),
        Scenario(
            "invalid-workflow-shape",
            {RUNS_ENDPOINT: response_json({"workflow_runs": None})},
            (RUNS_ENDPOINT,),
            "GitHub workflow history is unavailable",
        ),
        Scenario(
            "artifact-api-failure-stops-search",
            {
                RUNS_ENDPOINT: run_response(
                    run_record(20, created_at="2026-11-10T12:00:00Z"),
                    run_record(19, created_at="2026-11-09T12:00:00Z"),
                ),
                runs20: response(b'{"artifacts":null}'),
            },
            (RUNS_ENDPOINT, runs20),
            "GitHub artifact history is unavailable",
        ),
        Scenario(
            "invalid-metadata-skipped-before-download",
            {
                RUNS_ENDPOINT: run_response(
                    run_record(20, created_at="2026-11-10T12:00:00Z"),
                    run_record(19, created_at="2026-11-09T12:00:00Z"),
                ),
                runs20: artifact_response(
                    BASELINE.artifact(120, size_in_bytes="1024"),
                    BASELINE.artifact(121, size_in_bytes=MAX_ZIP_BYTES + 1),
                    BASELINE.artifact(122, size_in_bytes=-1),
                ),
                runs19: artifact_response(BASELINE.artifact(119)),
                zip119: response(older_zip),
            },
            (RUNS_ENDPOINT, runs20, runs19, zip119),
            "",
            expected_output_kind="file",
            expected_output=older_payload,
            expected_parent_kind="directory",
        ),
        Scenario(
            "candidate-exhaustion-no-output",
            {
                RUNS_ENDPOINT: run_response(run_record(8)),
                runs8: artifact_response(BASELINE.artifact(18)),
                zip18: response(invalid_json_zip),
            },
            (RUNS_ENDPOINT, runs8, zip18),
            "no valid coverage schedule timings artifact",
        ),
        Scenario(
            "bad-crc-falls-back-to-older-bytes",
            {
                RUNS_ENDPOINT: run_response(
                    run_record(20, created_at="2026-11-10T12:00:00Z"),
                    run_record(19, created_at="2026-11-09T12:00:00Z"),
                ),
                runs20: artifact_response(BASELINE.artifact(120)),
                zip120: response(crc_invalid),
                runs19: artifact_response(BASELINE.artifact(119)),
                zip119: response(older_zip),
            },
            (RUNS_ENDPOINT, runs20, zip120, runs19, zip119),
            "",
            expected_output_kind="file",
            expected_output=older_payload,
            expected_parent_kind="directory",
        ),
        Scenario(
            "corrupt-deflate-no-output",
            {
                RUNS_ENDPOINT: run_response(run_record(8)),
                runs8: artifact_response(BASELINE.artifact(18)),
                zip18: response(corrupt_deflate),
            },
            (RUNS_ENDPOINT, runs8, zip18),
            "no valid coverage schedule timings artifact",
        ),
        Scenario(
            "encrypted-entry-no-output",
            {
                RUNS_ENDPOINT: run_response(run_record(8)),
                runs8: artifact_response(BASELINE.artifact(18)),
                zip18: response(encrypted),
            },
            (RUNS_ENDPOINT, runs8, zip18),
            "no valid coverage schedule timings artifact",
        ),
        Scenario(
            "duplicate-root-entry-no-output",
            {
                RUNS_ENDPOINT: run_response(run_record(8)),
                runs8: artifact_response(BASELINE.artifact(18)),
                zip18: response(duplicate_root),
            },
            (RUNS_ENDPOINT, runs8, zip18),
            "no valid coverage schedule timings artifact",
        ),
        Scenario(
            "oversize-entry-no-output",
            {
                RUNS_ENDPOINT: run_response(run_record(8)),
                runs8: artifact_response(BASELINE.artifact(18)),
                zip18: response(oversize_entry),
            },
            (RUNS_ENDPOINT, runs8, zip18),
            "no valid coverage schedule timings artifact",
        ),
        Scenario(
            "bzip2-baseline-method",
            {
                RUNS_ENDPOINT: run_response(run_record(8)),
                runs8: artifact_response(BASELINE.artifact(18)),
                zip18: response(bzip2),
            },
            (RUNS_ENDPOINT, runs8, zip18),
            "",
            expected_output_kind="file",
            expected_output=canonical,
            expected_parent_kind="directory",
        ),
        Scenario(
            "lzma-baseline-method",
            {
                RUNS_ENDPOINT: run_response(run_record(8)),
                runs8: artifact_response(BASELINE.artifact(18)),
                zip18: response(lzma),
            },
            (RUNS_ENDPOINT, runs8, zip18),
            "",
            expected_output_kind="file",
            expected_output=canonical,
            expected_parent_kind="directory",
        ),
        Scenario(
            "method-9-deflate64-negative-control",
            {
                RUNS_ENDPOINT: run_response(run_record(8)),
                runs8: artifact_response(BASELINE.artifact(18)),
                zip18: response(method9),
            },
            (RUNS_ENDPOINT, runs8, zip18),
            "no valid coverage schedule timings artifact",
        ),
        Scenario(
            "exact-json-stdout-cap",
            {
                RUNS_ENDPOINT: response(
                    b'{"workflow_runs":[]}' + b" " * (MAX_JSON_BYTES - len(b'{"workflow_runs":[]}'))
                )
            },
            (RUNS_ENDPOINT,),
            "no successful completed build runs",
        ),
        Scenario(
            "json-stdout-over-cap",
            {
                RUNS_ENDPOINT: response(
                    b'{"workflow_runs":[]}' + b" " * (MAX_JSON_BYTES + 1 - len(b'{"workflow_runs":[]}'))
                )
            },
            (RUNS_ENDPOINT,),
            "GitHub workflow history is unavailable",
        ),
        Scenario(
            "json-integer-digit-limit",
            {
                RUNS_ENDPOINT: response(
                    b'{"workflow_runs":[],"ignored":'
                    + b"9" * (MAX_PYTHON_INTEGER_DIGITS + 1)
                    + b"}"
                )
            },
            (RUNS_ENDPOINT,),
            "GitHub workflow history is unavailable",
        ),
        Scenario(
            "large-stderr-does-not-fail-success",
            {
                RUNS_ENDPOINT: response(
                    run_response(run_record(40)).body,
                    stderr_bytes=LARGE_STDERR_BYTES,
                ),
                runs40: artifact_response(BASELINE.artifact(93)),
                zip93: response(basic_valid, stderr_bytes=LARGE_STDERR_BYTES),
            },
            (RUNS_ENDPOINT, runs40, zip93),
            "",
            expected_output_kind="file",
            expected_output=canonical,
            expected_parent_kind="directory",
        ),
        Scenario(
            "existing-file-preserved",
            {RUNS_ENDPOINT: run_response()},
            (),
            "--output already exists",
            output_mode="existing-file",
            expected_exit=2,
            expected_output_kind="file",
            expected_output=b"preserve-existing-output",
            expected_parent_kind="directory",
        ),
        Scenario(
            "existing-directory-rejected",
            {RUNS_ENDPOINT: run_response()},
            (),
            "--output already exists",
            output_mode="existing-directory",
            expected_exit=2,
            expected_output_kind="directory",
            expected_parent_kind="directory",
        ),
        Scenario(
            "dangling-symlink-rejected",
            {RUNS_ENDPOINT: run_response()},
            (),
            "--output already exists",
            output_mode="dangling-symlink",
            expected_exit=2,
            expected_output_kind="symlink",
            expected_parent_kind="directory",
        ),
        Scenario(
            "relative-output-rejected",
            {RUNS_ENDPOINT: run_response()},
            (),
            "--output must be an absolute path",
            output_mode="relative",
            expected_exit=2,
        ),
        Scenario(
            "missing-branch-rejected",
            {RUNS_ENDPOINT: run_response()},
            (),
            "GITHUB_DEFAULT_BRANCH is required",
            environment={"GITHUB_DEFAULT_BRANCH": None},
            expected_exit=2,
        ),
        Scenario(
            "regular-file-parent-fails-after-response",
            {
                RUNS_ENDPOINT: run_response(run_record(40)),
                runs40: artifact_response(BASELINE.artifact(93)),
                zip93: response(basic_valid),
            },
            (RUNS_ENDPOINT, runs40, zip93),
            "could not create --output",
            output_mode="regular-file-parent",
            expected_exit=2,
            expected_parent_kind="file",
            expected_parent_bytes=b"preserve-parent-file",
        ),
        Scenario(
            "late-create-collision-preserves-winner",
            {
                RUNS_ENDPOINT: run_response(run_record(40)),
                runs40: artifact_response(BASELINE.artifact(93)),
                zip93: response(basic_valid),
            },
            (RUNS_ENDPOINT, runs40, zip93),
            "could not create --output",
            output_mode="collision",
            collision=True,
            expected_exit=2,
            expected_output_kind="file",
            expected_output=b"winner-created-by-fake-gh",
            expected_parent_kind="directory",
        ),
        Scenario(
            "encoded-default-branch",
            {
                "repos/forge-trust/AppSurface/actions/workflows/build.yml/runs?branch=release%2Fcoverage%2Bhistory&event=push&status=success&per_page=10": run_response(
                    run_record(
                        40,
                        branch="release/coverage+history",
                        created_at="2026-11-08T12:00:00Z",
                    )
                ),
                runs40: artifact_response(BASELINE.artifact(93)),
                zip93: response(basic_valid),
            },
            (
                "repos/forge-trust/AppSurface/actions/workflows/build.yml/runs?branch=release%2Fcoverage%2Bhistory&event=push&status=success&per_page=10",
                runs40,
                zip93,
            ),
            "",
            environment={"GITHUB_DEFAULT_BRANCH": "release/coverage+history"},
            expected_output_kind="file",
            expected_output=canonical,
            expected_parent_kind="directory",
        ),
        Scenario(
            "run-id-tie-break-order",
            {
                RUNS_ENDPOINT: run_response(
                    run_record(20, created_at="2026-11-09T12:00:00Z"),
                    run_record(21, created_at="2026-11-09T07:00:00-05:00"),
                ),
                runs21: artifact_response(BASELINE.artifact(121)),
                artifact_zip_endpoint(121): response(empty_projects_zip),
                runs20: artifact_response(BASELINE.artifact(120)),
                zip120: response(older_zip),
            },
            (RUNS_ENDPOINT, runs21, artifact_zip_endpoint(121), runs20, zip120),
            "",
            expected_output_kind="file",
            expected_output=older_payload,
            expected_parent_kind="directory",
        ),
        Scenario(
            "timezone-equivalent-run-order",
            {
                RUNS_ENDPOINT: run_response(
                    run_record(20, created_at="2026-11-09T12:00:00Z"),
                    run_record(19, created_at="2026-11-09T07:00:00-05:00"),
                ),
                runs20: artifact_response(BASELINE.artifact(120)),
                zip120: response(older_zip),
            },
            (RUNS_ENDPOINT, runs20, zip120),
            "",
            expected_output_kind="file",
            expected_output=older_payload,
            expected_parent_kind="directory",
        ),
    ]
    for name, archive_bytes, accepted in (
        ("stored-baseline-method", zip_bytes(canonical, zipfile.ZIP_STORED), True),
        ("unsupported-method-99", BASELINE.make_unsupported_compression_zip(), False),
    ):
        scenarios.append(
            Scenario(
                name,
                {
                    RUNS_ENDPOINT: run_response(run_record(8)),
                    runs8: artifact_response(BASELINE.artifact(18)),
                    zip18: response(archive_bytes),
                },
                (RUNS_ENDPOINT, runs8, zip18),
                "" if accepted else "no valid coverage schedule timings artifact",
                expected_output_kind="file" if accepted else "absent",
                expected_output=canonical if accepted else None,
                expected_parent_kind="directory" if accepted else "absent",
            )
        )
    return scenarios


def output_paths(run_root: Path, mode: str) -> tuple[Path, Path]:
    if mode == "relative":
        output = Path("relative-output/timings.json")
        return output, output.parent
    if mode == "existing-file":
        output = run_root / "already-there.json"
        output.write_bytes(b"preserve-existing-output")
        return output, output.parent
    if mode == "existing-directory":
        output = run_root / "output-directory"
        output.mkdir()
        return output, output.parent
    if mode == "dangling-symlink":
        output = run_root / "dangling-output"
        output.symlink_to(run_root / "missing-target")
        return output, output.parent
    if mode == "regular-file-parent":
        parent = run_root / "blocked-parent"
        parent.write_bytes(b"preserve-parent-file")
        return parent / "timings.json", parent
    if mode == "collision":
        parent = run_root / "collision"
        return parent / "timings.json", parent
    output = run_root / "nested" / "timings.json"
    return output, output.parent


def kind(path: Path) -> str:
    try:
        info = path.lstat()
    except (FileNotFoundError, NotADirectoryError):
        return "absent"
    if stat.S_ISLNK(info.st_mode):
        return "symlink"
    if stat.S_ISDIR(info.st_mode):
        return "directory"
    if stat.S_ISREG(info.st_mode):
        return "file"
    return "other"


def invoke(
    scenario: Scenario,
    implementation: str,
    case_root: Path,
    fake_gh: Path,
    python_command: str,
    dotnet_command: str,
) -> Invocation:
    run_root = case_root / implementation
    run_root.mkdir(parents=True)
    # The relative-path case runs here so we can observe every filesystem effect.
    # The SDK searches for global.json from the working directory.
    if scenario.output_mode == "relative" and implementation == "csharp":
        shutil.copyfile(ROOT / "global.json", run_root / "global.json")
    output, output_parent = output_paths(run_root, scenario.output_mode)
    output_argument = str(output)
    if not output.is_absolute():
        output = run_root / output
        output_parent = output.parent
    responses_file = case_root / "responses.json"
    responses_file.write_text(
        json.dumps(encoded_responses(scenario.responses), separators=(",", ":")),
        encoding="utf-8",
    )
    call_log = case_root / f"{implementation}.calls.jsonl"

    environment = {
        key: value
        for key, value in os.environ.items()
        if key not in {"GH_TOKEN", "GITHUB_TOKEN", "GH_ENTERPRISE_TOKEN"}
    }
    python_support = case_root.parent / "python-support"
    python_support.mkdir(exist_ok=True)
    (python_support / "sitecustomize.py").write_text(
        PYTHON_SITE_CUSTOMIZE_SOURCE,
        encoding="utf-8",
    )
    environment.update(
        {
            "GITHUB_REPOSITORY": REPOSITORY,
            "GITHUB_DEFAULT_BRANCH": DEFAULT_BRANCH,
            "FAKE_GH_RESPONSES": str(responses_file),
            "FAKE_GH_CALL_LOG": str(call_log),
            "PATH": f"{fake_gh.parent}{os.pathsep}{os.environ.get('PATH', '')}",
            "PYTHONINTMAXSTRDIGITS": str(MAX_PYTHON_INTEGER_DIGITS),
            "PYTHONPATH": os.pathsep.join(
                item
                for item in (str(python_support), os.environ.get("PYTHONPATH", ""))
                if item
            ),
        }
    )
    if scenario.environment:
        for key, value in scenario.environment.items():
            if value is None:
                environment.pop(key, None)
            else:
                environment[key] = value

    if scenario.collision:
        environment["FAKE_GH_CREATE_COLLISION_PATH"] = str(output)

    if implementation == "python":
        command = [python_command, str(PYTHON_HELPER), "restore", "--output", output_argument]
    else:
        command = [
            dotnet_command,
            "run",
            "--file",
            str(CSHARP_APP),
            "--",
            "restore",
            "--output",
            output_argument,
        ]

    result = subprocess.run(
        command,
        cwd=ROOT if scenario.output_mode != "relative" else run_root,
        env=environment,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        check=False,
    )

    calls: list[dict[str, Any]] = []
    if call_log.exists():
        calls = [json.loads(line) for line in call_log.read_text(encoding="utf-8").splitlines()]

    output_kind = kind(output)
    output_exists = output_kind != "absent"
    output_bytes = output.read_bytes() if output_kind == "file" else None
    parent_kind = kind(output_parent)
    return Invocation(
        implementation=implementation,
        return_code=result.returncode,
        stdout=result.stdout,
        stderr=result.stderr,
        calls=calls,
        credentials_present=any(call["credentials_present"] for call in calls),
        output_exists=output_exists,
        output_kind=output_kind,
        output_bytes=output_bytes,
        parent_exists=parent_kind != "absent",
        parent_kind=parent_kind,
        parent_bytes=output_parent.read_bytes() if parent_kind == "file" else None,
    )


def expected_reason_present(stderr: bytes, reason: str) -> bool:
    text = stderr.decode("utf-8", errors="replace")
    if not reason:
        return "warning:" not in text and "error:" not in text
    return reason in text


def summarize_invocation(invocation: Invocation) -> str:
    file_hash = (
        hashlib.sha256(invocation.output_bytes).hexdigest()
        if invocation.output_bytes is not None
        else "-"
    )
    requests = [
        f"{call.get('method')} {call.get('endpoint')}"
        for call in invocation.calls
    ]
    return (
        f"{invocation.implementation}: exit={invocation.return_code}, "
        f"output={invocation.output_kind}:{file_hash}, "
        f"parent={invocation.parent_kind}, requests={requests}"
    )


def compare_scenario(
    scenario: Scenario,
    python_result: Invocation,
    csharp_result: Invocation,
) -> list[str]:
    failures: list[str] = []
    for result in (python_result, csharp_result):
        if result.stdout:
            failures.append(f"{result.implementation} wrote unexpected bytes to stdout")
        if result.return_code != scenario.expected_exit:
            failures.append(
                f"{result.implementation} exit was {result.return_code}, expected {scenario.expected_exit}"
            )
        if result.output_kind != scenario.expected_output_kind:
            failures.append(
                f"{result.implementation} output kind was {result.output_kind}, "
                f"expected {scenario.expected_output_kind}"
            )
        if result.output_bytes != scenario.expected_output:
            failures.append(f"{result.implementation} output bytes differ from this case's expected bytes")
        if result.parent_kind != scenario.expected_parent_kind:
            failures.append(
                f"{result.implementation} parent kind was {result.parent_kind}, "
                f"expected {scenario.expected_parent_kind}"
            )
        if result.parent_bytes != scenario.expected_parent_bytes:
            failures.append(f"{result.implementation} parent bytes differ from this case's expected bytes")

    if python_result.return_code != csharp_result.return_code:
        failures.append(
            f"exit differs ({python_result.return_code} vs {csharp_result.return_code})"
        )

    python_calls = [
        (call.get("method"), call.get("endpoint"))
        for call in python_result.calls
    ]
    csharp_calls = [
        (call.get("method"), call.get("endpoint"))
        for call in csharp_result.calls
    ]
    if python_calls != csharp_calls:
        failures.append("method, endpoint, or request order differs")
    if python_calls != [("GET", endpoint) for endpoint in scenario.endpoints]:
        failures.append(f"request trace differs from expected {scenario.endpoints!r}")

    for result in (python_result, csharp_result):
        if result.credentials_present:
            failures.append(f"{result.implementation} fake gh received a GitHub credential")
        if any(not call.get("arguments_valid") for call in result.calls):
            failures.append(f"{result.implementation} did not call gh api --method GET <endpoint>")
        if not expected_reason_present(result.stderr, scenario.reason):
            failures.append(
                f"{result.implementation} stderr did not contain expected reason {scenario.reason!r}"
            )

    python_state = (
        python_result.output_exists,
        python_result.output_kind,
        python_result.output_bytes,
        python_result.parent_exists,
        python_result.parent_kind,
        python_result.parent_bytes,
    )
    csharp_state = (
        csharp_result.output_exists,
        csharp_result.output_kind,
        csharp_result.output_bytes,
        csharp_result.parent_exists,
        csharp_result.parent_kind,
        csharp_result.parent_bytes,
    )
    if python_state != csharp_state:
        failures.append("output or parent presence, type, or bytes differ")
    return failures


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--python", default=sys.executable, help="Python executable for both production helper and fake gh")
    parser.add_argument("--dotnet", default=shutil.which("dotnet") or "dotnet", help=".NET SDK executable")
    parser.add_argument("--case", action="append", help="run only this case name, repeatable")
    args = parser.parse_args()

    sdk = subprocess.run(
        [args.dotnet, "--version"],
        cwd=ROOT,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        check=False,
    )
    if sdk.returncode != 0:
        sys.stderr.write(sdk.stderr)
        return sdk.returncode
    revision = subprocess.run(
        ["git", "rev-parse", "HEAD"],
        cwd=ROOT,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        check=False,
    )
    if revision.returncode != 0:
        sys.stderr.write(revision.stderr)
        return revision.returncode
    if not Path(PYTHON_HELPER).is_file() or not Path(CSHARP_APP).is_file():
        parser.error("both production entry points must exist for revision A")

    scenarios = build_scenarios()
    if args.case:
        selected_names = set(args.case)
        known_names = {scenario.name for scenario in scenarios}
        unknown_names = selected_names - known_names
        if unknown_names:
            parser.error(f"unknown case name(s): {', '.join(sorted(unknown_names))}")
        scenarios = [scenario for scenario in scenarios if scenario.name in selected_names]

    if not scenarios:
        parser.error("at least one parity scenario is required")

    failures = 0
    passed = 0
    with tempfile.TemporaryDirectory(prefix="coverage-history-parity-") as temporary:
        root = Path(temporary)
        fake_bin = root / "fake-bin"
        fake_bin.mkdir()
        fake_gh = fake_bin / "gh"
        fake_gh.write_text(FAKE_GH_SOURCE, encoding="utf-8")
        fake_gh.chmod(0o755)

        for scenario in scenarios:
            case_root = root / scenario.name
            case_root.mkdir()
            python_result = invoke(
                scenario,
                "python",
                case_root,
                fake_gh,
                args.python,
                args.dotnet,
            )
            csharp_result = invoke(
                scenario,
                "csharp",
                case_root,
                fake_gh,
                args.python,
                args.dotnet,
            )
            case_failures = compare_scenario(scenario, python_result, csharp_result)
            if case_failures:
                failures += len(case_failures)
                print(f"FAIL {scenario.name}")
                for failure in case_failures:
                    print(f"  {failure}")
                print(f"  {summarize_invocation(python_result)}")
                print(f"  {summarize_invocation(csharp_result)}")
                for result in (python_result, csharp_result):
                    diagnostic = result.stderr.decode("utf-8", errors="replace").strip()
                    if diagnostic:
                        print(f"  {result.implementation} stderr: {diagnostic[:500]}")
            else:
                passed += 1
                print(f"PASS {scenario.name}")

    print(
        f"Coverage scheduling process parity: "
        f"{passed}/{len(scenarios)} cases passed, "
        f"{failures} comparison failures."
    )
    print(f"Checkout commit: {revision.stdout.strip()}")
    print(f"OS: {platform.platform()} ({platform.machine()})")
    print(
        f"Python: {platform.python_implementation()} {platform.python_version()}, "
        f"Unicode database {unicodedata.unidata_version}; integer digit limit "
        f"{MAX_PYTHON_INTEGER_DIGITS}."
    )
    print(f".NET SDK: {sdk.stdout.strip()}")
    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(main())
