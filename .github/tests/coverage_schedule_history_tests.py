#!/usr/bin/env python3
"""Offline regression tests for coverage-schedule-history.py."""

from __future__ import annotations

import contextlib
import importlib.util
import io
import json
import os
import subprocess
import tempfile
import unittest
import unittest.mock
import warnings
import zipfile
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
HELPER = ROOT / ".github/scripts/coverage-schedule-history.py"
SPEC = importlib.util.spec_from_file_location("coverage_schedule_history", HELPER)
assert SPEC is not None and SPEC.loader is not None
SCHEDULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(SCHEDULE)

_MISSING = object()


def make_zip(entries: list[tuple[str, bytes]]) -> bytes:
    buffer = io.BytesIO()
    with warnings.catch_warnings():
        warnings.simplefilter("ignore", UserWarning)
        with zipfile.ZipFile(buffer, "w", compression=zipfile.ZIP_DEFLATED) as archive:
            for name, content in entries:
                archive.writestr(name, content)
    return buffer.getvalue()


def make_unsupported_compression_zip() -> bytes:
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w", compression=zipfile.ZIP_STORED) as archive:
        archive.writestr("timings.json", timings())
    data = bytearray(buffer.getvalue())
    local_header = data.index(b"PK\x03\x04")
    central_header = data.index(b"PK\x01\x02")
    data[local_header + 8 : local_header + 10] = (99).to_bytes(2, "little")
    data[central_header + 10 : central_header + 12] = (99).to_bytes(2, "little")
    return bytes(data)


def make_corrupt_deflate_zip() -> bytes:
    data = bytearray(make_zip([("timings.json", timings())]))
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        info = archive.getinfo("timings.json")
        local_header = info.header_offset
    filename_length = int.from_bytes(data[local_header + 26 : local_header + 28], "little")
    extra_length = int.from_bytes(data[local_header + 28 : local_header + 30], "little")
    compressed_data = local_header + 30 + filename_length + extra_length
    data[compressed_data] = 0x07
    return bytes(data)


def make_encrypted_flag_zip() -> bytes:
    data = bytearray(make_zip([("timings.json", timings())]))
    local_header = data.index(b"PK\x03\x04")
    central_header = data.index(b"PK\x01\x02")
    local_flags = int.from_bytes(data[local_header + 6 : local_header + 8], "little")
    central_flags = int.from_bytes(data[central_header + 8 : central_header + 10], "little")
    data[local_header + 6 : local_header + 8] = (local_flags | 1).to_bytes(2, "little")
    data[central_header + 8 : central_header + 10] = (central_flags | 1).to_bytes(2, "little")
    return bytes(data)


def timings(project: str = "src/App/App.csproj", seconds: object = 84) -> bytes:
    return json.dumps({"projects": [{"project": project, "seconds": seconds}]}).encode()


def run_record(
    run_id: int,
    *,
    branch: str = "main",
    status: str = "completed",
    conclusion: str = "success",
    created_at: str = "2026-11-09T12:00:00Z",
    event: object = "push",
    head_repository: object = "forge-trust/AppSurface",
) -> dict[str, object]:
    record: dict[str, object] = {
        "id": run_id,
        "head_branch": branch,
        "status": status,
        "conclusion": conclusion,
        "created_at": created_at,
    }
    if event is not _MISSING:
        record["event"] = event
    if head_repository is not _MISSING:
        record["head_repository"] = (
            {"full_name": head_repository} if isinstance(head_repository, str) else head_repository
        )
    return record


def artifact(
    artifact_id: int,
    name: str = "coverage-schedule-timings",
    *,
    expired: bool = False,
    size_in_bytes: object = 1024,
) -> dict[str, object]:
    return {
        "id": artifact_id,
        "name": name,
        "expired": expired,
        "size_in_bytes": size_in_bytes,
    }


class CoverageScheduleHistoryTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory(prefix="coverage-schedule-history-")
        self.root = Path(self.temp.name)
        self.repository = "forge-trust/AppSurface"
        self.branch = "main"
        self.runs_endpoint = (
            "repos/forge-trust/AppSurface/actions/workflows/build.yml/runs"
            "?branch=main&event=push&status=success&per_page=10"
        )
        self.env = {
            "GITHUB_REPOSITORY": self.repository,
            "GITHUB_DEFAULT_BRANCH": self.branch,
            "GH_TOKEN": "test-token",
        }

    def tearDown(self) -> None:
        self.temp.cleanup()

    def invoke(
        self,
        responses: dict[str, tuple[int, bytes]],
        *,
        output: Path | None = None,
    ) -> tuple[int, list[str], str]:
        output = output or self.root / "nested" / "timings.json"
        calls: list[str] = []

        def fake_run(command: list[str], **_: object) -> subprocess.CompletedProcess[bytes]:
            self.assertEqual(command[:4], ["gh", "api", "--method", "GET"])
            endpoint = command[4]
            calls.append(endpoint)
            if endpoint not in responses:
                raise AssertionError(f"unexpected gh api request: {endpoint}")
            code, body = responses[endpoint]
            return subprocess.CompletedProcess(command, code, stdout=body, stderr=b"simulated")

        stderr = io.StringIO()
        with unittest.mock.patch.object(SCHEDULE.subprocess, "run", side_effect=fake_run), unittest.mock.patch.dict(
            os.environ, self.env
        ), contextlib.redirect_stderr(stderr):
            result = SCHEDULE.main(["restore", "--output", str(output)])
        return result, calls, stderr.getvalue()

    def list_response(self, runs: list[dict[str, object]]) -> tuple[int, bytes]:
        return 0, json.dumps({"workflow_runs": runs}).encode()

    def artifact_response(self, artifacts: list[dict[str, object]]) -> tuple[int, bytes]:
        return 0, json.dumps({"artifacts": artifacts}).encode()

    def test_restores_only_valid_exact_nonexpired_artifact(self) -> None:
        output = self.root / "created" / "timings.json"
        responses = {
            self.runs_endpoint: self.list_response([run_record(40)]),
            "repos/forge-trust/AppSurface/actions/runs/40/artifacts?per_page=100": self.artifact_response(
                [
                    artifact(91, expired=True),
                    artifact(92, name="coverage-schedule-timings-extra"),
                    artifact(93),
                ]
            ),
            "repos/forge-trust/AppSurface/actions/artifacts/93/zip": (
                0,
                make_zip(
                    [
                        ("nested/timings.json", b'{"projects":[]}'),
                        ("notes.txt", b"ignored"),
                        ("timings.json", timings()),
                    ]
                ),
            ),
        }
        code, calls, stderr = self.invoke(responses, output=output)
        self.assertEqual(code, 0)
        self.assertEqual(stderr, "")
        self.assertEqual(json.loads(output.read_text()), {"projects": [{"project": "src/App/App.csproj", "seconds": 84}]})
        self.assertEqual(
            calls,
            [
                self.runs_endpoint,
                "repos/forge-trust/AppSurface/actions/runs/40/artifacts?per_page=100",
                "repos/forge-trust/AppSurface/actions/artifacts/93/zip",
            ],
        )

    def test_no_history_and_api_failure_succeed_without_creating_output_or_parent(self) -> None:
        for response, expected_warning in [
            (self.list_response([]), "no successful completed build runs"),
            ((47, b"authentication failed"), "GitHub workflow history is unavailable"),
            ((0, b'{"workflow_runs":' + b"1" * 5000 + b"}"), "GitHub workflow history is unavailable"),
        ]:
            with self.subTest(response=response[0]):
                output = self.root / f"missing-{response[0]}" / "timings.json"
                code, calls, stderr = self.invoke({self.runs_endpoint: response}, output=output)
                self.assertEqual(code, 0)
                self.assertIn(expected_warning, stderr)
                self.assertFalse(output.exists())
                self.assertFalse(output.parent.exists())
                self.assertEqual(calls, [self.runs_endpoint])

    def test_only_default_branch_completed_successes_are_eligible(self) -> None:
        endpoint = "repos/forge-trust/AppSurface/actions/runs/1/artifacts?per_page=100"
        zip_endpoint = "repos/forge-trust/AppSurface/actions/artifacts/11/zip"
        responses = {
            self.runs_endpoint: self.list_response(
                [
                    run_record(99, branch="feature", created_at="2026-11-10T12:00:00Z"),
                    run_record(98, status="in_progress", created_at="2026-11-10T11:00:00Z"),
                    run_record(97, conclusion="failure", created_at="2026-11-10T10:00:00Z"),
                    run_record(1, created_at="2026-11-08T12:00:00Z"),
                ]
            ),
            endpoint: self.artifact_response([artifact(11)]),
            zip_endpoint: (0, make_zip([("timings.json", timings())])),
        }
        code, calls, _ = self.invoke(responses)
        self.assertEqual(code, 0)
        self.assertEqual(calls[0], self.runs_endpoint)
        self.assertNotIn("actions/runs/99/", " ".join(calls))
        self.assertNotIn("actions/runs/98/", " ".join(calls))
        self.assertNotIn("actions/runs/97/", " ".join(calls))
        self.assertIn("actions/runs/1/", " ".join(calls))

    def test_only_push_runs_from_the_current_repository_are_eligible(self) -> None:
        records = [
            run_record(90, event="pull_request", head_repository="fork-owner/AppSurface"),
            run_record(89, event="workflow_dispatch"),
            run_record(88, head_repository="another-owner/AppSurface"),
            run_record(1, head_repository="FORGE-TRUST/APPSURFACE"),
        ]
        with unittest.mock.patch.object(SCHEDULE, "_api_json", return_value={"workflow_runs": records}) as api_json:
            runs = SCHEDULE._workflow_runs(self.repository, self.branch, deadline=123.0)

        self.assertEqual(api_json.call_args.args[0], self.runs_endpoint)
        self.assertEqual([run["id"] for run in runs], [1])

    def test_missing_or_null_run_event_and_repository_shapes_are_ineligible(self) -> None:
        missing_event = run_record(90, event=_MISSING)
        null_event = run_record(89, event=None)
        missing_repository = run_record(88, head_repository=_MISSING)
        null_repository = run_record(87, head_repository=None)
        null_repository_name = run_record(86, head_repository={"full_name": None})
        eligible = run_record(1)
        records = [
            missing_event,
            null_event,
            missing_repository,
            null_repository,
            null_repository_name,
            eligible,
        ]
        with unittest.mock.patch.object(SCHEDULE, "_api_json", return_value={"workflow_runs": records}):
            runs = SCHEDULE._workflow_runs(self.repository, self.branch, deadline=123.0)

        self.assertEqual([run["id"] for run in runs], [1])

    def test_missing_or_null_workflow_runs_shape_falls_back_without_output(self) -> None:
        for payload in ({}, {"workflow_runs": None}):
            with self.subTest(payload=payload):
                output = self.root / f"malformed-{len(payload)}" / "timings.json"
                code, calls, stderr = self.invoke(
                    {self.runs_endpoint: (0, json.dumps(payload).encode())}, output=output
                )
                self.assertEqual(code, 0)
                self.assertIn("GitHub workflow history is unavailable", stderr)
                self.assertEqual(calls, [self.runs_endpoint])
                self.assertFalse(output.exists())

    def test_invalid_json_oversize_entry_duplicate_entries_and_duplicate_projects_are_ignored(self) -> None:
        duplicate_key = b'{"projects":[{"project":"src/App/App.csproj","project":"other.csproj","seconds":1}]}'
        duplicate_entries = make_zip(
            [("timings.json", timings()), ("timings.json", timings("src/Other/Other.csproj", 2))]
        )
        cases = [
            ("invalid-json", make_zip([("timings.json", b"not JSON")])),
            ("oversized-integer", make_zip([("timings.json",
                b'{"projects":[{"project":"a.csproj","seconds":' + b"1" * 5000 + b"}]}\n")])),
            ("oversize-entry", make_zip([("timings.json", b" " * (SCHEDULE.MAX_JSON_BYTES + 1))])),
            ("duplicate-zip-entry", duplicate_entries),
            ("duplicate-project", make_zip([("timings.json", json.dumps({"projects": [
                {"project": "src/App/App.csproj", "seconds": 1},
                {"project": "src/App/App.csproj", "seconds": 2},
            ]}).encode())])),
            ("case-insensitive-duplicate-project", make_zip([("timings.json", json.dumps({"projects": [
                {"project": "src/App/App.csproj", "seconds": 1},
                {"project": "SRC/app/app.csproj", "seconds": 2},
            ]}).encode())])),
            ("padded-project-path", make_zip([("timings.json", timings(" src/App/App.csproj"))])),
            ("empty-projects", make_zip([("timings.json", b'{"projects":[]}')])),
            ("duplicate-json-key", make_zip([("timings.json", duplicate_key)])),
            ("unsafe-project", make_zip([("timings.json", timings("../App.csproj"))])),
            ("boolean-seconds", make_zip([("timings.json", timings(seconds=True))])),
        ]
        for name, archive in cases:
            with self.subTest(case=name), warnings.catch_warnings():
                warnings.simplefilter("ignore", UserWarning)
                output = self.root / f"invalid-{name}" / "timings.json"
                responses = {
                    self.runs_endpoint: self.list_response([run_record(8)]),
                    "repos/forge-trust/AppSurface/actions/runs/8/artifacts?per_page=100": self.artifact_response(
                        [artifact(18)]
                    ),
                    "repos/forge-trust/AppSurface/actions/artifacts/18/zip": (0, archive),
                }
                code, _, stderr = self.invoke(responses, output=output)
                self.assertEqual(code, 0)
                self.assertIn("warning:", stderr)
                self.assertFalse(output.exists())

    def test_falls_back_to_previous_valid_artifact(self) -> None:
        older_zip = make_zip([("timings.json", timings("src/Previous/Previous.csproj", 31))])
        empty_newer_zip = make_zip([("timings.json", b'{"projects":[]}')])
        responses = {
            self.runs_endpoint: self.list_response(
                [
                    run_record(20, created_at="2026-11-10T12:00:00Z"),
                    run_record(19, created_at="2026-11-09T12:00:00Z"),
                ]
            ),
            "repos/forge-trust/AppSurface/actions/runs/20/artifacts?per_page=100": self.artifact_response(
                [artifact(120)]
            ),
            "repos/forge-trust/AppSurface/actions/artifacts/120/zip": (0, empty_newer_zip),
            "repos/forge-trust/AppSurface/actions/runs/19/artifacts?per_page=100": self.artifact_response(
                [artifact(119)]
            ),
            "repos/forge-trust/AppSurface/actions/artifacts/119/zip": (0, older_zip),
        }
        output = self.root / "fallback" / "timings.json"
        code, calls, stderr = self.invoke(responses, output=output)
        self.assertEqual(code, 0)
        self.assertEqual(stderr, "")
        self.assertEqual(json.loads(output.read_text()), {"projects": [{"project": "src/Previous/Previous.csproj", "seconds": 31}]})
        self.assertLess(calls.index("repos/forge-trust/AppSurface/actions/artifacts/120/zip"), calls.index("repos/forge-trust/AppSurface/actions/artifacts/119/zip"))

    def test_invalid_artifact_size_metadata_is_skipped_before_download(self) -> None:
        older_zip = make_zip([("timings.json", timings("src/Previous/Previous.csproj", 31))])
        responses = {
            self.runs_endpoint: self.list_response(
                [
                    run_record(20, created_at="2026-11-10T12:00:00Z"),
                    run_record(19, created_at="2026-11-09T12:00:00Z"),
                ]
            ),
            "repos/forge-trust/AppSurface/actions/runs/20/artifacts?per_page=100": self.artifact_response(
                [
                    artifact(120, size_in_bytes="1024"),
                    artifact(121, size_in_bytes=SCHEDULE.MAX_ZIP_BYTES + 1),
                    artifact(122, size_in_bytes=-1),
                ]
            ),
            "repos/forge-trust/AppSurface/actions/runs/19/artifacts?per_page=100": self.artifact_response(
                [artifact(119)]
            ),
            "repos/forge-trust/AppSurface/actions/artifacts/119/zip": (0, older_zip),
        }
        output = self.root / "size-metadata-fallback" / "timings.json"
        code, calls, stderr = self.invoke(responses, output=output)

        self.assertEqual(code, 0)
        self.assertEqual(stderr, "")
        self.assertEqual(
            json.loads(output.read_text()),
            {"projects": [{"project": "src/Previous/Previous.csproj", "seconds": 31}]},
        )
        self.assertNotIn("repos/forge-trust/AppSurface/actions/artifacts/120/zip", calls)
        self.assertNotIn("repos/forge-trust/AppSurface/actions/artifacts/121/zip", calls)
        self.assertNotIn("repos/forge-trust/AppSurface/actions/artifacts/122/zip", calls)

    def test_unsupported_compression_and_corrupt_deflate_fall_back_without_output(self) -> None:
        malformed_archives = [
            ("unsupported-compression", make_unsupported_compression_zip()),
            ("encrypted-flag", make_encrypted_flag_zip()),
            ("corrupt-deflate", make_corrupt_deflate_zip()),
        ]
        for name, archive in malformed_archives:
            with self.subTest(archive=name):
                output = self.root / f"malformed-zip-{name}" / "timings.json"
                responses = {
                    self.runs_endpoint: self.list_response([run_record(8)]),
                    "repos/forge-trust/AppSurface/actions/runs/8/artifacts?per_page=100": self.artifact_response(
                        [artifact(18)]
                    ),
                    "repos/forge-trust/AppSurface/actions/artifacts/18/zip": (0, archive),
                }
                code, calls, stderr = self.invoke(responses, output=output)
                self.assertEqual(code, 0)
                self.assertIn("no valid coverage schedule timings artifact", stderr)
                self.assertEqual(calls[-1], "repos/forge-trust/AppSurface/actions/artifacts/18/zip")
                self.assertFalse(output.exists())
                self.assertFalse(output.parent.exists())

    def test_total_api_deadline_bounds_each_request(self) -> None:
        clock = [100.0]
        calls: list[tuple[str, float]] = []

        def fake_run(command: list[str], **kwargs: object) -> subprocess.CompletedProcess[bytes]:
            endpoint = command[4]
            timeout = float(kwargs["timeout"])
            calls.append((endpoint, timeout))
            if len(calls) == 1:
                clock[0] += 20
                body = self.list_response([run_record(20)])[1]
            elif len(calls) == 2:
                clock[0] += 20
                body = self.artifact_response([artifact(120)])[1]
            else:
                raise subprocess.TimeoutExpired(command, timeout)
            return subprocess.CompletedProcess(command, 0, stdout=body, stderr=b"")

        output = self.root / "deadline" / "timings.json"
        stderr = io.StringIO()
        with unittest.mock.patch.object(SCHEDULE.subprocess, "run", side_effect=fake_run), unittest.mock.patch.object(
            SCHEDULE.time, "monotonic", side_effect=lambda: clock[0]
        ), unittest.mock.patch.dict(os.environ, self.env), contextlib.redirect_stderr(stderr):
            result = SCHEDULE.main(["restore", "--output", str(output)])
        self.assertEqual(result, 0)
        self.assertIn("warning: GitHub artifact download is unavailable", stderr.getvalue())
        self.assertEqual(len(calls), 3)
        self.assertEqual([timeout for _, timeout in calls], [30.0, 25.0, 5.0])
        self.assertFalse(output.exists())

    def test_api_failure_does_not_fall_back_or_create_output(self) -> None:
        output = self.root / "api-failure" / "timings.json"
        responses = {
            self.runs_endpoint: self.list_response(
                [
                    run_record(20, created_at="2026-11-10T12:00:00Z"),
                    run_record(19, created_at="2026-11-09T12:00:00Z"),
                ]
            ),
            "repos/forge-trust/AppSurface/actions/runs/20/artifacts?per_page=100": (47, b"temporarily unavailable"),
        }
        code, calls, stderr = self.invoke(responses, output=output)
        self.assertEqual(code, 0)
        self.assertIn("warning: GitHub artifact history is unavailable", stderr)
        self.assertFalse(output.exists())
        self.assertEqual(
            calls,
            [
                self.runs_endpoint,
                "repos/forge-trust/AppSurface/actions/runs/20/artifacts?per_page=100",
            ],
        )

    def test_existing_output_is_never_clobbered_and_is_rejected_before_api_call(self) -> None:
        output = self.root / "already-there.json"
        output.write_text("keep me")
        calls: list[str] = []
        stderr = io.StringIO()
        with unittest.mock.patch.object(SCHEDULE.subprocess, "run", side_effect=lambda *args, **kwargs: calls.append("called")), unittest.mock.patch.dict(
            os.environ, self.env
        ), contextlib.redirect_stderr(stderr), self.assertRaises(SystemExit) as raised:
            SCHEDULE.main(["restore", "--output", str(output)])
        self.assertEqual(raised.exception.code, 2)
        self.assertIn("already exists", stderr.getvalue())
        self.assertEqual(output.read_text(), "keep me")
        self.assertEqual(calls, [])

    def test_required_configuration_and_absolute_output_are_fatal(self) -> None:
        with unittest.mock.patch.dict(os.environ, {"GITHUB_REPOSITORY": self.repository}, clear=True), contextlib.redirect_stderr(
            io.StringIO()
        ), self.assertRaises(SystemExit) as missing:
            SCHEDULE.main(["restore", "--output", str(self.root / "output.json")])
        self.assertEqual(missing.exception.code, 2)
        with unittest.mock.patch.dict(os.environ, self.env), contextlib.redirect_stderr(io.StringIO()), self.assertRaises(
            SystemExit
        ) as relative:
            SCHEDULE.main(["restore", "--output", "relative.json"])
        self.assertEqual(relative.exception.code, 2)


if __name__ == "__main__":
    unittest.main()
