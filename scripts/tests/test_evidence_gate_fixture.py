"""Keep the checked-in revision-bound docs fixture tied to real Git objects."""

from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
FIXTURE = ROOT / "docs" / "fixtures" / "issue-777-docs-only"


class EvidenceGateFixtureTests(unittest.TestCase):
    def test_bundle_and_fixed_diff_match_immutable_metadata(self) -> None:
        metadata = json.loads((FIXTURE / "fixture.json").read_text())
        bundle = FIXTURE / "repository.bundle"
        source_diff = (FIXTURE / "source.diff").read_bytes()
        policy = (FIXTURE / "policy.json").read_bytes()

        self.assertEqual(metadata["bundleSha256"], hashlib.sha256(bundle.read_bytes()).hexdigest())
        self.assertEqual(metadata["sourceDiffSha256"], hashlib.sha256(source_diff).hexdigest())
        self.assertEqual(metadata["policySha256"], hashlib.sha256(policy).hexdigest())
        self.assertEqual(metadata["changedPath"], "docs/fixture-note.md")

        with tempfile.TemporaryDirectory(prefix="appsurface-777-fixture-") as temporary:
            repository = Path(temporary) / "repository"
            environment = {key: value for key, value in os.environ.items() if not key.startswith("GIT_")}
            environment.update({"GIT_CONFIG_NOSYSTEM": "1", "GIT_CONFIG_GLOBAL": os.devnull, "LC_ALL": "C"})

            def git(*arguments: str) -> bytes:
                result = subprocess.run(
                    ["git", "-C", str(repository), *arguments],
                    env=environment,
                    check=True,
                    stdout=subprocess.PIPE,
                    stderr=subprocess.PIPE,
                    timeout=30,
                )
                return result.stdout

            subprocess.run(
                ["git", "clone", "--quiet", str(bundle), str(repository)],
                env=environment,
                check=True,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                timeout=30,
            )
            base = metadata["baseRevision"]
            head = metadata["headRevision"]
            self.assertEqual(base, git("rev-parse", "fixture-base").decode().strip())
            self.assertEqual(head, git("rev-parse", "HEAD").decode().strip())

            diff_options = (
                "-c", "diff.external=", "-c", "diff.noprefix=false",
                "-c", "diff.mnemonicprefix=false", "-c", "diff.algorithm=myers",
            )
            source_options = (
                "diff", "--no-ext-diff", "--no-textconv", "--no-color", "--binary",
                "--full-index", "--unified=3", "--find-renames=50%", "--find-copies=50%",
                "--submodule=short", "--ignore-submodules=none", "--no-relative",
                "--src-prefix=a/", "--dst-prefix=b/", base, head, "--",
            )
            self.assertEqual(source_diff, git(*diff_options, *source_options))
            status = git(*diff_options, "diff", "--name-status", "-z", "--no-ext-diff",
                         "--no-textconv", "--no-color", "--find-renames=50%",
                         "--find-copies=50%", "--submodule=short", "--ignore-submodules=none",
                         "--no-relative", base, head, "--")
            self.assertEqual(b"M\0docs/fixture-note.md\0", status)


if __name__ == "__main__":
    unittest.main()
