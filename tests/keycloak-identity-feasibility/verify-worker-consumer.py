#!/usr/bin/env python3
"""Verify in-place worker package upgrades and SDK Clean in an offline NuGet consumer."""

import argparse
import hashlib
from pathlib import Path
import subprocess
import tempfile
from zipfile import ZipFile, ZipInfo

from replay_environment import (prepare_offline_nuget, prepared_file_manifest, verify_offline_restore,
                                verify_sdk_version)
from worker_payload import WORKER_PREFIX


PACKAGE = 'ForgeTrust.AppSurface.Auth.Aspire.Keycloak'
WORKER = PACKAGE + '.RealmReadyWorker'
OUTPUT_DIRECTORY = 'appsurface-keycloak-realm-ready'
FIXTURE_ARCHIVE_TIME = (2024, 1, 1, 0, 0, 0)


def write_package(feed: Path, version: str, targets: bytes, files: dict[str, bytes]) -> None:
    """Put a fixture worker release in the local NuGet feed with the real package target."""
    with ZipFile(feed / f'{PACKAGE}.{version}.nupkg', 'w') as archive:
        archive.writestr(ZipInfo(f'{PACKAGE}.nuspec', FIXTURE_ARCHIVE_TIME),
                         f'<package><metadata><id>{PACKAGE}</id><version>{version}</version>'
                         '<authors>AppSurface</authors><description>Worker output fixture</description>'
                         '</metadata></package>')
        archive.writestr(ZipInfo(f'buildTransitive/{PACKAGE}.targets', FIXTURE_ARCHIVE_TIME), targets)
        for name, contents in files.items():
            archive.writestr(ZipInfo(WORKER_PREFIX + name, FIXTURE_ARCHIVE_TIME), contents)


def verify_worker_files(output: Path, files: dict[str, bytes]) -> None:
    """Require every expected file and SHA-256 without accepting extra files."""
    expected = {name: hashlib.sha256(contents).hexdigest() for name, contents in files.items()}
    if prepared_file_manifest(output) != expected:
        raise ValueError(f'Worker output does not match the current package: {output}')


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dotnet', type=Path, required=True, help='Absolute path to the pinned SDK host')
    dotnet = parser.parse_args().dotnet.resolve(strict=True)
    repo = Path(__file__).resolve().parents[2]
    targets = (repo / 'Auth' / PACKAGE / 'buildTransitive' / f'{PACKAGE}.targets').read_bytes()
    releases = {
        '1.0.0': {f'{WORKER}.dll': b'old worker', f'{WORKER}.deps.json': b'{}',
                  f'{WORKER}.runtimeconfig.json': b'{}', 'runtimes/shared.dll': b'old dependency',
                  'runtimes/obsolete.dll': b'old version only'},
        '2.0.0': {f'{WORKER}.dll': b'new worker', f'{WORKER}.deps.json': b'{}',
                  f'{WORKER}.runtimeconfig.json': b'{}', 'runtimes/shared.dll': b'new dependency',
                  'runtimes/added.dll': b'new version only'},
    }
    replacement_files = (f'{WORKER}.dll', 'runtimes/shared.dll')
    for name in replacement_files:
        previous = releases['1.0.0'].get(name)
        current = releases['2.0.0'].get(name)
        if previous is None or current is None or previous == current or len(previous) != len(current):
            raise ValueError(f'Fixture must replace {name} with different bytes of the same length')

    with tempfile.TemporaryDirectory(prefix='i888-worker-consumer-') as temporary:
        root = Path(temporary).resolve()
        feed = root / 'feed'
        feed.mkdir()
        consumer = root / 'consumer'
        consumer.mkdir()
        verify_sdk_version(dotnet, consumer)
        packages = root / 'packages'
        config, env = prepare_offline_nuget(dotnet, consumer, packages, feed=feed)

        def run(*args: str) -> None:
            result = subprocess.run([dotnet, *args, '-m:1', '-nr:false'], cwd=consumer, env=env,
                                    stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, timeout=60)
            if result.returncode:
                raise RuntimeError(result.stdout)

        project = consumer / 'Consumer.csproj'
        (consumer / 'Consumer.cs').write_text('public sealed class ConsumerFixture {}')
        output = consumer / 'bin' / 'Release' / 'net10.0'
        built = output / OUTPUT_DIRECTORY
        published = output / 'publish' / OUTPUT_DIRECTORY
        unrelated = output / 'consumer-owned.txt'
        unrelated_published = published.parent / unrelated.name
        original_timestamps = {}

        for version, files in releases.items():
            write_package(feed, version, targets, files)
            project.write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework>'
                               '<UseSharedCompilation>false</UseSharedCompilation></PropertyGroup><ItemGroup>'
                               f'<PackageReference Include="{PACKAGE}" Version="[{version}]" />'
                               '</ItemGroup></Project>')
            run('restore', '--configfile', str(config))
            verify_offline_restore(consumer / 'obj/project.assets.json', packages,
                                   exact_package_folder=True, feed=feed)
            extracted = packages / PACKAGE.lower() / version / WORKER_PREFIX
            timestamps = {name: (extracted / name).stat().st_mtime_ns for name in replacement_files}
            if version == '1.0.0':
                original_timestamps = timestamps
            elif timestamps != original_timestamps:
                raise ValueError('NuGet did not extract the replacement files with identical timestamps')
            run('build', '-c', 'Release', '--no-restore')
            verify_worker_files(built, files)
            run('publish', '-c', 'Release', '--no-build', '--no-restore')
            verify_worker_files(published, files)
            print(f'{version} built and published the exact worker payload from the offline feed.', flush=True)
            if version == '1.0.0':
                # Previous package targets did not register their copied output with MSBuild.
                (built / 'legacy-untracked.dll').write_bytes(b'left by a previous package')
                unrelated.write_bytes(b'consumer-owned sibling')
                unrelated_published.write_bytes(b'consumer-owned published sibling')
            else:
                if unrelated.read_bytes() != b'consumer-owned sibling':
                    raise ValueError('The upgrade changed a consumer-owned output')
                if unrelated_published.read_bytes() != b'consumer-owned published sibling':
                    raise ValueError('The upgrade changed a consumer-owned publish output')

        print('NuGet extracted both versions of the changed worker files with identical timestamps.')
        run('clean', '-c', 'Release')
        if built.exists():
            verify_worker_files(built, {})
        if unrelated.read_bytes() != b'consumer-owned sibling':
            raise ValueError('SDK Clean changed a consumer-owned output')
        print('SDK Clean removed the copied worker files and preserved the consumer-owned sibling.')


if __name__ == '__main__':
    main()
