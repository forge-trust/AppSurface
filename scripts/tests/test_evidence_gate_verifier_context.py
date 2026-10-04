"""Tests for base-owned workflow and subject-job identity resolution."""

from __future__ import annotations

from contextlib import redirect_stdout
import importlib.util
import io
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch


SCRIPT = Path(__file__).resolve().parents[1] / "evidence-gate-verifier-context.py"
SPEC = importlib.util.spec_from_file_location("evidence_gate_verifier_context", SCRIPT)
assert SPEC is not None and SPEC.loader is not None
context = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = context
SPEC.loader.exec_module(context)


class VerifierContextTests(unittest.TestCase):
    def setUp(self) -> None:
        self.identity = {
            "HeadRepositoryId": 321,
            "PullRequestNumber": 777,
            "RepositoryId": 321,
            "TargetBranch": "main",
            "WorkflowRunAttempt": 2,
            "WorkflowRunId": 654321,
        }
        self.environment = {
            "GITHUB_REPOSITORY": "forge-trust/AppSurface",
            "GITHUB_REPOSITORY_ID": "321",
            "GITHUB_RUN_ID": "654321",
            "GITHUB_RUN_ATTEMPT": "2",
            "GITHUB_EVENT_NAME": "pull_request_target",
        }
        self.prefix = "repos/forge-trust/AppSurface/actions/runs/654321/attempts/2"
        self.workflow = {
            "id": 654321,
            "run_attempt": 2,
            "workflow_id": 1234,
            "event": "pull_request_target",
            "repository": {"id": 321, "full_name": "forge-trust/AppSurface"},
        }
        self.jobs = {
            "total_count": 2,
            "jobs": [
                {"id": 5555, "run_id": 654321, "name": "Evidence gate controller capture (non-claiming)", "status": "completed", "conclusion": "success"},
                {"id": 5678, "run_id": 654321, "name": context.SUBJECT_JOB_NAME, "status": "completed", "conclusion": "success"},
            ],
        }

    def _api(self, path: str) -> dict[str, object]:
        if path == self.prefix:
            return self.workflow
        if path == self.prefix + "/jobs?per_page=100":
            return self.jobs
        self.fail(f"Unexpected API path: {path}")

    def test_resolves_attempt_specific_identity_without_job_attempt_field(self) -> None:
        expected = context.resolve_identity(self.identity, self.environment, self._api)

        self.assertEqual("1234", expected["WorkflowId"])
        self.assertEqual("5678", expected["SubjectJobId"])
        self.assertEqual("pull_request_target", expected["EventName"])
        self.assertEqual(self.identity, expected["RunIdentity"])
        self.assertEqual(
            b'{"EventName":"pull_request_target","Repository":"forge-trust/AppSurface","RunIdentity":{"HeadRepositoryId":321,"PullRequestNumber":777,"RepositoryId":321,"TargetBranch":"main","WorkflowRunAttempt":2,"WorkflowRunId":654321},"SubjectJobId":"5678","WorkflowId":"1234"}',
            context._canonical(expected),
        )

    def test_rejects_stale_or_wrong_workflow_context_before_job_lookup(self) -> None:
        for field, value in (
            ("GITHUB_REPOSITORY_ID", "322"),
            ("GITHUB_RUN_ATTEMPT", "1"),
            ("GITHUB_EVENT_NAME", "pull_request"),
        ):
            with self.subTest(field=field):
                environment = dict(self.environment, **{field: value})
                with self.assertRaises(context.VerifierContextError):
                    context.resolve_identity(self.identity, environment, self._api)

    def test_rejects_wrong_attempt_or_workflow_repository(self) -> None:
        for key, value in (("run_attempt", 1), ("id", 654322), ("event", "push")):
            with self.subTest(key=key):
                workflow = dict(self.workflow, **{key: value})
                with self.assertRaises(context.VerifierContextError):
                    context.resolve_identity(self.identity, self.environment, lambda path: workflow if path == self.prefix else self.jobs)
        workflow = dict(self.workflow, repository={"id": 322, "full_name": "forge-trust/AppSurface"})
        with self.assertRaises(context.VerifierContextError):
            context.resolve_identity(self.identity, self.environment, lambda path: workflow if path == self.prefix else self.jobs)

    def test_rejects_ambiguous_failed_or_wrong_run_subject_job(self) -> None:
        variants = [
            {"total_count": 3, "jobs": [*self.jobs["jobs"], dict(self.jobs["jobs"][1], id=9999)]},
            {"total_count": 2, "jobs": [self.jobs["jobs"][0], dict(self.jobs["jobs"][1], conclusion="failure")]},
            {"total_count": 2, "jobs": [self.jobs["jobs"][0], dict(self.jobs["jobs"][1], run_id=654322)]},
            {"total_count": 2, "jobs": [self.jobs["jobs"][0], dict(self.jobs["jobs"][1], run_attempt=1)]},
            {"total_count": 3, "jobs": self.jobs["jobs"]},
        ]
        for jobs in variants:
            with self.subTest(jobs=jobs):
                with self.assertRaises(context.VerifierContextError):
                    context.resolve_identity(self.identity, self.environment, lambda path: self.workflow if path == self.prefix else jobs)

    def test_accepts_matching_optional_job_attempt(self) -> None:
        self.jobs["jobs"][1]["run_attempt"] = 2
        self.assertEqual("5678", context.resolve_identity(self.identity, self.environment, self._api)["SubjectJobId"])

    def test_reads_only_canonical_regular_controller_identity(self) -> None:
        with tempfile.TemporaryDirectory(prefix="evidence-verifier-context-") as temporary:
            root = Path(temporary)
            identity = root / "identity.json"
            identity.write_bytes(context._canonical(self.identity))
            self.assertEqual(self.identity, context._read_identity(identity))

            identity.write_bytes(json.dumps(self.identity, indent=2).encode("utf-8"))
            with self.assertRaises(context.VerifierContextError):
                context._read_identity(identity)

            identity.write_bytes(context._canonical(self.identity))
            link = root / "link.json"
            link.symlink_to(identity)
            with self.assertRaises(context.VerifierContextError):
                context._read_identity(link)

    def test_writes_new_identity_without_replacing_existing_file(self) -> None:
        with tempfile.TemporaryDirectory(prefix="evidence-verifier-context-") as temporary:
            output = Path(temporary) / "expected.json"
            expected = context.resolve_identity(self.identity, self.environment, self._api)
            context.write_new_identity(output, expected)
            self.assertEqual(expected, json.loads(output.read_bytes()))
            with self.assertRaises(context.VerifierContextError):
                context.write_new_identity(output, expected)

    def test_main_reads_fresh_identity_and_writes_expected_identity(self) -> None:
        with tempfile.TemporaryDirectory(prefix="evidence-verifier-context-") as temporary:
            root = Path(temporary)
            capture = root / "capture.json"
            output = root / "expected.json"
            capture.write_bytes(context._canonical(self.identity))
            environment = dict(self.environment, GITHUB_TOKEN="test-token", GITHUB_API_URL="https://api.github.com")
            with patch.dict(os.environ, environment), patch.object(context, "_read_api", side_effect=lambda path, **_kwargs: self._api(path)):
                with redirect_stdout(io.StringIO()):
                    self.assertEqual(0, context.main(["--fresh-identity", str(capture), "--output", str(output)]))
            self.assertEqual("5678", json.loads(output.read_bytes())["SubjectJobId"])

    def test_api_reader_rejects_oversized_or_malformed_responses(self) -> None:
        class Response(io.BytesIO):
            def __init__(self, body: bytes, content_length: str | None = None) -> None:
                super().__init__(body)
                self.status = 200
                self.headers = {} if content_length is None else {"Content-Length": content_length}

        class Opener:
            def __init__(self, response: Response) -> None:
                self.response = response

            def open(self, _request: object, timeout: int) -> Response:
                assert timeout == context.API_TIMEOUT_SECONDS
                return self.response

        with patch.object(context, "build_opener", return_value=Opener(Response(b'{"id":1}'))):
            self.assertEqual({"id": 1}, context._read_api("repos/example/repo", api_base="https://api.github.com", token="test"))
        with patch.object(context, "build_opener", return_value=Opener(Response(b"{}", str(context.MAX_API_BYTES + 1)))):
            with self.assertRaises(context.VerifierContextError):
                context._read_api("repos/example/repo", api_base="https://api.github.com", token="test")
        with patch.object(context, "build_opener", return_value=Opener(Response(b"{"))):
            with self.assertRaises(context.VerifierContextError):
                context._read_api("repos/example/repo", api_base="https://api.github.com", token="test")


if __name__ == "__main__":
    unittest.main()
