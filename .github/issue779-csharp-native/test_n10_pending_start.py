#!/usr/bin/env python3
"""Data-only N10 parser controls; never creates a lease, worker, signal, or native result."""
import ast
import hashlib
import re
import importlib.util
import json
from pathlib import Path
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


if __name__ == "__main__":
    unittest.main()
