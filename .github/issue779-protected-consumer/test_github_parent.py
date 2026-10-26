"""Detached HTTP fixtures for fixed GitHub parent resolution; they assert no real proof."""
from io import BytesIO
import base64
import hashlib
import json
import stat
import unittest
from unittest import mock
import zipfile

import github_parent


class _Response:
    def __init__(self, body=b'', *, status=200, headers=None):
        self.body = body
        self.status = status
        self.headers = headers or {}
        self.position = 0

    def read(self, count=-1):
        if count < 0:
            count = len(self.body) - self.position
        result = self.body[self.position:self.position + count]
        self.position += len(result)
        return result

    def getcode(self):
        return self.status

    def __enter__(self):
        return self

    def __exit__(self, *_):
        return False


class GitHubProtectedParentResolverTests(unittest.TestCase):
    def setUp(self):
        self.repository = 'forge-trust/AppSurface'
        self.run_id = 40200000001
        self.run_attempt = 2
        self.head = 'a' * 40
        self.workflow_source_commit = 'e' * 40
        self.workflow = b'name: protected fixture\non:\n  workflow_dispatch:\n'
        self.workflow_sha = self.git_blob_sha(self.workflow)
        self.plan = b'{"schema":"plan-fixture"}'
        self.manifest = b'{"schema":"manifest-fixture"}'
        self.archive = self.make_archive({'plan.json': self.plan, 'manifest.json': self.manifest})
        self.artifact_sha = hashlib.sha256(self.archive).hexdigest()
        self.expectation = {
            'schema': 'issue779-protected-parent-expectation-v1', 'repository': self.repository,
            'run_id': self.run_id, 'run_attempt': self.run_attempt, 'head_sha': self.head,
            'workflow_path': '.github/workflows/protected.yml', 'workflow_sha': self.workflow_sha,
            'base_sha': 'b' * 40, 'subject_sha': 'c' * 40,
            'artifact_id': 987654321, 'artifact_name': 'protected-parent-fixture',
            'artifact_sha256': self.artifact_sha, 'artifact_bytes': len(self.archive),
        }
        self.expectation_bytes = self.encode(self.expectation)
        self.protected_facts = {key: self.expectation[key] for key in
                                ('workflow_path', 'workflow_sha', 'base_sha', 'subject_sha')}
        self.protected_facts['workflow_source_commit'] = self.workflow_source_commit

    @staticmethod
    def git_blob_sha(body):
        return hashlib.sha1(b'blob ' + str(len(body)).encode('ascii') + b'\0' + body).hexdigest()

    @staticmethod
    def encode(value):
        return json.dumps(value, separators=(',', ':')).encode('utf-8')

    @staticmethod
    def make_archive(files):
        output = BytesIO()
        with zipfile.ZipFile(output, 'w', compression=zipfile.ZIP_STORED) as archive:
            for name, body in files.items():
                info = zipfile.ZipInfo(name)
                info.external_attr = (stat.S_IFREG | 0o600) << 16
                archive.writestr(info, body)
        return output.getvalue()

    def api_documents(self, *, run_overrides=None, protected_content_overrides=None,
                      actual_head_content_overrides=None, artifact_overrides=None, total_count=1):
        run = {
            'id': self.run_id, 'run_attempt': self.run_attempt, 'head_sha': self.head,
            'event': 'workflow_dispatch', 'status': 'completed', 'conclusion': 'success',
            'workflow_id': 8123, 'path': self.expectation['workflow_path'] + '@refs/heads/main',
            'repository': {'id': 7001, 'full_name': self.repository},
            'head_repository': {'id': 7001, 'full_name': self.repository},
        }
        run.update(run_overrides or {})
        content = {
            'path': self.expectation['workflow_path'], 'sha': self.workflow_sha,
            'encoding': 'base64', 'content': base64.b64encode(self.workflow).decode('ascii'),
        }
        protected_content = dict(content)
        protected_content.update(protected_content_overrides or {})
        actual_head_content = dict(content)
        actual_head_content.update(actual_head_content_overrides or {})
        artifact = {
            'id': self.expectation['artifact_id'], 'name': self.expectation['artifact_name'],
            'expired': False, 'size_in_bytes': len(self.archive),
            'digest': 'sha256:' + self.artifact_sha,
            'workflow_run': {'id': self.run_id, 'repository_id': 7001,
                             'head_repository_id': 7001, 'head_sha': self.head},
        }
        artifact.update(artifact_overrides or {})
        return {
            '/actions/runs/{}/attempts/{}'.format(self.run_id, self.run_attempt): self.encode(run),
            '/contents/{}?ref={}'.format(self.expectation['workflow_path'], self.workflow_source_commit):
                self.encode(protected_content),
            '/contents/{}?ref={}'.format(self.expectation['workflow_path'], self.head):
                self.encode(actual_head_content),
            '/actions/runs/{}/artifacts?per_page=100&page=1'.format(self.run_id):
                self.encode({'total_count': total_count, 'artifacts': [artifact]}),
        }

    def resolver_with(self, *, documents=None, redirect='https://release-assets.githubusercontent.com/signed/archive'):
        documents = documents or self.api_documents()
        seen = []

        def transport(request, timeout):
            url = request.full_url
            parsed = github_parent.urlsplit(url)
            prefix = '/repos/' + self.repository
            api_path = parsed.path[len(prefix):] if parsed.path.startswith(prefix) else parsed.path
            key = api_path + (('?' + parsed.query) if parsed.query else '')
            seen.append((url, request.get_header('Authorization'), timeout))
            if key.endswith('/actions/artifacts/{}/zip'.format(self.expectation['artifact_id'])):
                return _Response(status=302, headers={'Location': redirect})
            if parsed.hostname == 'release-assets.githubusercontent.com' or parsed.hostname.endswith('.blob.core.windows.net'):
                return _Response(self.archive)
            try:
                return _Response(documents[key])
            except KeyError:
                raise AssertionError('unexpected fixed endpoint: ' + key)

        return transport, seen

    def resolve(self, total_timeout=30):
        transport, seen = self.resolver_with()
        with mock.patch.object(github_parent, '_open_request', side_effect=transport):
            result = github_parent.resolve_protected_parent(
                self.expectation_bytes, self.protected_facts, 'fixture-token', total_timeout=total_timeout)
        return result, seen

    def test_resolves_exact_run_workflow_artifact_and_two_documents(self):
        result, seen = self.resolve()
        self.assertEqual((self.plan, self.manifest), (result.plan_bytes, result.manifest_bytes))
        self.assertEqual((self.run_id, self.run_attempt, self.expectation['artifact_id']),
                         (result.run_id, result.run_attempt, result.artifact_id))
        self.assertEqual(self.head, result.head_sha)
        self.assertEqual(self.workflow_source_commit, result.workflow_source_commit)
        self.assertEqual(self.workflow_sha, result.workflow_sha)
        self.assertEqual(6, len(seen))
        self.assertTrue(all(item[1] == 'Bearer fixture-token' for item in seen[:5]))
        self.assertIsNone(seen[5][1])
        self.assertTrue(all(item[0].startswith('https://api.github.com/') for item in seen[:5]))
        self.assertIn('ref=' + self.workflow_source_commit, seen[1][0])
        self.assertNotIn('ref=' + self.head, seen[1][0])
        self.assertIn('ref=' + self.head, seen[2][0])

    def test_run_identity_status_and_conclusion_mismatches_stop_before_artifacts(self):
        for key, value in (('id', self.run_id + 1), ('run_attempt', self.run_attempt + 1),
                           ('head_sha', 'd' * 40), ('event', 'pull_request'),
                           ('status', 'in_progress'), ('conclusion', 'failure'),
                           ('path', '.github/workflows/other.yml@refs/heads/main'),
                           ('repository', {'id': 7001, 'full_name': 'other/Repo'}),
                           ('head_repository', {'id': 7002, 'full_name': 'other/Repo'})):
            with self.subTest(key=key):
                docs = self.api_documents(run_overrides={key: value})
                transport, seen = self.resolver_with(documents=docs)
                with mock.patch.object(github_parent, '_open_request', side_effect=transport):
                    with self.assertRaises(ValueError):
                        github_parent.resolve_protected_parent(
                            self.expectation_bytes, self.protected_facts, 'fixture-token', total_timeout=30)
                self.assertEqual(1, len(seen))

    def test_workflow_path_and_content_at_separate_protected_commit_must_match_pin(self):
        for docs in (self.api_documents(run_overrides={
                         'path': '.github/workflows/other.yml@refs/heads/main'}),
                     self.api_documents(protected_content_overrides={
                         'content': base64.b64encode(b'changed').decode('ascii')}),
                     self.api_documents(actual_head_content_overrides={
                         'content': base64.b64encode(b'malicious candidate workflow').decode('ascii')})):
            with self.subTest(docs=docs):
                transport, seen = self.resolver_with(documents=docs)
                with mock.patch.object(github_parent, '_open_request', side_effect=transport):
                    with self.assertRaises(ValueError):
                        github_parent.resolve_protected_parent(
                            self.expectation_bytes, self.protected_facts, 'fixture-token', total_timeout=30)
                self.assertLessEqual(len(seen), 3)

    def test_artifact_run_repository_expiry_digest_and_size_are_checked_before_download(self):
        cases = (
            {'workflow_run': {'id': self.run_id + 1, 'repository_id': 7001, 'head_sha': self.head}},
            {'workflow_run': {'id': self.run_id, 'repository_id': 7002, 'head_sha': self.head}},
            {'workflow_run': {'id': self.run_id, 'repository_id': 7001,
                              'head_repository_id': 7002, 'head_sha': self.head}},
            {'expired': True}, {'digest': 'sha256:' + 'f' * 64},
            {'size_in_bytes': len(self.archive) + 1}, {'name': 'different-artifact'},
        )
        for update in cases:
            with self.subTest(update=update):
                docs = self.api_documents(artifact_overrides=update)
                transport, seen = self.resolver_with(documents=docs)
                with mock.patch.object(github_parent, '_open_request', side_effect=transport):
                    with self.assertRaises(ValueError):
                        github_parent.resolve_protected_parent(
                            self.expectation_bytes, self.protected_facts, 'fixture-token', total_timeout=30)
                self.assertFalse(any('/zip' in item[0] for item in seen))

    def test_redirect_origin_hops_and_archive_size_are_bounded_without_token_forwarding(self):
        transport, seen = self.resolver_with(redirect='https://attacker.example/archive')
        with mock.patch.object(github_parent, '_open_request', side_effect=transport):
            with self.assertRaises(ValueError):
                github_parent.resolve_protected_parent(
                    self.expectation_bytes, self.protected_facts, 'fixture-token', total_timeout=30)
        self.assertEqual('Bearer fixture-token', seen[-1][1])
        self.assertEqual(5, len(seen))

        transport, _ = self.resolver_with()
        with mock.patch.object(github_parent, 'MAX_ARCHIVE_BYTES', 32), \
             mock.patch.object(github_parent, '_open_request', side_effect=transport):
            with self.assertRaises(ValueError):
                github_parent.resolve_protected_parent(
                    self.expectation_bytes, self.protected_facts, 'fixture-token', total_timeout=30)

    def test_redirect_chain_stops_at_the_fixed_hop_limit(self):
        location = 'https://release-assets.githubusercontent.com/signed/archive'
        documents = self.api_documents()
        seen = []

        def transport(request, timeout):
            url = request.full_url
            seen.append((url, request.get_header('Authorization')))
            parsed = github_parent.urlsplit(url)
            prefix = '/repos/' + self.repository
            api_path = parsed.path[len(prefix):] if parsed.path.startswith(prefix) else parsed.path
            key = api_path + (('?' + parsed.query) if parsed.query else '')
            if key.endswith('/actions/artifacts/{}/zip'.format(self.expectation['artifact_id'])) or \
                    parsed.hostname == 'release-assets.githubusercontent.com':
                return _Response(status=302, headers={'Location': location})
            return _Response(documents[key])

        with mock.patch.object(github_parent, '_open_request', side_effect=transport):
            with self.assertRaisesRegex(ValueError, 'protected-parent-redirect-limit'):
                github_parent.resolve_protected_parent(
                    self.expectation_bytes, self.protected_facts, 'fixture-token', total_timeout=30)
        self.assertEqual(8, len(seen))
        self.assertEqual('Bearer fixture-token', seen[4][1])
        self.assertTrue(all(authorization is None for _, authorization in seen[5:]))

    def test_artifact_pagination_has_a_hard_page_cap(self):
        docs = self.api_documents(total_count=1001)
        transport, seen = self.resolver_with(documents=docs)
        with mock.patch.object(github_parent, '_open_request', side_effect=transport):
            with self.assertRaisesRegex(ValueError, 'protected-parent-artifact-pagination'):
                github_parent.resolve_protected_parent(
                    self.expectation_bytes, self.protected_facts, 'fixture-token', total_timeout=30)
        self.assertEqual(4, len(seen))

    def test_protected_facts_and_deadline_reject_before_network(self):
        transport, seen = self.resolver_with()
        with mock.patch.object(github_parent, '_open_request', side_effect=transport):
            with self.assertRaises(ValueError):
                github_parent.resolve_protected_parent(
                    self.expectation_bytes, dict(self.protected_facts, base_sha='e' * 40),
                    'fixture-token', total_timeout=30)
            with self.assertRaises(ValueError):
                github_parent.resolve_protected_parent(
                    self.expectation_bytes, self.protected_facts, 'fixture-token', total_timeout=0)
        self.assertEqual([], seen)

    def test_archive_preflight_cannot_return_a_resolution_after_the_original_deadline(self):
        transport, seen = self.resolver_with()
        current = [100.0]
        original = github_parent.preflight_protected_parent

        def preflight(*args):
            documents = original(*args)
            current[0] = 131.0
            return documents

        with mock.patch.object(github_parent, '_open_request', side_effect=transport), \
             mock.patch.object(github_parent.time, 'monotonic', side_effect=lambda: current[0]), \
             mock.patch.object(github_parent, 'preflight_protected_parent', side_effect=preflight):
            with self.assertRaisesRegex(ValueError, '^protected-parent-deadline$'):
                github_parent.resolve_protected_parent(
                    self.expectation_bytes, self.protected_facts, 'fixture-token', total_timeout=30)
        self.assertGreater(len(seen), 0)


if __name__ == '__main__':
    unittest.main()
