"""Finite checkpoint artifact bounds; no product budget or runtime authority.

Default retention remains 8 MiB per file, 32 MiB total and 128 files. The explicit
--producer-roots mode permits only the two root-level standard measurement JSON
reports up to 32 MiB each, still under the same 32 MiB total. All other files and
all regular-file, single-link and no-symlink checks retain their original bounds.
"""
from pathlib import Path
import os
import stat
import sys

MIB = 1024 * 1024
ROOT_REPORTS = frozenset(('native-producer-roots-c2.json', 'native-producer-roots.json'))


def check(root, producer_roots=False):
    """Check actual artifact nodes and apply private modes; this grants no native outcome."""
    root = Path(root)
    total = 0
    count = 0
    assert root.is_dir() and not root.is_symlink()
    for path in root.rglob('*'):
        metadata = path.lstat()
        assert not stat.S_ISLNK(metadata.st_mode)
        if path.is_dir():
            path.chmod(0o700)
            continue
        maximum = (32 if producer_roots and path.parent == root and path.name in ROOT_REPORTS else 8) * MIB
        assert stat.S_ISREG(metadata.st_mode) and metadata.st_nlink == 1 and metadata.st_size <= maximum
        total += metadata.st_size
        count += 1
        path.chmod(0o600)
    assert count <= 128 and total <= 32 * MIB
    root.chmod(0o700)


if __name__ == '__main__':
    assert len(sys.argv) in (2, 3)
    assert len(sys.argv) == 2 or sys.argv[2] == '--producer-roots'
    check(Path(sys.argv[1]), producer_roots=len(sys.argv) == 3)
