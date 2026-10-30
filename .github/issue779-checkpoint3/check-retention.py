"""Bound the seven-module C3 measurement artifact; product quotas and C2 retention are unchanged."""
from pathlib import Path
import hashlib
import stat
import sys


_DIAGNOSTIC_LEAVES = {
    "native-supervision.json", "native-sdk.json", "native-supervision.cobertura.xml",
    "source.json", "measurement-binding.json", "root-file-tests/root-files.trx", "root-supplement.trx",
    "root-supplement-driver.log", "retention-supplement-controls.log", "application-bundle.trx",
    "application-bundle-driver.log", "application-bundle-controls.log", "peer-credentials.trx",
    "instrumentation.json", "original-supervision.json", "registration-digests.json",
    "native-receipt.json", "native-sdk-failure.json", "sdk-test-failure.json",
}


def _reject(path: Path, reason: str, root: Path) -> None:
    """Emit a bounded retention failure without exposing arbitrary path text."""
    try:
        relative = path.relative_to(root).as_posix()
    except ValueError:
        relative = path.name
    safe_name = relative if relative in _DIAGNOSTIC_LEAVES else (
        "leaf-" + hashlib.sha256(relative.encode("utf-8", "surrogatepass")).hexdigest()[:12]
    )
    print(f"C3_RETENTION_REJECT reason={reason} leaf={safe_name}", file=sys.stderr)
    raise SystemExit(1)


def check(root: Path) -> None:
    if not root.is_dir() or root.is_symlink():
        _reject(root, "root-type", root)
    total = count = 0
    for path in root.rglob('*'):
        metadata = path.lstat()
        if stat.S_ISLNK(metadata.st_mode):
            _reject(path, "symlink", root)
        if path.is_dir():
            path.chmod(0o700)
            continue
        maximum = {
            'native-supervision.json': 32,
            'native-sdk.json': 32,
            'native-supervision.cobertura.xml': 40,
        }.get(path.name, 8) * 1024 * 1024
        if not stat.S_ISREG(metadata.st_mode):
            _reject(path, "not-regular", root)
        if metadata.st_nlink != 1:
            _reject(path, "link-count", root)
        if metadata.st_size > maximum:
            _reject(path, "size-cap", root)
        total += metadata.st_size
        count += 1
        path.chmod(0o600)
    # Three separately bounded files retain the supplementary root TRX, collector log,
    # and its portable retention-control log. The distinct peer-credential TRX adds one fixed leaf.
    # Three additional fixed leaves retain the application-bundle TRX, collector log and parser-control log.
    # Product limits, per-file limits and the total retained byte budget stay unchanged.
    if count > 135:
        _reject(root, "file-count-cap", root)
    if total > 96 * 1024 * 1024:
        _reject(root, "total-size-cap", root)
    root.chmod(0o700)


if __name__ == "__main__":
    check(Path(sys.argv[1]))
