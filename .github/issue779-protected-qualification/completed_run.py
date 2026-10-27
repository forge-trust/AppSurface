"""Resolve bounded evidence archives for one completed protected qualification run.

This provider/data staging layer never qualifies a run, creates admission, or
claims Trusted/native acceptance. Validation of staged archive contents is a
separate source-owned step.
"""
from __future__ import annotations

from dataclasses import dataclass
import base64
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import time
from urllib.request import Request
from urllib.error import HTTPError, URLError
from urllib.parse import quote, urljoin

from entrypoint import REQUIRED_RUNTIME_JOBS, REPOSITORY, RUNTIME_WORKFLOW, WORKFLOW
import github_parent as transport


MAX_EVENT_BYTES = 128 * 1024
MAX_CAPTURE_BYTES = 1024 * 1024
MAX_WORKFLOW_BYTES = 1024 * 1024
MAX_ARTIFACTS = 37
MIB = 1024 * 1024
MAX_TOTAL_ARCHIVE_BYTES = 512 * MIB
ROLE_ARCHIVE_LIMITS = (
    ('issue779-protected-parent-controls', 8 * MIB),
    ('issue779-checkpoint1-selector', 8 * MIB),
    ('issue779-c1-measured', 24 * MIB),
    ('issue779-checkpoint1-current', 16 * MIB),
    ('issue779-csharp-checkpoint2', 16 * MIB),
    ('issue779-checkpoint3-application', 16 * MIB),
    ('issue779-c3sdk-lifecycle', 16 * MIB),
    ('issue779-c4-accounts-C4Accounts', 12 * MIB),
    ('issue779-c4-accounts-C4AccountRollback', 12 * MIB),
    ('issue779-checkpoint4-coverage', 32 * MIB),
    ('issue779-protected-full-runtime-partial', 8 * MIB),
)
MAX_REQUESTS = 7 + MAX_ARTIFACTS * (transport.MAX_REDIRECTS + 1)
MAX_TOTAL_SECONDS = 1200.0
REQUEST_SECONDS = 15.0
MAX_REDIRECTS = 3
SHA1 = re.compile(r'[0-9a-f]{40}\Z')
SHA256 = re.compile(r'[0-9a-f]{64}\Z')
SELECTORS = ('None', 'N04', 'N05', 'N06', 'N07', 'N08', 'N09', 'N10', 'N11', 'N12', 'N13', 'N14', 'N15', 'N16')
QUALIFICATION_WORKFLOW_NAME = 'EvidenceHost protected qualification prerequisite'
EXPECTED_TERMINAL_JOBS = frozenset({
    'Check the protected qualification source',
    'Retain full-runtime qualification provenance without acceptance',
})


@dataclass(frozen=True)
class ArtifactBinding:
    """Provider metadata and the hash of one raw downloaded artifact archive."""

    artifact_id: int
    name: str
    size: int
    sha256: str
    staged_filename: str


@dataclass(frozen=True)
class CompletedJob:
    """Source-checked provider facts for one required completed-run job."""

    name: str
    id: int
    head_sha: str
    run_attempt: int
    status: str
    conclusion: str


@dataclass(frozen=True)
class CompletedRunStage:
    """Non-authorizing evidence staging metadata for one completed workflow attempt."""

    repository_id: int
    run_id: int
    run_attempt: int
    head_sha: str
    workflow_path: str
    workflow_sha256s: tuple[tuple[str, str], ...]
    source_tree: str
    source_capture_sha256: str
    job_ids: tuple[int, ...]
    artifacts: tuple[ArtifactBinding, ...]
    total_archive_bytes: int
    jobs: tuple[CompletedJob, ...] = ()


class _Budget:
    """Independent finite budget; the smaller protected-parent transport stays unchanged."""

    def __init__(self, total_seconds: float):
        if type(total_seconds) not in (int, float) or not 0 < total_seconds <= MAX_TOTAL_SECONDS:
            raise ValueError('completed-run-deadline')
        self.deadline = time.monotonic() + total_seconds
        self.requests = 0
        self.archive_bytes = 0

    def timeout(self):
        remaining = self.deadline - time.monotonic()
        if remaining <= 0:
            raise ValueError('completed-run-deadline')
        return min(REQUEST_SECONDS, remaining)

    def count(self):
        self.requests += 1
        if self.requests > MAX_REQUESTS:
            raise ValueError('completed-run-request-limit')

    def charge_archive(self, amount):
        self.archive_bytes += amount
        if self.archive_bytes > MAX_TOTAL_ARCHIVE_BYTES:
            raise ValueError('completed-run-total-bytes')


def _need(condition, code):
    if not condition:
        raise ValueError(code)


def _json(raw, maximum, code):
    _need(type(raw) is bytes and 0 < len(raw) <= maximum, code)
    try:
        value = json.loads(raw.decode('utf-8'), object_pairs_hook=_unique_pairs,
                           parse_constant=lambda _value: (_ for _ in ()).throw(ValueError(code)))
    except (UnicodeDecodeError, json.JSONDecodeError, RecursionError, ValueError):
        raise ValueError(code) from None
    _need(type(value) is dict, code)
    return value


def _unique_pairs(pairs):
    value = {}
    folded = set()
    for key, item in pairs:
        alias = key.casefold() if type(key) is str else None
        if type(key) is not str or key in value or alias in folded:
            raise ValueError('completed-run-duplicate-or-case-alias-json-key')
        value[key] = item
        folded.add(alias)
    return value


def _event_projection(event_raw):
    event = _json(event_raw, MAX_EVENT_BYTES, 'completed-run-event')
    run = event.get('workflow_run')
    _need(type(run) is dict, 'completed-run-event-run')
    repository = event.get('repository')
    _need(type(repository) is dict and repository.get('full_name') == REPOSITORY
          and type(repository.get('id')) is int and repository['id'] > 0,
          'completed-run-event-repository')
    _need(type(run.get('id')) is int and 0 < run['id'] < 2**63
          and type(run.get('run_attempt')) is int and 0 < run['run_attempt'] < 2**31
          and run.get('event') == 'workflow_dispatch' and run.get('head_branch') == 'main'
          and run.get('head_sha') and type(run['head_sha']) is str and SHA1.fullmatch(run['head_sha'])
          and run.get('status') == 'completed' and run.get('conclusion') == 'success'
          and run.get('name') == QUALIFICATION_WORKFLOW_NAME
          and run.get('path') in (WORKFLOW, WORKFLOW + '@refs/heads/main')
          and type(run.get('workflow_id')) is int and run['workflow_id'] > 0,
          'completed-run-event-identity')
    _need(type(run.get('repository')) is dict
          and type(run['repository'].get('id')) is int and run['repository']['id'] == repository['id']
          and run['repository'].get('full_name') == REPOSITORY
          and type(run.get('head_repository')) is dict
          and type(run['head_repository'].get('id')) is int and run['head_repository']['id'] == repository['id']
          and run['head_repository'].get('full_name') == REPOSITORY,
          'completed-run-event-head-repository')
    return repository['id'], run['id'], run['run_attempt'], run['head_sha'], run['workflow_id']


def expected_artifact_names(run_id, attempt):
    """Closed artifact inventory, derived only from fixed source workflow names."""
    suffix = f'{run_id}-{attempt}'
    names = {
        f'issue779-protected-parent-controls-{suffix}',
        f'issue779-checkpoint1-current-{suffix}',
        f'issue779-csharp-checkpoint2-{suffix}',
        f'issue779-checkpoint3-application-{suffix}',
        f'issue779-c3sdk-lifecycle-{suffix}',
        f'issue779-c4-accounts-C4Accounts-{suffix}',
        f'issue779-c4-accounts-C4AccountRollback-{suffix}',
        f'issue779-checkpoint4-coverage-{suffix}',
        f'issue779-protected-full-runtime-partial-{suffix}',
    }
    names.update(f'issue779-checkpoint1-selector-{selector}-{suffix}' for selector in SELECTORS)
    names.update(f'issue779-c1-measured-{selector}-{suffix}' for selector in SELECTORS)
    _need(len(names) == MAX_ARTIFACTS
          and all(any(name.startswith(prefix + '-') for prefix, _limit in ROLE_ARCHIVE_LIMITS) for name in names),
          'completed-run-inventory-definition')
    return tuple(sorted(names))


def _artifact_limit(name):
    matches = [limit for prefix, limit in ROLE_ARCHIVE_LIMITS if name.startswith(prefix + '-')]
    _need(len(matches) == 1, 'completed-run-artifact-role')
    return matches[0]


def _remove_owned(path, identity):
    """Remove only the regular single-link file still named by this invocation."""
    try:
        named = os.lstat(path)
    except FileNotFoundError:
        return True
    except OSError:
        return False
    if identity is None or not stat.S_ISREG(named.st_mode) or named.st_nlink != 1:
        return False
    if (named.st_dev, named.st_ino) != identity:
        return False
    try:
        os.unlink(path)
    except FileNotFoundError:
        return True
    except OSError:
        return False
    return True


def _source_projection(capture_raw, workflow_bytes, head_sha):
    capture = _json(capture_raw, MAX_CAPTURE_BYTES, 'completed-run-source-capture')
    _need(set(capture) == {'schema', 'source', 'tree', 'count', 'files'}
          and capture.get('schema') == 'issue779-checkpoint2-source-v1'
          and capture.get('source') == head_sha and type(capture.get('tree')) is str
          and SHA1.fullmatch(capture['tree']) and type(capture.get('count')) is int
          and type(capture.get('files')) is dict and capture['count'] == len(capture['files']) > 0,
          'completed-run-source-shape')
    _need(type(workflow_bytes) is dict and set(workflow_bytes) == {WORKFLOW, RUNTIME_WORKFLOW},
          'completed-run-workflow-inputs')
    for path, raw in workflow_bytes.items():
        _need(type(raw) is bytes and 0 < len(raw) <= MAX_WORKFLOW_BYTES, 'completed-run-workflow-bytes')
        row = capture['files'].get(path)
        _need(type(row) is dict and set(row) == {'sha256', 'git_blob', 'mode'}
              and type(row['sha256']) is str and SHA256.fullmatch(row['sha256'])
              and type(row['git_blob']) is str and SHA1.fullmatch(row['git_blob'])
              and row['mode'] in ('0644', '0755')
              and row['sha256'] == hashlib.sha256(raw).hexdigest()
              and row['git_blob'] == _git_blob(raw), 'completed-run-workflow-capture')
    return capture


def _git_blob(raw):
    return hashlib.sha1(b'blob ' + str(len(raw)).encode('ascii') + b'\0' + raw).hexdigest()


def _decode_workflow_content(value):
    _need(value.get('encoding') == 'base64' and type(value.get('content')) is str,
          'completed-run-workflow-content')
    try:
        encoded = value['content'].encode('ascii', 'strict')
    except UnicodeEncodeError:
        raise ValueError('completed-run-workflow-content') from None
    _need(len(encoded) <= MAX_WORKFLOW_BYTES * 2, 'completed-run-workflow-content-size')
    try:
        raw = base64.b64decode(b''.join(encoded.split()), validate=True)
    except (ValueError, UnicodeEncodeError):
        raise ValueError('completed-run-workflow-content') from None
    _need(0 < len(raw) <= MAX_WORKFLOW_BYTES, 'completed-run-workflow-content-size')
    return raw


def _required_jobs(rows, head_sha, attempt):
    facts = _required_job_facts(rows, head_sha, attempt)
    return tuple(sorted(row.id for row in facts))


def _required_job_facts(rows, head_sha, attempt):
    _need(type(rows) is list and all(type(row) is dict for row in rows), 'completed-run-jobs')
    required = set(REQUIRED_RUNTIME_JOBS) | EXPECTED_TERMINAL_JOBS
    selected = [row for row in rows if type(row.get('name')) is str and row['name'] in required]
    _need(len(selected) == len(required) and {row['name'] for row in selected} == required,
          'completed-run-required-jobs')
    _need(all(type(row.get('id')) is int and row['id'] > 0
              and type(row.get('name')) is str and row['name'] in required
              and type(row.get('head_sha')) is str and row['head_sha'] == head_sha
              and type(row.get('run_attempt')) is int and row['run_attempt'] == attempt
              and row.get('status') == 'completed' and row.get('conclusion') == 'success'
              for row in selected)
          and len({row['id'] for row in selected}) == len(required),
          'completed-run-required-jobs')
    return tuple(sorted((CompletedJob(row['name'], row['id'], row['head_sha'], row['run_attempt'],
                                      row['status'], row['conclusion']) for row in selected),
                        key=lambda fact: fact.name))


def require_closed_jobs(stage):
    """Validate retained fixed job facts before consumers use a terminal job ID.

    Legacy positional `CompletedRunStage` construction remains possible and
    yields an empty `jobs` tuple; such an object deliberately cannot satisfy
    this stronger provider-fact check.
    """
    _need(type(stage) is CompletedRunStage and type(stage.jobs) is tuple
          and len(stage.jobs) == len(set(REQUIRED_RUNTIME_JOBS) | EXPECTED_TERMINAL_JOBS)
          and all(type(job) is CompletedJob for job in stage.jobs),
          'completed-run-closed-jobs-shape')
    required = set(REQUIRED_RUNTIME_JOBS) | EXPECTED_TERMINAL_JOBS
    _need(type(stage.head_sha) is str and SHA1.fullmatch(stage.head_sha) is not None
          and type(stage.run_attempt) is int and 0 < stage.run_attempt < 2**31,
          'completed-run-closed-jobs-stage-identity')
    names = [job.name for job in stage.jobs]
    ids = [job.id for job in stage.jobs]
    _need(all(type(job.name) is str and type(job.id) is int and job.id > 0
                  and type(job.head_sha) is str and SHA1.fullmatch(job.head_sha) is not None
                  and job.head_sha == stage.head_sha
                  and type(job.run_attempt) is int and job.run_attempt == stage.run_attempt
                  and type(job.status) is str and job.status == 'completed'
                  and type(job.conclusion) is str and job.conclusion == 'success'
                  for job in stage.jobs)
          and set(names) == required and len(names) == len(set(names))
          and type(stage.job_ids) is tuple and all(type(value) is int and value > 0
                                                    for value in stage.job_ids)
          and len(ids) == len(set(ids)) == len(stage.job_ids) == len(set(stage.job_ids))
          and set(ids) == set(stage.job_ids), 'completed-run-closed-jobs-identity')
    return tuple(sorted(stage.jobs, key=lambda job: job.name))


def _api_run_matches(run, repository_id, run_id, attempt, head_sha, workflow_id):
    return (type(run) is dict and type(run.get('id')) is int and run['id'] == run_id
            and type(run.get('run_attempt')) is int and run['run_attempt'] == attempt
            and run.get('head_sha') == head_sha and run.get('event') == 'workflow_dispatch'
            and run.get('head_branch') == 'main' and run.get('name') == QUALIFICATION_WORKFLOW_NAME
            and run.get('path') in (WORKFLOW, WORKFLOW + '@refs/heads/main')
            and run.get('status') == 'completed' and run.get('conclusion') == 'success'
            and type(run.get('workflow_id')) is int and run['workflow_id'] == workflow_id
            and type(run.get('repository')) is dict
            and type(run['repository'].get('id')) is int and run['repository']['id'] == repository_id
            and run['repository'].get('full_name') == REPOSITORY
            and type(run.get('head_repository')) is dict
            and type(run['head_repository'].get('id')) is int
            and run['head_repository']['id'] == repository_id
            and run['head_repository'].get('full_name') == REPOSITORY)


def _artifact_inventory(rows, run_id, attempt, head_sha):
    _need(type(rows) is list and all(type(row) is dict for row in rows), 'completed-run-artifacts')
    names = expected_artifact_names(run_id, attempt)
    _need(len(rows) == len(names) and {row.get('name') for row in rows} == set(names)
          and len({row.get('id') for row in rows}) == len(names),
          'completed-run-artifact-inventory')
    selected = []
    for name in names:
        metadata = next(row for row in rows if row.get('name') == name)
        size = metadata.get('size_in_bytes')
        digest = metadata.get('digest')
        run_meta = metadata.get('workflow_run')
        _need(type(metadata.get('id')) is int and metadata['id'] > 0 and metadata.get('expired') is False
              and type(size) is int and 0 < size <= _artifact_limit(name)
              and type(digest) is str and digest.startswith('sha256:')
              and SHA256.fullmatch(digest[7:]) is not None and type(run_meta) is dict
              and type(run_meta.get('id')) is int and run_meta['id'] == run_id
              and run_meta.get('head_sha') == head_sha,
              'completed-run-artifact-metadata')
        selected.append((metadata, name, size, digest[7:]))
    _need(sum(item[2] for item in selected) <= MAX_TOTAL_ARCHIVE_BYTES,
          'completed-run-total-bytes')
    return tuple(selected)


def _url(suffix):
    return transport.API_ORIGIN + '/repos/' + quote(REPOSITORY.split('/')[0], safe='') + '/' + quote(REPOSITORY.split('/')[1], safe='') + suffix


def _archive_url(url, token, budget, destination, expected_size, archive_limit):
    current = url
    digest = hashlib.sha256()
    written = 0
    flags = os.O_RDWR | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC
    fd = os.open(destination, flags, 0o600)
    created_identity = None
    try:
        created = os.fstat(fd)
        _need(stat.S_ISREG(created.st_mode) and created.st_nlink == 1
              and stat.S_IMODE(created.st_mode) == 0o600 and created.st_uid == os.geteuid(),
              'completed-run-archive-file')
        created_identity = (created.st_dev, created.st_ino)
        for hop in range(MAX_REDIRECTS + 1):
            transport._https_url(current, allowed_api=(hop == 0), download=(hop > 0))
            budget.count()
            headers = {'Accept': 'application/vnd.github+json', 'User-Agent': 'ForgeTrust-CompletedRun-Stage'}
            if hop == 0:
                headers['Authorization'] = 'Bearer ' + token
            request = Request(current, headers=headers)
            try:
                response = transport._open_request(request, budget.timeout())
            except HTTPError as error:
                response = error
            except (URLError, TimeoutError, OSError):
                raise ValueError('completed-run-download-request') from None
            with response:
                status = getattr(response, 'status', response.getcode())
                if status in (301, 302, 303, 307, 308):
                    _need(hop < MAX_REDIRECTS, 'completed-run-redirect-limit')
                    location = transport._header(response, 'Location')
                    _need(type(location) is str and 0 < len(location) <= 4096, 'completed-run-redirect')
                    current = urljoin(current, location)
                    transport._https_url(current, download=True)
                    continue
                _need(status == 200, 'completed-run-download-status')
                while True:
                    budget.timeout()
                    chunk = response.read(min(64 * 1024, archive_limit + 1 - written))
                    budget.timeout()
                    _need(type(chunk) is bytes, 'completed-run-download-chunk')
                    if not chunk:
                        break
                    written += len(chunk)
                    budget.charge_archive(len(chunk))
                    _need(written <= archive_limit and written <= expected_size,
                          'completed-run-archive-size')
                    digest.update(chunk)
                    view = memoryview(chunk)
                    while view:
                        count = os.write(fd, view)
                        _need(count > 0, 'completed-run-archive-write')
                        view = view[count:]
                _need(written == expected_size, 'completed-run-archive-size')
                break
        else:
            raise ValueError('completed-run-redirect-limit')
        os.fsync(fd)
        before_readback = os.fstat(fd)
        _need(stat.S_ISREG(before_readback.st_mode) and before_readback.st_nlink == 1
              and stat.S_IMODE(before_readback.st_mode) == 0o600 and before_readback.st_uid == os.geteuid()
              and before_readback.st_size == written,
              'completed-run-archive-file')
        os.lseek(fd, 0, os.SEEK_SET)
        readback_digest = hashlib.sha256()
        readback_size = 0
        while True:
            budget.timeout()
            chunk = os.read(fd, 64 * 1024)
            if not chunk:
                break
            readback_size += len(chunk)
            _need(readback_size <= expected_size, 'completed-run-archive-readback-size')
            readback_digest.update(chunk)
        after_readback = os.fstat(fd)
        identity_before = (before_readback.st_dev, before_readback.st_ino, before_readback.st_mode,
                           before_readback.st_nlink, before_readback.st_size, before_readback.st_mtime_ns,
                           before_readback.st_ctime_ns)
        identity_after = (after_readback.st_dev, after_readback.st_ino, after_readback.st_mode,
                          after_readback.st_nlink, after_readback.st_size, after_readback.st_mtime_ns,
                          after_readback.st_ctime_ns)
        _need(readback_size == expected_size == written and identity_before == identity_after
              and readback_digest.hexdigest() == digest.hexdigest(),
              'completed-run-archive-readback')
        try:
            named = os.lstat(destination)
        except OSError:
            raise ValueError('completed-run-archive-path-replaced') from None
        _need(stat.S_ISREG(named.st_mode) and named.st_nlink == 1
              and (named.st_dev, named.st_ino) == created_identity
              and named.st_size == readback_size and stat.S_IMODE(named.st_mode) == 0o600
              and named.st_uid == os.geteuid(), 'completed-run-archive-path-replaced')
        return readback_digest.hexdigest(), readback_size, created_identity
    except Exception:
        _remove_owned(destination, created_identity)
        raise
    finally:
        try:
            os.close(fd)
        except OSError:
            pass


def resolve_completed_run(event_raw, environment, checkout_head, current_capture, workflow_bytes,
                          token, stage_directory, *, total_timeout=MAX_TOTAL_SECONDS):
    """Bind a completed protected run and all fixed raw archives into a fresh private stage.

    The returned value is transport metadata only. Archive content interpretation and any
    qualification decision must happen later in source-owned validators.
    """
    budget = _Budget(total_timeout)
    _need(environment.get('GITHUB_ACTIONS') == 'true' and environment.get('GITHUB_REPOSITORY') == REPOSITORY
          and environment.get('GITHUB_EVENT_NAME') == 'workflow_run'
          and environment.get('GITHUB_REF') == 'refs/heads/main'
          and environment.get('GITHUB_REF_PROTECTED') == 'true', 'completed-run-local-context')
    repository_id, run_id, attempt, head_sha, workflow_id = _event_projection(event_raw)
    _need(type(checkout_head) is str and SHA1.fullmatch(checkout_head) and checkout_head == head_sha
          and type(token) is str and 0 < len(token) <= 4096
          and not any(character.isspace() for character in token), 'completed-run-token-or-head')
    capture = _source_projection(current_capture, workflow_bytes, head_sha)
    stage = Path(stage_directory)
    _need(stage.is_absolute() and stage.resolve(strict=True) == stage and stage.is_dir()
          and stat.S_IMODE(stage.stat().st_mode) == 0o700 and stage.stat().st_uid == os.geteuid()
          and not any(stage.iterdir()),
          'completed-run-stage-directory')

    base = _url('')
    repository = transport._api_json(base, token, budget)
    _need(repository.get('full_name') == REPOSITORY and type(repository.get('id')) is int
          and repository.get('id') == repository_id
          and repository.get('default_branch') == 'main', 'completed-run-repository-api')
    run = transport._api_json(_url('/actions/runs/' + str(run_id) + '/attempts/' + str(attempt)), token, budget)
    _need(_api_run_matches(run, repository_id, run_id, attempt, head_sha, workflow_id),
          'completed-run-api-identity')
    _need(_api_run_matches(run, repository_id, run_id, attempt, head_sha, workflow_id),
          'completed-run-api-identity')
    for path in (WORKFLOW, RUNTIME_WORKFLOW):
        content = transport._api_json(_url('/contents/' + path + '?ref=' + head_sha), token, budget)
        _need(_decode_workflow_content(content) == workflow_bytes[path], 'completed-run-workflow-api-bytes')
    jobs = transport._api_json(_url('/actions/runs/' + str(run_id) + '/attempts/' + str(attempt)
                                    + '/jobs?per_page=100'), token, budget)
    job_rows = jobs.get('jobs')
    _need(type(jobs.get('total_count')) is int and type(job_rows) is list
          and jobs['total_count'] == len(job_rows) <= 100 and all(type(row) is dict for row in job_rows),
          'completed-run-jobs-page')
    job_facts = _required_job_facts(job_rows, head_sha, attempt)
    job_ids = tuple(sorted(row.id for row in job_facts))
    artifact_response = transport._api_json(_url('/actions/runs/' + str(run_id) + '/artifacts?per_page=100'),
                                            token, budget)
    rows = artifact_response.get('artifacts')
    _need(type(artifact_response.get('total_count')) is int and type(rows) is list
          and artifact_response['total_count'] == len(rows) <= 100 and all(type(row) is dict for row in rows),
          'completed-run-artifact-page')
    chosen_artifacts = _artifact_inventory(rows, run_id, attempt, head_sha)

    bindings = []
    created = []
    try:
        for metadata, name, size, expected_digest in chosen_artifacts:
            budget.timeout()
            filename = name + '.zip'
            destination = stage / filename
            actual_digest, actual_size, identity = _archive_url(
                _url('/actions/artifacts/' + str(metadata['id']) + '/zip'), token, budget,
                destination, size, _artifact_limit(name))
            created.append((destination, identity))
            _need(actual_digest == expected_digest, 'completed-run-artifact-digest')
            bindings.append(ArtifactBinding(metadata['id'], name, actual_size, actual_digest, filename))
        budget.timeout()
    except Exception:
        cleanup_results = [_remove_owned(path, identity) for path, identity in created]
        cleaned = all(cleanup_results)
        if not cleaned:
            raise ValueError('completed-run-stage-cleanup') from None
        raise
    return CompletedRunStage(repository_id, run_id, attempt, head_sha, WORKFLOW,
                             tuple((path, hashlib.sha256(workflow_bytes[path]).hexdigest())
                                   for path in (WORKFLOW, RUNTIME_WORKFLOW)), capture['tree'],
                             hashlib.sha256(current_capture).hexdigest(),
                             job_ids, tuple(bindings), budget.archive_bytes, job_facts)
