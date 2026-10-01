"""Focused tests for the private evidence-gate controller handoff."""

from __future__ import annotations

import importlib.util
import json
from pathlib import Path
import shutil
import sys
import subprocess
import tarfile
import tempfile
import unittest


SCRIPT = Path(__file__).resolve().parents[1] / "evidence-gate-handoff.py"
SPEC = importlib.util.spec_from_file_location("evidence_gate_handoff", SCRIPT)
assert SPEC is not None and SPEC.loader is not None
handoff = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = handoff
SPEC.loader.exec_module(handoff)


class HandoffPathTests(unittest.TestCase):
    def test_new_output_directory_can_be_created_under_physical_parent(self) -> None:
        with tempfile.TemporaryDirectory(prefix="evidence-gate-handoff-") as temporary:
            parent = Path(temporary).resolve() / "physical-parent"
            parent.mkdir()
            output = handoff._new_output_directory(parent / "new-handoff", "handoff output")

            self.assertEqual(parent / "new-handoff", output)
            self.assertFalse(output.exists())
            output.mkdir(mode=0o700)
            self.assertTrue(output.is_dir())


class HandoffArchiveTests(unittest.TestCase):
    def _git(self, directory: Path, *arguments: str) -> str:
        completed = subprocess.run(
            ["git", *arguments],
            cwd=directory,
            check=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
        )
        return completed.stdout.strip()

    def _capture(self, root: Path, *, attributes: str | None = None) -> tuple[Path, Path]:
        worktree = root / "worktree"
        capture = root / "capture"
        scripts = root / "trusted-scripts"
        worktree.mkdir(parents=True)
        capture.mkdir()
        scripts.mkdir()
        self._git(worktree, "init", "--quiet")
        self._git(worktree, "config", "user.name", "Evidence Gate Test")
        self._git(worktree, "config", "user.email", "evidence-gate@example.invalid")

        (worktree / "base.txt").write_text("base\n", encoding="utf-8")
        self._git(worktree, "add", "base.txt")
        self._git(worktree, "commit", "--quiet", "-m", "base")
        base_revision = self._git(worktree, "rev-parse", "HEAD")

        if attributes is not None:
            (worktree / ".gitattributes").write_text(attributes, encoding="utf-8")
        (worktree / "subject.txt").write_text("subject $Format:%H$\n", encoding="utf-8")
        (worktree / "nested").mkdir()
        (worktree / "nested" / "source.txt").write_text("nested source\n", encoding="utf-8")
        if attributes and "export-ignore" in attributes:
            (worktree / "omitted.txt").write_text("tracked but ignored by git archive\n", encoding="utf-8")
        self._git(worktree, "add", ".")
        self._git(worktree, "commit", "--quiet", "-m", "subject")
        head_revision = self._git(worktree, "rev-parse", "HEAD")
        self._git(worktree, "clone", "--bare", "--quiet", str(worktree), str(capture / "repository.git"))

        identity = {
            "BaseRevision": base_revision,
            "HeadRevision": head_revision,
            "HeadRepositoryId": 123,
            "PullRequestNumber": 7,
            "RepositoryId": 123,
            "TargetBranch": "main",
            "WorkflowRunAttempt": 1,
            "WorkflowRunId": 456,
        }
        (capture / "pull-request-run-identity.json").write_bytes(handoff._canonical_json(identity))
        (capture / "source.diff").write_bytes(b"bounded test diff\n")
        for name in handoff.TRUSTED_SUBJECT_FILES:
            shutil.copyfile(SCRIPT.parent / name, scripts / name)
        return capture, scripts

    def test_valid_handoff_round_trip_accepts_sha256_source_diff(self) -> None:
        with tempfile.TemporaryDirectory(prefix="evidence-gate-round-trip-") as temporary:
            root = Path(temporary).resolve()
            capture, scripts = self._capture(root)
            output_parent = root / "outputs"
            output_parent.mkdir()
            output = output_parent / "handoff"

            self.assertEqual(
                "captured",
                handoff.create_handoff(
                    capture_directory=capture,
                    trusted_scripts_directory=scripts,
                    output_directory=output,
                ),
            )

            manifest, _ = handoff._read_json(output / "handoff.json", 128 * 1024, "handoff manifest")
            identity, archive = handoff._validate_bundle_directory(output, manifest)
            self.assertIsNotNone(identity)
            self.assertEqual(identity["HeadRevision"], manifest["HeadRevision"])
            self.assertRegex(manifest["SourceDiffSha256"], r"^[0-9a-f]{64}$")
            self.assertIsNotNone(archive)

            result_path = root / "subject-result.json"
            exit_code = handoff.execute_handoff(
                handoff_directory=output,
                subject_checkout=root / "subject-checkout",
                scratch_directory=root / "subject-scratch",
                result_path=result_path,
                image_digest=None,
                environment={
                    "GITHUB_RUN_ID": "456",
                    "GITHUB_RUN_ATTEMPT": "1",
                    "GITHUB_REPOSITORY_ID": "123",
                },
            )

            self.assertEqual(2, exit_code)
            result = json.loads(result_path.read_text(encoding="utf-8"))
            self.assertFalse(result["claimEligible"])
            self.assertEqual("ASEGH008", result["diagnostic"]["code"])

    def test_export_ignore_omission_is_rejected_against_git_tree(self) -> None:
        with tempfile.TemporaryDirectory(prefix="evidence-gate-export-ignore-") as temporary:
            root = Path(temporary).resolve()
            capture, scripts = self._capture(root, attributes="omitted.txt export-ignore\n")
            output_parent = root / "outputs"
            output_parent.mkdir()

            with self.assertRaisesRegex(handoff.HandoffError, "omits or adds a regular file") as failure:
                handoff.create_handoff(
                    capture_directory=capture,
                    trusted_scripts_directory=scripts,
                    output_directory=output_parent / "handoff",
                )

            self.assertEqual("ASEGH004", failure.exception.code)

    def test_export_subst_transformation_is_rejected_against_git_blob(self) -> None:
        with tempfile.TemporaryDirectory(prefix="evidence-gate-export-subst-") as temporary:
            root = Path(temporary).resolve()
            capture, scripts = self._capture(root, attributes="subject.txt export-subst\n")
            output_parent = root / "outputs"
            output_parent.mkdir()

            with self.assertRaisesRegex(handoff.HandoffError, "differs from its captured Git blob") as failure:
                handoff.create_handoff(
                    capture_directory=capture,
                    trusted_scripts_directory=scripts,
                    output_directory=output_parent / "handoff",
                )

            self.assertEqual("ASEGH004", failure.exception.code)

    def test_archive_case_collision_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory(prefix="evidence-gate-case-collision-") as temporary:
            archive_path = Path(temporary) / "snapshot.tar"
            with tarfile.open(archive_path, "w") as archive:
                for name in ("Directory", "directory"):
                    member = tarfile.TarInfo(name)
                    member.type = tarfile.DIRTYPE
                    archive.addfile(member)

            with self.assertRaisesRegex(handoff.HandoffError, "case-colliding") as failure:
                handoff._scan_archive(archive_path)

            self.assertEqual("ASEGH004", failure.exception.code)


if __name__ == "__main__":
    unittest.main()
