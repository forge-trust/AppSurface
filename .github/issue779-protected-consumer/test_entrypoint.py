"""Detached workflow/context selection controls; provider environment here is comparison data only."""
from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest import mock

import dispatch
import entrypoint


class ProtectedEntrypointControls(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix='parent-record-')
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        (self.root / 'records').mkdir()
        self.head = 'a' * 40
        self.environment = {
            'GITHUB_ACTIONS': 'true', 'GITHUB_REPOSITORY': 'forge-trust/AppSurface',
            'GITHUB_EVENT_NAME': 'workflow_dispatch', 'GITHUB_REF': 'refs/heads/main',
            'GITHUB_REF_PROTECTED': 'true', 'GITHUB_REPOSITORY_ID': '12345',
            'GITHUB_SHA': self.head, 'GITHUB_WORKFLOW_SHA': self.head,
            'GITHUB_WORKFLOW_REF': 'forge-trust/AppSurface/.github/workflows/evidencehost-protected-parent.yml@refs/heads/main',
        }
        self.context = b'{"detached":"reviewed fixture bytes, not runtime authority"}'
        self.context_path = self.root / 'records' / 'reviewed.json'
        self.context_path.write_bytes(self.context)
        self.record = {'id': 'reviewed', 'context_sha256': hashlib.sha256(self.context).hexdigest(),
                       'repository_id': 12345}
        self.registry = {'schema': entrypoint.REGISTRY_SCHEMA, 'records': [self.record]}
        self.write_registry()
        self.addCleanup(mock.patch.stopall)
        mock.patch.object(entrypoint, 'ROOT', self.root).start()
        self.dispatcher = mock.patch.object(dispatch, 'dispatch_protected_parent',
                                            return_value=dispatch.DispatchResult('semantic-gate-denied', 1, 'fixed')).start()
        self.repository_api = mock.patch.object(entrypoint.github_parent, '_api_json',
                                                return_value={'full_name': 'forge-trust/AppSurface',
                                                              'id': 12345, 'default_branch': 'main'}).start()

    def write_registry(self):
        (self.root / 'protected-records.json').write_text(json.dumps(self.registry))

    def run_record(self, record='reviewed', environment=None):
        return entrypoint.run_reviewed_record(record, self.environment if environment is None else environment,
                                              'private-api-token', dotnet_host=Path('/protected/dotnet'),
                                              checkout_head=self.head)

    def test_actual_fixed_record_bytes_are_digest_bound_before_the_dispatch_call(self):
        result = self.run_record()
        self.assertEqual('semantic-gate-denied', result.status)
        self.assertEqual((self.context, 'private-api-token'), self.dispatcher.call_args.args)
        self.assertEqual(Path('/protected/dotnet'), self.dispatcher.call_args.kwargs['dotnet_host'])
        self.assertGreater(self.dispatcher.call_args.kwargs['total_timeout'], 0)
        self.assertLessEqual(self.dispatcher.call_args.kwargs['total_timeout'], 150)
        self.assertEqual('https://api.github.com/repos/forge-trust/AppSurface', self.repository_api.call_args.args[0])

    def test_every_provider_workflow_binding_rejects_before_opening_registry(self):
        for key, wrong in (
                ('GITHUB_ACTIONS', 'false'), ('GITHUB_REPOSITORY', 'attacker/AppSurface'),
                ('GITHUB_EVENT_NAME', 'pull_request'), ('GITHUB_REF', 'refs/heads/candidate'),
                ('GITHUB_REF_PROTECTED', 'false'), ('GITHUB_SHA', 'b' * 40),
                ('GITHUB_WORKFLOW_SHA', 'b' * 40), ('GITHUB_WORKFLOW_REF', 'forged')):
            with self.subTest(key=key), mock.patch.object(entrypoint, '_read_fixed_file') as reader:
                with self.assertRaises(ValueError):
                    self.run_record(environment=dict(self.environment, **{key: wrong}))
                reader.assert_not_called()
        self.dispatcher.assert_not_called()

    def test_unregistered_and_unsafe_identifiers_do_not_reach_dispatch(self):
        self.registry['records'] = []
        self.write_registry()
        for value in ('reviewed', '../reviewed', '/reviewed', 'ReviewED', '', True, 'a' * 65):
            with self.subTest(value=value), self.assertRaises(ValueError):
                self.run_record(value)
        self.dispatcher.assert_not_called()

    def test_wrong_repository_id_and_changed_record_bytes_reject(self):
        with self.assertRaises(ValueError):
            self.run_record(environment=dict(self.environment, GITHUB_REPOSITORY_ID='99999'))
        self.context_path.write_bytes(self.context + b' ')
        with self.assertRaises(ValueError):
            self.run_record()
        self.dispatcher.assert_not_called()

    def test_registry_is_closed_typed_and_rejects_duplicate_json_members(self):
        wrong = [dict(self.registry, extra=True), {'schema': entrypoint.REGISTRY_SCHEMA, 'records': {}},
                 {'schema': entrypoint.REGISTRY_SCHEMA, 'records': [dict(self.record, repository_id=True)]},
                 {'schema': entrypoint.REGISTRY_SCHEMA, 'records': [dict(self.record, context_sha256='f' * 63)]},
                 {'schema': entrypoint.REGISTRY_SCHEMA, 'records': [self.record, self.record]}]
        for value in wrong:
            with self.subTest(value=value), self.assertRaises(ValueError):
                entrypoint._registry(json.dumps(value).encode())
        for raw in (b'{"schema":"x","Schema":"x","records":[]}',
                    b'{"schema":"x","schema":"x","records":[]}', b'null'):
            with self.subTest(raw=raw), self.assertRaises(ValueError):
                entrypoint._registry(raw)
        self.dispatcher.assert_not_called()

    def test_record_symlink_hardlink_and_directory_are_rejected_without_outside_changes(self):
        outside = self.root / 'outside'
        outside.write_bytes(self.context)
        for kind in ('symlink', 'hardlink', 'directory'):
            with self.subTest(kind=kind):
                self.context_path.unlink()
                if kind == 'symlink':
                    self.context_path.symlink_to(outside)
                elif kind == 'hardlink':
                    os.link(outside, self.context_path)
                else:
                    self.context_path.mkdir()
                with self.assertRaises((ValueError, OSError)):
                    self.run_record()
                self.assertEqual(self.context, outside.read_bytes())
                if kind == 'directory':
                    self.context_path.rmdir()
                else:
                    self.context_path.unlink()
                self.context_path.write_bytes(self.context)
        self.dispatcher.assert_not_called()

    def test_record_directory_cannot_redirect_to_an_outside_tree(self):
        actual = self.root / 'records'
        moved = self.root / 'outside-records'
        actual.rename(moved)
        actual.symlink_to(moved, target_is_directory=True)
        with self.assertRaises(ValueError):
            self.run_record()
        self.dispatcher.assert_not_called()

    def test_read_bound_and_named_file_substitution_are_checked(self):
        with self.assertRaises(ValueError):
            entrypoint._read_fixed_file(self.context_path, len(self.context) - 1)
        real_stat = entrypoint.os.stat
        outside = self.root / 'other'
        outside.write_bytes(self.context)

        def changed_name(path, **kwargs):
            return real_stat(outside, **kwargs) if path == self.context_path else real_stat(path, **kwargs)

        with mock.patch.object(entrypoint.os, 'stat', side_effect=changed_name):
            with self.assertRaises(ValueError):
                self.run_record()
        self.dispatcher.assert_not_called()

    def test_missing_or_malformed_token_rejects_without_returning_secret_text(self):
        for token in (None, '', 'secret\ncanary', 'x' * 4097):
            with self.subTest(token_type=type(token).__name__), self.assertRaisesRegex(ValueError, '^protected-entry-token$'):
                entrypoint.run_reviewed_record('reviewed', self.environment, token,
                                               dotnet_host=Path('/protected/dotnet'), checkout_head=self.head)
        self.dispatcher.assert_not_called()

    def test_authenticated_repository_metadata_must_bind_the_canonical_default_branch_and_id(self):
        for wrong in ({'full_name': 'other/AppSurface'}, {'id': True}, {'id': 99999},
                      {'default_branch': 'release'}, {'default_branch': None}):
            value = {'full_name': 'forge-trust/AppSurface', 'id': 12345, 'default_branch': 'main'}
            value.update(wrong)
            self.repository_api.return_value = value
            with self.subTest(wrong=wrong), self.assertRaisesRegex(ValueError, '^protected-entry-default-repository$'):
                self.run_record()
        self.dispatcher.assert_not_called()

    def test_repository_resolution_cannot_renew_the_original_entrypoint_deadline(self):
        current = [100.0]

        def metadata(*_):
            current[0] = 251.0
            return {'full_name': 'forge-trust/AppSurface', 'id': 12345, 'default_branch': 'main'}

        with mock.patch.object(entrypoint.time, 'monotonic', side_effect=lambda: current[0]):
            self.repository_api.side_effect = metadata
            with self.assertRaises(ValueError):
                self.run_record()
        self.dispatcher.assert_not_called()


if __name__ == '__main__':
    unittest.main()
