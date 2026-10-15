"""Pure parser checks for the builder's physical-harness index/tree seam."""
import ast
import hashlib
import json
import pathlib
import re
import subprocess
import tempfile
import unittest
from pathlib import Path

ROOT = pathlib.Path(__file__).parent
CAPTURE_PATH = ROOT.parent / 'issue779-csharp-build' / 'capture-receipt.json'
source = (ROOT.parent / 'issue779-csharp-build' / 'build-fdd.sh').read_text()
body = source.split("<<'PY'\n", 1)[1].rsplit('\nPY', 1)[0]
tree = ast.parse(body)
names = {
    'BuildFailure', 'require', 'relative_name', '_git_path',
    'parse_index_rows', 'parse_tree_rows', 'validate_harness_inventory',
    'parse_name_status_rows', 'validate_github_run_sha', 'harness_installation_check',
}
selected = [node for node in tree.body
            if isinstance(node, (ast.FunctionDef, ast.ClassDef)) and node.name in names]
namespace = {'pathlib': pathlib, 're': re, 'stat': __import__('stat'), 'os': __import__('os')}
exec(compile(ast.Module(body=selected, type_ignores=[]), '<data-only-harness-parser>', 'exec'), namespace)
parse_index = namespace['parse_index_rows']
parse_tree = namespace['parse_tree_rows']
validate = namespace['validate_harness_inventory']
parse_delta = namespace['parse_name_status_rows']
validate_run_sha = namespace['validate_github_run_sha']
Rejected = namespace['BuildFailure']

OID_A = b'0123456789abcdef0123456789abcdef01234567'
OID_B = b'1123456789abcdef0123456789abcdef01234567'


def index_rows(entries):
    return b''.join(mode + b' ' + oid + b' ' + stage + b'\t' + name + b'\0'
                    for mode, oid, stage, name in entries)


def tree_rows(entries):
    return b''.join(mode + b' ' + kind + b' ' + oid + b'\t' + name + b'\0'
                    for mode, kind, oid, name in entries)


class HarnessInventoryParserControls(unittest.TestCase):
    def test_small_real_git_record_shapes_and_physical_map(self):
        index = parse_index(index_rows([
            (b'100644', OID_A, b'0', b'README.md'),
            (b'100755', OID_B, b'0', b'.github/run.sh'),
        ]))
        tree = parse_tree(tree_rows([
            (b'100644', b'blob', OID_A, b'README.md'),
            (b'100755', b'blob', OID_B, b'.github/run.sh'),
        ]))
        physical = {
            'README.md': {'mode': '0644', 'git_sha1': OID_A.decode()},
            '.github/run.sh': {'mode': '0755', 'git_sha1': OID_B.decode()},
        }
        self.assertTrue(validate(index, tree, physical, 2))

    def test_index_mode_and_blob_id_are_closed(self):
        with self.assertRaises(Rejected):
            parse_index(index_rows([(b'120000', OID_A, b'0', b'link')]))
        with self.assertRaises(Rejected):
            parse_index(index_rows([(b'100644', b'z' * 40, b'0', b'file')]))

    def test_tree_rejects_gitlinks_and_nonblob_objects(self):
        with self.assertRaises(Rejected):
            parse_tree(tree_rows([(b'160000', b'commit', OID_A, b'submodule')]))
        with self.assertRaises(Rejected):
            parse_tree(tree_rows([(b'100644', b'tree', OID_A, b'not-a-blob')]))

    def test_nonzero_stage_and_duplicate_or_aliased_paths_rejected(self):
        with self.assertRaises(Rejected):
            parse_index(index_rows([(b'100644', OID_A, b'1', b'file')]))
        with self.assertRaises(Rejected):
            parse_index(index_rows([(b'100644', OID_A, b'0', b'file'),
                                    (b'100644', OID_B, b'0', b'file')]))
        with self.assertRaises(Rejected):
            parse_tree(tree_rows([(b'100644', b'blob', OID_A, b'./file')]))

    def test_missing_or_extra_physical_paths_rejected(self):
        index = parse_index(index_rows([(b'100644', OID_A, b'0', b'one')]))
        tree = parse_tree(tree_rows([(b'100644', b'blob', OID_A, b'one')]))
        with self.assertRaises(Rejected):
            validate(index, tree, {'one': {'mode': '0644', 'git_sha1': OID_A.decode()}}, 2)
        with self.assertRaises(Rejected):
            validate(index, tree, {'one': {'mode': '0644', 'git_sha1': OID_A.decode()},
                                   'extra': {'mode': '0644', 'git_sha1': OID_B.decode()}}, 1)

    def test_physical_mode_and_blob_must_match_index_and_tree(self):
        index = parse_index(index_rows([(b'100644', OID_A, b'0', b'one')]))
        tree = parse_tree(tree_rows([(b'100644', b'blob', OID_A, b'one')]))
        for facts in [{'mode': '0755', 'git_sha1': OID_A.decode()},
                      {'mode': '0644', 'git_sha1': OID_B.decode()}]:
            with self.subTest(facts=facts), self.assertRaises(Rejected):
                validate(index, tree, {'one': facts}, 1)
        wrong_tree = parse_tree(tree_rows([(b'100755', b'blob', OID_A, b'one')]))
        with self.assertRaises(Rejected):
            validate(index, wrong_tree, {'one': {'mode': '0644', 'git_sha1': OID_A.decode()}}, 1)

    def test_exact_added_path_status_records(self):
        self.assertEqual(parse_delta(b'A\0a\0A\0b/c\0'), {'a': 'A', 'b/c': 'A'})
        with self.assertRaises(Rejected):
            parse_delta(b'M\0a\0')
        with self.assertRaises(Rejected):
            parse_delta(b'A\0a\0A\0a\0')

    def test_github_run_sha_is_strict_lowercase_sha1(self):
        self.assertEqual(validate_run_sha('a' * 40), 'a' * 40)
        for value in ('', 'a' * 39, 'A' * 40, 'g' * 40, 'a' * 41):
            with self.subTest(value=value), self.assertRaises(Rejected):
                validate_run_sha(value)

    def test_actual_builder_calls_full_parser_and_physical_reader(self):
        fn = next(node for node in tree.body if isinstance(node, ast.FunctionDef)
                  and node.name == 'harness_installation_check')
        calls = {node.func.id for node in ast.walk(fn)
                 if isinstance(node, ast.Call) and isinstance(node.func, ast.Name)}
        self.assertTrue({'parse_index_rows', 'parse_tree_rows', 'validate_harness_inventory',
                         'validate_github_run_sha', 'regular', 'run', 'check'} <= calls)
        self.assertRegex(source, r"GITHUB_SHA',\s*''")
        self.assertIn("RESULT['harness_installation'] = harness", source)

    def test_exact_22_added_paths_and_upstream_gates_remain_pending(self):
        assignment = next(node for node in tree.body if isinstance(node, ast.Assign)
                          and any(isinstance(target, ast.Name) and target.id == 'INSTALLATION_DELTA'
                                  for target in node.targets))
        paths = ast.literal_eval(assignment.value.generators[0].iter)
        self.assertEqual(len(paths), 22)
        self.assertEqual(len(set(paths)), 22)
        self.assertNotIn('INSTALLATION_INPUT_SHA256', source)
        runner_sha = hashlib.sha256((ROOT / 'run-native.py').read_bytes()).hexdigest()
        runtime_text = (ROOT / 'prepare-ubuntu-runtime.py').read_text()
        self.assertEqual(re.search(r"NATIVE_RUNNER_SHA = '([0-9a-f]{64})'", source).group(1), runner_sha)
        self.assertEqual(re.search(r"RUNNER_SHA = '([0-9a-f]{64})'", runtime_text).group(1), runner_sha)
        runtime_sha = hashlib.sha256((ROOT / 'prepare-ubuntu-runtime.py').read_bytes()).hexdigest()
        self.assertEqual(re.search(r"UBUNTU_PREREQUISITE_SHA = '([0-9a-f]{64})'", source).group(1), runtime_sha)



class OwnerDeathInstalledDeltaControls(unittest.TestCase):
    def test_exact_22_path_delta_and_full_checkout_count(self):
        assignment = next(n for n in tree.body if isinstance(n, ast.Assign) and any(isinstance(t, ast.Name) and t.id == 'INSTALLATION_DELTA' for t in n.targets))
        paths = ast.literal_eval(assignment.value.generators[0].iter)
        self.assertEqual(len(paths), 22)
        self.assertEqual(len(set(paths)), 22)
        self.assertEqual(2872 + len(paths), 2891)


class CommonSourceCaptureControls(unittest.TestCase):
    """Exercise the current v4 source map and the exact root-facing jq predicates as data only."""

    @staticmethod
    def query_from(script_name):
        text = (Path(__file__).parent / script_name).read_text()
        match = re.search(r"capture_query='\n(.*?)'\nbounded /usr/bin/jq -e", text, re.DOTALL)
        if match is None:
            raise AssertionError(f'capture jq binding not found in {script_name}')
        return match.group(1)

    def test_capture_binds_raw_and_compact_source_maps(self):
        raw = CAPTURE_PATH.read_bytes()
        capture = json.loads(raw)
        self.assertEqual(hashlib.sha256(raw).hexdigest(), runner.CAPTURE_SHA)
        self.assertTrue(runner.validate_capture(capture))
        self.assertEqual(capture['schema'], 'issue779-csharp-common-source-capture-v4')
        self.assertEqual(capture['source_count'], 2872)
        source = capture['source']
        full = {name: {'sha256': source['sha256'][name], 'mode': source['modes'][name]}
                for name in sorted(source['sha256'])}
        self.assertEqual(hashlib.sha256(json.dumps(full, sort_keys=True, separators=(',', ':')).encode()).hexdigest(),
                         runner.SOURCE_MAP)
        self.assertEqual(hashlib.sha256(json.dumps(source, sort_keys=True, separators=(',', ':')).encode()).hexdigest(),
                         runner.CANONICAL_SOURCE_MAP)

    def test_exact_shell_jq_accepts_capture_and_rejects_nested_map_drift(self):
        capture = json.loads(CAPTURE_PATH.read_text())
        scripts = ('prepare-root-inputs-v2.sh',)
        bad_cases = []
        bad = json.loads(json.dumps(capture)); bad['source_count'] += 1; bad_cases.append(bad)
        bad = json.loads(json.dumps(capture)); del bad['source']['modes'][next(iter(bad['source']['modes']))]; bad_cases.append(bad)
        bad = json.loads(json.dumps(capture)); bad['source']['modes'][next(iter(bad['source']['modes']))] = '0640'; bad_cases.append(bad)
        bad = json.loads(json.dumps(capture)); bad['source']['sha256'][next(iter(bad['source']['sha256']))] = 'x' * 64; bad_cases.append(bad)
        bad = json.loads(json.dumps(capture)); bad['clean'] = False; bad_cases.append(bad)
        bad = json.loads(json.dumps(capture)); bad['native_credit'] = True; bad_cases.append(bad)
        with tempfile.TemporaryDirectory() as temp:
            temp_path = Path(temp)
            acquisition_text = (Path(__file__).parent / 'acquire-inbound.sh').read_text()
            self.assertIn('capture=${v[reviewed-root]}/source-capture.json', acquisition_text)
            self.assertIn('pin "$capture" "$CAPTURE" 4194304', acquisition_text)
            for script_name in scripts:
                query = self.query_from(script_name)
                script_text = (Path(__file__).parent / script_name).read_text()
                self.assertIn('"$capture_query" "${values[source-capture]}" >/dev/null', script_text)
                args = ['/usr/bin/jq', '-e', '--arg', 'h', runner.SOURCE,
                        '--arg', 'p', runner.PARENT, '--arg', 't', '3bafd0fcd5321f1b29323f46df94b77eb48fa859',
                        '--argjson', 'n', '2872', query]
                accepted = subprocess.run(args + [str(CAPTURE_PATH)], stdout=subprocess.DEVNULL,
                                          stderr=subprocess.PIPE, check=False)
                self.assertEqual(accepted.returncode, 0, script_name)
                for index, malformed in enumerate(bad_cases):
                    candidate = temp_path / f'{script_name.replace("/", "_")}-{index}.json'
                    candidate.write_text(json.dumps(malformed, separators=(',', ':')))
                    rejected = subprocess.run(args + [str(candidate)], stdout=subprocess.DEVNULL,
                                              stderr=subprocess.PIPE, check=False)
                    self.assertNotEqual(rejected.returncode, 0, f'{script_name} malformed case {index}')


"""Additional pure-data coverage for current six-command and source-capture receipt contracts."""
import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest

RUNNER_PATH = Path(__file__).with_name('run-native.py')
CAPTURE_PATH = Path(__file__).parents[1] / 'issue779-csharp-build' / 'capture-receipt.json'
spec = importlib.util.spec_from_file_location('owner_death_runner_data', RUNNER_PATH)
runner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runner)


def build_commands(case):
    operations = ('restore', 'publish', 'restore', 'test', 'restore', 'test')
    result = []
    for ordinal, operation in enumerate(operations):
        result.append({
            'ordinal': ordinal,
            'argv': ['dotnet', operation, '-p:EvidencePrivateQualification=' + case],
            'exit': 0,
            'failure': None,
            'waited': True,
            'group_absent': True,
            'forced_cleanup': False,
            'timed_out': False,
            'logs': [{'sha256': 'a' * 64}, {'sha256': 'b' * 64}],
        })
    return result


class RunnerDataTests(unittest.TestCase):
    def test_actual_capture_schema_and_identity(self):
        raw = CAPTURE_PATH.read_bytes()
        self.assertEqual(runner.sha(raw), runner.CAPTURE_SHA)
        capture = json.loads(raw)
        self.assertTrue(runner.validate_capture(capture))
        self.assertEqual(capture['schema'], 'issue779-csharp-common-source-capture-v4')

    def test_capture_wrong_source_count_rejected(self):
        capture = json.loads(CAPTURE_PATH.read_text())
        capture['source_count'] += 1
        with self.assertRaises(runner.Rejected):
            runner.validate_capture(capture)

    def test_actual_builder_sequence_and_case_selectors(self):
        for case in ('N13', 'N14'):
            self.assertTrue(runner.validate_private_build_commands(build_commands(case), case))

    def test_wrong_case_selector_rejected(self):
        commands = build_commands('N13')
        with self.assertRaises(runner.Rejected):
            runner.validate_private_build_commands(commands, 'N14')


if __name__ == '__main__':
    unittest.main(verbosity=2)

    def test_duplicate_selector_rejected(self):
        commands = build_commands('N13')
        commands[0]['argv'].append('-p:EvidencePrivateQualification=N13')
        with self.assertRaises(runner.Rejected):
            runner.validate_private_build_commands(commands, 'N13')

    def test_timeout_and_late_completion_rejected(self):
        for field, value in (('timed_out', True), ('late_completion', True)):
            commands = build_commands('N13')
            commands[0][field] = value
            with self.subTest(field=field), self.assertRaises(runner.Rejected):
                runner.validate_private_build_commands(commands, 'N13')

    def test_output_requires_real_lf(self):
        record = {'phase_line': 'OWNER_DEATH_PHASE:N13:complete', 'pipes': {
            'stdout_sha256': runner.sha(b'OWNER_DEATH_PHASE:N13:complete\n'),
            'stderr_sha256': runner.sha(b'')
        }}
        values = {
            'fixture-stdout.log': b'OWNER_DEATH_MEASURED:N13:QUARANTINED:NOT_NATIVE_ACCEPTANCE\n',
            'fixture-stderr.log': b'',
            'root.stdout': b'OWNER_DEATH_PHASE:N13:complete\n',
            'root.stderr': b'',
        }
        self.assertIsNone(runner.validate_captured_lines(values, 'N13', record))
        values['root.stdout'] = b'OWNER_DEATH_PHASE:N13:complete\\n'
        with self.assertRaises(runner.Rejected):
            runner.validate_captured_lines(values, 'N13', record)


class WorkflowSourceContractControls(unittest.TestCase):
    """Pure source/literal coherence checks; do not execute the workflow or native fixture."""

    def test_workflow_pins_operands_artifact_paths_and_duration_contract(self):
        workflow_path = ROOT.parent / 'workflows' / 'issue779-csharp-linux-native.yml'
        workflow = workflow_path.read_text()
        rows = re.findall(r"\('([^']+)', '([A-Z0-9_]+)', ([0-9]+)\)", workflow)
        values = dict(re.findall(r"^\s+([A-Z0-9_]+): ([0-9a-f]{64})$", workflow, re.MULTILINE))
        self.assertEqual(len(rows), 18)
        self.assertEqual(len({path for path, _, _ in rows}), 18)
        self.assertEqual(len({variable for _, variable, _ in rows}), 18)
        repo = ROOT.parent.parent
        for rel, variable, cap_text in rows:
            path = repo / rel
            content = path.read_bytes()
            self.assertGreater(len(content), 0)
            self.assertLessEqual(len(content), int(cap_text))
            self.assertEqual(hashlib.sha256(content).hexdigest(), values[variable], rel)

        n13 = workflow.split('- name: N13 build then owner-death case', 1)[1].split(
            '- name: N14 build then owner-death case', 1)[0]
        n14 = workflow.split('- name: N14 build then owner-death case', 1)[1].split(
            '- name: Retain bounded receipts and private records', 1)[0]
        for block, case in ((n13, 'N13'), (n14, 'N14')):
            self.assertIn('base="$RUNNER_TEMP/owner-death"', block)
            self.assertIn(f'--build-root "$base/build-{case.lower()}"', block)
            self.assertIn(f'--output "$base/run-{case.lower()}"', block)
            self.assertIn('--reviewed-script-sha256 "$ORCHESTRATOR_SHA256"', block)
            self.assertNotIn('$8f8cea', block)
        retention = workflow.split('- name: Retain bounded receipts and private records', 1)[1]
        self.assertIn('path: ${{ runner.temp }}/owner-death/', retention)

        builder_text = (ROOT.parent / 'issue779-csharp-build' / 'build-fdd.sh').read_text()
        builder_match = re.search(r'duration_policy_seconds=(\{[^}]+\})', builder_text, re.DOTALL)
        self.assertIsNotNone(builder_match)
        self.assertEqual(ast.literal_eval(builder_match.group(1)), {
            'original_request': 240, 'owner_runtime': 240,
            'timeout_stop': 5, 'n14_owner_bound': 245})

        runner_text = (ROOT / 'run-native.py').read_text()
        ast.parse(runner_text)
        runner_match = re.search(r"receipt.get\('duration_policy_seconds'\)==(\{[^\n]+\})", runner_text)
        self.assertIsNotNone(runner_match)
        self.assertEqual(ast.literal_eval(runner_match.group(1)), {
            'original_request': 240, 'owner_runtime': 240,
            'timeout_stop': 5, 'n14_owner_bound': 245})
        runtime_text = (ROOT / 'prepare-ubuntu-runtime.py').read_text()
        runner_sha = hashlib.sha256((ROOT / 'run-native.py').read_bytes()).hexdigest()
        self.assertEqual(re.search(r"RUNNER_SHA = '([0-9a-f]{64})'", runtime_text).group(1), runner_sha)
        self.assertEqual(re.search(r"NATIVE_RUNNER_SHA = '([0-9a-f]{64})'", builder_text).group(1), runner_sha)

        fixture = (ROOT / 'checkpoint-owner-death-common.sh').read_text()
        self.assertIn('ORIGINAL_JOB_SECONDS=240 FIXTURE_SECONDS=600 CLEANUP_RESERVE=30', fixture)
        self.assertIn('--property=RuntimeMaxSec=240s', fixture)
        self.assertIn('--property=TimeoutStopSec=5s', fixture)
        parser_tree = ast.parse((ROOT / 'check_owner_death_record.py').read_text())
        runtime_values = next(ast.literal_eval(node.value) for node in ast.walk(parser_tree)
                              if isinstance(node, ast.Assign)
                              and any(isinstance(target, ast.Name) and target.id == 'runtime_values'
                                      for target in node.targets))
        self.assertEqual(runtime_values, {'4min': 240000000, '240000000': 240000000})
        self.assertIn('<= 245', (ROOT / 'check_owner_death_record.py').read_text())


if __name__ == '__main__':
    unittest.main()
