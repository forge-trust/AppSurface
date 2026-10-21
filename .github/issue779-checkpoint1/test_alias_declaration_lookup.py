"""Actual Bash declaration lookup with real jq and detached physical samples.

These controls establish data and call ordering only. They issue no root lease,
native execution, or filesystem identity assertion.
"""

import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parent
SCRIPTS = (ROOT / "procedures/slot/checkpoint-none.sh",
           ROOT / "procedures/n12/checkpoint-n12-data-integration.sh")
HASH = "a" * 64


def function(source, name):
    start = source.index(name + "() {")
    return source[start:source.index("\n}", start) + 2]


def alias(literal):
    return {"literal": literal, "resolved_sha256": HASH,
            "resolved_path": "/owned/resolved", "links": []}


class AliasDeclarationLookupTests(unittest.TestCase):
    def invoke(self, script, aliases, action, *, count="", expired=False,
               changed=False, wrong_hash=False, projection_failure=False):
        source = script.read_text()
        block = source[source.index("os_declaration_alias_ready=0"):
                       source.index("\nfor key in ", source.index("os_declaration_alias_ready=0"))]
        program = "set -euo pipefail\nif ((BASH_VERSINFO[0]<4)); then set +u; fi\n"
        program += function(source, "lexical") + "\n" + block + r'''
fail() { printf '%s\n' "$1" >&2; exit 1; }
remaining() { [[ $EXPIRED == no ]] || fail expired; }
bounded() {
 printf '%s\n' "$1" >>"$CALLS"
 if [[ $1 == jq ]]; then
  if [[ $3 == '.aliases|length' && -n $COUNT ]]; then printf '%s\n' "$COUNT"
  elif [[ $3 == '.aliases|sort_by(.literal)|.[]' && $PROJECTION_FAILURE == yes ]]; then return 91
  else command jq "${@:2}"; fi
 elif [[ $1 == stat ]]; then
  local calls=0 line
  while IFS= read -r line; do [[ $line != stat ]] || calls=$((calls+1)); done <"$CALLS"
  if [[ $CHANGED == yes && $calls == 2 ]]; then printf '1:99:4:81a4:1:0:0\n'
  else printf '1:2:4:81a4:1:0:0\n'; fi
 else exit 92; fi
}
alias_walk() { printf 'walk\n' >>"$CALLS"; printf '%s\n' "$1" >>"$ROWS"; printf '/owned/resolved'; }
ancestor() { printf 'ancestor:%s\n' "$1" >>"$CALLS"; }
pin_file() { printf 'pin:%s:%s:%s\n' "$1" "$2" "$3" >>"$CALLS"; }
MAX_INPUT_FILE_BYTES=2147483648
case "$ACTION" in
 uninitialized) lookup_alias_declaration /ordinary/file ;;
 *)
  initialize_alias_declarations
  printf 'initialized\n' >>"$CALLS"
  case "$ACTION" in
   lookup)
    for p in /literal/a /literal/z /ordinary/file /literal/a; do
     lookup_alias_declaration "$p"
     printf '%s:%s\n' "$p" "$os_declaration_alias_index"
    done ;;
   replay) initialize_alias_declarations ;;
   mutate) os_declaration_alias_rows[0]=forged ;;
   ordinary) verify_os_path /ordinary/file "$EXPECTED" ;;
   alias) verify_os_path /literal/a "$EXPECTED" ;;
   many) printf '%s\n' "${#os_declaration_alias_paths[@]}" ;;
   *) exit 93 ;;
  esac ;;
esac
'''
        with tempfile.TemporaryDirectory(prefix="c1-alias-declarations-") as directory:
            root = Path(directory)
            audit, calls, rows = (root / name for name in ("audit.json", "calls", "rows"))
            audit.write_text(json.dumps({"aliases": aliases}))
            environment = {"PATH": os.environ["PATH"], "LC_ALL": "C", "os_audit": str(audit),
                           "CALLS": str(calls), "ROWS": str(rows), "ACTION": action,
                           "COUNT": count, "EXPIRED": "yes" if expired else "no",
                           "CHANGED": "yes" if changed else "no",
                           "EXPECTED": "b" * 64 if wrong_hash else HASH,
                           "PROJECTION_FAILURE": "yes" if projection_failure else "no"}
            result = subprocess.run(["/bin/bash", "--noprofile", "--norc", "-c", program],
                                    env=environment, capture_output=True, timeout=3)
            return (result, calls.read_text().splitlines() if calls.exists() else [],
                    rows.read_text().splitlines() if rows.exists() else [])

    def test_empty_table_keeps_fresh_ordinary_ancestor_and_pin_without_lookup_jq(self):
        for script in SCRIPTS:
            result, calls, rows = self.invoke(script, [], "ordinary")
            self.assertEqual((0, b"", b""), (result.returncode, result.stdout, result.stderr))
            self.assertEqual(["ancestor:/ordinary", "pin:/ordinary/file:" + HASH + ":2147483648"],
                             calls[calls.index("initialized") + 1:])
            self.assertEqual([], rows)

    def test_sorted_exact_lookup_has_no_jq_and_does_not_reuse_previous_match(self):
        for script in SCRIPTS:
            result, calls, _ = self.invoke(script, [alias("/literal/z"), alias("/literal/a")], "lookup")
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertEqual(b"/literal/a:0\n/literal/z:1\n/ordinary/file:-1\n/literal/a:0\n", result.stdout)
            self.assertEqual([], calls[calls.index("initialized") + 1:])

    def test_alias_still_performs_two_walks_two_samples_and_a_fresh_hash_pin(self):
        declaration = alias("/literal/a")
        for script in SCRIPTS:
            result, calls, rows = self.invoke(script, [declaration], "alias")
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertEqual(["jq", "walk", "stat", "pin:/owned/resolved:" + HASH + ":2147483648", "walk", "stat"],
                             calls[calls.index("initialized") + 1:])
            self.assertEqual([declaration, declaration], [json.loads(row) for row in rows])

    def test_hash_mismatch_and_changed_metadata_reject_without_losing_physical_checks(self):
        for script in SCRIPTS:
            result, calls, rows = self.invoke(script, [alias("/literal/a")], "alias", wrong_hash=True)
            self.assertEqual((1, b"alias-hash-binding\n"), (result.returncode, result.stderr))
            self.assertEqual(["jq"], calls[calls.index("initialized") + 1:])
            self.assertEqual([], rows)
            result, calls, rows = self.invoke(script, [alias("/literal/a")], "alias", changed=True)
            self.assertEqual((1, b"alias-before-after\n"), (result.returncode, result.stderr))
            self.assertEqual(2, len(rows))
            self.assertEqual(2, calls.count("stat"))

    def test_duplicates_bad_literals_and_projection_failure_cannot_publish_table(self):
        for script in SCRIPTS:
            for declarations in ([alias("/same"), alias("/same")], [alias("relative")],
                                 [alias("/bad/../path")], [alias("/bad\npath")]):
                result, calls, _ = self.invoke(script, declarations, "ordinary")
                self.assertNotEqual(0, result.returncode)
                self.assertNotIn("initialized", calls)
            result, calls, _ = self.invoke(script, [alias("/literal/a")], "ordinary", projection_failure=True)
            self.assertNotEqual(0, result.returncode)
            self.assertNotIn("initialized", calls)

    def test_count_bounds_membership_and_maximum_neighbor(self):
        for script in SCRIPTS:
            declarations = [alias("/literal/" + str(i).zfill(2)) for i in range(64)]
            result, _, _ = self.invoke(script, declarations, "many")
            self.assertEqual((0, b"64\n"), (result.returncode, result.stdout))
            for count in ("65", "-1", "true", "01", "1"):
                result, calls, _ = self.invoke(script, [], "ordinary", count=count)
                self.assertNotEqual(0, result.returncode)
                self.assertNotIn("initialized", calls)

    def test_uninitialized_replay_and_mutation_fail_closed(self):
        for script in SCRIPTS:
            for action, reason in (("uninitialized", b"alias-declaration-uninitialized\n"),
                                   ("replay", b"alias-declaration-replay\n")):
                result, _, _ = self.invoke(script, [alias("/literal/a")], action)
                self.assertEqual((1, reason), (result.returncode, result.stderr))
            result, _, _ = self.invoke(script, [alias("/literal/a")], "mutate")
            self.assertNotEqual(0, result.returncode)
            self.assertIn(b"readonly", result.stderr)

    def test_expiry_and_projection_byte_bound_cannot_reach_physical_operations(self):
        for script in SCRIPTS:
            result, calls, _ = self.invoke(script, [alias("/literal/a")], "ordinary", expired=True)
            self.assertEqual((1, b"expired\n"), (result.returncode, result.stderr))
            self.assertNotIn("initialized", calls)
            oversized = alias("/literal/a")
            oversized["links"] = ["x" * 1048576]
            result, calls, _ = self.invoke(script, [oversized], "ordinary")
            self.assertEqual((1, b"alias-declaration-bytes\n"), (result.returncode, result.stderr))
            self.assertNotIn("initialized", calls)

    def test_initialization_is_after_full_schema_and_before_unchanged_two_physical_passes(self):
        for script in SCRIPTS:
            source = script.read_text()
            schema = source.index("' \"$os_audit\" >/dev/null || fail OS-audit-schema")
            initialize = source.index("\ninitialize_alias_declarations\n")
            first_pass = source.index('while IFS=$\'\\t\' read -r p h; do verify_os_path "$p" "$h"; done <<<"$os_files"')
            self.assertLess(schema, initialize)
            self.assertLess(initialize, first_pass)
            self.assertEqual(2, source.count('while IFS=$\'\\t\' read -r p h; do verify_os_path "$p" "$h"; done <<<"$os_files"'))


if __name__ == "__main__":
    unittest.main()
