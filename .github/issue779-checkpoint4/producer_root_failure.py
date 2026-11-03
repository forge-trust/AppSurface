"""Failure-only data capture; no runtime authority or native outcome is established.

collect(evidence, output) reads only the seven producer_roots.FILES names and the
joined native-driver.log through a retained, no-follow directory FD. Reports are
never read. A fresh private output contains metadata.json and at most 64 KiB of
log slices. Unavailable inputs produce closed reasons, never guessed absence or
success. The caller must preserve the original failed step regardless of this
    diagnostic's outcome. This is not an adversarial ancestor-path proof.
"""
import argparse
import json
import os
from pathlib import Path
import stat
import time

from producer_roots import FILES

REPORT_NAMES = tuple(FILES)
LOG_NAME = 'native-driver.log'
SLICE_BYTES = 32768
LOG_LIMIT = 65536
METADATA_LIMIT = 8192
TOTAL_LIMIT = 73728
SECONDS = 5
PUBLICATION_RESERVE = 0.5
FLAGS = os.O_RDONLY | os.O_CLOEXEC | os.O_NOFOLLOW | os.O_NONBLOCK


class _Unavailable(Exception):
    """Internal closed reason; never carries a pathname or external exception."""


def _check(end):
    if time.monotonic() >= end:
        raise _Unavailable('deadline')


def _signature(node):
    return (node.st_dev, node.st_ino, node.st_mode, node.st_uid, node.st_gid,
            node.st_nlink, node.st_size, node.st_mtime_ns, node.st_ctime_ns)


def _kind(mode):
    for check, name in ((stat.S_ISREG, 'regular'), (stat.S_ISDIR, 'directory'),
                        (stat.S_ISLNK, 'symlink'), (stat.S_ISFIFO, 'fifo'),
                        (stat.S_ISSOCK, 'socket'), (stat.S_ISCHR, 'character'),
                        (stat.S_ISBLK, 'block')):
        if check(mode):
            return name
    return 'other'


def _metadata(name, node=None):
    row = {'name': name, 'available': False, 'stable': False}
    if node is not None:
        row.update(type=_kind(node.st_mode), links=node.st_nlink,
                   mode=format(stat.S_IMODE(node.st_mode), '04o'),
                   bytes=node.st_size, uid=node.st_uid, gid=node.st_gid)
    return row


def _error(error):
    if isinstance(error, _Unavailable):
        return error.args[0]
    if isinstance(error, FileNotFoundError):
        return 'missing'
    if isinstance(error, PermissionError):
        return 'permission'
    return 'io-unavailable'


def _observe(root, name, end, log=False):
    row, handle = _metadata(name), None
    body = b''
    try:
        _check(end)
        named = os.stat(name, dir_fd=root, follow_symlinks=False)
        row = _metadata(name, named)
        if not stat.S_ISREG(named.st_mode):
            raise _Unavailable('not-regular')
        if named.st_nlink != 1:
            raise _Unavailable('not-single-link')
        _check(end)
        handle = os.open(name, FLAGS, dir_fd=root)
        opened = os.fstat(handle)
        if _signature(opened) != _signature(named):
            raise _Unavailable('changed')
        segments = []
        if log:
            size = opened.st_size
            if size <= LOG_LIMIT:
                segments = [{'offset': 0, 'bytes': size}]
            else:
                segments = [{'offset': 0, 'bytes': SLICE_BYTES},
                            {'offset': size - SLICE_BYTES, 'bytes': SLICE_BYTES}]
            parts = []
            for segment in segments:
                _check(end)
                part = os.pread(handle, segment['bytes'], segment['offset'])
                if len(part) != segment['bytes']:
                    raise _Unavailable('changed')
                parts.append(part)
            body = b''.join(parts)
        _check(end)
        after = os.fstat(handle)
        renamed = os.stat(name, dir_fd=root, follow_symlinks=False)
        if _signature(named) != _signature(after) or _signature(after) != _signature(renamed):
            raise _Unavailable('changed')
        _check(end)
        row.update(available=True, stable=True)
        if log:
            row.update(complete=opened.st_size <= LOG_LIMIT,
                       capture='full' if opened.st_size <= LOG_LIMIT else 'first-and-last-slices',
                       retained_bytes=len(body), segments=segments)
        return row, body
    except (OSError, _Unavailable) as error:
        row['unavailable'] = _error(error)
        if log:
            row.update(complete=False, capture='unavailable', retained_bytes=0, segments=[])
        return row, b''
    finally:
        if handle is not None:
            os.close(handle)


def _publish(output, metadata, log, end):
    """Publish only into a fresh directory; existing output is never changed."""
    body = json.dumps(metadata, sort_keys=True, separators=(',', ':'), allow_nan=False).encode() + b'\n'
    if len(body) > METADATA_LIMIT or len(log) > LOG_LIMIT or len(body) + len(log) > TOTAL_LIMIT:
        raise ValueError('producer-root-failure-output-bound')
    _check(end)
    os.mkdir(output, 0o700)
    root = None
    created = []
    try:
        root = os.open(output, FLAGS | os.O_DIRECTORY)
        node = os.fstat(root)
        if node.st_uid != os.geteuid() or stat.S_IMODE(node.st_mode) != 0o700:
            raise _Unavailable('output-permission')
        for name, data in (('native-driver-slices.log', log), ('metadata.json', body)):
            _check(end)
            handle = os.open(name, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC,
                             0o600, dir_fd=root)
            created.append((name, os.fstat(handle)))
            try:
                os.fchmod(handle, 0o600)
                position = 0
                while position < len(data):
                    _check(end)
                    written = os.write(handle, data[position:])
                    if written <= 0:
                        raise _Unavailable('output-write')
                    position += written
            finally:
                os.close(handle)
        _check(end)
        if _signature(os.fstat(root)) != _signature(os.stat(output, follow_symlinks=False)):
            raise _Unavailable('output-changed')
    except (OSError, _Unavailable):
        # Remove only leaves we created that still identify the same real inode.
        if root is not None:
            for name, node in reversed(created):
                try:
                    current = os.stat(name, dir_fd=root, follow_symlinks=False)
                    if (current.st_dev, current.st_ino) == (node.st_dev, node.st_ino):
                        os.unlink(name, dir_fd=root)
                except OSError:
                    pass
        raise ValueError('producer-root-failure-output-unavailable') from None
    finally:
        if root is not None:
            os.close(root)


def collect(evidence, output):
    """Capture fixed metadata and joined-log slices under one five-second budget.

    The caller invokes this only after the original measurement has joined; file
    stability alone does not prove process exit. "complete" describes source-log
    byte coverage only. Input modes/UIDs/GIDs are data, not capabilities.
    Missing, unsafe, changed and deadline-limited input records remain unavailable.
    No log text is parsed or echoed. Failure to create fresh output raises a closed
    error; callers must retain their original measurement failure in every case.
    """
    if not isinstance(evidence, (str, Path)) or not isinstance(output, (str, Path)):
        raise ValueError('producer-root-failure-path-shape')
    end = time.monotonic() + SECONDS
    read_end = end - PUBLICATION_RESERVE
    root = None
    names = (*REPORT_NAMES, LOG_NAME)
    try:
        _check(read_end)
        root = os.open(evidence, FLAGS | os.O_DIRECTORY)
        rows, log = [], b''
        for name in names:
            row, part = _observe(root, name, read_end, log=name == LOG_NAME)
            rows.append(row)
            if name == LOG_NAME:
                log = part
    except (OSError, _Unavailable) as error:
        rows = [dict(_metadata(name), unavailable='root-' + _error(error)) for name in names]
        rows[-1].update(complete=False, capture='unavailable', retained_bytes=0, segments=[])
        log = b''
    finally:
        if root is not None:
            os.close(root)
    value = {'schema': 'issue779-producer-root-failure-v1', 'diagnostic_only': True,
             'namespace_authority': False, 'native_acceptance': False,
             'original_outcome_unchanged': True, 'joined_source_is_caller_precondition': True,
             'records': rows}
    try:
        _publish(Path(output), value, log, end)
    except (OSError, _Unavailable):
        raise ValueError('producer-root-failure-output-unavailable') from None
    return value


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--evidence', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    try:
        collect(args.evidence, args.output)
        return 0
    except (OSError, ValueError, _Unavailable):
        return 1


if __name__ == '__main__':
    raise SystemExit(main())
