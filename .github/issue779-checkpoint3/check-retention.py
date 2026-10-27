"""Bound the seven-module C3 measurement artifact; product quotas and C2 retention are unchanged."""
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
    maximum = {'native-supervision.json': 32, 'native-sdk.json': 32, 'native-supervision.cobertura.xml': 40}.get(path.name, 8) * 1024 * 1024
    assert stat.S_ISREG(metadata.st_mode) and metadata.st_nlink == 1 and metadata.st_size <= maximum
    total += metadata.st_size
    count += 1
    path.chmod(0o600)
# Three separately bounded files retain the supplementary root TRX, collector log,
# and its portable retention-control log.
# Three additional fixed leaves retain the application-bundle TRX, collector log and parser-control log.
# Product limits, per-file limits and the total retained byte budget stay unchanged.
assert count <= 134 and total <= 96 * 1024 * 1024
root.chmod(0o700)
