"""Detached receipt grammar and ordinary owned-file tests; no native or execution proof."""
import argparse
import copy
import os
from pathlib import Path
import tempfile
import time
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET

import binding
import measurement_data_tests as subject


def model(selector='N07'):
    """Synthetic data-validator fixture, never evidence of a process or passing native case."""
    args = argparse.Namespace(source='a' * 40, selector=selector, workflow_identity='github-actions-1-1',
                              evidence=Path('/evidence'))
    original = {'source_root': '/source', 'source_capture_sha256': 'b' * 64,
                'commands': [{'argv': ['/sdk/dotnet']}], 'tool': {'files': {}}}
    raws = {}
    files = {}
    for name in subject.NAMES:
        raw = ('original-' + name).encode()
        files[name] = subject.projection(raw)
        original['tool']['files'][name] = {'sha256': binding.digest(raw)}
        raws['data-tests/originals/' + name] = raw
    commands = []
    for index, argv in enumerate(subject.argv(args, original)):
        phase = ('data-restore', 'data-build', 'data-run')[index]
        log = (phase + ' closed log').encode()
        raws['data-tests/' + phase + '.log'] = log
        commands.append({'phase': phase, 'argv': argv, 'exit': 0, 'timed_out': False, 'joined': True,
                         'log_path': '/evidence/data-tests/' + phase + '.log',
                         'log_sha256': binding.digest(log), 'log_bytes': len(log)})
    prepared = {'schema': 'issue779-c1-data-prepared-v1', 'source': args.source, 'selector': selector,
                'workflow_identity': args.workflow_identity, 'source_capture_sha256': 'b' * 64,
                'original_build_sha256': binding.digest(binding.canonical(original)), 'sdk': '/sdk/dotnet',
                'test_binary': str(subject.paths(args, original)[3]),
                'classes': [subject.PREFIX + name for name in subject.CLASSES[selector]],
                'original_files': files, 'commands': commands[:2],
                'native_acceptance': False, 'coverage_credit': False}
    root = ET.Element('TestRun', xmlns='http://microsoft.com/schemas/VisualStudio/TeamTest/2010')
    results = ET.SubElement(root, 'Results')
    definitions = ET.SubElement(root, 'TestDefinitions')
    for index, name in enumerate(prepared['classes']):
        key = 'test-' + str(index)
        ET.SubElement(results, 'UnitTestResult', outcome='Passed', testId=key)
        ET.SubElement(ET.SubElement(definitions, 'UnitTest', id=key), 'TestMethod', className=name)
    count = str(len(prepared['classes']))
    ET.SubElement(ET.SubElement(root, 'ResultSummary'), 'Counters', total=count, executed=count, passed=count,
                  failed='0', error='0', notExecuted='0')
    raw = ET.tostring(root)
    raws['data-tests/trx/data.trx'] = raw
    raws['data-tests/prepared.json'] = binding.canonical(prepared)
    result = {'schema': 'issue779-c1-data-result-v1',
              'prepared_sha256': binding.digest(raws['data-tests/prepared.json']),
              'instrumented': {name: subject.projection(('instrumented-' + name).encode()) for name in subject.NAMES},
              'restored': files, 'commands': commands[2:],
              'trx': {**subject.projection(raw), 'counts': subject.trx(raw, prepared['classes'])},
              'native_acceptance': False, 'coverage_credit': False}
    raws['data-tests/result.json'] = binding.canonical(result)
    return args, original, prepared, raws


class SdkDataDirectoryControls(unittest.TestCase):
    """SDK environment and real temporary-file controls; no SDK or native case is fabricated."""

    def test_each_sdk_phase_uses_private_absolute_data_outside_the_checkout(self):
        with tempfile.TemporaryDirectory() as directory:
            parent = Path(directory).resolve()
            source = parent / 'source'
            evidence = parent / 'evidence'
            source.mkdir()
            evidence.mkdir()
            args, original, _, _ = model('N04')
            args.evidence = evidence
            original['source_root'] = str(source)
            seen = []

            def execute(argv, phase, log, root, environment, deadline):
                state = Path(environment['XDG_DATA_HOME'])
                self.assertTrue(state.is_absolute())
                self.assertEqual(0o700, state.stat().st_mode & 0o777)
                self.assertEqual(os.geteuid(), state.stat().st_uid)
                self.assertFalse(binding.overlaps(root, state))
                self.assertFalse(binding.overlaps(evidence, state))
                self.assertEqual('/nonexistent', environment['HOME'])
                self.assertEqual(subject.argv(args, original)[len(seen)], argv)
                self.assertEqual(440, deadline)
                # Model NuGet's migration write through its selected data root.
                marker = state / 'NuGet/Migrations/1'
                marker.parent.mkdir(parents=True)
                marker.write_bytes(b'')
                seen.append(state)
                return {'exit': 0, 'joined': True, 'timed_out': False}

            with patch.dict(os.environ, {'HOME': '/nonexistent', 'XDG_DATA_HOME': 'relative-unselected'}), \
                    patch.object(subject.time, 'monotonic', return_value=400), \
                    patch.object(subject.publish, 'command', side_effect=execute):
                for index in range(3):
                    # The final command keeps the original 60-second reserve;
                    # earlier phases have only their original 10-second reserve.
                    original_deadline = 500 if index == 2 else 450
                    subject.command(args, original, index, original_deadline)
                    self.assertFalse(seen[-1].exists())
                self.assertEqual('relative-unselected', os.environ['XDG_DATA_HOME'])
            self.assertEqual(3, len(set(seen)))
            self.assertFalse((source / '.local').exists())
            self.assertEqual([], list(source.iterdir()))

    def test_sdk_failure_still_removes_its_owned_temporary_directory(self):
        with tempfile.TemporaryDirectory() as directory:
            parent = Path(directory).resolve()
            args, original, _, _ = model('N04')
            args.evidence = parent / 'evidence'
            args.evidence.mkdir()
            original['source_root'] = str(parent / 'source')
            (parent / 'source').mkdir()
            seen = []

            def fail(*argv):
                state = Path(argv[4]['XDG_DATA_HOME'])
                seen.append(state)
                (state / 'owned-state').write_bytes(b'private-canary')
                raise RuntimeError('sdk-command-failed')

            with patch.object(subject.publish, 'command', side_effect=fail):
                with self.assertRaisesRegex(RuntimeError, '^sdk-command-failed$'):
                    subject.command(args, original, 2, time.monotonic() + 300)
            self.assertEqual(1, len(seen))
            self.assertFalse(seen[0].exists())
            self.assertEqual([], list((parent / 'source').iterdir()))

    def test_source_parent_rejects_before_creating_sdk_state_or_dispatching(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory).resolve()
            args, original, _, _ = model('N04')
            args.evidence = root / 'evidence'
            args.evidence.mkdir()
            original['source_root'] = str(root)
            with patch.object(subject.tempfile, 'TemporaryDirectory', side_effect=AssertionError('create')), \
                    patch.object(subject.publish, 'command', side_effect=AssertionError('dispatch')):
                with self.assertRaises(binding.Rejected):
                    subject.command(args, original, 2, time.monotonic() + 300)
            self.assertEqual(['evidence'], [path.name for path in root.iterdir()])


class QualifiedSelectionControls(unittest.TestCase):
    """Closed command/receipt binding only; these controls execute no SDK or native case."""

    NEW_RECIPES = {
        'N05': 'LinuxSlotNegativeObservationTests',
        'N08': 'LinuxJoinedWorkerOutputDiagnosticTests',
        'N10': 'LinuxN10PendingStartCheckpointTests',
        'N12': 'LinuxN12DescendantTests',
        'N13': 'LinuxOwnerDeathPhaseFrameTests',
        'N14': 'LinuxOwnerDeathPhaseFrameTests',
    }

    def test_new_recipes_bind_matching_build_properties_and_exact_vstest_classes(self):
        for selector, name in self.NEW_RECIPES.items():
            with self.subTest(selector=selector):
                args, original, prepared, _ = model(selector)
                restore, build, run = subject.argv(args, original)
                selected = '-p:EvidencePrivateQualification=' + selector
                for command, verb in ((restore, 'restore'), (build, 'build')):
                    self.assertEqual(['/sdk/dotnet', verb], command[:2])
                    self.assertEqual([selected], [value for value in command
                                                 if value.startswith('-p:EvidencePrivateQualification=')])
                expected = subject.PREFIX + name
                expected_classes = [expected] + ([subject.PREFIX + 'SupervisionAcceptedWorkFramingTests'] if selector == 'N13' else [])
                self.assertEqual(expected_classes, prepared['classes'])
                self.assertEqual(['/sdk/dotnet', 'vstest', prepared['test_binary']], run[:3])
                self.assertEqual(['--TestCaseFilter:' + '|'.join('FullyQualifiedName~' + value for value in expected_classes)],
                                 [value for value in run if value.startswith('--TestCaseFilter:')])
                self.assertFalse(any(value.startswith('-p:') for value in run))

    def test_new_recipes_reject_foreign_prepared_and_trx_classes(self):
        foreign = subject.PREFIX + 'LinuxN04CheckpointOrderTests'
        for selector, name in self.NEW_RECIPES.items():
            with self.subTest(selector=selector):
                args, original, prepared, raws = model(selector)
                changed = copy.deepcopy(prepared)
                changed['classes'] = [foreign]
                with self.assertRaises(binding.Rejected):
                    subject.require_prepared(args, original, changed)
                actual = raws['data-tests/trx/data.trx']
                replaced = actual.replace((subject.PREFIX + name).encode(), foreign.encode())
                self.assertNotEqual(actual, replaced)
                with self.assertRaises(binding.Rejected):
                    subject.trx(replaced, prepared['classes'])

    def test_accepted_work_framing_uses_the_real_selected_runtime_without_adding_native_cases(self):
        args, original, prepared, raws = model('N13')
        expected = [subject.PREFIX + name for name in
                    ('LinuxOwnerDeathPhaseFrameTests', 'SupervisionAcceptedWorkFramingTests')]
        self.assertEqual(expected, prepared['classes'])
        self.assertEqual('--TestCaseFilter:' + '|'.join('FullyQualifiedName~' + name for name in expected),
                         subject.argv(args, original)[2][3])
        self.assertEqual(2, subject.trx(raws['data-tests/trx/data.trx'], expected)['passed'])
        self.assertFalse(prepared['native_acceptance'])
        self.assertFalse(prepared['coverage_credit'])

    def test_other_five_selectors_return_before_paths_reads_copies_or_commands(self):
        for selector in ('None', 'N06', 'N09', 'N11', 'N15'):
            args = argparse.Namespace(selector=selector)
            with self.subTest(selector=selector), \
                    patch.object(subject, 'paths', side_effect=AssertionError('paths')), \
                    patch.object(subject, 'read', side_effect=AssertionError('read')), \
                    patch.object(subject, 'replace_owned', side_effect=AssertionError('copy')), \
                    patch.object(subject, 'command', side_effect=AssertionError('dispatch')):
                self.assertIsNone(subject.prepare(args, {}, b'{}', deadline=0))
                self.assertIsNone(subject.run(args, {}, {}, deadline=0))


class DataMeasurementControls(unittest.TestCase):
    def test_each_fixed_recipe_accepts_closed_data_without_any_os_read_or_dispatch(self):
        for selector in subject.CLASSES:
            args, original, prepared, raws = model(selector)
            with self.subTest(selector=selector), patch.object(subject, 'read', side_effect=AssertionError('read')), \
                    patch.object(subject.publish, 'command', side_effect=AssertionError('dispatch')):
                result = subject.validate(raws, args, original, prepared, deadline=time.monotonic() + 5)
                self.assertFalse(result['native_acceptance'])
                self.assertFalse(result['coverage_credit'])

    def test_one_changed_receipt_field_or_actual_leaf_rejects(self):
        for fault in ('missing-log', 'extra-leaf', 'log-bytes', 'exit-bool', 'unjoined', 'authority',
                      'unmodified-image', 'trx-hash', 'unknown-field'):
            args, original, prepared, raws = model()
            result = binding.decode(raws['data-tests/result.json'])
            if fault == 'missing-log': del raws['data-tests/data-run.log']
            elif fault == 'extra-leaf': raws['data-tests/foreign'] = b'private-canary'
            elif fault == 'log-bytes': raws['data-tests/data-run.log'] += b'x'
            elif fault == 'exit-bool': result['commands'][0]['exit'] = False
            elif fault == 'unjoined': result['commands'][0]['joined'] = False
            elif fault == 'authority': result['native_acceptance'] = True
            elif fault == 'unmodified-image': result['instrumented'][subject.NAMES[0]] = prepared['original_files'][subject.NAMES[0]]
            elif fault == 'trx-hash': result['trx']['sha256'] = '0' * 64
            else: result['private-canary'] = True
            raws['data-tests/result.json'] = binding.canonical(result)
            with self.subTest(fault=fault), self.assertRaises(binding.Rejected) as caught:
                subject.validate(raws, args, original, prepared, deadline=time.monotonic() + 5)
            self.assertNotIn('private-canary', str(caught.exception))

    def test_prepared_path_source_class_and_backup_changes_reject_before_run(self):
        for key, value in [('source', 'c' * 40), ('sdk', '/other/dotnet'), ('test_binary', '/other/test.dll'),
                           ('classes', []), ('coverage_credit', True)]:
            args, original, prepared, _ = model()
            prepared[key] = value
            with self.subTest(key=key), self.assertRaises(binding.Rejected):
                subject.require_prepared(args, original, prepared)

    def test_trx_cannot_invent_success_classes_or_expand_entities(self):
        args, original, prepared, raws = model()
        raw = raws['data-tests/trx/data.trx']
        for bad in (raw.replace(b'outcome="Passed"', b'outcome="Failed"', 1),
                    raw.replace(prepared['classes'][0].encode(), b'Unselected.NativeOwner'),
                    b'<!DOCTYPE x [<!ENTITY e SYSTEM "file:///private-canary">]>' + raw,
                    b'<TestRun/>', b'\xff'):
            with self.subTest(length=len(bad)), self.assertRaises(binding.Rejected):
                subject.trx(bad, prepared['classes'])

    def test_trx_results_must_join_exact_selected_definitions_by_unique_id(self):
        _, _, prepared, raws = model()
        raw = raws['data-tests/trx/data.trx']
        for bad in (raw.replace(b'testId="test-0"', b'testId="unrelated"', 1),
                    raw.replace(b'testId="test-1"', b'testId="test-0"', 1),
                    raw.replace(b'id="test-1"', b'id="test-0"', 1),
                    raw.replace(b' testId="test-0"', b'', 1)):
            with self.subTest(length=len(bad)), self.assertRaises(binding.Rejected):
                subject.trx(bad, prepared['classes'])

    def test_none_recipe_has_no_supplemental_io(self):
        args = argparse.Namespace(selector='None')
        with patch.object(subject, 'read', side_effect=AssertionError('read')), \
                patch.object(subject, 'command', side_effect=AssertionError('dispatch')):
            self.assertIsNone(subject.prepare(args, {}, b'{}', deadline=0))
            self.assertIsNone(subject.run(args, {}, {}, deadline=0))

    def test_replacement_registers_original_inode_before_mutation_and_restores_it(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory).resolve() / 'runtime.dll'
            path.write_bytes(b'original')
            pins = []
            subject.replace_owned(path, b'original', b'measured', time.monotonic() + 5,
                                  on_write=lambda pin: (self.assertEqual(b'original', path.read_bytes()), pins.append(pin)))
            self.assertEqual(b'measured', path.read_bytes())
            subject.replace_owned(path, b'measured', b'original', time.monotonic() + 5, expected_pin=pins[0])
            self.assertEqual(b'original', path.read_bytes())

    def test_wrong_bytes_hardlink_and_symlink_never_register_a_write(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory).resolve()
            path = root / 'runtime.dll'
            path.write_bytes(b'original')
            with patch.object(subject.os, 'write', side_effect=AssertionError('write')):
                for fault in ('bytes', 'hardlink', 'symlink'):
                    if fault == 'hardlink': os.link(path, root / 'alias')
                    if fault == 'symlink':
                        (root / 'link').symlink_to(path)
                    selected = root / 'link' if fault == 'symlink' else path
                    with self.subTest(fault=fault), self.assertRaises(binding.Rejected):
                        subject.replace_owned(selected, b'wrong' if fault == 'bytes' else b'original',
                                              b'measured', time.monotonic() + 5,
                                              on_write=lambda pin: self.fail('registered'))
                    if fault == 'hardlink': (root / 'alias').unlink()
            self.assertEqual(b'original', path.read_bytes())

    def test_expired_copy_does_not_open_or_register_anything(self):
        with patch.object(subject.os, 'open', side_effect=AssertionError('open')):
            with self.assertRaises(binding.Rejected):
                subject.replace_owned(Path('/unselected'), b'', b'', time.monotonic() - 1)

    def test_validation_never_closes_its_file_before_selecting_the_write_inode(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory).resolve()
            path = root / 'runtime.dll'
            foreign = root / 'foreign'
            path.write_bytes(b'original')
            foreign.write_bytes(b'foreign sentinel')
            original_id = (path.stat().st_dev, path.stat().st_ino)
            real_close, real_write = os.close, os.write
            wrote = False

            def close(fd):
                nonlocal wrote
                value = os.fstat(fd)
                selected = (value.st_dev, value.st_ino) == original_id
                real_close(fd)
                # This is the old reader-close -> pathname-stat gap. A retained
                # read/write FD cannot expose that gap before its owned write.
                if selected and not wrote:
                    path.rename(root / 'original-held')
                    foreign.rename(path)

            def write(fd, raw):
                nonlocal wrote
                value = os.fstat(fd)
                self.assertEqual(original_id, (value.st_dev, value.st_ino))
                wrote = True
                return real_write(fd, raw)

            with patch.object(subject.os, 'close', side_effect=close), patch.object(subject.os, 'write', side_effect=write):
                subject.replace_owned(path, b'original', b'measured', time.monotonic() + 5)
            self.assertEqual(b'measured', path.read_bytes())
            self.assertEqual(b'foreign sentinel', foreign.read_bytes())


if __name__ == '__main__':
    unittest.main()
