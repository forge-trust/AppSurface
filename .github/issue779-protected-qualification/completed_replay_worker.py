"""Credential-free fixed CLI for one bounded completed-run replay."""
from __future__ import annotations

import errno
import fcntl
import hashlib
import math
import os
from pathlib import Path
import stat
import sys
import time

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / '.github/issue779-protected-qualification'))
import completed_c1
import completed_controller
import completed_native_replay
import completed_orchestration
import completed_run
import completed_source
import completed_validation
import completed_wire

MAX_SECONDS = completed_orchestration.MAX_SECONDS
ENVIRONMENT = {
    'PATH': '/usr/bin:/bin',
    'HOME': '/nonexistent',
    'LANG': 'C.UTF-8',
    'LC_ALL': 'C',
}


def _need(value, code):
    if not value:
        raise ValueError('completed-replay-worker-' + code)


def _remaining(deadline):
    _need(type(deadline) in (int, float) and math.isfinite(deadline)
          and time.monotonic() < deadline, 'deadline')


def _environment():
    _need(dict(os.environ) == ENVIRONMENT, 'environment')


def _proc_status():
    flags = os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC
    fd = os.open('/proc/self/status', flags)
    try:
        before = os.fstat(fd)
        _need(stat.S_ISREG(before.st_mode), 'proc-status-shape')
        raw = bytearray()
        while len(raw) <= 64 * 1024:
            chunk = os.read(fd, min(4096, 64 * 1024 + 1 - len(raw)))
            if not chunk:
                break
            raw.extend(chunk)
        after = os.fstat(fd)
        _need(len(raw) <= 64 * 1024
              and (before.st_dev, before.st_ino) == (after.st_dev, after.st_ino),
              'proc-status-changed')
    finally:
        os.close(fd)
    result = {}
    try:
        for line in raw.decode('ascii').splitlines():
            if ':' in line:
                key, value = line.split(':', 1)
                if key in ('CapEff', 'CapPrm', 'CapInh', 'CapAmb', 'NoNewPrivs'):
                    _need(key not in result, 'proc-status-duplicate')
                    result[key] = value.strip()
    except UnicodeDecodeError:
        raise ValueError('completed-replay-worker-proc-status-encoding') from None
    _need(set(result) == {'CapEff', 'CapPrm', 'CapInh', 'CapAmb', 'NoNewPrivs'},
          'proc-status-fields')
    return result


def _fd_numbers():
    _need(Path('/proc/self/fd').is_dir(), 'proc-fd-unavailable')
    try:
        return tuple(sorted(int(name) for name in os.listdir('/proc/self/fd') if name.isdecimal()))
    except OSError:
        raise ValueError('completed-replay-worker-proc-fd') from None


def _require_process_facts():
    _need(completed_native_replay._is_linux(), 'linux-required')
    _need(hasattr(os, 'getresuid') and hasattr(os, 'getresgid'), 'identity-api')
    _need(len(set(os.getresuid())) == 1 and len(set(os.getresgid())) == 1
          and os.geteuid() != 0 and os.getegid() != 0, 'single-unprivileged-identity')
    groups = os.getgroups()
    _need(type(groups) is list and len(groups) == 1 and groups[0] == os.getegid(),
          'supplementary-groups')
    status = _proc_status()
    _need(all(status[key] and set(status[key]) == {'0'}
              for key in ('CapEff', 'CapPrm', 'CapInh', 'CapAmb'))
          and status['NoNewPrivs'] == '1', 'process-privileges')
    for fd in _fd_numbers():
        if fd <= 2:
            continue
        try:
            flags = fcntl.fcntl(fd, fcntl.F_GETFD)
        except OSError as error:
            if error.errno == errno.EBADF:
                continue
            raise
        _need(flags & fcntl.FD_CLOEXEC, 'inheritable-descriptor')


def _directory(path, code, *, private=False, empty=False):
    root = Path(path)
    _need(root.is_absolute() and root.resolve(strict=True) == root, code + '-canonical')
    info = root.lstat()
    _need(stat.S_ISDIR(info.st_mode) and info.st_uid == os.geteuid()
          and (not private or stat.S_IMODE(info.st_mode) == 0o700), code + '-shape')
    _need(not empty or not os.listdir(root), code + '-not-empty')
    return root


def _path(value, code):
    path = Path(value)
    _need(path.is_absolute() and path.resolve(strict=True) == path, code + '-canonical')
    parent = _directory(path.parent, code + '-parent', private=True)
    _need(path.name not in ('', '.', '..') and str(path) == str(parent / path.name), code + '-name')
    return path, parent


def _destination_path(value, code):
    path = Path(value)
    _need(path.is_absolute() and str(path) == str(value), code + '-canonical')
    parent = _directory(path.parent, code + '-parent', private=True)
    _need(path.name not in ('', '.', '..') and path == parent / path.name, code + '-name')
    parent_fd = os.open(parent, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
    try:
        os.stat(path.name, dir_fd=parent_fd, follow_symlinks=False)
    except FileNotFoundError:
        return path, parent
    except OSError as error:
        if error.errno == errno.ENOENT:
            return path, parent
        raise
    else:
        raise ValueError('completed-replay-worker-result-exists')
    finally:
        os.close(parent_fd)


def _file_stat_identity(info):
    return (info.st_dev, info.st_ino, info.st_mode, info.st_uid, info.st_gid,
            info.st_nlink, info.st_size, info.st_mtime_ns, info.st_ctime_ns)


def _read_input(path, *, maximum, code, deadline):
    path, parent = _path(path, code)
    parent_fd = os.open(parent, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
    fd = None
    try:
        parent_info = os.fstat(parent_fd)
        _need(completed_c1._node_identity(parent_info) == completed_c1._node_identity(parent.lstat()),
              code + '-parent-changed')
        fd = os.open(path.name, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK | os.O_CLOEXEC,
                     dir_fd=parent_fd)
        before = os.fstat(fd)
        _need(stat.S_ISREG(before.st_mode) and before.st_uid == os.geteuid()
              and stat.S_IMODE(before.st_mode) == 0o600 and before.st_nlink == 1
              and 0 < before.st_size <= maximum, code + '-file-shape')
        data = bytearray()
        while len(data) <= maximum:
            _remaining(deadline)
            chunk = os.read(fd, min(64 * 1024, maximum + 1 - len(data)))
            if not chunk:
                break
            data.extend(chunk)
        after = os.fstat(fd)
        named = os.stat(path.name, dir_fd=parent_fd, follow_symlinks=False)
        _need(len(data) == before.st_size and len(data) <= maximum
              and _file_stat_identity(before) == _file_stat_identity(after)
              == _file_stat_identity(named), code + '-file-changed')
        return bytes(data), fd, parent_fd, path, _file_stat_identity(before), \
            parent, completed_c1._node_identity(parent_info)
    except BaseException as error:
        for opened in (fd, parent_fd):
            if opened is not None:
                try:
                    os.close(opened)
                except OSError:
                    if hasattr(error, 'add_note'):
                        error.add_note('completed-replay-worker-input-close-failed')
        raise


def _check_input(snapshot, expected_sha, *, deadline):
    fd, parent_fd, path, identity, parent, parent_identity = snapshot
    _remaining(deadline)
    _need(completed_c1._node_identity(os.fstat(parent_fd)) == parent_identity
          and completed_c1._node_identity(parent.lstat()) == parent_identity, 'input-parent-changed')
    before = os.fstat(fd)
    _need(_file_stat_identity(before) == identity, 'input-fd-changed')
    os.lseek(fd, 0, os.SEEK_SET)
    data = bytearray()
    while len(data) <= before.st_size:
        _remaining(deadline)
        chunk = os.read(fd, min(64 * 1024, before.st_size + 1 - len(data)))
        if not chunk:
            break
        data.extend(chunk)
    after = os.fstat(fd)
    named = os.stat(path.name, dir_fd=parent_fd, follow_symlinks=False)
    _need(len(data) == before.st_size and _file_stat_identity(before) == _file_stat_identity(after)
          == _file_stat_identity(named) and hashlib.sha256(data).hexdigest() == expected_sha,
          'input-changed')


def _write_result(path, raw, *, deadline):
    path, parent = _destination_path(path, 'result')
    parent_fd = os.open(parent, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
    fd = None
    created = None
    primary = None

    def remove_created(error):
        if created is None:
            return
        try:
            current = os.stat(path.name, dir_fd=parent_fd, follow_symlinks=False)
            if stat.S_ISREG(current.st_mode) and current.st_nlink == 1 \
                    and (current.st_dev, current.st_ino) == (created.st_dev, created.st_ino):
                os.unlink(path.name, dir_fd=parent_fd)
                os.fsync(parent_fd)
        except FileNotFoundError:
            pass
        except BaseException:
            if hasattr(error, 'add_note'):
                error.add_note('completed-replay-worker-result-cleanup-failed')

    try:
        parent_identity = completed_c1._node_identity(os.fstat(parent_fd))
        _need(parent_identity == completed_c1._node_identity(parent.lstat()), 'result-parent-changed')
        fd = os.open(path.name, os.O_RDWR | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC,
                     0o600, dir_fd=parent_fd)
        created = os.fstat(fd)
        named = os.stat(path.name, dir_fd=parent_fd, follow_symlinks=False)
        _need(stat.S_ISREG(created.st_mode) and created.st_uid == os.geteuid()
              and stat.S_IMODE(created.st_mode) == 0o600 and created.st_nlink == 1
              and completed_c1._node_identity(created) == completed_c1._node_identity(named),
              'result-file-shape')
        view = memoryview(raw)
        while view:
            _remaining(deadline)
            written = os.write(fd, view)
            _need(written > 0, 'result-short-write')
            view = view[written:]
        os.fsync(fd)
        after = os.fstat(fd)
        named = os.stat(path.name, dir_fd=parent_fd, follow_symlinks=False)
        written = os.pread(fd, len(raw), 0)
        _need(stat.S_ISREG(after.st_mode) and after.st_uid == os.geteuid()
              and stat.S_IMODE(after.st_mode) == 0o600 and after.st_nlink == 1
              and after.st_size == len(raw)
              and completed_c1._node_identity(after) == completed_c1._node_identity(named)
              and written == raw,
              'result-file-changed')
        os.fsync(parent_fd)
        return _file_stat_identity(after)
    except BaseException as error:
        primary = error
        remove_created(error)
        raise
    finally:
        close_error = None
        if fd is not None:
            try:
                os.close(fd)
            except OSError as error:
                close_error = error
                if primary is None:
                    remove_created(error)
        try:
            os.close(parent_fd)
        except OSError as error:
            if close_error is None:
                close_error = error
            if primary is None:
                # A failed close can leave the original directory descriptor usable.
                remove_created(error)
        if close_error is not None:
            if primary is None:
                raise close_error
            if hasattr(primary, 'add_note'):
                primary.add_note('completed-replay-worker-result-close-failed')


def _remove_result_if_owned(path, identity):
    """Remove only the result inode created by this invocation."""
    path = Path(path)
    parent_fd = os.open(path.parent, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
    try:
        try:
            named = os.stat(path.name, dir_fd=parent_fd, follow_symlinks=False)
        except FileNotFoundError:
            return
        if stat.S_ISREG(named.st_mode) and named.st_nlink == 1 \
                and _file_stat_identity(named) == identity:
            os.unlink(path.name, dir_fd=parent_fd)
            os.fsync(parent_fd)
    finally:
        os.close(parent_fd)


def _check_result(path, identity, expected_sha, *, deadline):
    path = Path(path)
    _remaining(deadline)
    parent = _directory(path.parent, 'result-parent', private=True)
    parent_fd = os.open(parent, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
    fd = None
    try:
        parent_identity = completed_c1._node_identity(os.fstat(parent_fd))
        _need(parent_identity == completed_c1._node_identity(parent.lstat()), 'result-parent-changed')
        fd = os.open(path.name, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK | os.O_CLOEXEC,
                     dir_fd=parent_fd)
        before = os.fstat(fd)
        _need(stat.S_ISREG(before.st_mode) and before.st_uid == os.geteuid()
              and stat.S_IMODE(before.st_mode) == 0o600 and before.st_nlink == 1
              and _file_stat_identity(before) == identity,
              'result-file-changed')
        raw = bytearray()
        while len(raw) <= completed_wire.MAX_WIRE_BYTES:
            _remaining(deadline)
            chunk = os.read(fd, min(64 * 1024, completed_wire.MAX_WIRE_BYTES + 1 - len(raw)))
            if not chunk:
                break
            raw.extend(chunk)
        after = os.fstat(fd)
        named = os.stat(path.name, dir_fd=parent_fd, follow_symlinks=False)
        _need(len(raw) == before.st_size and len(raw) <= completed_wire.MAX_WIRE_BYTES
              and _file_stat_identity(before) == _file_stat_identity(after)
              == _file_stat_identity(named) and hashlib.sha256(raw).hexdigest() == expected_sha,
              'result-file-changed')
    finally:
        if fd is not None:
            os.close(fd)
        os.close(parent_fd)


def _roots(preparation, handoff_parent, capture_parent, private_parent, result_parent):
    roots = completed_controller._roots(
        preparation.stage_directory, preparation.source_root, preparation.native_source_root)
    _need(preparation.source_root == completed_source.SOURCE_MOUNT, 'source-mount')
    auxiliaries = tuple(dict.fromkeys((handoff_parent, capture_parent, private_parent, result_parent)))
    _need(all(not completed_c1._paths_overlap(left, right)
              for index, left in enumerate(roots) for right in roots[index + 1:]), 'root-overlap')
    _need(all(not completed_c1._paths_overlap(left, right)
              for index, left in enumerate(auxiliaries) for right in auxiliaries[index + 1:]),
          'handoff-root-overlap')
    _need(all(not completed_c1._paths_overlap(aux, root)
              for aux in auxiliaries for root in roots), 'handoff-root-overlap')
    return roots


def run_replay_worker(preparation_path, source_capture_path, private_parent, result_path, *, deadline):
    """Run fixed replay from bounded local files and write one exclusive result handoff."""
    _remaining(deadline)
    _need(0 < deadline - time.monotonic() <= MAX_SECONDS, 'deadline-bound')
    _environment()
    _require_process_facts()

    preparation_file, handoff_parent = _path(preparation_path, 'preparation')
    result_file, result_parent = _destination_path(result_path, 'result')
    _need(preparation_file.parent == result_file.parent and preparation_file != result_file,
          'handoff-siblings')
    capture_file, capture_parent = _path(source_capture_path, 'source-capture')
    private = _directory(private_parent, 'private-parent', private=True, empty=True)
    _need(not result_file.exists(), 'result-exists')

    prep_raw, prep_fd, prep_parent_fd, prep_path, prep_identity, prep_dir, prep_dir_identity = _read_input(
        preparation_file, maximum=completed_wire.MAX_WIRE_BYTES, code='preparation', deadline=deadline)
    prep_snapshot = (prep_fd, prep_parent_fd, prep_path, prep_identity, prep_dir, prep_dir_identity)
    capture_fd = capture_parent_fd = None
    source_capture = None
    primary = None
    result_identity = None
    try:
        (source_capture, capture_fd, capture_parent_fd, capture_path, capture_identity,
         capture_dir, capture_dir_identity) = _read_input(
            capture_file, maximum=completed_c1.MAX_SOURCE_BYTES, code='source-capture', deadline=deadline)
        capture_snapshot = (capture_fd, capture_parent_fd, capture_path, capture_identity,
                            capture_dir, capture_dir_identity)
        preparation = completed_wire.decode_preparation(prep_raw)
        completed_controller._stage_binding(preparation.stage, preparation.source_binding)
        completed_run.require_closed_jobs(preparation.stage)
        _need(hashlib.sha256(source_capture).hexdigest() == preparation.stage.source_capture_sha256
              == preparation.source_binding.source_capture_sha256, 'source-capture-binding')
        completed_source._strict_capture(source_capture, preparation.stage.head_sha)
        _need(preparation.stage_directory.is_absolute() and preparation.source_root.is_absolute()
              and preparation.native_source_root.is_absolute(), 'preparation-paths')
        roots = _roots(preparation, handoff_parent, capture_parent, private, result_parent)
        _need(tuple((path, completed_c1._node_identity(path.lstat())) for path in roots)
              == preparation.root_identities, 'preparation-root-identities')
        with (completed_controller._pin_roots(roots, preparation.root_identities),
              completed_controller._pin_archives(preparation.stage, preparation.stage_directory, deadline)):
            inventory = completed_validation.inspect_completed_run(
                preparation.stage, preparation.stage_directory, source_capture, deadline=deadline)
            _need(inventory == preparation.inventory, 'preparation-inventory')
            replay = completed_orchestration.replay_completed_run(
                preparation.stage, preparation.stage_directory, source_capture,
                preparation.source_root, preparation.native_source_root, private, deadline=deadline)
            _check_input(prep_snapshot, hashlib.sha256(prep_raw).hexdigest(), deadline=deadline)
            _check_input(capture_snapshot, hashlib.sha256(source_capture).hexdigest(), deadline=deadline)
            encoded = completed_wire.encode_replay(replay)
            _need(type(encoded) is bytes and 0 < len(encoded) <= completed_wire.MAX_WIRE_BYTES,
                  'result-wire-size')
            result_identity = _write_result(result_file, encoded, deadline=deadline)
            _check_input(prep_snapshot, hashlib.sha256(prep_raw).hexdigest(), deadline=deadline)
            _check_input(capture_snapshot, hashlib.sha256(source_capture).hexdigest(), deadline=deadline)
        # Pin-context readbacks finish before the final result/input checks.
        _check_input(prep_snapshot, hashlib.sha256(prep_raw).hexdigest(), deadline=deadline)
        _check_input(capture_snapshot, hashlib.sha256(source_capture).hexdigest(), deadline=deadline)
        _check_result(result_file, result_identity, hashlib.sha256(encoded).hexdigest(),
                      deadline=deadline)
        _remaining(deadline)
        return replay
    except BaseException as error:
        primary = error
        if result_identity is not None:
            try:
                _remove_result_if_owned(result_file, result_identity)
            except BaseException:
                if hasattr(primary, 'add_note'):
                    primary.add_note('completed-replay-worker-result-cleanup-failed')
        raise
    finally:
        close_failure = None
        for fd in (prep_fd, prep_parent_fd, capture_fd, capture_parent_fd):
            if fd is None:
                continue
            try:
                os.close(fd)
            except OSError as error:
                if close_failure is None:
                    close_failure = error
        if close_failure is not None:
            if primary is None:
                if result_identity is not None:
                    try:
                        _remove_result_if_owned(result_file, result_identity)
                    except BaseException:
                        if hasattr(close_failure, 'add_note'):
                            close_failure.add_note('completed-replay-worker-result-cleanup-failed')
                raise close_failure
            if hasattr(primary, 'add_note'):
                primary.add_note('completed-replay-worker-input-close-failed')


def main(argv=None):
    """CLI entry: exactly four fixed local paths, no options or callback selection."""
    values = sys.argv[1:] if argv is None else list(argv)
    if len(values) != 4 or not all(type(value) is str and value.startswith('/') for value in values):
        return 2
    try:
        run_replay_worker(*values[:3], values[3], deadline=time.monotonic() + MAX_SECONDS)
    except BaseException:
        return 1
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
