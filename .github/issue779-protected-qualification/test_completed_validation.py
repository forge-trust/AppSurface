"""Synthetic tests for the non-authorizing completed-run inventory slice."""
from __future__ import annotations

import hashlib
import io
import json
import os
from pathlib import Path
import stat
import struct
import sys
import tempfile
import time
import unittest
import zipfile

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / '.github/issue779-protected-qualification'))
import completed_run
import completed_validation as validation
import entrypoint


def _json(value):
    return json.dumps(value, sort_keys=True, separators=(',', ':')).encode()


def _zip(members):
    output = io.BytesIO()
    with zipfile.ZipFile(output, 'w', compression=zipfile.ZIP_STORED) as archive:
        for name, body in members.items():
            info = zipfile.ZipInfo(name)
            info.create_system = 3
            mode = stat.S_IFDIR | 0o700 if name.endswith('/') else stat.S_IFREG | 0o600
            info.external_attr = mode << 16
            archive.writestr(info, body)
    return output.getvalue()


def _blob(raw):
    return hashlib.sha1(b'blob ' + str(len(raw)).encode() + b'\0' + raw).hexdigest()


class CompletedValidationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.stage_root = Path(self.temp.name).resolve() / 'stage'
        self.stage_root.mkdir(mode=0o700)
        os.chmod(self.stage_root, 0o700)
        self.head = 'a' * 40
        self.tree = 'b' * 40
        self.run_id = 12345
        self.attempt = 1
        self.workflow_bytes = {
            completed_run.WORKFLOW: b'protected workflow bytes',
            completed_run.RUNTIME_WORKFLOW: b'runtime workflow bytes',
        }
        files = {}
        for path, raw in self.workflow_bytes.items():
            files[path] = {'sha256': hashlib.sha256(raw).hexdigest(), 'git_blob': _blob(raw), 'mode': '0644'}
        self.capture = _json({'schema': 'issue779-checkpoint2-source-v1', 'source': self.head,
                              'tree': self.tree, 'count': len(files), 'files': files})
        self.capture_sha = hashlib.sha256(self.capture).hexdigest()
        names = completed_run.expected_artifact_names(self.run_id, self.attempt)
        self.aggregate_name = f'issue779-checkpoint1-current-{self.run_id}-{self.attempt}'
        self.partial_name = f'issue779-protected-full-runtime-partial-{self.run_id}-{self.attempt}'
        aggregate = {
            'schema': 'issue779-checkpoint1-aggregate-v1', 'source': self.head,
            'source_capture_sha256': self.capture_sha, 'selectors': list(entrypoint.SELECTORS),
            'cases': list(entrypoint.CASES), 'passed': 16, 'skipped': 0, 'exit': 0,
            'native_acceptance': False, 'coverage_credit': False,
        }
        self.aggregate_body = _json(aggregate)
        self.aggregate_raw = _zip({'aggregate.json': self.aggregate_body})
        self.artifacts = []
        self.raw_by_name = {}
        for index, name in enumerate(names, 1):
            if name == self.aggregate_name:
                raw = self.aggregate_raw
            elif name == self.partial_name:
                receipt = {
                    'schema': 'issue779-protected-full-runtime-partial-v1',
                    'repository': completed_run.REPOSITORY, 'repository_id': 99,
                    'workflow_path': completed_run.WORKFLOW, 'workflow_commit': self.head,
                    'workflow_sha256': hashlib.sha256(self.workflow_bytes[completed_run.WORKFLOW]).hexdigest(),
                    'event': 'workflow_dispatch', 'ref': 'refs/heads/main',
                    'runtime_workflow_path': completed_run.RUNTIME_WORKFLOW,
                    'runtime_workflow_sha256': hashlib.sha256(self.workflow_bytes[completed_run.RUNTIME_WORKFLOW]).hexdigest(),
                    'runtime_workflow_git_blob': files[completed_run.RUNTIME_WORKFLOW]['git_blob'],
                    'runtime_job_result': 'success', 'run_id': self.run_id, 'run_attempt': self.attempt,
                    'observed_run_status': 'in_progress', 'source_capture_sha256': self.capture_sha,
                    'source_count': len(files), 'source_tree': self.tree,
                    'artifact_id': 77, 'artifact_name': self.aggregate_name,
                    'artifact_bytes': len(self.aggregate_raw),
                    'artifact_sha256': hashlib.sha256(self.aggregate_raw).hexdigest(),
                    'aggregate_sha256': hashlib.sha256(self.aggregate_body).hexdigest(),
                    'runtime_job_ids': list(range(1, 37)),
                    **{flag: False for flag in validation.PARTIAL_FLAGS},
                }
                raw = _zip({'source.json': self.capture, 'aggregate.zip': self.aggregate_raw,
                            'aggregate.json': self.aggregate_body,
                            'partial-qualification.json': _json(receipt)})
                self.partial_body = _json(receipt)
            else:
                members = {'source.json': self.capture}
                if name.startswith('issue779-c1-measured-None-'):
                    members['native/commands/00-stdout.log'] = b''
                raw = _zip(members)
            digest = hashlib.sha256(raw).hexdigest()
            staged = name + '.zip'
            path = self.stage_root / staged
            path.write_bytes(raw)
            os.chmod(path, 0o600)
            self.raw_by_name[name] = raw
            self.artifacts.append(completed_run.ArtifactBinding(1000 + index, name, len(raw), digest, staged))
        partial_binding = next(row for row in self.artifacts if row.name == self.partial_name)
        self.partial_body = json.loads(self.partial_body)
        self.partial_body['artifact_id'] = next(row.artifact_id for row in self.artifacts if row.name == self.aggregate_name)
        self.partial_body['artifact_bytes'] = len(self.aggregate_raw)
        self.partial_body['artifact_sha256'] = hashlib.sha256(self.aggregate_raw).hexdigest()
        self.partial_body = _json(self.partial_body)
        partial_raw = _zip({'source.json': self.capture, 'aggregate.zip': self.aggregate_raw,
                            'aggregate.json': self.aggregate_body,
                            'partial-qualification.json': self.partial_body})
        # Replace the synthetic partial archive with one whose receipt binds the actual aggregate artifact ID.
        partial_path = self.stage_root / partial_binding.staged_filename
        partial_path.write_bytes(partial_raw)
        os.chmod(partial_path, 0o600)
        updated = completed_run.ArtifactBinding(partial_binding.artifact_id, partial_binding.name,
                                                len(partial_raw), hashlib.sha256(partial_raw).hexdigest(),
                                                partial_binding.staged_filename)
        self.artifacts = [updated if row.name == self.partial_name else row for row in self.artifacts]
        self.stage = completed_run.CompletedRunStage(
            99, self.run_id, self.attempt, self.head, completed_run.WORKFLOW,
            tuple((path, hashlib.sha256(raw).hexdigest()) for path, raw in self.workflow_bytes.items()),
            self.tree, self.capture_sha, tuple(range(1, 38)), tuple(self.artifacts),
            sum(row.size for row in self.artifacts))

    def tearDown(self):
        self.temp.cleanup()

    def inspect(self, capture=None):
        import time
        return validation.inspect_completed_run(self.stage, self.stage_root,
                                                self.capture if capture is None else capture,
                                                deadline=time.monotonic() + 20)

    def test_projects_exact37_archive_inventory_without_qualification(self):
        result = self.inspect()
        self.assertIsInstance(result, validation.ParsedCompletedRunInventory)
        self.assertEqual(37, len(result.archives))
        names = {archive.artifact_name for archive in result.archives}
        self.assertEqual(14, sum(name.startswith('issue779-c1-measured-') for name in names))
        self.assertEqual(1, sum(name.startswith('issue779-csharp-checkpoint2-') for name in names))
        measured_none = next(archive for archive in result.archives
                             if archive.artifact_name.startswith('issue779-c1-measured-None-'))
        empty_log = next(member for member in measured_none.members
                         if member.path == 'native/commands/00-stdout.log')
        self.assertEqual(0, empty_log.expanded_bytes)
        self.assertEqual(16, result.aggregate.passed)
        self.assertEqual(0, result.aggregate.skipped)
        self.assertTrue(all(value is False for _, value in result.partial_receipt.claims))
        self.assertFalse(hasattr(result, 'qualification_complete'))
        self.assertFalse(hasattr(result, 'trusted'))

    def test_rejects_capture_bytes_that_differ_from_every_archive(self):
        with self.assertRaisesRegex(ValueError, 'completed-validation-source-capture-hash'):
            self.inspect(self.capture + b' ')

    def test_rejects_partial_receipt_claim_before_returning_projection(self):
        receipt = json.loads(self.partial_body)
        receipt['qualification_complete'] = True
        partial_binding = next(row for row in self.artifacts if row.name == self.partial_name)
        raw = _zip({'source.json': self.capture, 'aggregate.zip': self.aggregate_raw,
                    'aggregate.json': self.aggregate_body, 'partial-qualification.json': _json(receipt)})
        path = self.stage_root / partial_binding.staged_filename
        path.write_bytes(raw)
        os.chmod(path, 0o600)
        replacement = completed_run.ArtifactBinding(partial_binding.artifact_id, partial_binding.name,
                                                     len(raw), hashlib.sha256(raw).hexdigest(),
                                                     partial_binding.staged_filename)
        self.artifacts = [replacement if row.name == self.partial_name else row for row in self.artifacts]
        self.stage = completed_run.CompletedRunStage(
            self.stage.repository_id, self.stage.run_id, self.stage.run_attempt, self.stage.head_sha,
            self.stage.workflow_path, self.stage.workflow_sha256s, self.stage.source_tree,
            self.stage.source_capture_sha256, self.stage.job_ids, tuple(self.artifacts),
            sum(row.size for row in self.artifacts))
        with self.assertRaisesRegex(ValueError, 'completed-validation-partial-receipt-fields'):
            self.inspect()

    def test_rejects_extra_partial_member(self):
        partial = next(row for row in self.artifacts if row.name == self.partial_name)
        raw = _zip({'source.json': self.capture, 'aggregate.zip': self.aggregate_raw,
                    'aggregate.json': self.aggregate_body, 'partial-qualification.json': self.partial_body,
                    'unexpected.bin': b'x'})
        path = self.stage_root / partial.staged_filename
        path.write_bytes(raw)
        os.chmod(path, 0o600)
        replacement = completed_run.ArtifactBinding(partial.artifact_id, partial.name, len(raw),
                                                     hashlib.sha256(raw).hexdigest(), partial.staged_filename)
        self.artifacts = [replacement if row.name == self.partial_name else row for row in self.artifacts]
        self.stage = completed_run.CompletedRunStage(
            self.stage.repository_id, self.stage.run_id, self.stage.run_attempt, self.stage.head_sha,
            self.stage.workflow_path, self.stage.workflow_sha256s, self.stage.source_tree,
            self.stage.source_capture_sha256, self.stage.job_ids, tuple(self.artifacts),
            sum(row.size for row in self.artifacts))
        with self.assertRaisesRegex(ValueError, 'completed-validation-partial-exact-members'):
            self.inspect()

    def test_zero_byte_regular_member_is_read_and_crc_checked_when_projected(self):
        raw = _zip({'empty.log': b''})
        rows, expanded = validation._zip_directory(raw)
        self.assertEqual(0, expanded)
        self.assertEqual({'empty.log': b''},
                         validation._member_bodies(raw, rows, {'empty.log'}, time.monotonic() + 5))

        damaged = bytearray(raw)
        central = damaged.index(b'PK\x01\x02')
        struct.pack_into('<I', damaged, central + 16, 1)
        broken_rows, _ = validation._zip_directory(bytes(damaged))
        with self.assertRaisesRegex(ValueError, 'completed-validation-zip-invalid'):
            validation._member_bodies(bytes(damaged), broken_rows, {'empty.log'}, time.monotonic() + 5)

    def test_zip_name_trailing_separator_aliases_are_rejected(self):
        for name in ('folder//', 'folder///', 'a//b/'):
            with self.subTest(name=name), self.assertRaisesRegex(ValueError, 'completed-validation-zip-name'):
                validation._zip_directory(_zip({name: b''}))
        with self.assertRaisesRegex(ValueError, 'completed-validation-zip-duplicate-members'):
            validation._zip_directory(_zip({'folder': b'x', 'folder/': b''}))
        rows, _ = validation._zip_directory(_zip({'folder/': b''}))
        self.assertEqual(('folder/',), tuple(row[0] for row in rows))


if __name__ == '__main__':
    unittest.main()
