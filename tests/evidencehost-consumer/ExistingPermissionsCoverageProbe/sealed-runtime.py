"""Bounded private runtime copy; no SDK, process launch, or authority issuance.

The trusted caller selects the installed runtime and a fresh destination below
its protected workspace. Source ownership is deliberately not trusted: bytes
are copied through pinned FDs and the destination is owned by the caller.
Call cleanup only after every runtime user has physically exited. An error
after destination creation requires quarantine of the caller's fresh workspace;
this module never recursively deletes an incompletely prepared tree.
"""
import hashlib
import json
import math
import os
import re
import stat
import time
from pathlib import Path

FILE_LIMIT = 64 << 20
TOTAL_LIMIT = 256 << 20
RECORD_LIMIT = 128 << 10
VERSION_LIMIT = 16
FRAMEWORK_LIMIT = 256
CHUNK = 65536
VERSION = re.compile(r'(0|[1-9][0-9]{0,5})\.(0|[1-9][0-9]{0,5})\.(0|[1-9][0-9]{0,5})')
NAME = re.compile(r'[A-Za-z0-9][A-Za-z0-9_.+-]{0,127}')


class SealError(Exception):
    """Closed category; quarantine_required marks only our newly created tree."""

    def __init__(self, category, destination=None):
        super().__init__(category)
        self.category = category
        self.quarantine_required = destination is not None
        self.destination = destination


def _require(ok, category):
    if not ok:
        raise SealError(category)


def _remaining(deadline):
    _require(type(deadline) in (int, float) and math.isfinite(deadline) and time.monotonic() < deadline, 'deadline')


def _directory_identity(value):
    return (value.st_dev, value.st_ino, value.st_mode, value.st_uid, value.st_gid)


def _file_identity(value):
    return (_directory_identity(value), value.st_nlink, value.st_size,
            value.st_mtime_ns, value.st_ctime_ns)


def _component(value):
    _require(type(value) is str and (NAME.fullmatch(value) or value == '.version')
             and value not in ('.', '..'), 'name')
    return value


class _Chain:
    """Pinned absolute directories, including every named ancestor identity."""

    def __init__(self, path, deadline):
        self.fds = []
        self.links = []
        try:
            _require(path.is_absolute() and '..' not in path.parts and str(path) == str(path.resolve(strict=True)), 'canonical-directory')
            _remaining(deadline)
            self.fds.append(os.open('/', os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW))
            _require(len(path.parts) <= 64, 'path-depth')
            for part in path.parts[1:]:
                _remaining(deadline)
                parent = self.fds[-1]
                before = os.stat(part, dir_fd=parent, follow_symlinks=False)
                _require(stat.S_ISDIR(before.st_mode), 'directory-type')
                fd = os.open(part, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=parent)
                self.fds.append(fd)
                identity = _directory_identity(before)
                _require(identity == _directory_identity(os.fstat(fd)) == _directory_identity(os.stat(part, dir_fd=parent, follow_symlinks=False)), 'directory-changed')
                self.links.append((parent, part, fd, identity))
            self.check(deadline)
        except BaseException:
            self.close()
            raise

    @property
    def fd(self):
        return self.fds[-1]

    def check(self, deadline):
        for parent, name, fd, identity in self.links:
            _remaining(deadline)
            _require(identity == _directory_identity(os.fstat(fd)) == _directory_identity(os.stat(name, dir_fd=parent, follow_symlinks=False)), 'directory-changed')

    def close(self):
        while self.fds:
            os.close(self.fds.pop())


def _inventory(fd, deadline, limit):
    values = []
    with os.scandir(fd) as entries:
        for entry in entries:
            _remaining(deadline)
            _require(len(values) < limit, 'inventory-bound')
            values.append(_component(entry.name))
    return sorted(values)


def _child(parent, name, deadline):
    _remaining(deadline)
    _component(name)
    before = os.stat(name, dir_fd=parent, follow_symlinks=False)
    _require(stat.S_ISDIR(before.st_mode), 'directory-type')
    fd = os.open(name, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=parent)
    try:
        _require(_directory_identity(before) == _directory_identity(os.fstat(fd)) == _directory_identity(os.stat(name, dir_fd=parent, follow_symlinks=False)), 'directory-changed')
        return fd
    except BaseException:
        os.close(fd)
        raise


def _version(fd, deadline):
    names = _inventory(fd, deadline, VERSION_LIMIT)
    choices = []
    for name in names:
        _remaining(deadline)
        match = VERSION.fullmatch(name)
        _require(match is not None, 'version')
        entry = os.stat(name, dir_fd=fd, follow_symlinks=False)
        _require(stat.S_ISDIR(entry.st_mode), 'version-directory')
        version = tuple(int(part) for part in match.groups())
        if version[:2] == (10, 0):
            choices.append((version, name))
    _require(bool(choices), 'version-missing')
    return max(choices)[1], names


def _framework_file(name):
    _component(name)
    _require(name in ('.version', 'createdump', 'LICENSE.txt', 'ThirdPartyNotices.txt')
             or name.endswith(('.dll', '.json', '.so', '.dat', '.pdb')), 'framework-kind')


def _copy_file(source_fd, source_name, target_fd, target_name, deadline, uid, gid, remaining, executable=False):
    _remaining(deadline)
    _require(type(remaining) is int and 0 <= remaining <= TOTAL_LIMIT, 'total-bound')
    cap = min(FILE_LIMIT, remaining)
    before = os.stat(source_name, dir_fd=source_fd, follow_symlinks=False)
    _require(stat.S_ISREG(before.st_mode) and before.st_nlink == 1 and 0 <= before.st_size <= cap, 'source-file')
    source = os.open(source_name, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK, dir_fd=source_fd)
    target = None
    try:
        _require(_file_identity(before) == _file_identity(os.fstat(source)), 'source-changed')
        target = os.open(target_name, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_NONBLOCK, 0o600, dir_fd=target_fd)
        os.fchown(target, uid, gid)
        digest = hashlib.sha256()
        length = 0
        prefix = bytearray()
        while True:
            _remaining(deadline)
            block = os.read(source, min(CHUNK, cap + 1 - length))
            if not block:
                break
            length += len(block)
            _require(length <= cap and length <= before.st_size, 'source-growth')
            prefix.extend(block[:max(0, 4 - len(prefix))])
            digest.update(block)
            view = memoryview(block)
            while view:
                _remaining(deadline)
                written = os.write(target, view)
                _require(written > 0, 'copy-write')
                view = view[written:]
        _require(length == before.st_size and _file_identity(before) == _file_identity(os.fstat(source)) == _file_identity(os.stat(source_name, dir_fd=source_fd, follow_symlinks=False)), 'source-changed')
        if executable or source_name.endswith('.so') or source_name == 'createdump':
            _require(prefix == b'\x7fELF', 'elf')
        mode = 0o755 if executable else 0o444
        os.fchmod(target, mode)
        final = os.fstat(target)
        _require(stat.S_ISREG(final.st_mode) and final.st_nlink == 1 and final.st_uid == uid and final.st_gid == gid and stat.S_IMODE(final.st_mode) == mode and final.st_size == length, 'destination-file')
        _require(_file_identity(final) == _file_identity(os.stat(target_name, dir_fd=target_fd, follow_symlinks=False)), 'destination-changed')
        _remaining(deadline)
        return {'sha256': digest.hexdigest(), 'bytes': length, 'mode': f'{mode:04o}'}, _file_identity(final), _file_identity(before)
    finally:
        if target is not None:
            os.close(target)
        os.close(source)


class SealedRuntime:
    """Owns only the fresh copied manifest. Cleanup requires all users gone.

    record is a detached, bounded data summary, never an admission credential.
    cleanup validates the entire manifest before deleting any entry; unexpected
    entries, replacement, modified bytes, or an expired deadline fail closed.
    """

    def __init__(self, destination, parent, directory_fds, directory_links, files, identities, record):
        self.dotnet = destination / 'dotnet'
        self._destination = destination
        self._parent = parent
        self._directories = directory_fds
        self._links = directory_links
        self._files = files
        self._identities = identities
        self._record_bytes = json.dumps(record, sort_keys=True, separators=(',', ':')).encode()
        _require(len(self._record_bytes) <= RECORD_LIMIT, 'record-bound')
        self._closed = False

    @property
    def record(self):
        return json.loads(self._record_bytes)

    def _check(self, deadline):
        self._parent.check(deadline)
        for relative, parent_fd, name, fd, identity in self._links:
            _remaining(deadline)
            _require(identity == _directory_identity(os.fstat(fd)) == _directory_identity(os.stat(name, dir_fd=parent_fd, follow_symlinks=False)), 'destination-directory-changed')
            expected = {Path(path).name for path in self._files if str(Path(path).parent) == (relative or '.')}
            expected.update(Path(path).name for path in self._directories if path and str(Path(path).parent) == (relative or '.'))
            _require(set(_inventory(fd, deadline, FRAMEWORK_LIMIT + 8)) == expected, 'destination-inventory')

    def cleanup(self, deadline):
        """Delete exact retained files/dirs; caller first joins every runtime user.

        On any failure leave the residual tree for quarantine, close every owned
        FD, and reject reuse of this object. No recursive or best-effort deletion.
        """
        _require(not self._closed, 'cleanup-closed')
        try:
            self._check(deadline)
            for relative, expected in self._files.items():
                _remaining(deadline)
                path = Path(relative)
                parent = self._directories['' if str(path.parent) == '.' else str(path.parent)]
                fd = os.open(path.name, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK, dir_fd=parent)
                try:
                    before = os.fstat(fd)
                    _require(_file_identity(before) == self._identities[relative], 'cleanup-file-changed')
                    digest = hashlib.sha256()
                    length = 0
                    while True:
                        _remaining(deadline)
                        block = os.read(fd, min(CHUNK, expected['bytes'] + 1 - length))
                        if not block:
                            break
                        length += len(block)
                        _require(length <= expected['bytes'], 'cleanup-size')
                        digest.update(block)
                    _require(length == expected['bytes'] and digest.hexdigest() == expected['sha256'] and _file_identity(before) == _file_identity(os.fstat(fd)) == _file_identity(os.stat(path.name, dir_fd=parent, follow_symlinks=False)), 'cleanup-file-changed')
                finally:
                    os.close(fd)
            self._check(deadline)
            for relative in self._files:
                _remaining(deadline)
                path = Path(relative)
                parent = self._directories['' if str(path.parent) == '.' else str(path.parent)]
                _require(_file_identity(os.stat(path.name, dir_fd=parent, follow_symlinks=False)) == self._identities[relative], 'cleanup-file-changed')
                os.unlink(path.name, dir_fd=parent)
            for relative, parent_fd, name, fd, identity in reversed(self._links):
                _remaining(deadline)
                _require(identity == _directory_identity(os.fstat(fd)) == _directory_identity(os.stat(name, dir_fd=parent_fd, follow_symlinks=False)), 'cleanup-directory-changed')
                os.rmdir(name, dir_fd=parent_fd)
            self._parent.check(deadline)
            _remaining(deadline)
        except BaseException as error:
            if isinstance(error, SealError):
                error.quarantine_required = True
                error.destination = self._destination
                raise
            raise SealError('cleanup-io', self._destination) from None
        finally:
            self._closed = True
            for fd in reversed(list(self._directories.values())):
                os.close(fd)
            self._parent.close()


def seal_runtime(selected_dotnet: Path, destination: Path, deadline: float, owner_uid=0, owner_gid=0):
    """Copy one canonical dotnet host, highest stable 10.0 fxr/framework.

    Maximums: 16 version directories per family, 256 flat framework files,
    64 MiB per file, 256 MiB total, 128 KiB summary. Directory chains and files
    are no-follow FD pinned; .so files and the host require ELF magic. Other
    major/minor stable versions are inspected only for selection, never copied.
    Source mutation is rejected during copying; it may change after return.
    The immediate destination parent must already be creator-owned, protected,
    and canonical. A partial failure leaves our fresh root 0700 for quarantine.
    """
    source_chain = parent_chain = None
    source_fds = []
    source_links = []
    destination_fds = {}
    links = []
    created = False
    try:
        _remaining(deadline)
        _require(type(owner_uid) is int and type(owner_gid) is int and owner_uid == os.geteuid() and owner_gid >= 0, 'creator')
        selected_dotnet, destination = Path(selected_dotnet), Path(destination)
        _require(selected_dotnet.is_absolute() and str(selected_dotnet) == str(selected_dotnet.resolve(strict=True)) and selected_dotnet.name == 'dotnet' and not str(selected_dotnet).startswith(('/home/', '/root/', '/run/user/')), 'dotnet-path')
        _component(destination.name)
        source_chain = _Chain(selected_dotnet.parent, deadline)
        parent_chain = _Chain(destination.parent, deadline)
        parent = os.fstat(parent_chain.fd)
        _require(parent.st_uid == owner_uid and parent.st_gid == owner_gid and not stat.S_IMODE(parent.st_mode) & 0o022, 'destination-parent')
        _require(destination != selected_dotnet.parent and not destination.is_relative_to(selected_dotnet.parent), 'destination-overlap')
        host = _child(source_chain.fd, 'host', deadline); source_fds.append(host)
        fxrs = _child(host, 'fxr', deadline); source_fds.append(fxrs)
        fxr_version, fxr_inventory = _version(fxrs, deadline)
        fxr = _child(fxrs, fxr_version, deadline); source_fds.append(fxr)
        _require(_inventory(fxr, deadline, VERSION_LIMIT) == ['libhostfxr.so'], 'fxr-inventory')
        shared = _child(source_chain.fd, 'shared', deadline); source_fds.append(shared)
        frameworks = _child(shared, 'Microsoft.NETCore.App', deadline); source_fds.append(frameworks)
        framework_version, framework_inventory = _version(frameworks, deadline)
        framework = _child(frameworks, framework_version, deadline); source_fds.append(framework)
        for parent_fd, name, fd in ((source_chain.fd, 'host', host), (host, 'fxr', fxrs), (fxrs, fxr_version, fxr), (source_chain.fd, 'shared', shared), (shared, 'Microsoft.NETCore.App', frameworks), (frameworks, framework_version, framework)):
            _remaining(deadline)
            identity = _directory_identity(os.fstat(fd))
            _require(identity == _directory_identity(os.stat(name, dir_fd=parent_fd, follow_symlinks=False)), 'source-directory-changed')
            source_links.append((parent_fd, name, fd, identity))
        framework_files = _inventory(framework, deadline, FRAMEWORK_LIMIT)
        _require({'System.Private.CoreLib.dll', 'libhostpolicy.so', 'Microsoft.NETCore.App.deps.json', 'Microsoft.NETCore.App.runtimeconfig.json'} <= set(framework_files), 'framework-required')
        for name in framework_files:
            _remaining(deadline)
            _framework_file(name)
        _remaining(deadline)
        os.mkdir(destination.name, 0o700, dir_fd=parent_chain.fd)
        created = True
        directory_specs = [('', parent_chain.fd, destination.name), ('host', None, 'host'), ('host/fxr', None, 'fxr'), (f'host/fxr/{fxr_version}', None, fxr_version), ('shared', None, 'shared'), ('shared/Microsoft.NETCore.App', None, 'Microsoft.NETCore.App'), (f'shared/Microsoft.NETCore.App/{framework_version}', None, framework_version)]
        for relative, supplied_parent, name in directory_specs:
            _remaining(deadline)
            parent_fd = supplied_parent if supplied_parent is not None else destination_fds['' if str(Path(relative).parent) == '.' else str(Path(relative).parent)]
            if relative:
                os.mkdir(name, 0o700, dir_fd=parent_fd)
            fd = _child(parent_fd, name, deadline)
            destination_fds[relative] = fd
            initial = os.fstat(fd)
            _require(initial.st_uid == owner_uid and stat.S_IMODE(initial.st_mode) & 0o077 == 0, 'created-directory')
            os.fchown(fd, owner_uid, owner_gid)
            os.fchmod(fd, 0o700)
            links.append((relative, parent_fd, name, fd, None))
        requests = [(source_chain.fd, 'dotnet', '', True), (fxr, 'libhostfxr.so', f'host/fxr/{fxr_version}', False)]
        requests.extend((framework, name, f'shared/Microsoft.NETCore.App/{framework_version}', False) for name in framework_files)
        files, identities, source_checks = {}, {}, []
        total = 0
        for source_fd, name, relative_parent, executable in requests:
            _remaining(deadline)
            source_chain.check(deadline); parent_chain.check(deadline)
            for parent_fd, child_name, child_fd, original in source_links:
                _remaining(deadline)
                _require(original == _directory_identity(os.fstat(child_fd)) == _directory_identity(os.stat(child_name, dir_fd=parent_fd, follow_symlinks=False)), 'source-directory-changed')
            size = os.stat(name, dir_fd=source_fd, follow_symlinks=False).st_size
            _require(0 <= size <= FILE_LIMIT and total + size <= TOTAL_LIMIT, 'total-bound')
            row, identity, original = _copy_file(source_fd, name, destination_fds[relative_parent], name, deadline, owner_uid, owner_gid, TOTAL_LIMIT - total, executable)
            relative = str(Path(relative_parent) / name)
            files[relative], identities[relative] = row, identity
            source_checks.append((source_fd, name, original))
            total += row['bytes']
            _require(total <= TOTAL_LIMIT, 'total-bound')
        source_chain.check(deadline); parent_chain.check(deadline)
        for parent_fd, name, fd, identity in source_links:
            _remaining(deadline)
            _require(identity == _directory_identity(os.fstat(fd)) == _directory_identity(os.stat(name, dir_fd=parent_fd, follow_symlinks=False)), 'source-directory-changed')
        _require(_version(fxrs, deadline) == (fxr_version, fxr_inventory) and _version(frameworks, deadline) == (framework_version, framework_inventory) and _inventory(fxr, deadline, VERSION_LIMIT) == ['libhostfxr.so'] and _inventory(framework, deadline, FRAMEWORK_LIMIT) == framework_files, 'source-inventory-changed')
        for fd, name, identity in source_checks:
            _remaining(deadline)
            _require(_file_identity(os.stat(name, dir_fd=fd, follow_symlinks=False)) == identity, 'source-changed')
        # Seal the outer destination last, so partial copies remain inaccessible.
        sealed_links = []
        for relative, parent_fd, name, fd, _ in reversed(links):
            _remaining(deadline)
            os.fchmod(fd, 0o755)
            observed = os.fstat(fd)
            _require(observed.st_uid == owner_uid and observed.st_gid == owner_gid and stat.S_IMODE(observed.st_mode) == 0o755, 'destination-directory')
            identity = _directory_identity(observed)
            _require(identity == _directory_identity(os.stat(name, dir_fd=parent_fd, follow_symlinks=False)), 'destination-directory-changed')
            sealed_links.append((relative, parent_fd, name, fd, identity))
        record = {'schema': 'issue779-sealed-runtime-copy-v1', 'owner_uid': owner_uid, 'owner_gid': owner_gid, 'hostfxr_version': fxr_version, 'framework_version': framework_version, 'file_count': len(files), 'total_bytes': total, 'files': files}
        runtime = SealedRuntime(destination, parent_chain, destination_fds, list(reversed(sealed_links)), files, identities, record)
        runtime._check(deadline)
        _remaining(deadline)
        parent_chain = None
        destination_fds = {}
        return runtime
    except BaseException as error:
        if created:
            # The caller owns this exclusive path; never claim partial cleanup.
            if destination_fds.get('') is not None:
                try:
                    os.fchmod(destination_fds[''], 0o700)
                except OSError:
                    raise SealError('runtime-quarantine', destination) from None
        if isinstance(error, SealError):
            error.quarantine_required = created
            error.destination = destination if created else None
            raise
        raise SealError('runtime-io', destination if created else None) from None
    finally:
        for fd in reversed(list(destination_fds.values())):
            os.close(fd)
        for fd in reversed(source_fds):
            os.close(fd)
        if source_chain is not None:
            source_chain.close()
        if parent_chain is not None:
            parent_chain.close()
