"""Copy only fixed bounded private proof records to a canonical runner archive."""
import hashlib
import io
import json
import os
import stat
import sys
import tarfile
import time
from pathlib import Path

native = Path(sys.argv[1])
build = Path(sys.argv[2])
destination = Path(sys.argv[3])
uid, gid = int(sys.argv[4]), int(sys.argv[5])
deadline = time.monotonic() + 5
assert os.geteuid() == 0 and uid > 0 and gid > 0
fixed = {
    'build/supervision-receipt.json': (build / 'supervision-receipt.json', 128 * 1024, uid),
    'build/supervisor.stdout': (build / 'supervisor.stdout', 65536, uid),
    'build/supervisor.stderr': (build / 'supervisor.stderr', 65536, uid),
    'build/build-receipt.json': (build / 'build-receipt.json', 128 * 1024, uid),
    'native/receipt.json': (native / 'receipt.json', 128 * 1024, 0),
    'native/supervision-receipt.json': (native / 'supervision-receipt.json', 128 * 1024, 0),
    'native/coverage.cobertura.xml': (native / 'coverage.cobertura.xml', 1024 * 1024, 0),
    'native/coverage.json': (native / 'coverage.json', 1024 * 1024, 0),
    'native/host-stdout.log': (native / 'host-stdout.log', 65536, 0),
    'native/host-stderr.log': (native / 'host-stderr.log', 65536, 0),
    'native/worker-stdout.log': (native / 'worker-stdout.log', 65536, 0),
    'native/worker-stderr.log': (native / 'worker-stderr.log', 65536, 0),
}
contents = []
for name, (path, limit, owner) in sorted(fixed.items()):
    try:
        before = path.lstat()
    except FileNotFoundError:
        continue
    assert time.monotonic() < deadline
    assert stat.S_ISREG(before.st_mode) and before.st_uid == owner and before.st_nlink == 1
    assert stat.S_IMODE(before.st_mode) == 0o600 and before.st_size <= limit
    assert stat.S_ISDIR(path.parent.lstat().st_mode) and not path.parent.is_symlink()
    fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
    try:
        opened = os.fstat(fd)
        assert (opened.st_dev, opened.st_ino) == (before.st_dev, before.st_ino)
        data = bytearray()
        while len(data) <= limit:
            assert time.monotonic() < deadline
            block = os.read(fd, min(65536, limit + 1 - len(data)))
            if not block:
                break
            data.extend(block)
        after = os.fstat(fd)
        named = path.lstat()
        identity = lambda s: (s.st_dev, s.st_ino, s.st_size, s.st_mode, s.st_uid, s.st_gid, s.st_nlink, s.st_mtime_ns, s.st_ctime_ns)
        assert identity(before) == identity(opened) == identity(after) == identity(named)
        assert len(data) == before.st_size and len(data) <= limit
        contents.append((name, bytes(data)))
    finally:
        os.close(fd)
assert contents and sum(len(data) for _, data in contents) <= 3 * 1024 * 1024
with destination.open('xb') as stream:
    os.chmod(destination, 0o600)
    with tarfile.open(fileobj=stream, mode='w', format=tarfile.USTAR_FORMAT) as archive:
        for name, data in contents:
            info = tarfile.TarInfo(name)
            info.size = len(data)
            info.mode = 0o600
            info.uid = info.gid = info.mtime = 0
            info.uname = info.gname = ''
            archive.addfile(info, io.BytesIO(data))
assert destination.stat().st_size <= 4 * 1024 * 1024 and time.monotonic() < deadline
os.chown(destination, uid, gid)
print(json.dumps(dict(archive_bytes=destination.stat().st_size, member_count=len(contents),
                      archive_sha256=hashlib.sha256(destination.read_bytes()).hexdigest())))
