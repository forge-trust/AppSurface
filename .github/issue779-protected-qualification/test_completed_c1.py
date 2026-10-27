"""Data-only boundaries for fixed C1 callback staging; no callback-success fixture."""
from __future__ import annotations

import hashlib
import importlib.util
import json
import io
import os
from pathlib import Path
import stat
import sys
import tempfile
import time
import unittest
import zipfile
from types import SimpleNamespace

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / '.github/issue779-protected-qualification'))
import completed_c1 as c1
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


class CompletedC1Tests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name).resolve()
        self.source_root = self.root / 'source-checkout'
        self.source_root.mkdir(mode=0o700)
        self.stage_root = self.root / 'stage'
        self.stage_root.mkdir(mode=0o700)
        os.chmod(self.stage_root, 0o700)
        self.target = self.root / 'target'
        self.target.mkdir(mode=0o700)
        os.chmod(self.target, 0o700)

    def tearDown(self):
        self.temp.cleanup()

    def test_selector_artifact_names_are_the_closed_ordered_set(self):
        names = c1.selector_artifact_names(1234, 2)
        self.assertEqual(('None', 'N04', 'N05', 'N06', 'N07', 'N08', 'N09', 'N10',
                          'N11', 'N12', 'N13', 'N14', 'N15', 'N16'),
                         tuple(c1._selector_from_name(name, 1234, 2) for name in names))
        with self.assertRaisesRegex(ValueError, 'completed-c1-selector-artifact-name'):
            c1._selector_from_name(names[0], 1234, 3)

    def test_projection_mismatch_rejects_before_creating_callback_output(self):
        empty_aggregate = completed_validation.AggregateProjection(
            'issue779-checkpoint1-aggregate-v1', 'a' * 40, 'b' * 64, (), (), 0, 0, 1, 'c' * 64)
        empty_partial = completed_validation.PartialReceiptProjection(
            1234, 1, 'b' * 64, 1, 'd' * 64, (), (), 'e' * 64)
        inventory = completed_validation.ParsedCompletedRunInventory(
            99, 1234, 1, 'a' * 40, 'f' * 40, 'b' * 64, 1, (), empty_aggregate, empty_partial)
        stage = completed_run.CompletedRunStage(
            99, 1234, 1, 'a' * 40, completed_run.WORKFLOW, (), 'f' * 40, 'b' * 64,
            tuple(range(1, 38)), (), 0)
        with self.assertRaisesRegex(ValueError, 'completed-c1-inventory-artifact-count'):
            c1.run_fixed_c1_callbacks(stage, inventory, self.stage_root, self.root, self.target,
                                      deadline=time.monotonic() + 20)
        self.assertEqual([], list(self.target.iterdir()))

    def test_wrong_run_binding_rejects_before_creating_callback_output(self):
        aggregate = completed_validation.AggregateProjection(
            'issue779-checkpoint1-aggregate-v1', 'a' * 40, 'b' * 64,
            tuple(c1.SELECTORS), tuple(f'N{i:02d}' for i in range(1, 17)), 16, 0, 0, 'c' * 64)
        partial = completed_validation.PartialReceiptProjection(
            1234, 1, 'b' * 64, 1, 'd' * 64, tuple(range(1, 37)), (), 'e' * 64)
        inventory = completed_validation.ParsedCompletedRunInventory(
            99, 1234, 1, 'a' * 40, 'f' * 40, 'b' * 64, 1, (), aggregate, partial)
        stage = completed_run.CompletedRunStage(
            99, 1235, 1, 'a' * 40, completed_run.WORKFLOW, (), 'f' * 40, 'b' * 64,
            tuple(range(1, 38)), (), 0)
        with self.assertRaisesRegex(ValueError, 'completed-c1-inventory-stage-binding'):
            c1.run_fixed_c1_callbacks(stage, inventory, self.stage_root, self.root, self.target,
                                      deadline=time.monotonic() + 20)
        self.assertEqual([], list(self.target.iterdir()))

    def test_extracts_only_projection_matched_bounded_members(self):
        raw = _archive({'native/': b'', 'native/result.json': b'{"exit":0}',
                        'native/empty.log': b''})
        rows, expanded = completed_validation._zip_directory(raw)
        members = tuple(completed_validation.MemberProjection(row[0], row[3], row[4], row[6]) for row in rows)
        projection = completed_validation.ArchiveProjection(1, 'fixture', hashlib.sha256(raw).hexdigest(),
                                                             len(raw), expanded, members, None)
        out = self.root / 'out'
        actual = c1._extract_archive(raw, projection, out, deadline=time.monotonic() + 5, expanded_used=0)
        self.assertEqual(expanded, actual)
        self.assertEqual(b'{"exit":0}', (out / 'native/result.json').read_bytes())
        self.assertEqual(b'', (out / 'native/empty.log').read_bytes())
        self.assertEqual(0o700, stat.S_IMODE((out / 'native').lstat().st_mode))
        self.assertEqual(0o600, stat.S_IMODE((out / 'native/result.json').lstat().st_mode))

    def test_projection_mismatch_rejects_before_extraction_directory_creation(self):
        raw = _archive({'native/result.json': b'{"exit":0}'})
        rows, expanded = completed_validation._zip_directory(raw)
        member = rows[0]
        projection = completed_validation.ArchiveProjection(
            1, 'fixture', hashlib.sha256(raw).hexdigest(), len(raw), expanded + 1,
            (completed_validation.MemberProjection(member[0], member[3], member[4], member[6]),), None)
        out = self.root / 'never-created'
        with self.assertRaisesRegex(ValueError, 'completed-c1-archive-projection-mismatch'):
            c1._extract_archive(raw, projection, out, deadline=time.monotonic() + 5, expanded_used=0)
        self.assertFalse(out.exists())

    def test_staging_rejects_excessive_implicit_directory_depth_before_creation(self):
        raw = _archive({'/'.join(['d'] * (c1.MAX_STAGING_DEPTH + 1) + ['leaf']): b'x'})
        rows, expanded = completed_validation._zip_directory(raw)
        projection = completed_validation.ArchiveProjection(
            1, 'fixture', hashlib.sha256(raw).hexdigest(), len(raw), expanded,
            tuple(completed_validation.MemberProjection(row[0], row[3], row[4], row[6]) for row in rows), None)
        out = self.root / 'too-deep'
        with self.assertRaisesRegex(ValueError, 'completed-c1-staging-node-depth'):
            c1._extract_archive(raw, projection, out, deadline=time.monotonic() + 5, expanded_used=0)
        self.assertFalse(out.exists())

    def test_forged_native_claim_in_aggregate_sentinel_is_rejected(self):
        expected = {'schema': 'issue779-checkpoint1-aggregate-v1', 'source': 'a' * 40,
                    'source_capture_sha256': 'b' * 64, 'selectors': list(c1.SELECTORS),
                    'cases': list(c1.CASES), 'passed': 16, 'skipped': 0, 'exit': 0,
                    'native_acceptance': False, 'coverage_credit': False}
        claimed = dict(expected, native_acceptance=True)
        with self.assertRaisesRegex(ValueError, 'completed-c1-aggregate-content-mismatch'):
            c1._check_aggregate(claimed, expected)
        fields = set(c1.C1CallbackProjection.__dataclass_fields__)
        self.assertFalse({'qualified', 'accepted', 'admission', 'trusted'} & fields)

    def test_original_binding_decoder_allows_distinct_case_image_directory_keys(self):
        raw = json.dumps({'tool': {'directories': {'assets': '/x/a', 'Assets': '/x/A'}}}).encode()
        spec = importlib.util.spec_from_file_location('test_c1_original_binding',
                                                       ROOT / '.github/issue779-checkpoint1/binding.py')
        trusted = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(trusted)
        self.assertEqual({'assets': '/x/a', 'Assets': '/x/A'},
                         trusted.decode(raw)['tool']['directories'])
        with self.assertRaises(trusted.Rejected):
            trusted.exact({'source_root': '/x', 'Source_Root': '/y'}, ('source_root',), 'build-fields')

    def test_exact_aggregate_identity_checks_source_and_workflow(self):
        aggregate = {'schema': 'issue779-checkpoint1-aggregate-v1', 'source': 'a' * 40,
                     'source_capture_sha256': 'b' * 64, 'selectors': list(c1.SELECTORS),
                     'cases': list(c1.CASES), 'passed': 16, 'skipped': 0, 'exit': 0,
                     'native_acceptance': False, 'coverage_credit': False}
        raw = json.dumps(aggregate, separators=(',', ':')).encode()
        self.assertEqual(aggregate, c1._validate_aggregate_identity(raw, 'a' * 40, 'b' * 64))
        aggregate['source'] = 'c' * 40
        with self.assertRaisesRegex(ValueError, 'completed-c1-aggregate-identity'):
            c1._validate_aggregate_identity(json.dumps(aggregate).encode(), 'a' * 40, 'b' * 64)

    def test_cleanup_removes_only_invocation_owned_nodes_and_root(self):
        invocation = self.target / 'owned'
        invocation.mkdir(mode=0o700)
        identity = c1._identity_fd(invocation.lstat())
        owned = {(): identity}
        subdir = invocation / 'sub'
        c1._mkdir(subdir, owned=owned, ownership_root=invocation)
        file_path = subdir / 'data'
        c1._write_bytes(file_path, b'owned', deadline=time.monotonic() + 5,
                        owned=owned, ownership_root=invocation)
        self.assertTrue(c1._cleanup_invocation(self.target, invocation, identity, owned))
        self.assertFalse(invocation.exists())

    def test_mkdir_post_create_open_failure_records_and_cleans_only_created_node(self):
        invocation = self.target / 'owned'
        owned = {}
        original_open = c1.os.open
        failure = OSError('injected post-create directory open failure')
        injected = False

        def fail_child_open(path, flags, *args, **kwargs):
            nonlocal injected
            if path == 'owned' and kwargs.get('dir_fd') is not None and not injected:
                injected = True
                raise failure
            return original_open(path, flags, *args, **kwargs)

        c1.os.open = fail_child_open
        try:
            with self.assertRaises(OSError) as caught:
                c1._mkdir(invocation, owned=owned, ownership_root=invocation)
        finally:
            c1.os.open = original_open
        self.assertIs(failure, caught.exception)
        self.assertNotIn((), owned)
        self.assertFalse(invocation.exists())
        self.assertTrue(self.target.exists())

    def test_mkdir_open_substitution_preserves_replacement_and_original_error(self):
        invocation = self.target / 'owned'
        owned = {}
        detached = self.target / 'detached-original'
        original_open = c1.os.open

        def substitute_before_child_open(path, flags, *args, **kwargs):
            if path == 'owned' and kwargs.get('dir_fd') is not None and not detached.exists():
                invocation.rename(detached)
                invocation.mkdir(mode=0o700)
            return original_open(path, flags, *args, **kwargs)

        c1.os.open = substitute_before_child_open
        try:
            with self.assertRaisesRegex(ValueError, 'completed-c1-directory-open-binding') as caught:
                c1._mkdir(invocation, owned=owned, ownership_root=invocation)
        finally:
            c1.os.open = original_open
        self.assertTrue(getattr(caught.exception, 'completed_c1_cleanup_failed', False)
                        or 'completed-c1-cleanup-failed' in getattr(caught.exception, '__notes__', []))
        self.assertTrue(detached.is_dir())
        self.assertTrue(invocation.is_dir())
        self.assertIn((), owned)
        self.assertTrue(self.target.exists())

    def test_cleanup_refuses_substituted_file_and_retains_invocation(self):
        invocation = self.target / 'owned'
        invocation.mkdir(mode=0o700)
        identity = c1._identity_fd(invocation.lstat())
        owned = {(): identity}
        file_path = invocation / 'data'
        c1._write_bytes(file_path, b'owned', deadline=time.monotonic() + 5,
                        owned=owned, ownership_root=invocation)
        held = file_path.open('rb')
        file_path.unlink()
        file_path.write_bytes(b'substitute')
        self.assertFalse(c1._cleanup_invocation(self.target, invocation, identity, owned))
        self.assertEqual(b'substitute', file_path.read_bytes())
        self.assertTrue(invocation.exists())
        held.close()

    def test_cleanup_refuses_substituted_parent_directory(self):
        invocation = self.target / 'owned'
        invocation.mkdir(mode=0o700)
        identity = c1._identity_fd(invocation.lstat())
        owned = {(): identity}
        subdir = invocation / 'sub'
        c1._mkdir(subdir, owned=owned, ownership_root=invocation)
        file_path = subdir / 'data'
        c1._write_bytes(file_path, b'owned', deadline=time.monotonic() + 5,
                        owned=owned, ownership_root=invocation)
        held = os.open(subdir, os.O_RDONLY | os.O_DIRECTORY)
        subdir.rename(invocation / 'detached-original')
        subdir.mkdir(mode=0o700)
        (subdir / 'data').write_bytes(b'substitute')
        self.assertFalse(c1._cleanup_invocation(self.target, invocation, identity, owned))
        self.assertEqual(b'substitute', (subdir / 'data').read_bytes())
        os.close(held)

    def test_cleanup_refuses_substituted_work_root(self):
        invocation = self.target / 'owned'
        invocation.mkdir(mode=0o700)
        identity = c1._identity_fd(invocation.lstat())
        owned = {(): identity}
        file_path = invocation / 'data'
        c1._write_bytes(file_path, b'owned', deadline=time.monotonic() + 5,
                        owned=owned, ownership_root=invocation)
        held = os.open(invocation, os.O_RDONLY | os.O_DIRECTORY)
        invocation.rename(self.target / 'detached-original')
        invocation.mkdir(mode=0o700)
        (invocation / 'data').write_bytes(b'substitute')
        self.assertFalse(c1._cleanup_invocation(self.target, invocation, identity, owned))
        self.assertEqual(b'substitute', (invocation / 'data').read_bytes())
        os.close(held)

    def test_primary_callback_failure_survives_cleanup_failure(self):
        primary = ValueError('callback-failed')
        original = c1._cleanup_invocation
        try:
            c1._cleanup_invocation = lambda *_args: False
            with self.assertRaisesRegex(ValueError, 'callback-failed') as caught:
                c1._finish_invocation(self.target, self.target / 'absent', (), {}, primary)
        finally:
            c1._cleanup_invocation = original
        self.assertTrue(getattr(caught.exception, 'completed_c1_cleanup_failed', True))

    def test_cleanup_failure_without_callback_error_is_fixed_rejection(self):
        original = c1._cleanup_invocation
        try:
            c1._cleanup_invocation = lambda *_args: False
            with self.assertRaisesRegex(ValueError, 'completed-c1-cleanup'):
                c1._finish_invocation(self.target, self.target / 'absent', (), {}, None)
        finally:
            c1._cleanup_invocation = original

    def test_callback_module_state_is_restored_when_callback_raises(self):
        class Current:
            def decode(self, value):
                return json.loads(value)

        captured = object()
        old = c1.sys.modules.get('binding')
        existed = 'binding' in c1.sys.modules
        c1.sys.modules['binding'] = None
        original_load = c1._load_run_native
        original_identity = c1._validate_receipt_identities
        original_git = c1._git
        original_current = c1._load_trusted_current_binding
        original_capture = c1._load_capture_and_binding
        try:
            c1._load_trusted_current_binding = lambda *_a, **_k: Current()
            c1._load_capture_and_binding = lambda *_a, **_k: (
                {'source': 'a' * 40, 'tree': 'b' * 40}, captured)
            c1._git = lambda _root, *args, **_kwargs: 'a' * 40 if args[-1] == 'HEAD' else 'b' * 40
            c1._validate_receipt_identities = lambda *_a, **_k: []

            class Module:
                class binding:
                    MAX_JSON = 100
                def aggregate(self, _args):
                    raise RuntimeError('callback failed')

            c1._load_run_native = lambda *_a, **_k: Module()
            callback_root = self.target / 'callback'
            callback_root.mkdir(mode=0o700)
            with self.assertRaisesRegex(RuntimeError, 'callback failed'):
                c1._reconcile_callbacks(self.source_root, [], b'{"source":"a"}', callback_root, b'{}',
                                        run_id=1, run_attempt=1, input_aggregate_archive_sha256='c' * 64,
                                        deadline=time.monotonic() + 180)
            self.assertIn('binding', c1.sys.modules)
            self.assertIsNone(c1.sys.modules.get('binding'))
        finally:
            c1._load_run_native = original_load
            c1._validate_receipt_identities = original_identity
            c1._git = original_git
            c1._load_trusted_current_binding = original_current
            c1._load_capture_and_binding = original_capture
            if existed:
                c1.sys.modules['binding'] = old
            else:
                c1.sys.modules.pop('binding', None)

    def test_source_root_and_workflow_mismatch_reject_before_callback(self):
        receipts = [self._receipt_dir(selector, str(self.root), 'github-actions-1-1')
                    for selector in c1.SELECTORS]
        receipts[0] = self._receipt_dir('None', '/wrong', 'github-actions-1-1')
        self.assertRaisesRegex(ValueError, 'completed-c1-source-root-receipt',
                               lambda: c1._validate_receipt_identities(
                                   self.root, receipts, type('Binding', (), {'decode': staticmethod(json.loads)})(),
                                   1, 1, deadline=time.monotonic() + 5))
        receipts[0] = self._receipt_dir('None', str(self.root), 'github-actions-1-1')
        receipts[-1] = self._receipt_dir(c1.SELECTORS[-1], str(self.root), 'github-actions-2-1')
        self.assertRaisesRegex(ValueError, 'completed-c1-workflow-identity',
                               lambda: c1._validate_receipt_identities(
                                   self.root, receipts, type('Binding', (), {'decode': staticmethod(json.loads)})(),
                                   1, 1, deadline=time.monotonic() + 5))

    def _run_synthetic_outer(self, *, fail=False, substitute=False):
        source = self.root / 'source-root'
        source.mkdir(mode=0o700)
        stage_root = self.root / 'full-stage'
        stage_root.mkdir(mode=0o700)
        run_id, attempt = 700, 2
        head, tree = 'a' * 40, 'b' * 40
        capture = json.dumps({'schema': 'issue779-checkpoint2-source-v1', 'source': head,
                              'tree': tree, 'count': 0, 'files': {}},
                             separators=(',', ':')).encode()
        capture_sha = hashlib.sha256(capture).hexdigest()
        names = completed_run.expected_artifact_names(run_id, attempt)
        bindings, projections, archives, artifact_rows = {}, {}, {}, []
        for index, name in enumerate(names):
            raw = b'archive:' + name.encode()
            path = stage_root / (name + '.zip')
            path.write_bytes(raw)
            os.chmod(path, 0o600)
            row = completed_run.ArtifactBinding(index + 1, name, len(raw), hashlib.sha256(raw).hexdigest(), path.name)
            artifact_rows.append(row)
            bindings[name] = row
            archives[name] = raw
            if name.startswith('issue779-checkpoint1-selector-'):
                projections[name] = SimpleNamespace(archive_sha256=row.sha256, expanded_bytes=0,
                                                    members=(), source_sha256=capture_sha)
        stage = completed_run.CompletedRunStage(
            99, run_id, attempt, head, completed_run.WORKFLOW, (), tree, capture_sha,
            tuple(range(1, 38)), tuple(artifact_rows), sum(row.size for row in artifact_rows))
        aggregate = {'schema': 'issue779-checkpoint1-aggregate-v1', 'source': head,
                     'source_capture_sha256': capture_sha, 'selectors': list(c1.SELECTORS),
                     'cases': list(c1.CASES), 'passed': 16, 'skipped': 0, 'exit': 0,
                     'native_acceptance': False, 'coverage_credit': False}
        body = json.dumps(aggregate, sort_keys=True, separators=(',', ':')).encode()
        agg_projection = completed_validation.AggregateProjection(
            aggregate['schema'], head, capture_sha, tuple(c1.SELECTORS), tuple(c1.CASES), 16, 0, 0,
            hashlib.sha256(body).hexdigest())
        partial = completed_validation.PartialReceiptProjection(run_id, attempt, capture_sha, 1, 'd' * 64,
                                                                  tuple(range(1, 37)), (), 'e' * 64)
        inventory = completed_validation.ParsedCompletedRunInventory(
            99, run_id, attempt, head, tree, capture_sha, 0, (), agg_projection, partial)
        archive_sha = 'f' * 64
        sentinel = RuntimeError('synthetic fixed callback failure')

        def extract(_raw, _projection, destination, *, deadline, expanded_used, owned, ownership_root):
            c1._mkdir(destination, owned=owned, ownership_root=ownership_root)
            c1._write_bytes(destination / 'source.json', capture, deadline=deadline,
                             owned=owned, ownership_root=ownership_root)
            native = destination / 'native'
            c1._mkdir(native, owned=owned, ownership_root=ownership_root)
            c1._write_bytes(native / 'result.json', b'{}', deadline=deadline,
                             owned=owned, ownership_root=ownership_root)
            return expanded_used

        def reconcile(_source, _receipts, _capture, _callback_root, _expected, *, owned,
                      ownership_root, **_kwargs):
            if substitute:
                victim = ownership_root / 'selectors' / 'selector-None' / 'native' / 'result.json'
                sentinel.held_file = victim.open('rb')
                victim.unlink()
                victim.write_bytes(b'substituted')
            if fail:
                raise sentinel
            return c1.C1CallbackProjection(run_id, attempt, head, capture_sha, tuple(c1.SELECTORS),
                                           tuple(c1.CASES), 16, 0, 0, archive_sha,
                                           hashlib.sha256(body).hexdigest(), hashlib.sha256(body).hexdigest())

        original = {name: getattr(c1, name) for name in (
            '_assert_projection', '_read_expected_aggregate', '_open_bound_file',
            '_extract_archive', '_reconcile_callbacks')}
        try:
            c1._assert_projection = lambda *_args: (bindings, projections)
            c1._read_expected_aggregate = lambda *_args, **_kwargs: (aggregate, archive_sha, body)
            c1._open_bound_file = lambda path, *_args, **_kwargs: archives[Path(path).name[:-4]]
            c1._extract_archive = extract
            c1._reconcile_callbacks = reconcile
            invoke = lambda: c1.run_fixed_c1_callbacks(
                stage, inventory, stage_root, source, self.target, deadline=time.monotonic() + 180)
            if fail:
                with self.assertRaisesRegex(RuntimeError, 'synthetic fixed callback failure') as caught:
                    invoke()
                if substitute:
                    self.assertTrue(getattr(caught.exception, 'completed_c1_cleanup_failed', False)
                                    or 'completed-c1-cleanup-failed' in getattr(caught.exception, '__notes__', []))
                    victim = (self.target / 'c1-fixed-callbacks' / 'selectors' / 'selector-None'
                              / 'native' / 'result.json')
                    self.assertEqual(b'substituted', victim.read_bytes())
                    sentinel.held_file.close()
                else:
                    self.assertEqual([], list(self.target.iterdir()))
                return caught.exception
            result = invoke()
            self.assertEqual(archive_sha, result.input_aggregate_archive_sha256)
            self.assertEqual(hashlib.sha256(body).hexdigest(), result.input_aggregate_body_sha256)
            self.assertEqual([], list(self.target.iterdir()))
            return result
        finally:
            for name, value in original.items():
                setattr(c1, name, value)

    def test_outer_wrapper_cleans_synthetic_stage_on_success_and_keeps_archive_hash(self):
        self._run_synthetic_outer()

    def test_outer_wrapper_cleans_synthetic_stage_after_callback_failure(self):
        self._run_synthetic_outer(fail=True)

    def test_outer_wrapper_preserves_substituted_node_and_original_failure(self):
        self._run_synthetic_outer(fail=True, substitute=True)

    def _receipt_dir(self, selector, source_root, workflow_identity):
        suffix = hashlib.sha256((source_root + workflow_identity + str(time.monotonic_ns())).encode()).hexdigest()[:10]
        directory = self.root / ('receipt-' + selector + '-' + suffix)
        native = directory / 'native'
        native.mkdir(mode=0o700, parents=True)
        for name, value in {
            'result.json': {'selector': selector},
            'build-receipt.json': {'source_root': source_root},
            'execution.json': {'workflow_identity': workflow_identity},
        }.items():
            path = native / name
            path.write_text(json.dumps(value), encoding='utf-8')
            os.chmod(path, 0o600)
        return native


class CompletedC1ArchivePipelineTests(unittest.TestCase):
    """Synthetic provider inventory; real ZIP inspection/extraction and owned cleanup."""

    def _invoke(self, *, callback_fails=False):
        from dataclasses import replace
        from test_completed_validation import CompletedValidationTests

        fixture = CompletedValidationTests('test_projects_exact37_archive_inventory_without_qualification')
        fixture.setUp()
        self.addCleanup(fixture.tearDown)
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        root = Path(temp.name).resolve()
        source = root / 'source'
        target = root / 'target'
        source.mkdir(mode=0o700)
        target.mkdir(mode=0o700)
        replacements = []
        for binding in fixture.stage.artifacts:
            if binding.name.startswith('issue779-checkpoint1-selector-'):
                selector = c1._selector_from_name(binding.name, fixture.run_id, fixture.attempt)
                raw = _archive({'source.json': fixture.capture,
                                'native/result.json': json.dumps({'selector': selector}).encode(),
                                'native/empty.log': b''})
                (fixture.stage_root / binding.staged_filename).write_bytes(raw)
                replacements.append(replace(binding, size=len(raw), sha256=hashlib.sha256(raw).hexdigest()))
            else:
                replacements.append(binding)
        stage = replace(fixture.stage, artifacts=tuple(replacements),
                        total_archive_bytes=sum(binding.size for binding in replacements))
        inventory = completed_validation.inspect_completed_run(
            stage, fixture.stage_root, fixture.capture, deadline=time.monotonic() + 10)
        archive_hash = hashlib.sha256(fixture.aggregate_raw).hexdigest()
        body_hash = hashlib.sha256(fixture.aggregate_body).hexdigest()
        expected_error = RuntimeError('synthetic callback failure after actual ZIP staging')
        calls = []

        def callback(_source, receipts, capture, callback_root, expected, **kwargs):
            self.assertEqual(source, _source)
            self.assertEqual(fixture.capture, capture)
            self.assertEqual(fixture.aggregate_body, expected)
            self.assertEqual(archive_hash, kwargs['input_aggregate_archive_sha256'])
            self.assertEqual(list(c1.SELECTORS), [json.loads((receipt / 'result.json').read_bytes())['selector']
                                                  for receipt in receipts])
            self.assertTrue(all((receipt / 'empty.log').read_bytes() == b'' for receipt in receipts))
            calls.append(True)
            if callback_fails:
                raise expected_error
            return c1.C1CallbackProjection(stage.run_id, stage.run_attempt, stage.head_sha,
                                           stage.source_capture_sha256, c1.SELECTORS, c1.CASES,
                                           16, 0, 0, archive_hash, body_hash, body_hash)

        original = c1._reconcile_callbacks
        try:
            c1._reconcile_callbacks = callback
            if callback_fails:
                with self.assertRaisesRegex(RuntimeError, 'actual ZIP staging') as caught:
                    c1.run_fixed_c1_callbacks(stage, inventory, fixture.stage_root, source, target,
                                              deadline=time.monotonic() + 180)
                self.assertIs(expected_error, caught.exception)
            else:
                result = c1.run_fixed_c1_callbacks(stage, inventory, fixture.stage_root, source, target,
                                                  deadline=time.monotonic() + 180)
                self.assertEqual(archive_hash, result.input_aggregate_archive_sha256)
                self.assertEqual(body_hash, result.input_aggregate_body_sha256)
            self.assertEqual([True], calls)
            self.assertEqual([], list(target.iterdir()))
            self.assertEqual(37, len(list(fixture.stage_root.iterdir())))
        finally:
            c1._reconcile_callbacks = original

    def test_actual_archive_pipeline_cleans_after_synthetic_callback_return(self):
        self._invoke()

    def test_actual_archive_pipeline_cleans_after_synthetic_callback_error(self):
        self._invoke(callback_fails=True)


if __name__ == '__main__':
    unittest.main()
