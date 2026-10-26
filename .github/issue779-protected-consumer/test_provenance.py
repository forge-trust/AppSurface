"""Bounded archive/schema controls; all inputs are untrusted detached bytes."""
from io import BytesIO
import hashlib
import json
import stat
import struct
import unittest
from unittest import mock
import zipfile

from provenance import preflight_protected_parent


class ProtectedParentProvenanceTests(unittest.TestCase):
    def setUp(self):
        self.plan = b'{"schema":"plan-fixture"}'
        self.manifest = b'{"schema":"manifest-fixture"}'
        self.archive = self.make_archive({'plan.json': self.plan, 'manifest.json': self.manifest})
        self.expectation = {
            'schema': 'issue779-protected-parent-expectation-v1', 'repository': 'forge-trust/AppSurface',
            'run_id': 40200000001, 'run_attempt': 2, 'head_sha': 'a' * 40,
            'workflow_path': '.github/workflows/evidencehost-runtime-proof.yml',
            'workflow_sha': 'b' * 40, 'base_sha': 'c' * 40, 'subject_sha': 'd' * 40,
            'artifact_id': 987654321, 'artifact_name': 'protected-parent-40200000001-2',
            'artifact_sha256': hashlib.sha256(self.archive).hexdigest(), 'artifact_bytes': len(self.archive),
        }
        self.run = {key: self.expectation[key] for key in (
            'repository', 'run_id', 'run_attempt', 'head_sha', 'workflow_path',
            'workflow_sha', 'base_sha', 'subject_sha')}
        self.run['event'] = 'workflow_dispatch'
        self.artifact = {key: self.expectation[key] for key in (
            'repository', 'run_id', 'artifact_id', 'artifact_name')}
        self.artifact.update({'expired': False, 'size_in_bytes': len(self.archive),
                              'digest': 'sha256:' + hashlib.sha256(self.archive).hexdigest()})

    @staticmethod
    def make_archive(files, *, compression=zipfile.ZIP_STORED, modes=None):
        output = BytesIO()
        with zipfile.ZipFile(output, 'w', compression=compression) as archive:
            for name, body in files.items():
                info = zipfile.ZipInfo(name)
                info.compress_type = compression
                info.external_attr = ((modes or {}).get(name, stat.S_IFREG | 0o600) << 16)
                archive.writestr(info, body)
        return output.getvalue()

    def encode(self, value):
        return json.dumps(value, separators=(',', ':')).encode()

    def check(self, *, expectation=None, run=None, artifact=None, archive=None):
        return preflight_protected_parent(
            self.encode(self.expectation if expectation is None else expectation),
            self.encode(self.run if run is None else run),
            self.encode(self.artifact if artifact is None else artifact),
            self.archive if archive is None else archive)

    def test_exact_parent_run_artifact_digest_and_selected_documents(self):
        result = self.check()
        self.assertEqual(self.plan, result.plan_bytes)
        self.assertEqual(self.manifest, result.manifest_bytes)
        self.assertIsInstance(result.plan_bytes, bytes)
        self.assertIsInstance(result.manifest_bytes, bytes)
        self.assertFalse(hasattr(result, 'admission'))

    def test_every_protected_identity_dimension_must_match_actual_metadata(self):
        dimensions = ('repository', 'run_id', 'run_attempt', 'head_sha', 'workflow_path',
                      'workflow_sha', 'base_sha', 'subject_sha')
        for key in dimensions:
            with self.subTest(key=key):
                actual = dict(self.run)
                actual[key] = ('other-owner/OtherRepo' if key == 'repository' else
                               '.github/workflows/other.yml' if key == 'workflow_path' else
                               'e' * 40 if key.endswith('sha') else
                               actual[key] + 1 if type(actual[key]) is int else 'other')
                with self.assertRaises(ValueError): self.check(run=actual)
        for key in ('repository', 'run_id', 'artifact_id', 'artifact_name', 'size_in_bytes'):
            with self.subTest(artifact_key=key):
                actual = dict(self.artifact)
                actual[key] = ('other-owner/OtherRepo' if key == 'repository' else
                               'different-artifact' if key == 'artifact_name' else
                               actual[key] + 1 if type(actual[key]) is int else 'other')
                with self.assertRaises(ValueError): self.check(artifact=actual)

    def test_expectation_rejects_unknown_case_alias_boolean_integer_bad_path_and_digest(self):
        mutations = (
            {'extra': 1}, {'Run_Id': 4}, {'run_id': True}, {'run_attempt': 0},
            {'workflow_path': '.github/workflows/../evil.yml'}, {'workflow_path': '.github/workflows\\bad.yml'},
            {'head_sha': 'A' * 40}, {'artifact_sha256': 'a' * 64}, {'artifact_bytes': True},
            {'artifact_name': '../escape'},
        )
        for mutation in mutations:
            with self.subTest(mutation=mutation):
                value = dict(self.expectation); value.update(mutation)
                with self.assertRaises(ValueError): self.check(expectation=value)

    def test_actual_responses_reject_unknown_event_expiry_and_boolean_numeric_fields(self):
        bad_run = dict(self.run, event='pull_request')
        with self.assertRaises(ValueError): self.check(run=bad_run)
        for key, value in (('expired', True), ('artifact_id', True), ('size_in_bytes', True)):
            with self.subTest(key=key):
                actual = dict(self.artifact); actual[key] = value
                with self.assertRaises(ValueError): self.check(artifact=actual)

    def test_api_artifact_digest_must_bind_expectation_and_download_before_zip_open(self):
        import provenance
        cases = ('missing', 'null', 'malformed', 'wrong-value')
        for case in cases:
            with self.subTest(case=case):
                actual = dict(self.artifact)
                if case == 'missing': actual.pop('digest')
                if case == 'null': actual['digest'] = None
                if case == 'malformed': actual['digest'] = 'SHA256:' + hashlib.sha256(self.archive).hexdigest().upper()
                if case == 'wrong-value': actual['digest'] = 'sha256:' + 'f' * 64
                with mock.patch.object(provenance.zipfile, 'ZipFile', side_effect=AssertionError('ZIP opened')) as opener:
                    with self.assertRaises(ValueError): self.check(artifact=actual)
                    opener.assert_not_called()

    def test_bad_digest_rejects_before_zipfile_open(self):
        import provenance
        wrong = dict(self.expectation, artifact_sha256='f' * 64)
        with mock.patch.object(provenance.zipfile, 'ZipFile', side_effect=AssertionError('ZIP opened')):
            with self.assertRaises(ValueError): self.check(expectation=wrong)

    def _check_archive_bytes(self, archive):
        expectation = dict(self.expectation, artifact_sha256=hashlib.sha256(archive).hexdigest(),
                           artifact_bytes=len(archive))
        artifact = dict(self.artifact, size_in_bytes=len(archive),
                        digest='sha256:' + hashlib.sha256(archive).hexdigest())
        return self.check(expectation=expectation, artifact=artifact, archive=archive)

    def test_two_member_archive_reaches_zipfile_after_bounded_structure_check(self):
        import provenance
        constructor = zipfile.ZipFile
        with mock.patch.object(provenance.zipfile, 'ZipFile', wraps=constructor) as opener:
            result = self._check_archive_bytes(self.archive)
        opener.assert_called_once()
        self.assertEqual(self.plan, result.plan_bytes)
        self.assertEqual(self.manifest, result.manifest_bytes)

    def test_extra_or_forged_central_entries_reject_before_zipfile(self):
        import provenance
        three = self.make_archive({'plan.json': self.plan, 'manifest.json': self.manifest,
                                   'extra.json': b'{}'})
        forged = bytearray(three)
        eocd = forged.rfind(b'PK\x05\x06')
        self.assertGreaterEqual(eocd, 0)
        struct.pack_into('<H', forged, eocd + 8, 2)
        struct.pack_into('<H', forged, eocd + 10, 2)
        for archive in (three, bytes(forged)):
            with self.subTest(length=len(archive)):
                with mock.patch.object(provenance.zipfile, 'ZipFile',
                                       side_effect=AssertionError('ZIP opened')) as opener:
                    with self.assertRaises(ValueError): self._check_archive_bytes(archive)
                    opener.assert_not_called()

    def test_truncated_multidisk_zip64_and_oversized_central_directory_reject_before_zipfile(self):
        import provenance
        eocd = self.archive.rfind(b'PK\x05\x06')
        multi_disk = bytearray(self.archive); struct.pack_into('<H', multi_disk, eocd + 4, 1)
        zip64_count = bytearray(self.archive); struct.pack_into('<H', zip64_count, eocd + 10, 0xFFFF)
        zip64_offset = bytearray(self.archive); struct.pack_into('<I', zip64_offset, eocd + 16, 0xFFFFFFFF)
        oversized_cd = bytearray(self.archive); struct.pack_into('<I', oversized_cd, eocd + 12, 16 * 1024 + 1)
        malformed_extra = bytearray(self.archive)
        central = malformed_extra.find(b'PK\x01\x02')
        self.assertGreaterEqual(central, 0)
        struct.pack_into('<H', malformed_extra, central + 30, 0xFFFF)
        cases = (self.archive[:-1], bytes(multi_disk), bytes(zip64_count),
                 bytes(zip64_offset), bytes(oversized_cd), bytes(malformed_extra))
        for archive in cases:
            with self.subTest(length=len(archive)):
                with mock.patch.object(provenance.zipfile, 'ZipFile',
                                       side_effect=AssertionError('ZIP opened')) as opener:
                    with self.assertRaises(ValueError): self._check_archive_bytes(archive)
                    opener.assert_not_called()

    def test_only_exact_regular_unencrypted_archive_members_are_selected(self):
        unsafe = (
            {'../plan.json': self.plan, 'manifest.json': self.manifest},
            {'/plan.json': self.plan, 'manifest.json': self.manifest},
            {'PLAN.json': self.plan, 'manifest.json': self.manifest},
            {'..\\plan.json': self.plan, 'manifest.json': self.manifest},
        )
        for files in unsafe:
            with self.subTest(names=tuple(files)):
                archive = self.make_archive(files)
                expectation = dict(self.expectation, artifact_sha256=hashlib.sha256(archive).hexdigest(),
                                   artifact_bytes=len(archive))
                artifact = dict(self.artifact, size_in_bytes=len(archive),
                                digest='sha256:' + hashlib.sha256(archive).hexdigest())
                with self.assertRaises(ValueError): self.check(expectation=expectation, artifact=artifact, archive=archive)
        symlink_archive = self.make_archive({'plan.json': self.plan, 'manifest.json': self.manifest},
                                            modes={'plan.json': stat.S_IFLNK | 0o777})
        expectation = dict(self.expectation, artifact_sha256=hashlib.sha256(symlink_archive).hexdigest(),
                           artifact_bytes=len(symlink_archive))
        artifact = dict(self.artifact, size_in_bytes=len(symlink_archive),
                        digest='sha256:' + hashlib.sha256(symlink_archive).hexdigest())
        with self.assertRaises(ValueError): self.check(expectation=expectation, artifact=artifact, archive=symlink_archive)

    def test_duplicate_json_members_reject(self):
        raw = self.encode(self.expectation).replace(b'"run_id":40200000001,', b'"run_id":40200000001,"run_id":2,')
        with self.assertRaises(ValueError):
            preflight_protected_parent(raw, self.encode(self.run), self.encode(self.artifact), self.archive)

    def test_deep_json_is_normalized_to_a_fixed_rejection(self):
        deep = b'[' * 1500 + b'0' + b']' * 1500
        with self.assertRaisesRegex(ValueError, 'protected-parent-expectation-json'):
            preflight_protected_parent(deep, self.encode(self.run), self.encode(self.artifact), self.archive)


if __name__ == '__main__':
    unittest.main()
