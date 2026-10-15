#!/usr/bin/env python3
"""Data-only N10 parser controls; never creates a lease, worker, signal, or native result."""
import ast
import hashlib
import re
import importlib.util
import json
import stat
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parent
SPEC = importlib.util.spec_from_file_location("n10_parser_under_test", ROOT / "check_n10_pending_start.py")
MOD = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MOD)
GEN = "11111111222233334444555555555555"
UNIT = "appsurface-evidence-worker-" + GEN + ".service"
JOB = "/org/freedesktop/systemd1/job/17"


def frames():
    start = {"schema": "issue779-n10-pending-start-phase-v1", "case": "N10",
             "phase": "start-transient-unit-reply", "unit": UNIT, "job_path": JOB,
             "native_authority": False, "native_acceptance": False}
    end = {"schema": "issue779-n10-pending-start-observation-v1", "case": "N10",
           "phase": "original-start-stop-and-pumps-joined-before-custody", "generation": GEN,
           "unit": UNIT, "job_path": JOB, "start_reply_observed": True,
           "pending_start": {"start_reserved": True, "started": False, "start_joined": True,
                             "closed": True, "stop_joined": True, "first_failure": "StartCancelled"},
           "lifetime": {"startup_joined": True, "startup_failed": True, "stop_joined": True,
                        "physically_settled": False},
           "original_stop_delegate_calls": 2,
           "final_unit": {"id": UNIT, "active_state": "inactive", "sub_state": "dead", "main_pid": 0,
                          "exec_main_pid": 217, "exec_main_code": 1, "exec_main_status": 2, "stopped": True},
           "final_group_after_pumps": {"sample_taken": True, "empty": True, "exists": True,
                                       "populated": False, "frozen": False, "device_major": 0,
                                       "device_minor": 34, "inode": 123},
           "pumps": {"joined": True, "received_bytes": 0, "discarded_bytes": 0, "failure": "None",
                     "stdout_eof": True, "stdout_failure": "None", "stderr_eof": True,
                     "stderr_failure": "None"},
           "root_custody_completed": False, "native_authority": False, "native_acceptance": False}
    return start, end


def encode(start=None, end=None):
    a, b = frames()
    return (json.dumps(start or a, separators=(",", ":")) + "\n" +
            json.dumps(end or b, separators=(",", ":")) + "\n").encode()


class N10ParserControls(unittest.TestCase):
    def test_joined_actual_contract_is_data_only(self):
        result = MOD.validate_bytes(encode())
        self.assertEqual(GEN, result["generation"])
        self.assertEqual(2, result["original_stop_delegate_calls"])
        self.assertFalse(result["physically_settled"])
        self.assertTrue(result["final_group_empty"])
        self.assertFalse(result["native_authority"])
        self.assertFalse(result["native_acceptance"])

    def test_phase_requires_original_reply_only(self):
        result = MOD.validate_bytes((json.dumps(frames()[0]) + "\n").encode(), "phase")
        self.assertEqual(JOB, result["job_path"])

    def test_bad_unit_and_job_are_rejected(self):
        for field, value in (("unit", "appsurface-evidence-worker-canary.service"),
                             ("job_path", "/org/freedesktop/systemd1/unit/17")):
            start, _ = frames()
            start[field] = value
            with self.subTest(field=field), self.assertRaises(MOD.Reject):
                MOD.validate_bytes(encode(start=start))

    def test_reply_must_precede_unique_join_frame(self):
        start, end = frames()
        with self.assertRaises(MOD.Reject):
            MOD.validate_bytes((json.dumps(end) + "\n" + json.dumps(start) + "\n").encode())
        with self.assertRaises(MOD.Reject):
            MOD.validate_bytes(encode() + (json.dumps(start) + "\n").encode())

    def test_two_stops_sticky_failure_and_false_physical_state_are_required(self):
        for mutate in (
            lambda x: x["pending_start"].update(first_failure="None"),
            lambda x: x.update(original_stop_delegate_calls=1),
            lambda x: x["lifetime"].update(physically_settled=True),
            lambda x: x["lifetime"].update(startup_failed=False),
            lambda x: x["pending_start"].update(started=True),
        ):
            start, end = frames()
            mutate(end)
            with self.assertRaises(MOD.Reject):
                MOD.validate_bytes(encode(start, end))

    def test_pending_start_flags_reject_integer_boolean_aliases(self):
        for field in ("start_reserved", "started", "start_joined", "closed", "stop_joined"):
            start, end = frames()
            # The integer equals the valid bool under Python dictionary equality.
            end["pending_start"][field] = int(end["pending_start"][field])
            with self.subTest(field=field), self.assertRaises(MOD.Reject):
                MOD.validate_bytes(encode(start, end))

    def test_post_pump_group_and_error_free_eof_are_required(self):
        for mutate in (
            lambda x: x["final_group_after_pumps"].update(sample_taken=False),
            lambda x: x["final_group_after_pumps"].update(empty=False),
            lambda x: x["pumps"].update(stderr_eof=False),
            lambda x: x["pumps"].update(stdout_failure="Read"),
            lambda x: x["pumps"].update(discarded_bytes=1),
        ):
            start, end = frames()
            mutate(end)
            with self.assertRaises(MOD.Reject):
                MOD.validate_bytes(encode(start, end))

    def test_final_unit_accepts_source_terminal_alternatives_and_rejects_bad_primitives(self):
        start, end = frames()
        end["final_unit"].update(active_state="failed", sub_state="failed", exec_main_code=2,
                                 exec_main_status=9)
        self.assertTrue(MOD.validate_bytes(encode(start, end))["unit_stopped"])
        for field, value in (("active_state", "active"), ("sub_state", "exited"), ("main_pid", 1),
                             ("exec_main_pid", True), ("exec_main_pid", 0),
                             ("exec_main_code", 1.0), ("exec_main_code", 4),
                             ("exec_main_status", True), ("exec_main_status", 256)):
            start, end = frames()
            end["final_unit"][field] = value
            with self.subTest(field=field, value=value), self.assertRaises(MOD.Reject):
                MOD.validate_bytes(encode(start, end))
        start, end = frames()
        end["final_unit"].update(exec_main_code=2, exec_main_status=65)
        with self.assertRaises(MOD.Reject):
            MOD.validate_bytes(encode(start, end))

    def test_group_accepts_absent_all_null_and_rejects_mixed_or_contradictory_samples(self):
        start, end = frames()
        end["final_group_after_pumps"].update(exists=False, populated=None, frozen=None,
                                               device_major=None, device_minor=None, inode=None)
        self.assertTrue(MOD.validate_bytes(encode(start, end))["final_group_empty"])
        for field, value in (("exists", False), ("populated", True), ("frozen", True),
                             ("device_major", True), ("device_minor", 1.0),
                             ("inode", False), ("inode", 0)):
            start, end = frames()
            if field == "exists":
                end["final_group_after_pumps"].update(exists=False, populated=None, frozen=None,
                                                       device_major=None, device_minor=None, inode=None)
                end["final_group_after_pumps"]["device_major"] = 0
            else:
                end["final_group_after_pumps"][field] = value
            with self.subTest(field=field, value=value), self.assertRaises(MOD.Reject):
                MOD.validate_bytes(encode(start, end))

    def test_custody_or_acceptance_cannot_be_upgraded(self):
        for field, value in (("native_authority", True), ("native_acceptance", True),
                             ("root_custody_completed", True)):
            start, end = frames()
            (start if field != "root_custody_completed" else end)[field] = value
            with self.assertRaises(MOD.Reject):
                MOD.validate_bytes(encode(start, end))


class _SourceFactRejected(Exception):
    pass


def _source_fact_require(condition, code):
    if not condition:
        raise _SourceFactRejected(code)


def _load_source_fact_validator():
    runner = Path(__file__).with_name('run-native.py')
    tree = ast.parse(runner.read_text(encoding='utf-8'))
    functions = [node for node in tree.body if isinstance(node, ast.FunctionDef) and node.name == 'validate_source_facts']
    if len(functions) != 1:
        raise AssertionError('expected exactly one pure source-fact validator')
    namespace = {'re': re, 'require': _source_fact_require}
    exec(compile(ast.Module(body=functions, type_ignores=[]), str(runner), 'exec'), namespace)
    return namespace['validate_source_facts'], tree


class SourceDigestBoundaryControls(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        validator, cls.runner_ast = _load_source_fact_validator()
        cls.validate = staticmethod(validator)
        cls.head = '1' * 40
        cls.rows = {'Evidence/alpha.cs': {'sha256': hashlib.sha256(b'alpha').hexdigest(), 'mode': '0644'},
                    'Evidence/beta.sh': {'sha256': hashlib.sha256(b'beta').hexdigest(), 'mode': '0755'}}
        cls.product = cls._product(cls.rows)
        cls.canonical = hashlib.sha256((json.dumps(cls.rows, sort_keys=True, separators=(',', ':'), ensure_ascii=False) + '\n').encode()).hexdigest()

    @staticmethod
    def _product(rows):
        projected = {'sha256': {p:r['sha256'] for p,r in rows.items()}, 'modes': {p:r['mode'] for p,r in rows.items()}}
        return hashlib.sha256(json.dumps(projected, sort_keys=True, separators=(',', ':')).encode()).hexdigest()

    def fact(self, **updates):
        row={'head':self.head,'count':2,'source_map_sha256':self.product,'index_tree_matches':True,'physical_git_sha1':True,'physical_sha256_modes':True}; row.update(updates); return row

    def test_independent_product_projection_accepts_and_constants_remain_distinct(self):
        self.assertNotEqual(self.canonical,self.product)
        self.assertIsNone(self.validate(self.fact(),2,self.head,self.product))
        values={n.targets[0].id:n.value.value for n in self.runner_ast.body if isinstance(n,ast.Assign) and len(n.targets)==1 and isinstance(n.targets[0],ast.Name) and n.targets[0].id in ('SOURCE_MAP','BUILD_SOURCE_MAP') and isinstance(n.value,ast.Constant)}
        self.assertEqual(values.get('SOURCE_MAP'),'a3dbb4a7be588b3e5234bf728e4eda4032f5ee8acab1d2ab7213cceef106586e')
        self.assertEqual(values.get('BUILD_SOURCE_MAP'),'07e4be7c1c59989a70d88c345ff360e68a0a394a6729877afa7981d6d913d238')

    def test_canonical_and_wrong_product_digests_reject(self):
        for digest in (self.canonical,'0'*64):
            with self.assertRaises(_SourceFactRejected): self.validate(self.fact(source_map_sha256=digest),2,self.head,self.product)

    def test_file_bytes_and_mode_mutations_reject(self):
        changed_bytes={p:dict(r) for p,r in self.rows.items()}; changed_bytes['Evidence/alpha.cs']['sha256']=hashlib.sha256(b'changed').hexdigest()
        changed_mode={p:dict(r) for p,r in self.rows.items()}; changed_mode['Evidence/beta.sh']['mode']='0644'
        for rows in (changed_bytes,changed_mode):
            digest=self._product(rows); self.assertNotEqual(digest,self.product)
            with self.assertRaises(_SourceFactRejected): self.validate(self.fact(source_map_sha256=digest),2,self.head,self.product)

    def test_identity_count_and_integrity_types_are_strict(self):
        bad=((self.fact(count=True),2,self.head,self.product),(self.fact(),True,self.head,self.product),(self.fact(),2,'2'*40,self.product),(self.fact(),2,'bad',self.product),(self.fact(),2,self.head,'bad'))
        for fact,count,head,digest in bad:
            with self.assertRaises(_SourceFactRejected): self.validate(fact,count,head,digest)
        for key in ('index_tree_matches','physical_git_sha1','physical_sha256_modes'):
            for value in (False,1,None):
                with self.assertRaises(_SourceFactRejected): self.validate(self.fact(**{key:value}),2,self.head,self.product)
            missing=self.fact(); del missing[key]
            with self.assertRaises(_SourceFactRejected): self.validate(missing,2,self.head,self.product)


class CaptureMapOperandControls(unittest.TestCase):
    """Execute the installed jq statement with the selected file, without root work."""

    def project(self, capture, cwd):
        acquisition = (ROOT / 'acquire-inbound.sh').read_text()
        statements = [line for line in acquisition.splitlines()
                      if line.startswith('capture_map=$(bounded ')]
        self.assertEqual(1, len(statements))
        jq = shutil.which('jq')
        self.assertIsNotNone(jq, 'The actual jq encoder is required for this control.')
        # macOS has no /usr/bin/jq. Only the fixed executable is mapped; the
        # installed expression and selected-file operand are executed verbatim.
        script = ('bounded() { [[ $1 == /usr/bin/jq ]] || return 64; shift; '
                  '"$JQ_PATH" "$@"; }\n'
                  'fail() { exit 65; }\ncapture=$1\n' + statements[0] +
                  '\nprintf "%s" "$capture_map"\n')
        return subprocess.run(['/bin/bash', '-c', script, 'capture-map-control', str(capture)],
                              cwd=cwd, capture_output=True, timeout=10,
                              env={'PATH': '/usr/bin:/bin', 'LC_ALL': 'C', 'JQ_PATH': jq})

    def test_selected_absolute_path_with_spaces_is_used(self):
        with tempfile.TemporaryDirectory(prefix='n10-capture-') as temp:
            cwd = Path(temp)
            selected = cwd / 'selected capture.json'
            selected.write_text(json.dumps({'source': {'sha256': {'a': '1' * 64},
                                                       'modes': {'a': '0644'}}}))
            result = self.project(selected, cwd)
            self.assertEqual(0, result.returncode)
            self.assertEqual({'source_sha256': {'a': '1' * 64}, 'source_modes': {'a': '0644'}},
                             json.loads(result.stdout))

    def test_cwd_decoy_cannot_replace_the_selected_capture(self):
        with tempfile.TemporaryDirectory(prefix='n10-capture-') as temp:
            cwd = Path(temp)
            selected = cwd / 'selected.json'
            selected.write_text(json.dumps({'source': {'sha256': {'real': '2' * 64},
                                                       'modes': {'real': '0755'}}}))
            (cwd / 'capture').write_text(json.dumps({'source': {'sha256': {'decoy': '3' * 64},
                                                                 'modes': {'decoy': '0644'}}}))
            result = self.project(selected, cwd)
            self.assertEqual(0, result.returncode)
            self.assertEqual({'real': '2' * 64}, json.loads(result.stdout)['source_sha256'])
            rejected = self.project(cwd / 'missing.json', cwd)
            self.assertNotEqual(0, rejected.returncode)
            self.assertEqual(b'', rejected.stdout)

    def test_actual_full_capture_matches_pinned_encoder_and_mode_mutation_differs(self):
        capture = ROOT.parent / 'issue779-csharp-build/capture-receipt.json'
        expected_capture = '4ad23a2fdeccf9abbcd06724b9c97e3a4c1751ecb45e8321cc35bd79c036d540'
        self.assertEqual(expected_capture, hashlib.sha256(capture.read_bytes()).hexdigest())
        with tempfile.TemporaryDirectory(prefix='n10-capture-') as temp:
            cwd = Path(temp)
            result = self.project(capture, cwd)
            self.assertEqual(0, result.returncode)
            expected = 'a3dbb4a7be588b3e5234bf728e4eda4032f5ee8acab1d2ab7213cceef106586e'
            self.assertEqual(expected, hashlib.sha256(result.stdout).hexdigest())
            mutated = json.loads(capture.read_bytes())
            name = next(iter(mutated['source']['modes']))
            mutated['source']['modes'][name] = ('0755' if mutated['source']['modes'][name] == '0644'
                                               else '0644')
            selected = cwd / 'mode-mutated.json'
            selected.write_text(json.dumps(mutated))
            different = self.project(selected, cwd)
            self.assertEqual(0, different.returncode)
            self.assertNotEqual(expected, hashlib.sha256(different.stdout).hexdigest())


class SystemctlNamedPropertyControls(unittest.TestCase):
    """Fixed-data Bash checks for the exact parser embedded in the N10 fixture."""

    @classmethod
    def setUpClass(cls):
        fixture = ROOT / "checkpoint-n10-pending-start.sh"
        source = fixture.read_text()
        begin = "# BEGIN N10 named systemctl property parser\n"
        end = "# END N10 named systemctl property parser"
        if source.count(begin) != 1 or source.count(end) != 1:
            raise AssertionError("named systemctl parser boundaries")
        cls.helper = source.split(begin, 1)[1].split(end, 1)[0]

    def evaluate(self, records, role, unit):
        script = (
            "set -euo pipefail\n" + self.helper + "\ninput=$(cat)\n"
            f"parse_n10_systemctl_properties \"$input\" && "
            f"n10_systemctl_identity_matches {role} {unit} || exit 41\n"
            "printf 'MATCH:%s:%s:%s\n' \"$n10_pid\" \"$n10_uid\" \"$n10_gid\"\n"
        )
        return subprocess.run(
            ["bash", "--noprofile", "--norc", "-c", script],
            input=records, text=True, capture_output=True, timeout=2, check=False,
        )

    def test_owner_properties_are_order_independent(self):
        rows = "ControlGroup=/system.slice/ev-n10-owner.service\nUser=0\nMainPID=4101\nSubState=running\nGroup=0\nActiveState=active"
        result = self.evaluate(rows, "owner", "ev-n10-owner.service")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("MATCH:4101:0:0\n", result.stdout)

    def test_worker_properties_are_order_independent(self):
        rows = "Group=42\nActiveState=active\nControlGroup=/system.slice/ev-n10-worker.service\nMainPID=4102\nUser=41\nSubState=running"
        result = self.evaluate(rows, "worker", "ev-n10-worker.service")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("MATCH:4102:41:42\n", result.stdout)

    def test_missing_property_is_rejected(self):
        rows = "ActiveState=active\nSubState=running\nMainPID=4101\nUser=0\nGroup=0"
        self.assertEqual(41, self.evaluate(rows, "owner", "ev-n10-owner.service").returncode)

    def test_duplicate_property_is_rejected(self):
        rows = "ActiveState=active\nActiveState=inactive\nSubState=running\nMainPID=4101\nUser=0\nGroup=0\nControlGroup=/system.slice/ev-n10-owner.service"
        self.assertEqual(41, self.evaluate(rows, "owner", "ev-n10-owner.service").returncode)

    def test_unrequested_property_is_rejected(self):
        rows = "ActiveState=active\nSubState=running\nMainPID=4101\nUser=0\nGroup=0\nControlGroup=/system.slice/ev-n10-owner.service\nDescription=x"
        self.assertEqual(41, self.evaluate(rows, "owner", "ev-n10-owner.service").returncode)

    def test_malformed_property_line_is_rejected(self):
        rows = "ActiveState=active\nSubState=running\nMainPID=4101\nUser=0\nGroup=0\nControlGroup=/system.slice/ev-n10-owner.service\nnot-a-property"
        self.assertEqual(41, self.evaluate(rows, "owner", "ev-n10-owner.service").returncode)

    def test_owner_uid_guard_is_preserved(self):
        rows = "ActiveState=active\nSubState=running\nMainPID=4101\nUser=41\nGroup=0\nControlGroup=/system.slice/ev-n10-owner.service"
        self.assertEqual(41, self.evaluate(rows, "owner", "ev-n10-owner.service").returncode)

    def test_worker_nonroot_guard_is_preserved(self):
        rows = "ActiveState=active\nSubState=running\nMainPID=4102\nUser=0\nGroup=42\nControlGroup=/system.slice/ev-n10-worker.service"
        self.assertEqual(41, self.evaluate(rows, "worker", "ev-n10-worker.service").returncode)

    def test_positive_live_pid_guard_is_preserved(self):
        rows = "ActiveState=active\nSubState=running\nMainPID=0\nUser=41\nGroup=42\nControlGroup=/system.slice/ev-n10-worker.service"
        self.assertEqual(41, self.evaluate(rows, "worker", "ev-n10-worker.service").returncode)

    def test_exact_cgroup_guard_is_preserved(self):
        rows = "ActiveState=active\nSubState=running\nMainPID=4102\nUser=41\nGroup=42\nControlGroup=/system.slice/other.service"
        self.assertEqual(41, self.evaluate(rows, "worker", "ev-n10-worker.service").returncode)

    def test_active_state_guard_is_preserved(self):
        rows = "ActiveState=inactive\nSubState=running\nMainPID=4102\nUser=41\nGroup=42\nControlGroup=/system.slice/ev-n10-worker.service"
        self.assertEqual(41, self.evaluate(rows, "worker", "ev-n10-worker.service").returncode)


class UnitCollisionDiagnosticControls(unittest.TestCase):
    """Runs extracted bounded unit checks against fixed mock systemctl replies."""

    @classmethod
    def setUpClass(cls):
        fixture = (ROOT / "checkpoint-n10-pending-start.sh").read_text()
        begin = "# BEGIN N10 post-run unit-collision diagnostic; data-only, original rejection remains mandatory.\n"
        end = "# END N10 post-run unit-collision diagnostic"
        if fixture.count(begin) != 1 or fixture.count(end) != 1:
            raise AssertionError("unit-collision helper boundaries")
        cls.helper = fixture.split(begin, 1)[1].split(end, 1)[0]
        report_begin = "report_bounded_failure() {\n"
        report_end = "\nbounded() {\n"
        if fixture.count(report_begin) != 1 or fixture.count(report_end) != 1:
            raise AssertionError("bounded failure reporter boundary")
        cls.bounded_reporter = report_begin + fixture.split(report_begin, 1)[1].split(report_end, 1)[0]
        preflight_begin = "# BEGIN N10 preflight unit-collision diagnostic; one bounded query, original rejection preserved.\n"
        preflight_end = "# END N10 preflight unit-collision diagnostic"
        if fixture.count(preflight_begin) != 1 or fixture.count(preflight_end) != 1:
            raise AssertionError("preflight unit-collision helper boundaries")
        cls.preflight = fixture.split(preflight_begin, 1)[1].split(preflight_end, 1)[0]
        runner = ast.parse((ROOT / "run-native.py").read_text())
        cls.runner_ast = runner
        functions = [n for n in runner.body if isinstance(n, ast.FunctionDef)
                     and n.name == "validate_unit_collision_diagnostic"]
        if len(functions) != 1:
            raise AssertionError("unit-collision validator boundary")

        class InvalidRecord(Exception):
            pass

        def require(condition, _code):
            if not condition:
                raise InvalidRecord()

        namespace = {"decode_data": lambda raw: json.loads(raw.decode("utf-8")),
                     "require": require, "Rejected": InvalidRecord}
        exec(compile(ast.Module(body=functions, type_ignores=[]), "<unit-collision-validator>", "exec"), namespace)
        cls.validate = staticmethod(namespace["validate_unit_collision_diagnostic"])

    def invoke(self, state, exit_code=0, *, role="owner", collision_file=False,
               capture_failure=None):
        with tempfile.TemporaryDirectory(prefix="n10-unit-state-") as temp:
            private = Path(temp)
            final = private / "n10-unit-collision.json"
            if collision_file:
                final.write_text("keep-original\n")
            calls = private / "calls"
            harness = r'''try_bounded() { "$@"; }
systemctl() {
  [[ $# == 5 && $1 == show && $2 == "$TEST_UNIT" && $3 == -p && $4 == LoadState && $5 == --value ]] || return 90
  printf 'q\n' >>"$CALLS"
  printf '%s\n' "$MOCK_STATE"
  return "$MOCK_EXIT"
}
fail() { printf 'CHECKPOINT_REJECTED:%s\n' "$1" >&2; exit 77; }
mv() {
  [[ $1 == -nT && $2 == -- ]] || return 72
  [[ ${MOCK_CAPTURE_FAILURE:-} != mv ]] || return 71
  command mv -n -- "$3" "$4"
}
chmod() {
  [[ $# == 3 && $1 == 0600 && $2 == -- ]] || return 73
  [[ ${MOCK_CAPTURE_FAILURE:-} != chmod ]] || return 71
  command chmod "$1" "$3"
}
stat() {
  local format=$2 path=$4
  case "$format" in
    %s) wc -c <"$path" | tr -d ' ' ;;
    %a:%h) [[ -f $path ]] && printf '600:1\n' ;;
    *) return 2 ;;
  esac
}
private=$TEST_PRIVATE
'''
            script = "diagnostic_failure_reported=0\ndiagnostic_stage=n10\n" + self.bounded_reporter + "\n" + harness + self.helper + f"\nunit_absent_post '{role}' \"$TEST_UNIT\"; result=$?; printf 'RETURN:%s\\n' \"$result\"\n"
            env = {"PATH": "/usr/bin:/bin", "LC_ALL": "C", "CALLS": str(calls),
                   "MOCK_STATE": state, "MOCK_EXIT": str(exit_code), "TEST_PRIVATE": str(private),
                   "TEST_UNIT": UNIT,
                   "MOCK_CAPTURE_FAILURE": capture_failure or ""}
            result = subprocess.run(["/bin/bash", "--noprofile", "--norc", "-c", script],
                                    env=env, text=True, capture_output=True, timeout=5, check=False)
            call_count = len(calls.read_text().splitlines()) if calls.exists() else 0
            raw = final.read_bytes() if final.exists() else None
            mode = stat.S_IMODE(final.stat().st_mode) if final.exists() else None
            return result, call_count, raw, mode

    def invoke_preflight(self, state, exit_code=0, *, role="owner", capture_failure=None):
        with tempfile.TemporaryDirectory(prefix="n10-preflight-state-") as temp:
            private = Path(temp)
            final = private / "n10-unit-collision.json"
            calls = private / "calls"
            harness = r'''try_bounded() { "$@"; }
systemctl() {
  [[ $# == 5 && $1 == show && $2 == "$TEST_UNIT" && $3 == -p && $4 == LoadState && $5 == --value ]] || return 90
  printf 'q\n' >>"$CALLS"
  printf '%s\n' "$MOCK_STATE"
  return "$MOCK_EXIT"
}
timeout() {
  [[ $1 == --signal=KILL && $2 == 30 && $3 == systemctl ]] || return 89
  shift 2
  "$@"
}
left() { printf '30\n'; }
remaining() { :; }
report_bounded_failure() { printf 'FIXTURE_BOUNDED_FAILURE:%s\n' "$1" >&2; }
fail() { printf 'CHECKPOINT_REJECTED:%s\n' "$1" >&2; return 77; }
mv() {
  [[ $1 == -nT && $2 == -- ]] || return 72
  [[ ${MOCK_CAPTURE_FAILURE:-} != mv ]] || return 71
  command mv -n -- "$3" "$4"
}
chmod() {
  [[ $# == 3 && $1 == 0600 && $2 == -- ]] || return 73
  [[ ${MOCK_CAPTURE_FAILURE:-} != chmod ]] || return 71
  command chmod "$1" "$3"
}
stat() {
  local format=$2 path=$4
  case "$format" in
    %s) wc -c <"$path" | tr -d ' ' ;;
    %a:%h) [[ -f $path ]] && printf '600:1\n' ;;
    *) return 2 ;;
  esac
}
private=$TEST_PRIVATE
'''
            script = harness + self.helper + self.preflight + (
                f"\nunit_absent '{role}' \"$TEST_UNIT\"; result=$?; "
                "printf 'RETURN:%s\\n' \"$result\"\n")
            env = {"PATH": "/usr/bin:/bin", "LC_ALL": "C", "CALLS": str(calls),
                   "MOCK_STATE": state, "MOCK_EXIT": str(exit_code), "TEST_PRIVATE": str(private),
                   "TEST_UNIT": UNIT,
                   "MOCK_CAPTURE_FAILURE": capture_failure or ""}
            result = subprocess.run(["/bin/bash", "--noprofile", "--norc", "-c", script],
                                    env=env, text=True, capture_output=True, timeout=5, check=False)
            call_count = len(calls.read_text().splitlines()) if calls.exists() else 0
            raw = final.read_bytes() if final.exists() else None
            mode = stat.S_IMODE(final.stat().st_mode) if final.exists() else None
            return result, call_count, raw, mode

    def test_preflight_existing_unit_records_same_loaded_query_then_rejects(self):
        result, calls, raw, mode = self.invoke_preflight("loaded", role="worker")
        self.assertEqual(1, calls)
        self.assertIn("CHECKPOINT_REJECTED:unit-collision\n", result.stderr)
        self.assertNotIn("bounded-operation", result.stderr)
        self.assertEqual(0o600, mode)
        self.assertEqual({"schema": "issue779-n10-unit-collision-v1", "role": "worker",
                          "load_state": "loaded", "query_exit": 0}, json.loads(raw))

    def test_preflight_not_found_succeeds_and_nonzero_query_keeps_bounded_rejection(self):
        absent, calls, raw, _ = self.invoke_preflight("not-found")
        self.assertEqual(1, calls)
        self.assertEqual("RETURN:0\n", absent.stdout)
        self.assertIsNone(raw)
        failed, calls, raw, _ = self.invoke_preflight("loaded", exit_code=5)
        self.assertEqual(1, calls)
        self.assertIn("FIXTURE_BOUNDED_FAILURE:5\n", failed.stderr)
        self.assertIn("CHECKPOINT_REJECTED:bounded-operation\n", failed.stderr)
        self.assertEqual({"schema": "issue779-n10-unit-collision-v1", "role": "owner",
                          "load_state": "loaded", "query_exit": 5}, json.loads(raw))

    def test_preflight_capture_failure_keeps_original_collision_rejection(self):
        result, calls, raw, _ = self.invoke_preflight("loaded", capture_failure="chmod")
        self.assertEqual(1, calls)
        self.assertIsNone(raw)
        self.assertIn("CHECKPOINT_REJECTED:unit-collision\n", result.stderr)

    def test_loaded_owner_and_worker_are_recorded_then_rejected(self):
        for role in ("owner", "worker"):
            with self.subTest(role=role):
                result, calls, raw, mode = self.invoke("loaded", role=role)
                self.assertEqual(1, calls)
                self.assertIn("CHECKPOINT_REJECTED:unit-collision\n", result.stderr)
                self.assertIsNotNone(raw)
                self.assertLessEqual(len(raw), 4096)
                self.assertEqual(0o600, mode)
                self.assertEqual({"schema": "issue779-n10-unit-collision-v1", "role": role,
                                  "load_state": "loaded", "query_exit": 0}, json.loads(raw))

    def test_positive_not_found_remains_success_without_diagnostic(self):
        result, calls, raw, _ = self.invoke("not-found")
        self.assertEqual(1, calls)
        self.assertEqual("RETURN:0\n", result.stdout)
        self.assertIsNone(raw)

    def test_status_zero_wrong_facts_collide_but_nonzero_query_keeps_bounded_class(self):
        wrong, calls, raw, _ = self.invoke("Description=private")
        self.assertEqual(1, calls)
        self.assertEqual("unknown", json.loads(raw)["load_state"])
        self.assertNotIn("private", raw.decode())
        self.assertIn("CHECKPOINT_REJECTED:unit-collision\n", wrong.stderr)
        self.assertNotIn("FIXTURE_BOUNDED_FAILURE:", wrong.stderr)
        failed, calls, raw, _ = self.invoke("loaded", exit_code=5)
        self.assertEqual(1, calls)
        self.assertEqual({"schema": "issue779-n10-unit-collision-v1", "role": "owner",
                          "load_state": "loaded", "query_exit": 5}, json.loads(raw))
        self.assertIn("FIXTURE_BOUNDED_FAILURE:other:systemctl:5\n", failed.stderr)
        self.assertIn("CHECKPOINT_REJECTED:bounded-operation\n", failed.stderr)
        self.assertNotIn("CHECKPOINT_REJECTED:unit-collision\n", failed.stderr)
        capture_failed, calls, raw, _ = self.invoke(
            "loaded", exit_code=5, capture_failure="chmod")
        self.assertEqual(1, calls)
        self.assertIsNone(raw)
        self.assertIn("FIXTURE_BOUNDED_FAILURE:other:systemctl:5\n", capture_failed.stderr)
        self.assertIn("CHECKPOINT_REJECTED:bounded-operation\n", capture_failed.stderr)
        self.assertNotIn("CHECKPOINT_REJECTED:unit-collision\n", capture_failed.stderr)

    def test_capture_failure_never_replaces_original_rejection(self):
        for failure in (None, "chmod", "mv"):
            with self.subTest(failure=failure):
                result, calls, raw, _ = self.invoke("masked", collision_file=failure is None,
                                                    capture_failure=failure)
                self.assertEqual(1, calls)
                if failure is None:
                    self.assertEqual(b"keep-original\n", raw)
                else:
                    self.assertIsNone(raw)
                self.assertIn("CHECKPOINT_REJECTED:unit-collision\n", result.stderr)

    def test_malformed_property_output_is_captured_as_unknown(self):
        for malformed in ("", "not-a-property", "LoadState=loaded", "loaded\nextra"):
            with self.subTest(malformed=malformed):
                result, calls, raw, _ = self.invoke(malformed)
                self.assertEqual(1, calls)
                self.assertIn("CHECKPOINT_REJECTED:unit-collision\n", result.stderr)
                self.assertEqual("unknown", json.loads(raw)["load_state"])
                if malformed:
                    self.assertNotIn(malformed.encode(), raw)

    def test_closed_retained_schema_rejects_unexpected_or_ambiguous_fields(self):
        valid = {"schema": "issue779-n10-unit-collision-v1", "role": "worker",
                 "load_state": "masked", "query_exit": 0}
        for state in ("loaded", "not-found", "error", "masked", "unknown"):
            self.assertEqual(state, self.validate(json.dumps({**valid, "load_state": state}).encode())["load_state"])
        for mutate in (
            lambda x: x.update(unit="raw-name"),
            lambda x: x.update(role="other"),
            lambda x: x.update(load_state="unexpected"),
            lambda x: x.update(query_exit=True),
            lambda x: x.update(query_exit=256),
        ):
            value = dict(valid)
            mutate(value)
            with self.assertRaises(Exception):
                self.validate(json.dumps(value).encode())
        archive_constants = {n.targets[0].id: ast.literal_eval(n.value)
                             for n in self.runner_ast.body if isinstance(n, ast.Assign) and len(n.targets) == 1
                             and isinstance(n.targets[0], ast.Name)
                             and n.targets[0].id in ("ARCHIVE_NAMES", "CAPS")}
        self.assertIn("n10-unit-collision.json", archive_constants["ARCHIVE_NAMES"])
        self.assertEqual(4096, archive_constants["CAPS"]["n10-unit-collision.json"])



"""Portable data/procedure controls; no systemd, root identity, admission or native credit."""
import ast
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).parent
GEN = '11111111222233334444555555555555'
OWNER = 'appsurface-evidence-owner-' + GEN + '.service'
WORKER = 'appsurface-evidence-worker-' + GEN + '.service'

def facts(role='owner'):
    return {'Id': OWNER if role == 'owner' else WORKER, 'LoadState': 'loaded',
            'ActiveState': 'failed' if role == 'owner' else 'inactive',
            'SubState': 'failed' if role == 'owner' else 'dead', 'MainPID': '0',
            'ExecMainPID': '4101' if role == 'owner' else '4102',
            'ExecMainCode': '1' if role == 'owner' else '2',
            'ExecMainStatus': '1' if role == 'owner' else '15',
            'User': '0' if role == 'owner' else '41', 'Group': '0' if role == 'owner' else '42',
            'ControlGroup': '', 'Job': ''}

class TerminalProjectionControls(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        source = (ROOT/'checkpoint-n10-pending-start.sh').read_text()
        begin = '# BEGIN N10 terminal loaded-unit observation; physical group verification remains separate.\n'
        end = '# END N10 terminal loaded-unit observation'
        assert source.count(begin) == source.count(end) == 1
        cls.helper = source.split(begin)[1].split(end)[0]
        cls.source = source

    def invoke(self, rows=None, *, role='owner', load='loaded', query_exit=0, full_exit=0):
        values = facts(role) if rows is None else rows
        text = values if isinstance(values, str) else '\n'.join(k+'='+v for k,v in values.items())
        unit = OWNER if role == 'owner' else WORKER
        with tempfile.TemporaryDirectory(prefix='n10-terminal-') as folder:
            calls = Path(folder)/'calls'
            harness = r'''try_bounded() { "$@"; }
fail() { printf 'REJECTED:%s\n' "$1" >&2; return 71; }
systemctl() {
 printf '%s\n' "$*" >>"$CALLS"
 [[ $1 == show && $2 == "$UNIT" ]] || return 92
 if [[ $# == 5 && $3 == -p && $4 == LoadState && $5 == --value ]]; then
  printf '%s\n' "$LOAD"; return "$QUERY_EXIT"
 fi
 [[ $3 == --all && $# == 27 ]] || return 93
 printf '%s\n' "$ROWS"; return "$FULL_EXIT"
}
'''
            pid, uid, gid = ('4101','0','0') if role == 'owner' else ('4102','41','42')
            script = 'set -euo pipefail\n'+harness+self.helper+f'\nunit_terminal_post {role} "$UNIT" {pid} {uid} {gid}\nprintf "TERMINAL_VERIFIED\\n"\n'
            env = {'PATH':'/usr/bin:/bin','LC_ALL':'C','CALLS':str(calls),'UNIT':unit,
                   'LOAD':load,'ROWS':text,'QUERY_EXIT':str(query_exit),'FULL_EXIT':str(full_exit)}
            completed = subprocess.run(['/bin/bash','--noprofile','--norc','-c',script],
                                       env=env,stdin=subprocess.DEVNULL,capture_output=True,timeout=2)
            return completed, calls.read_text().splitlines()

    def test_loaded_original_failed_owner_is_terminal(self):
        result,calls=self.invoke()
        self.assertEqual(0,result.returncode,result.stderr)
        self.assertEqual(b'TERMINAL_VERIFIED\n',result.stdout)
        self.assertEqual(2,len(calls))

    def test_loaded_signal_terminated_worker_and_exact_group_are_terminal(self):
        rows=facts('worker');rows['ControlGroup']='/system.slice/'+WORKER
        result,calls=self.invoke(rows,role='worker')
        self.assertEqual(0,result.returncode,result.stderr)
        self.assertEqual(2,len(calls))

    def test_unloaded_unit_requires_no_invented_pid_query(self):
        result,calls=self.invoke(load='not-found')
        self.assertEqual(0,result.returncode,result.stderr)
        self.assertEqual(1,len(calls))

    def test_active_unit_or_surviving_main_pid_reject(self):
        for updates in ({'ActiveState':'active','SubState':'running'}, {'MainPID':'4101'},
                        {'ActiveState':'activating','SubState':'start'}, {'SubState':'exited'}):
            with self.subTest(updates=updates):
                rows=facts();rows.update(updates)
                result,_=self.invoke(rows)
                self.assertNotEqual(0,result.returncode)

    def test_foreign_identity_group_or_pending_job_reject(self):
        for key,value in [('Id',WORKER),('ExecMainPID','9999'),('User','41'),('Group','42'),
                          ('ControlGroup','/system.slice/foreign.service'),('Job','17')]:
            with self.subTest(key=key):
                rows=facts();rows[key]=value
                result,_=self.invoke(rows)
                self.assertNotEqual(0,result.returncode)

    def test_missing_duplicate_unknown_and_malformed_properties_reject(self):
        rows=facts();del rows['Job']
        text='\n'.join(k+'='+v for k,v in facts().items())
        for value in [rows,text+'\nId='+OWNER,text+'\nUnknown=canary',text+'\ncanary',text+'\n']:
            with self.subTest(value=value):
                result,_=self.invoke(value)
                self.assertNotEqual(0,result.returncode)

    def test_named_property_order_is_irrelevant(self):
        result,_=self.invoke(dict(reversed(list(facts().items()))))
        self.assertEqual(0,result.returncode,result.stderr)

    def test_owner_zero_exit_wrong_code_or_bad_numeric_bounds_reject(self):
        for key,value in [('ExecMainStatus','0'),('ExecMainStatus','256'),('ExecMainStatus','-1'),
                          ('ExecMainCode','2'),('ExecMainCode','0'),('ExecMainPID','0')]:
            with self.subTest(key=key,value=value):
                rows=facts();rows[key]=value
                result,_=self.invoke(rows)
                self.assertNotEqual(0,result.returncode)

    def test_worker_signal_bounds_reject(self):
        for value in ['0','65','256','-1','canary']:
            with self.subTest(value=value):
                rows=facts('worker');rows['ExecMainStatus']=value
                result,_=self.invoke(rows,role='worker')
                self.assertNotEqual(0,result.returncode)

    def test_query_failure_cannot_be_replaced_by_terminal_payload(self):
        for args,count in [({'query_exit':1},1),({'full_exit':1},2),({'load':'masked'},1)]:
            with self.subTest(args=args):
                result,calls=self.invoke(**args)
                self.assertNotEqual(0,result.returncode)
                self.assertEqual(count,len(calls))

    def test_preflight_absence_and_physical_cgroup_checks_are_preserved(self):
        self.assertEqual(2,self.source.count('unit_absent owner "$owner"; unit_absent worker "$worker"'))
        self.assertIn("grep -qx 'populated 0'",self.source)
        self.assertNotIn('systemctl reset-failed',self.source)
        self.assertNotIn('CollectMode=inactive-or-failed',self.source)
        self.assertIn('quarantined_workspace_retained:$workspace_retained',self.source)

class ResultProjectionControls(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        source=(ROOT/'run-native.py').read_text()
        tree=ast.parse(source)
        nodes=[x for x in tree.body if isinstance(x,ast.FunctionDef) and x.name=='validate_fixture_result']
        def require(ok,category):
            if not ok: raise ValueError(category)
        ns={'require':require,'re':__import__('re'),'SOURCE':'s'*40,'CAPTURE_SHA':'c'*64}
        exec(compile(ast.Module(body=nodes,type_ignores=[]),'<result-validator>','exec'),ns)
        cls.validate=staticmethod(ns['validate_fixture_result'])
    def value(self):
        return {'schema':'issue779-n10-native-fixture-observation-v1','generation':GEN,
            'source_commit':'s'*40,'source_capture_sha256':'c'*64,'root_projection_sha256':'b'*64,
            'root_exit':1,'worker_unit':WORKER,'job_path':'/org/freedesktop/systemd1/job/17',
            'worker_pid':4102,'worker_uid':41,'worker_gid':42,'worker_starttime_ticks':1,
            'root_owner_pid':4101,'root_owner_starttime_ticks':1,'worker_passwd_lookup_exit':0,
            'worker_group_lookup_exit':0,'generated_name_checks':5,'generated_names_present':5,
            'stderr_sha256':'e'*64,'pending_start_failure':'StartCancelled','original_stop_delegate_calls':2,
            'physically_settled':False,'unit_stopped':True,'final_group_sampled_after_pumps':True,
            'final_group_empty':True,'pumps_joined':True,'stdout_eof':True,'stderr_eof':True,'discarded_bytes':0,
            'staging_path_retained':True,'private_path_retained':True,'quarantined_workspace_retained':True,
            'owner_terminal_verified':True,'worker_terminal_verified':True,'root_custody_completed':False,
            'native_authority':False,'native_acceptance':False}
    def test_retained_quarantine_and_terminal_checks_preserve_failure(self):
        self.validate(self.value(),GEN,'b'*64)
    def test_no_success_or_integer_boolean_upgrade(self):
        for key,value in [('owner_terminal_verified',False),('worker_terminal_verified',False),
                          ('owner_terminal_verified',1),('quarantined_workspace_retained',1),
                          ('root_exit',0),('root_custody_completed',True),('physically_settled',True)]:
            with self.subTest(key=key,value=value):
                rows=self.value();rows[key]=value
                with self.assertRaises(ValueError):self.validate(rows,GEN,'b'*64)


if __name__ == "__main__":
    unittest.main()
