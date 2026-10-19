"""Fixed C4 artifact retention: one 32 MiB report; all other leaves stay at 8 MiB.

This changes no product quota or C3 retention. No directories, unknown leaves,
links or special nodes are accepted. The existing 128-file / 96 MiB total bounds
remain in force. Call only after native collection and source verification.
"""
from pathlib import Path
import os
import stat
import sys

MIB = 1024 * 1024
NAMES = frozenset(('source.json driver-controls.log restore.log publish.log coverlet-install.log '
                   'collector.log account-original.json account-instrumented.json staging.json '
                   'owner.stdout owner.stderr native-accounts-receipt.json native-accounts.json '
                   'measurement-binding.json').split())


def check(root):
    """Inspect actual opened leaves and set private retention modes without reading counters."""
    flags = os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC
    directory = os.open(root, flags | os.O_DIRECTORY)
    signature = lambda value: (value.st_dev, value.st_ino, value.st_mode, value.st_uid,
                               value.st_gid, value.st_nlink, value.st_size,
                               value.st_mtime_ns, value.st_ctime_ns)
    try:
        original = os.fstat(directory)
        if (original.st_dev, original.st_ino) != (root.lstat().st_dev, root.lstat().st_ino):
            raise ValueError('account-retention-root')
        names = os.listdir(directory)
        if len(names) > 128 or not set(names) <= NAMES:
            raise ValueError('account-retention-names')
        total = 0
        snapshots = {}
        for name in names:
            leaf = os.open(name, flags | os.O_NONBLOCK, dir_fd=directory)
            try:
                before = os.fstat(leaf)
                maximum = (32 if name == 'native-accounts.json' else 8) * MIB
                if not stat.S_ISREG(before.st_mode) or before.st_nlink != 1 or before.st_size > maximum:
                    raise ValueError('account-retention-leaf')
                if signature(before) != signature(os.stat(name, dir_fd=directory, follow_symlinks=False)):
                    raise ValueError('account-retention-substitution')
                total += before.st_size
                if total > 96 * MIB:
                    raise ValueError('account-retention-total')
                os.fchmod(leaf, 0o600)
                after = os.fstat(leaf)
                stable = lambda value: (value.st_dev, value.st_ino, value.st_uid, value.st_gid,
                                         value.st_nlink, value.st_size, value.st_mtime_ns)
                if stable(before) != stable(after) or signature(after) != signature(os.stat(name, dir_fd=directory, follow_symlinks=False)):
                    raise ValueError('account-retention-substitution')
                snapshots[name] = signature(after)
            finally:
                os.close(leaf)
        if sorted(os.listdir(directory)) != sorted(names):
            raise ValueError('account-retention-names')
        if any(signature(os.stat(name, dir_fd=directory, follow_symlinks=False)) != expected
               for name, expected in snapshots.items()):
            raise ValueError('account-retention-substitution')
        os.fchmod(directory, 0o700)
    finally:
        os.close(directory)


if __name__ == '__main__':
    check(Path(sys.argv[1]))
