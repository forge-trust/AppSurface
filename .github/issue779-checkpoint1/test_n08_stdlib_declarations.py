"""Bound N08 stdlib declaration lookup without changing physical checks."""

from pathlib import Path
import subprocess
import tempfile
import unittest

import test_n04_stdlib_declarations as n04


ROOT = Path(__file__).resolve().parent


class N08StdlibDeclarationTests(n04.N04StdlibDeclarationTests):
    script = ROOT / "procedures/slot/checkpoint-n08.sh"

    def test_current_loop_uses_initialized_hash_cache_and_keeps_physical_verification(self):
        self.assertIn(
            'python_hash=$(negative_os_file_hash "$python_file") || fail negative-stdlib-file-unpinned',
            self.loop,
        )
        self.assertNotIn("missing-fixed-pin", self.loop)
        self.assertIn('verify_os_path "$python_file" "$python_hash"', self.loop)
        self.assertIn("negative_stdlib_inventory", self.loop)

    def test_malformed_declared_digest_fails_during_cache_initialization(self):
        with tempfile.TemporaryDirectory(prefix="n08-stdlib-cache-") as temp:
            root = Path(temp)
            audit_path = root / "os-audit.json"
            audit_path.write_text(
                '{"files":[{"path":"/usr/lib/python3.12/bad.py","sha256":"bad"}],"aliases":[]}',
                encoding="utf-8",
            )
            program = r'''set -euo pipefail
fail() { printf 'REJECTED:%s\n' "$1" >&2; exit 1; }
bounded() { [[ $1 == jq ]] || fail fixture-tool; shift; command jq "$@"; }
remaining() { :; }
os_audit=$AUDIT
os_cache_ready=0
declare -a os_cache_file_paths=() os_cache_file_hashes=() os_cache_alias_paths=() os_cache_alias_rows=()
os_alias_declarations_ready=0
declare -a os_alias_resolved=() os_alias_hashes=() os_alias_starts=() os_alias_counts=()
declare -a os_alias_link_paths=() os_alias_link_targets=() os_alias_link_metadata=()
'''
            helpers = "\n".join(
                n04.shell_function(self.source, name)
                for name in (
                    "lexical",
                    "sha",
                    "negative_os_file_hash_during_initialize",
                    "negative_os_alias_index",
                    "negative_os_alias_declarations_initialize",
                    "negative_os_cache_initialize",
                    "negative_os_file_hash",
                )
            )
            result = subprocess.run(
                [
                    "/bin/bash",
                    "--noprofile",
                    "--norc",
                    "-c",
                    program + helpers + "\nnegative_os_cache_initialize\n",
                ],
                env={"PATH": "/usr/bin:/bin", "AUDIT": str(audit_path)},
                capture_output=True,
                timeout=5,
            )
        self.assertNotEqual(0, result.returncode)
        self.assertEqual(b"", result.stdout)
        self.assertIn(b"REJECTED:sha256-syntax", result.stderr)

    def test_original_240_second_root_bound_remains_strict_and_prelaunch(self):
        start_guard = self.source.index("cancellation_root_start_ms=$(monotonic_ms)")
        guard_end = self.source.index("readonly cancellation_root_start_ms cancellation_end_ms", start_guard)
        guard = self.source[start_guard:guard_end]
        self.assertIn("cancellation_root_start_ms+240000", guard)
        self.assertIn("cancellation_end_ms<work_end*1000", guard)
        self.assertIn("CLEANUP_RESERVE=30", self.source)
        self.assertIn("work_end=$((fixture_start+FIXTURE_SECONDS-CLEANUP_RESERVE))", self.source)
        final_membership = self.source.index(
            'negative_stdlib_exact "$stdlib_expected" "$stdlib_after"'
        )
        self.assertGreater(start_guard, final_membership)
        self.assertLess(guard_end, self.source.index("launched=1", guard_end))

        def evaluate(work_end):
            program = "\n".join(
                [
                    "set -euo pipefail",
                    "fail() { printf 'CHECKPOINT_REJECTED:%s\\n' \"$1\" >&2; exit 1; }",
                    "monotonic_ms() { printf '%s\\n' \"$TEST_START\"; }",
                    "work_end=$TEST_WORK_END",
                    guard,
                ]
            )
            return subprocess.run(
                ["/bin/bash", "--noprofile", "--norc", "-c", program],
                env={"PATH": "/usr/bin:/bin", "TEST_START": "100000", "TEST_WORK_END": str(work_end)},
                capture_output=True,
                timeout=2,
            )

        self.assertEqual(0, evaluate(341).returncode)
        expired = evaluate(340)
        self.assertNotEqual(0, expired.returncode)
        self.assertEqual(b"CHECKPOINT_REJECTED:cancellation-original-root-bound\n", expired.stderr)


if __name__ == "__main__":
    unittest.main()
