"""Offline data/transport controls; synthetic provider context is never authentication."""
import base64
import copy
import hashlib
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest import mock
import zipfile

import entrypoint as subject


class QualificationBoundaryControls(unittest.TestCase):
    def setUp(self):
        self.head = 'a' * 40
        self.environment = {
            'GITHUB_ACTIONS': 'true', 'GITHUB_REPOSITORY': subject.REPOSITORY,
            'GITHUB_EVENT_NAME': 'workflow_dispatch', 'GITHUB_REF': 'refs/heads/main',
            'GITHUB_REF_PROTECTED': 'true', 'GITHUB_WORKFLOW_REF': subject.REPOSITORY + '/' + subject.WORKFLOW + '@refs/heads/main',
            'GITHUB_SHA': self.head, 'GITHUB_WORKFLOW_SHA': self.head,
            'GITHUB_REPOSITORY_ID': '12345', 'GITHUB_RUN_ID': '98765', 'GITHUB_RUN_ATTEMPT': '2',
        }
        self.capture = json.dumps({'schema': 'issue779-checkpoint2-source-v1', 'source': self.head,
                                   'tree': 'b' * 40, 'count': 1, 'files': {'fixed.cs': {}}}).encode()
        self.workflow = b'fixed protected workflow fixture bytes'
        self.aggregate = {'schema': 'issue779-checkpoint1-aggregate-v1', 'source': self.head,
                          'source_capture_sha256': hashlib.sha256(self.capture).hexdigest(),
                          'selectors': list(subject.SELECTORS), 'cases': list(subject.CASES),
                          'passed': 16, 'skipped': 0, 'exit': 0,
                          'native_acceptance': False, 'coverage_credit': False}
        self.archive = self.zip_body(json.dumps(self.aggregate).encode())
        self.metadata = {'id': 45678, 'name': 'issue779-checkpoint1-current-98765-2',
                         'size_in_bytes': len(self.archive), 'digest': 'sha256:' + hashlib.sha256(self.archive).hexdigest(),
                         'expired': False, 'workflow_run': {'id': 98765, 'head_sha': self.head}}
        self.repository = {'id': 12345, 'full_name': subject.REPOSITORY, 'default_branch': 'main'}
        self.run = {'id': 98765, 'run_attempt': 2, 'event': 'workflow_dispatch', 'head_branch': 'main',
                    'head_sha': self.head, 'path': subject.WORKFLOW, 'repository': self.repository,
                    'status': 'in_progress', 'conclusion': None}
        names = {subject.PREFIX + 'Current CSharp worker ' + name for name in subject.SELECTORS}
        names.add(subject.PREFIX + 'Reconcile all sixteen current worker controls')
        self.jobs = {'total_count': len(names), 'jobs': [dict(id=100 + index, name=name,
                     status='completed', conclusion='success', head_sha=self.head, run_attempt=2)
                     for index, name in enumerate(sorted(names))]}

    @staticmethod
    def zip_body(raw, name='aggregate.json', extra=False, mode=0o100600):
        output = io.BytesIO()
        with zipfile.ZipFile(output, 'w', zipfile.ZIP_DEFLATED) as archive:
            entry = zipfile.ZipInfo(name)
            entry.external_attr = mode << 16
            archive.writestr(entry, raw)
            if extra:
                archive.writestr('extra', b'canary')
        return output.getvalue()

    def responses(self):
        return [self.repository, self.run, {'encoding': 'base64', 'content': base64.b64encode(self.workflow).decode()},
                self.jobs, {'total_count': 1, 'artifacts': [self.metadata]}]

    def invoke(self, responses=None, raw=None):
        with mock.patch.object(subject.transport, '_api_json', side_effect=responses or self.responses()) as api:
            with mock.patch.object(subject.transport, '_download', return_value=raw or self.archive) as download:
                result = subject.collect(self.environment, self.head, 'detached-token', self.capture, self.workflow)
                return result, api, download

    def test_valid_detached_neighbors_return_partial_flags_and_exact_original_bytes(self):
        (receipt, raw, body), api, download = self.invoke()
        self.assertIs(self.archive, raw)
        self.assertEqual(self.aggregate, json.loads(body))
        for key in ('selector_callbacks_independently_verified', 'qualification_complete', 'admission_created',
                    'trusted_manifest_created', 'gate_claimed', 'native_acceptance_claimed'):
            self.assertIs(False, receipt[key])
        self.assertEqual('in_progress', receipt['observed_run_status'])
        self.assertEqual(5, api.call_count)
        self.assertEqual(1, download.call_count)
        self.assertEqual((12345, 98765, 2), subject.context(self.environment, self.head))

    def test_each_protected_context_mismatch_rejects_before_api(self):
        for key in self.environment:
            for value in (None, 'canary'):
                bad = dict(self.environment, **{key: value})
                with self.subTest(key=key, value=value), mock.patch.object(subject.transport, '_api_json') as api:
                    with self.assertRaisesRegex(ValueError, '^protected-qualification-rejected$'):
                        subject.collect(bad, self.head, 'token', self.capture, self.workflow)
                    api.assert_not_called()
        for value in ('0', '01', '-1', 'true', '9' * 20, '\u0661'):
            with self.assertRaises(ValueError):
                subject.context(dict(self.environment, GITHUB_RUN_ID=value), self.head)

    def test_workflow_api_path_accepts_only_exact_bare_or_protected_main_suffix(self):
        for path in (subject.WORKFLOW, subject.WORKFLOW + '@refs/heads/main'):
            responses = copy.deepcopy(self.responses())
            responses[1]['path'] = path
            self.assertFalse(self.invoke(responses)[0][0]['qualification_complete'])
        for path in (subject.WORKFLOW + '@refs/heads/other', subject.WORKFLOW + '@refs/tags/main',
                     subject.WORKFLOW + '@refs/heads/main/extra'):
            responses = copy.deepcopy(self.responses())
            responses[1]['path'] = path
            with self.assertRaises(ValueError):
                self.invoke(responses)

    def test_repository_run_and_workflow_mismatch_reject_before_download(self):
        changes = [(0, 'id', True), (0, 'default_branch', 'other'), (1, 'head_sha', 'b' * 40),
                   (1, 'run_attempt', 1), (1, 'status', 'completed'), (1, 'conclusion', 'success'),
                   (1, 'event', 'push'), (1, 'path', 'other'), (2, 'content', 'Y2FuYXJ5')]
        for index, key, value in changes:
            responses = copy.deepcopy(self.responses())
            responses[index][key] = value
            with self.subTest(index=index, key=key), mock.patch.object(subject.transport, '_api_json', side_effect=responses):
                with mock.patch.object(subject.transport, '_download') as download, self.assertRaises(ValueError):
                    subject.collect(self.environment, self.head, 'token', self.capture, self.workflow)
                download.assert_not_called()

    def test_missing_duplicate_failed_and_old_attempt_jobs_reject(self):
        for kind in ('missing', 'duplicate', 'failed', 'attempt', 'head'):
            responses = copy.deepcopy(self.responses())
            jobs = responses[3]
            if kind == 'missing':
                jobs['jobs'].pop()
                jobs['total_count'] -= 1
            elif kind == 'duplicate':
                jobs['jobs'][-1] = jobs['jobs'][0]
            else:
                key, value = {'failed': ('conclusion', 'failure'), 'attempt': ('run_attempt', True),
                              'head': ('head_sha', 'b' * 40)}[kind]
                jobs['jobs'][0][key] = value
            with self.subTest(kind=kind), self.assertRaises(ValueError):
                self.invoke(responses)

    def test_artifact_identity_bounds_expiry_and_digest_reject(self):
        for key, value in (('id', True), ('name', 'issue779-checkpoint1-current-98765-1'), ('expired', True),
                           ('size_in_bytes', subject.MAX_ARCHIVE + 1), ('digest', 'sha256:' + '0' * 64)):
            responses = copy.deepcopy(self.responses())
            responses[4]['artifacts'][0][key] = value
            with self.subTest(key=key), self.assertRaises(ValueError):
                self.invoke(responses)

    def test_digest_is_checked_before_archive_parser(self):
        with mock.patch.object(subject.zipfile, 'ZipFile') as parser, self.assertRaises(ValueError):
            subject.aggregate_archive(self.archive + b'x', self.metadata, self.head,
                                      self.aggregate['source_capture_sha256'])
        parser.assert_not_called()

    def test_malformed_nested_api_rows_fail_closed_without_raw_trace(self):
        for index, key, value in ((1, 'repository', None), (3, 'jobs', [None]),
                                  (4, 'artifacts', [None])):
            responses = copy.deepcopy(self.responses())
            responses[index][key] = value
            if key in ('jobs', 'artifacts'):
                responses[index]['total_count'] = 1
            with self.subTest(key=key), self.assertRaisesRegex(ValueError, '^protected-qualification-rejected$'):
                self.invoke(responses)
        responses = copy.deepcopy(self.responses())
        responses[4]['artifacts'][0]['workflow_run'] = None
        with self.assertRaisesRegex(ValueError, '^protected-qualification-rejected$'):
            self.invoke(responses)

    def test_zip_members_and_document_shape_are_closed(self):
        bodies = [self.zip_body(b'{}', name='../aggregate.json'), self.zip_body(b'{}', extra=True),
                  self.zip_body(b'{}', mode=0o120777), self.zip_body(b'x' * (subject.MAX_DOCUMENT + 1))]
        for change in ({'passed': True}, {'skipped': 1}, {'native_acceptance': True},
                       {'source': 'b' * 40}, {'cases': list(reversed(subject.CASES))}, {'unknown': 'canary'}):
            bodies.append(self.zip_body(json.dumps(dict(self.aggregate, **change)).encode()))
        bodies.append(self.zip_body(b'{"passed":16,"PASSED":16}'))
        for raw in bodies:
            metadata = dict(self.metadata, size_in_bytes=len(raw), digest='sha256:' + hashlib.sha256(raw).hexdigest())
            with self.subTest(raw_sha=metadata['digest']), self.assertRaises(ValueError):
                subject.aggregate_archive(raw, metadata, self.head, self.aggregate['source_capture_sha256'])

    def test_fixed_file_and_exclusive_output_preserve_original_bytes(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            path = root / 'data'
            subject.save(path, b'original')
            self.assertEqual(b'original', subject.read_fixed(path, 8))
            self.assertEqual(0o600, path.stat().st_mode & 0o777)
            with self.assertRaises(FileExistsError):
                subject.save(path, b'canary')
            alias = root / 'link'
            alias.symlink_to(path)
            with self.assertRaises(OSError):
                subject.read_fixed(alias, 8)
            self.assertEqual(b'original', path.read_bytes())
            with self.assertRaises(ValueError):
                subject.read_fixed(path, 7)


if __name__ == '__main__':
    unittest.main()
