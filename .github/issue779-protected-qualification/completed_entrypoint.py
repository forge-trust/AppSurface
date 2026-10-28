"""Protected metadata entrypoint; replay execution lives in a separate credential-free process."""
from __future__ import annotations

from contextlib import contextmanager
from dataclasses import fields
import hashlib
import json
import math
import os
from pathlib import Path
import stat
import subprocess
import sys
import time

sys.path.insert(0, str(Path(__file__).resolve().parent))

import completed_c1
import completed_controller
import completed_run
import completed_source
import completed_wire

PREPARE_SECONDS = completed_controller.MAX_PREPARE_SECONDS
CLOSE_SECONDS = completed_controller.MAX_CLOSE_SECONDS
MAX_REPORT_BYTES = 64 * 1024


def _need(condition, code):
    if not condition:
        raise ValueError('completed-entrypoint-' + code)


def _remaining(deadline, maximum=None):
    _need(type(deadline) in (int, float) and math.isfinite(deadline), 'deadline')
    remaining = deadline - time.monotonic()
    _need(0 < remaining and (maximum is None or remaining <= maximum), 'deadline')


def guard_completed_entrypoint(event_raw, environment, *, deadline):
    """Compare the protected validator context independently of the completed predecessor.

    Environment strings outside the reviewed protected workflow authenticate nothing.
    This guard does not create a support record or certify any replay process.
    """
    _remaining(deadline)
    _need(type(environment) is dict and environment.get('GITHUB_ACTIONS') == 'true'
          and environment.get('GITHUB_EVENT_NAME') == 'workflow_run'
          and environment.get('GITHUB_REPOSITORY') == completed_run.REPOSITORY
          and environment.get('GITHUB_REF') == 'refs/heads/main'
          and environment.get('GITHUB_REF_PROTECTED') == 'true'
          and environment.get('GITHUB_WORKFLOW_REF') == completed_run.REPOSITORY + '/'
          + completed_source.FUTURE_WORKFLOW + '@refs/heads/main', 'protected-context')
    repository, run_id, attempt, head, workflow_id = completed_run._event_projection(event_raw)
    _need(environment.get('GITHUB_REPOSITORY_ID') == str(repository), 'repository-id')
    current = completed_source._git(completed_source.ROOT, 'rev-parse', 'HEAD', deadline=deadline)
    _need(type(current) is str and completed_run.SHA1.fullmatch(current) is not None
          and environment.get('GITHUB_SHA') == current
          and environment.get('GITHUB_WORKFLOW_SHA') == current, 'validator-head')
    return repository, run_id, attempt, head, workflow_id, current


@contextmanager
def _inputs(specifications, deadline):
    opened = []
    primary = None
    try:
        raws = []
        for value, maximum, private in specifications:
            _remaining(deadline)
            path = Path(value)
            _need(path.is_absolute() and path.resolve(strict=True) == path, 'input-path')
            fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK | os.O_CLOEXEC)
            opened.append((fd, path, maximum, None, None))
            info = os.fstat(fd)
            _need(stat.S_ISREG(info.st_mode) and info.st_nlink == 1
                  and info.st_uid == os.geteuid() and 0 < info.st_size <= maximum
                  and stat.S_IMODE(info.st_mode) in ((0o600,) if private else (0o600, 0o644)),
                  'input-shape')
            body = bytearray()
            while True:
                _remaining(deadline)
                chunk = os.read(fd, min(65536, maximum + 1 - len(body)))
                if not chunk:
                    break
                body.extend(chunk)
                _need(len(body) <= maximum, 'input-size')
            _need(len(body) == info.st_size, 'input-size')
            identity = completed_c1._node_identity(info)
            digest = hashlib.sha256(body).hexdigest()
            after = os.fstat(fd)
            named = path.lstat()
            _need(completed_c1._node_identity(after) == identity
                  == completed_c1._node_identity(named)
                  and info.st_size == after.st_size == named.st_size
                  and info.st_nlink == after.st_nlink == named.st_nlink == 1
                  and info.st_mtime_ns == after.st_mtime_ns == named.st_mtime_ns
                  and info.st_ctime_ns == after.st_ctime_ns == named.st_ctime_ns,
                  'input-custody')
            opened[-1] = (fd, path, maximum, identity, digest)
            raws.append(bytes(body))
        yield tuple(raws)
        for fd, path, maximum, identity, digest in opened:
            _remaining(deadline)
            _need(completed_c1._node_identity(os.fstat(fd)) == identity
                  and os.fstat(fd).st_nlink == 1, 'input-custody')
            body = completed_c1._open_bound_file(
                path, os.fstat(fd).st_size, digest, deadline=deadline, maximum=maximum,
                required_mode=identity[3], expected_identity=identity)
            _need(hashlib.sha256(body).hexdigest() == digest, 'input-changed')
    except BaseException as error:
        primary = error
        raise
    finally:
        first = None
        for fd, _path, _maximum, _identity, _digest in opened:
            try:
                os.close(fd)
            except OSError as error:
                if first is None:
                    first = error
        if first is not None:
            if primary is None:
                raise first
            if hasattr(primary, 'add_note'):
                primary.add_note('completed-entrypoint-input-close-failed')


def _output_parent(path):
    path = Path(path)
    _need(path.is_absolute() and path.parent.resolve(strict=True) == path.parent
          and not os.path.lexists(path), 'output-path')
    completed_controller._directory(path.parent, private=True)
    return path


def _output_disjoint(output, roots):
    _need(all(not completed_c1._paths_overlap(output.parent, Path(root)) for root in roots),
          'output-overlap')


@contextmanager
def _publication(path, deadline):
    """Retain the exclusive output descriptor through every enclosing custody check."""
    parent_fd = os.open(path.parent, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
    parent_identity = None
    fd = None
    identity = None
    digest = None
    size = None
    primary = None

    def check():
        _remaining(deadline)
        _need(completed_c1._node_identity(os.fstat(parent_fd)) == parent_identity
              == completed_c1._node_identity(path.parent.lstat()), 'output-parent-custody')
        _need(fd is not None and os.fstat(fd).st_nlink == 1
              and completed_c1._node_identity(os.fstat(fd)) == identity, 'output-custody')
        completed_c1._open_bound_file(
            path, size, digest, deadline=deadline, maximum=completed_wire.MAX_WIRE_BYTES,
            required_mode=0o600, expected_identity=identity)

    def publish(raw):
        nonlocal fd, identity, digest, size
        _remaining(deadline)
        _need(fd is None and type(raw) is bytes and 0 < len(raw) <= completed_wire.MAX_WIRE_BYTES,
              'output-size')
        fd = os.open(path.name, os.O_RDWR | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC,
                     0o600, dir_fd=parent_fd)
        info = os.fstat(fd)
        identity = completed_c1._node_identity(info)
        _need(stat.S_ISREG(info.st_mode) and info.st_nlink == 1 and info.st_uid == os.geteuid()
              and stat.S_IMODE(info.st_mode) == 0o600, 'output-shape')
        size, digest = len(raw), hashlib.sha256(raw).hexdigest()
        view = memoryview(raw)
        while view:
            _remaining(deadline)
            amount = os.write(fd, view)
            _need(amount > 0, 'output-short-write')
            view = view[amount:]
        os.fsync(fd)
        check()
        os.fsync(parent_fd)

    def remove_owned(error):
        if identity is None:
            return
        try:
            named = os.stat(path.name, dir_fd=parent_fd, follow_symlinks=False)
            if stat.S_ISREG(named.st_mode) and named.st_nlink == 1 \
                    and completed_c1._node_identity(named) == identity:
                os.unlink(path.name, dir_fd=parent_fd)
                os.fsync(parent_fd)
        except FileNotFoundError:
            pass
        except BaseException:
            if hasattr(error, 'add_note'):
                error.add_note('completed-entrypoint-output-cleanup-failed')

    try:
        parent_identity = completed_c1._node_identity(os.fstat(parent_fd))
        _need(parent_identity == completed_c1._node_identity(path.parent.lstat()), 'output-parent-custody')
        yield publish
        check()
    except BaseException as error:
        primary = error
        remove_owned(error)
        raise
    finally:
        close_error = None
        if fd is not None:
            try:
                os.close(fd)
            except OSError as error:
                close_error = error
                if primary is None:
                    remove_owned(error)
        try:
            os.close(parent_fd)
        except OSError as error:
            if close_error is None:
                close_error = error
            if primary is None:
                remove_owned(error)
        if close_error is not None:
            if primary is None:
                raise close_error
            if hasattr(primary, 'add_note'):
                primary.add_note('completed-entrypoint-output-close-failed')


def prepare_entrypoint(event_path, environment, validator_capture_path, source_capture_path,
                       source_root, native_source_root, stage_directory, preparation_path, *, deadline):
    """Write local preparation data after protected source/provider checks, without target execution."""
    _remaining(deadline, PREPARE_SECONDS)
    _need(type(environment) is dict, 'environment')
    token = environment.get('GH_TOKEN')
    output = _output_parent(preparation_path)
    _output_disjoint(output, (completed_source.ROOT, source_root, native_source_root, stage_directory))
    with _publication(output, deadline) as publish, completed_controller._pin_roots((output.parent,)):
        with _inputs(((event_path, completed_run.MAX_EVENT_BYTES, False),
                      (validator_capture_path, completed_source.MAX_CAPTURE_BYTES, True),
                      (source_capture_path, completed_source.MAX_CAPTURE_BYTES, True)), deadline) as raws:
            event, validator_capture, source_capture = raws
            guard_completed_entrypoint(event, environment, deadline=deadline)
            preparation = completed_controller.prepare_completed_run(
                event, environment, validator_capture, source_capture, source_root, native_source_root,
                stage_directory, token, deadline=deadline)
            raw = completed_wire.encode_preparation(preparation)
        # Input readback checks finish before publishing the handoff.
        publish(raw)
    return preparation


def _bind_context(context, preparation):
    stage = preparation.stage
    _need(context[:5] == (stage.repository_id, stage.run_id, stage.run_attempt, stage.head_sha,
                          stage.provider_run.workflow_id)
          and context[5] == preparation.source_binding.validator_head_sha, 'preparation-context')


def close_entrypoint(event_path, environment, validator_capture_path, source_capture_path,
                     preparation_path, replay_path, report_path, *, deadline):
    """Read comparison data and revalidate protected context/source/provider facts; never replay here."""
    _remaining(deadline, CLOSE_SECONDS)
    _need(type(environment) is dict, 'environment')
    token = environment.get('GH_TOKEN')
    output = _output_parent(report_path)
    with _publication(output, deadline) as publish, completed_controller._pin_roots((output.parent,)):
        with _inputs(((event_path, completed_run.MAX_EVENT_BYTES, False),
                      (validator_capture_path, completed_source.MAX_CAPTURE_BYTES, True),
                      (source_capture_path, completed_source.MAX_CAPTURE_BYTES, True),
                      (preparation_path, completed_wire.MAX_WIRE_BYTES, True),
                      (replay_path, completed_wire.MAX_WIRE_BYTES, True)), deadline) as raws:
            event, validator_capture, source_capture, preparation_raw, replay_raw = raws
            context = guard_completed_entrypoint(event, environment, deadline=deadline)
            preparation = completed_wire.decode_preparation(preparation_raw)
            replay = completed_wire.decode_replay(replay_raw)
            _bind_context(context, preparation)
            _output_disjoint(output, (completed_source.ROOT, preparation.source_root,
                                     preparation.native_source_root, preparation.stage_directory,
                                     replay.retained_native_root.parent))
            observation = completed_controller.close_completed_run(
                preparation, replay, validator_capture, source_capture, token, deadline=deadline)
            report = {'schema': 'issue779-completed-validation-observation-v1',
                      'preparation_sha256': hashlib.sha256(preparation_raw).hexdigest(),
                      'replay_wire_sha256': hashlib.sha256(replay_raw).hexdigest(),
                      'observation': {item.name: getattr(observation, item.name) for item in fields(observation)}}
            raw = json.dumps(report, sort_keys=True, separators=(',', ':')).encode()
            _need(len(raw) <= MAX_REPORT_BYTES, 'report-size')
        publish(raw)
    return observation


def main(argv=None):
    """Fixed prepare/close commands only; paths select local files, never executable behavior."""
    argv = sys.argv[1:] if argv is None else argv
    try:
        environment = dict(os.environ)
        event = environment['GITHUB_EVENT_PATH']
        if len(argv) == 7 and argv[0] == 'prepare':
            prepare_entrypoint(event, environment, *argv[1:], deadline=time.monotonic() + PREPARE_SECONDS)
        elif len(argv) == 6 and argv[0] == 'close':
            close_entrypoint(event, environment, *argv[1:], deadline=time.monotonic() + CLOSE_SECONDS)
        else:
            raise ValueError('completed-entrypoint-arguments')
        print('EVIDENCE_COMPLETED_COMPARISON_RETAINED')
        return 0
    except (ValueError, OSError, KeyError, TypeError, UnicodeError, RecursionError, subprocess.SubprocessError):
        print('EVIDENCE_COMPLETED_INPUT_REJECTED', file=sys.stderr)
        return 2


if __name__ == '__main__':
    raise SystemExit(main())
