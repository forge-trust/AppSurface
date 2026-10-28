"""Reuse the N04 detached declaration-loop harness against N10's current shell."""

import os
from pathlib import Path
import subprocess
import unittest

import test_n04_stdlib_declarations as n04


ROOT = Path(__file__).resolve().parent


class N10StdlibDeclarationTests(n04.N04StdlibDeclarationTests):
    script = ROOT / "procedures/n10/checkpoint-n10-pending-start.sh"

    def test_current_loop_uses_the_initialized_hash_cache_only(self):
        self.assertIn(
            'python_hash=$(negative_os_file_hash "$python_file") || fail negative-stdlib-file-unpinned',
            self.loop,
        )
        self.assertNotIn("missing-fixed-pin", self.loop)
        self.assertNotIn("negative_python_trust_recheck()", self.loop)

    def test_original_root_fit_is_rechecked_after_the_complete_stdlib_scan(self):
        guard = next(
            line.strip()
            for line in self.source.splitlines()
            if "job_deadline_epoch-$(bounded date -u +%s)>245" in line
            and "fail n10-original-root-bound" in line
        )
        self.assertGreater(self.source.index(guard), self.source.index("stdlib_after=$(negative_stdlib_inventory)"))
        self.assertLess(self.source.index(guard), self.source.index("n10_root_start_ms=$(monotonic_ms)"))

        def evaluate(*, remaining, deadline):
            program = "\n".join(
                [
                    "set -euo pipefail",
                    "fail() { printf '%s\\n' \"$1\" >&2; exit 1; }",
                    "remaining() { printf '%s\\n' \"$FIXTURE_REMAINING\"; }",
                    "bounded() { [[ $1 == date ]] || fail fixture-tool; printf '%s\\n' \"$FIXTURE_DATE\"; }",
                    "job_deadline_epoch=$FIXTURE_DEADLINE",
                    guard,
                ]
            )
            return subprocess.run(
                ["/bin/bash", "--noprofile", "--norc", "-c", program],
                env={
                    "PATH": "/usr/bin:/bin",
                    "FIXTURE_REMAINING": str(remaining),
                    "FIXTURE_DEADLINE": str(deadline),
                    "FIXTURE_DATE": "1000",
                },
                capture_output=True,
                timeout=2,
            )

        self.assertEqual(0, evaluate(remaining=247, deadline=1246).returncode)
        remaining_edge = evaluate(remaining=246, deadline=2000)
        self.assertNotEqual(0, remaining_edge.returncode)
        self.assertEqual(b"n10-original-root-bound\n", remaining_edge.stderr)
        deadline_edge = evaluate(remaining=247, deadline=1245)
        self.assertNotEqual(0, deadline_edge.returncode)
        self.assertEqual(b"n10-original-root-bound\n", deadline_edge.stderr)


if __name__ == "__main__":
    unittest.main()
