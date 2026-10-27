"""Data-only controls for completed protected-run staging; no credentials or network calls."""
import hashlib
import json
import base64
from dataclasses import replace
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import completed_run as completed
import github_parent as transport
from entrypoint import REQUIRED_RUNTIME_JOBS, REPOSITORY, RUNTIME_WORKFLOW, WORKFLOW


class _Response:
    status = 200
    headers = {}

    def __init__(self, data):
        self._data = data

    def read(self, size=-1):
        if size < 0:
            size = len(self._data)
        data, self._data = self._data[:size], self._data[size:]
        return data

    def getcode(self):
        return self.status

    def __enter__(self):
        return self

    def __exit__(self, *_):
        return False


class CompletedRunDataTests(unittest.TestCase):
    def setUp(self):
        self.run_id = 40445212390
        self.attempt = 1
        self.head = 'a' * 40
        self.repo_id = 987654321
        repo = {'id': self.repo_id, 'full_name': REPOSITORY}
        self.event = {'repository': repo, 'workflow_run': {
            'id': self.run_id, 'run_attempt': self.attempt, 'event': 'workflow_dispatch',
            'head_branch': 'main', 'head_sha': self.head, 'path': WORKFLOW,
            'name': completed.QUALIFICATION_WORKFLOW_NAME, 'workflow_id': 12345,
            'status': 'completed', 'conclusion': 'success', 'repository': repo,
            'head_repository': repo,
        }}

    def test_artifact_inventory_is_the_fixed_run_attempt_set(self):
        names = completed.expected_artifact_names(self.run_id, self.attempt)
        self.assertEqual(completed.MAX_ARTIFACTS, len(names))
        self.assertEqual(len(names), len(set(names)))
        self.assertIn(f'issue779-checkpoint1-selector-None-{self.run_id}-1', names)
        self.assertIn(f'issue779-c1-measured-N16-{self.run_id}-1', names)
        self.assertIn(f'issue779-checkpoint4-coverage-{self.run_id}-1', names)

    def test_completed_event_requires_exact_protected_source_identity(self):
        projection = completed._event_projection(json.dumps(self.event).encode())
        self.assertEqual((self.repo_id, self.run_id, 1, self.head, 12345), projection)
        for key, value in (('conclusion', 'failure'), ('event', 'push'),
                           ('head_branch', 'feature'), ('path', 'other.yml'),
                           ('name', 'other workflow')):
            event = json.loads(json.dumps(self.event))
            event['workflow_run'][key] = value
            with self.subTest(key=key), self.assertRaises(ValueError):
                completed._event_projection(json.dumps(event).encode())
        bool_fields = (
            ('repository', 'id'),
            ('workflow_run', 'id'),
            ('workflow_run', 'run_attempt'),
            ('workflow_run', 'workflow_id'),
            ('workflow_run', 'repository', 'id'),
            ('workflow_run', 'head_repository', 'id'),
        )
        for field_path in bool_fields:
            event = json.loads(json.dumps(self.event))
            node = event
            for key in field_path[:-1]:
                node = node[key]
            node[field_path[-1]] = True
            with self.subTest(field_path=field_path), self.assertRaises(ValueError):
                completed._event_projection(json.dumps(event).encode())

    def test_duplicate_event_json_keys_are_rejected(self):
        raw = (b'{"repository":{"id":1,"full_name":"forge-trust/AppSurface"},'
               b'"workflow_run":{},"workflow_run":{}}')
        with self.assertRaises(ValueError):
            completed._event_projection(raw)

    def test_required_job_inventory_requires_each_fixed_row_once_and_successful(self):
        required = set(REQUIRED_RUNTIME_JOBS) | completed.EXPECTED_TERMINAL_JOBS
        rows = [{'id': index + 1, 'name': name, 'status': 'completed', 'conclusion': 'success',
                 'head_sha': self.head, 'run_attempt': self.attempt}
                for index, name in enumerate(sorted(required))]
        self.assertEqual(len(required), len(completed._required_jobs(rows, self.head, self.attempt)))
        for changed in (rows[:-1], rows + [dict(rows[0])],
                        [{**rows[0], 'conclusion': 'skipped'}, *rows[1:]],
                        [{**rows[0], 'run_attempt': 2}, *rows[1:]]):
            with self.subTest(count=len(changed)), self.assertRaises(ValueError):
                completed._required_jobs(changed, self.head, self.attempt)
        bool_attempt = [dict(row) for row in rows]
        bool_attempt[0]['run_attempt'] = True
        with self.assertRaises(ValueError):
            completed._required_jobs(bool_attempt, self.head, self.attempt)

    def _job_rows(self):
        required = set(REQUIRED_RUNTIME_JOBS) | completed.EXPECTED_TERMINAL_JOBS
        return [{'id': index + 1, 'name': name, 'status': 'completed', 'conclusion': 'success',
                 'head_sha': self.head, 'run_attempt': self.attempt}
                for index, name in enumerate(sorted(required))]

    def test_required_job_facts_preserve_each_validated_field_and_bind_closed_stage(self):
        rows = self._job_rows()
        facts = completed._required_job_facts(rows, self.head, self.attempt)
        self.assertEqual(tuple(sorted(REQUIRED_RUNTIME_JOBS | completed.EXPECTED_TERMINAL_JOBS)),
                         tuple(row.name for row in facts))
        self.assertEqual(tuple(sorted(row['id'] for row in rows)),
                         tuple(sorted(row.id for row in facts)))
        self.assertTrue(all(row.head_sha == self.head and row.run_attempt == self.attempt
                            and row.status == 'completed' and row.conclusion == 'success'
                            for row in facts))
        stage = completed.CompletedRunStage(
            self.repo_id, self.run_id, self.attempt, self.head, WORKFLOW, (), 'b' * 40, 'c' * 64,
            tuple(sorted(row.id for row in facts)), (), 0, facts)
        self.assertEqual(facts, completed.require_closed_jobs(stage))

    def test_required_job_facts_reject_wrong_status_name_head_attempt_and_duplicate_id(self):
        rows = self._job_rows()
        cases = (
            ({**rows[0], 'status': 'in_progress'},),
            ({**rows[0], 'conclusion': 'failure'},),
            ({**rows[0], 'name': 'unknown job'},),
            ({**rows[0], 'head_sha': 'f' * 40},),
            ({**rows[0], 'run_attempt': self.attempt + 1},),
            ({**rows[0], 'id': rows[1]['id']},),
            ({**rows[0], 'id': []},),
            ({**rows[0], 'name': {}},),
        )
        for (first,) in cases:
            changed = [first, *rows[1:]]
            with self.subTest(first=first), self.assertRaises(ValueError):
                completed._required_job_facts(changed, self.head, self.attempt)

    def test_closed_job_helper_rejects_legacy_empty_and_tampered_stage_facts(self):
        rows = self._job_rows()
        facts = completed._required_job_facts(rows, self.head, self.attempt)
        legacy = completed.CompletedRunStage(
            self.repo_id, self.run_id, self.attempt, self.head, WORKFLOW, (), 'b' * 40, 'c' * 64,
            tuple(sorted(row.id for row in facts)), (), 0)
        with self.assertRaisesRegex(ValueError, 'closed-jobs-shape'):
            completed.require_closed_jobs(legacy)

        cases = (
            replace(facts[0], status='queued'),
            replace(facts[0], conclusion='cancelled'),
            replace(facts[0], name='unexpected'),
            replace(facts[0], head_sha='f' * 40),
            replace(facts[0], run_attempt=self.attempt + 1),
            replace(facts[0], id=facts[1].id),
            replace(facts[0], name=[]),
        )
        for altered in cases:
            changed = (altered, *facts[1:])
            stage = replace(legacy, jobs=changed)
            with self.subTest(job=altered), self.assertRaises(ValueError):
                completed.require_closed_jobs(stage)

    def test_api_run_identity_rejects_boolean_ids_and_attempts(self):
        run = {**self.event['workflow_run']}
        self.assertTrue(completed._api_run_matches(
            run, self.repo_id, self.run_id, self.attempt, self.head, 12345))
        fields = (
            ('id',), ('run_attempt',), ('workflow_id',), ('repository', 'id'),
            ('head_repository', 'id'),
        )
        for field_path in fields:
            changed = json.loads(json.dumps(run))
            node = changed
            for key in field_path[:-1]:
                node = node[key]
            node[field_path[-1]] = True
            with self.subTest(field_path=field_path):
                self.assertFalse(completed._api_run_matches(
                    changed, self.repo_id, self.run_id, self.attempt, self.head, 12345))

    def test_boolean_identity_values_do_not_alias_integer_one_in_any_provider_projection(self):
        def set_field(value, path, replacement):
            node = value
            for key in path[:-1]:
                node = node[key]
            node[path[-1]] = replacement

        event = json.loads(json.dumps(self.event))
        event['repository']['id'] = 1
        for path in (('id',), ('workflow_id',), ('repository', 'id'), ('head_repository', 'id')):
            set_field(event['workflow_run'], path, 1)
        event['workflow_run']['run_attempt'] = 1
        self.assertEqual((1, 1, 1, self.head, 1), completed._event_projection(json.dumps(event).encode()))
        for path in (('repository', 'id'), ('workflow_run', 'id'), ('workflow_run', 'run_attempt'),
                     ('workflow_run', 'workflow_id'), ('workflow_run', 'repository', 'id'),
                     ('workflow_run', 'head_repository', 'id')):
            changed = json.loads(json.dumps(event))
            set_field(changed, path, True)
            with self.subTest(projection='event', field=path), self.assertRaises(ValueError):
                completed._event_projection(json.dumps(changed).encode())

        run = json.loads(json.dumps(event['workflow_run']))
        self.assertTrue(completed._api_run_matches(run, 1, 1, 1, self.head, 1))
        for path in (('id',), ('run_attempt',), ('workflow_id',), ('repository', 'id'),
                     ('head_repository', 'id')):
            changed = json.loads(json.dumps(run))
            set_field(changed, path, True)
            with self.subTest(projection='api-run', field=path):
                self.assertFalse(completed._api_run_matches(changed, 1, 1, 1, self.head, 1))

        required = set(REQUIRED_RUNTIME_JOBS) | completed.EXPECTED_TERMINAL_JOBS
        jobs = [{'id': index + 1, 'name': name, 'status': 'completed', 'conclusion': 'success',
                 'head_sha': self.head, 'run_attempt': 1}
                for index, name in enumerate(sorted(required))]
        self.assertEqual(len(required), len(completed._required_jobs(jobs, self.head, 1)))
        for field in ('id', 'run_attempt'):
            changed = [dict(row) for row in jobs]
            changed[0][field] = True
            with self.subTest(projection='jobs', field=field), self.assertRaises(ValueError):
                completed._required_jobs(changed, self.head, 1)

        names = completed.expected_artifact_names(1, 1)
        artifacts = [{'id': index + 1, 'name': name, 'expired': False, 'size_in_bytes': 3,
                      'digest': 'sha256:' + '0' * 64,
                      'workflow_run': {'id': 1, 'head_sha': self.head}}
                     for index, name in enumerate(names)]
        self.assertEqual(completed.MAX_ARTIFACTS, len(completed._artifact_inventory(artifacts, 1, 1, self.head)))
        for path in (('id',), ('workflow_run', 'id')):
            changed = json.loads(json.dumps(artifacts))
            set_field(changed[0], path, True)
            with self.subTest(projection='artifacts', field=path), self.assertRaises(ValueError):
                completed._artifact_inventory(changed, 1, 1, self.head)

    def test_artifact_metadata_requires_exact_names_run_and_digest(self):
        rows = []
        for index, name in enumerate(completed.expected_artifact_names(self.run_id, self.attempt)):
            rows.append({'id': index + 1, 'name': name, 'expired': False, 'size_in_bytes': 3,
                         'digest': 'sha256:' + '0' * 64,
                         'workflow_run': {'id': self.run_id, 'head_sha': self.head}})
        selected = completed._artifact_inventory(rows, self.run_id, self.attempt, self.head)
        self.assertEqual(completed.MAX_ARTIFACTS, len(selected))
        corruptions = [rows[:-1], rows + [{'name': 'extra'}],
                       [{**rows[0], 'expired': True}, *rows[1:]],
                       [{**rows[0], 'digest': 'sha256:' + 'G' * 64}, *rows[1:]],
                       [{**rows[0], 'workflow_run': {'id': self.run_id + 1, 'head_sha': self.head}}, *rows[1:]],
                       [{**rows[0], 'id': rows[1]['id']}, *rows[1:]]]
        for changed in corruptions:
            with self.subTest(first=changed[0]), self.assertRaises(ValueError):
                completed._artifact_inventory(changed, self.run_id, self.attempt, self.head)
        for field_path in (('id',), ('size_in_bytes',), ('workflow_run', 'id')):
            changed = json.loads(json.dumps(rows))
            node = changed[0]
            for key in field_path[:-1]:
                node = node[key]
            node[field_path[-1]] = True
            with self.subTest(field_path=field_path), self.assertRaises(ValueError):
                completed._artifact_inventory(changed, self.run_id, self.attempt, self.head)

    def test_declared_archive_total_is_rejected_before_any_download(self):
        rows = []
        for index, name in enumerate(completed.expected_artifact_names(self.run_id, self.attempt)):
            rows.append({'id': index + 1, 'name': name, 'expired': False,
                         'size_in_bytes': completed._artifact_limit(name),
                         'digest': 'sha256:' + '0' * 64,
                         'workflow_run': {'id': self.run_id, 'head_sha': self.head}})
        # All individually legal role maxima exceed the explicit staging cap as a set.
        self.assertGreater(sum(row['size_in_bytes'] for row in rows), completed.MAX_TOTAL_ARCHIVE_BYTES)
        with self.assertRaises(ValueError):
            completed._artifact_inventory(rows, self.run_id, self.attempt, self.head)
        for row in rows:
            row['size_in_bytes'] = completed._artifact_limit(row['name']) + 1
        with self.assertRaises(ValueError):
            completed._artifact_inventory(rows, self.run_id, self.attempt, self.head)

    def test_capture_binds_both_workflow_files_to_independent_checkout_bytes(self):
        workflows = {WORKFLOW: b'protected workflow', RUNTIME_WORKFLOW: b'runtime workflow'}
        files = {}
        for path, raw in workflows.items():
            files[path] = {'sha256': hashlib.sha256(raw).hexdigest(),
                           'git_blob': completed._git_blob(raw), 'mode': '0644'}
        capture = {'schema': 'issue779-checkpoint2-source-v1', 'source': self.head,
                   'tree': 'b' * 40, 'count': len(files), 'files': files}
        raw_capture = json.dumps(capture, sort_keys=True).encode()
        self.assertEqual(capture, completed._source_projection(raw_capture, workflows, self.head))
        altered = workflows.copy()
        altered[WORKFLOW] = b'changed'
        with self.assertRaises(ValueError):
            completed._source_projection(raw_capture, altered, self.head)
        bad_mode = json.loads(raw_capture)
        bad_mode['files'][WORKFLOW]['mode'] = '100644'
        with self.assertRaises(ValueError):
            completed._source_projection(json.dumps(bad_mode).encode(), workflows, self.head)

    def test_source_json_rejects_case_aliases_and_nonfinite_numbers(self):
        for raw in (b'{"A":1,"a":2}', b'{"value":NaN}', b'{"value":Infinity}'):
            with self.subTest(raw=raw), self.assertRaises(ValueError):
                completed._json(raw, 1024, 'source-json')

    def test_workflow_content_decoder_handles_current_file_over_shared_metadata_cap(self):
        raw = b'# runtime workflow\n' + b'x' * 24000
        value = {'encoding': 'base64', 'content': base64.b64encode(raw).decode('ascii')}
        self.assertEqual(raw, completed._decode_workflow_content(value))
        with self.assertRaises(ValueError):
            completed._decode_workflow_content({'encoding': 'utf-8', 'content': 'workflow'})

    def test_stage_data_type_has_no_qualification_or_authority_flags(self):
        fields = set(completed.CompletedRunStage.__dataclass_fields__)
        self.assertFalse(fields & {'qualified', 'qualification_complete', 'admission', 'trusted',
                                   'native_acceptance', 'accepted_consumer_proof'})

    def test_raw_download_is_streamed_bounded_and_written_exclusively(self):
        body = b'raw-zip-bytes'
        with tempfile.TemporaryDirectory() as root:
            destination = Path(root) / 'artifact.zip'
            budget = completed._Budget(10)
            with patch.object(transport, '_open_request', return_value=_Response(body)):
                digest, size, identity = completed._archive_url(
                    transport.API_ORIGIN + '/repos/forge-trust/AppSurface/actions/artifacts/7/zip',
                    'fixture-token', budget, destination, len(body), 1024)
            self.assertEqual(hashlib.sha256(body).hexdigest(), digest)
            self.assertEqual(len(body), size)
            named = destination.lstat()
            self.assertEqual((named.st_dev, named.st_ino), identity)
            self.assertEqual(body, destination.read_bytes())
            with self.assertRaises(FileExistsError):
                with patch.object(transport, '_open_request', return_value=_Response(body)):
                    completed._archive_url(
                        transport.API_ORIGIN + '/repos/forge-trust/AppSurface/actions/artifacts/7/zip',
                        'fixture-token', budget, destination, len(body), 1024)

    def test_raw_download_partial_write_and_read_failures_remove_only_owned_file(self):
        body = b'partial archive bytes'
        real_write = completed.os.write
        with tempfile.TemporaryDirectory() as root:
            for failure in ('write', 'read'):
                destination = Path(root) / (failure + '.zip')
                budget = completed._Budget(10)
                with self.subTest(failure=failure):
                    if failure == 'write':
                        calls = 0

                        def partial_then_fail(fd, data):
                            nonlocal calls
                            calls += 1
                            if calls == 1:
                                return real_write(fd, data[:4])
                            raise OSError('injected-write-failure')

                        write_patch = patch.object(completed.os, 'write', side_effect=partial_then_fail)
                        read_patch = patch.object(completed.os, 'read', wraps=completed.os.read)
                    else:
                        write_patch = patch.object(completed.os, 'write', wraps=completed.os.write)
                        read_patch = patch.object(completed.os, 'read', side_effect=OSError('injected-read-failure'))
                    with write_patch, read_patch, \
                            patch.object(transport, '_open_request', return_value=_Response(body)), \
                            self.assertRaises(OSError):
                        completed._archive_url(
                            transport.API_ORIGIN + '/repos/forge-trust/AppSurface/actions/artifacts/7/zip',
                            'fixture-token', budget, destination, len(body), 1024)
                    self.assertFalse(destination.exists())

    def test_raw_download_rejects_substituted_destination_and_preserves_replacement(self):
        body = b'archive bytes'
        with tempfile.TemporaryDirectory() as root:
            destination = Path(root) / 'artifact.zip'

            class ReplaceDestination(_Response):
                replaced = False

                def read(self, size=-1):
                    if not self.replaced:
                        self.replaced = True
                        destination.unlink()
                        destination.write_bytes(b'other owner file')
                    return super().read(size)

            with patch.object(transport, '_open_request', return_value=ReplaceDestination(body)):
                with self.assertRaisesRegex(ValueError, 'completed-run-archive-(?:file|path-replaced)'):
                    completed._archive_url(
                        transport.API_ORIGIN + '/repos/forge-trust/AppSurface/actions/artifacts/7/zip',
                        'fixture-token', completed._Budget(10), destination, len(body), 1024)
            self.assertEqual(b'other owner file', destination.read_bytes())

    def test_resolver_uses_authenticated_run_projections_and_stages_only_the_closed_inventory(self):
        workflows = {WORKFLOW: b'protected workflow', RUNTIME_WORKFLOW: b'runtime workflow'}
        files = {path: {'sha256': hashlib.sha256(raw).hexdigest(),
                        'git_blob': completed._git_blob(raw), 'mode': '0644'}
                 for path, raw in workflows.items()}
        capture = json.dumps({'schema': 'issue779-checkpoint2-source-v1', 'source': self.head,
                              'tree': 'b' * 40, 'count': len(files), 'files': files},
                             sort_keys=True).encode()
        archived = b'opaque raw archive bytes'
        artifact_rows = [
            {'id': index + 101, 'name': name, 'expired': False, 'size_in_bytes': len(archived),
             'digest': 'sha256:' + hashlib.sha256(archived).hexdigest(),
             'workflow_run': {'id': self.run_id, 'head_sha': self.head}}
            for index, name in enumerate(completed.expected_artifact_names(self.run_id, self.attempt))]
        required = set(REQUIRED_RUNTIME_JOBS) | completed.EXPECTED_TERMINAL_JOBS
        job_rows = [{'id': index + 1, 'name': name, 'status': 'completed', 'conclusion': 'success',
                     'head_sha': self.head, 'run_attempt': self.attempt}
                    for index, name in enumerate(sorted(required))]
        repo = {'id': self.repo_id, 'full_name': REPOSITORY, 'default_branch': 'main'}
        run = {**self.event['workflow_run'], 'repository': {'id': self.repo_id, 'full_name': REPOSITORY},
               'head_repository': {'id': self.repo_id, 'full_name': REPOSITORY}}
        def api(url, token, budget):
            self.assertEqual('fixture-token', token)
            budget.count()
            if url.endswith('/repos/forge-trust/AppSurface'):
                return repo
            if '/attempts/' in url and '/jobs?' not in url:
                return run
            if '/contents/' in url:
                path = next(path for path in workflows if '/contents/' + path + '?' in url)
                return {'encoding': 'base64', 'content': base64.b64encode(workflows[path]).decode()}
            if '/jobs?' in url:
                return {'total_count': len(job_rows), 'jobs': job_rows}
            if '/artifacts?' in url:
                return {'total_count': len(artifact_rows), 'artifacts': artifact_rows}
            self.fail('unexpected API endpoint')
        environment = {'GITHUB_ACTIONS': 'true', 'GITHUB_REPOSITORY': REPOSITORY,
                       'GITHUB_EVENT_NAME': 'workflow_run', 'GITHUB_REF': 'refs/heads/main',
                       'GITHUB_REF_PROTECTED': 'true'}
        with tempfile.TemporaryDirectory() as root:
            stage = Path(root).resolve() / 'stage'
            stage.mkdir(mode=0o700)
            with patch.object(transport, '_api_json', side_effect=api), \
                    patch.object(transport, '_open_request', side_effect=lambda *_args, **_kwargs: _Response(archived)):
                result = completed.resolve_completed_run(
                    json.dumps(self.event).encode(), environment, self.head, capture, workflows,
                    'fixture-token', stage, total_timeout=30)
            self.assertEqual(completed.MAX_ARTIFACTS, len(result.artifacts))
            self.assertEqual(completed.MAX_ARTIFACTS, len(list(stage.iterdir())))
            self.assertEqual(self.run_id, result.run_id)
            self.assertEqual(self.attempt, result.run_attempt)
            self.assertEqual(len(REQUIRED_RUNTIME_JOBS | completed.EXPECTED_TERMINAL_JOBS), len(result.jobs))
            self.assertEqual(tuple(sorted(result.job_ids)), tuple(sorted(row.id for row in result.jobs)))
            self.assertEqual(result.jobs, completed.require_closed_jobs(result))
            self.assertEqual(2, len(result.workflow_sha256s))
            self.assertEqual(archived, (stage / result.artifacts[0].staged_filename).read_bytes())
            self.assertFalse(set(result.__dataclass_fields__) & {'qualified', 'qualification_complete', 'admission'})


if __name__ == '__main__':
    unittest.main()
