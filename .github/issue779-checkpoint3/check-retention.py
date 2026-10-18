"""Bound the six-module C3 measurement artifact; product quotas and C2 retention are unchanged."""
from pathlib import Path
import stat
import sys

root = Path(sys.argv[1])
assert root.is_dir() and not root.is_symlink()
total = count = 0
for path in root.rglob('*'):
    metadata = path.lstat()
    assert not stat.S_ISLNK(metadata.st_mode)
    if path.is_dir():
        path.chmod(0o700)
        continue
    maximum = {'native-supervision.json': 32, 'native-supervision.cobertura.xml': 40}.get(path.name, 8) * 1024 * 1024
    assert stat.S_ISREG(metadata.st_mode) and metadata.st_nlink == 1 and metadata.st_size <= maximum
    total += metadata.st_size
    count += 1
    path.chmod(0o600)
assert count <= 128 and total <= 96 * 1024 * 1024
root.chmod(0o700)
