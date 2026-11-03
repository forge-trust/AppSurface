import os
import re
import stat
import sys

class Reject(Exception):
    pass

def need(condition):
    if not condition:
        raise Reject()

def signature(value):
    return (value.st_dev, value.st_ino, value.st_mode, value.st_uid, value.st_gid,
            value.st_nlink, value.st_size, value.st_mtime_ns, value.st_ctime_ns)

def directory_flags():
    return os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | getattr(os, "O_CLOEXEC", 0)

def open_absolute_directory(path):
    need(type(path) is str and path.startswith("/") and os.path.normpath(path) == path)
    fd = os.open("/", directory_flags())
    try:
        if path == "/":
            return fd
        for part in path.split("/")[1:]:
            need(part not in ("", ".", ".."))
            child = os.open(part, directory_flags(), dir_fd=fd)
            os.close(fd)
            fd = child
        return fd
    except BaseException:
        os.close(fd)
        raise

def require_directory(value, device, uid, gid):
    need(stat.S_ISDIR(value.st_mode) and value.st_dev == device
         and value.st_uid == uid and value.st_gid == gid
         and stat.S_IMODE(value.st_mode) == 0o700)

def open_child_directory(parent_fd, name, device, uid, gid):
    before = os.stat(name, dir_fd=parent_fd, follow_symlinks=False)
    require_directory(before, device, uid, gid)
    fd = os.open(name, directory_flags(), dir_fd=parent_fd)
    try:
        after = os.fstat(fd)
        need(signature(before) == signature(after))
        require_directory(after, device, uid, gid)
        return fd
    except BaseException:
        os.close(fd)
        raise

def exact_names(fd, expected):
    names = os.listdir(fd)
    need(len(names) == len(expected) and set(names) == set(expected))

def open_descriptor(worker_fd, device, uid, gid):
    name = "worker-control.json"
    before = os.stat(name, dir_fd=worker_fd, follow_symlinks=False)
    need(stat.S_ISREG(before.st_mode) and before.st_dev == device
         and before.st_uid == uid and before.st_gid == gid
         and stat.S_IMODE(before.st_mode) == 0o400
         and before.st_nlink == 1 and 0 < before.st_size <= 65536)
    flags = os.O_RDONLY | os.O_NOFOLLOW | getattr(os, "O_NONBLOCK", 0) | getattr(os, "O_CLOEXEC", 0)
    fd = os.open(name, flags, dir_fd=worker_fd)
    try:
        after = os.fstat(fd)
        need(signature(before) == signature(after) and stat.S_ISREG(after.st_mode)
             and after.st_dev == device and after.st_uid == uid and after.st_gid == gid
             and stat.S_IMODE(after.st_mode) == 0o400 and after.st_nlink == 1
             and 0 < after.st_size <= 65536)
    finally:
        os.close(fd)

def require_socket(broker_fd, device, uid, gid):
    name = "control.sock"
    before = os.stat(name, dir_fd=broker_fd, follow_symlinks=False)
    need(stat.S_ISSOCK(before.st_mode) and before.st_dev == device
         and before.st_uid == uid and before.st_gid == gid
         and stat.S_IMODE(before.st_mode) == 0o600
         and before.st_nlink == 1 and before.st_size == 0)
    after = os.stat(name, dir_fd=broker_fd, follow_symlinks=False)
    need(signature(before) == signature(after) and stat.S_ISSOCK(after.st_mode))

def audit(root, generation, uid, gid):
    need(type(generation) is str and re.fullmatch(r"[0-9a-f]{32}", generation) is not None)
    need(type(uid) is int and type(gid) is int and uid >= 0 and gid >= 0)
    need(type(root) is str and os.path.isabs(root) and os.path.normpath(root) == root
         and os.path.basename(root) == "appsurface-evidence-" + generation)
    parent_path, root_name = os.path.split(root)
    parent_fd = open_absolute_directory(parent_path)
    root_fd = worker_fd = output_fd = raw_fd = broker_fd = None
    try:
        parent = os.fstat(parent_fd)
        need(stat.S_ISDIR(parent.st_mode) and parent.st_uid == uid
             and parent.st_gid == gid and (stat.S_IMODE(parent.st_mode) & 0o022) == 0)
        before_root = os.stat(root_name, dir_fd=parent_fd, follow_symlinks=False)
        root_device = before_root.st_dev
        require_directory(before_root, root_device, uid, gid)
        root_fd = os.open(root_name, directory_flags(), dir_fd=parent_fd)
        after_root = os.fstat(root_fd)
        need(signature(before_root) == signature(after_root))
        require_directory(after_root, root_device, uid, gid)
        exact_names(root_fd, ("worker", "output", "raw-results"))
        worker_fd = open_child_directory(root_fd, "worker", root_device, uid, gid)
        output_fd = open_child_directory(root_fd, "output", root_device, uid, gid)
        raw_fd = open_child_directory(root_fd, "raw-results", root_device, uid, gid)
        exact_names(worker_fd, ("broker", "worker-control.json"))
        exact_names(output_fd, ())
        exact_names(raw_fd, ())
        open_descriptor(worker_fd, root_device, uid, gid)
        broker_fd = open_child_directory(worker_fd, "broker", root_device, uid, gid)
        exact_names(broker_fd, ("control.sock",))
        require_socket(broker_fd, root_device, uid, gid)
        after_name = os.stat(root_name, dir_fd=parent_fd, follow_symlinks=False)
        need(signature(after_root) == signature(after_name))
    finally:
        for fd in (broker_fd, raw_fd, output_fd, worker_fd, root_fd, parent_fd):
            if fd is not None:
                os.close(fd)

def main():
    try:
        need(len(sys.argv) == 5)
        root, generation = sys.argv[1], sys.argv[2]
        uid_text, gid_text = sys.argv[3], sys.argv[4]
        need(re.fullmatch(r"0|[1-9][0-9]{0,9}", uid_text) is not None
             and re.fullmatch(r"0|[1-9][0-9]{0,9}", gid_text) is not None)
        audit(root, generation, int(uid_text), int(gid_text))
        return 0
    except (OSError, ValueError, TypeError, OverflowError, Reject, RecursionError):
        print("N16_WORKSPACE_CUSTODY_REJECTED", file=sys.stderr)
        return 2

raise SystemExit(main())
