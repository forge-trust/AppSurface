"""Hostile subject run under a separate systemd User with a minimal environment."""
import ctypes
import errno
import json
import os
import sys
from pathlib import Path


def main():
    tool, output, allowed, worker_pid, supervisor = sys.argv[1:]
    results = {}
    results["allowed-input"] = Path(allowed).read_text() == "declared-input\n"
    targets = {
        "registration-read": Path(tool) / "registration.bin",
        "verifier-read": Path(tool) / "verifier.bin",
        "worker-fd-read": Path(f"/proc/{worker_pid}/fd/0"),
        "worker-environment-read": Path(f"/proc/{worker_pid}/environ"),
        "supervisor-read": Path(supervisor),
    }
    for name, path in targets.items():
        try:
            with path.open("rb") as stream:
                stream.read(1)
        except PermissionError:
            results[name] = True
        else:
            results[name] = False
    try:
        with (Path(output) / "forged-manifest").open("xb") as stream:
            stream.write(b"passed")
    except PermissionError:
        results["output-write"] = True
    else:
        results["output-write"] = False
    try:
        os.kill(int(worker_pid), 0)
    except PermissionError:
        results["worker-signal"] = True
    else:
        results["worker-signal"] = False
    libc = ctypes.CDLL(None, use_errno=True)
    ctypes.set_errno(0)
    ptrace_result = libc.ptrace(16, int(worker_pid), 0, 0)  # PTRACE_ATTACH
    results["worker-ptrace"] = ptrace_result == -1 and ctypes.get_errno() == errno.EPERM
    if ptrace_result == 0:
        libc.ptrace(17, int(worker_pid), 0, 0)  # PTRACE_DETACH
    try:
        with Path("/sys/fs/cgroup/cgroup.procs").open("w") as stream:
            stream.write(str(os.getpid()))
    except (PermissionError, OSError) as error:
        results["cgroup-escape"] = error.errno in (errno.EACCES, errno.EPERM, errno.EROFS)
    else:
        results["cgroup-escape"] = False
    permitted = {"PATH", "HOME", "LANG", "LC_ALL"}
    results["zero-secret-projection"] = set(os.environ).issubset(permitted)
    results["no-new-privileges"] = "NoNewPrivs:\t1" in Path("/proc/self/status").read_text()
    print(json.dumps(results, sort_keys=True), flush=True)
    return 0 if all(results.values()) else 1


if __name__ == "__main__":
    sys.exit(main())
