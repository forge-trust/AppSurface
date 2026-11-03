"""Actual None Bash OS-cache and detached snapshot controls; no native authority."""
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parent
SCRIPT = ROOT / "procedures/slot/checkpoint-none.sh"
HASH_A = "a" * 64
HASH_B = "b" * 64


def function(source, name):
    start = source.index(name + "() {")
    line = source[start:source.index("\n", start)]
    return line if line.endswith(" }") else source[start:source.index("\n}", start) + 2]


def declarations(source):
    start = source.index("os_declaration_alias_ready=0")
    end = source.index("\n# BEGIN ordinary-os-batch candidate definitions", start)
    return source[start:end]


def fixture_alias(literal="/owned/a", digest=HASH_A):
    return {"literal": literal, "resolved_sha256": digest,
            "resolved_path": "/owned/resolved", "links": []}


def audit(files=None, aliases=None):
    return {"files": files if files is not None else [
        {"path": "/owned/z", "sha256": HASH_B},
        {"path": "/owned/a", "sha256": HASH_A}],
        "aliases": aliases if aliases is not None else []}


class NoneOrdinaryOsBatchTests(unittest.TestCase):
    def invoke_cache(self, value, action="positive", *, clock_fail=0,
                     count_override="", rows_override=""):
        source = SCRIPT.read_text()
        program = "set -euo pipefail\nif ((BASH_VERSINFO[0]<4)); then set +u; fi\n" + function(source, "lexical") + "\n" + function(source, "sha") + "\n"
        program += declarations(source) + r'''
fail() { printf '%s\n' "$1" >&2; exit 1; }
remaining() {
 local count=0 row
 if [[ -f $CALLS ]]; then while IFS= read -r row; do count=$((count+1)); done <"$CALLS"; fi
 count=$((count+1)); printf 'remaining\n' >>"$CALLS"
 [[ $CLOCK_FAIL == 0 || $count != "$CLOCK_FAIL" ]] || fail fixture-deadline
 printf '120\n'
}
bounded() {
 if [[ $1 == jq && $3 == '.files|length' && -n $COUNT_OVERRIDE ]]; then printf '%s\n' "$COUNT_OVERRIDE"
 elif [[ $1 == jq && $3 == '.files|sort_by(.path)|.[]|[.path,.sha256]|@tsv' && -n $ROWS_OVERRIDE ]]; then printf '%s\n' "$ROWS_OVERRIDE"
 else [[ $1 == jq ]] || exit 94; command "$@"; fi
}
MAX_INPUT_FILE_BYTES=2147483648
case "$ACTION" in
 uninitialized) initialize_ordinary_os_declarations ;;
 *)
  initialize_alias_declarations
  initialize_ordinary_os_declarations
  case "$ACTION" in
   positive)
    printf '%s\n' "${os_cache_file_paths[@]}"
    printf '%s\n' "$(negative_os_file_hash /owned/a)"
    if negative_os_file_hash /missing; then fail unexpected-hash; else printf 'MISSING\n'; fi
    ;;
   replay) initialize_ordinary_os_declarations ;;
   readonly) os_cache_file_paths[0]=/forged ;;
   *) exit 93 ;;
  esac ;;
esac
'''
        with tempfile.TemporaryDirectory(prefix="c1-none-os-cache-") as temporary:
            path = Path(temporary) / "audit.json"
            path.write_text(json.dumps(value), encoding="utf-8")
            env = {"PATH": os.environ["PATH"], "LC_ALL": "C", "os_audit": str(path),
                   "ACTION": action, "CLOCK_FAIL": str(clock_fail),
                   "COUNT_OVERRIDE": count_override, "ROWS_OVERRIDE": rows_override,
                   "CALLS": str(Path(temporary) / "calls")}
            return subprocess.run(["/bin/bash", "--noprofile", "--norc", "-c", program],
                                  env=env, capture_output=True, timeout=3)

    def test_actual_declarations_sort_bind_hash_freeze_and_fail_closed(self):
        result = self.invoke_cache(audit(aliases=[fixture_alias()]))
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(f"/owned/a\n/owned/z\n{HASH_A}\nMISSING\n".encode(), result.stdout)
        self.assertNotEqual(0, self.invoke_cache(audit(), "readonly").returncode)
        self.assertEqual(b"alias-declaration-uninitialized\n",
                         self.invoke_cache(audit(), "uninitialized").stderr)
        self.assertEqual(b"OS-cache-replay\n", self.invoke_cache(audit(), "replay").stderr)
        self.assertEqual(b"fixture-deadline\n", self.invoke_cache(audit(), clock_fail=1).stderr)
        for count in ("0", "4097", "01", "true"):
            rejected = self.invoke_cache(audit(), count_override=count)
            self.assertEqual((1, b"OS-cache-count\n"), (rejected.returncode, rejected.stderr))

    def test_actual_declaration_rows_reject_empty_duplicate_order_truncation_count_and_alias_mismatch(self):
        cases = (
            (audit(files=[]), "", "", b"OS-cache-count\n"),
            (audit(files=[{"path":"/owned/a","sha256":HASH_A},
                          {"path":"/owned/a","sha256":HASH_A}]), "", "", b"OS-cache-file-order\n"),
            (audit(), "", f"/owned/z\t{HASH_B}\n/owned/a\t{HASH_A}", b"OS-cache-file-order\n"),
            (audit(files=[{"path":"/owned/a","sha256":HASH_A}]), "", f"/owned/a\t{HASH_A}\n/owned/z\t{HASH_B}", b"OS-cache-file-membership\n"),
            (audit(files=[{"path":"/owned/a","sha256":HASH_A}]), "", f"/owned/a\t", b"sha256-syntax\n"),
            (audit(files=[{"path":"/owned/a","sha256":HASH_B}], aliases=[fixture_alias()]), "", "", b"OS-cache-alias-file-hash\n"),
            (audit(files=[{"path":"/owned/z","sha256":HASH_B}], aliases=[fixture_alias()]), "", "", b"OS-cache-alias-file-membership\n"),
        )
        for value, _, rows, expected in cases:
            with self.subTest(expected=expected):
                result = self.invoke_cache(value, count_override="" if not rows else "1",
                                           rows_override=rows)
                self.assertEqual(expected, result.stderr)

    def test_alias_batch_reuses_the_existing_physical_verifier(self):
        source = SCRIPT.read_text()
        batch = source[source.index("# BEGIN ordinary-os-batch candidate definitions"):
                       source.index("# END ordinary-os-batch candidate definitions")]
        program = "set -euo pipefail\nif ((BASH_VERSINFO[0]<4)); then set +u; fi\n" + function(source, "lexical") + "\n" + function(source, "sha") + "\n"
        program += declarations(source) + "\n" + function_from(batch, "ordinary_os_batch_verify_aliases") + r'''
fail() { printf '%s\n' "$1" >&2; exit 1; }
remaining() { :; }
bounded() { command "$@"; }
MAX_INPUT_FILE_BYTES=2147483648
verify_os_path() { printf '%s\t%s\n' "$1" "$2"; }
initialize_alias_declarations
initialize_ordinary_os_declarations
ordinary_os_batch_verify_aliases
'''
        with tempfile.TemporaryDirectory(prefix="c1-none-alias-batch-") as temporary:
            path = Path(temporary) / "audit.json"
            path.write_text(json.dumps(audit(aliases=[fixture_alias()])), encoding="utf-8")
            result = subprocess.run(["/bin/bash", "--noprofile", "--norc", "-c", program],
                env={"PATH": os.environ["PATH"], "LC_ALL": "C", "os_audit": str(path)},
                capture_output=True, timeout=3)
        self.assertEqual((0, f"/owned/a\t{HASH_A}\n".encode(), b""),
                         (result.returncode, result.stdout, result.stderr))

    def test_snapshot_parser_reads_detached_nul_rows_from_real_fds(self):
        source = SCRIPT.read_text()
        block = source[source.index("# BEGIN ordinary-os-batch candidate definitions"):
                       source.index("# END ordinary-os-batch candidate definitions")]
        names = ("ordinary_os_batch_leaf_index", "ordinary_os_batch_decimal_at_most",
                 "ordinary_os_batch_parse_snapshot")
        funcs = "\n".join(function_from(block, name) for name in names)
        program = "set -euo pipefail\nif ((BASH_VERSINFO[0]<4)); then set +u; fi\n" + funcs + r'''
fail() { printf '%s\n' "$1" >&2; exit 1; }
tick=0; remaining() { tick=$((tick+1)); [[ $CLOCK_FAIL == 0 || $tick != "$CLOCK_FAIL" ]] || fail fixture-deadline; }
MAX_INPUT_FILE_BYTES=2147483648
osb_node_paths=(/ /detached /detached/file); osb_leaf_paths=(/detached/file); osb_pre_records=()
exec 3<"$PRE"; ordinary_os_batch_parse_snapshot pre 3; exec 3<&-
if [[ -n $POST ]]; then exec 3<"$POST"; ordinary_os_batch_parse_snapshot post 3; exec 3<&-; fi
'''
        outer = ["/", "1", "9", "4096", "41ed", "2", "0", "0", "100.1", "100.2"]
        root = ["/detached", "1", "10", "4096", "41ed", "2", "0", "0", "100.1", "100.2"]
        leaf = ["/detached/file", "1", "11", "4", "81a4", "1", "0", "0", "100.1", "100.2"]
        self.assertEqual(0, self.run_parser(program, [outer, root, leaf], [outer, root, leaf]).returncode)
        for field, value in ((1,"2"),(2,"12"),(3,"5"),(4,"8180"),(7,"10"),(8,"101.1"),(9,"101.2")):
            changed = leaf.copy(); changed[field] = value
            with self.subTest(changed_field=field):
                result = self.run_parser(program, [outer, root, leaf], [outer, root, changed])
                self.assertNotEqual(0, result.returncode)
                self.assertEqual(b"OS-batch-before-after\n", result.stderr)
        invalid = (([outer, root], None, b"OS-batch-node-count\n"),
                   ([outer, leaf, root], None, b"OS-batch-node-order\n"),
                   ([outer, root, leaf, leaf], None, b"OS-batch-node-count\n"),
                   ([], None, b"OS-batch-node-count\n"))
        for records, _, expected in invalid:
            self.assertEqual(expected, self.run_parser(program, records).stderr)
        malformed = []
        for index, value, reason in ((1,"x",b"OS-batch-stat-number\n"),
            (4,"nothex",b"OS-batch-stat-mode\n"),(3,"2147483649",b"OS-batch-file-bytes\n"),
            (4,"a1ff",b"OS-batch-owner-mode\n"),(4,"41ed",b"OS-batch-file-type-links\n"),(5,"2",b"OS-batch-file-type-links\n"),
            (6,"1000",b"OS-batch-owner-mode\n"),(4,"81b4",b"OS-batch-owner-mode\n")):
            bad=leaf.copy(); bad[index]=value; malformed.append((bad,reason))
        for bad, expected in malformed:
            with self.subTest(record=bad):
                self.assertEqual(expected, self.run_parser(program,[outer,root,bad]).stderr)
        self.assertEqual(b"OS-batch-partial-row\n", self.run_parser(program,[root], truncated=True).stderr)
        self.assertEqual(b"fixture-deadline\n", self.run_parser(program,[outer,root,leaf],clock_fail=1).stderr)

    def test_snapshot_groups_more_than_32_short_paths_but_splits_at_argv_byte_bound(self):
        source = SCRIPT.read_text()
        snapshot = function(source, "ordinary_os_batch_snapshot")
        program = "set -euo pipefail\n" + snapshot + r'''
fail() { printf '%s\n' "$1" >&2; exit 1; }
remaining() { :; }
batch_scratch_pin() { :; }
bounded() {
 if [[ $1 == stat && $2 == --printf ]]; then
  shift 2; local fmt=$1; shift; [[ $1 == -- ]] || exit 91; shift
  local count=$# bytes=0 path
  for path in "$@"; do ((bytes+=${#path}+1)); done
  printf '%s\t%s\n' "$count" "$bytes" >>"$CALLS"
  printf '%s\0' "$@" >>"$DISPATCHED"
  ((bytes<=16384)) || exit 92
  printf 'synthesized\0'
 elif [[ $1 == stat && $2 == -c && $3 == %s ]]; then printf '1\n'
 else exit 93; fi
}
osb_pre_records=(); osb_leaf_paths=(); osb_leaf_hashes=(); osb_node_paths=()
if [[ $KIND == short ]]; then
 for ((i=0;i<100;i++)); do osb_node_paths+=("/x/$i"); done
else
 for ((i=0;i<200;i++)); do printf -v path '/deep/%04d/%03000d' "$i" "$i"; osb_node_paths+=("$path"); done
fi
exec 9>"$SINK"
ordinary_os_batch_snapshot "$SINK" 9
'''
        for relative in ("procedures/slot/checkpoint-n06.sh",
                         "procedures/slot/checkpoint-n05.sh",
                         "procedures/n12/checkpoint-n12-data-integration.sh"):
            source = (ROOT / relative).read_text()
            snapshot = function(source, "ordinary_os_batch_snapshot")
            selected_program = program.replace(function(SCRIPT.read_text(), "ordinary_os_batch_snapshot"), snapshot)
            with tempfile.TemporaryDirectory(prefix="c1-stat-batch-bound-") as temporary:
                calls = Path(temporary) / "calls.tsv"
                dispatched = Path(temporary) / "dispatched.bin"
                sink = Path(temporary) / "sink"
                dispatches_by_kind = {}
                for kind in ("short", "long"):
                    calls.unlink(missing_ok=True)
                    dispatched.unlink(missing_ok=True)
                    result = subprocess.run(["/bin/bash", "--noprofile", "--norc", "-c", selected_program],
                        env={"PATH": os.environ["PATH"], "LC_ALL": "C", "CALLS": str(calls),
                             "DISPATCHED": str(dispatched), "SINK": str(sink), "KIND": kind},
                        capture_output=True, timeout=3)
                    self.assertEqual((0, b"", b""), (result.returncode, result.stdout, result.stderr), relative)
                    dispatches_by_kind[kind] = (
                        [tuple(map(int, line.split("\t"))) for line in calls.read_text().splitlines()],
                        dispatched.read_bytes().split(b"\0")[:-1])
                short_dispatches, short_paths = dispatches_by_kind["short"]
                self.assertEqual(1, len(short_dispatches), relative)
                self.assertEqual(100, short_dispatches[0][0], relative)
                self.assertGreater(short_dispatches[0][0], 32, relative)
                self.assertEqual([f"/x/{i}".encode() for i in range(100)], short_paths, relative)
                long_dispatches, long_paths = dispatches_by_kind["long"]
                self.assertGreater(len(long_dispatches), 1, relative)
                self.assertTrue(all(0 < size <= 16384 for _, size in long_dispatches), relative)
                self.assertTrue(all(count < 32 for count, _ in long_dispatches), relative)
                self.assertEqual(200, sum(count for count, _ in long_dispatches), relative)
                expected_long_paths = [f"/deep/{i:04d}/{i:03000d}".encode() for i in range(200)]
                self.assertEqual(expected_long_paths, long_paths, relative)

    def run_parser(self, program, rows, post=None, *, truncated=False, clock_fail=0):
        with tempfile.TemporaryDirectory(prefix="c1-none-stat-fd-") as temporary:
            def write(name, values, cut=False):
                raw=b"".join(b"\0".join(x.encode() for x in row)+b"\0" for row in values)
                if cut and raw: raw=raw[:-1]
                path=Path(temporary)/name; path.write_bytes(raw); return str(path)
            env={"PATH":os.environ["PATH"],"LC_ALL":"C","PRE":write("pre",rows,truncated),
                 "POST":write("post",post) if post is not None else "","CLOCK_FAIL":str(clock_fail)}
            return subprocess.run(["/bin/bash","--noprofile","--norc","-c",program],
                env=env,capture_output=True,timeout=3)

    def test_observation_helper_has_fixed_names_and_keeps_both_clock_checks(self):
        source = SCRIPT.read_text()
        program = "set -euo pipefail\n" + function(source, "fixture_stage_observation") + r'''
fail() { printf '%s\n' "$1" >&2; exit 1; }
monotonic() { printf 'clock\n' >>"$CALLS"; printf '110\n'; }
remaining() {
 local count=0 row
 if [[ -f $CALLS ]]; then
  while IFS= read -r row; do [[ $row != remaining ]] || count=$((count+1)); done <"$CALLS"
 fi
 count=$((count+1)); printf 'remaining\n' >>"$CALLS"
 [[ $EXPIRE == 0 || $count != "$EXPIRE" ]] || fail fixture-deadline
 printf '200\n'
}
fixture_start=100
fixture_stage_observation "$EVENT" "$STAGE"
printf 'returned\n'
'''
        def run(stage, event="begin", expire=0):
            with tempfile.TemporaryDirectory(prefix="c1-none-stage-") as directory:
                calls = Path(directory) / "calls"
                result = subprocess.run(["/bin/bash", "--noprofile", "--norc", "-c", program],
                    env={"EXPIRE": str(expire), "STAGE": stage, "EVENT": event,
                         "CALLS": str(calls)}, capture_output=True, timeout=2)
                return result, calls.read_text().splitlines() if calls.exists() else []
        for stage in ("declaration-cache", "ordinary-os-initial", "elf-closure",
                      "sealed-trees", "ordinary-os-final"):
            for event in ("begin", "end"):
                good, calls = run(stage, event)
                self.assertEqual((0, b"returned\n"), (good.returncode, good.stdout))
                self.assertEqual({"schema": "issue779-fixture-stage-observation-v1",
                    "status": "observations-only", "stage": stage, "event": event,
                    "elapsed_seconds": 10, "remaining_seconds": 200}, json.loads(good.stderr))
                self.assertEqual(["clock", "remaining", "remaining"], calls)
        bad, calls = run("unknown-canary")
        self.assertEqual((1, b"timing-stage\n", []), (bad.returncode, bad.stderr, calls))
        for sample in (1, 2):
            bad, calls = run("ordinary-os-initial", expire=sample)
            self.assertEqual(1, bad.returncode)
            self.assertTrue(bad.stderr.endswith(b"fixture-deadline\n"))
            self.assertEqual(sample, calls.count("remaining"))

    def test_batch_block_is_existing_n06_and_all_original_guards_remain(self):
        source = SCRIPT.read_text()
        reference = (ROOT / "procedures/slot/checkpoint-n06.sh").read_text()
        start, end = "# BEGIN ordinary-os-batch candidate definitions", "# END ordinary-os-batch candidate definitions"
        block = lambda text: text[text.index(start):text.index(end) + len(end)]
        normalized = lambda text: text.replace("while ((i<${#osb_node_paths[@]} && ${#chunk[@]}<32)); do",
                                                "while ((i<${#osb_node_paths[@]})); do")
        self.assertEqual(normalized(block(reference)), normalized(block(source)))
        for relative in ("procedures/slot/checkpoint-n05.sh",
                         "procedures/n12/checkpoint-n12-data-integration.sh"):
            self.assertEqual(block(reference), block((ROOT / relative).read_text()))
        self.assertEqual(1, source.count("\nordinary_os_batch_full_pass initial\n"))
        self.assertEqual(1, source.count("\nordinary_os_batch_full_pass final\n"))
        self.assertLess(source.index("fixture_stage_observation end elf-closure\n"),
                        source.index("if [[ $mode == prepare-only ]]; then printf"))
        self.assertIn("readonly FIXTURE_SECONDS=600 CLEANUP_RESERVE=30", source)
        self.assertIn('(( $(remaining)>246 && job_deadline_epoch-$(bounded date -u +%s)>245 )) || fail native-life-outside-fixture', source)


def function_from(source, name):
    start=source.index(name+"() {")
    return source[start:source.index("\n}",start)+2]


if __name__ == "__main__":
    unittest.main()
