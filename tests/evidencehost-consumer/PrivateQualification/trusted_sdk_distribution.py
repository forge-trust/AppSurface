"""Private installation of one complete publisher-pinned SDK distribution.

Only ``install`` selects an installation: its URL, SHA-512 and root are literals.
``audit_archive`` and ``extract_audited`` are retained-FD data/procedure seams for
portable controls, not enrollment, admission or an alternative installer API.
The later trusted_sdk audit must still examine this entire tree under its own
unchanged 120-second/four-hash-pass contract. This module does not execute .NET.
"""
from dataclasses import dataclass
from functools import wraps
import hashlib
import io
import json
import math
import os
from pathlib import Path
import shutil
import ssl
import stat
import sys
import tarfile
import tempfile
import time
import urllib.request

SDK_VERSION = '10.0.401'
SDK_ROOT = Path('/usr/share/issue779-dotnet-10.0.401')
ARCHIVE_URL = 'https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.401/dotnet-sdk-10.0.401-linux-x64.tar.gz'
ARCHIVE_SHA512 = '51c8b999af9e8dd9998c9edc5944e19a90788862068acd38694e098889054ce8c23d4f0c5cccfa16bf187d044562359e5ee69a9f8ad0bbe913ba90311fbce25b'
MAX_ARCHIVE_BYTES = 512 * 1024 * 1024
MAX_TOTAL_BYTES = 16 * 1024 * 1024 * 1024
MAX_MEMBER_BYTES = 256 * 1024 * 1024
MAX_NODES = 100_000
MAX_DEPTH = 32
INSTALL_SECONDS = 180
MAX_LONGNAME_BYTES = 4096
CHUNK = 128 * 1024


class DistributionFailure(Exception):
    """Closed rejection; never contains paths, archive names or exception text."""


def require(value):
    if not value:
        raise DistributionFailure('trusted-sdk-distribution-rejected')


def _closed(operation):
    @wraps(operation)
    def invoke(*args, **kwargs):
        try:
            return operation(*args, **kwargs)
        except Exception:
            raise DistributionFailure('trusted-sdk-distribution-rejected') from None
    return invoke


def remaining(deadline):
    require(type(deadline) in (int, float) and math.isfinite(deadline))
    value = deadline - time.monotonic()
    require(value > 0)
    return value


def identity(info):
    return (info.st_dev, info.st_ino, info.st_mode, info.st_uid, info.st_gid,
            info.st_nlink, info.st_size, info.st_mtime_ns, info.st_ctime_ns)


def directory_pin(info):
    # Creating our own children legitimately changes parent timestamps/nlink.
    return (info.st_dev, info.st_ino, info.st_mode, info.st_uid, info.st_gid)


def regular(info, owner_uid, *, mode=0o600):
    require(stat.S_ISREG(info.st_mode) and info.st_uid == owner_uid
            and info.st_nlink == 1 and stat.S_IMODE(info.st_mode) == mode
            and 0 < info.st_size <= MAX_ARCHIVE_BYTES)


def named(parent, name, fd):
    require(identity(os.fstat(fd)) == identity(os.stat(name, dir_fd=parent, follow_symlinks=False)))


class _DeadlineFile(io.RawIOBase):
    def __init__(self, fd, deadline):
        self.fd, self.deadline = fd, deadline

    def readable(self):
        return True

    def seekable(self):
        return True

    def read(self, size=-1):
        remaining(self.deadline)
        require(size >= 0)
        return os.read(self.fd, min(size, CHUNK))

    def seek(self, offset, whence=os.SEEK_SET):
        remaining(self.deadline)
        return os.lseek(self.fd, offset, whence)

    def tell(self):
        return os.lseek(self.fd, 0, os.SEEK_CUR)


def archive_hash(fd, deadline):
    before = identity(os.fstat(fd))
    os.lseek(fd, 0, os.SEEK_SET)
    digest = hashlib.sha512()
    total = 0
    while True:
        remaining(deadline)
        data = os.read(fd, CHUNK)
        if not data:
            break
        total += len(data)
        require(total <= MAX_ARCHIVE_BYTES)
        digest.update(data)
    require(total == os.fstat(fd).st_size and identity(os.fstat(fd)) == before)
    return digest.hexdigest()


@dataclass(frozen=True, slots=True)
class Member:
    path: str
    directory: bool
    size: int
    mode: int
    explicit: bool = True


@dataclass(frozen=True, slots=True)
class ArchiveAudit:
    archive_identity: tuple
    sha512: str
    members: tuple[Member, ...]
    explicit_members: tuple[Member, ...]
    total_bytes: int
    gnu_longname_headers: int


def member_metadata(info):
    # TarInfo.offset_data also exposes otherwise hidden GNU/PAX extension headers.
    require(info.type in (tarfile.REGTYPE, tarfile.AREGTYPE, tarfile.DIRTYPE)
            and not info.pax_headers and info.offset_data == info.offset + 512
            and not info.linkname and type(info.size) is int
            and 0 <= info.size <= MAX_MEMBER_BYTES and info.mode & ~0o777 == 0)
    directory = info.type == tarfile.DIRTYPE
    require(not directory or info.size == 0)
    path = info.name
    require(type(path) is str and '\x00' not in path and '\\' not in path
            and ':' not in path and not path.startswith('/')
            and all(ord(c) >= 32 and ord(c) != 127 for c in path))
    if path.startswith('./'):
        path = path[2:]
    if path.endswith('/'):
        path = path[:-1]
    if path in ('', '.'):
        require(directory)
        path = ''
    else:
        parts = path.split('/')
        require(len(parts) <= MAX_DEPTH and all(p not in ('', '.', '..')
                and len(p.encode('utf-8')) <= 255 for p in parts))
    mode = info.mode & ~0o022
    require((mode & 0o555 == 0o555) if directory else (mode & 0o400 != 0))
    return Member(path, directory, info.size, mode)


class _StrictTarInfo(tarfile.TarInfo):
    """Bounded GNU L processing; all other extension/control types are rejected.

    This uses TarInfo's documented subclass processing entry point. It never
    delegates extension processing to tarfile's unbounded GNU/PAX readers.
    """
    @classmethod
    def _header(cls, archive):
        remaining(cls._sdk_deadline)
        header = archive.fileobj.read(tarfile.BLOCKSIZE)
        info = cls.frombuf(header, archive.encoding, archive.errors)
        info.offset = archive.fileobj.tell() - tarfile.BLOCKSIZE
        # Keep original slashes for our own closed path grammar.
        raw_name = header[:100].split(b'\x00', 1)[0]
        if header[257:263] == b'ustar\x00':
            prefix = header[345:500].split(b'\x00', 1)[0]
            if prefix:
                raw_name = prefix + b'/' + raw_name
        info.name = raw_name.decode('utf-8')
        archive._sdk_physical_headers = getattr(archive, '_sdk_physical_headers', 0) + 1
        require(archive._sdk_physical_headers <= 2 * MAX_NODES)
        return info

    @classmethod
    def fromtarfile(cls, archive):
        return cls._header(archive)._proc_member(archive)

    def _regular(self, archive):
        require(self.type in (tarfile.REGTYPE, tarfile.AREGTYPE, tarfile.DIRTYPE)
                and not archive.pax_headers and not self.pax_headers
                and type(self.size) is int and 0 <= self.size <= MAX_MEMBER_BYTES)
        name = self.name
        result = self._proc_builtin(archive)
        result.name = name
        return result

    def _proc_member(self, archive):
        if self.type != tarfile.GNUTYPE_LONGNAME:
            return self._regular(archive)
        require(type(self.size) is int and 1 < self.size <= MAX_LONGNAME_BYTES
                and not self.linkname and not self.pax_headers)
        remaining(type(self)._sdk_deadline)
        rounded = (self.size + tarfile.BLOCKSIZE - 1) // tarfile.BLOCKSIZE * tarfile.BLOCKSIZE
        block = archive.fileobj.read(rounded)
        require(len(block) == rounded and block[self.size - 1] == 0
                and not any(block[self.size:]) and b'\x00' not in block[:self.size - 1])
        resolved = block[:self.size - 1].decode('utf-8')
        # Read exactly one next physical header: no recursion/nesting/orphan L.
        try:
            following = self._header(archive)
        except tarfile.HeaderError:
            # TarFile.next must not reinterpret an orphan control as normal EOF.
            require(False)
        require(following.type in (tarfile.REGTYPE, tarfile.AREGTYPE, tarfile.DIRTYPE))
        following.name = resolved
        archive._sdk_longname_count = getattr(archive, '_sdk_longname_count', 0) + 1
        require(archive._sdk_longname_count <= MAX_NODES)
        return following._regular(archive)


def _tar(fd, deadline):
    class BoundedTarInfo(_StrictTarInfo):
        _sdk_deadline = deadline

    os.lseek(fd, 0, os.SEEK_SET)
    return tarfile.open(fileobj=_DeadlineFile(fd, deadline), mode='r:gz', tarinfo=BoundedTarInfo)


def _tar_end(archive, deadline):
    # Do not hide concatenated archives or nonzero data after tar's EOF marker.
    padding = 0
    while True:
        remaining(deadline)
        data = archive.fileobj.read(CHUNK)
        if not data:
            break
        padding += len(data)
        require(padding <= tarfile.RECORDSIZE and not any(data))


@_closed
def audit_archive(fd, expected_sha512, deadline, *, owner_uid):
    """Data-only whole-archive validation; performs no destination mutations.

    ``owner_uid``/``expected_sha512`` describe a portable owned fixture. ``install``
    supplies root UID and the publisher literal; callers cannot enroll a fixture.
    All explicit members and implied parent directories count toward node bounds.
    Only one bounded GNU L control may precede a regular file or directory.
    Every resolved name passes the same grammar/bounds; all other extensions
    are rejected before their payloads can be consumed by a permissive reader.
    """
    remaining(deadline)
    info = os.fstat(fd)
    regular(info, owner_uid)
    before = identity(info)
    require(type(expected_sha512) is str and len(expected_sha512) == 128
            and all(c in '0123456789abcdef' for c in expected_sha512))
    actual = archive_hash(fd, deadline)
    require(actual == expected_sha512)
    explicit, rows, total = [], {}, 0
    with _tar(fd, deadline) as archive:
        while True:
            remaining(deadline)
            info = archive.next()
            if info is None:
                break
            row = member_metadata(info)
            require(row.path not in rows and len(explicit) < MAX_NODES)
            rows[row.path] = row
            explicit.append(row)
            total += row.size
            require(total <= MAX_TOTAL_BYTES)
        _tar_end(archive, deadline)
        longnames = getattr(archive, '_sdk_longname_count', 0)
    for row in tuple(rows.values()):
        remaining(deadline)
        parts = row.path.split('/') if row.path else []
        for count in range(1, len(parts)):
            path = '/'.join(parts[:count])
            if path in rows:
                require(rows[path].directory)
            else:
                require(len(rows) < MAX_NODES)
                rows[path] = Member(path, True, 0, 0o755, False)
    require(len(rows) <= MAX_NODES and identity(os.fstat(fd)) == before)
    require('dotnet' in rows and not rows['dotnet'].directory
            and all(p in rows and rows[p].directory for p in ('host', 'host/fxr', 'shared', 'sdk')))
    return ArchiveAudit(before, actual, tuple(sorted(rows.values(), key=lambda x: x.path)),
                        tuple(explicit), total, longnames)


def _open_directory(root, parts, deadline, owner_uid):
    fd = os.dup(root)
    try:
        for part in parts:
            remaining(deadline)
            child = os.open(part, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=fd)
            try:
                info = os.fstat(child)
                require(stat.S_ISDIR(info.st_mode) and info.st_uid == owner_uid
                        and not info.st_mode & 0o7022 and info.st_mode & 0o555 == 0o555)
                named(fd, part, child)
            except BaseException:
                os.close(child)
                raise
            os.close(fd)
            fd = child
        return fd
    except BaseException:
        os.close(fd)
        raise


@_closed
def extract_audited(fd, destination_fd, audit, deadline, *, owner_uid):
    """Owned-FD procedure seam; never chooses a root or grants admission.

    Production uses a fresh root-owned 0700 destination. Fixture callers supply
    current-UID empty directories. Failure leaves a partial quarantined tree;
    it is never adopted, retried in place, or represented as a complete SDK.
    """
    require(type(audit) is ArchiveAudit and identity(os.fstat(fd)) == audit.archive_identity)
    directory = os.fstat(destination_fd)
    require(stat.S_ISDIR(directory.st_mode) and directory.st_uid == owner_uid
            and stat.S_IMODE(directory.st_mode) == 0o700)
    with os.scandir(destination_fd) as entries:
        require(next(entries, None) is None)
    tree = []
    for row in sorted((r for r in audit.members if r.directory and r.path), key=lambda r: (r.path.count('/'), r.path)):
        remaining(deadline)
        parts = row.path.split('/')
        parent = _open_directory(destination_fd, parts[:-1], deadline, owner_uid)
        try:
            os.mkdir(parts[-1], 0o700, dir_fd=parent)
            child = os.open(parts[-1], os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=parent)
            try:
                require(os.fstat(child).st_uid == owner_uid)
                os.fchmod(child, row.mode)
                named(parent, parts[-1], child)
                tree.append({'path': row.path, 'directory': True, 'mode': row.mode})
            finally:
                os.close(child)
        finally:
            os.close(parent)
    index = 0
    with _tar(fd, deadline) as archive:
        while True:
            remaining(deadline)
            info = archive.next()
            if info is None:
                break
            require(index < len(audit.explicit_members))
            row = member_metadata(info)
            require(row == audit.explicit_members[index])
            index += 1
            if row.directory:
                continue
            parts = row.path.split('/')
            parent = _open_directory(destination_fd, parts[:-1], deadline, owner_uid)
            target = None
            try:
                target = os.open(parts[-1], os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC,
                                 0o600, dir_fd=parent)
                before = os.fstat(target)
                require(stat.S_ISREG(before.st_mode) and before.st_uid == owner_uid and before.st_nlink == 1)
                digest, received = hashlib.sha256(), 0
                source = archive.extractfile(info)
                require(source is not None)
                with source:
                    while received < row.size:
                        remaining(deadline)
                        data = source.read(min(CHUNK, row.size - received))
                        require(data)
                        require(os.write(target, data) == len(data))
                        digest.update(data)
                        received += len(data)
                    require(source.read(1) == b'')
                os.fchmod(target, row.mode)
                after = os.fstat(target)
                require(after.st_dev == before.st_dev and after.st_ino == before.st_ino
                        and after.st_uid == owner_uid and after.st_nlink == 1
                        and after.st_size == row.size and stat.S_IMODE(after.st_mode) == row.mode)
                named(parent, parts[-1], target)
                tree.append({'path': row.path, 'directory': False, 'mode': row.mode,
                             'bytes': received, 'sha256': digest.hexdigest()})
            finally:
                if target is not None:
                    os.close(target)
                os.close(parent)
        _tar_end(archive, deadline)
        require(getattr(archive, '_sdk_longname_count', 0) == audit.gnu_longname_headers)
    require(index == len(audit.explicit_members) and identity(os.fstat(fd)) == audit.archive_identity
            and archive_hash(fd, deadline) == audit.sha512
            and directory_pin(os.fstat(destination_fd)) == directory_pin(directory))
    remaining(deadline)
    return hashlib.sha256(json.dumps(sorted(tree, key=lambda r: r['path']), sort_keys=True,
                                     separators=(',', ':')).encode()).hexdigest()


class _NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        raise DistributionFailure('trusted-sdk-distribution-rejected')


def _download(fd, deadline):
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}),
                                        urllib.request.HTTPSHandler(context=ssl.create_default_context()), _NoRedirect())
    request = urllib.request.Request(ARCHIVE_URL, headers={'Accept-Encoding': 'identity'})
    total = 0
    require(remaining(deadline) >= 5)
    with opener.open(request, timeout=min(5, remaining(deadline))) as response:
        require(response.status == 200 and response.geturl() == ARCHIVE_URL)
        while True:
            # Reserve the socket's fixed timeout within the cumulative budget.
            require(remaining(deadline) >= 5)
            data = response.read1(CHUNK)
            if not data:
                break
            total += len(data)
            require(total <= MAX_ARCHIVE_BYTES)
            require(os.write(fd, data) == len(data))
    require(total > 0)
    return total


def install(deadline):
    """Root-only fixed installation, at most 180 seconds across all operations.

    Existing destination (including dangling links) is rejected. /usr and
    /usr/share must already be protected, root-owned and nonwritable by others.
    This module never seals or relaxes an ancestor; the parent bootstrap must
    complete its separate retained-FD ancestor hardening first. Archive hash
    and exhaustive metadata are accepted before any destination mkdir/write.
    No existing SDK is moved/deleted. Successful return is provenance only;
    the separate full SDK audit and all consumer gates remain mandatory.
    """
    require(os.geteuid() == os.getegid() == 0)
    remaining(deadline)
    deadline = min(deadline, time.monotonic() + INSTALL_SECONDS)
    remaining(deadline)
    root = share = scratch = archive = destination = None
    temporary = None
    try:
        root = os.open('/', os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
        for name in ('usr', 'share'):
            remaining(deadline)
            child = os.open(name, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=root)
            try:
                info = os.fstat(child)
                require(stat.S_ISDIR(info.st_mode) and info.st_uid == info.st_gid == 0
                        and not info.st_mode & 0o7022 and info.st_mode & 0o555 == 0o555)
                named(root, name, child)
            except BaseException:
                os.close(child)
                raise
            os.close(root)
            root = child
        share, root = root, None
        try:
            os.stat(SDK_ROOT.name, dir_fd=share, follow_symlinks=False)
        except FileNotFoundError:
            pass
        else:
            require(False)
        share_before = directory_pin(os.fstat(share))
        temporary = Path(tempfile.mkdtemp(prefix='issue779-sdk-distribution-', dir='/run'))
        os.chmod(temporary, 0o700)
        scratch = os.open(temporary, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
        require(os.fstat(scratch).st_uid == 0 and stat.S_IMODE(os.fstat(scratch).st_mode) == 0o700)
        archive = os.open('sdk.tar.gz', os.O_RDWR | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC,
                          0o600, dir_fd=scratch)
        os.fchmod(archive, 0o600)
        compressed = _download(archive, deadline)
        named(scratch, 'sdk.tar.gz', archive)
        audit = audit_archive(archive, ARCHIVE_SHA512, deadline, owner_uid=0)
        named(scratch, 'sdk.tar.gz', archive)
        require(directory_pin(os.fstat(share)) == share_before)
        os.mkdir(SDK_ROOT.name, 0o700, dir_fd=share)
        destination = os.open(SDK_ROOT.name, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=share)
        os.fchmod(destination, 0o700)
        named(share, SDK_ROOT.name, destination)
        tree = extract_audited(archive, destination, audit, deadline, owner_uid=0)
        named(scratch, 'sdk.tar.gz', archive)
        named(share, SDK_ROOT.name, destination)
        require(directory_pin(os.fstat(share)) == share_before)
        os.fchmod(destination, 0o755)
        named(share, SDK_ROOT.name, destination)
        remaining(deadline)
        return {'schema': 'issue779-pinned-sdk-distribution-v1', 'sdk_version': SDK_VERSION,
                'rid': 'linux-x64', 'root': str(SDK_ROOT), 'archive_url': ARCHIVE_URL,
                'archive_sha512': audit.sha512, 'compressed_bytes': compressed,
                'expanded_bytes': audit.total_bytes, 'node_count': len(audit.members),
                'explicit_member_count': len(audit.explicit_members), 'tree_sha256': tree,
                'gnu_longname_headers': audit.gnu_longname_headers,
                'complete': True, 'sdk_audit_completed': False, 'qualification_claim': False}
    except Exception:
        raise DistributionFailure('trusted-sdk-distribution-rejected') from None
    finally:
        for fd in (destination, archive, scratch, share, root):
            if fd is not None:
                try:
                    os.close(fd)
                except OSError:
                    pass
        if temporary is not None:
            try:
                shutil.rmtree(temporary)
            except OSError:
                pass


def main(argv=None):
    """Fixed child CLI; parent must enforce absolute process-group ownership.

    The only accepted argv is --install. Parent's fixed-argv process owner must
    enforce an absolute 180-second deadline, kill/reap the owned group on any
    stall/failure, and prohibit consumers until actual successful termination.
    Python timeouts/checks alone do not bound DNS/TLS/header/stream stalls.
    stdout is <=4096 bytes of provenance; failures are fixed stderr JSON/exit65.
    """
    try:
        require((sys.argv[1:] if argv is None else argv) == ['--install'])
        result = install(time.monotonic() + INSTALL_SECONDS)
        encoded = json.dumps(result, sort_keys=True, separators=(',', ':'))
        require(len(encoded.encode()) + 1 <= 4096)
    except BaseException:
        print('{"schema":"issue779-pinned-sdk-distribution-failure-v1","failure":"distribution-rejected"}', file=sys.stderr)
        return 65
    print(encoded)
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
