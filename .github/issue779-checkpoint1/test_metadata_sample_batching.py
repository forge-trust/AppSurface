"""Live Bash functions with controlled command data; no native authority.

Actual temporary paths exercise lexical and file-type checks. The bounded stat
and hash outputs are detached data, not claims about root ownership or the OS.
"""

from pathlib import Path
import json
import os
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parent
SCRIPTS = (ROOT / "procedures/slot/checkpoint-none.sh",
           ROOT / "procedures/slot/checkpoint-n06.sh",
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


class AliasComponentSampleTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="c1-alias-samples-")
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name).resolve()
        (self.root / "nested").mkdir()
        self.leaf = self.root / "nested/payload"
        self.leaf.write_bytes(b"data")
        self.link = self.root / "alias"
        self.link.symlink_to("nested/payload")
        self.calls = self.root / "calls.tsv"

    def invoke(self, script, *, sample="0:755", malformed_at="", changed_link=False,
               wrong_target=False, expired=False, projection=None):
        source = script.read_text()
        helpers = "\n".join(function(source, name)
                            for name in ("lexical", "normalize_os_target", "alias_walk"))
        # macOS ships Bash 3, where nounset rejects an initialized empty array.
        # The native fixture uses Linux Bash 5; leave its strict mode intact there.
        strict = "set -euo pipefail\nif ((BASH_VERSINFO[0]<4)); then set +u; fi\n"
        program = strict + helpers + r'''
fail() { printf '%s\n' "$1" >&2; exit 1; }
remaining() { [[ $EXPIRED == no ]] || fail expired; }
bounded() {
 printf '%s\t%s\t%s\n' "$1" "${3:-}" "${5:-}" >>"$CALLS"
 case "$1" in
  stat)
   [[ $2 == -c && $4 == -- ]] || exit 90
   case "$3" in
    '%u:%a')
     if [[ $5 == "$MALFORMED_AT" ]]; then printf '%s\n' "$SAMPLE";
     else printf '0:755\n'; fi ;;
    '%u:%g:%a:%d:%i')
     local count=0 line
     while IFS= read -r line; do
      [[ $line == *'%u:%g:%a:%d:%i'* ]] && count=$((count+1))
     done <"$CALLS"
     if [[ $CHANGED_LINK == yes && $count == 2 ]]; then printf '0:0:777:1:99\n';
     else printf '0:0:777:1:2\n'; fi ;;
    *) exit 91;;
   esac;;
  readlink)
   if [[ $WRONG_TARGET == yes ]]; then printf 'nested/other\n';
   else command readlink "$3"; fi;;
  jq)
   if [[ $2 == -er && $3 == --argjson && $4 == i && $OVERRIDE == yes ]]; then
    printf '%s\n' "$PROJECTION"
   else command jq "${@:2}"; fi;;
  *) exit 92;;
 esac
}
if [[ $FAMILY == none ]]; then alias_walk "$RECORD";
else
 os_alias_declarations_ready=1
 os_cache_alias_paths=("$LITERAL"); os_alias_counts=(1); os_alias_starts=(0)
 os_alias_link_paths=("$LITERAL"); os_alias_link_metadata=('0:0:777:1:2')
 os_alias_link_targets=('nested/payload'); os_alias_resolved=("$LEAF")
 alias_walk 0
fi
'''
        record = {"literal": str(self.link), "resolved_path": str(self.leaf),
                  "links": [{"path": str(self.link), "target": "nested/payload",
                             "uid": 0, "gid": 0, "mode": "777", "device": 1, "inode": 2}]}
        self.calls.unlink(missing_ok=True)
        env = {"PATH": os.environ["PATH"], "LC_ALL": "C", "CALLS": str(self.calls),
               "SAMPLE": sample, "MALFORMED_AT": malformed_at,
               "CHANGED_LINK": "yes" if changed_link else "no",
               "WRONG_TARGET": "yes" if wrong_target else "no",
               "EXPIRED": "yes" if expired else "no", "LITERAL": str(self.link),
               "LEAF": str(self.leaf), "RECORD": json.dumps(record),
               "OVERRIDE": "yes" if projection is not None else "no",
               "PROJECTION": projection or "",
               "FAMILY": "n06" if script.name == "checkpoint-n06.sh" else "none"}
        result = subprocess.run(["/bin/bash", "--noprofile", "--norc", "-c", program],
                                env=env, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=3)
        calls = [line.split("\t") for line in self.calls.read_text().splitlines()] if self.calls.exists() else []
        return result, calls

    def test_every_ordinary_component_is_sampled_once_and_link_is_sampled_twice(self):
        def components(path):
            return [str(Path(path.anchor).joinpath(*path.parts[1:index + 1]))
                    for index in range(1, len(path.parts))]

        for script in SCRIPTS:
            with self.subTest(script=script.name):
                result, calls = self.invoke(script)
                self.assertEqual((0, str(self.leaf).encode(), b""),
                                 (result.returncode, result.stdout, result.stderr))
                ordinary = [row[2] for row in calls if row[0] == "stat" and row[1] == "%u:%a"]
                self.assertEqual(components(self.root) + components(self.leaf), ordinary)
                self.assertEqual(2, sum(row[0] == "stat" and row[1] == "%u:%g:%a:%d:%i" for row in calls))
                self.assertEqual(1, sum(row[0] == "readlink" for row in calls))

    def test_nonroot_writable_and_malformed_ordinary_metadata_reject(self):
        samples = (("1:755", b"alias-node\n"), ("0:775", b"alias-writable-node\n"),
                   ("0:757", b"alias-writable-node\n"), ("0:755:extra", b"alias-node\n"),
                   ("root:755", b"alias-node\n"), ("0:789", b"alias-node\n"),
                   ("0:", b"alias-node\n"))
        for script in SCRIPTS:
            for sample, reason in samples:
                with self.subTest(script=script.name, sample=sample):
                    result, calls = self.invoke(script, sample=sample, malformed_at=str(self.root))
                    self.assertEqual((1, reason), (result.returncode, result.stderr))
                    self.assertEqual([], [row for row in calls if row[0] == "readlink"])

    def test_link_identity_change_wrong_target_and_expiry_still_reject(self):
        for script in SCRIPTS:
            for kwargs, reason in (({"changed_link": True}, b"alias-link-changed\n"),
                                   ({"wrong_target": True}, b"alias-target-pin\n"),
                                   ({"expired": True}, b"expired\n")):
                with self.subTest(script=script.name, kwargs=kwargs):
                    result, calls = self.invoke(script, **kwargs)
                    self.assertEqual((1, reason), (result.returncode, result.stderr))
                    if kwargs.get("expired") and script.name == "checkpoint-n06.sh":
                        self.assertEqual([], calls)

    def test_n12_projects_three_declaration_fields_once_and_rejects_bad_projection(self):
        script = SCRIPTS[2]
        result, calls = self.invoke(script)
        self.assertEqual(0, result.returncode)
        declarations = [row for row in calls if row[0] == "jq" and row[1] == "--argjson"]
        self.assertEqual(1, len(declarations))
        good = str(self.link) + "\x1f0:0:777:1:2\x1fnested/payload"
        cases = ((good + "\x1f", b"alias-unreviewed-link\n"),
                 (good + "\x1fextra", b"alias-unreviewed-link\n"),
                 (str(self.link) + "\x1f0:0:777:1:2", b"alias-unreviewed-link\n"),
                 (good.replace(str(self.link), "/different"), b"alias-unreviewed-link\n"),
                 (good.replace("777:1:2", "777:1:3"), b"alias-lstat-pin\n"),
                 (good.replace("nested/payload", "nested/other"), b"alias-target-pin\n"))
        for projection, reason in cases:
            with self.subTest(reason=reason, projection_fields=projection.count("\x1f")):
                result, _ = self.invoke(script, projection=projection)
                self.assertEqual((1, reason), (result.returncode, result.stderr))


if __name__ == "__main__":
    unittest.main()
