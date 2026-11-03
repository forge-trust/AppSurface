"""Exercise N05's closed timing data; these controls establish no native facts."""

import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
import unittest


SCRIPT = Path(__file__).resolve().parent / "procedures/slot/checkpoint-n05.sh"
STAGES = ("declaration-cache", "ordinary-os-initial", "elf-closure",
          "sealed-trees", "ordinary-os-final", "negative-python-qualification")


def observation_function(source):
    start = source.index("fixture_stage_observation() {")
    return source[start:source.index("\n}", start) + 2]


class N05StageObservationTests(unittest.TestCase):
    def invoke(self, event="begin", stage="declaration-cache", *, clock_fails=False,
               expired_sample=0, close_stderr=False):
        """Run only the extracted observer with detached clock/deadline samples.

        Files preserve mock call order across Bash command substitutions. No
        fixture, root owner, process identity, or accepted kernel state is made.
        """
        with tempfile.TemporaryDirectory(prefix="n05-stage-observation-") as temporary:
            calls_path = Path(temporary) / "calls"
            program = r'''set -euo pipefail
fixture_start=100
fail() { printf '%s\n' "$1" >&2; exit 1; }
monotonic() {
 printf 'clock\n' >>"$CALLS"
 [[ $CLOCK_FAILS == no ]] || return 1
 printf '137'
}
remaining() {
 local count=0 row
 if [[ -f $CALLS ]]; then
  while IFS= read -r row; do
   if [[ $row == remaining ]]; then count=$((count+1)); fi
  done <"$CALLS"
 fi
 count=$((count+1)); printf 'remaining\n' >>"$CALLS"
 ((EXPIRED_SAMPLE==0 || count<EXPIRED_SAMPLE)) || fail fixture-deadline
 printf '203'
}
'''
            program += observation_function(SCRIPT.read_text())
            program += r'''
if [[ $CLOSE_STDERR == yes ]]; then exec 2>&-; fi
fixture_stage_observation "$EVENT" "$STAGE"
printf 'observer-returned\n'
'''
            environment = {"PATH": "/usr/bin:/bin", "LC_ALL": "C",
                           "CALLS": str(calls_path), "EVENT": event, "STAGE": stage,
                           "CLOCK_FAILS": "yes" if clock_fails else "no",
                           "EXPIRED_SAMPLE": str(expired_sample),
                           "CLOSE_STDERR": "yes" if close_stderr else "no"}
            result = subprocess.run(["/bin/bash", "--noprofile", "--norc", "-c", program],
                                    env=environment, capture_output=True, timeout=2)
            calls = calls_path.read_text().splitlines() if calls_path.exists() else []
            return result, calls

    def test_all_closed_stage_event_pairs_emit_only_original_numeric_samples(self):
        for stage in STAGES:
            for event in ("begin", "end"):
                with self.subTest(stage=stage, event=event):
                    result, calls = self.invoke(event, stage)
                    self.assertEqual((0, b"observer-returned\n"),
                                     (result.returncode, result.stdout))
                    self.assertEqual(1, result.stderr.count(b"\n"))
                    value = json.loads(result.stderr)
                    self.assertEqual({"schema": "issue779-fixture-stage-observation-v1",
                                      "status": "observations-only", "stage": stage,
                                      "event": event, "elapsed_seconds": 37,
                                      "remaining_seconds": 203}, value)
                    self.assertIs(type(value["elapsed_seconds"]), int)
                    self.assertIs(type(value["remaining_seconds"]), int)
                    self.assertEqual(["clock", "remaining", "remaining"], calls)

    def test_unknown_event_or_stage_rejects_without_clock_or_input_echo(self):
        for event, stage, reason in (("private-canary", STAGES[0], b"timing-event\n"),
                                     ("begin", "/private/canary", b"timing-stage\n")):
            with self.subTest(reason=reason):
                result, calls = self.invoke(event, stage)
                self.assertEqual((1, b"", reason),
                                 (result.returncode, result.stdout, result.stderr))
                self.assertEqual([], calls)

    def test_clock_failure_does_not_emit_a_stage_or_return_success(self):
        result, calls = self.invoke(clock_fails=True)
        self.assertEqual((1, b"", b"monotonic-clock\n"),
                         (result.returncode, result.stdout, result.stderr))
        self.assertEqual(["clock"], calls)

    def test_original_deadline_expiry_before_or_after_projection_stays_failure(self):
        for sample in (1, 2):
            with self.subTest(expired_sample=sample):
                result, calls = self.invoke(expired_sample=sample)
                self.assertEqual((1, b""), (result.returncode, result.stdout))
                self.assertTrue(result.stderr.endswith(b"fixture-deadline\n"))
                lines = result.stderr.splitlines()
                self.assertEqual(sample - 1, sum(line.startswith(b"{") for line in lines))
                self.assertEqual(["clock"] + ["remaining"] * sample, calls)

    def test_failed_stderr_write_cannot_return_success(self):
        result, calls = self.invoke(close_stderr=True)
        self.assertEqual((1, b"", b""),
                         (result.returncode, result.stdout, result.stderr))
        self.assertEqual(["clock", "remaining"], calls)

    def test_all_six_stage_pairs_surround_the_existing_work_in_order(self):
        source = SCRIPT.read_text()
        calls = re.findall(r"^fixture_stage_observation (begin|end) ([a-z-]+)$",
                           source, re.MULTILINE)
        self.assertEqual([(event, stage) for stage in STAGES for event in ("begin", "end")], calls)
        required = {
            "declaration-cache": ("negative_os_cache_initialize",),
            "ordinary-os-initial": ("ordinary_os_batch_full_pass initial",),
            "elf-closure": ("runtime_files=$(bounded find", "elf_records=$(bounded jq",
                            'header=$(bounded readelf -h "$p")',
                            'needed=$(bounded readelf -d "$p"',
                            'interpreter=$(bounded readelf -l "$p"'),
            "sealed-trees": ('verify_tree "$stage/tool"', 'verify_tree "$stage/runtime"',
                             'verify_tree "$stage/subject"', 'pin_file "$build_receipt"'),
            "ordinary-os-final": ("ordinary_os_batch_full_pass final",),
            "negative-python-qualification": ("for alternate_python_path in", "stdlib_files=",
                                               'verify_os_path "$python_file" "$python_hash"',
                                               "stdlib_after=", 'pin_file "$os_audit"'),
        }
        for stage, commands in required.items():
            with self.subTest(stage=stage):
                begin = source.index("\nfixture_stage_observation begin " + stage + "\n")
                end = source.index("\nfixture_stage_observation end " + stage + "\n", begin)
                region = source[begin:end]
                positions = [region.index(command) for command in commands]
                self.assertEqual(sorted(positions), positions)
        prelaunch = source.index("fail native-life-outside-fixture",
                                 source.index("\nfixture_stage_observation end ordinary-os-final\n"))
        self.assertLess(prelaunch, source.index("\nfixture_stage_observation begin negative-python-qualification\n"))


if __name__ == "__main__":
    unittest.main()
