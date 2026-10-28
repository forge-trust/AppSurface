"""Exercise N04's actual stdlib loop against its immutable audit declarations.

The harness starts after the source-owned complete OS-audit schema and audit
pin. It runs the actual cache initializer, lookup helpers, and stdlib loop with
detached physical-observation stubs; it establishes no native facts.
"""

import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parent
SCRIPT = ROOT / "procedures/slot/checkpoint-n04.sh"
HASH_A = "a" * 64
HASH_B = "b" * 64
PLAIN = "/usr/lib/python3.12/plain.py"
ALIAS = "/usr/lib/python3.12/alias.so"
RESOLVED = "/usr/lib/python3.12/real.so"
EXTRA = "/usr/lib/python3.12/extra.py"


def shell_function(source, name):
    start = source.index(name + "() {")
    line_end = source.index("\n", start)
    if source[start:line_end].endswith(" }"):
        return source[start:line_end]
    return source[start:source.index("\n}", start) + 2]


def audit(paths, *, aliases=(), elf_paths=()):
    hashes = {PLAIN: HASH_A, ALIAS: HASH_B, RESOLVED: HASH_B, EXTRA: "d" * 64}
    return {
        "files": [{"path": path, "sha256": hashes[path]} for path in paths],
        "aliases": list(aliases),
        "elf": [{"path": path} for path in elf_paths],
    }


def file_alias():
    return {
        "literal": ALIAS,
        "resolved_path": RESOLVED,
        "resolved_sha256": HASH_B,
        "links": [{"path": ALIAS, "target": "real.so", "uid": 0,
                   "gid": 0, "mode": "777", "device": 1, "inode": 2}],
    }


class N04StdlibDeclarationTests(unittest.TestCase):
    def setUp(self):
        if subprocess.run(["/usr/bin/jq", "--version"], capture_output=True).returncode != 0:
            self.skipTest("jq is required by the production audit parser")
        self.source = SCRIPT.read_text(encoding="utf-8")
        self.loop = self.source[
            self.source.index('stdlib_expected=$(bounded jq -er'):
            self.source.index("stdlib_after=$(negative_stdlib_inventory)")
        ]
        names = (
            "lexical", "sha", "negative_os_file_hash_during_initialize",
            "negative_os_alias_index", "negative_os_alias_declarations_initialize",
            "negative_os_cache_initialize", "negative_os_file_hash",
            "negative_stdlib_exact",
        )
        self.helpers = "\n".join(shell_function(self.source, name) for name in names)

    def invoke(self, *, paths, inventory_paths=None, aliases=(), elf_paths=(),
               head_elf=(), expire_tool=""):
        with tempfile.TemporaryDirectory(prefix="n04-stdlib-declarations-") as temp:
            root = Path(temp)
            audit_path = root / "os-audit.json"
            calls_path = root / "calls"
            audit_path.write_text(json.dumps(audit(paths, aliases=aliases,
                                                   elf_paths=elf_paths)), encoding="utf-8")
            loop = self.loop.replace(
                'bounded jq -e --arg p "$python_resolved" "[.elf[]|select(.path==$p)]|length==1" "$os_audit"',
                'bounded jq -e --arg p "$python_resolved" \'[.elf[]|select(.path==$p)]|length==1\' "$os_audit"',
            )
            program = r'''set -euo pipefail
fail() { printf '%s\n' "$1" >&2; exit 1; }
remaining() {
 local n=0 row
 if [[ -f $CALLS ]]; then while IFS= read -r row; do [[ $row != remaining:* ]] || n=$((n+1)); done <"$CALLS"; fi
 n=$((n+1)); printf 'remaining:%s\n' "$n" >>"$CALLS"
}
bounded() {
 local tool=$1 result status=0
 shift
 printf 'bounded:%s\n' "$tool" >>"$CALLS"
 case $tool in
  jq) command jq "$@" || status=$? ;;
  head)
   if [[ $HEAD_ELF == *"$3"* ]]; then printf '\177ELF'; else printf 'data'; fi
   ;;
  *) fail fixture-unexpected-tool ;;
 esac
 ((status==0)) || fail bounded-operation
 if [[ $EXPIRE_TOOL == "$tool" || ( $EXPIRE_TOOL == elf && $tool == jq && $* == *"[.elf[]"* ) ]]; then fail fixture-deadline; fi
 remaining
}
ancestor() { printf 'ancestor:%s\n' "$1" >>"$CALLS"; }
absolute() { :; }
pin_file() { printf 'pin:%s:%s:%s\n' "$1" "$2" "$3" >>"$CALLS"; }
verify_os_path() { printf 'verify:%s:%s\n' "$1" "$2" >>"$CALLS"; }
negative_os_alias_index() {
 local p=$1 i
 for ((i=0;i<${#os_cache_alias_paths[@]};i++)); do
  if [[ $p == "${os_cache_alias_paths[$i]}" ]]; then printf '%s' "$i"; return 0; fi
 done
 return 1
}
alias_walk() { printf 'alias-walk:%s\n' "$1" >>"$CALLS"; printf '%s' "$ALIAS_RESOLVED"; }
negative_stdlib_inventory() { printf 'inventory\n' >>"$CALLS"; printf '%s' "$TEST_FILES"; }
'''
            declarations = r'''os_audit=$AUDIT
os_cache_ready=0
declare -a os_cache_file_paths=() os_cache_file_hashes=() os_cache_alias_paths=() os_cache_alias_rows=()
os_alias_declarations_ready=0
declare -a os_alias_resolved=() os_alias_hashes=() os_alias_starts=() os_alias_counts=()
declare -a os_alias_link_paths=() os_alias_link_targets=() os_alias_link_metadata=()
'''
            tail = r'''
negative_os_cache_initialize
''' + loop + r'''
stdlib_after=$(negative_stdlib_inventory)
negative_stdlib_exact "$stdlib_expected" "$stdlib_after" || fail negative-stdlib-membership-after
pin_file "$os_audit" "$AUDIT_HASH" 1048576
printf 'done\n'
'''
            environment = {
                "PATH": "/usr/bin:/bin", "LC_ALL": "C", "AUDIT": str(audit_path),
                "AUDIT_HASH": "c" * 64, "CALLS": str(calls_path),
                "TEST_FILES": "\n".join(sorted(inventory_paths if inventory_paths is not None else paths)),
                "HEAD_ELF": "\n".join(head_elf),
                "EXPIRE_TOOL": expire_tool, "ALIAS_RESOLVED": RESOLVED,
            }
            result = subprocess.run(
                ["/bin/bash", "--noprofile", "--norc", "-c",
                 program + declarations + self.helpers + tail],
                env=environment, capture_output=True, timeout=5,
            )
            calls = calls_path.read_text().splitlines() if calls_path.exists() else []
            return result, calls

    def test_ordinary_and_alias_members_still_use_physical_verifier_and_final_checks(self):
        paths = [PLAIN, ALIAS]
        result, calls = self.invoke(paths=paths, aliases=[file_alias()],
                                    head_elf=[ALIAS], elf_paths=[RESOLVED])
        self.assertEqual((0, b"done\n", b""),
                         (result.returncode, result.stdout, result.stderr))
        self.assertIn(f"verify:{PLAIN}:{HASH_A}", calls)
        self.assertIn(f"verify:{ALIAS}:{HASH_B}", calls)
        self.assertIn("alias-walk:0", calls)
        self.assertEqual(2, calls.count("inventory"))
        one_file, one_calls = self.invoke(paths=[PLAIN])
        self.assertEqual(0, one_file.returncode, one_file.stderr)
        two_files, two_calls = self.invoke(paths=[PLAIN, EXTRA])
        self.assertEqual(0, two_files.returncode, two_files.stderr)
        self.assertEqual(one_calls.count("bounded:jq"), two_calls.count("bounded:jq"))
        self.assertTrue(calls[-1].startswith("pin:"))
        self.assertTrue(calls[-1].endswith(f":{'c' * 64}:1048576"))
        first_inventory = calls.index("inventory")
        final_inventory = calls.index("inventory", first_inventory + 1)
        first_verifier = min(i for i, call in enumerate(calls) if call.startswith("verify:"))
        self.assertLess(first_inventory, first_verifier)
        self.assertLess(first_verifier, final_inventory)
        final_pin = next(i for i, call in enumerate(calls) if call.startswith("pin:"))
        self.assertGreater(final_pin, final_inventory)

    def test_cached_lookup_returns_only_declared_hash_and_missing_pin_fails_closed(self):
        result, _ = self.invoke(paths=[PLAIN])
        self.assertEqual(0, result.returncode, result.stderr)
        # The same actual helper must reject a path absent from the validated table.
        program = self.helpers + r'''
fail() { printf '%s\n' "$1" >&2; exit 1; }
os_cache_ready=1; os_cache_file_paths=(/a); os_cache_file_hashes=("$EXPECTED")
negative_os_file_hash /missing
'''
        missing = subprocess.run(["/bin/bash", "--noprofile", "--norc", "-c", program],
                                 env={"PATH": "/usr/bin:/bin", "EXPECTED": HASH_A},
                                 capture_output=True, timeout=2)
        self.assertNotEqual(0, missing.returncode)
        self.assertEqual(b"", missing.stdout)

    def test_inventory_mismatch_elf_record_absence_and_deadline_remain_rejections(self):
        mismatch, calls = self.invoke(paths=[PLAIN], inventory_paths=[PLAIN, ALIAS])
        self.assertNotEqual(0, mismatch.returncode)
        self.assertEqual(["inventory"], [call for call in calls if call == "inventory"])
        missing_elf, calls = self.invoke(paths=[PLAIN], elf_paths=[], head_elf=[PLAIN])
        self.assertNotEqual(0, missing_elf.returncode)
        self.assertIn(f"verify:{PLAIN}:{HASH_A}", calls)
        self.assertNotIn("pin:", calls[-1])
        expired, calls = self.invoke(paths=[PLAIN], elf_paths=[PLAIN], head_elf=[PLAIN],
                                     expire_tool="elf")
        self.assertNotEqual(0, expired.returncode)
        self.assertIn(f"verify:{PLAIN}:{HASH_A}", calls)
        self.assertNotIn("inventory", calls[calls.index(f"verify:{PLAIN}:{HASH_A}") + 1:])


if __name__ == "__main__":
    unittest.main()
