#!/usr/bin/env python3
"""Exercise the package workflow's runner guard and fixed cleanup target safely.

Run: python3 .github/tests/package_runner_capacity_tests.py
The actual workflow shell runs with recording sudo/du/df commands; nothing is deleted.
"""

from __future__ import annotations

import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
WORKFLOW = ROOT / ".github/workflows/package-artifacts.yml"


def preparation_script() -> str:
    source = WORKFLOW.read_text()
    start = source.index("      - name: Prepare package runner disk capacity\n")
    end = source.index("\n      - name:", start + 1)
    step = source[start:end]
    return "\n".join(line[10:] for line in step.split("        run: |\n", 1)[1].splitlines())


class PackageRunnerCapacityTests(unittest.TestCase):
    def run_preparation(self, runner_environment: str, runner_os: str, *, cleanup_exit: int = 0) -> tuple[subprocess.CompletedProcess, list]:
        with tempfile.TemporaryDirectory(prefix="package-runner-guards-") as temporary:
            root = Path(temporary)
            commands = root / "bin"
            commands.mkdir()
            calls = root / "calls.jsonl"
            recorder = """#!/usr/bin/env python3
import json, os, pathlib, sys
with pathlib.Path(os.environ["CAPACITY_CALLS"]).open("a") as stream:
    stream.write(json.dumps([pathlib.Path(sys.argv[0]).name, *sys.argv[1:]]) + "\\n")
if pathlib.Path(sys.argv[0]).name == "sudo":
    raise SystemExit(int(os.environ["CAPACITY_CLEANUP_EXIT"]))
"""
            for name in ("sudo", "df", "du"):
                command = commands / name
                command.write_text(recorder)
                command.chmod(0o755)
            environment = os.environ | {
                "PATH": str(commands) + os.pathsep + os.environ["PATH"],
                "CAPACITY_CALLS": str(calls),
                "CAPACITY_CLEANUP_EXIT": str(cleanup_exit),
                "RUNNER_ENVIRONMENT": runner_environment,
                "RUNNER_OS": runner_os,
                "RUNNER_TEMP": str(root / "proof workspace"),
            }
            result = subprocess.run(
                ["bash", "-c", preparation_script()], cwd=ROOT, env=environment,
                text=True, capture_output=True, check=False,
            )
            recorded = [json.loads(line) for line in calls.read_text().splitlines()] if calls.exists() else []
            return result, recorded

    def test_hosted_linux_deletes_only_unused_image_toolkit_and_reports_capacity(self) -> None:
        result, calls = self.run_preparation("github-hosted", "Linux")
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual([["sudo", "rm", "-rf", "--", "/usr/local/lib/android"]], [call for call in calls if call[0] == "sudo"])
        disk_calls = [call for call in calls if call[0] == "df"]
        self.assertEqual(["-h", "-i", "-h", "-i"], [call[1] for call in disk_calls])
        self.assertTrue(all(call[2] == "." and call[3].endswith("/proof workspace") for call in disk_calls))

    def test_other_runner_environments_fail_before_any_filesystem_command(self) -> None:
        for runner_environment, runner_os in (("self-hosted", "Linux"), ("", "Linux"), ("github-hosted", "macOS"), ("github-hosted", "Windows")):
            with self.subTest(environment=runner_environment, os=runner_os):
                result, calls = self.run_preparation(runner_environment, runner_os)
                self.assertNotEqual(0, result.returncode)
                self.assertIn("requires a disposable GitHub-hosted Linux runner", result.stdout)
                self.assertEqual([], calls)

    def test_cleanup_failure_stops_preparation(self) -> None:
        result, calls = self.run_preparation("github-hosted", "Linux", cleanup_exit=37)
        self.assertEqual(37, result.returncode)
        self.assertEqual("sudo", calls[-1][0])
        self.assertEqual(["-h", "-i"], [call[1] for call in calls if call[0] == "df"])


if __name__ == "__main__":
    unittest.main()
