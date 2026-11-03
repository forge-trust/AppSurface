"""Data and original-artifact replay tests for the non-authorizing C2 receipt validator."""
from __future__ import annotations

import hashlib
from dataclasses import replace
import json
import os
from pathlib import Path
import shutil
import sys
import tempfile
import time
import unittest
import zipfile

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / '.github/issue779-protected-qualification'))
import completed_c2 as c2
import completed_c1
import completed_run
import completed_validation

ORIGINAL_SOURCE = Path('/private/tmp/issue779-source109-original')
ORIGINAL_ARTIFACT = Path('/private/tmp/issue779-109-gate-artifacts/issue779-csharp-checkpoint2-40483696166-1')


def _archive(members):
    import io
    output = io.BytesIO()
    with zipfile.ZipFile(output, 'w', compression=zipfile.ZIP_STORED) as archive:
        for name, body in members.items():
            info = zipfile.ZipInfo(name)
            archive.writestr(info, body)
    return output.getvalue()


class CompletedC2Tests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name).resolve()

    def tearDown(self):
        self.temp.cleanup()

    def test_outer_wrapper_uses_real_projection_open_extract_and_cleanup(self):
        stage, inventory, stage_root, source, staging, head, tree, capture_sha, artifact_sha = self._synthetic_stage()
        original = c2.replay_extracted_c2
        observed = {}
        def replay(source_root, artifact_root, source_raw, **kwargs):
            observed['source'] = source_root
            observed['capture'] = source_raw
            observed['names'] = {p.relative_to(artifact_root).as_posix() for p in artifact_root.rglob('*')}
            self.assertEqual(b'{}', (artifact_root / 'native/native-receipt.json').read_bytes())
            return c2.C2ReceiptProjection('issue779-csharp-checkpoint2-native-v1', head, tree,
                                          stage.run_id, stage.run_attempt, (),
                                          f'issue779-csharp-checkpoint2-{stage.run_id}-{stage.run_attempt}',
                                          artifact_sha, capture_sha, 'a' * 64, 'b' * 64, False, False)
        try:
            c2.replay_extracted_c2 = replay
            result = c2.run_fixed_c2_receipt(stage, inventory, stage_root, source, staging,
                                             deadline=time.monotonic() + 10)
        finally:
            c2.replay_extracted_c2 = original
        self.assertEqual({'source.json', 'native', 'native/native-receipt.json'}, observed['names'])
        self.assertEqual([], list(staging.iterdir()))
        self.assertEqual(artifact_sha, result.artifact_sha256)

    def test_outer_wrapper_preserves_callback_failure_and_cleans_owned_nodes(self):
        (stage, inventory, stage_root, source, staging, _head, _tree, _capture_sha,
         _archive_sha) = self._synthetic_stage()
        failure = RuntimeError('fixed C2 callback failure')
        original = c2.replay_extracted_c2
        try:
            c2.replay_extracted_c2 = lambda *_args, **_kwargs: (_ for _ in ()).throw(failure)
            with self.assertRaisesRegex(RuntimeError, 'fixed C2 callback failure') as caught:
                c2.run_fixed_c2_receipt(stage, inventory, stage_root, source, staging,
                                        deadline=time.monotonic() + 10)
        finally:
            c2.replay_extracted_c2 = original
        self.assertIs(failure, caught.exception)
        self.assertEqual([], list(staging.iterdir()))

    def test_run_attempt_mismatch_rejects_before_artifact_read(self):
        stage, inventory, stage_root, source, staging, *_ = self._synthetic_stage(attempt=2)
        bad_inventory = completed_validation.ParsedCompletedRunInventory(
            inventory.repository_id, inventory.run_id, 1, inventory.head_sha, inventory.source_tree,
            inventory.source_capture_sha256, inventory.source_count, inventory.archives,
            inventory.aggregate, inventory.partial_receipt)
        with self.assertRaisesRegex(ValueError, 'completed-c2-inventory-stage-binding'):
            c2.run_fixed_c2_receipt(stage, bad_inventory, stage_root, source, staging,
                                    deadline=time.monotonic() + 10)
        self.assertEqual([], list(staging.iterdir()))

    def test_stage_rejects_extra_or_missing_archives_before_callback(self):
        from unittest.mock import patch
        stage, inventory, stage_root, source, staging, *_ = self._synthetic_stage()
        extra = stage_root / 'unexpected.zip'
        extra.write_bytes(b'unexpected')
        with patch.object(c2, 'replay_extracted_c2') as replay:
            with self.assertRaisesRegex(ValueError, 'completed-c2-stage-directory-entries'):
                c2.run_fixed_c2_receipt(stage, inventory, stage_root, source, staging,
                                        deadline=time.monotonic() + 10)
            extra.unlink()
            (stage_root / stage.artifacts[0].staged_filename).unlink()
            with self.assertRaisesRegex(ValueError, 'completed-c2-stage-directory-entries'):
                c2.run_fixed_c2_receipt(stage, inventory, stage_root, source, staging,
                                        deadline=time.monotonic() + 10)
            replay.assert_not_called()
        self.assertEqual([], list(staging.iterdir()))

    def test_changed_archive_bytes_reject_before_extraction(self):
        from unittest.mock import patch
        stage, inventory, stage_root, source, staging, *_ = self._synthetic_stage()
        name = f'issue779-csharp-checkpoint2-{stage.run_id}-{stage.run_attempt}.zip'
        path = stage_root / name
        raw = path.read_bytes()
        path.write_bytes(bytes([raw[0] ^ 1]) + raw[1:])
        with patch.object(c2, 'replay_extracted_c2') as replay:
            with self.assertRaisesRegex(ValueError, 'completed-c1-file-binding'):
                c2.run_fixed_c2_receipt(stage, inventory, stage_root, source, staging,
                                        deadline=time.monotonic() + 10)
            replay.assert_not_called()
        self.assertEqual([], list(staging.iterdir()))

    def test_member_projection_disagreement_rejects_before_extraction(self):
        from unittest.mock import patch
        stage, inventory, stage_root, source, staging, *_ = self._synthetic_stage()
        name = f'issue779-csharp-checkpoint2-{stage.run_id}-{stage.run_attempt}'
        archives = tuple(replace(row, members=()) if row.artifact_name == name else row
                         for row in inventory.archives)
        altered = replace(inventory, archives=archives)
        with patch.object(c2, 'replay_extracted_c2') as replay:
            with self.assertRaisesRegex(ValueError, 'completed-c2-archive-projection'):
                c2.run_fixed_c2_receipt(stage, altered, stage_root, source, staging,
                                        deadline=time.monotonic() + 10)
            replay.assert_not_called()
        self.assertEqual([], list(staging.iterdir()))

    def test_cleanup_preserves_substituted_node_and_primary_failure(self):
        from unittest.mock import patch
        stage, inventory, stage_root, source, staging, *_ = self._synthetic_stage()
        failure = RuntimeError('original callback failure')
        moved = self.root / 'original-receipt'
        replacements = []
        def replay(_source, artifact_root, _capture, **_kwargs):
            path = artifact_root / 'native/native-receipt.json'
            path.rename(moved)
            path.write_bytes(b'caller replacement')
            os.chmod(path, 0o600)
            replacements.append(path)
            raise failure
        with patch.object(c2, 'replay_extracted_c2', replay):
            with self.assertRaisesRegex(RuntimeError, 'original callback failure') as caught:
                c2.run_fixed_c2_receipt(stage, inventory, stage_root, source, staging,
                                        deadline=time.monotonic() + 10)
        self.assertIs(failure, caught.exception)
        self.assertTrue(getattr(failure, 'completed_c1_cleanup_failed', False)
                        or 'completed-c1-cleanup-failed' in getattr(failure, '__notes__', ()))
        self.assertEqual(b'caller replacement', replacements[0].read_bytes())
        self.assertEqual(b'{}', moved.read_bytes())

    def test_nonfinite_or_expired_deadline_rejects_before_staging(self):
        stage, inventory, stage_root, source, staging, *_ = self._synthetic_stage()
        for deadline in (float('nan'), float('inf'), time.monotonic() - 1, True):
            with self.subTest(deadline=deadline):
                with self.assertRaisesRegex(ValueError, 'completed-c2-deadline'):
                    c2.run_fixed_c2_receipt(stage, inventory, stage_root, source, staging,
                                            deadline=deadline)
                self.assertEqual([], list(staging.iterdir()))

    def test_retention_projection_rejects_before_extraction(self):
        from unittest.mock import patch
        stage, inventory, stage_root, source, staging, *_ = self._synthetic_stage()
        for name, limit in (('MAX_RETAINED_BYTES', 1), ('MAX_RETAINED_FILES', 1), ('MAX_RETAINED_LEAF', 1)):
            with self.subTest(bound=name):
                with patch.object(c2, name, limit), patch.object(c2, 'replay_extracted_c2') as replay:
                    with self.assertRaisesRegex(ValueError, 'completed-c2-retention-projection-'):
                        c2.run_fixed_c2_receipt(stage, inventory, stage_root, source, staging,
                                                deadline=time.monotonic() + 10)
                    replay.assert_not_called()
                self.assertEqual([], list(staging.iterdir()))

    def _synthetic_stage(self, attempt=1):
        source = self.root / 'source-root'
        stage_root = self.root / 'staged-artifacts'
        staging = self.root / 'target'
        for path in (source, stage_root, staging):
            path.mkdir(mode=0o700)
            os.chmod(path, 0o700)
        run_id, head, tree = 700, 'a' * 40, 'b' * 40
        capture = json.dumps({'schema': 'issue779-checkpoint2-source-v1', 'source': head,
                              'tree': tree, 'files': {}, 'count': 0},
                             sort_keys=True, separators=(',', ':')).encode()
        capture_sha = hashlib.sha256(capture).hexdigest()
        name = f'issue779-csharp-checkpoint2-{run_id}-{attempt}'
        c2_archive = _archive({'source.json': capture, 'native/': b'',
                               'native/native-receipt.json': b'{}'})
        bindings, projections = [], []
        for index, artifact_name in enumerate(completed_run.expected_artifact_names(run_id, attempt), 1):
            body = c2_archive if artifact_name == name else b'fixture:' + artifact_name.encode()
            filename = artifact_name + '.zip'
            path = stage_root / filename
            path.write_bytes(body)
            os.chmod(path, 0o600)
            digest = hashlib.sha256(body).hexdigest()
            binding = completed_run.ArtifactBinding(index, artifact_name, len(body), digest, filename)
            bindings.append(binding)
            members, expanded = (), 0
            if artifact_name == name:
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
        archive_sha = hashlib.sha256(c2_archive).hexdigest()
        return stage, inventory, stage_root, source, staging, head, tree, capture_sha, archive_sha


@unittest.skipUnless(os.environ.get('ISSUE779_RUN_C2_ORIGINAL_REPLAY') == '1'
                     and ORIGINAL_SOURCE.is_dir() and ORIGINAL_ARTIFACT.is_dir(),
                     'set ISSUE779_RUN_C2_ORIGINAL_REPLAY=1 with the pinned local run-109 artifact')
class OriginalC2ReplayTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name).resolve()
        self.source = ORIGINAL_SOURCE.resolve()
        self.original = ORIGINAL_ARTIFACT.resolve()

    def tearDown(self):
        self.temp.cleanup()

    def _copy_artifact(self, name):
        target = self.root / name
        shutil.copytree(self.original, target)
        for path in sorted(target.rglob('*'), key=lambda item: len(item.parts), reverse=True):
            os.chmod(path, 0o700 if path.is_dir() else 0o600)
        os.chmod(target, 0o700)
        return target

    def _replay(self, copied):
        raw = (copied / 'source.json').read_bytes()
        return c2.replay_extracted_c2(
            self.source, copied, raw, head='64e25cb406243a648edee31a9cf76935034759a7',
            source_tree='1ffbe0c60493ffa0cc6dda5720ad611e144cb26a', run_id=40483696166, run_attempt=1,
            artifact_name='issue779-csharp-checkpoint2-40483696166-1',
            artifact_sha256='59b9c11a80f5f9e04135ecb590844921cb0c0682598fd10459c97ad4a8489afe',
            deadline=time.monotonic() + 180)

    def _rebind_case(self, root, case, mutate):
        binding_path = root / 'producer-roots-binding.json'
        producer_binding = json.loads(binding_path.read_text())
        receipt_path = root / 'native/native-receipt.json'
        receipt = json.loads(receipt_path.read_text())
        row = next(row for row in receipt['cases'] if row['case'] == case)
        mutate(row, root)
        case_raw = json.dumps(row, ensure_ascii=False, sort_keys=True, separators=(',', ':')).encode()
        (root / 'native' / (case + '.json')).write_bytes(case_raw)
        os.chmod(root / 'native' / (case + '.json'), 0o600)
        producer_binding['case_receipt_sha256'][case] = hashlib.sha256(case_raw).hexdigest()
        receipt_raw = json.dumps(receipt, ensure_ascii=False, sort_keys=True, separators=(',', ':')).encode()
        receipt_path.write_bytes(receipt_raw)
        os.chmod(receipt_path, 0o600)
        producer_binding['native_receipt_sha256'] = hashlib.sha256(receipt_raw).hexdigest()
        producer_binding['native_receipt_bytes'] = len(receipt_raw)
        binding_path.write_bytes(json.dumps(producer_binding, ensure_ascii=False, sort_keys=True,
                                             separators=(',', ':')).encode())
        os.chmod(binding_path, 0o600)

    def test_original_c2_retained_artifact_runs_fixed_source_callbacks(self):
        result = self._replay(self._copy_artifact('authentic'))
        self.assertEqual(tuple(c2.CASE_NAMES), tuple(row[0] for row in result.case_rows))
        self.assertEqual('issue779-csharp-checkpoint2-40483696166-1', result.artifact_name)
        self.assertFalse(result.native_acceptance)
        self.assertFalse(result.trusted_eligibility)

    def test_original_c2_case_receipt_tampering_is_rejected(self):
        for case in c2.CASE_NAMES:
            with self.subTest(case=case):
                copied = self._copy_artifact('case-tamper-' + case)
                def alter(row, _root):
                    row['passed'] = False
                self._rebind_case(copied, case, alter)
                with self.assertRaisesRegex(ValueError, 'completed-c2-|measurement-chain-|producer-roots-'):
                    self._replay(copied)

    def test_original_c2_paired_eof_removal_is_rejected(self):
        copied = self._copy_artifact('eof-tamper')
        def alter(row, root):
            target = root / 'native/normal-owner.stderr'
            lines = target.read_bytes().splitlines()
            record = None
            output_lines = []
            for line in lines:
                try:
                    value = json.loads(line)
                except (ValueError, UnicodeError):
                    output_lines.append(line)
                    continue
                if isinstance(value, dict) and value.get('schema') == 'evidence-coverage-output-v1':
                    value['pairs'][0]['stdout_eof'] = False
                    record = value
                    line = json.dumps(value, sort_keys=True, separators=(',', ':')).encode()
                output_lines.append(line)
            if record is None:
                self.fail('the original C2 stderr has no output observation')
            replacement = b'\n'.join(output_lines) + b'\n'
            target.write_bytes(replacement)
            os.chmod(target, 0o600)
            row['stderr_sha256'] = hashlib.sha256(replacement).hexdigest()
            row['output_observation'] = record
        self._rebind_case(copied, 'normal', alter)
        with self.assertRaisesRegex(ValueError, 'completed-c2-paired-stream-eof'):
            self._replay(copied)

    def test_original_package_command_log_tampering_is_rejected(self):
        copied = self._copy_artifact('package-log-tamper')
        target = copied / 'package-smoke-details/sdk-load.log'
        target.write_bytes(target.read_bytes() + b'altered retained log\n')
        os.chmod(target, 0o600)
        with self.assertRaisesRegex(ValueError, 'Package smoke evidence rejected'):
            self._replay(copied)


if __name__ == '__main__':
    unittest.main()
