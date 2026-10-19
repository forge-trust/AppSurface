"""Live Bash functions with controlled command data; no native authority.

Actual temporary paths exercise lexical and file-type checks. The bounded stat
and hash outputs are detached data, not claims about root ownership or the OS.
"""

from pathlib import Path
import os
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parent
SCRIPTS = (ROOT / "procedures/slot/checkpoint-none.sh",
           ROOT / "procedures/n12/checkpoint-n12-data-integration.sh")
HASH = "a" * 64
BEFORE = "1:2:4:81a4:1:0:0"


def function(source, name):
    start = source.index(name + "() {")
    if source[start:source.index("\n", start)].endswith(" }"):
        return source[start:source.index("\n", start)]
    return source[start:source.index("\n}", start) + 2]


class MetadataSampleBatchingTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="c1-metadata-samples-")
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name).resolve()
        self.leaf = self.root / "payload"
        self.leaf.write_bytes(b"data")
        self.directory = self.root / "nested"
        self.directory.mkdir()
        self.calls = self.root / "calls.tsv"

    def invoke(self, script, action="pin_file", *, before=BEFORE, after=BEFORE,
               digest=HASH, limit=4, ancestor_sample="0:755", unsafe_path="",
               target=None):
        source = script.read_text()
        helpers = "\n".join(function(source, name)
                            for name in ("lexical", "absolute", "sha", "pin_file", "ancestor"))
        program = "set -euo pipefail\n" + helpers + r'''
fail() { printf '%s\n' "$1" >&2; exit 1; }
bounded() {
 local count=0 line
 if [[ -e $CALLS ]]; then
  while IFS= read -r line; do count=$((count+1)); done <"$CALLS"
 fi
 printf '%s\t%s\t%s\n' "$1" "${3:-}" "${5:-}" >>"$CALLS"
 case "$1" in
  stat)
   [[ $2 == -c && $4 == -- ]] || exit 90
   case "$3" in
    '%d:%i:%s:%f:%h:%u:%g')
     if ((count==0)); then printf '%s\n' "$BEFORE"; else printf '%s\n' "$AFTER"; fi ;;
    '%u:%a')
     if [[ -n $UNSAFE_PATH && $5 == "$UNSAFE_PATH" ]]; then
      printf '%s\n' "$ANCESTOR_SAMPLE"
     else printf '0:755\n'; fi ;;
    *) exit 91 ;;
   esac ;;
  sha256sum) printf '%s  %s\n' "$DIGEST" "$3" ;;
  *) exit 92 ;;
 esac
}
if [[ $ACTION == pin_file ]]; then pin_file "$TARGET" "$EXPECTED" "$LIMIT"
else ancestor "$TARGET"; fi
'''
        self.calls.unlink(missing_ok=True)
        environment = {"PATH": "/usr/bin:/bin", "LC_ALL": "C",
                       "CALLS": str(self.calls), "BEFORE": before, "AFTER": after,
                       "DIGEST": digest, "EXPECTED": HASH, "LIMIT": str(limit),
                       "ANCESTOR_SAMPLE": ancestor_sample, "UNSAFE_PATH": unsafe_path,
                       "ACTION": action,
                       "TARGET": str(target or (self.leaf if action == "pin_file" else self.directory))}
        result = subprocess.run(["/bin/bash", "--noprofile", "--norc", "-c", program],
                                env=environment, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                timeout=2, check=False)
        calls = [line.split("\t") for line in self.calls.read_text().splitlines()] if self.calls.exists() else []
        return result, calls

    def test_file_positive_keeps_two_complete_samples_and_one_hash(self):
        for script in SCRIPTS:
            with self.subTest(script=script.name):
                result, calls = self.invoke(script)
                self.assertEqual((0, b"", b""), (result.returncode, result.stdout, result.stderr))
                self.assertEqual(["stat", "sha256sum", "stat"], [row[0] for row in calls])
                self.assertEqual("%d:%i:%s:%f:%h:%u:%g", calls[0][1])
                self.assertEqual(calls[0], calls[2])

    def test_size_cap_and_hardlink_reject_before_hash_or_second_sample(self):
        for script in SCRIPTS:
            for before, reason in (("1:2:5:81a4:1:0:0", b"file-byte-bound\n"),
                                   ("1:2:4:81a4:2:0:0", b"hardlink\n")):
                with self.subTest(script=script.name, before=before):
                    result, calls = self.invoke(script, before=before)
                    self.assertEqual((1, reason), (result.returncode, result.stderr))
                    self.assertEqual(1, len(calls))

    def test_complete_metadata_requires_exactly_seven_typed_fields(self):
        invalid = ("1:2:4:81a4:1:0", BEFORE + ":0", "1:2::81a4:1:0:0",
                   "x:2:4:81a4:1:0:0", "1:2:4:zz:1:0:0", "1:2:4:81a4:true:0:0",
                   "1:2:4:81a4:1:root:0", "1:2:4:81a4:1:0:-1", BEFORE + "\nextra")
        for script in SCRIPTS:
            for before in invalid:
                with self.subTest(script=script.name, before=before):
                    result, calls = self.invoke(script, before=before)
                    self.assertEqual((1, b"file-metadata\n"), (result.returncode, result.stderr))
                    self.assertEqual(1, len(calls))

    def test_changed_after_identity_or_wrong_hash_still_rejects(self):
        for script in SCRIPTS:
            for field in range(7):
                changed = BEFORE.split(":")
                changed[field] = "3" if field != 3 else "81ed"
                with self.subTest(script=script.name, field=field):
                    result, calls = self.invoke(script, after=":".join(changed))
                    self.assertEqual((1, b"file-pin-changed\n"), (result.returncode, result.stderr))
                    self.assertEqual(3, len(calls))
            with self.subTest(script=script.name, wrong_hash=True):
                result, calls = self.invoke(script, digest="b" * 64)
                self.assertEqual((1, b"file-pin-changed\n"), (result.returncode, result.stderr))
                self.assertEqual(3, len(calls))

    def test_ancestor_walk_keeps_every_component_through_root_with_one_sample_each(self):
        expected = [str(self.directory), *(str(p) for p in self.directory.parents)]
        for script in SCRIPTS:
            with self.subTest(script=script.name):
                result, calls = self.invoke(script, "ancestor")
                self.assertEqual((0, b"", b""), (result.returncode, result.stdout, result.stderr))
                self.assertEqual(expected, [row[2] for row in calls])
                self.assertEqual(["%u:%a"] * len(expected), [row[1] for row in calls])

    def test_unsafe_or_malformed_middle_ancestor_rejects_without_skipping_to_root(self):
        invalid = ("1:755", "0:775", "0:757", "0:1777", "0:755:extra", "root:755", "0:789", "0:")
        for script in SCRIPTS:
            for sample in invalid:
                with self.subTest(script=script.name, sample=sample):
                    result, calls = self.invoke(script, "ancestor", ancestor_sample=sample,
                                                unsafe_path=str(self.root))
                    self.assertEqual((1, b"unsafe-root-ancestor\n"), (result.returncode, result.stderr))
                    self.assertEqual([str(self.directory), str(self.root)], [row[2] for row in calls])

    def test_actual_symlink_paths_reject_before_any_metadata_command(self):
        file_link, directory_link = self.root / "file-link", self.root / "directory-link"
        file_link.symlink_to(self.leaf)
        directory_link.symlink_to(self.directory, target_is_directory=True)
        for script in SCRIPTS:
            for action, target in (("pin_file", file_link), ("ancestor", directory_link)):
                with self.subTest(script=script.name, action=action):
                    result, calls = self.invoke(script, action, target=target)
                    self.assertEqual((1, b"deployment-symlink\n"), (result.returncode, result.stderr))
                    self.assertEqual([], calls)


if __name__ == "__main__":
    unittest.main()
