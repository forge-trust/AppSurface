"""Descriptor-bound, bounded staging for the issue 779 native fixture.

The source is inventoried before the destination is created. Both passes use
retained directory descriptors and no-follow *at operations; any observed
substitution or metadata/name change fails closed without retrying.
"""

import hashlib
import os
import stat


MAX_NODES = 8192
MAX_DEPTH = 32
MAX_PATH_BYTES = 4096
MAX_FILE_BYTES = 256 * 1024 * 1024
MAX_TOTAL_BYTES = 1024 * 1024 * 1024
CHUNK_BYTES = 64 * 1024

_DIR_FLAGS = os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC
_FILE_FLAGS = os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC | getattr(os, "O_NONBLOCK", 0)
_DEST_FILE_FLAGS = os.O_RDWR | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC


def _absolute_parts(path):
    value = os.fspath(path)
    if not isinstance(value, str) or not value.startswith("/") or "\0" in value:
        raise ValueError("staging-path")
    parts = value.split("/")[1:]
    if not parts or any(part in ("", ".", "..") for part in parts):
        raise ValueError("staging-path")
    return parts


def _metadata(st):
    return (st.st_dev, st.st_ino, st.st_uid, st.st_gid, st.st_mode,
            st.st_nlink, st.st_size, st.st_mtime_ns, st.st_ctime_ns)


def _identity(st):
    return (st.st_dev, st.st_ino, st.st_uid, st.st_gid, stat.S_IFMT(st.st_mode),
            stat.S_IMODE(st.st_mode))


def _same_named(parent_fd, name, expected, *, full=True):
    actual = os.stat(name, dir_fd=parent_fd, follow_symlinks=False)
    if ( _metadata(actual) if full else _identity(actual) ) != expected:
        raise RuntimeError("staging-substitution")


def _open_absolute_directory(path):
    """Open every absolute component no-follow and retain its parent/name binding."""
    parts = _absolute_parts(path)
    fds = [os.open("/", _DIR_FLAGS)]
    bindings = []
    try:
        for name in parts:
            before = os.stat(name, dir_fd=fds[-1], follow_symlinks=False)
            if not stat.S_ISDIR(before.st_mode):
                raise RuntimeError("staging-path-component")
            child = os.open(name, _DIR_FLAGS, dir_fd=fds[-1])
            opened = os.fstat(child)
            if _identity(opened) != _identity(before):
                os.close(child)
                raise RuntimeError("staging-substitution")
            bindings.append((fds[-1], name, child, _identity(opened)))
            fds.append(child)
        return fds, bindings
    except BaseException:
        for fd in reversed(fds):
            try:
                os.close(fd)
            except OSError:
                pass
        raise


def _recheck_bindings(bindings):
    for parent_fd, name, child_fd, expected in bindings:
        if _identity(os.fstat(child_fd)) != expected:
            raise RuntimeError("staging-substitution")
        _same_named(parent_fd, name, expected, full=False)


def _relative_path(parent, name, depth):
    if (not name or name in (".", "..") or "/" in name or "\\" in name
            or name.startswith("/")):
        raise RuntimeError("staging-path")
    relative = name if not parent else parent + "/" + name
    try:
        encoded = relative.encode("utf-8", "strict")
    except UnicodeError as error:
        raise RuntimeError("staging-path") from error
    if depth > MAX_DEPTH or len(encoded) > MAX_PATH_BYTES:
        raise RuntimeError("staging-path-bound")
    return relative


def _scan_source(root_fd):
    """Preflight every node and retain exact snapshots and directory listings."""
    nodes = {"": ("dir", _metadata(os.fstat(root_fd)))}
    listings = {}
    count = 1  # Include the retained source root itself in the node budget.
    if count > MAX_NODES:
        raise RuntimeError("staging-node-bound")
    total = 0

    def walk(directory_fd, prefix, depth):
        nonlocal count, total
        before = _metadata(os.fstat(directory_fd))
        names = sorted(os.listdir(directory_fd))
        listings[prefix] = tuple(names)
        for name in names:
            relative = _relative_path(prefix, name, depth + 1)
            count += 1
            if count > MAX_NODES:
                raise RuntimeError("staging-node-bound")
            child_stat = os.stat(name, dir_fd=directory_fd, follow_symlinks=False)
            if stat.S_ISDIR(child_stat.st_mode):
                child_fd = os.open(name, _DIR_FLAGS, dir_fd=directory_fd)
                try:
                    opened = os.fstat(child_fd)
                    if _metadata(opened) != _metadata(child_stat):
                        raise RuntimeError("staging-substitution")
                    nodes[relative] = ("dir", _metadata(opened))
                    walk(child_fd, relative, depth + 1)
                    _same_named(directory_fd, name, _metadata(opened))
                finally:
                    os.close(child_fd)
            elif stat.S_ISREG(child_stat.st_mode):
                if child_stat.st_nlink != 1:
                    raise RuntimeError("staging-hardlink")
                if child_stat.st_size > MAX_FILE_BYTES:
                    raise RuntimeError("staging-file-bound")
                total += child_stat.st_size
                if total > MAX_TOTAL_BYTES:
                    raise RuntimeError("staging-tree-bound")
                child_fd = os.open(name, _FILE_FLAGS, dir_fd=directory_fd)
                try:
                    opened = os.fstat(child_fd)
                    if not stat.S_ISREG(opened.st_mode) or _metadata(opened) != _metadata(child_stat):
                        raise RuntimeError("staging-substitution")
                    nodes[relative] = ("file", _metadata(opened))
                    _same_named(directory_fd, name, _metadata(opened))
                finally:
                    os.close(child_fd)
            else:
                raise RuntimeError("staging-special-file")

        if tuple(sorted(os.listdir(directory_fd))) != listings[prefix]:
            raise RuntimeError("staging-directory-changed")
        if _metadata(os.fstat(directory_fd)) != before:
            raise RuntimeError("staging-directory-changed")
        if prefix:
            # Its caller checks the named binding after this recursive walk.
            pass

    walk(root_fd, "", 0)
    return nodes, listings, count, total


def _open_checked_directory(parent_fd, name, expected):
    before = os.stat(name, dir_fd=parent_fd, follow_symlinks=False)
    if _metadata(before) != expected or not stat.S_ISDIR(before.st_mode):
        raise RuntimeError("staging-substitution")
    fd = os.open(name, _DIR_FLAGS, dir_fd=parent_fd)
    if _metadata(os.fstat(fd)) != expected:
        os.close(fd)
        raise RuntimeError("staging-substitution")
    return fd


def _chown_fd(fd, owner_root):
    if owner_root:
        os.fchown(fd, 0, 0)


def _copy_source(root_fd, destination_fd, nodes, listings, owner_root):
    inventory = {}

    def walk(source_fd, dest_fd, prefix, depth):
        expected_dir = nodes[prefix][1]
        if _metadata(os.fstat(source_fd)) != expected_dir:
            raise RuntimeError("staging-source-changed")
        expected_names = listings[prefix]
        if tuple(sorted(os.listdir(source_fd))) != expected_names:
            raise RuntimeError("staging-source-changed")
        for name in expected_names:
            relative = _relative_path(prefix, name, depth + 1)
            kind, expected = nodes[relative]
            _same_named(source_fd, name, expected)
            source_stat = os.stat(name, dir_fd=source_fd, follow_symlinks=False)
            if kind == "dir":
                source_child = _open_checked_directory(source_fd, name, expected)
                try:
                    os.mkdir(name, 0o700, dir_fd=dest_fd)
                    dest_child = os.open(name, _DIR_FLAGS, dir_fd=dest_fd)
                    try:
                        initial = os.fstat(dest_child)
                        _chown_fd(dest_child, owner_root)
                        walk(source_child, dest_child, relative, depth + 1)
                        _same_named(source_fd, name, expected)
                        if _metadata(os.fstat(source_child)) != expected:
                            raise RuntimeError("staging-source-changed")
                        os.fchmod(dest_child, 0o755)
                        if _identity(os.fstat(dest_child))[:2] != _identity(initial)[:2]:
                            raise RuntimeError("staging-destination-changed")
                        _same_named(dest_fd, name, _identity(os.fstat(dest_child)), full=False)
                    finally:
                        os.close(dest_child)
                finally:
                    os.close(source_child)
            else:
                if not stat.S_ISREG(source_stat.st_mode) or source_stat.st_nlink != 1:
                    raise RuntimeError("staging-source-changed")
                source_file = os.open(name, _FILE_FLAGS, dir_fd=source_fd)
                try:
                    if _metadata(os.fstat(source_file)) != expected:
                        raise RuntimeError("staging-source-changed")
                    if expected[6] == 0:
                        _same_named(source_fd, name, expected)
                        if _metadata(os.fstat(source_file)) != expected:
                            raise RuntimeError("staging-source-changed")
                        continue  # Empty markers are counted and checked but intentionally omitted.

                    destination_file = os.open(name, _DEST_FILE_FLAGS, 0o600, dir_fd=dest_fd)
                    try:
                        source_hash = hashlib.sha256()
                        copied = 0
                        while True:
                            block = os.read(source_file, CHUNK_BYTES)
                            if not block:
                                break
                            copied += len(block)
                            if copied > expected[6]:
                                raise RuntimeError("staging-source-changed")
                            source_hash.update(block)
                            view = memoryview(block)
                            while view:
                                written = os.write(destination_file, view)
                                if written <= 0:
                                    raise OSError("staging-short-write")
                                view = view[written:]
                        if copied != expected[6]:
                            raise RuntimeError("staging-source-changed")
                        _same_named(source_fd, name, expected)
                        if _metadata(os.fstat(source_file)) != expected:
                            raise RuntimeError("staging-source-changed")
                        os.lseek(destination_file, 0, os.SEEK_SET)
                        destination_hash = hashlib.sha256()
                        verified = 0
                        while True:
                            block = os.read(destination_file, CHUNK_BYTES)
                            if not block:
                                break
                            verified += len(block)
                            destination_hash.update(block)
                        if verified != copied or destination_hash.digest() != source_hash.digest():
                            raise RuntimeError("staging-destination-mismatch")
                        _chown_fd(destination_file, owner_root)
                        mode = 0o755 if expected[4] & 0o111 else 0o644
                        os.fchmod(destination_file, mode)
                        _same_named(source_fd, name, expected)
                        if _metadata(os.fstat(source_file)) != expected:
                            raise RuntimeError("staging-source-changed")
                        dest_stat = os.fstat(destination_file)
                        dest_identity = _identity(dest_stat)
                        _same_named(dest_fd, name, dest_identity, full=False)
                        if (not stat.S_ISREG(dest_stat.st_mode) or dest_stat.st_nlink != 1
                                or dest_stat.st_size != copied or stat.S_IMODE(dest_stat.st_mode) != mode
                                or (owner_root and (dest_stat.st_uid != 0 or dest_stat.st_gid != 0))):
                            raise RuntimeError("staging-destination-changed")
                        inventory[relative] = {
                            "sha256": source_hash.hexdigest(),
                            "bytes": copied,
                            "mode": format(mode, "04o"),
                        }
                    finally:
                        os.close(destination_file)
                finally:
                    os.close(source_file)

        if tuple(sorted(os.listdir(source_fd))) != expected_names:
            raise RuntimeError("staging-source-changed")
        if _metadata(os.fstat(source_fd)) != expected_dir:
            raise RuntimeError("staging-source-changed")

    walk(root_fd, destination_fd, "", 0)
    return inventory


def copy_tree(source, dest, owner_root=True):
    """Copy one bounded regular-file tree into a new no-follow destination.

    The return shape matches ``run-native.py``'s existing file inventory. Empty
    regular files are counted and verified but intentionally absent, as in its
    previous staging implementation. No following, retrying or adoption occurs.
    """
    source_fds, source_bindings = _open_absolute_directory(source)
    destination_fds = []
    destination_bindings = []
    created_root = None
    try:
        source_root_fd = source_fds[-1]
        nodes, listings, _, _ = _scan_source(source_root_fd)
        _recheck_bindings(source_bindings)

        dest_parts = _absolute_parts(dest)
        destination_fds, destination_bindings = _open_absolute_directory("/" + "/".join(dest_parts[:-1])) if len(dest_parts) > 1 else ([os.open("/", _DIR_FLAGS)], [])
        parent_fd = destination_fds[-1]
        parent_stat = os.fstat(parent_fd)
        trusted_uid = 0 if owner_root else os.geteuid()
        if (not stat.S_ISDIR(parent_stat.st_mode) or parent_stat.st_uid != trusted_uid
                or parent_stat.st_mode & 0o022):
            raise RuntimeError("staging-untrusted-destination-parent")
        leaf = dest_parts[-1]
        try:
            os.stat(leaf, dir_fd=parent_fd, follow_symlinks=False)
        except FileNotFoundError:
            pass
        else:
            raise FileExistsError("staging-destination-exists")

        os.mkdir(leaf, 0o700, dir_fd=parent_fd)
        created_root = os.open(leaf, _DIR_FLAGS, dir_fd=parent_fd)
        root_initial = os.fstat(created_root)
        _chown_fd(created_root, owner_root)
        inventory = _copy_source(source_root_fd, created_root, nodes, listings, owner_root)
        _recheck_bindings(source_bindings)
        os.fchmod(created_root, 0o755)
        root_final = os.fstat(created_root)
        if (_identity(root_initial)[:2] != _identity(root_final)[:2]
                or not stat.S_ISDIR(root_final.st_mode) or stat.S_IMODE(root_final.st_mode) != 0o755
                or (owner_root and (root_final.st_uid != 0 or root_final.st_gid != 0))):
            raise RuntimeError("staging-destination-changed")
        _same_named(parent_fd, leaf, _identity(root_final), full=False)
        _recheck_bindings(destination_bindings)
        return inventory
    finally:
        for fd in reversed(destination_fds):
            try:
                os.close(fd)
            except OSError:
                pass
        for fd in reversed(source_fds):
            try:
                os.close(fd)
            except OSError:
                pass
        if created_root is not None:
            os.close(created_root)
