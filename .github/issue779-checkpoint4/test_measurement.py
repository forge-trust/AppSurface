"""Private measurement wiring data only; fabricated byte samples are never native coverage."""
from pathlib import Path
import hashlib
import importlib.util
import json
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

    def test_both_collectors_select_the_same_six_modules_and_only_one_existing_driver(self):
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


if __name__ == '__main__':
    unittest.main()
