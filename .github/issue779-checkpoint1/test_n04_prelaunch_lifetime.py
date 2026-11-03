"""N04's actual prelaunch fit guard with detached clocks; no native authority."""
from pathlib import Path
import subprocess
import unittest


SCRIPT = Path(__file__).parent / "procedures/slot/checkpoint-n04.sh"
FIT = "(( $(remaining)>246 && job_deadline_epoch-$(bounded date -u +%s)>245 )) || fail native-life-outside-fixture"


class N04PrelaunchLifetimeTests(unittest.TestCase):
    def invoke(self, work_remaining, job_remaining):
        source = SCRIPT.read_text()
        start = source.index("readonly n04_root_start_ms n04_end_ms\n")
        end = source.index("diagnostic_stage=n01\n", start)
        prelaunch = source[start:end]
        program = r'''set -euo pipefail
fail() { printf 'CHECKPOINT_REJECTED:%s\n' "$1" >&2; exit 1; }
remaining() { printf '%s\n' "$work_remaining"; }
bounded() {
 [[ $# == 3 && $1 == date && $2 == -u && $3 == +%s ]] || exit 94
 printf '%s\n' "$job_now"
}
job_deadline_epoch=1700001000
work_remaining=500; job_now=$((job_deadline_epoch-500))
''' + FIT + r'''
# The completed audit consumes the existing allowances; neither end is reset.
work_remaining=$1; job_now=$((job_deadline_epoch-$2))
n04_root_start_ms=1000; n04_end_ms=241000
''' + prelaunch + "\nprintf 'DISPATCH\n'\n"
        return subprocess.run(
            ["/bin/bash", "--noprofile", "--norc", "-c", program,
             "prelaunch", str(work_remaining), str(job_remaining)],
            capture_output=True, timeout=3)

    def test_work_allowance_consumed_by_audit_prevents_dispatch(self):
        for remaining in (246, 245, 240, 1, 0):
            with self.subTest(remaining=remaining):
                result = self.invoke(remaining, 500)
                self.assertEqual((1, b"", b"CHECKPOINT_REJECTED:native-life-outside-fixture\n"),
                                 (result.returncode, result.stdout, result.stderr))

    def test_job_allowance_consumed_by_audit_prevents_dispatch(self):
        for remaining in (245, 244, 240, 1, 0):
            with self.subTest(remaining=remaining):
                result = self.invoke(500, remaining)
                self.assertEqual((1, b"", b"CHECKPOINT_REJECTED:native-life-outside-fixture\n"),
                                 (result.returncode, result.stdout, result.stderr))

    def test_original_sufficient_boundary_stays_silent_and_dispatches(self):
        result = self.invoke(247, 246)
        self.assertEqual((0, b"DISPATCH\n", b""),
                         (result.returncode, result.stdout, result.stderr))

    def test_early_check_is_retained_and_final_check_follows_all_audit_work(self):
        source = SCRIPT.read_text()
        first = source.index(FIT)
        last = source.rindex(FIT)
        audit_end = source.index('pin_file "$os_audit" "$os_audit_sha256" 1048576',
                                 source.index("stdlib_after="))
        self.assertEqual(2, source.count(FIT))
        self.assertLess(first, source.index("stdlib_files="))
        self.assertLess(audit_end, last)
        launch = source.index('(ulimit -f 8192; exec setsid timeout', last)
        self.assertEqual(["diagnostic_stage=n01", "launched=1"],
                         source[last + len(FIT):launch].split())


if __name__ == "__main__":
    unittest.main()
