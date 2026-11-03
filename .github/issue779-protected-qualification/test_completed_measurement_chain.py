"""Bounded archive-selection controls for completed C1 measurement data."""
from __future__ import annotations

import hashlib
import io
import json
from pathlib import Path
import os
import stat
import sys
import tempfile
import time
import types
import unittest
import zipfile
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / '.github/issue779-protected-qualification'))
import completed_c1
import completed_measurement_chain as subject
import completed_run
import completed_validation


def _archive(members):
    output = io.BytesIO()
    with zipfile.ZipFile(output, 'w', compression=zipfile.ZIP_STORED) as archive:
        for name, body in members.items():
            info = zipfile.ZipInfo(name)
            info.create_system = 3
            mode = stat.S_IFDIR | 0o700 if name.endswith('/') else stat.S_IFREG | 0o600
            info.external_attr = mode << 16
            archive.writestr(info, body)
    return output.getvalue()


def _projection(raw, name='issue779-c1-measured-None-99-1'):
    rows, expanded = completed_validation._zip_directory(raw)
    return completed_validation.ArchiveProjection(
        1, name, hashlib.sha256(raw).hexdigest(), len(raw), expanded,
        tuple(completed_validation.MemberProjection(row[0], row[3], row[4], row[6]) for row in rows),
        hashlib.sha256(b'capture').hexdigest())


def _parsed_projection(raw, name, artifact_id, source_digest):
    rows, expanded = completed_validation._zip_directory(raw)
    return completed_validation.ArchiveProjection(
        artifact_id, name, hashlib.sha256(raw).hexdigest(), len(raw), expanded,
        tuple(completed_validation.MemberProjection(row[0], row[3], row[4], row[6]) for row in rows),
        source_digest)


class CompletedMeasurementChainTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name).resolve()
        self.staging = self.root / 'staging'
        self.staging.mkdir(mode=0o700)
        os.chmod(self.staging, 0o700)

    def tearDown(self):
        self.temp.cleanup()

    def test_fixed_measurement_order_and_run_attempt_names(self):
        names = subject._measured_names(40483696166, 1)
        self.assertEqual(14, len(names))
        self.assertEqual(subject.ORDER, tuple(name.split('-')[3] for name in names))
        self.assertEqual('issue779-c1-measured-None-40483696166-1', names[0])
        self.assertEqual('issue779-c1-measured-N16-40483696166-1', names[-1])

    def test_full_projection_is_checked_but_only_source_and_chain_payload_are_materialized(self):
        capture = b'capture'
        raw = _archive({'source.json': capture, 'chain/': b'', 'chain/source.json': capture,
                        'chain/coverage.json': b'unchanged-report', 'chain/nested/': b'',
                        'chain/nested/data.json': b'chain-member',
                        'outside/large-leaf.bin': b'not-selected'})
        projection = _projection(raw)
        binding = type('Binding', (), {'size': len(raw), 'sha256': hashlib.sha256(raw).hexdigest(),
                                       'name': projection.artifact_name})()
        rows, expanded = subject._projection_matches(raw, binding, projection)
        self.assertEqual(projection.expanded_bytes, expanded)
        owned = {}
        owned, root_identity, extracted = subject._extract_chain(
            raw, rows, self.staging / 'segment', capture, time.monotonic() + 5, owned)
        self.assertEqual(b'unchanged-report', (self.staging / 'segment/coverage.json').read_bytes())
        self.assertEqual(capture, (self.staging / 'segment/source.json').read_bytes())
        self.assertFalse((self.staging / 'segment/chain').exists())
        self.assertLess(extracted, expanded)
        self.assertEqual(root_identity, owned[()])
        self.assertTrue(completed_c1._cleanup_invocation(self.staging, self.staging / 'segment',
                                                         root_identity, owned))
        self.assertEqual([], list(self.staging.iterdir()))

    def test_archive_projection_mismatch_rejects_before_extraction(self):
        capture = b'capture'
        raw = _archive({'source.json': capture, 'chain/source.json': capture, 'chain/coverage.json': b'report'})
        projection = _projection(raw)
        binding = type('Binding', (), {'size': len(raw), 'sha256': hashlib.sha256(raw).hexdigest(),
                                       'name': projection.artifact_name})()
        wrong = type(projection)(projection.artifact_id, projection.artifact_name, projection.archive_sha256,
                                 projection.archive_bytes, projection.expanded_bytes + 1,
                                 projection.members, projection.source_sha256)
        with self.assertRaisesRegex(ValueError, 'completed-measurement-chain-archive-projection'):
            subject._projection_matches(raw, binding, wrong)
        self.assertEqual([], list(self.staging.iterdir()))

    def test_capture_disagreement_fails_and_owned_partial_tree_can_be_cleaned(self):
        raw = _archive({'source.json': b'capture', 'chain/source.json': b'other',
                        'chain/coverage.json': b'report'})
        rows, _ = completed_validation._zip_directory(raw)
        owned = {}
        with self.assertRaisesRegex(ValueError, 'completed-measurement-chain-source-consensus'):
            subject._extract_chain(raw, rows, self.staging / 'segment', b'capture',
                                   time.monotonic() + 5, owned)
        self.assertIn((), owned)
        self.assertTrue(completed_c1._cleanup_invocation(self.staging, self.staging / 'segment',
                                                         owned[()], owned))
        self.assertEqual([], list(self.staging.iterdir()))

    def test_predecessor_requires_exact_raw_c2_seed_binding_and_report(self):
        seeds = {'lineage/c2/native.json': b'raw-seed'}
        raw = {**seeds, 'chain-binding.json': b'binding', 'coverage.json': b'report',
               'lineage/selectors/N04/prior-binding.json': b'binding',
               'lineage/selectors/N04/prior-report.json': b'report'}
        subject._check_links(raw, 'N04', seeds, b'binding', b'report')
        cases = [
            ({**raw, 'lineage/c2/native.json': b'changed'}, 'c2-seed-mismatch'),
            ({**raw, 'lineage/selectors/N04/prior-binding.json': b'digest-only'},
             'predecessor-segment-mismatch'),
            ({**raw, 'lineage/selectors/N04/prior-report.json': b'changed'},
             'predecessor-segment-mismatch'),
        ]
        for changed, error in cases:
            with self.subTest(error=error, raw=changed):
                with self.assertRaisesRegex(ValueError, 'completed-measurement-chain-' + error):
                    subject._check_links(changed, 'N04', seeds, b'binding', b'report')

    def test_projection_flags_are_fixed_nonclaims(self):
        result = subject.CompletedMeasurementChainProjection(
            99, 1, 'a' * 40, 'b' * 64, 'github-actions-99-1', subject.ORDER,
            tuple(f'N{i:02d}' for i in range(1, 17)), b'{}', hashlib.sha256(b'{}').hexdigest())
        self.assertFalse(result.native_acceptance)
        self.assertFalse(result.coverage_credit)
        self.assertFalse(result.all_sixteen_claimed)
        with self.assertRaises((AttributeError, TypeError)):
            result.native_acceptance = True

    def test_capture_modules_replace_poison_cache_and_restore_on_success_or_failure(self):
        root = self.root / 'captured-source'
        checkpoint1 = root / '.github/issue779-checkpoint1'
        checkpoint4 = root / '.github/issue779-checkpoint4'
        checkpoint1.mkdir(parents=True)
        checkpoint4.mkdir(parents=True)
        chain_path = checkpoint1 / 'measurement_chain.py'
        chain_path.write_text('# captured source anchor\n')
        bodies = {
            '.github/issue779-checkpoint4/measurement_arguments.py': "VALUE = 'captured-arguments'\n",
            '.github/issue779-checkpoint1/publish.py': 'import binding\nVALUE = binding.VALUE\n',
            '.github/issue779-checkpoint1/measurement.py': 'import binding\nVALUE = binding.VALUE\n',
            '.github/issue779-checkpoint1/measurement_custody.py': "VALUE = 'captured-custody'\n",
        }
        rows = {}
        for relative, body in bodies.items():
            path = root / relative
            path.write_text(body)
            os.chmod(path, 0o644)
            rows[relative] = {'sha256': hashlib.sha256(body.encode()).hexdigest(), 'mode': '0644'}
        source_capture = json.dumps({'files': rows}).encode()
        binding = types.ModuleType('captured_binding')
        binding.VALUE = 'captured-binding'
        chain = types.SimpleNamespace(ROOT=root, __file__=str(chain_path), binding=binding)
        names = ('binding', 'measurement_arguments', 'publish', 'measurement', 'measurement_custody')
        missing = object()
        originals = {name: sys.modules.get(name, missing) for name in names}
        original_path = list(sys.path)

        def restore_original_state():
            sys.path[:] = original_path
            for name, prior in originals.items():
                if prior is missing:
                    sys.modules.pop(name, None)
                else:
                    sys.modules[name] = prior

        self.addCleanup(restore_original_state)

        def poison(value):
            modules = {name: types.ModuleType('poison_' + name) for name in names}
            for module in modules.values():
                module.VALUE = value
            sys.modules.update(modules)
            return modules, list(sys.path)

        def assert_restored(modules, path):
            self.assertEqual(path, sys.path)
            for name in names:
                self.assertIs(sys.modules.get(name), modules[name])

        modules, path = poison('success-poison')
        state = subject._pin_source_imports(chain, source_capture, binding, time.monotonic() + 5)
        try:
            self.assertIs(sys.modules['binding'], binding)
            self.assertEqual('captured-arguments', sys.modules['measurement_arguments'].VALUE)
            self.assertEqual('captured-binding', sys.modules['publish'].VALUE)
            self.assertEqual('captured-binding', sys.modules['measurement'].VALUE)
            self.assertEqual('captured-custody', sys.modules['measurement_custody'].VALUE)
        finally:
            subject._restore_source_imports(state)
        assert_restored(modules, path)

        modules, path = poison('failure-poison')
        try:
            state = subject._pin_source_imports(chain, source_capture, binding, time.monotonic() + 5)
            try:
                self.assertIs(sys.modules['publish'].VALUE, sys.modules['binding'].VALUE)
                raise RuntimeError('controlled failure')
            finally:
                subject._restore_source_imports(state)
        except RuntimeError:
            pass
        assert_restored(modules, path)

    def test_chain_source_hash_mismatch_rejects_before_execution(self):
        source = self.root / 'source-checkout'
        module_path = source / '.github/issue779-checkpoint1/measurement_chain.py'
        module_path.parent.mkdir(parents=True)
        marker = self.root / 'must-not-exist'
        module_path.write_text(f"from pathlib import Path\nPath({str(marker)!r}).write_text('executed')\n")
        os.chmod(module_path, 0o644)
        capture = json.dumps({'files': {'.github/issue779-checkpoint1/measurement_chain.py':
            {'sha256': '0' * 64}}}).encode()
        with self.assertRaisesRegex(ValueError, 'completed-measurement-chain-chain-source-hash'):
            subject._load_chain_module(source, capture, types.ModuleType('binding'), time.monotonic() + 5)
        self.assertFalse(marker.exists())

    def test_public_wrapper_rejects_invalid_stage_type_before_filesystem_work(self):
        with self.assertRaisesRegex(ValueError, 'completed-measurement-chain-stage-type'):
            subject.run_completed_measurement_chain(object(), object(), self.root / 'absent-stage',
                self.root / 'absent-source', self.staging, deadline=time.monotonic() + 5)
        self.assertEqual([], list(self.staging.iterdir()))

    def _public_wrapper_fixture(self):
        run_id, attempt = 109, 1
        head = completed_c1._git(completed_c1.ROOT, 'rev-parse', 'HEAD', deadline=time.monotonic() + 5)
        tree = completed_c1._git(completed_c1.ROOT, 'rev-parse', 'HEAD^{tree}', deadline=time.monotonic() + 5)
        capture = json.dumps({'schema': 'issue779-checkpoint2-source-v1', 'source': head,
                              'tree': tree, 'count': 0, 'files': {}}, separators=(',', ':')).encode()
        capture_digest = hashlib.sha256(capture).hexdigest()
        stage_root = self.root / 'full-archive-stage'
        stage_root.mkdir(mode=0o700)
        os.chmod(stage_root, 0o700)
        names = completed_run.expected_artifact_names(run_id, attempt)
        bindings, projections, archives = [], [], {}
        for index, name in enumerate(names):
            if name.startswith('issue779-c1-measured-'):
                selector = name.split('-')[3]
                binding_body, report = f'binding-{selector}'.encode(), f'report-{selector}'.encode()
                selector_index = subject.ORDER.index(selector)
                predecessor_binding = (b'c2-record' if selector_index == 0
                                       else f'binding-{subject.ORDER[selector_index - 1]}'.encode())
                predecessor_report = (b'c2-report' if selector_index == 0
                                      else f'report-{subject.ORDER[selector_index - 1]}'.encode())
                members = {'source.json': capture, 'chain/source.json': capture,
                           'chain/chain-binding.json': binding_body, 'chain/coverage.json': report,
                           'chain/lineage/c2/seed.bin': b'unchanged-c2-seed',
                           f'chain/lineage/selectors/{selector}/prior-binding.json': predecessor_binding,
                           f'chain/lineage/selectors/{selector}/prior-report.json': predecessor_report}
            else:
                members = {'source.json': capture, 'metadata.json': b'fixture-only'}
            raw = _archive(members)
            staged_filename = name + '.zip'
            archive_path = stage_root / staged_filename
            archive_path.write_bytes(raw)
            os.chmod(archive_path, 0o600)
            binding = completed_run.ArtifactBinding(index + 1, name, len(raw),
                hashlib.sha256(raw).hexdigest(), staged_filename)
            bindings.append(binding)
            projections.append(_parsed_projection(raw, name, index + 1, capture_digest))
            archives[name] = raw
        total = sum(binding.size for binding in bindings)
        stage = completed_run.CompletedRunStage(
            42, run_id, attempt, head, completed_run.WORKFLOW,
            ((completed_run.WORKFLOW, 'c' * 64), (completed_run.RUNTIME_WORKFLOW, 'd' * 64)),
            tree, capture_digest, tuple(range(1, 37)), tuple(bindings), total)
        aggregate = completed_validation.AggregateProjection(
            'fixture', head, capture_digest, (), (), 0, 0, 0, 'e' * 64)
        partial = completed_validation.PartialReceiptProjection(
            run_id, attempt, capture_digest, 1, 'f' * 64, (), (), '1' * 64)
        inventory = completed_validation.ParsedCompletedRunInventory(
            42, run_id, attempt, head, tree, capture_digest, 0, tuple(projections), aggregate, partial)
        source_root = Path(completed_c1.ROOT).resolve()
        chain_calls = []

        def scan(directory, _deadline):
            return {path.relative_to(directory).as_posix(): path.read_bytes()
                    for path in directory.rglob('*') if path.is_file()}

        def verify_segment(source_raw, raw_files, identity, prefix, _deadline):
            self.assertEqual(capture, source_raw)
            self.assertEqual(f'github-actions-{run_id}-{attempt}', identity)
            self.assertEqual(subject.ORDER[:len(prefix)], tuple(prefix))
            chain_calls.append(prefix[-1])
            return {'cases': [case for item in prefix for case in
                              (('N01', 'N02', 'N03') if item == 'None' else (item,))]}

        chain = types.SimpleNamespace(scan=scan,
            seed_files=lambda raw: {key: value for key, value in raw.items()
                                    if key.startswith('lineage/c2/')},
            verify_segment=verify_segment)
        return stage, inventory, stage_root, source_root, capture, capture_digest, archives, chain, chain_calls

    def test_public_wrapper_runs_full_inventory_through_fourteen_raw_archive_callbacks(self):
        (stage, inventory, stage_root, source_root, capture, capture_digest,
         archives, chain, calls) = self._public_wrapper_fixture()
        current_binding = types.ModuleType('fixture_current_binding')
        captured_binding = types.ModuleType('fixture_captured_binding')
        with patch.object(completed_c1, '_load_trusted_current_binding', return_value=current_binding), \
             patch.object(completed_c1, '_load_capture_and_binding',
                          return_value=({'source': stage.head_sha, 'tree': stage.source_tree}, captured_binding)), \
             patch.object(subject, '_load_chain_module', return_value=chain):
            result = subject.run_completed_measurement_chain(
                stage, inventory, stage_root, source_root, self.staging, deadline=time.monotonic() + 30)
        self.assertEqual(subject.ORDER, tuple(calls))
        final = archives[subject._measured_names(stage.run_id, stage.run_attempt)[-1]]
        with zipfile.ZipFile(io.BytesIO(final)) as archive:
            final_report = archive.read('chain/coverage.json')
        self.assertEqual(final_report, result.final_report_bytes)
        self.assertEqual(hashlib.sha256(final_report).hexdigest(), result.final_report_sha256)
        self.assertEqual([], list(self.staging.iterdir()))
        self.assertFalse(result.native_acceptance)
        self.assertFalse(result.coverage_credit)
        self.assertFalse(result.all_sixteen_claimed)

    def test_public_wrapper_rejects_capture_disagreement_before_loading_callback(self):
        (stage, inventory, stage_root, source_root, _capture, _capture_digest,
         _archives, _chain, _calls) = self._public_wrapper_fixture()
        loaded = []
        with patch.object(completed_c1, '_load_trusted_current_binding', return_value=types.ModuleType('binding')), \
             patch.object(completed_c1, '_load_capture_and_binding',
                          return_value=(
                              {'source': '0' * 40, 'tree': stage.source_tree},
                              types.ModuleType('binding'))), \
             patch.object(subject, '_load_chain_module', side_effect=lambda *args: loaded.append(args)):
            with self.assertRaisesRegex(ValueError, 'completed-measurement-chain-source-stage-binding'):
                subject.run_completed_measurement_chain(
                    stage, inventory, stage_root, source_root, self.staging, deadline=time.monotonic() + 30)
        self.assertEqual([], loaded)
        self.assertEqual([], list(self.staging.iterdir()))

    def test_fourteen_segments_replay_in_fixed_order_with_raw_predecessor_and_cleanup(self):
        capture = b'capture'
        stage = self.root / 'archive-stage'
        stage.mkdir(mode=0o700)
        os.chmod(stage, 0o700)
        bindings, projections, calls = {}, {}, []
        previous_binding = previous_report = b'c2-seed'
        seed = b'unchanged-c2-source-bytes'
        for index, selector in enumerate(subject.ORDER):
            name = subject._measured_names(99, 1)[index]
            binding_body = f'bound-{selector}'.encode()
            report = f'coverage-{selector}'.encode()
            prior_binding = b'c2-record' if index == 0 else previous_binding
            prior_report = b'c2-report' if index == 0 else previous_report
            files = {'chain/source.json': capture, 'chain/chain-binding.json': binding_body,
                     'chain/coverage.json': report, 'chain/lineage/c2/seed.bin': seed,
                     f'chain/lineage/selectors/{selector}/prior-binding.json': prior_binding,
                     f'chain/lineage/selectors/{selector}/prior-report.json': prior_report}
            members = {'source.json': capture, **files}
            raw = _archive(members)
            path = stage / (name + '.zip')
            path.write_bytes(raw)
            os.chmod(path, 0o600)
            rows, expanded = completed_validation._zip_directory(raw)
            bindings[name] = completed_run.ArtifactBinding(index + 1, name, len(raw),
                hashlib.sha256(raw).hexdigest(), path.name)
            projections[name] = completed_validation.ArchiveProjection(
                index + 1, name, hashlib.sha256(raw).hexdigest(), len(raw), expanded,
                tuple(completed_validation.MemberProjection(row[0], row[3], row[4], row[6]) for row in rows),
                hashlib.sha256(capture).hexdigest())
            previous_binding, previous_report = binding_body, report

        def scan(root, _deadline):
            result = {}
            for path in root.rglob('*'):
                if path.is_file():
                    result[path.relative_to(root).as_posix()] = path.read_bytes()
            return result

        def seed_files(raws):
            return {key: value for key, value in raws.items() if key.startswith('lineage/c2/')}

        def verify_segment(source_raw, raws, identity, prefix, _deadline):
            self.assertEqual(capture, source_raw)
            self.assertEqual('github-actions-99-1', identity)
            self.assertEqual(subject.ORDER[:len(prefix)], tuple(prefix))
            selector = prefix[-1]
            calls.append(selector)
            return {'cases': [case for item in prefix for case in
                              (('N01', 'N02', 'N03') if item == 'None' else (item,))]}

        chain = types.SimpleNamespace(scan=scan, seed_files=seed_files, verify_segment=verify_segment)
        result = subject._replay_segments(99, 1, bindings, projections, stage, self.staging,
            'a' * 40, capture, hashlib.sha256(capture).hexdigest(), 'github-actions-99-1', chain,
            stage_archive_bytes=sum(value.size for value in bindings.values()),
            deadline=time.monotonic() + 20)
        self.assertEqual(subject.ORDER, tuple(calls))
        self.assertEqual(b'coverage-N16', result.final_report_bytes)
        self.assertEqual(hashlib.sha256(b'coverage-N16').hexdigest(), result.final_report_sha256)
        self.assertEqual([], list(self.staging.iterdir()))
        self.assertFalse(result.native_acceptance)
        self.assertFalse(result.coverage_credit)
        self.assertFalse(result.all_sixteen_claimed)

    def test_fourteen_segment_replay_rejects_changed_immediate_raw_report(self):
        capture = b'capture'
        stage = self.root / 'archive-stage'
        stage.mkdir(mode=0o700)
        os.chmod(stage, 0o700)
        bindings, projections = {}, {}
        prior_binding = prior_report = b'seed'
        seed = b'c2'
        for index, selector in enumerate(subject.ORDER):
            name = subject._measured_names(101, 2)[index]
            current_binding, current_report = f'binding-{index}'.encode(), f'report-{index}'.encode()
            prior = {'chain/source.json': capture, 'chain/chain-binding.json': current_binding,
                     'chain/coverage.json': current_report, 'chain/lineage/c2/seed.bin': seed,
                     f'chain/lineage/selectors/{selector}/prior-binding.json':
                         b'wrong-binding' if selector == 'N08' else (b'c2-record' if index == 0 else prior_binding),
                     f'chain/lineage/selectors/{selector}/prior-report.json':
                         b'wrong-report' if selector == 'N08' else (b'c2-report' if index == 0 else prior_report)}
            raw = _archive({'source.json': capture, **prior})
            path = stage / (name + '.zip')
            path.write_bytes(raw)
            os.chmod(path, 0o600)
            rows, expanded = completed_validation._zip_directory(raw)
            bindings[name] = completed_run.ArtifactBinding(index + 1, name, len(raw),
                hashlib.sha256(raw).hexdigest(), path.name)
            projections[name] = completed_validation.ArchiveProjection(
                index + 1, name, hashlib.sha256(raw).hexdigest(), len(raw), expanded,
                tuple(completed_validation.MemberProjection(row[0], row[3], row[4], row[6]) for row in rows),
                hashlib.sha256(capture).hexdigest())
            prior_binding, prior_report = current_binding, current_report

        def scan(root, _deadline):
            return {path.relative_to(root).as_posix(): path.read_bytes()
                    for path in root.rglob('*') if path.is_file()}

        chain = types.SimpleNamespace(scan=scan,
            seed_files=lambda raws: {k: v for k, v in raws.items() if k.startswith('lineage/c2/')},
            verify_segment=lambda _source, _raws, _identity, prefix, _deadline:
                {'cases': [case for item in prefix for case in
                           (('N01', 'N02', 'N03') if item == 'None' else (item,))]})
        with self.assertRaisesRegex(ValueError, 'completed-measurement-chain-predecessor-segment-mismatch'):
            subject._replay_segments(101, 2, bindings, projections, stage, self.staging,
                'a' * 40, capture, hashlib.sha256(capture).hexdigest(), 'github-actions-101-2', chain,
                stage_archive_bytes=sum(value.size for value in bindings.values()),
                deadline=time.monotonic() + 20)
        self.assertEqual([], list(self.staging.iterdir()))


if __name__ == '__main__':
    unittest.main()
