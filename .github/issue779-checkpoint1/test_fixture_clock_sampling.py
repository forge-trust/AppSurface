"""Actual Bash fixture-clock data controls; no Linux clock or native authority.

Only the extracted test text redirects its one /proc/uptime read to an already
opened ordinary file on FD 3. Production functions and signatures stay intact.
Closed expectations preserve the original 600-second fixture/30-second cleanup
reserve and dynamically scoped N03 minimum; no Git or production shell runs here.
"""
from pathlib import Path
import re
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parent
SCRIPTS = (
    ROOT / "procedures/slot/checkpoint-none.sh",
    ROOT / "procedures/n12/checkpoint-n12-data-integration.sh",
)


def extracted_left(path):
    """Copy the actual function, changing exactly its fixed read redirection."""
    text = path.read_text()
    matches = re.findall(r"(?ms)^left\(\) \{\n.*?^\}\n", text)
    if len(matches) != 1 or matches[0].count("</proc/uptime") != 1:
        raise AssertionError("one original left clock read required")
    return matches[0].replace("</proc/uptime", "<&3", 1)


class FixtureClockSamplingTests(unittest.TestCase):
    """Real retained-file reads through each actual function, without sleeps."""

    def run_samples(self, path, contents, actions, *, original_initial_sample=False):
        function = extracted_left(path)
        initial = "readonly fixture_start=100 hard_end=700 work_end=670\n"
        initial_function = ""
        if original_initial_sample:
            matches = re.findall(r"(?m)^monotonic\(\) \{[^\n]*\}\n", path.read_text())
            self.assertEqual(1, len(matches))
            self.assertEqual(1, matches[0].count("</proc/uptime"))
            initial_function = matches[0].replace("monotonic()", "initial_sample()", 1).replace(
                "</proc/uptime", "<&3", 1)
            initial = (
                "fixture_start=$(initial_sample)\n"
                "readonly fixture_start hard_end=$((fixture_start+600)) "
                "work_end=$((fixture_start+600-30))\n"
                "printf 'initial:%s\\n' \"$fixture_start\"\n"
            )
        program = (
            "set -euo pipefail\n"
            "exec 3<\"$1\"\n"
            "trap 'exec 3<&-' EXIT\n"
            + initial_function + initial
            + "phase=work; n03_startup_active=0\n"
            # A stale $(monotonic) implementation cannot pass these controls.
            + "monotonic() { printf 'unexpected-clock-helper' >&2; return 99; }\n"
            + function
            + "sample() { local code=0; left || code=$?; printf '|%d\\n' \"$code\"; }\n"
            + actions + "\n"
        )
        with tempfile.TemporaryDirectory() as directory:
            fixture = Path(directory) / "uptime-data"
            fixture.write_bytes(contents)
            result = subprocess.run(
                ["/bin/bash", "--noprofile", "--norc", "-c", program, "--", str(fixture)],
                stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                timeout=2, check=False,
            )
        self.assertEqual(0, result.returncode, result.stderr.decode(errors="replace"))
        self.assertEqual(b"", result.stderr)
        return result.stdout.decode("ascii").splitlines()

    def test_real_file_samples_keep_fractional_floor_and_expiry_neighbors(self):
        cases = ((b"100.00 0.00\n", "570|0"), (b"100.99 9.99\n", "570|0"),
                 (b"669.00 0.00\n", "1|0"), (b"669.99 0.00\n", "1|0"),
                 (b"670.00 0.00\n", "|1"), (b"670.01 0.00\n", "|1"))
        for path in SCRIPTS:
            for raw, expected in cases:
                with self.subTest(script=path.name, raw=raw):
                    self.assertEqual([expected], self.run_samples(path, raw, "sample"))

    def test_cleanup_uses_original_hard_end_and_never_renews_it(self):
        for path in SCRIPTS:
            with self.subTest(script=path.name):
                self.assertEqual(["|1", "30|0", "1|0", "|1"], self.run_samples(
                    path, b"670.00 0.00\n670.00 0.00\n699.99 0.00\n700.00 0.00\n",
                    "sample; phase=cleanup; sample; sample; sample"))

    def test_n03_dynamic_scope_selects_minimum_without_replacing_work_end(self):
        for path in SCRIPTS:
            for startup_end, expected in ((590, "90|0"), (670, "170|0"), (900, "170|0")):
                actions = (
                    "nested() { local n03_startup_active=1 n03_startup_end="
                    + str(startup_end) + "; sample; }; nested; sample"
                )
                with self.subTest(script=path.name, startup_end=startup_end):
                    self.assertEqual([expected, "170|0"], self.run_samples(
                        path, b"500.00 0.00\n500.00 0.00\n", actions))

    def test_n03_expiry_boundary_and_cleanup_ignore_private_startup_bound(self):
        for path in SCRIPTS:
            with self.subTest(script=path.name):
                self.assertEqual(["1|0", "|1", "110|0"], self.run_samples(
                    path, b"589.99 0.00\n590.00 0.00\n590.00 0.00\n",
                    "n03_startup_active=1; n03_startup_end=590; sample; sample; "
                    "phase=cleanup; n03_startup_end=invalid-canary; sample"))

    def test_invalid_startup_bound_rejects_before_consuming_clock_sample(self):
        settings = ("unset n03_startup_end", "n03_startup_end=''",
                    "n03_startup_end=-1", "n03_startup_end=590.00",
                    "n03_startup_end=clock-canary")
        for path in SCRIPTS:
            for setting in settings:
                with self.subTest(script=path.name, setting=setting):
                    self.assertEqual(["|1", "170|0"], self.run_samples(
                        path, b"500.00 0.00\n", "n03_startup_active=1; " + setting
                        + "; sample; n03_startup_active=0; sample"))

    def test_malformed_empty_and_unterminated_samples_fail_closed(self):
        cases = (b"", b"\n", b"500 0.00\n", b".50 0.00\n", b"500. 0.00\n",
                 b"-1.00 0.00\n", b"500.00x 0.00\n", b"clock-canary 0.00\n",
                 b"500.00 0.00")
        for path in SCRIPTS:
            for raw in cases:
                with self.subTest(script=path.name, raw=raw):
                    self.assertEqual(["|1"], self.run_samples(path, raw, "sample"))

    def test_each_check_reads_fresh_after_the_original_initial_sample(self):
        for path in SCRIPTS:
            with self.subTest(script=path.name):
                self.assertEqual(["initial:100", "569|0", "1|0", "|1"], self.run_samples(
                    path, b"100.99 0.00\n101.00 0.00\n669.99 0.00\n670.00 0.00\n",
                    "sample; sample; sample", original_initial_sample=True))

    def test_eof_cannot_reuse_the_last_valid_sample(self):
        for path in SCRIPTS:
            with self.subTest(script=path.name):
                self.assertEqual(["170|0", "|1", "|1"], self.run_samples(
                    path, b"500.99 0.00\n", "sample; sample; sample"))


if __name__ == "__main__":
    unittest.main()
