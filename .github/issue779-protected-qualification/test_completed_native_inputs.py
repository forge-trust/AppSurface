"""Portable tests for fixed native validator input staging."""
from __future__ import annotations

import hashlib
import json
import os
from dataclasses import FrozenInstanceError, replace
from pathlib import Path
import sys
import tempfile
import time
import unittest
import zipfile
from io import BytesIO

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / '.github/issue779-protected-qualification'))
import completed_native_inputs as subject
import completed_run
import completed_validation


def _zip(members):
    stream = BytesIO()
    with zipfile.ZipFile(stream, 'w', compression=zipfile.ZIP_STORED) as archive:
        for name, body in members.items():
            archive.writestr(name, body)
    return stream.getvalue()


class NativeInputTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.base = Path(self.temp.name).resolve()
        self.stage_root = self.base / 'stage'
        self.parent = self.base / 'private'
        self.stage_root.mkdir(mode=0o700)
        self.parent.mkdir(mode=0o700)
        os.chmod(self.stage_root, 0o700)
        os.chmod(self.parent, 0o700)
        self.stage, self.inventory, self.capture = self._fixture()

    def tearDown(self):
        self.temp.cleanup()

    def _fixture(self):
        run_id, attempt, head, tree = 700, 1, 'a' * 40, 'b' * 40
        capture = json.dumps({'schema': 'issue779-checkpoint2-source-v1', 'source': head,
                              'tree': tree, 'files': {'README.md': {'sha256': 'f' * 64}}, 'count': 1}, sort_keys=True,
                             separators=(',', ':')).encode()
        capture_sha = hashlib.sha256(capture).hexdigest()
        names = subject._role_names(type('Run', (), {'run_id': run_id, 'run_attempt': attempt})())
        archives = {}
        for role, artifact_name in names.items():
            members = {'source.json': capture}
            for selected_role, member, _output in subject.MEMBER_MAP:
                if selected_role == role and member != 'source.json':
                    members[member] = b'<TestRun />' if member.endswith('.trx') else b'{}'
            archives[artifact_name] = _zip(members)
        expected = completed_run.expected_artifact_names(run_id, attempt)
        bindings, projections = [], []
        for index, artifact_name in enumerate(expected, 1):
            raw = archives.get(artifact_name, b'not-selected')
            filename = artifact_name + '.zip'
            path = self.stage_root / filename
            path.write_bytes(raw)
            path.chmod(0o600)
            digest = hashlib.sha256(raw).hexdigest()
            binding = completed_run.ArtifactBinding(index, artifact_name, len(raw), digest, filename)
            bindings.append(binding)
            members, expanded = (), 0
            if artifact_name in archives:
                rows, expanded = completed_validation._zip_directory(raw)
                members = tuple(completed_validation.MemberProjection(row[0], row[3], row[4], row[6])
                                for row in rows)
            source_digest = capture_sha if artifact_name.startswith('issue779-checkpoint1-selector-') else None
            projections.append(completed_validation.ArchiveProjection(
                index, artifact_name, digest, len(raw), expanded, members, source_digest))
        stage = completed_run.CompletedRunStage(321, run_id, attempt, head, completed_run.WORKFLOW,
            (), tree, capture_sha, tuple(range(1, 38)), tuple(bindings), sum(row.size for row in bindings))
        aggregate = completed_validation.AggregateProjection(
            'issue779-checkpoint1-aggregate-v1', head, capture_sha, (), (), 0, 0, 0, 'c' * 64)
        partial = completed_validation.PartialReceiptProjection(
            run_id, attempt, capture_sha, 1, 'd' * 64, tuple(range(1, 37)), (), 'e' * 64)
        inventory = completed_validation.ParsedCompletedRunInventory(
            321, run_id, attempt, head, tree, capture_sha, 1, tuple(projections), aggregate, partial)
        return stage, inventory, capture

    def _stage(self, **kwargs):
        defaults = dict(stage_directory=self.stage_root, source_capture=self.capture,
                        private_parent=self.parent, deadline=time.monotonic() + 10)
        defaults.update(kwargs)
        return subject.stage_native_inputs(self.stage, self.inventory, **defaults)

    def test_stages_exact_24_fixed_leaves_and_custody(self):
        data = self._stage()
        self.assertEqual(24, len(data.files))
        self.assertEqual(set(subject.EXPECTED_OUTPUTS), {row.relative_path for row in data.files})
        self.assertFalse(data.validator_executed)
        self.assertFalse(data.native_acceptance)
        self.assertFalse(data.qualification_complete)
        with self.assertRaises(FrozenInstanceError):
            data.native_acceptance = True
        for row in data.files:
            body = (data.root / row.relative_path).read_bytes()
            self.assertEqual(row.sha256, hashlib.sha256(body).hexdigest())
            self.assertEqual(row.inode, (data.root / row.relative_path).stat().st_ino)
        subject.verify_native_inputs(data, self.stage, deadline=time.monotonic() + 10)
        subject.cleanup_native_inputs(data)
        self.assertEqual([], list(self.parent.iterdir()))

    def test_capture_mismatch_and_inventory_projection_reject_before_output(self):
        with self.assertRaisesRegex(ValueError, 'capture-binding'):
            self._stage(source_capture=b'{}')
        altered = list(self.inventory.archives)
        selected = next(i for i, row in enumerate(altered)
                        if row.artifact_name.startswith('issue779-checkpoint3-application-'))
        altered[selected] = completed_validation.ArchiveProjection(
            **{**altered[selected].__dict__, 'members': ()})
        inventory = completed_validation.ParsedCompletedRunInventory(
            self.inventory.repository_id, self.inventory.run_id, self.inventory.run_attempt,
            self.inventory.head_sha, self.inventory.source_tree, self.inventory.source_capture_sha256,
            self.inventory.source_count, tuple(altered), self.inventory.aggregate, self.inventory.partial_receipt)
        with self.assertRaises(ValueError):
            subject.stage_native_inputs(self.stage, inventory, self.stage_root, self.capture,
                                       self.parent, deadline=time.monotonic() + 10)
        self.assertEqual([], list(self.parent.iterdir()))

    def test_wrong_private_path_and_missing_selected_member_reject(self):
        with self.assertRaisesRegex(ValueError, 'private-mode'):
            wrong = self.base / 'loose'
            wrong.mkdir(mode=0o755)
            wrong.chmod(0o755)
            subject.stage_native_inputs(self.stage, self.inventory, self.stage_root, self.capture,
                                        wrong, deadline=time.monotonic() + 10)
        self.assertEqual([], list(self.parent.iterdir()))

    def test_stage_parent_overlap_and_nonempty_parent_reject(self):
        with self.assertRaisesRegex(ValueError, 'stage-parent-overlap'):
            subject.stage_native_inputs(self.stage, self.inventory, self.stage_root, self.capture,
                                        self.stage_root, deadline=time.monotonic() + 10)
        occupied = self.base / 'occupied'
        occupied.mkdir(mode=0o700)
        (occupied / 'keep').write_bytes(b'caller data')
        with self.assertRaisesRegex(ValueError, 'private-parent-not-empty'):
            subject.stage_native_inputs(self.stage, self.inventory, self.stage_root, self.capture,
                                        occupied, deadline=time.monotonic() + 10)
        self.assertEqual(b'caller data', (occupied / 'keep').read_bytes())

    def test_missing_selected_member_fails_actual_archive_shape(self):
        role_name = subject._role_names(self.stage)['c2']
        path = self.stage_root / (role_name + '.zip')
        with zipfile.ZipFile(path) as archive:
            members = {name: archive.read(name) for name in archive.namelist()
                       if name != 'native/native-receipt.json'}
        raw = _zip(members)
        path.write_bytes(raw)
        binding_rows = tuple(replace(row, size=len(raw), sha256=hashlib.sha256(raw).hexdigest())
                             if row.name == role_name else row for row in self.stage.artifacts)
        self.stage = replace(self.stage, artifacts=binding_rows,
                             total_archive_bytes=sum(row.size for row in binding_rows))
        projection_rows = []
        for row in self.inventory.archives:
            if row.artifact_name == role_name:
                zrows, expanded = completed_validation._zip_directory(raw)
                projection_rows.append(replace(row, archive_sha256=hashlib.sha256(raw).hexdigest(),
                    archive_bytes=len(raw), expanded_bytes=expanded,
                    members=tuple(completed_validation.MemberProjection(item[0], item[3], item[4], item[6])
                                  for item in zrows)))
            else:
                projection_rows.append(row)
        self.inventory = replace(self.inventory, archives=tuple(projection_rows))
        with self.assertRaisesRegex(ValueError, 'c2-required-members'):
            self._stage()
        self.assertEqual([], list(self.parent.iterdir()))

    def test_declared_total_limit_rejects_before_decompression(self):
        from unittest.mock import patch
        with patch.object(subject, 'MAX_INPUT_BYTES', 1), \
             patch.object(subject.completed_validation, '_member_bodies') as decompress:
            with self.assertRaisesRegex(ValueError, 'selected-total-preflight'):
                self._stage()
            decompress.assert_not_called()

    def test_partial_write_failure_cleans_owned_nodes_and_preserves_exception(self):
        from unittest.mock import patch
        failure = RuntimeError('write failed')
        original = subject.completed_c1._write_bytes
        calls = 0
        def fail_second(*args, **kwargs):
            nonlocal calls
            calls += 1
            if calls == 2:
                raise failure
            return original(*args, **kwargs)
        with patch.object(subject.completed_c1, '_write_bytes', side_effect=fail_second):
            with self.assertRaisesRegex(RuntimeError, 'write failed') as caught:
                self._stage()
        self.assertIs(failure, caught.exception)
        self.assertEqual([], list(self.parent.iterdir()))

    def test_deadline_after_final_write_cleans_owned_nodes(self):
        from unittest.mock import patch
        original = subject._remaining
        calls = 0
        def expire_after_writes(deadline):
            nonlocal calls
            calls += 1
            if calls == 34:
                raise ValueError('completed-native-inputs-deadline')
            return original(deadline)
        with patch.object(subject, '_remaining', side_effect=expire_after_writes):
            with self.assertRaisesRegex(ValueError, 'completed-native-inputs-deadline'):
                self._stage()
        self.assertEqual([], list(self.parent.iterdir()))

    def test_partial_failure_with_substituted_node_preserves_primary_and_unknown_node(self):
        from unittest.mock import patch
        failure = RuntimeError('primary write failure')
        moved = self.base / 'moved-owned-file'
        original = subject.completed_c1._write_bytes
        calls = 0
        def sabotage_second(path, raw, **kwargs):
            nonlocal calls
            calls += 1
            if calls == 2:
                first = self.parent / subject.OUTPUT_DIRECTORY / subject.EXPECTED_OUTPUTS[0]
                first.rename(moved)
                first.write_bytes(b'unknown replacement')
                first.chmod(0o600)
                raise failure
            return original(path, raw, **kwargs)
        with patch.object(subject.completed_c1, '_write_bytes', side_effect=sabotage_second):
            with self.assertRaisesRegex(RuntimeError, 'primary write failure') as caught:
                self._stage()
        self.assertIs(failure, caught.exception)
        self.assertEqual(b'unknown replacement',
                         (self.parent / subject.OUTPUT_DIRECTORY / subject.EXPECTED_OUTPUTS[0]).read_bytes())
        self.assertEqual(self.capture, moved.read_bytes())

    def test_cleanup_refuses_substituted_output_and_does_not_remove_parent(self):
        data = self._stage()
        target = data.root / data.files[0].relative_path
        saved = self.base / 'owned-before-substitution'
        target.rename(saved)
        target.write_bytes(b'caller substitution')
        target.chmod(0o600)
        with self.assertRaises(ValueError):
            subject.cleanup_native_inputs(data)
        self.assertTrue(self.parent.exists())
        self.assertEqual(b'caller substitution', target.read_bytes())
        self.assertEqual(b'{}', saved.read_bytes())

    def test_readback_rejects_same_inode_changed_bytes_and_extra_tree_member(self):
        data = self._stage()
        path = data.root / 'native-sdk.json'
        path.write_bytes(b'[]')
        with self.assertRaisesRegex(ValueError, 'file-binding'):
            subject.verify_native_inputs(data, self.stage, deadline=time.monotonic() + 10)
        path.write_bytes(b'{}')
        extra = data.root / 'producer-roots' / 'extra'
        extra.write_bytes(b'unexpected')
        with self.assertRaisesRegex(ValueError, 'verification-directory-inventory'):
            subject.verify_native_inputs(data, self.stage, deadline=time.monotonic() + 10)
        extra.unlink()
        subject.verify_native_inputs(data, self.stage, deadline=time.monotonic() + 10)
        subject.cleanup_native_inputs(data)

    def test_readback_rejects_same_content_substitution_and_directory_link(self):
        data = self._stage()
        target = data.root / 'native-sdk.json'
        saved = self.base / 'saved-sdk'
        target.rename(saved)
        target.write_bytes(saved.read_bytes())
        target.chmod(0o600)
        with self.assertRaisesRegex(ValueError, 'verification-file-custody'):
            subject.verify_native_inputs(data, self.stage, deadline=time.monotonic() + 10)
        target.unlink()
        saved.rename(target)
        directory = data.root / 'producer-roots'
        saved_directory = self.base / 'saved-producer'
        directory.rename(saved_directory)
        directory.symlink_to(saved_directory, target_is_directory=True)
        with self.assertRaisesRegex(ValueError, 'verification-directory'):
            subject.verify_native_inputs(data, self.stage, deadline=time.monotonic() + 10)
        directory.unlink()
        saved_directory.rename(directory)
        subject.cleanup_native_inputs(data)

    def test_readback_rejects_changed_projection_and_expired_deadline(self):
        data = self._stage()
        for changed in (replace(data, files=data.files[:-1]),
                        replace(data, files=(data.files[0],) * len(data.files)),
                        replace(data, owned_nodes=data.owned_nodes + (data.owned_nodes[0],)),
                        replace(data, source_capture_sha256='0' * 64),
                        replace(data, run_id=data.run_id + 1),
                        replace(data, run_attempt=True),
                        replace(data, head_sha='0' * 40),
                        replace(data, native_acceptance=True)):
            with self.subTest(data=changed), self.assertRaises(ValueError):
                subject.verify_native_inputs(changed, self.stage, deadline=time.monotonic() + 10)
        with self.assertRaisesRegex(ValueError, 'deadline'):
            subject.verify_native_inputs(data, self.stage, deadline=time.monotonic() - 1)
        subject.cleanup_native_inputs(data)

    def test_readback_pins_original_inode_inside_open(self):
        from unittest.mock import patch
        data = self._stage()
        target = data.root / 'native-sdk.json'
        moved = self.base / 'moved-before-open'
        real_open = subject.completed_c1.os.open
        substituted = False
        def replace_before_open(path, flags, *args, **kwargs):
            nonlocal substituted
            if Path(path) == target and not substituted:
                substituted = True
                target.rename(moved)
                target.write_bytes(moved.read_bytes())
                target.chmod(0o600)
            return real_open(path, flags, *args, **kwargs)
        with patch.object(subject.completed_c1.os, 'open', side_effect=replace_before_open):
            with self.assertRaisesRegex(ValueError, 'file-shape'):
                subject.verify_native_inputs(data, self.stage, deadline=time.monotonic() + 10)
        self.assertTrue(substituted)
        target.unlink()
        moved.rename(target)
        subject.cleanup_native_inputs(data)


if __name__ == '__main__':
    unittest.main()
