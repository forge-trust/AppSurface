"""Fixed parent-side orchestration for provenance resolution and the semantic consumer."""
from __future__ import annotations

from dataclasses import dataclass
import base64
import hashlib
import json
import os
from pathlib import Path
import signal
import selectors
import subprocess
import time

import github_parent


SCHEMA = 'issue779-protected-parent-dispatch-v1'
SEMANTIC_GATE_DLL = (Path(__file__).resolve().parent / 'semantic-gate' / 'bin' / 'Release' /
                     'net10.0' / 'EvidenceHost.ProtectedParentGate.dll')
MAX_CONTEXT_BYTES = 24 * 1024 * 1024
MAX_ENVELOPE_BYTES = 20 * 1024 * 1024
MAX_DOCUMENT_BYTES = 1024 * 1024
MAX_ARTIFACTS = 64
MAX_CHILD_STREAM_BYTES = 4096
MAX_TOTAL_SECONDS = 150.0
CLEANUP_RESERVE_SECONDS = 4.0
_SHA256 = frozenset('0123456789abcdef')
_EXPECTED_KEYS = frozenset((
    'RunId', 'BaseRevision', 'SubjectRevision', 'WorkflowIdentity', 'PolicyDigest',
    'AcceptanceProofDigest', 'OutputIdentity', 'VerifierId', 'VerifierVersion', 'Provider',
    'CatalogueDigest', 'CapabilitiesDigest', 'AllocationPolicyDigest', 'ToolRootIdentity',
    'SubjectRootIdentity', 'OutputParentIdentity', 'AllowReleaseValidatedNotAttested',
))
_STRING_EXPECTED_KEYS = _EXPECTED_KEYS - {'AllowReleaseValidatedNotAttested'}


@dataclass(frozen=True)
class DispatchResult:
    """Fixed process result; not a protected-authenticity or Trusted receipt."""

    status: str
    exit_code: int | None
    diagnostic: str


class DispatchInputError(ValueError):
    pass


class CleanupUnconfirmed(RuntimeError):
    """The owned process group could not be fully stopped and reaped within its original budget."""


def _pairs_no_aliases(pairs):
    result = {}
    folded = set()
    for key, value in pairs:
        if type(key) is not str or key in result or key.casefold() in folded:
            raise DispatchInputError('dispatch-json-duplicate-or-case-alias')
        result[key] = value
        folded.add(key.casefold())
    return result


def _json_object(raw, maximum, code):
    if type(raw) is not bytes or not 0 < len(raw) <= maximum:
        raise DispatchInputError(code)
    try:
        value = json.loads(raw.decode('utf-8'), object_pairs_hook=_pairs_no_aliases)
    except (UnicodeDecodeError, json.JSONDecodeError, RecursionError, DispatchInputError):
        raise DispatchInputError(code) from None
    if type(value) is not dict:
        raise DispatchInputError(code)
    return value


def _exact(value, keys, code):
    if type(value) is not dict or set(value) != keys:
        raise DispatchInputError(code)


def _base64_field(value, name, maximum, *, allow_empty=False):
    encoded = value.get(name)
    if (type(encoded) is not str or (not encoded and not allow_empty)
            or len(encoded) > ((maximum + 2) // 3) * 4):
        raise DispatchInputError('dispatch-' + name)
    try:
        raw = base64.b64decode(encoded.encode('ascii', 'strict'), validate=True)
    except (ValueError, UnicodeEncodeError):
        raise DispatchInputError('dispatch-' + name) from None
    if (not raw and not allow_empty) or len(raw) > maximum:
        raise DispatchInputError('dispatch-' + name)
    return raw


def _sha256(raw, expected, code):
    if (type(expected) is not str or len(expected) != 64 or any(char not in _SHA256 for char in expected)
            or hashlib.sha256(raw).hexdigest() != expected):
        raise DispatchInputError(code)


def _checked_context(raw):
    context = _json_object(raw, MAX_CONTEXT_BYTES, 'dispatch-context')
    _exact(context, {'schema', 'expectation', 'protected_facts', 'policy_bytes', 'policy_sha256',
                     'diff_bytes', 'diff_sha256', 'expected_gate', 'artifacts'}, 'dispatch-context-fields')
    if context['schema'] != SCHEMA:
        raise DispatchInputError('dispatch-context-schema')
    expectation = _base64_field(context, 'expectation', MAX_DOCUMENT_BYTES)
    facts = context['protected_facts']
    _exact(facts, {'workflow_path', 'workflow_sha', 'workflow_source_commit', 'base_sha', 'subject_sha'},
           'dispatch-protected-facts')
    policy = _base64_field(context, 'policy_bytes', MAX_DOCUMENT_BYTES)
    diff = _base64_field(context, 'diff_bytes', MAX_DOCUMENT_BYTES)
    _sha256(policy, context['policy_sha256'], 'dispatch-policy-digest')
    _sha256(diff, context['diff_sha256'], 'dispatch-diff-digest')

    expected = context['expected_gate']
    _exact(expected, _EXPECTED_KEYS, 'dispatch-expected-gate-fields')
    for key in _STRING_EXPECTED_KEYS:
        item = expected[key]
        if (type(item) is not str or not item or len(item) > 1024
                or any(ord(char) < 0x20 or ord(char) == 0x7f for char in item)):
            raise DispatchInputError('dispatch-expected-gate-value')
    if type(expected['AllowReleaseValidatedNotAttested']) is not bool:
        raise DispatchInputError('dispatch-expected-gate-value')

    bodies = context['artifacts']
    if type(bodies) is not list or len(bodies) > MAX_ARTIFACTS:
        raise DispatchInputError('dispatch-artifacts')
    artifacts = []
    total_artifact_bytes = 0
    for body in bodies:
        _exact(body, {'ProducerId', 'RelativePath', 'Bytes'}, 'dispatch-artifact-fields')
        for key, max_length in (('ProducerId', 256), ('RelativePath', 1024)):
            item = body[key]
            if (type(item) is not str or not item or len(item) > max_length
                    or any(ord(char) < 0x20 or ord(char) == 0x7f for char in item)):
                raise DispatchInputError('dispatch-artifact-value')
        raw_body = _base64_field(body, 'Bytes', MAX_DOCUMENT_BYTES, allow_empty=True)
        total_artifact_bytes += len(raw_body)
        if total_artifact_bytes > MAX_ENVELOPE_BYTES:
            raise DispatchInputError('dispatch-artifact-total-size')
        artifacts.append({'ProducerId': body['ProducerId'], 'RelativePath': body['RelativePath'], 'Bytes': raw_body})
    return expectation, facts, policy, context['policy_sha256'], diff, context['diff_sha256'], expected, artifacts


def _semantic_envelope(resolution, policy, policy_sha, diff, diff_sha, expected, artifacts):
    encode = lambda raw: base64.b64encode(raw).decode('ascii')
    envelope = {
        'Schema': 'issue779-protected-parent-gate-v1',
        'PolicyBytes': encode(policy), 'PolicySha256': policy_sha,
        'DiffBytes': encode(diff), 'DiffSha256': diff_sha,
        'PlanBytes': encode(resolution.plan_bytes), 'ManifestBytes': encode(resolution.manifest_bytes),
        'Expected': expected,
        'Artifacts': [{'ProducerId': item['ProducerId'], 'RelativePath': item['RelativePath'],
                       'Bytes': encode(item['Bytes'])} for item in artifacts],
    }
    try:
        encoded = json.dumps(envelope, separators=(',', ':'), ensure_ascii=True).encode('ascii')
    except (TypeError, ValueError, UnicodeEncodeError):
        raise DispatchInputError('dispatch-envelope-encode') from None
    if len(encoded) > MAX_ENVELOPE_BYTES:
        raise DispatchInputError('dispatch-envelope-size')
    return encoded


def _child_environment():
    # The API token is intentionally absent. Keep only runtime-local settings needed by dotnet.
    allowed = {'PATH', 'HOME', 'TMPDIR', 'DOTNET_ROOT', 'DOTNET_ROOT_X64', 'DOTNET_ROOT_ARM64',
               'DOTNET_MULTILEVEL_LOOKUP', 'DOTNET_NOLOGO', 'DOTNET_CLI_TELEMETRY_OPTOUT', 'LANG', 'LC_ALL'}
    return {key: value for key, value in os.environ.items() if key in allowed}


def _group_signal(pgid, sig):
    try:
        os.killpg(pgid, sig)
        return True
    except ProcessLookupError:
        return False
    except PermissionError:
        raise CleanupUnconfirmed('dispatch-process-group-signal') from None


def _group_exists(pgid):
    try:
        os.killpg(pgid, 0)
        return True
    except ProcessLookupError:
        return False
    except PermissionError:
        raise CleanupUnconfirmed('dispatch-process-group-check') from None


def _stop_process_group(pgid, process, deadline):
    if not _group_exists(pgid):
        return
    _group_signal(pgid, signal.SIGTERM)
    grace_end = min(deadline, time.monotonic() + 0.4)
    while time.monotonic() < grace_end:
        try:
            process.wait(timeout=min(0.02, max(0.0, grace_end - time.monotonic())))
        except subprocess.TimeoutExpired:
            pass
        if process.poll() is not None and not _group_exists(pgid):
            return
        if not _group_exists(pgid):
            return
    if time.monotonic() >= deadline:
        raise CleanupUnconfirmed('dispatch-cleanup-deadline')
    if _group_exists(pgid):
        _group_signal(pgid, signal.SIGKILL)
    while time.monotonic() < deadline:
        if process.poll() is None:
            try:
                process.wait(timeout=min(0.02, max(0.0, deadline - time.monotonic())))
            except subprocess.TimeoutExpired:
                pass
        if process.poll() is not None and not _group_exists(pgid):
            return
        if not _group_exists(pgid):
            break
        time.sleep(min(0.02, max(0.0, deadline - time.monotonic())))
    if _group_exists(pgid):
        raise CleanupUnconfirmed('dispatch-process-group-remains')
    if process.poll() is None:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise CleanupUnconfirmed('dispatch-child-unreaped')
        try:
            process.wait(timeout=remaining)
        except subprocess.TimeoutExpired:
            raise CleanupUnconfirmed('dispatch-child-unreaped') from None


def _run_child(argv, payload, deadline, work_deadline=None):
    if os.name != 'posix':
        raise RuntimeError('dispatch-posix-process-group-required')
    work_deadline = deadline if work_deadline is None else work_deadline
    if time.monotonic() >= work_deadline or work_deadline > deadline:
        raise TimeoutError('dispatch-child-deadline')
    process = subprocess.Popen(argv, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                               shell=False, close_fds=True, start_new_session=True,
                               env=_child_environment())
    stdout = bytearray()
    stderr = bytearray()
    overflow = False
    write_failed = False
    offset = 0
    selector = selectors.DefaultSelector()
    pipes = {'stdout': process.stdout, 'stderr': process.stderr, 'stdin': process.stdin}
    try:
        for name in ('stdout', 'stderr'):
            os.set_blocking(pipes[name].fileno(), False)
            selector.register(pipes[name], selectors.EVENT_READ, name)
        os.set_blocking(pipes['stdin'].fileno(), False)
        if payload:
            selector.register(pipes['stdin'], selectors.EVENT_WRITE, 'stdin')
        else:
            pipes['stdin'].close()
        while True:
            if time.monotonic() >= work_deadline:
                raise TimeoutError('dispatch-child-deadline')
            if process.poll() is not None and not any(key.data in ('stdout', 'stderr') for key in selector.get_map().values()):
                if offset < len(payload):
                    write_failed = True
                break
            events = selector.select(min(0.05, max(0.0, work_deadline - time.monotonic())))
            if time.monotonic() >= work_deadline:
                raise TimeoutError('dispatch-child-deadline')
            for key, _ in events:
                name = key.data
                pipe = key.fileobj
                if name == 'stdin':
                    try:
                        written = os.write(pipe.fileno(), payload[offset:offset + 65536])
                        if written <= 0:
                            raise BrokenPipeError
                        offset += written
                        if offset == len(payload):
                            selector.unregister(pipe)
                            pipe.close()
                    except (BrokenPipeError, OSError):
                        write_failed = True
                        selector.unregister(pipe)
                        pipe.close()
                else:
                    try:
                        chunk = os.read(pipe.fileno(), 16384)
                    except BlockingIOError:
                        continue
                    if not chunk:
                        selector.unregister(pipe)
                        pipe.close()
                        continue
                    destination = stdout if name == 'stdout' else stderr
                    remaining = MAX_CHILD_STREAM_BYTES + 1 - len(destination)
                    if remaining > 0:
                        destination.extend(chunk[:remaining])
                    if len(chunk) > remaining or len(destination) > MAX_CHILD_STREAM_BYTES:
                        overflow = True
            if overflow:
                break
        if process.poll() is None:
            remaining = work_deadline - time.monotonic()
            if remaining <= 0:
                raise TimeoutError('dispatch-child-deadline')
            process.wait(timeout=remaining)
    except subprocess.TimeoutExpired:
        raise TimeoutError('dispatch-child-deadline') from None
    finally:
        selector.close()
        for pipe in pipes.values():
            try:
                pipe.close()
            except OSError:
                pass
        # Termination, direct-child reaping, inherited-pipe closure and final group
        # observation all consume the same reserved tail of the parent deadline.
        _stop_process_group(process.pid, process, deadline)
    if time.monotonic() >= deadline:
        raise CleanupUnconfirmed('dispatch-final-deadline')
    if process.poll() is None:
        raise CleanupUnconfirmed('dispatch-child-unreaped')
    if _group_exists(process.pid):
        raise CleanupUnconfirmed('dispatch-process-group-remains')
    return process.returncode, bytes(stdout), bytes(stderr), bool(overflow or write_failed)


_DIAGNOSTICS = {
    (0, b'EVIDENCE_PARENT_GATE_ALLOWED\n', b''): DispatchResult(
        'semantic-gate-allowed', 0, 'EVIDENCE_PARENT_GATE_ALLOWED'),
    (1, b'', b'EVIDENCE_PARENT_GATE_DENIED\n'): DispatchResult(
        'semantic-gate-denied', 1, 'EVIDENCE_PARENT_GATE_DENIED'),
    (2, b'', b'EVIDENCE_PARENT_GATE_INPUT_REJECTED\n'): DispatchResult(
        'semantic-gate-input-rejected', 2, 'EVIDENCE_PARENT_GATE_INPUT_REJECTED'),
    (3, b'', b'EVIDENCE_PARENT_GATE_FAILED\n'): DispatchResult(
        'semantic-gate-failed', 3, 'EVIDENCE_PARENT_GATE_FAILED'),
}


def dispatch_protected_parent(context_bytes, token, *, dotnet_host, total_timeout=MAX_TOTAL_SECONDS):
    """Resolve protected-parent documents, then invoke the fixed semantic executable once.

    `dotnet_host` is an explicit parent-owned canonical path, never context data. The executable
    DLL and empty argument list are fixed by this module. The caller supplies GH_TOKEN explicitly;
    it is used for resolver HTTP only and is removed from the child environment.
    """
    if (type(total_timeout) not in (int, float) or not 0 < total_timeout <= MAX_TOTAL_SECONDS):
        raise DispatchInputError('dispatch-deadline')
    started = time.monotonic()
    deadline = started + total_timeout
    host = Path(dotnet_host)
    if (not host.is_absolute() or host.name != 'dotnet' or '..' in host.parts
            or host != Path(os.path.normpath(os.fspath(host)))
            or host != Path(os.path.realpath(host))
            or any(ord(char) < 0x20 for char in os.fspath(host))):
        raise DispatchInputError('dispatch-dotnet-host')
    values = _checked_context(context_bytes)
    expectation, facts, policy, policy_sha, diff, diff_sha, expected, artifacts = values
    cleanup_reserve = min(CLEANUP_RESERVE_SECONDS, total_timeout * 0.1)
    work_deadline = deadline - cleanup_reserve
    remaining = min(github_parent.DEFAULT_TOTAL_TIMEOUT_SECONDS, work_deadline - time.monotonic())
    if remaining <= 0:
        raise DispatchInputError('dispatch-deadline')
    resolution = github_parent.resolve_protected_parent(expectation, facts, token, total_timeout=remaining)
    if time.monotonic() >= deadline:
        raise DispatchInputError('dispatch-deadline')
    expected_run_identity = 'github-actions-{}-{}'.format(resolution.run_id, resolution.run_attempt)
    if (expected['RunId'] != expected_run_identity or expected['SubjectRevision'] != resolution.head_sha
            or expected['BaseRevision'] != facts['base_sha']):
        raise DispatchInputError('dispatch-expected-run-binding')
    envelope = _semantic_envelope(resolution, policy, policy_sha, diff, diff_sha, expected, artifacts)
    consumer = SEMANTIC_GATE_DLL.resolve(strict=False)
    if not consumer.is_absolute() or consumer.name != 'EvidenceHost.ProtectedParentGate.dll':
        raise DispatchInputError('dispatch-consumer-path')
    argv = [os.fspath(host), os.fspath(consumer)]
    try:
        code, stdout, stderr, overflow = _run_child(argv, envelope, deadline, work_deadline=work_deadline)
    except TimeoutError:
        return DispatchResult('semantic-gate-timeout', None, 'DISPATCH_CHILD_DEADLINE')
    except CleanupUnconfirmed:
        return DispatchResult('semantic-gate-cleanup-unconfirmed', None, 'DISPATCH_CLEANUP_UNCONFIRMED')
    except (OSError, RuntimeError):
        return DispatchResult('semantic-gate-launch-failed', None, 'DISPATCH_CHILD_LAUNCH_FAILED')
    if overflow:
        return DispatchResult('semantic-gate-output-invalid', code, 'DISPATCH_CHILD_OUTPUT_LIMIT')
    return _DIAGNOSTICS.get((code, stdout, stderr),
                            DispatchResult('semantic-gate-output-invalid', code,
                                           'DISPATCH_CHILD_OUTPUT_PROTOCOL'))
