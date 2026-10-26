"""Bounded GitHub API resolver for the fixed protected-parent evidence artifact.

This module has no import-time I/O. It accepts a token as an argument and returns
only the two byte documents selected by the independent archive preflight.
"""
from dataclasses import dataclass
import base64
import hashlib
import json
import time
from urllib.error import HTTPError, URLError
from urllib.parse import quote, urlencode, urljoin, urlsplit
from urllib.request import HTTPRedirectHandler, Request, build_opener

from provenance import (
    MAX_ARCHIVE_BYTES, MAX_METADATA_BYTES, _LOWER_SHA1, _LOWER_SHA256,
    _actual_artifact, _expectation, _pairs_without_duplicates, _repository, _sha, _workflow_path,
    preflight_protected_parent,
)


API_ORIGIN = 'https://api.github.com'
PROTECTED_REPOSITORY = 'forge-trust/AppSurface'
API_VERSION = '2022-11-28'
MAX_API_RESPONSE_BYTES = 1024 * 1024
MAX_HTTP_REQUESTS = 20
MAX_ARTIFACT_PAGES = 10
ARTIFACTS_PER_PAGE = 100
MAX_REDIRECTS = 3
REQUEST_TIMEOUT_SECONDS = 15.0
DEFAULT_TOTAL_TIMEOUT_SECONDS = 120.0
ALLOWED_DOWNLOAD_HOSTS = frozenset(('release-assets.githubusercontent.com',))
ALLOWED_DOWNLOAD_SUFFIXES = ('.blob.core.windows.net',)
ALLOWED_GITHUB_S3_HOST_PREFIX = 'github-production-repository-file-'


@dataclass(frozen=True)
class ProtectedParentResolution:
    """Selected document bytes plus exact bounded source metadata; not a gate result."""

    plan_bytes: bytes
    manifest_bytes: bytes
    run_id: int
    run_attempt: int
    head_sha: str
    workflow_source_commit: str
    workflow_sha: str
    artifact_id: int
    artifact_sha256: str
    artifact_bytes: int


class _NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, request, response, code, message, headers, new_url):
        return None


class _Budget:
    def __init__(self, total_seconds):
        if type(total_seconds) not in (int, float) or not 0 < total_seconds <= DEFAULT_TOTAL_TIMEOUT_SECONDS:
            raise ValueError('protected-parent-deadline')
        self.deadline = time.monotonic() + total_seconds
        self.requests = 0

    def timeout(self):
        remaining = self.deadline - time.monotonic()
        if remaining <= 0:
            raise ValueError('protected-parent-deadline')
        return min(REQUEST_TIMEOUT_SECONDS, remaining)

    def count(self):
        self.requests += 1
        if self.requests > MAX_HTTP_REQUESTS:
            raise ValueError('protected-parent-request-limit')


def _open_request(request, timeout):
    """Narrow transport seam; the production opener refuses automatic redirects."""
    return build_opener(_NoRedirect()).open(request, timeout=timeout)


def _read_response(response, maximum, budget):
    chunks = []
    total = 0
    while total <= maximum:
        budget.timeout()
        chunk = response.read(min(64 * 1024, maximum + 1 - total))
        budget.timeout()
        if type(chunk) is not bytes:
            raise ValueError('protected-parent-response-type')
        if not chunk:
            return b''.join(chunks)
        chunks.append(chunk)
        total += len(chunk)
    raise ValueError('protected-parent-response-size')


def _response_json(raw, code):
    if not raw or len(raw) > MAX_API_RESPONSE_BYTES:
        raise ValueError(code)
    try:
        value = json.loads(raw.decode('utf-8'), object_pairs_hook=_pairs_without_duplicates)
    except (UnicodeDecodeError, json.JSONDecodeError, RecursionError, ValueError):
        raise ValueError(code) from None
    if type(value) is not dict:
        raise ValueError(code)
    return value


def _header(response, name):
    headers = getattr(response, 'headers', None)
    value = headers.get(name) if headers is not None else None
    return value if type(value) is str else None


def _https_url(url, *, allowed_api=False, download=False):
    try:
        parsed = urlsplit(url)
        host = parsed.hostname.lower() if parsed.hostname else ''
        port = parsed.port
    except ValueError:
        raise ValueError('protected-parent-url') from None
    allowed = (host == 'api.github.com' if allowed_api else
               host in ALLOWED_DOWNLOAD_HOSTS
               or (host.startswith(ALLOWED_GITHUB_S3_HOST_PREFIX) and host.endswith('.s3.amazonaws.com'))
               or any(host.endswith(suffix) for suffix in ALLOWED_DOWNLOAD_SUFFIXES)
               if download else False)
    if parsed.scheme != 'https' or not allowed or parsed.username or parsed.password or port not in (None, 443):
        raise ValueError('protected-parent-url-origin')
    if parsed.fragment:
        raise ValueError('protected-parent-url-fragment')
    return parsed


def _api_json(url, token, budget):
    _https_url(url, allowed_api=True)
    budget.count()
    request = Request(url, headers={
        'Accept': 'application/vnd.github+json',
        'Authorization': 'Bearer ' + token,
        'X-GitHub-Api-Version': API_VERSION,
        'User-Agent': 'ForgeTrust-ProtectedParent-Preflight',
    })
    try:
        with _open_request(request, budget.timeout()) as response:
            if getattr(response, 'status', response.getcode()) != 200:
                raise ValueError('protected-parent-api-status')
            return _response_json(_read_response(response, MAX_API_RESPONSE_BYTES, budget),
                                 'protected-parent-api-json')
    except (HTTPError, URLError, TimeoutError, OSError):
        raise ValueError('protected-parent-api-request') from None


def _download(url, token, budget):
    current = url
    for hop in range(MAX_REDIRECTS + 1):
        _https_url(current, allowed_api=(hop == 0), download=(hop > 0))
        budget.count()
        headers = {'Accept': 'application/vnd.github+json',
                   'User-Agent': 'ForgeTrust-ProtectedParent-Preflight'}
        if hop == 0:
            headers['Authorization'] = 'Bearer ' + token
        # Redirect targets never receive the API bearer token.
        request = Request(current, headers=headers)
        try:
            response = _open_request(request, budget.timeout())
        except HTTPError as error:
            response = error
        except (URLError, TimeoutError, OSError):
            raise ValueError('protected-parent-download-request') from None
        with response:
            status = getattr(response, 'status', response.getcode())
            if status in (301, 302, 303, 307, 308):
                if hop == MAX_REDIRECTS:
                    raise ValueError('protected-parent-redirect-limit')
                location = _header(response, 'Location')
                if not location or len(location) > 4096:
                    raise ValueError('protected-parent-redirect-location')
                next_url = urljoin(current, location)
                _https_url(next_url, download=True)
                current = next_url
                continue
            if status != 200:
                raise ValueError('protected-parent-download-status')
            return _read_response(response, MAX_ARCHIVE_BYTES, budget)
    raise ValueError('protected-parent-redirect-limit')


def _api_url(repository, suffix):
    owner, name = repository.split('/', 1)
    return API_ORIGIN + '/repos/' + quote(owner, safe='') + '/' + quote(name, safe='') + suffix


def _workflow_file_digests(raw):
    blob_sha1 = hashlib.sha1(b'blob ' + str(len(raw)).encode('ascii') + b'\0' + raw).hexdigest()
    return blob_sha1, hashlib.sha256(raw).hexdigest()


def _decode_content(value):
    if value.get('encoding') != 'base64' or type(value.get('content')) is not str:
        raise ValueError('protected-parent-workflow-content')
    try:
        content = value['content'].encode('ascii', 'strict')
    except UnicodeEncodeError:
        raise ValueError('protected-parent-workflow-content') from None
    if len(content) > MAX_METADATA_BYTES * 2:
        raise ValueError('protected-parent-workflow-content-size')
    content = b''.join(content.split())
    try:
        decoded = base64.b64decode(content, validate=True)
    except (ValueError, UnicodeEncodeError):
        raise ValueError('protected-parent-workflow-content') from None
    if not decoded or len(decoded) > MAX_METADATA_BYTES:
        raise ValueError('protected-parent-workflow-content-size')
    return decoded


def _run_projection(run, expected, protected_facts):
    repository = run.get('repository')
    head_repository = run.get('head_repository')
    if type(repository) is not dict or repository.get('full_name') != expected['repository']:
        raise ValueError('protected-parent-run-repository')
    if (type(repository.get('id')) is not int or repository['id'] <= 0
            or type(head_repository) is not dict
            or head_repository.get('full_name') != expected['repository']
            or type(head_repository.get('id')) is not int
            or head_repository['id'] != repository['id']):
        raise ValueError('protected-parent-run-head-repository')
    if (run.get('id') != expected['run_id'] or type(run.get('id')) is not int
            or run.get('run_attempt') != expected['run_attempt']
            or type(run.get('run_attempt')) is not int
            or run.get('head_sha') != expected['head_sha']
            or run.get('event') not in ('push', 'workflow_dispatch')
            or run.get('status') != 'completed' or run.get('conclusion') != 'success'):
        raise ValueError('protected-parent-run-mismatch')
    run_path = run.get('path')
    if (type(run_path) is not str or
            not (run_path == protected_facts['workflow_path']
                 or run_path.startswith(protected_facts['workflow_path'] + '@'))
            or len(run_path) > 512 or any(ord(c) < 0x20 or ord(c) == 0x7f for c in run_path)):
        raise ValueError('protected-parent-workflow-path')
    if run_path != protected_facts['workflow_path'] and not run_path[len(protected_facts['workflow_path']) + 1:]:
        raise ValueError('protected-parent-workflow-path')
    return {
        'repository': expected['repository'], 'run_id': run['id'],
        'run_attempt': run['run_attempt'], 'head_sha': run['head_sha'],
        'workflow_path': protected_facts['workflow_path'],
        'workflow_sha': protected_facts['workflow_sha'],
        'base_sha': protected_facts['base_sha'], 'subject_sha': protected_facts['subject_sha'],
        'event': run['event'],
        'repository_id': repository.get('id'),
        'head_repository_id': head_repository['id'],
    }


def _fetch_workflow_bytes(repository, path, commit, expected_digest, token, budget):
    content_url = _api_url(repository, '/contents/' + quote(path, safe='/'))
    content_url += '?' + urlencode({'ref': commit})
    content = _api_json(content_url, token, budget)
    if content.get('path') != path:
        raise ValueError('protected-parent-workflow-content-pin')
    workflow_bytes = _decode_content(content)
    actual_blob_sha, actual_raw_sha256 = _workflow_file_digests(workflow_bytes)
    if (content.get('sha') != actual_blob_sha
            or expected_digest not in (actual_blob_sha, actual_raw_sha256)):
        raise ValueError('protected-parent-workflow-content-pin')
    return workflow_bytes


def _artifacts(repository, run_id, artifact_id, token, budget):
    suffix = '/actions/runs/{}/artifacts'.format(run_id)
    found = []
    total = None
    seen_ids = set()
    for page in range(1, MAX_ARTIFACT_PAGES + 1):
        query = urlencode({'per_page': ARTIFACTS_PER_PAGE, 'page': page})
        payload = _api_json(_api_url(repository, suffix) + '?' + query, token, budget)
        count = payload.get('total_count')
        records = payload.get('artifacts')
        if type(count) is not int or count < 0 or count > MAX_ARTIFACT_PAGES * ARTIFACTS_PER_PAGE:
            raise ValueError('protected-parent-artifact-pagination')
        if type(records) is not list or len(records) > ARTIFACTS_PER_PAGE:
            raise ValueError('protected-parent-artifact-pagination')
        if total is None:
            total = count
        elif total != count:
            raise ValueError('protected-parent-artifact-pagination')
        for item in records:
            if type(item) is not dict or type(item.get('id')) is not int or item['id'] in seen_ids:
                raise ValueError('protected-parent-artifact-pagination')
            seen_ids.add(item['id'])
            if item['id'] == artifact_id:
                found.append(item)
        if len(seen_ids) >= total:
            if len(seen_ids) != total:
                raise ValueError('protected-parent-artifact-pagination')
            break
        if not records:
            raise ValueError('protected-parent-artifact-pagination')
    else:
        raise ValueError('protected-parent-artifact-page-limit')
    if len(found) != 1:
        raise ValueError('protected-parent-artifact-identity')
    return found[0]


def resolve_protected_parent(expectation_bytes, protected_facts, token, *, total_timeout=DEFAULT_TOTAL_TIMEOUT_SECONDS):
    """Resolve a fixed parent run through GitHub's API and return its selected evidence documents.

    `protected_facts` must be independently obtained from the caller's protected source and
    contain exactly workflow_path, workflow_sha, workflow_source_commit, base_sha, and subject_sha. `token` is supplied
    explicitly and is never read from the environment. This resolver is not a gate or admission.
    """
    expected = _expectation(expectation_bytes)
    if expected['repository'] != PROTECTED_REPOSITORY:
        raise ValueError('protected-parent-repository')
    if type(protected_facts) is not dict or set(protected_facts) != {
            'workflow_path', 'workflow_sha', 'workflow_source_commit', 'base_sha', 'subject_sha'}:
        raise ValueError('protected-parent-facts')
    if type(token) is not str or not token or len(token) > 4096 or any(ord(c) < 0x21 or ord(c) > 0x7e for c in token):
        raise ValueError('protected-parent-token')
    for field in ('workflow_path', 'workflow_sha', 'base_sha', 'subject_sha'):
        if protected_facts[field] != expected[field]:
            raise ValueError('protected-parent-facts-mismatch')
    _repository(expected['repository'])
    facts = {
        'workflow_path': protected_facts['workflow_path'],
        'workflow_sha': protected_facts['workflow_sha'],
        'workflow_source_commit': protected_facts['workflow_source_commit'],
        'base_sha': protected_facts['base_sha'],
        'subject_sha': protected_facts['subject_sha'],
    }
    # Reuse the parser's validators for explicitly supplied protected workflow facts.
    _workflow_path(facts['workflow_path'])
    _sha(facts['workflow_sha'], _LOWER_SHA1 if len(facts['workflow_sha']) == 40 else _LOWER_SHA256,
         'protected-parent-workflow-sha')
    if type(facts['workflow_source_commit']) is not str:
        raise ValueError('protected-parent-workflow-source-commit')
    _sha(facts['workflow_source_commit'], _LOWER_SHA1 if len(facts['workflow_source_commit']) == 40
         else _LOWER_SHA256, 'protected-parent-workflow-source-commit')
    _sha(facts['base_sha'], _LOWER_SHA1, 'protected-parent-base-sha')
    _sha(facts['subject_sha'], _LOWER_SHA1, 'protected-parent-subject-sha')
    budget = _Budget(total_timeout)

    run_url = _api_url(expected['repository'], '/actions/runs/{}/attempts/{}'.format(
        expected['run_id'], expected['run_attempt']))
    run_raw = _api_json(run_url, token, budget)
    run_projection = _run_projection(run_raw, expected, facts)

    protected_workflow_bytes = _fetch_workflow_bytes(
        expected['repository'], facts['workflow_path'], facts['workflow_source_commit'],
        facts['workflow_sha'], token, budget)
    if expected['head_sha'] == facts['workflow_source_commit']:
        actual_workflow_bytes = protected_workflow_bytes
    else:
        actual_workflow_bytes = _fetch_workflow_bytes(
            expected['repository'], facts['workflow_path'], expected['head_sha'],
            facts['workflow_sha'], token, budget)
    if actual_workflow_bytes != protected_workflow_bytes:
        raise ValueError('protected-parent-run-workflow-mismatch')

    artifact = _artifacts(expected['repository'], expected['run_id'], expected['artifact_id'], token, budget)
    workflow_run = artifact.get('workflow_run')
    if (type(workflow_run) is not dict or workflow_run.get('id') != expected['run_id']
            or type(workflow_run.get('id')) is not int
            or workflow_run.get('head_sha') != expected['head_sha']
            or type(run_projection['repository_id']) is not int or run_projection['repository_id'] <= 0
            or workflow_run.get('repository_id') != run_projection['repository_id']
            or workflow_run.get('head_repository_id') != run_projection['head_repository_id']):
        raise ValueError('protected-parent-artifact-run-binding')
    artifact_raw = {
        'repository': expected['repository'], 'run_id': workflow_run['id'],
        'artifact_id': artifact.get('id'), 'artifact_name': artifact.get('name'),
        'expired': artifact.get('expired'), 'size_in_bytes': artifact.get('size_in_bytes'),
        'digest': artifact.get('digest'),
    }
    # Validate the listing before requesting its single exact artifact endpoint.
    _actual_artifact(json.dumps(artifact_raw, separators=(',', ':')).encode('utf-8'))
    if artifact_raw['artifact_id'] != expected['artifact_id'] or artifact_raw['artifact_name'] != expected['artifact_name']:
        raise ValueError('protected-parent-artifact-identity')
    if (artifact_raw['size_in_bytes'] != expected['artifact_bytes']
            or artifact_raw['digest'] != 'sha256:' + expected['artifact_sha256']):
        raise ValueError('protected-parent-artifact-metadata-pin')
    download_url = _api_url(expected['repository'], '/actions/artifacts/{}/zip'.format(expected['artifact_id']))
    archive_bytes = _download(download_url, token, budget)
    run_projection_bytes = json.dumps({key: run_projection[key] for key in (
        'repository', 'run_id', 'run_attempt', 'head_sha', 'workflow_path', 'workflow_sha',
        'base_sha', 'subject_sha', 'event')}, separators=(',', ':')).encode('utf-8')
    artifact_projection_bytes = json.dumps(artifact_raw, separators=(',', ':')).encode('utf-8')
    documents = preflight_protected_parent(expectation_bytes, run_projection_bytes,
                                           artifact_projection_bytes, archive_bytes)
    budget.timeout()
    return ProtectedParentResolution(
        documents.plan_bytes, documents.manifest_bytes, expected['run_id'], expected['run_attempt'],
        expected['head_sha'], facts['workflow_source_commit'], facts['workflow_sha'],
        expected['artifact_id'], expected['artifact_sha256'], expected['artifact_bytes'])
