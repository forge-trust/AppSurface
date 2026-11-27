"""Resolve host tools and enforce offline inputs for the Keycloak replay."""

from __future__ import annotations

from dataclasses import dataclass
import hashlib
import json
import os
from pathlib import Path
import subprocess
import time


SDK_VERSION = '10.0.402'


def docker_endpoint(requested: str | None = None, recorded: str | None = None) -> str:
    """Select one qualified local socket and honor the endpoint in a newer receipt."""
    desktop = 'unix://' + str(Path.home() / '.docker/run/docker.sock')
    if requested is not None and recorded is not None and requested != recorded:
        raise ValueError('Docker endpoint differs from the run receipt')
    endpoint = recorded if recorded is not None else requested
    if endpoint is None:
        return desktop
    if endpoint not in (desktop, 'unix:///var/run/docker.sock'):
        raise ValueError('unqualified Docker endpoint')
    return endpoint


def dotnet_host(sdk: Path | None, recorded_sdk: str | None = None) -> Path:
    """Use the recorded SDK or an explicit SDK for a receipt that predates the pin."""
    selected = sdk.resolve(strict=True) if sdk is not None else None
    if recorded_sdk is not None:
        recorded = Path(recorded_sdk)
        if not recorded.is_absolute():
            raise ValueError('recorded SDK root must be absolute')
        recorded = recorded.resolve(strict=True)
        if selected is not None and selected != recorded:
            raise ValueError('SDK root differs from the preparation receipt')
        selected = recorded
    if selected is None:
        raise ValueError('pass --sdk when the preparation receipt lacks the SDK root')
    dotnet = selected / 'dotnet'
    if not dotnet.is_file() or not os.access(dotnet, os.X_OK):
        raise ValueError('SDK root does not contain an executable dotnet host')
    if not (selected / 'sdk' / SDK_VERSION).is_dir():
        raise ValueError(f'SDK root does not contain SDK {SDK_VERSION}')
    return dotnet


def verify_sdk_version(dotnet: Path, directory: Path) -> None:
    result = subprocess.run([dotnet, '--version'], cwd=directory, capture_output=True, text=True,
                            timeout=15, check=True)
    if result.stdout.strip() != SDK_VERSION:
        raise ValueError(f'replay SDK must be {SDK_VERSION}')


@dataclass(frozen=True)
class OfflineBuildStep:
    """One bounded preparation command and its exit status."""

    stage: str
    command: list[str]
    exit_code: int
    seconds: float


def run_offline_build(dotnet: Path, project: Path, *, cache_root: Path, logs: tuple[Path, Path],
                      publish_to: Path | None = None) -> list[OfflineBuildStep]:
    """Restore under private caches and build or publish without a second restore."""
    packages = cache_root / 'nuget'
    http_cache = cache_root / 'http-cache'
    scratch = cache_root / 'scratch'
    for directory in (packages, http_cache, scratch):
        directory.mkdir(mode=0o700, parents=True)
    config = project.with_name('NuGet.Config')
    config.write_text('<configuration><packageSources><clear /></packageSources>'
                      '<fallbackPackageFolders><clear /></fallbackPackageFolders></configuration>\n')
    env = dict(os.environ, DOTNET_ROOT=str(dotnet.parent), NUGET_PACKAGES=str(packages),
               NUGET_HTTP_CACHE_PATH=str(http_cache), NUGET_SCRATCH=str(scratch),
               DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1',
               MSBUILDDISABLENODEREUSE='1')
    stage = 'build' if publish_to is None else 'publish'
    build = [str(dotnet), stage, str(project), '--no-restore', '-c', 'Release',
             '-m:1', '-nr:false', '-p:UseSharedCompilation=false']
    if publish_to is not None:
        build.extend(['--output', str(publish_to)])
    commands = [
        ('restore', [str(dotnet), 'restore', str(project), '--configfile', str(config), '--packages', str(packages)]),
        (stage, build),
    ]
    outcomes = []
    for (stage, command), log_path in zip(commands, logs):
        started = time.monotonic()
        with log_path.open('x') as log:
            result = subprocess.run(command, cwd=project.parent, env=env, stdout=log,
                                    stderr=subprocess.STDOUT, timeout=120)
        outcomes.append(OfflineBuildStep(stage, command, result.returncode, time.monotonic() - started))
        if result.returncode:
            break
    return outcomes


def verify_offline_restore(assets: Path, packages: Path, *, exact_package_folder: bool = False) -> None:
    """Reject package dependencies or a populated private cache before recording proof."""
    restored = json.loads(assets.read_text())
    if restored['libraries']:
        raise ValueError('offline restore resolved package libraries')
    if exact_package_folder and set(restored['packageFolders']) != {str(packages)}:
        raise ValueError('offline restore recorded unexpected package folders')
    verify_empty_cache(packages)


def verify_empty_cache(packages: Path) -> None:
    if any(packages.iterdir()):
        raise ValueError('private NuGet cache is not empty')


def prepared_file_manifest(root: Path) -> dict[str, str]:
    """Hash the full regular-file inventory of a copied replay directory."""
    if root.is_symlink() or not root.is_dir():
        raise ValueError('prepared closure must be a directory')
    manifest = {}
    for member in sorted(root.rglob('*')):
        if member.is_symlink():
            raise ValueError('prepared closure contains a symbolic link')
        if member.is_dir():
            continue
        if not member.is_file():
            raise ValueError('prepared closure contains a non-regular file')
        digest = hashlib.sha256()
        with member.open('rb') as source:
            for block in iter(lambda: source.read(1024 * 1024), b''):
                digest.update(block)
        manifest[member.relative_to(root).as_posix()] = digest.hexdigest()
    return manifest


def verify_prepared_closure(root: Path, manifest: dict[str, str]) -> None:
    """Require an exact inventory and hashes before and after running a fixture."""
    if prepared_file_manifest(root) != manifest:
        raise ValueError('prepared closure changed')
