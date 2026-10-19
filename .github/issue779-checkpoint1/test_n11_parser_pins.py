"""Exercise the fixed N11 parser allowlist as data, without native authority."""

import hashlib
from pathlib import Path
import re
import shlex
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parent / "procedures/n11"
NAMES = ("root_stall_frame.py", "check_stall_record.py", "stall_capture.py", "capture_stall.py")


def native_pin_block():
    source = (ROOT / "checkpoint-n11.sh").read_text()
    start = source.index("readonly STALL_MODULE_SHA256=(")
    end = source.index("((local_parser_count==4)) || fail stall-parser-pin-count", start)
    return source[start:end] + "((local_parser_count==4)) || fail stall-parser-pin-count\n"


class NativeParserPinTests(unittest.TestCase):
    def invoke(self, raw):
        # The original row/count guards run unchanged. Physical custody is modeled
        # here; real source bytes are independently bound below, not imported.
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "pins.tsv").write_bytes(raw)
            script = r'''set -euo pipefail
stall_parser_root=$1; stall_parser_pins=$2; private=$3; calls=$4
local_parser_count=0
fail() { printf '%s\n' "$1"; exit 42; }
pin_file() { printf 'pin\n' >>"$calls"; }
bounded() {
 case "$1" in
  stat) printf '0:0:444:1\n';;
  cp) printf 'copy\n' >>"$calls";;
  chmod) :;;
  *) exit 43;;
 esac
}
'''
            result = subprocess.run(["/bin/bash", "--noprofile", "--norc", "-c",
                                     script + native_pin_block(), "pins", str(ROOT),
                                     str(root / "pins.tsv"), str(root), str(root / "calls")],
                                    stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=5)
            calls = (root / "calls").read_text().splitlines() if (root / "calls").exists() else []
            return result, calls

    def test_native_literals_tsv_and_all_four_current_module_bytes_agree(self):
        block = native_pin_block()
        hashes = shlex.split(re.search(r"^readonly STALL_MODULE_SHA256=\((.*)\)$", block, re.M)[1])
        names = shlex.split(re.search(r"^readonly STALL_MODULE_NAMES=\((.*)\)$", block, re.M)[1])
        expected = [(name, hashlib.sha256((ROOT / name).read_bytes()).hexdigest()) for name in NAMES]
        self.assertEqual(list(NAMES), names)
        self.assertEqual(len(NAMES), len(hashes))
        self.assertEqual(expected, list(zip(names, hashes)))
        raw = (ROOT / "parser-pins.tsv").read_bytes()
        self.assertEqual("".join(name + "\t" + digest + "\n" for name, digest in expected).encode(), raw)
        result, calls = self.invoke(raw)
        self.assertEqual((0, b"", b""), (result.returncode, result.stdout, result.stderr))
        self.assertEqual(["pin", "copy", "pin"] * 4, calls)

    def test_wrong_hash_order_missing_extra_and_extra_column_reject(self):
        rows = (ROOT / "parser-pins.tsv").read_bytes().splitlines(keepends=True)
        cases = {
            "wrong-hash": ([rows[0].split(b"\t")[0] + b"\t" + b"0" * 64 + b"\n"] + rows[1:], 0),
            "wrong-order": ([rows[1], rows[0]] + rows[2:], 0),
            "missing-row": (rows[:-1], 3),
            "extra-row": (rows + [rows[0]], 4),
            "extra-column": ([rows[0].rstrip(b"\n") + b"\textra\n"] + rows[1:], 0),
        }
        for name, (candidate, accepted_rows) in cases.items():
            with self.subTest(name=name):
                result, calls = self.invoke(b"".join(candidate))
                self.assertEqual(42, result.returncode)
                self.assertIn(result.stdout, (b"stall-parser-pin-row\n", b"stall-parser-pin-count\n"))
                self.assertEqual(b"", result.stderr)
                self.assertEqual(["pin", "copy", "pin"] * accepted_rows, calls)


if __name__ == "__main__":
    unittest.main()
