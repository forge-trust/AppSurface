"""Bound C1 diagnostic artifacts before upload; this grants no native outcome.

Only receipts, logs and the procedure's bounded private archives belong here.
The sealed source, SDK and publication trees remain outside the artifact root.
All nodes are checked before any permission change. The fixed 96 MiB total,
32 MiB file and 512-file limits are retention limits, not product quotas. The
fixed native-private.tar and N15 native.tar leaves retain their original
33,619,968-byte USTAR bound. Only the dedicated segmented measurement workflow
selects --measurement with 192 MiB/1,024 files; ordinary invocation is unchanged.
"""

from pathlib import Path
import os
import stat
import sys
import time


FILE_LIMIT = 32 * 1024 * 1024
TOTAL_LIMIT = 96 * 1024 * 1024
FILE_COUNT_LIMIT = 512
NODE_LIMIT = 1024
ARCHIVE_LIMIT = 33619968
ARCHIVE_NAMES = frozenset(("native-private.tar", "native.tar"))
MEASUREMENT_TOTAL_LIMIT = 192 * 1024 * 1024
MEASUREMENT_FILE_COUNT_LIMIT = 1024
MEASUREMENT_NODE_LIMIT = 2048


def check(root, *, deadline=None):
    """Validate a settled private tree, then apply private upload permissions.

    The caller must already have joined every writer. Links and special nodes
    are rejected without chmod; opened nodes are checked again before fchmod.
    No input is deleted or truncated to satisfy these bounds.
    """
    return _check(root, deadline=deadline, total_limit=TOTAL_LIMIT,
                  file_count_limit=FILE_COUNT_LIMIT, node_limit=NODE_LIMIT)


def check_measurement(root, *, deadline=None):
    """Retain one measurement segment within 192 MiB and 1,024 files.

    Only the dedicated measurement workflow selects this fixed contract. The
    ordinary native lane retains its original 96 MiB/512-file contract, and all
    per-file, archive, link, node-stability and no-truncation checks are shared.
    These are private artifact limits, not production or coverage limits.
    """
    return _check(root, deadline=deadline, total_limit=MEASUREMENT_TOTAL_LIMIT,
                  file_count_limit=MEASUREMENT_FILE_COUNT_LIMIT, node_limit=MEASUREMENT_NODE_LIMIT)


def _check(root, *, deadline, total_limit, file_count_limit, node_limit):
    """Apply identical stable-node checks for one source-selected retention contract."""
    root = Path(root)
    deadline = time.monotonic() + 30 if deadline is None else deadline
    if not stat.S_ISDIR(root.lstat().st_mode):
        raise ValueError("retention-file")
    pending = [root]
    rows = []
    total = files = 0
    while pending:
        if time.monotonic() >= deadline:
            raise ValueError("retention-deadline")
        path = pending.pop()
        metadata = path.lstat()
        directory = stat.S_ISDIR(metadata.st_mode)
        if not directory:
            if not stat.S_ISREG(metadata.st_mode) or metadata.st_nlink != 1:
                raise ValueError("retention-file")
            maximum = ARCHIVE_LIMIT if path.name in ARCHIVE_NAMES else FILE_LIMIT
            if not 0 <= metadata.st_size <= maximum:
                raise ValueError("retention-file-bytes")
            total += metadata.st_size
            files += 1
        rows.append((path, metadata, directory))
        if len(rows) > node_limit or files > file_count_limit or total > total_limit:
            raise ValueError("retention-tree-bound")
        if directory:
            with os.scandir(path) as entries:
                for entry in entries:
                    pending.append(Path(entry.path))
                    if len(rows) + len(pending) > node_limit:
                        raise ValueError("retention-tree-bound")
    for path, before, directory in rows:
        if time.monotonic() >= deadline:
            raise ValueError("retention-deadline")
        flags = os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC | os.O_NONBLOCK
        if directory:
            flags |= os.O_DIRECTORY
        fd = os.open(path, flags)
        try:
            opened = os.fstat(fd)
            if (opened.st_dev, opened.st_ino, opened.st_mode, opened.st_nlink,
                opened.st_size, opened.st_mtime_ns) != (
                before.st_dev, before.st_ino, before.st_mode, before.st_nlink,
                before.st_size, before.st_mtime_ns):
                raise ValueError("retention-node-changed")
            os.fchmod(fd, 0o700 if directory else 0o600)
        finally:
            os.close(fd)
    return {"files": files, "bytes": total}


if __name__ == "__main__":
    if len(sys.argv) == 3 and sys.argv[1] == "--measurement":
        check_measurement(Path(sys.argv[2]))
    elif len(sys.argv) == 2:
        check(Path(sys.argv[1]))
    else:
        raise SystemExit(2)
