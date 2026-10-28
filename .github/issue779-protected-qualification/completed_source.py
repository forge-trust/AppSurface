"""Preflight immutable completed-run source identities without executing that source."""
from __future__ import annotations

from dataclasses import dataclass, field
import hashlib
import math
import os
from pathlib import Path
import re
import resource
import stat
import subprocess
import tempfile
import time
from contextlib import contextmanager

import completed_c1
import completed_run
import completed_gate_replay

ROOT = Path(__file__).resolve().parents[2]
MAX_SECONDS = 120
MAX_CAPTURE_BYTES = completed_c1.MAX_SOURCE_BYTES
FUTURE_WORKFLOW = '.github/workflows/evidencehost-completed-validation.yml'
SHA1 = re.compile(r'[0-9a-f]{40}\Z')
SOURCE_MOUNT = completed_gate_replay.SOURCE_MOUNT
REQUIRED_CAPTURE_PATHS = frozenset((
    '.github/workflows/evidencehost-protected-qualification.yml',
    '.github/workflows/evidencehost-runtime-proof.yml',
    '.github/issue779-checkpoint1/binding.py',
    '.github/issue779-checkpoint1/run-native.py',
    '.github/issue779-checkpoint2/capture-source.py',
))
VALIDATOR_CAPTURE_PATHS = REQUIRED_CAPTURE_PATHS | {FUTURE_WORKFLOW}


def _need(condition, code):
    if not condition:
        raise ValueError('completed-source-' + code)


def _remaining(deadline):
    _need(type(deadline) in (int, float) and math.isfinite(deadline)
          and time.monotonic() < deadline, 'deadline')


def _strict_capture(raw, expected_head, *, required_paths=REQUIRED_CAPTURE_PATHS):
    value = completed_run._json(raw, MAX_CAPTURE_BYTES, 'completed-source-capture')
    _need(set(value) == {'schema', 'source', 'tree', 'count', 'files'}
          and value.get('schema') == 'issue779-checkpoint2-source-v1'
          and value.get('source') == expected_head and type(value.get('source')) is str
          and SHA1.fullmatch(value['source']) is not None
          and type(value.get('tree')) is str and SHA1.fullmatch(value['tree']) is not None
          and type(value.get('files')) is dict and required_paths <= set(value['files'])
          and type(value.get('count')) is int and value['count'] == len(value['files']) > 0,
          'capture-shape')
    return value


def _git(root, *args, deadline):
    _remaining(deadline)
    env = _git_environment()
    def limit_output_file():
        resource.setrlimit(resource.RLIMIT_FSIZE, (4096, 4096))

    with tempfile.TemporaryFile() as output:
        result = subprocess.run(
            ['/usr/bin/git', '--no-optional-locks', '-c', 'core.fsmonitor=false',
             '-c', 'safe.directory=' + str(root), *args], cwd=root,
            stdin=subprocess.DEVNULL, stdout=output, stderr=subprocess.DEVNULL,
            env=env, timeout=max(.001, min(10, deadline - time.monotonic())),
            preexec_fn=limit_output_file, check=False)
        output.seek(0)
        stdout = output.read(4097)
    _need(result.returncode == 0 and len(stdout) <= 4096, 'git')
    try:
        return stdout.decode('ascii').strip()
    except UnicodeDecodeError:
        raise ValueError('completed-source-git-output') from None


def _git_environment():
    return {'PATH': '/usr/bin:/bin', 'HOME': '/nonexistent', 'LANG': 'C.UTF-8',
            'LC_ALL': 'C', 'GIT_CONFIG_NOSYSTEM': '1', 'GIT_CONFIG_GLOBAL': '/dev/null',
            'GIT_NO_REPLACE_OBJECTS': '1'}


@contextmanager
def _controlled_process_environment():
    """Temporarily remove Git override/config state around the trusted verifier.

    Preflight is synchronous; callers must not run it concurrently with unrelated
    environment-sensitive threads in this process.
    """
    previous = dict(os.environ)
    controlled = _git_environment()
    os.environ.clear()
    os.environ.update(controlled)
    try:
        yield
    finally:
        os.environ.clear()
        os.environ.update(previous)


def _verify_source_with_trusted_binding(binding, value, root, deadline):
    """Run the independent verifier without inherited executable/Git overrides."""
    with _controlled_process_environment():
        binding.verify_source(value, root, deadline=deadline)


def _root(path, code, *, private=False):
    root = Path(path)
    _need(root.is_absolute() and root.resolve(strict=True) == root, code + '-canonical')
    info = root.lstat()
    _need(stat.S_ISDIR(info.st_mode) and info.st_uid == os.geteuid()
          and (not private or stat.S_IMODE(info.st_mode) == 0o700), code + '-shape')
    return root


def _fresh(root, deadline, *, forbid_ignored):
    args = ('status', '--porcelain=v1', '--untracked-files=all')
    if forbid_ignored:
        args += ('--ignored',)
    _need(_git(root, *args, deadline=deadline) == '', 'source-not-fresh')


def _source_identity(binding, raw, expected_head, root, deadline, code):
    value = _strict_capture(raw, expected_head)
    _verify_source_with_trusted_binding(binding, value, root, deadline)
    head = _git(root, 'rev-parse', 'HEAD', deadline=deadline)
    tree = _git(root, 'rev-parse', 'HEAD^{tree}', deadline=deadline)
    _need(head == expected_head and tree == value['tree'], code + '-git-identity')
    _fresh(root, deadline, forbid_ignored=True)
    return value, head, tree


@dataclass(frozen=True)
class SourceBinding:
    """Verified source identities only; no qualification or Trusted authority."""
    repository_id: int
    run_id: int
    run_attempt: int
    head_sha: str
    source_tree: str
    source_capture_sha256: str
    validator_head_sha: str
    validator_tree: str
    validator_capture_sha256: str
    admission_created: bool = field(default=False, init=False)
    native_acceptance: bool = field(default=False, init=False)
    qualification_complete: bool = field(default=False, init=False)
    gate_eligible: bool = field(default=False, init=False)


def validate_completed_source(event_raw, environment, validator_capture, source_capture,
                              source_root, native_source_root, *, deadline):
    """Bind a completed protected run to clean source checkouts without network or execution.

    The event is data from the predecessor run. `validator_capture` must describe the
    independently checked out current protected workflow commit; `source_capture` must
    describe the predecessor's completed source. Both are verified with the fixed current
    C1 source verifier before identities are returned.
    """
    _remaining(deadline)
    _need(0 < deadline - time.monotonic() <= MAX_SECONDS, 'deadline-bound')
    _need(type(environment) is dict and environment.get('GITHUB_ACTIONS') == 'true'
          and environment.get('GITHUB_EVENT_NAME') == 'workflow_run'
          and environment.get('GITHUB_REPOSITORY') == completed_run.REPOSITORY
          and environment.get('GITHUB_REF') == 'refs/heads/main'
          and environment.get('GITHUB_REF_PROTECTED') == 'true'
          and environment.get('GITHUB_WORKFLOW_REF') ==
          completed_run.REPOSITORY + '/' + FUTURE_WORKFLOW + '@refs/heads/main',
          'protected-context')
    repository_id, run_id, attempt, completed_head, _workflow_id = completed_run._event_projection(event_raw)
    _need(type(environment.get('GITHUB_REPOSITORY_ID')) is str
          and environment['GITHUB_REPOSITORY_ID'].isascii()
          and environment['GITHUB_REPOSITORY_ID'].isdecimal()
          and environment['GITHUB_REPOSITORY_ID'] == str(repository_id), 'repository-id')
    _need(type(environment.get('GITHUB_SHA')) is str
          and type(environment.get('GITHUB_WORKFLOW_SHA')) is str
          and environment['GITHUB_SHA'] == environment['GITHUB_WORKFLOW_SHA'], 'validator-context-head')

    validator = _root(ROOT, 'validator-root')
    source = _root(source_root, 'source-root')
    native = _root(native_source_root, 'native-source-root', private=True)
    _need(source == SOURCE_MOUNT, 'source-mount')
    roots = (validator, source, native)
    _need(all(not completed_c1._paths_overlap(left, right)
              for index, left in enumerate(roots) for right in roots[index + 1:]), 'root-overlap')

    binding = completed_c1._load_trusted_current_binding(deadline=deadline)
    trusted_head = _git(validator, 'rev-parse', 'HEAD', deadline=deadline)
    trusted_tree = _git(validator, 'rev-parse', 'HEAD^{tree}', deadline=deadline)
    _need(SHA1.fullmatch(trusted_head) is not None and SHA1.fullmatch(trusted_tree) is not None
          and environment['GITHUB_SHA'] == trusted_head, 'validator-head')
    validator_value = _strict_capture(validator_capture, trusted_head,
                                      required_paths=VALIDATOR_CAPTURE_PATHS)
    _verify_source_with_trusted_binding(binding, validator_value, validator, deadline)
    _need(validator_value['tree'] == trusted_tree, 'validator-capture-tree')
    _fresh(validator, deadline, forbid_ignored=True)

    source_value, verified_head, verified_tree = _source_identity(
        binding, source_capture, completed_head, source, deadline, 'completed-source')
    native_value, native_head, native_tree = _source_identity(
        binding, source_capture, completed_head, native, deadline, 'native-source')
    _need(native_value == source_value and native_head == verified_head
          and native_tree == verified_tree, 'native-source-binding')
    _need(_git(validator, 'merge-base', '--is-ancestor', completed_head, trusted_head,
               deadline=deadline) == '', 'completed-head-not-ancestor')
    _remaining(deadline)
    return SourceBinding(repository_id, run_id, attempt, completed_head, source_value['tree'],
                         hashlib.sha256(source_capture).hexdigest(), trusted_head, trusted_tree,
                         hashlib.sha256(validator_capture).hexdigest())


def verify_completed_source(binding, validator_capture, source_capture, source_root,
                            native_source_root, *, deadline):
    """Recheck the same identities after replay; target ignored build outputs are permitted.

    The orchestrating caller pins all three directory identities across its operation.
    This helper only revalidates bytes and Git state. It never imports code from target
    checkouts and does not permit the predecessor or validator commit to change.
    """
    _remaining(deadline)
    _need(0 < deadline - time.monotonic() <= MAX_SECONDS, 'deadline-bound')
    _need(type(binding) is SourceBinding and type(validator_capture) is bytes
          and type(source_capture) is bytes
          and hashlib.sha256(validator_capture).hexdigest() == binding.validator_capture_sha256
          and hashlib.sha256(source_capture).hexdigest() == binding.source_capture_sha256,
          'binding')
    _need(type(binding.repository_id) is int and binding.repository_id > 0
          and type(binding.run_id) is int and 0 < binding.run_id < 2**63
          and type(binding.run_attempt) is int and 0 < binding.run_attempt < 2**31
          and type(binding.head_sha) is str and type(binding.source_tree) is str
          and type(binding.validator_head_sha) is str and type(binding.validator_tree) is str
          and SHA1.fullmatch(binding.head_sha) is not None
          and SHA1.fullmatch(binding.source_tree) is not None
          and SHA1.fullmatch(binding.validator_head_sha) is not None
          and SHA1.fullmatch(binding.validator_tree) is not None
          and all(getattr(binding, name) is False for name in
                  ('admission_created', 'native_acceptance', 'qualification_complete', 'gate_eligible')),
          'binding-shape')
    validator = _root(ROOT, 'validator-root')
    source = _root(source_root, 'source-root')
    native = _root(native_source_root, 'native-source-root', private=True)
    _need(source == SOURCE_MOUNT, 'source-mount')
    roots = (validator, source, native)
    _need(all(not completed_c1._paths_overlap(left, right)
              for index, left in enumerate(roots) for right in roots[index + 1:]), 'root-overlap')
    trusted = completed_c1._load_trusted_current_binding(deadline=deadline)
    validator_value = _strict_capture(validator_capture, binding.validator_head_sha,
                                      required_paths=VALIDATOR_CAPTURE_PATHS)
    _verify_source_with_trusted_binding(trusted, validator_value, validator, deadline)
    _need(_git(validator, 'rev-parse', 'HEAD', deadline=deadline) == binding.validator_head_sha
          and _git(validator, 'rev-parse', 'HEAD^{tree}', deadline=deadline) == binding.validator_tree,
          'validator-changed')
    _need(validator_value['tree'] == binding.validator_tree, 'validator-capture-tree')
    _fresh(validator, deadline, forbid_ignored=True)
    source_value = _strict_capture(source_capture, binding.head_sha)
    _verify_source_with_trusted_binding(trusted, source_value, source, deadline)
    source_head = _git(source, 'rev-parse', 'HEAD', deadline=deadline)
    source_tree = _git(source, 'rev-parse', 'HEAD^{tree}', deadline=deadline)
    _fresh(source, deadline, forbid_ignored=False)
    native_value = _strict_capture(source_capture, binding.head_sha)
    _verify_source_with_trusted_binding(trusted, native_value, native, deadline)
    _need(_git(native, 'rev-parse', 'HEAD', deadline=deadline) == binding.head_sha
          and _git(native, 'rev-parse', 'HEAD^{tree}', deadline=deadline) == binding.source_tree,
          'native-source-changed')
    _fresh(native, deadline, forbid_ignored=False)
    _need(source_value['tree'] == binding.source_tree and source_head == binding.head_sha
          and source_tree == binding.source_tree
          and native_value['tree'] == binding.source_tree, 'completed-source-changed')
    _need(_git(validator, 'merge-base', '--is-ancestor', binding.head_sha,
               binding.validator_head_sha, deadline=deadline) == '', 'completed-head-not-ancestor')
    _remaining(deadline)
