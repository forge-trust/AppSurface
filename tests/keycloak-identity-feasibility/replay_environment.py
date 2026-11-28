"""Resolve host tools and enforce offline inputs for the Keycloak replay."""

from __future__ import annotations

from dataclasses import dataclass
import hashlib
import json
import os
from pathlib import Path
import subprocess
import time
from xml.sax.saxutils import quoteattr


SDK_VERSION = '10.0.402'
KEYCLOAK_FIXTURE_REPOSITORY = 'quay.io/keycloak/keycloak'
KEYCLOAK_FIXTURE_RELEASE = '26.7.2'
KEYCLOAK_FIXTURE_DIGEST = 'sha256:831330513f55695572286e521f94fcd3c7e285250ed5b848090265a33192f669'


def keycloak_fixture_image(*, tagged: bool) -> str:
    """Build tagged and digest-only references from the independent fixture release pin."""
    release = ':' + KEYCLOAK_FIXTURE_RELEASE if tagged else ''
    return f'{KEYCLOAK_FIXTURE_REPOSITORY}{release}@{KEYCLOAK_FIXTURE_DIGEST}'


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
    """One completed SDK command and its real exit status."""

    stage: str
    command: list[str]
    exit_code: int
    seconds: float


@dataclass(frozen=True)
class OfflineBuildResult:
    """Completed SDK commands and the verification stage that rejected the result, if any."""

    commands: tuple[OfflineBuildStep, ...]
    verification_failure: str | None = None

    @property
    def failed_stage(self) -> str | None:
        """Return the stage that stopped preparation, or None after a verified build or publish."""
        if self.verification_failure is not None:
            return self.verification_failure
        final_command = self.commands[-1]
        return final_command.stage if final_command.exit_code else None


def prepare_offline_nuget(dotnet: Path, directory: Path, packages: Path, *,
                          feed: Path | None = None) -> tuple[Path, dict[str, str]]:
    """Write a restore config and create private caches for an isolated SDK consumer.

    The config clears all sources and fallbacks. An optional local feed is the only source added.
    Use the returned config with --configfile, then verify the effective sources from the assets file.
    """
    http_cache = packages.parent / 'http-cache'
    scratch = packages.parent / 'scratch'
    for cache in (packages, http_cache, scratch):
        cache.mkdir(mode=0o700, parents=True)
    source = '' if feed is None else f'<add key="fixture" value={quoteattr(str(feed.resolve(strict=True)))} />'
    config = directory / 'NuGet.Config'
    config.write_text(f'<configuration><packageSources><clear />{source}</packageSources>'
                      '<fallbackPackageFolders><clear /></fallbackPackageFolders></configuration>\n')

    # MSBuild reads environment variables as properties, which can override the isolated config.
    restore_overrides = {'restoresources', 'restoreadditionalprojectsources', 'restorefallbackfolders',
                         'restoreadditionalprojectfallbackfolders', 'restoreconfigfile', 'restorepackagespath'}
    env = {key: value for key, value in os.environ.items() if key.casefold() not in restore_overrides}
    env.update(DOTNET_ROOT=str(dotnet.parent), NUGET_PACKAGES=str(packages),
               NUGET_HTTP_CACHE_PATH=str(http_cache), NUGET_SCRATCH=str(scratch),
               DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1',
               MSBUILDDISABLENODEREUSE='1')
    return config, env


def run_offline_build(dotnet: Path, project: Path, *, cache_root: Path, logs: tuple[Path, Path],
                      publish_to: Path | None = None, exact_package_folder: bool = False) -> OfflineBuildResult:
    """Verify private restore assets before building or publishing, then verify the final state.

    Require the restore log and the build or publish log before preparing the private caches.
    SDK exit codes are recorded as returned. A verification rejection stops preparation, records
    its stage separately in the result, and saves the reason in the preceding command's log.
    """
    if len(logs) != 2 or any(not isinstance(path, Path) for path in logs):
        raise ValueError('provide both the restore log path and the build or publish log path')
    restore_log, output_log = logs
    packages = cache_root / 'nuget'
    config, env = prepare_offline_nuget(dotnet, project.parent, packages)
    assets = project.parent / 'obj/project.assets.json'
    stage = 'build' if publish_to is None else 'publish'
    build = [str(dotnet), stage, str(project), '--no-restore', '-c', 'Release',
             '-m:1', '-nr:false', '-p:UseSharedCompilation=false']
    if publish_to is not None:
        build.extend(['--output', str(publish_to)])
    restore = [str(dotnet), 'restore', str(project), '--configfile', str(config), '--packages', str(packages)]
    commands = [('restore', restore, restore_log), (stage, build, output_log)]
    outcomes = []
    for stage, command, log_path in commands:
        started = time.monotonic()
        with log_path.open('x') as log:
            result = subprocess.run(command, cwd=project.parent, env=env, stdout=log,
                                    stderr=subprocess.STDOUT, timeout=120)
            outcomes.append(OfflineBuildStep(stage, command, result.returncode, time.monotonic() - started))
            if result.returncode:
                break
            try:
                verify_offline_restore(assets, packages, exact_package_folder=exact_package_folder)
            except (OSError, ValueError) as error:
                verification = f'verify-{stage}'
                log.write(f'\n{verification} failed: {error}\n')
                return OfflineBuildResult(tuple(outcomes), verification_failure=verification)
    return OfflineBuildResult(tuple(outcomes))


def verify_offline_restore(assets: Path, packages: Path, *, exact_package_folder: bool = False,
                           feed: Path | None = None) -> None:
    """Require the approved source set and optionally the exact private package folder.

    With no feed, restore may resolve no package libraries and the package cache must remain empty.
    With a feed, packages are allowed and the assets must name that local feed as their only source.
    """
    restored = json.loads(assets.read_text())
    project = restored.get('project') if isinstance(restored, dict) else None
    restore = project.get('restore') if isinstance(project, dict) else None
    if not isinstance(restore, dict):
        raise ValueError('offline restore metadata is missing')
    sources = restore.get('sources', {})
    expected_sources = set() if feed is None else {str(feed.resolve(strict=True))}
    if not isinstance(sources, dict) or set(sources) != expected_sources:
        raise ValueError('offline restore recorded unexpected package sources')
    if feed is None and restored.get('libraries') != {}:
        raise ValueError('offline restore resolved package libraries')
    if exact_package_folder:
        folders = restored.get('packageFolders')
        if not isinstance(folders, dict) or set(folders) != {str(packages)}:
            raise ValueError('offline restore recorded unexpected package folders')
    if feed is None:
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
