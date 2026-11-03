"""N04 exact-child join ordering with detached process metadata, not native proof."""

from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parent


def join_function():
    source = (ROOT / "procedures/slot/checkpoint-n04.sh").read_text()
    start = source.index("join_owned() {")
    return source[start:source.index("\n}", start) + 2]


class N04JoinRaceTests(unittest.TestCase):
    def invoke(self, scenario, *, child_status=0, fail_left=0, registered=True):
        with tempfile.TemporaryDirectory() as temporary:
            program = r'''set -euo pipefail
scenario=$1; child_status=$2; fail_left=$3; registered=$4; model=$5
pid_value=2000000000; watched=$pid_value
declare -a owned_start=()
if ((registered)); then owned_start[$pid_value]=42; fi
printf 0 >"$model/kill-count"; printf 0 >"$model/left-count"
left() {
 local n; n=$(cat "$model/left-count"); n=$((n+1)); printf '%s' "$n" >"$model/left-count"
 ((fail_left==0 || n<fail_left))
}
kill() {
 [[ $# == 2 && $1 == -0 && $2 == "$pid_value" ]] || return 2
 local n; n=$(cat "$model/kill-count"); n=$((n+1)); printf '%s' "$n" >"$model/kill-count"
 case "$scenario" in
  live|mismatch) return 0 ;;
  gone|missing|post-group) return 1 ;;
  *) ((n==1)) ;;
 esac
}
owned_identity() {
 [[ $1 == "$pid_value" ]] || return 2
 case "$scenario" in
  matching) printf 42 ;;
  mismatch) printf 43 ;;
  *) return 1 ;;
 esac
}
try_bounded() { [[ $1 == sleep && $2 == .05 ]]; }
pg_alive() { [[ $1 == "$pid_value" ]] && [[ $scenario == group || $scenario == post-group ]]; }
wait() {
 [[ $# == 1 && $1 == "$pid_value" ]] || return 2
 printf called >"$model/wait-called"
 return "$child_status"
}
'''
            program += join_function()
            program += r'''
status=0; join_owned watched || status=$?
waited=0; retired=0
[[ ! -f $model/wait-called ]] || waited=1
if [[ -z $watched && ! ${owned_start[$pid_value]+present} ]]; then retired=1; fi
printf '%s:%s:%s\n' "$status" "$waited" "$retired"
'''
            result = subprocess.run(["/bin/bash", "--noprofile", "--norc", "-c", program,
                                     "join-model", scenario, str(child_status), str(fail_left),
                                     "1" if registered else "0", temporary], capture_output=True, timeout=3)
            self.assertEqual(0, result.returncode, result.stderr.decode())
            self.assertEqual(b"", result.stderr)
            return tuple(int(value) for value in result.stdout.decode().strip().split(":"))

    def test_matching_live_and_already_gone_children_preserve_exact_wait_status(self):
        for scenario in ("matching", "gone"):
            for status in (0, 1, 17):
                with self.subTest(scenario=scenario, status=status):
                    self.assertEqual((status, 1, 1), self.invoke(scenario, child_status=status))

    def test_identity_read_racing_confirmed_disappearance_reaches_original_wait(self):
        for status in (0, 1):
            with self.subTest(status=status):
                self.assertEqual((status, 1, 1), self.invoke("race", child_status=status))

    def test_live_mismatch_missing_registration_and_surviving_group_stay_failed(self):
        for scenario in ("live", "mismatch", "group"):
            with self.subTest(scenario=scenario):
                self.assertEqual((125, 0, 0), self.invoke(scenario))
        self.assertEqual((125, 0, 0), self.invoke("missing", registered=False))
        self.assertEqual((125, 1, 0), self.invoke("post-group"))

    def test_expiry_during_fallback_or_after_wait_cannot_retire_registration(self):
        self.assertEqual((124, 0, 0), self.invoke("race", fail_left=2))
        self.assertEqual((124, 1, 0), self.invoke("race", fail_left=4))


if __name__ == "__main__":
    unittest.main()
