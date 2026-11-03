"""Portable identity and output-shape tests for the bounded C3 replay helper."""
from pathlib import Path
from dataclasses import replace
import hashlib
import json
import os
import tempfile
import time
import unittest
import zipfile
import io

import completed_c3
import completed_c1
import completed_run
import completed_validation


def _archive(members):
    output = io.BytesIO()
    with zipfile.ZipFile(output, 'w', compression=zipfile.ZIP_STORED) as archive:
        for name, body in members.items():
            archive.writestr(name, body)
    return output.getvalue()


class CompletedC3Tests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name).resolve()

    def tearDown(self):
        self.temp.cleanup()

    def test_projection_cannot_express_acceptance_or_trusted_eligibility(self):
        row = completed_c3.C3ReceiptProjection(
            'a' * 40, 'b' * 40, 10, 1,
            'issue779-checkpoint3-application-10-1', 100, 'c' * 64,
            'issue779-c3sdk-lifecycle-10-1', 200, 'd' * 64,
            'e' * 64, 'f' * 64, ('normal',), ('configure-sync-stall',), False, False)
        self.assertIs(row.native_acceptance, False)
        self.assertIs(row.trusted_eligibility, False)

    def test_wrong_run_bound_artifact_names_reject_before_any_artifact_read(self):
        with tempfile.TemporaryDirectory() as name:
            root = Path(name).resolve()
            source, application, lifecycle = (root / value for value in ('source', 'application', 'lifecycle'))
            for path in (source, application, lifecycle):
                path.mkdir(mode=0o700)
                path.chmod(0o700)
            with self.assertRaisesRegex(ValueError, '^completed-c3-artifact-identity$'):
                completed_c3.replay_extracted_c3(
                    source, application, lifecycle, b'{}', b'{}', head='a' * 40,
                    source_tree='b' * 40, run_id=10, run_attempt=1,
                    application_artifact_name='wrong', application_artifact_size=1,
                    application_artifact_sha256='c' * 64,
                    lifecycle_artifact_name='wrong', lifecycle_artifact_size=1,
                    lifecycle_artifact_sha256='d' * 64, deadline=time.monotonic() + 2)
            self.assertEqual([], list(application.iterdir()))
            self.assertEqual([], list(lifecycle.iterdir()))

    def test_outer_wrapper_stages_real_37_role_projection_and_cleans(self):
        from unittest.mock import patch
        stage, inventory, stage_root, source, staging, capture_sha, app_sha, life_sha = self._synthetic_stage()
        seen = {}
        def replay(source_root, app_root, life_root, app_capture, life_capture, **kwargs):
            seen['source'] = source_root
            seen['app_capture'] = app_capture
            seen['life_capture'] = life_capture
            seen['app_names'] = {p.relative_to(app_root).as_posix() for p in app_root.rglob('*')}
            seen['life_names'] = {p.relative_to(life_root).as_posix() for p in life_root.rglob('*')}
            return completed_c3.C3ReceiptProjection(
                stage.head_sha, stage.source_tree, stage.run_id, stage.run_attempt,
                f'issue779-checkpoint3-application-{stage.run_id}-{stage.run_attempt}',
                next(x.size for x in stage.artifacts if x.name.startswith(completed_c3.APPLICATION_PREFIX)),
                app_sha,
                f'issue779-c3sdk-lifecycle-{stage.run_id}-{stage.run_attempt}',
                next(x.size for x in stage.artifacts if x.name.startswith(completed_c3.LIFECYCLE_PREFIX)),
                life_sha, 'e' * 64, 'f' * 64, ('normal',), ('configure-sync-stall',), False, False)
        with patch.object(completed_c3, 'replay_extracted_c3', replay):
            result = completed_c3.run_fixed_c3_receipts(stage, inventory, stage_root, source, staging,
                                                         deadline=time.monotonic() + 10)
        self.assertEqual({'source.json', 'native', 'native/native-receipt.json'}, seen['app_names'])
        self.assertEqual({'source.json', 'lifecycle-stall-receipt.json'}, seen['life_names'])
        self.assertEqual(seen['app_capture'], seen['life_capture'])
        self.assertEqual(capture_sha, hashlib.sha256(seen['app_capture']).hexdigest())
        self.assertIs(result.native_acceptance, False)
        self.assertIs(result.trusted_eligibility, False)
        self.assertEqual([], list(staging.iterdir()))

    def test_outer_wrapper_preserves_callback_failure_and_cleans(self):
        from unittest.mock import patch
        stage, inventory, stage_root, source, staging, *_ = self._synthetic_stage()
        failure = RuntimeError('fixed C3 callback failure')
        with patch.object(completed_c3, 'replay_extracted_c3',
                          side_effect=failure):
            with self.assertRaisesRegex(RuntimeError, 'fixed C3 callback failure') as caught:
                completed_c3.run_fixed_c3_receipts(stage, inventory, stage_root, source, staging,
                                                     deadline=time.monotonic() + 10)
        self.assertIs(failure, caught.exception)
        self.assertEqual([], list(staging.iterdir()))

    def test_outer_wrapper_rejects_projection_mismatch_before_extraction(self):
        from unittest.mock import patch
        stage, inventory, stage_root, source, staging, *_ = self._synthetic_stage()
        name = f'issue779-checkpoint3-application-{stage.run_id}-{stage.run_attempt}'
        changed = tuple(replace(row, archive_sha256='0' * 64) if row.artifact_name == name else row
                        for row in inventory.archives)
        altered = replace(inventory, archives=changed)
        with patch.object(completed_c3, 'replay_extracted_c3') as replay:
            with self.assertRaisesRegex(ValueError, 'completed-c1-inventory-artifact-binding'):
                completed_c3.run_fixed_c3_receipts(stage, altered, stage_root, source, staging,
                                                     deadline=time.monotonic() + 10)
            replay.assert_not_called()
        self.assertEqual([], list(staging.iterdir()))

    def test_outer_wrapper_preserves_substituted_node_and_primary_failure(self):
        from unittest.mock import patch
        stage, inventory, stage_root, source, staging, *_ = self._synthetic_stage()
        failure = RuntimeError('original C3 callback failure')
        saved = self.root / 'owned-receipt'
        selected = {}
        def replay(_source, app_root, _life_root, *_args, **_kwargs):
            path = app_root / 'native/native-receipt.json'
            path.rename(saved)
            path.write_bytes(b'replacement')
            os.chmod(path, 0o600)
            selected['path'] = path
            raise failure
        with patch.object(completed_c3, 'replay_extracted_c3', replay):
            with self.assertRaisesRegex(RuntimeError, 'original C3 callback failure') as caught:
                completed_c3.run_fixed_c3_receipts(stage, inventory, stage_root, source, staging,
                                                     deadline=time.monotonic() + 10)
        self.assertIs(failure, caught.exception)
        self.assertEqual(b'replacement', selected['path'].read_bytes())
        self.assertEqual(b'{}', saved.read_bytes())
        self.assertTrue(getattr(failure, 'completed_c1_cleanup_failed', False)
                        or 'completed-c1-cleanup-failed' in getattr(failure, '__notes__', ()))

    def _synthetic_stage(self):
        source = self.root / 'source'
        stage_root = self.root / 'stage'
        staging = self.root / 'staging'
        for path in (source, stage_root, staging):
            path.mkdir(mode=0o700)
            os.chmod(path, 0o700)
        run_id, attempt = 700, 1
        head, tree = 'a' * 40, 'b' * 40
        capture = json.dumps({'schema': 'issue779-checkpoint2-source-v1', 'source': head,
                              'tree': tree, 'files': {}, 'count': 0},
                             sort_keys=True, separators=(',', ':')).encode()
        capture_sha = hashlib.sha256(capture).hexdigest()
        app_name = f'issue779-checkpoint3-application-{run_id}-{attempt}'
        life_name = f'issue779-c3sdk-lifecycle-{run_id}-{attempt}'
        app_zip = _archive({'source.json': capture, 'native/': b'',
                            'native/native-receipt.json': b'{}'})
        life_zip = _archive({'source.json': capture, 'lifecycle-stall-receipt.json': b'{}'})
        bindings, projections = [], []
        for index, artifact_name in enumerate(completed_run.expected_artifact_names(run_id, attempt), 1):
            body = app_zip if artifact_name == app_name else life_zip if artifact_name == life_name \
                else b'fixture:' + artifact_name.encode()
            filename = artifact_name + '.zip'
            path = stage_root / filename
            path.write_bytes(body)
            os.chmod(path, 0o600)
            digest = hashlib.sha256(body).hexdigest()
            bindings.append(completed_run.ArtifactBinding(index, artifact_name, len(body), digest, filename))
            members, expanded = (), 0
            if artifact_name in (app_name, life_name):
                rows, expanded = completed_validation._zip_directory(body)
                members = tuple(completed_validation.MemberProjection(row[0], row[3], row[4], row[6])
                                for row in rows)
            source_digest = capture_sha if artifact_name.startswith('issue779-checkpoint1-selector-') else None
            projections.append(completed_validation.ArchiveProjection(
                index, artifact_name, digest, len(body), expanded, members, source_digest))
        stage = completed_run.CompletedRunStage(
            321, run_id, attempt, head, completed_run.WORKFLOW, (), tree, capture_sha,
            tuple(range(1, 38)), tuple(bindings), sum(row.size for row in bindings))
        aggregate = completed_validation.AggregateProjection(
            'issue779-checkpoint1-aggregate-v1', head, capture_sha, tuple(completed_c1.SELECTORS),
            tuple(f'N{i:02d}' for i in range(1, 17)), 16, 0, 0, 'c' * 64)
        partial = completed_validation.PartialReceiptProjection(
            run_id, attempt, capture_sha, 1, 'd' * 64, tuple(range(1, 37)), (), 'e' * 64)
        inventory = completed_validation.ParsedCompletedRunInventory(
            321, run_id, attempt, head, tree, capture_sha, 0, tuple(projections), aggregate, partial)
        return stage, inventory, stage_root, source, staging, capture_sha, \
            hashlib.sha256(app_zip).hexdigest(), hashlib.sha256(life_zip).hexdigest()


if __name__ == '__main__':
    unittest.main()
