"""Private root bootstrap for the fixed, trusted setup-dotnet installation.

This does not accept subject material or grant admission. The trusted workflow's
runner identity and both dotnet resolutions must agree before root seals the
existing installation. Portable helpers exercise filesystem procedures only.
"""
import hashlib
from functools import wraps
import json
import math
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
MAX_WORK_NODES = 3 * MAX_NODES
MAX_HASHED_BYTES = 4 * MAX_TOTAL_BYTES
MAX_ELAPSED_MILLISECONDS = 120_000
SDK_WORK_PASSES = frozenset(('unknown', 'initial-inventory', 'seal-before', 'seal-after', 'final-inventory'))

SDK_PHASES = frozenset(('unknown', 'root-identity', 'sudo-identity', 'path-binding',
    'usr-validation', 'share-validation', 'share-sealing', 'share-recheck', 'sdk-root-validation', 'inventory-directory',
    'inventory-node', 'inventory-type', 'inventory-owner', 'inventory-mode',
    'inventory-size', 'inventory-hash', 'inventory-count', 'inventory-shape',
    'deadline', 'sealing-directory', 'sealing-node', 'sealing-hash', 'sealing-adoption',
    'sealing-recheck', 'sealing-count', 'recheck-inventory', 'recheck-binding', 'final-deadline'))
SDK_ROLES = frozenset(('unknown', 'usr', 'share', 'sdk-root', 'host', 'directory', 'file'))


class SdkDiagnostic:
    """Closed diagnostic data only; observations cannot influence sealing guards.

    First failure freezes the current phase and last observed numeric facts.
    No filenames, paths, messages, arguments, bytes or arbitrary type names are
    stored. Missing observations are null, never inferred from a later syscall.
    """
    def __init__(self):
        self.current = {'schema': 'issue779-trusted-sdk-diagnostic-v1',
            'phase': 'unknown', 'role': 'unknown', 'node': None,
            'runner_uid': None, 'runner_gid': None, 'sudo_uid': None, 'sudo_gid': None,
            'ambient_path_match': None, 'fixed_path_match': None, 'error_class': None,
            'phase_before_deadline': None, 'work_pass': 'unknown', 'elapsed_milliseconds': None,
            'nodes_observed': 0, 'hashed_bytes': 0, 'work_overflow': False}
        self.started_at = None
        self.failure = None

    def note(self, phase, role=None, info=None):
        if self.failure is not None:
            return
        try:
            if type(phase) is not str or phase not in SDK_PHASES:
                return
            self.current['phase'] = phase
            if role is not None:
                if type(role) is not str or role not in SDK_ROLES:
                    return
                self.current['role'], self.current['node'] = role, None
            if info is not None:
                values = {'uid': info.st_uid, 'gid': info.st_gid,
                    'mode': stat.S_IMODE(info.st_mode), 'links': info.st_nlink,
                    'device': info.st_dev, 'inode': info.st_ino, 'length': info.st_size}
                if all(type(v) is int and 0 <= v < 2**64 for v in values.values()):
                    values['kind'] = 'regular' if stat.S_ISREG(info.st_mode) else 'directory' \
                        if stat.S_ISDIR(info.st_mode) else 'symlink' if stat.S_ISLNK(info.st_mode) else 'other'
                    self.current['node'] = values
        except BaseException:
            pass

    def work_pass(self, value):
        if self.failure is None and type(value) is str and value in SDK_WORK_PASSES:
            self.current['work_pass'] = value

    def add_work(self, *, nodes=0, hashed_bytes=0):
        """Bound cumulative observations; overflow clamps and aborts, never passes."""
        if self.failure is not None:
            return
        require(type(nodes) is int and nodes >= 0 and type(hashed_bytes) is int and hashed_bytes >= 0)
        overflow = False
        for key, increment, limit in (('nodes_observed', nodes, MAX_WORK_NODES),
                                      ('hashed_bytes', hashed_bytes, MAX_HASHED_BYTES)):
            value = self.current[key] + increment
            require(type(value) is int and value >= 0)
            self.current[key] = min(value, limit)
            overflow = overflow or value > limit
        self.current['work_overflow'] = overflow
        require(not overflow)

    def observe_clock(self, now, expired):
        """Use only the existing deadline sample; elapsed is a capped sampled span."""
        if self.failure is not None:
            return
        require(type(now) in (int, float) and math.isfinite(now) and now >= 0 and type(expired) is bool)
        if self.started_at is None:
            self.started_at = now
        require(now >= self.started_at)
        self.current['elapsed_milliseconds'] = min(MAX_ELAPSED_MILLISECONDS,
                                                  int(min(MAX_ELAPSED_MILLISECONDS / 1000, now - self.started_at) * 1000))
        if expired and self.current['phase_before_deadline'] is None:
            self.current['phase_before_deadline'] = self.current['phase']

    def identities(self, runner, uid, gid):
        if self.failure is not None:
            return
        try:
            for key, value in (('runner_uid', runner.pw_uid), ('runner_gid', runner.pw_gid)):
                if type(value) is int and 0 <= value < 2**32:
                    self.current[key] = value
            for key, value in (('sudo_uid', uid), ('sudo_gid', gid)):
                if type(value) is str and 0 < len(value) <= 10 and value.isascii() and value.isdecimal():
                    number = int(value)
                    if number < 2**32:
                        self.current[key] = number
        except BaseException:
            pass

    def path_match(self, key, value):
        if self.failure is None and key in ('ambient_path_match', 'fixed_path_match') and type(value) is bool:
            self.current[key] = value

    def capture(self, error):
        if self.failure is not None:
            return
        try:
            category = 'sdk' if type(error) is TrustedSdkFailure else 'os' if isinstance(error, OSError) \
                else 'memory' if type(error) is MemoryError else 'unknown'
            self.failure = dict(self.current, error_class=category)
        except BaseException:
            pass

    def snapshot(self):
        value = dict(self.failure if self.failure is not None else self.current)
        if value['node'] is not None:
            value['node'] = dict(value['node'])
        return value


def _note(diagnostic, phase, role=None, info=None):
    try:
        if type(diagnostic) is SdkDiagnostic:
            diagnostic.note(phase, role, info)
    except BaseException:
        pass


def _work(diagnostic, *, nodes=0, hashed_bytes=0):
    if type(diagnostic) is SdkDiagnostic:
        diagnostic.add_work(nodes=nodes, hashed_bytes=hashed_bytes)


def _work_pass(diagnostic, value):
    if type(diagnostic) is SdkDiagnostic:
        diagnostic.work_pass(value)


def _check(diagnostic, phase, role, value, info=None):
    _note(diagnostic, phase, role, info)
    return value


def _match(diagnostic, key, value):
    try:
        if type(diagnostic) is SdkDiagnostic:
            diagnostic.path_match(key, value)
    except BaseException:
        pass
    return value


def _observed(function):
    """Capture before propagation; never suppress or substitute the exception."""
    @wraps(function)
    def call(*args, **kwargs):
        try:
            return function(*args, **kwargs)
        except BaseException as error:
            try:
                diagnostic = kwargs.get('diagnostic')
                if type(diagnostic) is SdkDiagnostic:
                    diagnostic.capture(error)
            except BaseException:
                pass
            raise
    return call


class TrustedSdkFailure(Exception):
    """Fixed bootstrap rejection; no paths, exception text or file bytes."""


def require(value):
    if not value:
        raise TrustedSdkFailure('trusted-sdk-bootstrap-rejected')


@_observed
def remaining(deadline, *, diagnostic=None):
    now = time.monotonic()
    within_deadline = now < deadline
    if type(diagnostic) is SdkDiagnostic:
        diagnostic.observe_clock(now, not within_deadline)
    if not within_deadline:
        _note(diagnostic, 'final-deadline' if type(diagnostic) is SdkDiagnostic
              and diagnostic.current['phase'] == 'final-deadline' else 'deadline')
    require(within_deadline)


def identity(info):
    return (info.st_dev, info.st_ino, info.st_uid, info.st_gid,
            stat.S_IMODE(info.st_mode), info.st_nlink, info.st_size,
            info.st_mtime_ns, info.st_ctime_ns)


def metadata(info):
    return {'device': info.st_dev, 'inode': info.st_ino, 'uid': info.st_uid,
            'gid': info.st_gid, 'mode': format(stat.S_IMODE(info.st_mode), '04o'),
            'links': info.st_nlink, 'length': info.st_size}


@_observed
def named_matches(parent, name, fd, *, diagnostic=None):
    observed = os.stat(name, dir_fd=parent, follow_symlinks=False)
    _note(diagnostic, diagnostic.current['phase'] if type(diagnostic) is SdkDiagnostic else 'unknown', info=observed)
    require(identity(observed) == identity(os.fstat(fd)))


@_observed
def digest_fd(fd, deadline, *, diagnostic=None):
    before = os.fstat(fd)
    _note(diagnostic, 'sealing-hash' if type(diagnostic) is SdkDiagnostic
          and diagnostic.current['phase'].startswith('sealing-') else 'inventory-hash', info=before)
    require(stat.S_ISREG(before.st_mode) and 0 <= before.st_size <= MAX_FILE_BYTES)
    os.lseek(fd, 0, os.SEEK_SET)
    digest, total, prefix = hashlib.sha256(), 0, b''
    while True:
        remaining(deadline, diagnostic=diagnostic)
        block = os.read(fd, min(128 * 1024, before.st_size + 1 - total))
        if not block:
            break
        if len(prefix) < 20:
            prefix += block[:20 - len(prefix)]
        total += len(block)
        require(total <= before.st_size)
        _work(diagnostic, hashed_bytes=len(block))
        digest.update(block)
    require(total == before.st_size and identity(os.fstat(fd)) == identity(before))
    return digest.hexdigest(), prefix


@_observed
def bounded_names(directory, maximum, deadline, *, diagnostic=None):
    """Stop enumeration at the retained FD's deadline or first excess entry."""
    _note(diagnostic, 'sealing-count' if type(diagnostic) is SdkDiagnostic
          and diagnostic.current['phase'] == 'sealing-count' else 'inventory-count')
    require(type(maximum) is int and 0 <= maximum <= MAX_NODES)
    names = []
    with os.scandir(directory) as entries:
        for entry in entries:
            remaining(deadline, diagnostic=diagnostic)
            names.append(entry.name)
            _note(diagnostic, 'sealing-count' if type(diagnostic) is SdkDiagnostic
                  and diagnostic.current['phase'] == 'sealing-count' else 'inventory-count')
            require(len(names) <= maximum)
    remaining(deadline, diagnostic=diagnostic)
    return sorted(names)


@_observed
def inventory(root_fd, setup_uid, setup_gid, deadline, *, diagnostic=None):
    """Read a bounded SDK tree through retained directory FDs; never mutate it."""
    if type(diagnostic) is SdkDiagnostic and diagnostic.current['work_pass'] == 'unknown':
        _work_pass(diagnostic, 'initial-inventory')
    rows, total = {}, 0
    allowed_uids, allowed_gids = {0, setup_uid}, {0, setup_gid}

    def walk(directory, relative, depth):
        nonlocal total
        remaining(deadline, diagnostic=diagnostic)
        _note(diagnostic, 'inventory-count')
        require(depth <= MAX_DEPTH and len(rows) < MAX_NODES)
        info = os.fstat(directory)
        mode = stat.S_IMODE(info.st_mode)
        _note(diagnostic, 'inventory-directory', 'sdk-root' if not relative else 'directory', info)
        _work(diagnostic, nodes=1)
        require(stat.S_ISDIR(info.st_mode) and info.st_uid in allowed_uids
                and info.st_gid in allowed_gids and not mode & 0o7000
                and mode & 0o555 == 0o555)
        rows[relative] = {'metadata': metadata(info), 'directory': True}
        names = bounded_names(directory, MAX_NODES - len(rows), deadline, diagnostic=diagnostic)
        for name in names:
            remaining(deadline, diagnostic=diagnostic)
            require(name not in ('.', '..') and '/' not in name and '\x00' not in name)
            _note(diagnostic, 'inventory-node', 'unknown')
            observed = os.stat(name, dir_fd=directory, follow_symlinks=False)
            role = 'host' if not relative and name == 'dotnet' else 'directory' if stat.S_ISDIR(observed.st_mode) else 'file'
            _note(diagnostic, 'inventory-node', role, observed)
            if not stat.S_ISDIR(observed.st_mode):
                _work(diagnostic, nodes=1)
            mode = stat.S_IMODE(observed.st_mode)
            require(_check(diagnostic, 'inventory-owner', role, observed.st_uid in allowed_uids and observed.st_gid in allowed_gids, observed)
                    and _check(diagnostic, 'inventory-mode', role, not mode & 0o7000, observed))
            child_relative = name if not relative else relative + '/' + name
            if stat.S_ISDIR(observed.st_mode):
                child = os.open(name, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC,
                                dir_fd=directory)
                try:
                    named_matches(directory, name, child, diagnostic=diagnostic)
                    walk(child, child_relative, depth + 1)
                    named_matches(directory, name, child, diagnostic=diagnostic)
                finally:
                    os.close(child)
            else:
                require(_check(diagnostic, 'inventory-type', role, stat.S_ISREG(observed.st_mode), observed)
                        and _check(diagnostic, 'inventory-type', role, observed.st_nlink == 1, observed)
                        and _check(diagnostic, 'inventory-mode', role, mode & 0o444 == 0o444, observed)
                        and _check(diagnostic, 'inventory-size', role, 0 <= observed.st_size <= MAX_FILE_BYTES, observed))
                child = os.open(name, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK | os.O_CLOEXEC,
                                dir_fd=directory)
                try:
                    named_matches(directory, name, child, diagnostic=diagnostic)
                    digest, prefix = digest_fd(child, deadline, diagnostic=diagnostic)
                    named_matches(directory, name, child, diagnostic=diagnostic)
                    total += observed.st_size
                    _note(diagnostic, 'inventory-count', role, observed)
                    require(total <= MAX_TOTAL_BYTES and len(rows) < MAX_NODES)
                    rows[child_relative] = {'metadata': metadata(observed), 'directory': False,
                                            'sha256': digest}
                    if child_relative == 'dotnet':
                        _note(diagnostic, 'inventory-type', 'host', observed)
                        require(mode & 0o111 and prefix[:7] == b'\x7fELF\x02\x01\x01'
                                and int.from_bytes(prefix[16:18], 'little') in (2, 3)
                                and int.from_bytes(prefix[18:20], 'little') == 62)
                finally:
                    os.close(child)
    walk(root_fd, '', 0)
    _note(diagnostic, 'inventory-shape')
    require('dotnet' in rows and not rows['dotnet']['directory']
            and all(name in rows and rows[name]['directory'] for name in ('host', 'host/fxr', 'shared', 'sdk')))
    return rows, total


@_observed
def seal_inventory(root_fd, before, deadline, *, diagnostic=None):
    """Seal only a completely pre-audited tree, then require an identical file set."""
    _work_pass(diagnostic, 'seal-before')
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
        remaining(deadline, diagnostic=diagnostic)
        require(relative in before and before[relative]['directory'])
        row = before[relative]
        _note(diagnostic, 'sealing-directory', 'sdk-root' if not relative else 'directory')
        info = os.fstat(directory)
        _work(diagnostic, nodes=1)
        require(metadata(info) == row['metadata'])
        _note(diagnostic, 'sealing-adoption')
        if info.st_uid != 0 or info.st_gid != 0:
            os.fchown(directory, 0, 0)
        if stat.S_IMODE(info.st_mode) != int(row['metadata']['mode'], 8) & ~0o022:
            os.fchmod(directory, int(row['metadata']['mode'], 8) & ~0o022)
        seen.add(relative)
        expected = children[relative]
        _note(diagnostic, 'sealing-count')
        require(bounded_names(directory, len(expected), deadline, diagnostic=diagnostic) == expected)
        for name in expected:
            remaining(deadline, diagnostic=diagnostic)
            child_relative = name if not relative else relative + '/' + name
            row = before[child_relative]
            flags = os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC
            flags |= os.O_DIRECTORY if row['directory'] else os.O_NONBLOCK
            _note(diagnostic, 'sealing-node', 'directory' if row['directory'] else 'host' if child_relative == 'dotnet' else 'file')
            child = os.open(name, flags, dir_fd=directory)
            try:
                named_matches(directory, name, child, diagnostic=diagnostic)
                info = os.fstat(child)
                require(metadata(info) == row['metadata'])
                if row['directory']:
                    seal(child, child_relative)
                else:
                    _work(diagnostic, nodes=1)
                    _work_pass(diagnostic, 'seal-before')
                    _note(diagnostic, 'sealing-hash')
                    require(digest_fd(child, deadline, diagnostic=diagnostic)[0] == row['sha256'])
                    _note(diagnostic, 'sealing-adoption')
                    if info.st_uid != 0 or info.st_gid != 0:
                        os.fchown(child, 0, 0)
                    if stat.S_IMODE(info.st_mode) != int(row['metadata']['mode'], 8) & ~0o022:
                        os.fchmod(child, int(row['metadata']['mode'], 8) & ~0o022)
                    _work_pass(diagnostic, 'seal-after')
                    _note(diagnostic, 'sealing-hash')
                    require(digest_fd(child, deadline, diagnostic=diagnostic)[0] == row['sha256'])
                    seen.add(child_relative)
                info = os.fstat(child)
                _note(diagnostic, 'sealing-recheck', info=info)
                require(info.st_uid == 0 and info.st_gid == 0
                        and stat.S_IMODE(info.st_mode) == int(row['metadata']['mode'], 8) & ~0o022)
                named_matches(directory, name, child, diagnostic=diagnostic)
            finally:
                os.close(child)
    seal(root_fd, '')
    _note(diagnostic, 'sealing-count')
    require(seen == set(before))


@_observed
def seal_share_ancestor(parent, share_fd, deadline, *, diagnostic=None):
    """Clear only 022 on the fixed named, retained root-owned share directory.

    Internal FD procedure only; the root bootstrap supplies its pinned /usr FD.
    Root ownership, directory type, ordinary permission bits and named identity
    must be established before mutation. Failure propagates without rollback or
    SDK dispatch. Portable tests simulate only the observed root UID/GID.
    """
    _note(diagnostic, 'share-validation', 'share')
    named_matches(parent, 'share', share_fd, diagnostic=diagnostic)
    before = os.fstat(share_fd)
    _note(diagnostic, 'share-validation', 'share', before)
    mode = stat.S_IMODE(before.st_mode)
    require(stat.S_ISDIR(before.st_mode) and before.st_uid == 0 and before.st_gid == 0
            and not mode & 0o7000 and mode & 0o555 == 0o555)
    remaining(deadline, diagnostic=diagnostic)
    named_matches(parent, 'share', share_fd, diagnostic=diagnostic)
    require(identity(os.fstat(share_fd)) == identity(before))
    if mode & 0o022:
        _note(diagnostic, 'share-sealing', 'share', before)
        remaining(deadline, diagnostic=diagnostic)
        os.fchmod(share_fd, mode & ~0o022)
    _note(diagnostic, 'share-recheck', 'share')
    after = os.fstat(share_fd)
    _note(diagnostic, 'share-recheck', 'share', after)
    require(stat.S_ISDIR(after.st_mode) and after.st_uid == 0 and after.st_gid == 0
            and stat.S_IMODE(after.st_mode) == mode & ~0o022
            and (after.st_dev, after.st_ino, after.st_nlink, after.st_size, after.st_mtime_ns)
            == (before.st_dev, before.st_ino, before.st_nlink, before.st_size, before.st_mtime_ns))
    named_matches(parent, 'share', share_fd, diagnostic=diagnostic)
    remaining(deadline, diagnostic=diagnostic)
    return {'before': metadata(before), 'after': metadata(after),
            'write_bits_cleared': bool(mode & 0o022)}


@_observed
def seal_trusted_sdk(deadline, *, diagnostic=None):
    """Root-only audit/adoption of the independently named trusted runner SDK.

    The sudo invoker must be the NSS ``runner`` account. Both the preparation
    PATH and the launcher's fixed PATH must resolve to the fixed canonical host.
    The /usr ancestor remains strict and unchanged; the fixed retained root-owned
    /usr/share directory may lose only group/other write bits. No subject has
    been evaluated.
    """
    _note(diagnostic, 'root-identity')
    require(os.geteuid() == 0)
    _note(diagnostic, 'sudo-identity')
    runner = pwd.getpwnam('runner')
    if type(diagnostic) is SdkDiagnostic:
        diagnostic.identities(runner, os.environ.get('SUDO_UID'), os.environ.get('SUDO_GID'))
    require(runner.pw_uid > 0 and runner.pw_gid > 0
            and os.environ.get('SUDO_UID') == str(runner.pw_uid)
            and os.environ.get('SUDO_GID') == str(runner.pw_gid))
    host = SDK_ROOT / 'dotnet'
    _note(diagnostic, 'path-binding')
    require(_match(diagnostic, 'ambient_path_match', Path(shutil.which('dotnet') or '').resolve(strict=True) == host)
            and _match(diagnostic, 'fixed_path_match', Path(shutil.which('dotnet', path='/usr/bin:/bin') or '').resolve(strict=True) == host))
    parent = os.open('/', os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
    try:
        for name in ('usr', 'share'):
            remaining(deadline, diagnostic=diagnostic)
            _note(diagnostic, 'usr-validation' if name == 'usr' else 'share-validation', name)
            child = os.open(name, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=parent)
            try:
                named_matches(parent, name, child, diagnostic=diagnostic)
                info = os.fstat(child)
                _note(diagnostic, 'usr-validation' if name == 'usr' else 'share-validation', name, info)
                if name == 'usr':
                    require(stat.S_ISDIR(info.st_mode) and info.st_uid == 0 and not info.st_mode & 0o022)
                else:
                    share_ancestor = seal_share_ancestor(parent, child, deadline, diagnostic=diagnostic)
            except BaseException:
                os.close(child)
                raise
            os.close(parent)
            parent = child
        _note(diagnostic, 'sdk-root-validation', 'sdk-root')
        root = os.open('dotnet', os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=parent)
        try:
            named_matches(parent, 'dotnet', root, diagnostic=diagnostic)
            _work_pass(diagnostic, 'initial-inventory')
            before, total = inventory(root, runner.pw_uid, runner.pw_gid, deadline, diagnostic=diagnostic)
            initial_host = before['dotnet']
            seal_inventory(root, before, deadline, diagnostic=diagnostic)
            _note(diagnostic, 'recheck-inventory', 'sdk-root')
            _work_pass(diagnostic, 'final-inventory')
            after, final_total = inventory(root, 0, 0, deadline, diagnostic=diagnostic)
            _note(diagnostic, 'recheck-binding')
            require(set(after) == set(before) and total == final_total)
            for name, row in before.items():
                remaining(deadline, diagnostic=diagnostic)
                _note(diagnostic, 'recheck-binding')
                expected = dict(row['metadata'], uid=0, gid=0, mode=format(int(row['metadata']['mode'], 8) & ~0o022, '04o'))
                require(after[name]['metadata'] == expected and after[name]['directory'] == row['directory']
                        and after[name].get('sha256') == row.get('sha256'))
            named_matches(parent, 'dotnet', root, diagnostic=diagnostic)
            remaining(deadline, diagnostic=diagnostic)
            encode = lambda value: json.dumps(value, sort_keys=True, separators=(',', ':')).encode()
            record = {'schema': 'issue779-trusted-sdk-bootstrap-v1', 'root': str(SDK_ROOT),
                          'trusted_setup_uid': runner.pw_uid, 'trusted_setup_gid': runner.pw_gid,
                          'node_count': len(before), 'total_file_bytes': total,
                          'before_sha256': hashlib.sha256(encode(before)).hexdigest(),
                          'after_sha256': hashlib.sha256(encode(after)).hexdigest(),
                          'host_before': initial_host, 'host_after': after['dotnet'],
                          'share_ancestor': share_ancestor,
                          'sealed': True, 'contents_unchanged': True}
            _note(diagnostic, 'final-deadline')
            remaining(deadline, diagnostic=diagnostic)
            return host, record
        finally:
            os.close(root)
    finally:
        os.close(parent)
