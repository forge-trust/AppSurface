"""Pure N09 record-parser controls; vectors are consistency data, never runtime evidence."""
import ast
import hashlib
import re
import os
from pathlib import Path
import json
import shutil
import subprocess
import unittest

import check_n09_records as parser

GENERATION = "0123456789abcdef0123456789abcdef"


def line(value):
    return json.dumps(value, separators=(",", ":"), sort_keys=True).encode("utf-8") + b"\n"


def worker_bytes():
    phase = dict(parser.PHASE_FRAME)
    allocation = {
        "schema": "evidence-allocation-failure-v1", "phase": "BeforeActivation",
        "operation": "Completed", "stageOutcome": "Cancelled", "terminalCode": "CallerCancelled",
        "errorClass": "Cancelled", "nativeErrno": None,
    }
    cancellation = {
        "schema": "issue779-original-cancellation-v1", "case": "N09",
        "phase": "BeforeActivation", "callerTokenCancelled": True,
        "lifecycleCallerCancelled": True, "ownWorkStopped": True, "nativeAuthority": False,
    }
    return line(phase) + line(allocation) + line(cancellation) + parser.TERMINAL


def slot_record():
    parent = {"DeviceMajor": 8, "DeviceMinor": 1, "Inode": 4001,
              "Uid": 1001, "Gid": 1001, "Mode": 0x41c0}
    slot = {"DeviceMajor": 8, "DeviceMinor": 1, "Inode": 4002,
            "Uid": 1001, "Gid": 1001, "Mode": 0x41c0}
    return {
        "schema": "issue779-n09-allocation-slot-observation-v1", "generation": GENERATION,
        "output_parent_before_signal": parent, "output_parent_after_join": parent,
        "allocated_slot_before_signal": slot, "allocated_slot_after_join": slot,
        "slot_empty_before_signal": True, "slot_empty_after_worker_join": True,
        "parent_handle_closed": True, "slot_handle_closed": True, "observation_only": True,
        "native_authority": False, "native_acceptance": False,
    }


def cleanup_record():
    return {
        "schema": "issue779-cancellation-root-cleanup-v1", "generation": GENERATION,
        "results_gid": 1001, "accounts_closed": True, "root_custody_closed": True,
        "original_owners_closed": True, "observation_only": True,
        "native_authority": False, "native_acceptance": False,
    }


class N09RecordParserTests(unittest.TestCase):
    def test_worker_vector_is_consistency_only(self):
        result = parser.check_worker(worker_bytes())
        self.assertEqual(result["case"], "N09")
        self.assertIs(result["native_authority"], False)
        self.assertIs(result["native_acceptance"], False)

    def test_worker_rejects_changed_cancellation_field(self):
        raw = worker_bytes()
        parts = raw.splitlines(keepends=True)
        record = json.loads(parts[2])
        record["callerTokenCancelled"] = False
        parts[2] = line(record)
        with self.assertRaises(parser.Rejected):
            parser.check_worker(b"".join(parts))

    def test_slot_vector_is_consistency_only(self):
        result = parser.check_allocation_slot_record(line(slot_record()), generation=GENERATION)
        self.assertEqual(result["generation"], GENERATION)
        self.assertIs(result["native_authority"], False)
        self.assertIs(result["native_acceptance"], False)

    def test_slot_rejects_authority_claim(self):
        record = slot_record()
        record["native_acceptance"] = True
        with self.assertRaises(parser.Rejected):
            parser.check_allocation_slot_record(line(record), generation=GENERATION)

    def test_cleanup_vector_is_consistency_only(self):
        result = parser.check_root_cleanup_record(line(cleanup_record()), generation=GENERATION)
        self.assertIs(result["native_authority"], False)
        self.assertIs(result["native_acceptance"], False)

    def test_cleanup_rejects_non_boolean_fact(self):
        record = cleanup_record()
        record["accounts_closed"] = 1
        with self.assertRaises(parser.Rejected):
            parser.check_root_cleanup_record(line(record), generation=GENERATION)

    def test_root_projection_requires_closed_seven_line_shape(self):
        raw = (line(slot_record()) + b'{"record":"signal"}\n' + b'{"record":"kernel"}\n'
               + b'{"record":"joined-streams"}\n' + b'{"record":"first-failure"}\n'
               + line(cleanup_record()) + parser.TERMINAL)
        result = parser.split_root_stderr(raw, generation=GENERATION)
        self.assertEqual(len(result["original_root_lines"]), 5)
        self.assertIs(result["native_authority"], False)
        self.assertIs(result["native_acceptance"], False)

    def test_root_projection_rejects_wrong_generation(self):
        raw = (line(slot_record()) + b'{"record":"signal"}\n' + b'{"record":"kernel"}\n'
               + b'{"record":"joined-streams"}\n' + b'{"record":"first-failure"}\n'
               + line(cleanup_record()) + parser.TERMINAL)
        with self.assertRaises(parser.Rejected):
            parser.split_root_stderr(raw, generation="f" * 32)




class _BuildSourceRejected(Exception):
    pass


def _source_require(condition, code):
    if not condition:
        raise _BuildSourceRejected(code)


def _load_build_source_validator():
    runner = Path(__file__).with_name('run-native.py')
    tree = ast.parse(runner.read_text(encoding='utf-8'))
    nodes = [node for node in tree.body if isinstance(node, ast.FunctionDef) and node.name == 'validate_source_facts']
    if len(nodes) != 1:
        raise AssertionError('expected one pure source-fact validator')
    namespace = {'re': re, 'require': _source_require}
    exec(compile(ast.Module(body=nodes, type_ignores=[]), str(runner), 'exec'), namespace)
    return namespace['validate_source_facts'], tree


class SourceDigestBoundaryControls(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.validate, cls.runner_tree = _load_build_source_validator()
        assignments = {
            node.targets[0].id: node.value.value
            for node in cls.runner_tree.body
            if isinstance(node, ast.Assign) and len(node.targets) == 1
            and isinstance(node.targets[0], ast.Name) and isinstance(node.value, ast.Constant)
            and node.targets[0].id in ('SOURCE', 'PARENT', 'SOURCE_MAP', 'BUILD_SOURCE_MAP')
        }
        cls.source = assignments['SOURCE']
        cls.parent = assignments['PARENT']
        # The same field name is used for two different encoders in this
        # installation.  The runner's SOURCE_MAP is the full per-path
        # {sha256, mode} projection emitted by source_check(); the capture
        # validator's SOURCE_MAP is the compact source_sha256/source_modes
        # predicate.  Keep those roles separate instead of treating either
        # digest as a universal source-map hash.
        cls.runner_full_rows = assignments['SOURCE_MAP']
        cls.expected_product = assignments['BUILD_SOURCE_MAP']
        capture_path = Path(__file__).parents[1] / 'issue779-csharp-build' / 'capture-receipt.json'
        capture_bytes = capture_path.read_bytes()
        if hashlib.sha256(capture_bytes).hexdigest() != '653d30169f6fc3aed53274349304dfebc3aff2b529e42bf3a4524a37fa546fd2':
            raise AssertionError('immutable N09 capture pin mismatch')
        cls.capture = json.loads(capture_bytes)
        builder_path = capture_path.with_name('build-fdd.sh')
        builder_text = builder_path.read_text(encoding='utf-8')
        cls.builder_maps = {
            name: re.search(rf"^{name} = '([0-9a-f]{{64}})'$", builder_text, re.MULTILINE).group(1)
            for name in ('SOURCE_MAP', 'BUILD_SOURCE_MAP')
        }
        cls.builder_capture_map = cls.builder_maps['SOURCE_MAP']
        cls.builder_product_map = cls.builder_maps['BUILD_SOURCE_MAP']
        cls.transport_maps = {}
        for filename in ('acquire-inbound.sh', 'prepare-root-inputs-v2.sh'):
            script = Path(__file__).with_name(filename).read_text(encoding='utf-8')
            cls.transport_maps[filename] = {
                name: re.search(rf'^readonly {name}=([0-9a-f]{{64}})$', script, re.MULTILINE).group(1)
                for name in ('SOURCE_MAP', 'BUILD_SOURCE_MAP')
            }
        cls.sha_rows = cls.capture['source']['sha256']
        cls.mode_rows = cls.capture['source']['modes']
        cls.count = len(cls.sha_rows)
        cls.full_rows_digest = cls.full_rows_digest(cls.sha_rows, cls.mode_rows)
        cls.capture_canonical = cls.capture_digest(cls.sha_rows, cls.mode_rows)
        cls.product = cls.product_digest(cls.sha_rows, cls.mode_rows)

    @staticmethod
    def product_digest(sha_rows, mode_rows):
        source = {'sha256': sha_rows, 'modes': mode_rows}
        return hashlib.sha256(json.dumps(source, sort_keys=True, separators=(',', ':')).encode()).hexdigest()

    @staticmethod
    def full_rows_digest(sha_rows, mode_rows):
        rows = {path: {'sha256': sha_rows[path], 'mode': mode_rows[path]} for path in sorted(sha_rows)}
        return hashlib.sha256(json.dumps(rows, sort_keys=True, separators=(',', ':')).encode()).hexdigest()

    @staticmethod
    def capture_digest(sha_rows, mode_rows):
        canonical = {'source_sha256': sha_rows, 'source_modes': mode_rows}
        return hashlib.sha256(json.dumps(canonical, sort_keys=True, separators=(',', ':')).encode()).hexdigest()

    def fact(self, **updates):
        value = {'head': self.source, 'count': self.count, 'source_map_sha256': self.product,
                 'index_tree_matches': True, 'physical_git_sha1': True, 'physical_sha256_modes': True}
        value.update(updates)
        return value

    def test_full_pinned_capture_derives_each_distinct_source_digest_role(self):
        self.assertEqual(self.count, 2865)
        self.assertEqual(self.capture['validation_commit'], self.source)
        self.assertEqual(self.capture['previous_validation_commit'], self.parent)
        self.assertEqual(self.runner_full_rows, self.full_rows_digest)
        self.assertEqual(self.runner_full_rows, 'af4bee87511df5cc07f1505cb84e8607f4ca10dea74bf5830e23128622bd0dfd')
        self.assertEqual(self.capture_canonical, 'a3dbb4a7be588b3e5234bf728e4eda4032f5ee8acab1d2ab7213cceef106586e')
        self.assertEqual(self.product, '07e4be7c1c59989a70d88c345ff360e68a0a394a6729877afa7981d6d913d238')
        self.assertEqual(self.builder_capture_map, self.capture_canonical)
        self.assertEqual(self.builder_product_map, self.product)
        self.assertTrue(all(maps['SOURCE_MAP'] == self.capture_canonical for maps in self.transport_maps.values()))
        self.assertTrue(all(maps['BUILD_SOURCE_MAP'] == self.product for maps in self.transport_maps.values()))
        self.assertEqual(self.expected_product, self.product)
        self.assertEqual(len({self.runner_full_rows, self.builder_capture_map, self.product}), 3)
        self.assertIsNone(type(self).validate(self.fact(), self.count, self.source, self.expected_product))

    def test_capture_and_full_row_digests_are_rejected_as_builder_product(self):
        for digest in (self.capture_canonical, self.runner_full_rows, '0' * 64):
            with self.subTest(digest=digest[:8]), self.assertRaises(_BuildSourceRejected):
                type(self).validate(self.fact(source_map_sha256=digest), self.count, self.source, self.expected_product)

    def test_file_hash_and_mode_mutations_change_the_full_product_projection(self):
        changed_sha = dict(self.sha_rows)
        changed_sha[next(iter(changed_sha))] = hashlib.sha256(b'changed-file-bytes').hexdigest()
        changed_modes = dict(self.mode_rows)
        path = next(iter(changed_modes))
        changed_modes[path] = '0755' if changed_modes[path] != '0755' else '0644'
        for sha_rows, mode_rows in ((changed_sha, self.mode_rows), (self.sha_rows, changed_modes)):
            digest = self.product_digest(sha_rows, mode_rows)
            self.assertNotEqual(digest, self.product)
            with self.assertRaises(_BuildSourceRejected):
                type(self).validate(self.fact(source_map_sha256=digest), self.count, self.source, self.expected_product)

    def test_head_count_and_integrity_flags_require_exact_types_and_values(self):
        bad_facts = (
            (self.fact(count=True), self.count, self.source, self.expected_product),
            (self.fact(), True, self.source, self.expected_product),
            (self.fact(), self.count, '2' * 40, self.expected_product),
            (self.fact(), self.count, 'bad', self.expected_product),
            (self.fact(), self.count, self.source, 'bad'),
        )
        for fact, count, source, digest in bad_facts:
            with self.assertRaises(_BuildSourceRejected):
                type(self).validate(fact, count, source, digest)
        for name in ('index_tree_matches', 'physical_git_sha1', 'physical_sha256_modes'):
            for bad in (False, 1, None):
                with self.subTest(name=name, value=bad), self.assertRaises(_BuildSourceRejected):
                    type(self).validate(self.fact(**{name: bad}), self.count, self.source, self.expected_product)
            missing = self.fact()
            del missing[name]
            with self.assertRaises(_BuildSourceRejected):
                type(self).validate(missing, self.count, self.source, self.expected_product)


class N09RetainedObservationControls(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        import importlib.util
        runner_path = Path(__file__).with_name('run-native.py')
        spec = importlib.util.spec_from_file_location('n09_data_runner', runner_path)
        cls.runner = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(cls.runner)

    @staticmethod
    def observation():
        return {
            'schema': 'issue779-n09-retained-slot-observation-v1', 'case': 'N09',
            'generation': GENERATION, 'source_revision': '0c100a94d93f82fd633e5674009a41659940d010',
            'base_revision': '4dd992ec1bc2df8220c73149115c5b478edb0085',
            'allocation_record_sha256': 'a' * 64, 'cleanup_record_sha256': 'b' * 64,
            'results_gid': 12001, 'account_ids_absent': True, 'worker_cgroup_settled': True,
            'original_projection_verified': True,
            'path_comparison': {'schema':'issue779-n09-post-settlement-path-comparison-v1',
                'parent_identity_matches':True, 'slot_identity_matches':True, 'slot_empty':True,
                'native_authority':False, 'native_acceptance':False},
            'observation_only':True, 'native_authority':False, 'native_acceptance':False,
        }

    @classmethod
    def retained_archive(cls, *, claimed_digest=None, base_revision=None):
        import io, tarfile
        runner = cls.runner
        observation_raw = json.dumps(cls.observation(), sort_keys=True, separators=(',', ':')).encode()
        fixture = {'schema':'issue779-n09-retained-slot-fixture-v1', 'generation':GENERATION,
            'case':'N09', 'source_revision':runner.SOURCE,
            'base_revision':base_revision or runner.N09_BASE_REVISION,
            'observation_sha256':claimed_digest or hashlib.sha256(observation_raw).hexdigest(),
            'root_launch_exit':1, 'fixture_processes_joined':True,
            'fresh_generated_groups_empty':True, 'account_disposition':'closed-and-nss-absent',
            'publication':False, 'native_acceptance':False, 'trusted':False}
        fixture_raw=json.dumps(fixture,sort_keys=True,separators=(',',':')).encode()
        rows={'fixture-result.json':fixture_raw, 'n09-observation.json':observation_raw}
        data_bytes=sum(map(len,rows.values()))
        selection={'schema':'issue779-native-retention-selection-v1','selection_status':'one-namespace',
            'data_file_count':2,'data_bytes':data_bytes,'missing_fixed_file_count':0}
        rows['retention-selection.json']=(json.dumps(selection,sort_keys=True,separators=(',',':'))+'\n').encode()
        archive=io.BytesIO()
        with tarfile.open(fileobj=archive,mode='w:',format=tarfile.USTAR_FORMAT) as tar:
            for name,content in sorted(rows.items()):
                info=tarfile.TarInfo(name); info.size=len(content); info.mode=0o600
                info.uid=info.gid=info.mtime=0; info.uname=info.gname=''
                tar.addfile(info,io.BytesIO(content))
        return archive.getvalue()

    def test_retained_archive_binds_observation_bytes_and_fixed_base(self):
        values=self.runner.inspect_archive(self.retained_archive())
        self.runner.validate_native_result(values['fixture-result.json'])
        self.assertTrue(self.runner.validate_n09_observation(values,values['fixture-result.json']))
        self.assertIn('n09-observation.json',values['_retained_raw'])

    def test_retained_observation_digest_mismatch_rejects(self):
        values=self.runner.inspect_archive(self.retained_archive(claimed_digest='0'*64))
        with self.assertRaises(self.runner.Rejected):
            self.runner.validate_n09_observation(values,values['fixture-result.json'])

    def test_well_formed_but_wrong_fixture_base_revision_rejects(self):
        values=self.runner.inspect_archive(self.retained_archive(base_revision='5'*40))
        with self.assertRaises(self.runner.Rejected):
            self.runner.validate_native_result(values['fixture-result.json'])


class N09RootRecordBindingControls(unittest.TestCase):
    """Exercise actual archive cross-validation using detached record data only."""
    setUpClass = N09RetainedObservationControls.__dict__['setUpClass']
    observation = staticmethod(N09RetainedObservationControls.observation)

    @classmethod
    def root_records(cls, field=None):
        slot = line(slot_record())
        cleanup = line(cleanup_record())
        # The inner parser validates the slot/cleanup records; intermediate records
        # are consumed by the independent original cancellation parser in production.
        raw = slot + b'{}\n' * 4 + cleanup + parser.TERMINAL
        observation = cls.observation()
        observation.update(allocation_record_sha256=hashlib.sha256(slot).hexdigest(),
                           cleanup_record_sha256=hashlib.sha256(cleanup).hexdigest(),
                           results_gid=cleanup_record()['results_gid'])
        if field is not None:
            observation[field] = (observation[field] + 1 if field == 'results_gid' else 'f' * 64)
        observation_raw = line(observation)
        fixture = {'schema':'issue779-n09-retained-slot-fixture-v1','generation':GENERATION,
            'case':'N09','source_revision':cls.runner.SOURCE,'base_revision':cls.runner.N09_BASE_REVISION,
            'observation_sha256':hashlib.sha256(observation_raw).hexdigest(),'root_launch_exit':1,
            'fixture_processes_joined':True,'fresh_generated_groups_empty':True,
            'account_disposition':'closed-and-nss-absent','publication':False,'native_acceptance':False,'trusted':False}
        return {'fixture-result.json':fixture,'n09-observation.json':observation,
                '_retained_raw':{'logs/n01.stderr':raw,'n09-observation.json':observation_raw}}

    def validate_root_records(self, values):
        original = self.runner.n09_record_parser
        try:
            self.runner.n09_record_parser = parser
            return self.runner.validate_negative_archive(values)
        finally:
            self.runner.n09_record_parser = original

    def test_exact_retained_hashes_and_gid_pass_consistency_without_authority(self):
        result = self.validate_root_records(self.root_records())
        self.assertTrue(result['n09_seven_line_projection_valid'])
        self.assertEqual(cleanup_record()['results_gid'],result['results_gid'])
        self.assertIs(result['native_authority'],False)
        self.assertIs(result['native_acceptance'],False)

    def test_wellformed_allocation_hash_mismatch_rejects(self):
        with self.assertRaises(self.runner.Rejected) as error:
            self.validate_root_records(self.root_records('allocation_record_sha256'))
        self.assertEqual('n09-observation-record-binding',error.exception.category)

    def test_wellformed_cleanup_hash_mismatch_rejects(self):
        with self.assertRaises(self.runner.Rejected) as error:
            self.validate_root_records(self.root_records('cleanup_record_sha256'))
        self.assertEqual('n09-observation-record-binding',error.exception.category)

    def test_positive_but_wrong_results_gid_rejects(self):
        with self.assertRaises(self.runner.Rejected) as error:
            self.validate_root_records(self.root_records('results_gid'))
        self.assertEqual('n09-observation-record-binding',error.exception.category)


class BootstrapNameGuardTests(unittest.TestCase):
    """Exercise only the embedded fixed bootstrap basename predicate."""
    @classmethod
    def setUpClass(cls):
        runner_path = Path(__file__).with_name("run-native.py")
        tree = ast.parse(runner_path.read_text(encoding="utf-8"))
        values = {}
        for node in tree.body:
            if isinstance(node, ast.Assign) and len(node.targets) == 1 and isinstance(node.targets[0], ast.Name):
                if node.targets[0].id in {"BOOTSTRAP", "PINS"}:
                    values[node.targets[0].id] = ast.literal_eval(node.value)
        cls.bootstrap = values["BOOTSTRAP"]
        cls.pins = values["PINS"]
        guards = [line.strip() for line in cls.bootstrap.splitlines()
                  if "$name =~" in line and "&& $hash =~" in line]
        if len(guards) != 1:
            raise AssertionError("bootstrap-name-guard-shape")
        cls.name_guard = guards[0].split("&& $hash", 1)[0].rstrip() + " ]]"
        cls.bash = shutil.which("bash")
        if cls.bash is None:
            raise AssertionError("bash-unavailable")
        cls.command = ("set -euo pipefail; name=$N09_TEST_NAME; hash=" + "0" * 64 +
                       "; source=/dev/null; " + cls.name_guard)

    def accepts(self, name):
        env = os.environ.copy()
        env["N09_TEST_NAME"] = name
        result = subprocess.run(
            [self.bash, "--noprofile", "--norc", "-c", self.command],
            env=env, stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL, timeout=2, check=False,
        )
        return result.returncode == 0

    def test_all_eleven_fixed_bootstrap_names_are_accepted(self):
        self.assertEqual(11, len(self.pins))
        for name in self.pins:
            with self.subTest(name=name):
                self.assertTrue(self.accepts(name))

    def test_invalid_basename_forms_are_rejected(self):
        for name in ("", ".", "..", "../outside", "a/../b", "sub/name", "line\nbreak"):
            with self.subTest(name=name):
                self.assertFalse(self.accepts(name))

    def test_nul_cannot_enter_the_bootstrap_name_environment(self):
        with self.assertRaises(ValueError):
            self.accepts("bad\x00name")


if __name__ == '__main__':
    unittest.main()
