"""Partial protected-workflow qualification evidence; never runtime admission or acceptance."""
from __future__ import annotations

import hashlib
import io
import json
import os
from pathlib import Path
import re
import stat
import struct
import subprocess
import sys
import time
import zipfile

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / '.github/issue779-protected-consumer'))
import github_parent as transport

REPOSITORY = 'forge-trust/AppSurface'
WORKFLOW = '.github/workflows/evidencehost-protected-qualification.yml'
RUNTIME_PREFIX = 'Full current runtime proof and unchanged gates / '
WORKER_PREFIX = RUNTIME_PREFIX + 'All sixteen current CSharp worker controls / '
PREFIX = WORKER_PREFIX
RUNTIME_WORKFLOW = '.github/workflows/evidencehost-runtime-proof.yml'
SELECTORS = ('None', 'N04', 'N05', 'N06', 'N07', 'N08', 'N09', 'N10', 'N11', 'N12', 'N13', 'N14', 'N15', 'N16')
CASES = tuple('N' + str(index).zfill(2) for index in range(1, 17))
REQUIRED_RUNTIME_JOBS = frozenset(
    {'Check the protected qualification source',
     RUNTIME_PREFIX + 'Protected parent transport and semantic controls (no admission)',
     RUNTIME_PREFIX + 'Packaged CSharp CLI producer and artifact custody / linux',
     RUNTIME_PREFIX + 'CSharp CLI and SDK application consumers / linux-native-application',
     RUNTIME_PREFIX + 'Genuine account creation with the successful C3 measurement / accounts',
     RUNTIME_PREFIX + 'Genuine reserved rollback with the successful account measurement / accounts',
     RUNTIME_PREFIX + 'Unchanged solution and codecov patch gates'}
    | {WORKER_PREFIX + 'Current CSharp worker ' + selector for selector in SELECTORS}
    | {WORKER_PREFIX + 'Reconcile all sixteen current worker controls'}
    | {RUNTIME_PREFIX + 'Standard root measurement for current C1 ' + selector
       + ' / Measure current CSharp worker ' + selector for selector in SELECTORS})
MAX_ARCHIVE = 64 * 1024
MAX_DOCUMENT = 16 * 1024
SHA1 = re.compile(r'[0-9a-f]{40}\Z')
SHA256 = re.compile(r'[0-9a-f]{64}\Z')


def need(condition):
    if not condition:
        raise ValueError('protected-qualification-rejected')


def context(environment, checkout_head):
    """Compare fixed provider context before API access; outside this workflow these are data only."""
    need(environment.get('GITHUB_ACTIONS') == 'true'
         and environment.get('GITHUB_REPOSITORY') == REPOSITORY
         and environment.get('GITHUB_EVENT_NAME') == 'workflow_dispatch'
         and environment.get('GITHUB_REF') == 'refs/heads/main'
         and environment.get('GITHUB_REF_PROTECTED') == 'true'
         and environment.get('GITHUB_WORKFLOW_REF') == REPOSITORY + '/' + WORKFLOW + '@refs/heads/main'
         and type(checkout_head) is str and SHA1.fullmatch(checkout_head) is not None
         and environment.get('GITHUB_SHA') == checkout_head
         and environment.get('GITHUB_WORKFLOW_SHA') == checkout_head)
    values = []
    for key in ('GITHUB_REPOSITORY_ID', 'GITHUB_RUN_ID', 'GITHUB_RUN_ATTEMPT'):
        raw = environment.get(key)
        need(type(raw) is str and raw.isascii() and raw.isdecimal() and 0 < len(raw) < 20
             and raw == str(int(raw)) and 0 < int(raw) < 2**63)
        values.append(int(raw))
    return tuple(values)


def read_fixed(path, maximum):
    """Read stable regular source/evidence bytes without following a final symlink."""
    fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK | os.O_CLOEXEC)
    try:
        before = os.fstat(fd)
        need(stat.S_ISREG(before.st_mode) and before.st_nlink == 1 and 0 < before.st_size <= maximum)
        raw = bytearray()
        while len(raw) <= before.st_size:
            chunk = os.read(fd, min(65536, before.st_size + 1 - len(raw)))
            if not chunk:
                break
            raw.extend(chunk)
        identity = lambda value: (value.st_dev, value.st_ino, value.st_mode, value.st_nlink,
                                  value.st_size, value.st_mtime_ns, value.st_ctime_ns)
        need(len(raw) == before.st_size and identity(before) == identity(os.fstat(fd))
             == identity(os.stat(path, follow_symlinks=False)))
        return bytes(raw)
    finally:
        os.close(fd)


def aggregate_archive(raw, metadata, source, capture_sha256):
    """Bind API digest before parsing one bounded aggregate leaf; selector callbacks remain unverified."""
    need(type(raw) is bytes and 0 < len(raw) <= MAX_ARCHIVE
         and type(metadata.get('size_in_bytes')) is int and metadata['size_in_bytes'] == len(raw)
         and metadata.get('digest') == 'sha256:' + hashlib.sha256(raw).hexdigest())
    # Prebound central-directory allocation before ZipFile constructs entry objects. No ZIP64/multidisk.
    offset = raw.rfind(b'PK\x05\x06', max(0, len(raw) - 65557))
    need(offset >= 0 and offset + 22 <= len(raw))
    end = struct.unpack_from('<4s4H2LH', raw, offset)
    need(end[1:5] == (0, 0, 1, 1) and end[5] <= 4096
         and end[6] + end[5] == offset and offset + 22 + end[7] == len(raw))
    with zipfile.ZipFile(io.BytesIO(raw)) as archive:
        rows = archive.infolist()
        need(len(rows) == 1)
        row = rows[0]
        mode = row.external_attr >> 16
        need(row.filename == 'aggregate.json' and not row.is_dir() and not row.flag_bits & 1
             and stat.S_IFMT(mode) in (0, stat.S_IFREG) and 0 < row.file_size <= MAX_DOCUMENT
             and len(row.extra) <= 4096 and len(row.comment) <= 4096
             and row.compress_type in (zipfile.ZIP_STORED, zipfile.ZIP_DEFLATED))
        with archive.open(row) as stream:
            body = stream.read(MAX_DOCUMENT + 1)
        need(len(body) == row.file_size)
    value = transport._response_json(body, 'protected-qualification-aggregate')
    need(set(value) == {'schema', 'source', 'source_capture_sha256', 'selectors', 'cases', 'passed',
                        'skipped', 'exit', 'native_acceptance', 'coverage_credit'}
         and value['schema'] == 'issue779-checkpoint1-aggregate-v1'
         and value['source'] == source and value['source_capture_sha256'] == capture_sha256
         and value['selectors'] == list(SELECTORS) and value['cases'] == list(CASES)
         and all(type(value[key]) is int and value[key] == expected
                 for key, expected in (('passed', 16), ('skipped', 0), ('exit', 0))))
    need(value['native_acceptance'] is False and value['coverage_credit'] is False)
    return body


def collect(environment, checkout_head, token, capture_raw, workflow_raw, *, total_timeout=120):
    """Resolve C1 material after the protected caller's full reusable runtime graph succeeds."""
    need(environment.get('ISSUE779_FULL_RUNTIME_RESULT') == 'success')
    repository_id, run_id, attempt = context(environment, checkout_head)
    need(type(token) is str and 0 < len(token) <= 4096 and not any(c.isspace() for c in token))
    capture = transport._response_json(capture_raw, 'protected-qualification-source')
    need(set(capture) == {'schema', 'source', 'tree', 'count', 'files'}
         and capture['schema'] == 'issue779-checkpoint2-source-v1'
         and type(capture['tree']) is str and SHA1.fullmatch(capture['tree']) is not None
         and capture.get('source') == checkout_head and type(capture.get('count')) is int
         and type(capture.get('files')) is dict and capture['count'] == len(capture['files']) > 0)
    runtime_entry = capture['files'].get(RUNTIME_WORKFLOW)
    need(type(runtime_entry) is dict and type(runtime_entry.get('sha256')) is str
         and SHA256.fullmatch(runtime_entry['sha256']) is not None
         and type(runtime_entry.get('git_blob')) is str and SHA1.fullmatch(runtime_entry['git_blob']) is not None)
    workflow_sha256 = hashlib.sha256(workflow_raw).hexdigest()
    budget = transport._Budget(total_timeout)
    api = transport.API_ORIGIN + '/repos/' + REPOSITORY
    repository = transport._api_json(api, token, budget)
    need(repository.get('full_name') == REPOSITORY and type(repository.get('id')) is int
         and repository['id'] == repository_id and repository.get('default_branch') == 'main')
    run = transport._api_json(api + '/actions/runs/' + str(run_id), token, budget)
    need(type(run.get('repository')) is dict
         and type(run.get('id')) is int and run['id'] == run_id
         and type(run.get('run_attempt')) is int and run['run_attempt'] == attempt
         and run.get('event') == 'workflow_dispatch' and run.get('head_branch') == 'main'
         and run.get('head_sha') == checkout_head
         and run.get('path') in (WORKFLOW, WORKFLOW + '@refs/heads/main')
         and type(run.get('repository', {}).get('id')) is int
         and run.get('repository', {}).get('id') == repository_id
         and run.get('repository', {}).get('full_name') == REPOSITORY
         and run.get('status') == 'in_progress' and run.get('conclusion') is None)
    content = transport._api_json(api + '/contents/' + WORKFLOW + '?ref=' + checkout_head, token, budget)
    need(transport._decode_content(content) == workflow_raw)
    jobs = transport._api_json(api + '/actions/runs/' + str(run_id) + '/attempts/' + str(attempt)
                               + '/jobs?per_page=100', token, budget)
    need(type(jobs.get('total_count')) is int and type(jobs.get('jobs')) is list
         and jobs['total_count'] == len(jobs['jobs']) <= 100
         and all(type(row) is dict for row in jobs['jobs']))
    chosen = [row for row in jobs['jobs'] if row.get('name') in REQUIRED_RUNTIME_JOBS]
    need(len(chosen) == len(REQUIRED_RUNTIME_JOBS)
         and {row['name'] for row in chosen} == REQUIRED_RUNTIME_JOBS
         and len({row.get('id') for row in chosen}) == len(REQUIRED_RUNTIME_JOBS)
         and all(row.get('status') == 'completed' and row.get('conclusion') == 'success'
                 and row.get('head_sha') == checkout_head and type(row.get('id')) is int
                 and row['id'] > 0 and type(row.get('run_attempt')) is int
                 and row['run_attempt'] == attempt for row in chosen))
    artifacts = transport._api_json(api + '/actions/runs/' + str(run_id) + '/artifacts?per_page=100', token, budget)
    need(type(artifacts.get('total_count')) is int and type(artifacts.get('artifacts')) is list
         and artifacts['total_count'] == len(artifacts['artifacts']) <= 100
         and all(type(row) is dict for row in artifacts['artifacts']))
    name = 'issue779-checkpoint1-current-' + str(run_id) + '-' + str(attempt)
    selected = [row for row in artifacts['artifacts'] if row.get('name') == name]
    need(len(selected) == 1)
    metadata = selected[0]
    need(type(metadata.get('workflow_run')) is dict
         and type(metadata.get('id')) is int and metadata['id'] > 0 and metadata.get('expired') is False
         and type(metadata.get('size_in_bytes')) is int and 0 < metadata['size_in_bytes'] <= MAX_ARCHIVE
         and type(metadata.get('digest')) is str and metadata['digest'].startswith('sha256:')
         and SHA256.fullmatch(metadata['digest'][7:]) is not None
         and type(metadata['workflow_run'].get('id')) is int
         and metadata['workflow_run']['id'] == run_id
         and metadata.get('workflow_run', {}).get('head_sha') == checkout_head)
    raw = transport._download(api + '/actions/artifacts/' + str(metadata['id']) + '/zip', token, budget)
    capture_sha256 = hashlib.sha256(capture_raw).hexdigest()
    body = aggregate_archive(raw, metadata, checkout_head, capture_sha256)
    budget.timeout()
    receipt = {'schema': 'issue779-protected-full-runtime-partial-v1', 'repository': REPOSITORY,
               'repository_id': repository_id, 'workflow_path': WORKFLOW, 'workflow_commit': checkout_head,
               'workflow_sha256': workflow_sha256, 'event': 'workflow_dispatch', 'ref': 'refs/heads/main',
               'runtime_workflow_path': RUNTIME_WORKFLOW,
               'runtime_workflow_sha256': runtime_entry['sha256'],
               'runtime_workflow_git_blob': runtime_entry['git_blob'],
               'runtime_job_result': environment['ISSUE779_FULL_RUNTIME_RESULT'],
               'run_id': run_id, 'run_attempt': attempt, 'observed_run_status': 'in_progress',
               'source_capture_sha256': capture_sha256, 'source_count': capture['count'],
               'source_tree': capture['tree'],
               'artifact_id': metadata['id'], 'artifact_name': name, 'artifact_bytes': len(raw),
               'artifact_sha256': hashlib.sha256(raw).hexdigest(),
               'aggregate_sha256': hashlib.sha256(body).hexdigest(),
               'runtime_job_ids': sorted(row['id'] for row in chosen),
               'selector_callbacks_independently_verified': False, 'qualification_complete': False,
               'admission_created': False, 'trusted_manifest_created': False,
               'gate_claimed': False, 'native_acceptance_claimed': False}
    return receipt, raw, body


def save(path, raw):
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC, 0o600)
    with os.fdopen(fd, 'wb') as stream:
        stream.write(raw)


def main():
    try:
        need(sys.argv[1:] in (['guard'], ['collect']))
        started = time.monotonic()
        head = subprocess.check_output(['/usr/bin/git', 'rev-parse', '--verify', 'HEAD'], cwd=ROOT,
                                       stdin=subprocess.DEVNULL, timeout=3).decode('ascii').strip()
        context(os.environ, head)
        if sys.argv[1] == 'guard':
            print('PROTECTED_QUALIFICATION_CONTEXT_CHECKED')
            return 0
        output = Path(os.environ['QUALIFICATION_EVIDENCE'])
        need(output.is_absolute() and output.resolve(strict=True) == output
             and stat.S_IMODE(output.stat().st_mode) == 0o700 and output.stat().st_uid == os.geteuid())
        capture = read_fixed(output / 'source.json', 1024 * 1024)
        workflow = read_fixed(ROOT / WORKFLOW, 1024 * 1024)
        receipt, archive, body = collect(os.environ, head, os.environ.get('GH_TOKEN'), capture, workflow,
                                         total_timeout=120 - (time.monotonic() - started))
        save(output / 'aggregate.zip', archive)
        save(output / 'aggregate.json', body)
        save(output / 'partial-qualification.json', json.dumps(receipt, sort_keys=True).encode('utf-8'))
        print('PROTECTED_QUALIFICATION_PARTIAL_RETAINED')
        return 0
    except (ValueError, OSError, KeyError, UnicodeError, subprocess.SubprocessError, zipfile.BadZipFile):
        print('PROTECTED_QUALIFICATION_REJECTED', file=sys.stderr)
        return 1


if __name__ == '__main__':
    raise SystemExit(main())
