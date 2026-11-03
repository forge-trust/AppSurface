"""Exercise the fixed N04 join-result diagnostic; no native authority."""
import subprocess
import tempfile
import unittest
from pathlib import Path


class N04JoinDiagnosticTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        source = (Path(__file__).parent / "procedures/slot/checkpoint-n04.sh").read_text()
        start = source.index("if [[ $n04_helper_exit != 0 || $negative_launch != 1")
        end = source.index("\nfi", start) + 3
        cls.block = source[start:end]

    def invoke(self, helper=0, root=1, helper_live=False, root_live=False, stdout=False):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "n01.stdout"
            path.write_bytes(b"unexpected" if stdout else b"")
            program = """set -euo pipefail
n04_helper_exit=$1; negative_launch=$2; n04_launch_pid=$3; launch_pid=$4; log=$5
fail() { printf 'CHECKPOINT_REJECTED:%s\\n' "$1" >&2; exit 1; }
""" + self.block
            return subprocess.run(["/bin/bash", "--noprofile", "--norc", "-c", program,
                                   "diagnostic", str(helper), str(root), "12" if helper_live else "",
                                   "13" if root_live else "", temporary], capture_output=True, timeout=3)

    def test_expected_joined_outcome_produces_no_diagnostic(self):
        result = self.invoke()
        self.assertEqual(0, result.returncode)
        self.assertEqual(b"", result.stdout)
        self.assertEqual(b"", result.stderr)

    def test_each_unconfirmed_outcome_stays_failed_with_only_closed_numeric_facts(self):
        rows = [dict(helper=1), dict(root=0), dict(helper=125), dict(root=124),
                dict(helper_live=True), dict(root_live=True), dict(stdout=True)]
        for row in rows:
            with self.subTest(row=row):
                result = self.invoke(**row)
                self.assertEqual(1, result.returncode)
                self.assertEqual(b"", result.stdout)
                self.assertRegex(result.stderr, rb"^N04_JOINED_OUTCOMES:helper=[0-9]+:root=[0-9]+:helper_retired=[01]:root_retired=[01]:stdout_empty=[01]\nCHECKPOINT_REJECTED:N04-owned-outcomes\n$")
                self.assertNotIn(b"unexpected", result.stderr)
