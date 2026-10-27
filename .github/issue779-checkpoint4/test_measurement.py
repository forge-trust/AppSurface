"""Private measurement wiring data only; fabricated byte samples are never native coverage."""
from pathlib import Path
import ast
import hashlib
import importlib.util
import json
import re
import tempfile
import unittest
from unittest.mock import patch
import measurement_arguments as arguments

spec = importlib.util.spec_from_file_location('measured_application', Path(__file__).with_name('run-measured-application.py'))
wrapper = importlib.util.module_from_spec(spec)
spec.loader.exec_module(wrapper)


class ArgumentControls(unittest.TestCase):
    def inputs(self):
        return ['/evidence', '/tool', '/sdk-tool', '/runtime', '/prepared', '/workspace', 'a' * 40, '/collector/coverlet.dll']

    def test_both_collectors_select_the_same_seven_modules_and_only_one_existing_driver(self):
        self.assertEqual((
            'ForgeTrust.AppSurface.Cli', 'ForgeTrust.AppSurface.Evidence.Contracts',
            'ForgeTrust.AppSurface.Evidence.Planner', 'ForgeTrust.AppSurface.Evidence.Coverage',
            'ForgeTrust.AppSurface.Evidence.Aspire', 'ForgeTrust.AppSurface.Evidence.Supervision',
            'ForgeTrust.AppSurface.Evidence.Cli',
        ), arguments.MODULES)
        values = arguments.build(*self.inputs())
        self.assertEqual('/sdk-tool/ForgeTrust.AppSurface.Cli.dll', values[1])
        self.assertEqual('/usr/bin/python3', values[values.index('--target') + 1])
        self.assertEqual(['[' + module + ']*' for module in arguments.MODULES],
                         values[values.index('--include') + 1:values.index('--include-test-assembly')])
        target = values[values.index('--targetargs') + 1]
        self.assertEqual(1, target.count('run-measured-application.py'))
        self.assertNotIn('--case', target)
        self.assertIn('--sdk-tool /sdk-tool', target)
        self.assertIn('--tool /tool', target)
        self.assertEqual('/evidence/native-sdk', values[-1])

    def test_quote_grammar_preserves_nested_quotes_and_backslashes_without_a_shell(self):
        self.assertEqual('plain "space path" say\\"hello a\\b',
                         arguments.arguments(['plain', 'space path', 'say"hello', 'a\\b']))
        for values in ([''], ['line\nbreak'], ['nul\0'], [True], ['x' * 65537]):
            with self.subTest(values=type(values[0])):
                with self.assertRaises(ValueError):
                    arguments.arguments(values)

    def test_whole_image_workflows_select_the_same_closed_modules_and_original_leaves(self):
        workflows = Path(__file__).resolve().parents[1] / 'workflows'
        expected = ['[' + module + ']*' for module in arguments.MODULES]
        for filename, count in (('issue779-checkpoint3-application.yml', 2),
                                ('issue779-checkpoint4-accounts.yml', 1)):
            text = (workflows / filename).read_text()
            rows = [row for row in text.splitlines() if "--include '[ForgeTrust.AppSurface.Cli]*'" in row]
            with self.subTest(workflow=filename):
                self.assertEqual(count, len(rows))
                for row in rows:
                    self.assertEqual(expected, re.findall(r"'([^']+)'", row))
        text = (workflows / 'issue779-checkpoint3-application.yml').read_text()
        row = next(row.strip() for row in text.splitlines() if row.strip().startswith('names = tuple('))
        expression = ast.parse(row).body[0].value.args[0]
        self.assertEqual(arguments.MODULES, ast.literal_eval(expression.generators[0].iter))

    def test_unclosed_inputs_reject_before_a_command_can_be_constructed(self):
        for index, value in ((0, 'relative'), (1, '/tool\ncanary'), (2, '/sdk"canary'),
                             (3, '/runtime\\canary'), (6, 'g' * 40), (6, 'a' * 39)):
            values = self.inputs()
            values[index] = value
            with self.subTest(index=index, value=value):
                with self.assertRaises(ValueError):
                    arguments.build(*values)


class ImageBindingControls(unittest.TestCase):
    def exercise(self, mutation=None):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            tool, sdk = root / 'tool', root / 'sdk'
            tool.mkdir()
            sdk.mkdir()
            names = [module + suffix for module in arguments.MODULES for suffix in ('.dll', '.pdb')]
            original = {'source': 'a' * 40, 'files': {}, 'sdk_files': {}}
            for name in names:
                for key, image in (('files', tool), ('sdk_files', sdk)):
                    before = (key + name + '-before').encode()
                    original[key][name] = hashlib.sha256(before).hexdigest()
                    (image / name).write_bytes(before + b'-instrumented-data-sample')
            if mutation == 'unchanged-sdk':
                name = names[0]
                (sdk / name).write_bytes(('sdk_files' + name + '-before').encode())
            if mutation == 'missing-sdk':
                original['sdk_files'].pop(names[0])
            if mutation == 'extra-module':
                original['files']['unexpected.dll'] = 'a' * 64
            if mutation and mutation.startswith('shared:'):
                _, operation, key, suffix = mutation.split(':')
                name = 'ForgeTrust.AppSurface.Evidence.Cli.' + suffix
                if operation == 'missing':
                    del original[key][name]
                elif operation == 'alias':
                    original[key]['other.dll'] = original[key].pop(name)
                else:
                    image = tool if key == 'files' else sdk
                    (image / name).write_bytes((key + name + '-before').encode())
            capture = root / 'original.json'
            capture.write_text(json.dumps(original))
            binding = root / 'binding.json'
            argv = ['run-measured-application.py', '--binding', str(binding), '--original', str(capture),
                    '--', '--tool', str(tool), '--sdk-tool', str(sdk), '--source', 'a' * 40]
            with patch.object(wrapper.sys, 'argv', argv), patch.object(wrapper.os, 'geteuid', return_value=0), \
                    patch.object(wrapper.runpy, 'run_path') as run:
                if mutation:
                    with self.assertRaises(ValueError):
                        wrapper.main()
                    run.assert_not_called()
                    self.assertFalse(binding.exists())
                else:
                    wrapper.main()
                    run.assert_called_once()
                    row = json.loads(binding.read_text())
                    self.assertEqual(14, len(row['instrumented']))
                    self.assertEqual(14, len(row['sdk_instrumented']))
                    self.assertEqual(set(names), set(row['sdk_instrumented']))
                    self.assertEqual(original['sdk_files'], row['sdk_original'])
                    for name in names:
                        self.assertEqual(hashlib.sha256((sdk / name).read_bytes()).hexdigest(), row['sdk_instrumented'][name])

    def test_separate_image_hashes_are_captured_before_the_real_driver_is_dispatched(self):
        self.exercise()

    def test_unchanged_missing_or_extra_sdk_inputs_cannot_dispatch_the_driver(self):
        for mutation in ('unchanged-sdk', 'missing-sdk', 'extra-module'):
            with self.subTest(mutation=mutation):
                self.exercise(mutation)

    def test_shared_cli_inputs_must_exist_in_both_images_and_be_instrumented(self):
        for key in ('files', 'sdk_files'):
            for operation, suffix in (('missing', 'dll'), ('missing', 'pdb'),
                                      ('alias', 'dll'), ('unchanged', 'dll')):
                mutation = 'shared:' + operation + ':' + key + ':' + suffix
                with self.subTest(image=key, operation=operation, suffix=suffix):
                    self.exercise(mutation)


class OrdinaryBuildMeasurementIntegrationControls(unittest.TestCase):
    """Source-owned workflow dependency and retained-input contracts, never native evidence."""

    @staticmethod
    def job(text, name):
        match = re.search(r'^  ' + re.escape(name) + r':\n.*?(?=^  [A-Za-z0-9_-]+:\n|\Z)',
                          text, re.MULTILINE | re.DOTALL)
        if match is None:
            raise AssertionError('missing-fixed-measurement-job:' + name)
        return match.group(0)

    def test_ordinary_build_joins_the_same_original_native_measurement_chain(self):
        workflows = Path(__file__).resolve().parents[1] / 'workflows'
        current = (workflows / 'build.yml').read_text()
        runtime = (workflows / 'evidencehost-runtime-proof.yml').read_text()
        selectors = ('none', *(f'n{index:02d}' for index in range(4, 17)))
        shared = ('packaged-producer', 'worker-controls', 'account-creation', 'account-rollback',
                  *(f'worker-measurement-{selector}' for selector in selectors))
        for name in shared:
            with self.subTest(job=name):
                expected = self.job(runtime, name).replace('needs: application-consumers',
                                                          'needs: evidence-native-measurement')
                self.assertEqual(expected, self.job(current, name))
        build = self.job(current, 'build')
        line = next(row.strip() for row in build.splitlines() if row.strip().startswith('needs:'))
        required = {'worker-controls', 'evidence-native-measurement', 'account-creation',
                    'account-rollback', 'packaged-producer', 'coverage-security-platform',
                    'durable-postgresql-compatibility',
                    *(f'worker-measurement-{selector}' for selector in selectors)}
        self.assertEqual(required, set(part.strip() for part in line.removeprefix('needs: [').removesuffix(']').split(',')))
        self.assertIn('/usr/bin/bash ./scripts/coverage-solution.sh', build)
        # The broker uses a private base environment. The fixed env command must carry
        # fan-in inputs and same-run provenance into the nonroot coverage process.
        for name in ('COVERAGE_FANIN_NATIVE', 'GITHUB_REPOSITORY', 'GITHUB_RUN_ID', 'GITHUB_RUN_ATTEMPT'):
            self.assertIn(name + '="$' + name + '"', build)
        self.assertIn("COVERAGE_GATE_DIFF_BASE: ${{ github.event_name == 'pull_request' && 'HEAD^1' || (github.ref != 'refs/heads/main' && 'origin/main' || '') }}", build)

    def test_ordinary_build_stages_all_fixed_validator_inputs_before_enabling_the_test(self):
        workflows = Path(__file__).resolve().parents[1] / 'workflows'
        current = self.job((workflows / 'build.yml').read_text(), 'build')
        runtime = self.job((workflows / 'evidencehost-runtime-proof.yml').read_text(), 'solution-coverage')
        first = '      - name: Download current native measurement from the successful application lane\n'
        last = "          printf 'EVIDENCEHOST_NATIVE_MEASUREMENT=1\\n' >> \"$GITHUB_ENV\"\n"
        def staging(text):
            start = text.index(first)
            end = text.index(last, start) + len(last)
            return text[start:end]
        self.assertEqual(staging(runtime), staging(current))
        section = staging(current)
        for leaf in ('native-sdk.json', 'native-sdk-failure.json', 'native/sdk-test-failure.json',
                     'root-file-tests/peer-credentials.trx', 'root-supplement-tests/root-supplement.trx',
                     'application-bundle-tests/application-bundle.trx', 'native-accounts-receipt.json',
                     'native-producer-roots.json', 'native-producer-roots-c2.json'):
            with self.subTest(input=leaf):
                self.assertIn(leaf, section)
        self.assertIn('for variant in creation rollback', section)
        self.assertIn('from measurement_chain import validate_final', section)
        self.assertIn("chain_data['predecessor']['report_sha256'] == prior_sha", section)
        self.assertLess(section.index('validate_final(source_raw'), section.index('EVIDENCEHOST_NATIVE_MEASUREMENT=1'))


if __name__ == '__main__':
    unittest.main()
