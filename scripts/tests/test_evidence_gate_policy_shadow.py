"""Synthetic Git-object tests for the trusted policy-shadow preparer."""

from __future__ import annotations

import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import unittest


SCRIPT = Path(__file__).resolve().parents[1] / "evidence-gate-policy-shadow.py"
SPEC = importlib.util.spec_from_file_location("evidence_gate_policy_shadow", SCRIPT)
assert SPEC is not None and SPEC.loader is not None
policy_shadow = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = policy_shadow
SPEC.loader.exec_module(policy_shadow)


POLICY_PATH = ".appsurface/evidence/evidence.policy.json"
FIXTURES_PATH = "docs/fixtures/issue-777-policy-shadow/fixtures.json"


def run_git(repository: Path, *arguments: str) -> bytes:
    result = subprocess.run(
        ["git", *arguments],
        cwd=repository,
        check=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        shell=False,
    )
    return result.stdout


class EvidenceGatePolicyShadowTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary_directory = tempfile.TemporaryDirectory(prefix="evidence-gate-policy-shadow-tests-")
        self.root = Path(self.temporary_directory.name).resolve()
        self.protected_base = self.root / "protected-base"
        self._write_file(self.protected_base, POLICY_PATH, b'{"protected":"policy"}\n')
        self._write_file(self.protected_base, FIXTURES_PATH, b"[]\n")

    def tearDown(self) -> None:
        self.temporary_directory.cleanup()

    @staticmethod
    def _write_file(root: Path, relative_path: str, content: bytes) -> Path:
        destination = root.joinpath(*relative_path.split("/"))
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_bytes(content)
        return destination

    def _new_worktree(self) -> tuple[Path, Path, Path]:
        capture = self.root / "capture"
        capture.mkdir()
        worktree = self.root / "candidate"
        worktree.mkdir()
        run_git(worktree, "init")
        run_git(worktree, "config", "user.name", "Policy Shadow Test")
        run_git(worktree, "config", "user.email", "policy-shadow@example.invalid")
        return capture, worktree, capture / "repository.git"

    def _commit(self, worktree: Path, message: str) -> str:
        run_git(worktree, "add", "--all")
        run_git(worktree, "commit", "--quiet", "-m", message)
        return run_git(worktree, "rev-parse", "HEAD").decode("ascii").strip()

    @staticmethod
    def _write_identity(capture: Path, head_revision: str) -> None:
        identity = {
            "BaseRevision": "a" * len(head_revision),
            "HeadRepositoryId": 123,
            "HeadRevision": head_revision,
            "PullRequestNumber": 777,
            "RepositoryId": 123,
            "TargetBranch": "main",
            "WorkflowRunAttempt": 1,
            "WorkflowRunId": 456,
        }
        (capture / "pull-request-run-identity.json").write_text(
            json.dumps(identity, separators=(",", ":")),
            encoding="utf-8",
        )

    def _publish_bare_store(self, capture: Path, worktree: Path, repository: Path, head_revision: str) -> Path:
        subprocess.run(
            ["git", "clone", "--quiet", "--bare", str(worktree), str(repository)],
            check=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            shell=False,
        )
        self._write_identity(capture, head_revision)
        return capture

    def _prepare(self, capture: Path, name: str = "prepared") -> Path:
        output = self.root / name
        return policy_shadow.prepare(capture, self.protected_base, output)

    def test_extracts_only_blobs_at_exact_captured_head(self) -> None:
        capture, worktree, repository = self._new_worktree()
        self._write_file(worktree, POLICY_PATH, b'{"revision":"initial"}\n')
        self._write_file(worktree, FIXTURES_PATH, b'[{"id":"initial"}]\n')
        self._commit(worktree, "initial candidate")
        head_policy = b'{"revision":"captured-head"}\n'
        head_fixtures = b'[{"id":"captured-head"}]\n'
        self._write_file(worktree, POLICY_PATH, head_policy)
        self._write_file(worktree, FIXTURES_PATH, head_fixtures)
        captured_head = self._commit(worktree, "captured head")
        self._write_file(worktree, POLICY_PATH, b'{"revision":"later-branch-tip"}\n')
        self._write_file(worktree, FIXTURES_PATH, b'[{"id":"later-branch-tip"}]\n')
        self._commit(worktree, "later branch tip")
        self._publish_bare_store(capture, worktree, repository, captured_head)

        output = self._prepare(capture)

        self.assertEqual(head_policy, (output / POLICY_PATH).read_bytes())
        self.assertEqual(head_fixtures, (output / FIXTURES_PATH).read_bytes())

    def test_changed_policy_is_copied_as_candidate_data(self) -> None:
        capture, worktree, repository = self._new_worktree()
        self._write_file(worktree, POLICY_PATH, b'{"policy":"candidate-change"}\n')
        self._write_file(worktree, FIXTURES_PATH, b"[]\n")
        head = self._commit(worktree, "candidate policy change")
        self._publish_bare_store(capture, worktree, repository, head)

        output = self._prepare(capture)

        self.assertEqual(b'{"policy":"candidate-change"}\n', (output / POLICY_PATH).read_bytes())
        self.assertEqual(b'{"protected":"policy"}\n', (self.protected_base / POLICY_PATH).read_bytes())

    def test_deleted_candidate_fixtures_remain_absent(self) -> None:
        capture, worktree, repository = self._new_worktree()
        self._write_file(worktree, POLICY_PATH, b'{"policy":"candidate"}\n')
        self._write_file(worktree, FIXTURES_PATH, b'[{"id":"deleted-later"}]\n')
        self._commit(worktree, "with candidate fixtures")
        (worktree / FIXTURES_PATH).unlink()
        head = self._commit(worktree, "delete candidate fixtures")
        self._publish_bare_store(capture, worktree, repository, head)

        output = self._prepare(capture)

        self.assertTrue((output / POLICY_PATH).is_file())
        self.assertFalse((output / FIXTURES_PATH).exists())

    def test_symbolic_link_mode_is_rejected(self) -> None:
        capture, worktree, repository = self._new_worktree()
        policy = worktree / POLICY_PATH
        policy.parent.mkdir(parents=True, exist_ok=True)
        policy.symlink_to("candidate-policy-target.json")
        self._write_file(worktree, FIXTURES_PATH, b"[]\n")
        head = self._commit(worktree, "symlink candidate policy")
        self._publish_bare_store(capture, worktree, repository, head)

        output = self.root / "unsafe-output"
        with self.assertRaisesRegex(policy_shadow.PolicyShadowPreparerError, "unsafe Git file mode"):
            policy_shadow.prepare(capture, self.protected_base, output)

        self.assertFalse(output.exists())

    def test_executable_blob_mode_is_rejected(self) -> None:
        capture, worktree, repository = self._new_worktree()
        run_git(worktree, "config", "core.filemode", "true")
        policy = self._write_file(worktree, POLICY_PATH, b'{"policy":"executable"}\n')
        os.chmod(policy, 0o755)
        self._write_file(worktree, FIXTURES_PATH, b"[]\n")
        head = self._commit(worktree, "executable candidate policy")
        self._publish_bare_store(capture, worktree, repository, head)

        output = self.root / "executable-output"
        with self.assertRaisesRegex(policy_shadow.PolicyShadowPreparerError, "unsafe Git file mode"):
            policy_shadow.prepare(capture, self.protected_base, output)

        self.assertFalse(output.exists())

    def test_missing_candidate_policy_fails_closed(self) -> None:
        capture, worktree, repository = self._new_worktree()
        self._write_file(worktree, FIXTURES_PATH, b"[]\n")
        head = self._commit(worktree, "candidate policy missing")
        self._publish_bare_store(capture, worktree, repository, head)

        output = self.root / "missing-policy-output"
        with self.assertRaisesRegex(policy_shadow.PolicyShadowPreparerError, "missing its required candidate policy"):
            policy_shadow.prepare(capture, self.protected_base, output)

        self.assertFalse(output.exists())

    def test_oversized_candidate_blob_fails_closed_before_output_creation(self) -> None:
        capture, worktree, repository = self._new_worktree()
        self._write_file(worktree, POLICY_PATH, b"p" * (policy_shadow.MAX_FILE_BYTES + 1))
        self._write_file(worktree, FIXTURES_PATH, b"[]\n")
        head = self._commit(worktree, "oversized candidate policy")
        self._publish_bare_store(capture, worktree, repository, head)

        output = self.root / "oversized-output"
        with self.assertRaisesRegex(policy_shadow.PolicyShadowPreparerError, "byte limit"):
            policy_shadow.prepare(capture, self.protected_base, output)

        self.assertFalse(output.exists())

    def test_invalid_head_revision_is_rejected_before_git_invocation(self) -> None:
        capture, _worktree, _repository = self._new_worktree()
        identity = {
            "BaseRevision": "a" * 40,
            "HeadRepositoryId": 123,
            "HeadRevision": "A" * 40,
            "PullRequestNumber": 777,
            "RepositoryId": 123,
            "TargetBranch": "main",
            "WorkflowRunAttempt": 1,
            "WorkflowRunId": 456,
        }
        (capture / "pull-request-run-identity.json").write_text(json.dumps(identity), encoding="utf-8")

        with self.assertRaisesRegex(policy_shadow.PolicyShadowPreparerError, "full lower-case Git object ID"):
            policy_shadow.prepare(capture, self.protected_base, self.root / "invalid-revision-output")

    def test_nonregular_protected_input_is_rejected_without_blocking(self) -> None:
        fixture_path = self.protected_base.joinpath(*FIXTURES_PATH.split("/"))
        fixture_path.unlink()
        os.mkfifo(fixture_path)
        capture, _worktree, _repository = self._new_worktree()

        started = time.monotonic()
        with self.assertRaisesRegex(policy_shadow.PolicyShadowPreparerError, "bounded regular file"):
            policy_shadow.prepare(capture, self.protected_base, self.root / "nonregular-output")

        self.assertLess(time.monotonic() - started, 1.0)


if __name__ == "__main__":
    unittest.main()
