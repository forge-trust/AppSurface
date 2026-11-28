"""Load the exact recursive worker payload from a candidate adapter archive."""

from __future__ import annotations

from pathlib import Path, PurePosixPath
from zipfile import ZipFile


WORKER_PREFIX = 'tools/net10.0/any/realm-ready/'
PURE_LIBRARY = 'ForgeTrust.AppSurface.Auth.Keycloak.dll'


def read_worker_payload(archive: Path) -> dict[str, bytes]:
    """Read file members using canonical ZIP paths and unique relative names."""
    payload = {}
    with ZipFile(archive) as package:
        for member in package.infolist():
            if not member.filename.startswith(WORKER_PREFIX) or member.is_dir():
                continue
            name = member.filename.removeprefix(WORKER_PREFIX)
            relative = PurePosixPath(name)
            if relative.is_absolute() or any(part in ('', '.', '..') for part in name.split('/')) or '\\' in name:
                raise ValueError('unsafe package entry')
            if name in payload:
                raise ValueError('duplicate worker payload entry')
            payload[name] = package.read(member)
    if not payload:
        raise ValueError('candidate archive has no worker payload')
    return payload


def worker_payload_target(root: Path, member: str | PurePosixPath) -> Path:
    """Resolve a worker file inside its staging root, including existing parent links."""
    relative = Path(member)
    if not relative.parts or relative.is_absolute() or '..' in relative.parts:
        raise ValueError('unsafe package entry')
    target = (root / relative).resolve()
    if target == root or not target.is_relative_to(root):
        raise ValueError('unsafe package entry')
    return target
