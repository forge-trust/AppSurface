"""Exercise the actual CI mirror preflight on ordinary files; no APT/native credit."""

import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[1]
WORKFLOWS = ("issue779-checkpoint1-worker.yml", "issue779-checkpoint1-measurement.yml")
AZURE = "http://azure.archive.ubuntu.com/ubuntu"
EC2 = "http://us-east-2.ec2.archive.ubuntu.com/ubuntu/"
CANONICAL = "https://archive.ubuntu.com/ubuntu/"
SECURITY = "https://security.ubuntu.com/ubuntu/\tpriority:3\n"


def mirror_preflight(workflow):
    source = (ROOT / "workflows" / workflow).read_text()
    start = source.index('          cp /etc/apt/apt-mirrors.txt ')
    end = source.index('          # Bound stalled mirror connections', start)
    return "\n".join(line[10:] for line in source[start:end].splitlines())


@unittest.skipUnless(sys.platform == "linux", "Workflow uses Linux GNU sed and Bash")
class RuntimeMirrorControls(unittest.TestCase):
    def run_preflight(self, workflow, mirrors, *, omit_rewrite=False):
        temporary = tempfile.TemporaryDirectory(prefix="c1-runtime-mirrors-")
        self.addCleanup(temporary.cleanup)
        root = Path(temporary.name)
        target = root / "apt-mirrors.txt"
        target.write_text(mirrors)
        evidence = root / "evidence"
        evidence.mkdir()
        # Only replace the fixed system path; execute the workflow's original
        # cp/grep/sed commands. The sudo function avoids needing root in this test.
        script = mirror_preflight(workflow).replace(
            "/etc/apt/apt-mirrors.txt", '"$RUNTIME_MIRROR_FIXTURE"')
        sudo = ('sudo() { test "$1" = --non-interactive; shift; "$@"; }\n'
                if not omit_rewrite else 'sudo() { test "$1" = --non-interactive; return 0; }\n')
        result = subprocess.run(
            ["/bin/bash", "--noprofile", "--norc", "-c",
             'set -euo pipefail\n' + sudo
             + script + '\nprintf done > "$C1_EVIDENCE/preflight-completed"\n'],
            env=dict(os.environ, RUNTIME_MIRROR_FIXTURE=str(target), C1_EVIDENCE=str(evidence)),
            stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=10)
        return result, target, evidence

    def test_observed_ec2_list_reaches_canonical_https_and_retains_both_snapshots(self):
        mirrors = EC2 + "\tpriority:1\n" + CANONICAL + "\tpriority:2\n" + SECURITY
        expected = CANONICAL + "\tpriority:1\n" + CANONICAL + "\tpriority:2\n" + SECURITY
        for workflow in WORKFLOWS:
            with self.subTest(workflow=workflow):
                result, target, evidence = self.run_preflight(workflow, mirrors)
                self.assertEqual(0, result.returncode, result.stderr.decode())
                self.assertEqual(mirrors, (evidence / "runtime-mirrors-before.txt").read_text())
                self.assertEqual(expected, target.read_text())
                self.assertEqual(expected, (evidence / "runtime-mirrors-after.txt").read_text())
                self.assertEqual("done", (evidence / "preflight-completed").read_text())

    def test_azure_input_preserves_prior_supported_behavior_and_https_fallbacks(self):
        mirrors = AZURE + "\tpriority:1\n" + CANONICAL + "\tpriority:2\n" + SECURITY
        for workflow in WORKFLOWS:
            with self.subTest(workflow=workflow):
                result, target, evidence = self.run_preflight(workflow, mirrors)
                self.assertEqual(0, result.returncode, result.stderr.decode())
                self.assertNotIn("http://", target.read_text())
                self.assertIn(SECURITY, target.read_text())
                self.assertEqual(target.read_bytes(), (evidence / "runtime-mirrors-after.txt").read_bytes())

    def test_both_known_mirrors_are_rewritten_without_changing_priorities(self):
        mirrors = AZURE + "\tpriority:1\n" + EC2 + "\tpriority:2\n" + SECURITY
        expected = CANONICAL.rstrip("/") + "\tpriority:1\n" + CANONICAL + "\tpriority:2\n" + SECURITY
        for workflow in WORKFLOWS:
            with self.subTest(workflow=workflow):
                result, target, _ = self.run_preflight(workflow, mirrors)
                self.assertEqual(0, result.returncode, result.stderr.decode())
                self.assertEqual(expected, target.read_text())

    def test_unknown_or_missing_expected_source_rejects_before_rewrite(self):
        for workflow in WORKFLOWS:
            for mirrors in ("", CANONICAL + "\n", "http://unknown.invalid/ubuntu/\n",
                            EC2.replace("ubuntu/", "ubuntu/extra") + "\n"):
                with self.subTest(workflow=workflow, mirrors=mirrors):
                    result, target, evidence = self.run_preflight(workflow, mirrors)
                    self.assertNotEqual(0, result.returncode)
                    self.assertEqual(mirrors, target.read_text())
                    self.assertFalse((evidence / "runtime-mirrors-after.txt").exists())
                    self.assertFalse((evidence / "preflight-completed").exists())

    def test_unrelated_http_entry_cannot_survive_the_rewrite(self):
        for workflow in WORKFLOWS:
            for scheme in ("http", "HTTP", "hTtP"):
                unknown = scheme + "://unknown.invalid/ubuntu/"
                mirrors = EC2 + "\tpriority:1\n" + unknown + "\tpriority:2\n" + SECURITY
                with self.subTest(workflow=workflow, scheme=scheme):
                    result, target, evidence = self.run_preflight(workflow, mirrors)
                    self.assertNotEqual(0, result.returncode)
                    self.assertIn(b"runtime-mirror-selection-rejected", result.stderr)
                    self.assertIn(CANONICAL, target.read_text())
                    self.assertIn(unknown, target.read_text())
                    self.assertTrue((evidence / "runtime-mirrors-after.txt").is_file())
                    self.assertFalse((evidence / "preflight-completed").exists())

    def test_commented_known_url_cannot_authorize_an_unrelated_list(self):
        # A same-shape known URL in a comment cannot authorize an unrelated list.
        mirrors = "# " + AZURE + "\n" + SECURITY
        for workflow in WORKFLOWS:
            with self.subTest(workflow=workflow):
                result, target, evidence = self.run_preflight(workflow, mirrors)
                self.assertNotEqual(0, result.returncode)
                self.assertEqual(mirrors, target.read_text())
                self.assertFalse((evidence / "preflight-completed").exists())

    def test_noop_rewrite_cannot_pass_canonical_or_remaining_http_checks(self):
        for workflow in WORKFLOWS:
            for mirrors in (AZURE + "\n", EC2 + "\n" + CANONICAL + "\n"):
                with self.subTest(workflow=workflow, mirrors=mirrors):
                    result, target, evidence = self.run_preflight(workflow, mirrors, omit_rewrite=True)
                    self.assertNotEqual(0, result.returncode)
                    self.assertEqual(mirrors, target.read_text())
                    self.assertTrue((evidence / "runtime-mirrors-after.txt").is_file())
                    self.assertFalse((evidence / "preflight-completed").exists())


if __name__ == "__main__":
    unittest.main()
