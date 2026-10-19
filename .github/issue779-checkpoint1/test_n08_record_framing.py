"""Exercise four real N03 publishers and their unchanged strict data framing."""

import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parent
CASES = ("N04", "N05", "N06", "N08")
SOURCE = "a" * 40


def publisher_block(case):
    source = (ROOT / ("procedures/slot/checkpoint-" + case.lower() + ".sh")).read_text()
    marker = 'try_bounded jq -cS -n --arg source "$source_revision" --argjson worker "$sampled_pid" --argjson broker "$broker_actual_pid"'
    start = source.index(marker)
    end = source.index("  printf 'N03_OBSERVATION_TERMINAL", start)
    return source[start:end]


class N03RecordFramingTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        spec = importlib.util.spec_from_file_location("strict_n08_record_data", ROOT / "procedures/slot/records-n08.py")
        cls.records = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(cls.records)

    def invoke(self, case, temporary, *, partial_failure=False):
        harness = r'''set -euo pipefail
umask 077
n03log=$1; source_revision=$2; sampled_pid=1234; broker_actual_pid=1235; code=0
left() { :; }
try_bounded() {
 if [[ $1 == mv && $2 == -T && $3 == -- ]]; then command mv "$4" "$5";
 else "$@"; fi
}
'''
        if partial_failure:
            harness += "jq() { printf '%s' '{\"schema\":'; return 7; }\n"
        return subprocess.run(["/bin/bash", "--noprofile", "--norc", "-c",
                               harness + publisher_block(case), "publisher", str(temporary), SOURCE],
                              stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=5)

    def test_four_real_publishers_emit_one_lf_and_unchanged_closed_result(self):
        for case in CASES:
            with self.subTest(case=case), tempfile.TemporaryDirectory() as temporary:
                result = self.invoke(case, temporary)
                self.assertEqual((0, b"", b""), (result.returncode, result.stdout, result.stderr))
                raw = (Path(temporary) / "result.json").read_bytes()
                self.assertTrue(raw.endswith(b"\n"))
                self.assertFalse(raw.endswith(b"\n\n"))
                parsed = self.records.require_n03_result(raw, SOURCE)
                self.assertEqual(1234, parsed["worker_pid"])
                self.assertEqual(1235, parsed["broker_pid"])
                self.assertFalse(parsed["native_acceptance"])
                self.assertEqual(json.loads(raw), parsed)
                self.assertFalse((Path(temporary) / "result.pending").exists())
                self.assertEqual(0o600, (Path(temporary) / "result.json").stat().st_mode & 0o777)
                with self.assertRaises(ValueError):
                    self.records.require_n03_result(raw[:-1], SOURCE)

    def test_each_partial_serialization_fails_before_rename(self):
        for case in CASES:
            with self.subTest(case=case), tempfile.TemporaryDirectory() as temporary:
                result = self.invoke(case, temporary, partial_failure=True)
                self.assertEqual(1, result.returncode)
                self.assertFalse((Path(temporary) / "result.json").exists())
                self.assertEqual(b'{"schema":', (Path(temporary) / "result.pending").read_bytes())
                self.assertEqual(b"", result.stdout)


if __name__ == "__main__":
    unittest.main()
