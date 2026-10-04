"""Private root bootstrap for the fixed, trusted setup-dotnet installation.

This does not accept subject material or grant admission. The trusted workflow's
runner identity and both dotnet resolutions must agree before root seals the
existing installation. Portable helpers exercise filesystem procedures only.
"""
import hashlib
import json
import os
from pathlib import Path
import pwd
import shutil
import stat
import time

SDK_ROOT = Path('/usr/share/dotnet')
MAX_NODES = 100_000
MAX_FILE_BYTES = 256 * 1024 * 1024
MAX_TOTAL_BYTES = 16 * 1024 * 1024 * 1024
MAX_DEPTH = 32


class TrustedSdkFailure(Exception):
    """Fixed bootstrap rejection; no paths, exception text or file bytes."""


def require(value):
    if not value:
        raise TrustedSdkFailure('trusted-sdk-bootstrap-rejected')


def remaining(deadline):
    require(time.monotonic() < deadline)


def identity(info):
    return (info.st_dev, info.st_ino, info.st_uid, info.st_gid,
            stat.S_IMODE(info.st_mode), info.st_nlink, info.st_size,
            info.st_mtime_ns, info.st_ctime_ns)


def metadata(info):
    return {'device': info.st_dev, 'inode': info.st_ino, 'uid': info.st_uid,
            'gid': info.st_gid, 'mode': format(stat.S_IMODE(info.st_mode), '04o'),
            'links': info.st_nlink, 'length': info.st_size}


def named_matches(parent, name, fd):
    require(identity(os.stat(name, dir_fd=parent, follow_symlinks=False)) == identity(os.fstat(fd)))


def digest_fd(fd, deadline):
    before = os.fstat(fd)
    require(stat.S_ISREG(before.st_mode) and 0 <= before.st_size <= MAX_FILE_BYTES)
    os.lseek(fd, 0, os.SEEK_SET)
    digest, total, prefix = hashlib.sha256(), 0, b''
    while True:
        remaining(deadline)
        block = os.read(fd, min(128 * 1024, before.st_size + 1 - total))
        if not block:
            break
        if len(prefix) < 20:
            prefix += block[:20 - len(prefix)]
        total += len(block)
        require(total <= before.st_size)
        digest.update(block)
    require(total == before.st_size and identity(os.fstat(fd)) == identity(before))
    return digest.hexdigest(), prefix


def bounded_names(directory, maximum, deadline):
    """Stop enumeration at the retained FD's deadline or first excess entry."""
    require(type(maximum) is int and 0 <= maximum <= MAX_NODES)
    names = []
    with os.scandir(directory) as entries:
        for entry in entries:
            remaining(deadline)
            names.append(entry.name)
            require(len(names) <= maximum)
    remaining(deadline)
    return sorted(names)


def inventory(root_fd, setup_uid, setup_gid, deadline):
    """Read a bounded SDK tree through retained directory FDs; never mutate it."""
    rows, total = {}, 0
    allowed_uids, allowed_gids = {0, setup_uid}, {0, setup_gid}

    def walk(directory, relative, depth):
        nonlocal total
        remaining(deadline)
        require(depth <= MAX_DEPTH and len(rows) < MAX_NODES)
        info = os.fstat(directory)
        mode = stat.S_IMODE(info.st_mode)
        require(stat.S_ISDIR(info.st_mode) and info.st_uid in allowed_uids
                and info.st_gid in allowed_gids and not mode & 0o7000
                and mode & 0o555 == 0o555)
        rows[relative] = {'metadata': metadata(info), 'directory': True}
        names = bounded_names(directory, MAX_NODES - len(rows), deadline)
        for name in names:
            remaining(deadline)
            require(name not in ('.', '..') and '/' not in name and '\x00' not in name)
            observed = os.stat(name, dir_fd=directory, follow_symlinks=False)
            mode = stat.S_IMODE(observed.st_mode)
            require(observed.st_uid in allowed_uids and observed.st_gid in allowed_gids
                    and not mode & 0o7000)
            child_relative = name if not relative else relative + '/' + name
            if stat.S_ISDIR(observed.st_mode):
                child = os.open(name, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC,
                                dir_fd=directory)
                try:
                    named_matches(directory, name, child)
                    walk(child, child_relative, depth + 1)
                    named_matches(directory, name, child)
                finally:
                    os.close(child)
            else:
                require(stat.S_ISREG(observed.st_mode) and observed.st_nlink == 1
                        and mode & 0o444 == 0o444 and 0 <= observed.st_size <= MAX_FILE_BYTES)
                child = os.open(name, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK | os.O_CLOEXEC,
                                dir_fd=directory)
                try:
                    named_matches(directory, name, child)
                    digest, prefix = digest_fd(child, deadline)
                    named_matches(directory, name, child)
                    total += observed.st_size
                    require(total <= MAX_TOTAL_BYTES and len(rows) < MAX_NODES)
                    rows[child_relative] = {'metadata': metadata(observed), 'directory': False,
                                            'sha256': digest}
                    if child_relative == 'dotnet':
                        require(mode & 0o111 and prefix[:7] == b'\x7fELF\x02\x01\x01'
                                and int.from_bytes(prefix[16:18], 'little') in (2, 3)
                                and int.from_bytes(prefix[18:20], 'little') == 62)
                finally:
                    os.close(child)
    walk(root_fd, '', 0)
    require('dotnet' in rows and not rows['dotnet']['directory']
            and all(name in rows and rows[name]['directory'] for name in ('host', 'host/fxr', 'shared', 'sdk')))
    return rows, total


def seal_inventory(root_fd, before, deadline):
    """Seal only a completely pre-audited tree, then require an identical file set."""
    seen = set()
    children = {name: [] for name, row in before.items() if row['directory']}
    for name in before:
        if name:
            parent_name = name.rsplit('/', 1)[0] if '/' in name else ''
            require(parent_name in children)
            children[parent_name].append(name.rsplit('/', 1)[-1])
    for names in children.values():
        names.sort()

    def seal(directory, relative):
        remaining(deadline)
        require(relative in before and before[relative]['directory'])
        row = before[relative]
        require(metadata(os.fstat(directory)) == row['metadata'])
        os.fchown(directory, 0, 0)
        os.fchmod(directory, int(row['metadata']['mode'], 8) & ~0o022)
        seen.add(relative)
        expected = children[relative]
        require(bounded_names(directory, len(expected), deadline) == expected)
        for name in expected:
            remaining(deadline)
            child_relative = name if not relative else relative + '/' + name
            row = before[child_relative]
            flags = os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC
            flags |= os.O_DIRECTORY if row['directory'] else os.O_NONBLOCK
            child = os.open(name, flags, dir_fd=directory)
            try:
                named_matches(directory, name, child)
                require(metadata(os.fstat(child)) == row['metadata'])
                if row['directory']:
                    seal(child, child_relative)
                else:
                    require(digest_fd(child, deadline)[0] == row['sha256'])
                    os.fchown(child, 0, 0)
                    os.fchmod(child, int(row['metadata']['mode'], 8) & ~0o022)
                    require(digest_fd(child, deadline)[0] == row['sha256'])
                    seen.add(child_relative)
                info = os.fstat(child)
                require(info.st_uid == 0 and info.st_gid == 0
                        and stat.S_IMODE(info.st_mode) == int(row['metadata']['mode'], 8) & ~0o022)
                named_matches(directory, name, child)
            finally:
                os.close(child)
    seal(root_fd, '')
    require(seen == set(before))


def seal_trusted_sdk(deadline):
    """Root-only audit/adoption of the independently named trusted runner SDK.

    The sudo invoker must be the NSS ``runner`` account. Both the preparation
    PATH and the launcher's fixed PATH must resolve to the fixed canonical host.
    Root-owned system ancestors remain unchanged. No subject has been evaluated.
    """
    require(os.geteuid() == 0)
    runner = pwd.getpwnam('runner')
    require(runner.pw_uid > 0 and runner.pw_gid > 0
            and os.environ.get('SUDO_UID') == str(runner.pw_uid)
            and os.environ.get('SUDO_GID') == str(runner.pw_gid))
    host = SDK_ROOT / 'dotnet'
    require(Path(shutil.which('dotnet') or '').resolve(strict=True) == host
            and Path(shutil.which('dotnet', path='/usr/bin:/bin') or '').resolve(strict=True) == host)
    parent = os.open('/', os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
    try:
        for name in ('usr', 'share'):
            remaining(deadline)
            child = os.open(name, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=parent)
            try:
                named_matches(parent, name, child)
                info = os.fstat(child)
                require(stat.S_ISDIR(info.st_mode) and info.st_uid == 0 and not info.st_mode & 0o022)
            except BaseException:
                os.close(child)
                raise
            os.close(parent)
            parent = child
        root = os.open('dotnet', os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=parent)
        try:
            named_matches(parent, 'dotnet', root)
            before, total = inventory(root, runner.pw_uid, runner.pw_gid, deadline)
            initial_host = before['dotnet']
            seal_inventory(root, before, deadline)
            after, final_total = inventory(root, 0, 0, deadline)
            require(set(after) == set(before) and total == final_total)
            for name, row in before.items():
                remaining(deadline)
                expected = dict(row['metadata'], uid=0, gid=0, mode=format(int(row['metadata']['mode'], 8) & ~0o022, '04o'))
                require(after[name]['metadata'] == expected and after[name]['directory'] == row['directory']
                        and after[name].get('sha256') == row.get('sha256'))
            named_matches(parent, 'dotnet', root)
            remaining(deadline)
            encode = lambda value: json.dumps(value, sort_keys=True, separators=(',', ':')).encode()
            record = {'schema': 'issue779-trusted-sdk-bootstrap-v1', 'root': str(SDK_ROOT),
                          'trusted_setup_uid': runner.pw_uid, 'trusted_setup_gid': runner.pw_gid,
                          'node_count': len(before), 'total_file_bytes': total,
                          'before_sha256': hashlib.sha256(encode(before)).hexdigest(),
                          'after_sha256': hashlib.sha256(encode(after)).hexdigest(),
                          'host_before': initial_host, 'host_after': after['dotnet'],
                          'sealed': True, 'contents_unchanged': True}
            remaining(deadline)
            return host, record
        finally:
            os.close(root)
    finally:
        os.close(parent)
