"""Protected-workflow record selection; this module grants no runtime admission."""
from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path
import re
import stat
import subprocess
import sys
import time

import dispatch
import github_parent


ROOT = Path(__file__).resolve().parent
REPOSITORY = 'forge-trust/AppSurface'
WORKFLOW_PATH = '.github/workflows/evidencehost-protected-parent.yml'
REGISTRY_SCHEMA = 'issue779-protected-parent-records-v1'
MAX_REGISTRY_BYTES = 64 * 1024
MAX_RECORDS = 64
_ID = re.compile(r'[a-z0-9][a-z0-9-]{0,63}\Z')
_SHA = re.compile(r'[a-f0-9]{40}\Z')
_DIGEST = re.compile(r'[a-f0-9]{64}\Z')


def _read_fixed_file(path, maximum):
    """Read one fixed protected-checkout file, rejecting links and unstable metadata."""
    with os.fdopen(os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK), 'rb') as stream:
        before = os.fstat(stream.fileno())
        if (not stat.S_ISREG(before.st_mode) or before.st_nlink != 1
                or not 0 < before.st_size <= maximum):
            raise ValueError('protected-entry-file')
        raw = stream.read(maximum + 1)
        after = os.fstat(stream.fileno())
        named = os.stat(path, follow_symlinks=False)
        fingerprint = lambda value: (value.st_dev, value.st_ino, value.st_mode, value.st_nlink,
                                     value.st_size, value.st_mtime_ns, value.st_ctime_ns)
        if (len(raw) != before.st_size or fingerprint(before) != fingerprint(after)
                or fingerprint(after) != fingerprint(named)):
            raise ValueError('protected-entry-file-changed')
        return raw


def _registry(raw):
    value = dispatch._json_object(raw, MAX_REGISTRY_BYTES, 'protected-entry-registry')
    dispatch._exact(value, {'schema', 'records'}, 'protected-entry-registry-fields')
    if value['schema'] != REGISTRY_SCHEMA or type(value['records']) is not list or len(value['records']) > MAX_RECORDS:
        raise ValueError('protected-entry-registry')
    records = {}
    for record in value['records']:
        dispatch._exact(record, {'id', 'context_sha256', 'repository_id'}, 'protected-entry-record-fields')
        if (type(record['id']) is not str or _ID.fullmatch(record['id']) is None
                or record['id'] in records or type(record['context_sha256']) is not str
                or _DIGEST.fullmatch(record['context_sha256']) is None
                or type(record['repository_id']) is not int or not 0 < record['repository_id'] < 2**63):
            raise ValueError('protected-entry-record')
        records[record['id']] = record
    return records


def _require_protected_workflow(environment, checkout_head):
    """Compare provider-supplied facts only inside the independently protected workflow."""
    if (environment.get('GITHUB_ACTIONS') != 'true'
            or environment.get('GITHUB_REPOSITORY') != REPOSITORY
            or environment.get('GITHUB_EVENT_NAME') != 'workflow_dispatch'
            or environment.get('GITHUB_REF') != 'refs/heads/main'
            or environment.get('GITHUB_REF_PROTECTED') != 'true'
            or environment.get('GITHUB_WORKFLOW_REF') != REPOSITORY + '/' + WORKFLOW_PATH + '@refs/heads/main'
            or type(checkout_head) is not str or _SHA.fullmatch(checkout_head) is None
            or checkout_head != environment.get('GITHUB_SHA')
            or checkout_head != environment.get('GITHUB_WORKFLOW_SHA')):
        raise ValueError('protected-entry-workflow')


def run_reviewed_record(record_id, environment, token, *, dotnet_host, checkout_head, total_timeout=150):
    """Select a checked-in hash-bound record, then resolve and semantically check its completed producer run.

    The record ID is the only dispatch input. Context bytes, expected facts, policy, diff and
    collected artifact bodies come exclusively from the reviewed protected checkout. The
    provider environment is not authentication when this function is called outside that workflow.
    """
    if type(total_timeout) not in (int, float) or not 0 < total_timeout <= 150:
        raise ValueError('protected-entry-deadline')
    deadline = time.monotonic() + total_timeout
    _require_protected_workflow(environment, checkout_head)
    if type(record_id) is not str or _ID.fullmatch(record_id) is None:
        raise ValueError('protected-entry-record-id')
    records = _registry(_read_fixed_file(ROOT / 'protected-records.json', MAX_REGISTRY_BYTES))
    if record_id not in records:
        raise ValueError('protected-entry-unregistered-record')
    record = records[record_id]
    if environment.get('GITHUB_REPOSITORY_ID') != str(record['repository_id']):
        raise ValueError('protected-entry-repository-id')
    # No record-controlled filename, URL, executable, selector or argument is accepted.
    directory = ROOT / 'records'
    if directory.is_symlink() or not directory.is_dir():
        raise ValueError('protected-entry-record-directory')
    raw = _read_fixed_file(directory / (record_id + '.json'), dispatch.MAX_CONTEXT_BYTES)
    if hashlib.sha256(raw).hexdigest() != record['context_sha256']:
        raise ValueError('protected-entry-context-digest')
    if type(token) is not str or not token or len(token) > 4096 or any(char.isspace() for char in token):
        raise ValueError('protected-entry-token')
    remaining = deadline - time.monotonic()
    if remaining <= 0:
        raise ValueError('protected-entry-deadline')
    # Read the default-branch fact through authenticated repository metadata,
    # not through a dispatch input or an uploaded expectation.
    budget = github_parent._Budget(min(15, remaining))
    repository = github_parent._api_json(github_parent.API_ORIGIN + '/repos/' + REPOSITORY, token, budget)
    budget.timeout()
    if (repository.get('full_name') != REPOSITORY or type(repository.get('id')) is not int
            or repository['id'] != record['repository_id'] or repository.get('default_branch') != 'main'):
        raise ValueError('protected-entry-default-repository')
    remaining = deadline - time.monotonic()
    if remaining <= 0:
        raise ValueError('protected-entry-deadline')
    decision = dispatch.dispatch_protected_parent(raw, token, dotnet_host=dotnet_host, total_timeout=remaining)
    if time.monotonic() >= deadline:
        raise ValueError('protected-entry-deadline')
    return decision


def main():
    try:
        deadline = time.monotonic() + 150
        if len(sys.argv) != 2:
            raise ValueError('protected-entry-arguments')
        repository_root = ROOT.parents[1]
        result = subprocess.run(['/usr/bin/git', 'rev-parse', '--verify', 'HEAD'], cwd=repository_root,
                                stdin=subprocess.DEVNULL, capture_output=True, timeout=3, check=True)
        head = result.stdout.decode('ascii', 'strict').strip()
        _require_protected_workflow(os.environ, head)
        host = Path(os.environ['DOTNET_ROOT']) / 'dotnet'
        decision = run_reviewed_record(sys.argv[1], os.environ, os.environ.get('GH_TOKEN'),
                                       dotnet_host=host.resolve(strict=True), checkout_head=head,
                                       total_timeout=deadline - time.monotonic())
        if decision.status == 'semantic-gate-allowed':
            print('EVIDENCE_PROTECTED_PARENT_SEMANTIC_ALLOWED')
            return 0
        print('EVIDENCE_PROTECTED_PARENT_DENIED', file=sys.stderr)
        return 1
    except (ValueError, OSError, KeyError, UnicodeError, subprocess.SubprocessError):
        print('EVIDENCE_PROTECTED_PARENT_INPUT_REJECTED', file=sys.stderr)
        return 2


if __name__ == '__main__':
    raise SystemExit(main())
