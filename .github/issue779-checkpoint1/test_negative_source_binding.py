"""Data-only controls for the negative fixture's immutable source snapshot."""
import os
from pathlib import Path
import re
import subprocess
import unittest


HERE = Path(__file__).resolve().parent
SCRIPTS = tuple(HERE / f"procedures/slot/checkpoint-n{number:02d}.sh"
                for number in (4, 5, 6, 7, 8))
DECLARATION = "readonly NEGATIVE_SOURCE_REVISION=$source_revision"


def live_guard_comparison(script):
    for line in script.splitlines():
        stripped = line.strip()
        if stripped.startswith("[[ $# == 11 && $EUID == 0 && $source_revision =="):
            match = re.search(r'\$source_revision == "\$NEGATIVE_SOURCE_REVISION"', stripped)
            if match:
                return match.group(0)
    raise AssertionError("live N03 source comparison not found")


class NegativeSourceBindingTests(unittest.TestCase):
    def setUp(self):
        self.assertNotEqual(os.geteuid(), 0, "portable shell control must run without root")

    def test_snapshot_is_after_root_binding_and_before_source_diagnostic(self):
        for path in SCRIPTS:
            with self.subTest(script=path.name):
                script = path.read_text()
                lines = script.splitlines()
                root_verify = [i for i, line in enumerate(lines) if "root-verify \\" in line]
                self.assertEqual(len(root_verify), 1)
                verify_end = next(i for i in range(root_verify[0], len(lines))
                                  if '--deadline-monotonic "$binding_deadline_monotonic" >/dev/null' in lines[i])
                declarations = [i for i, line in enumerate(lines) if line == DECLARATION]
                self.assertEqual(len(declarations), 1)
                declaration = declarations[0]
                diagnostic = lines.index("diagnostic_stage=source-input", root_verify[0])
                self.assertEqual(declaration, verify_end + 1)
                self.assertEqual(diagnostic, declaration + 1)
                self.assertLess(declaration, next(i for i, line in enumerate(lines)
                                                   if line.startswith("n03_run()")))
                live_guard_comparison(script)

    def test_matching_snapshot_passes_and_changed_source_rejects(self):
        original = "a" * 40
        changed = "b" * 40
        for path in SCRIPTS:
            with self.subTest(script=path.name):
                comparison = live_guard_comparison(path.read_text())
                command = ("set -eu; source_revision=$1; " + DECLARATION +
                           "; source_revision=$2; [[ " + comparison + " ]] ")
                matching = subprocess.run(["bash", "-c", command, "source-control", original, original],
                                          stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False)
                changed_source = subprocess.run(["bash", "-c", command, "source-control", original, changed],
                                                stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False)
                self.assertEqual(matching.returncode, 0)
                self.assertNotEqual(changed_source.returncode, 0)

    def test_snapshot_reassignment_is_rejected(self):
        original = "a" * 40
        changed = "b" * 40
        for path in SCRIPTS:
            with self.subTest(script=path.name):
                comparison = live_guard_comparison(path.read_text())
                command = ("set -eu; source_revision=$1; " + DECLARATION +
                           "; NEGATIVE_SOURCE_REVISION=$2; [[ " + comparison + " ]] ")
                result = subprocess.run(["bash", "-c", command, "source-control", original, changed],
                                        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False)
                self.assertNotEqual(result.returncode, 0)


if __name__ == "__main__":
    unittest.main()
