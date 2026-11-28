#!/usr/bin/env python3
"""Pack the real Keycloak adapter and verify both single-file AppHost publication layouts."""

import argparse
import hashlib
import os
from pathlib import Path
import platform
import shutil
import subprocess
import tempfile
from zipfile import ZipFile

from replay_environment import (prepare_offline_nuget, prepared_file_manifest, verify_offline_restore,
                                verify_sdk_version)
from worker_payload import PURE_LIBRARY, WORKER_PREFIX, read_worker_payload, worker_payload_target


ADAPTER = 'ForgeTrust.AppSurface.Auth.Aspire.Keycloak'
CORE = 'ForgeTrust.AppSurface.Core'
WORKER = f'{ADAPTER}.RealmReadyWorker'
WORKER_DIRECTORY = 'appsurface-keycloak-realm-ready'
FIXTURE = Path(__file__).resolve().with_name('packed-worker-single-file')


def stage_consumer(archive: Path, core: Path, consumer: Path) -> dict[str, str]:
    """Stage the shipped publish target and its recursive worker payload for the offline probe."""
    consumer.mkdir(parents=True)
    shutil.copyfile(FIXTURE / 'Probe.csproj.template', consumer / 'Probe.csproj')
    shutil.copyfile(FIXTURE / 'Program.cs', consumer / 'Program.cs')
    references = consumer / 'references'
    references.mkdir()
    adapter = consumer / 'adapter'
    targets = adapter / 'buildTransitive' / f'{ADAPTER}.targets'
    targets.parent.mkdir(parents=True)
    payload = read_worker_payload(archive)
    with ZipFile(archive) as package:
        targets.write_bytes(package.read(f'buildTransitive/{ADAPTER}.targets'))
        (references / f'{ADAPTER}.dll').write_bytes(package.read(f'lib/net10.0/{ADAPTER}.dll'))
    (references / PURE_LIBRARY).write_bytes(payload[PURE_LIBRARY])
    shutil.copyfile(core, references / core.name)
    worker_root = adapter / WORKER_PREFIX
    worker_root.mkdir(parents=True)
    for name, contents in payload.items():
        path = worker_payload_target(worker_root, name)
        path.parent.mkdir(exist_ok=True, parents=True)
        path.write_bytes(contents)
    return {name: hashlib.sha256(contents).hexdigest() for name, contents in payload.items()}


def prove_muxer_rejects_worker(dotnet: Path, env: dict[str, str], consumer: Path,
                              root: Path, rid: str) -> None:
    """A real muxer must ignore a complete worker placed beside its private installation."""
    managed = root / 'muxer-control' / 'managed'
    subprocess.run([dotnet, 'build', '-c', 'Release', '--no-restore', '-o', managed,
                    f'-p:RuntimeIdentifier={rid}', '-p:PublishSingleFile=false', '-p:UseAppHost=false', '-m:1', '-nr:false'],
                   cwd=consumer, env=env, check=True, timeout=60)
    muxer = managed.parent / 'install'
    muxer.mkdir()
    for name in ('host', 'shared'):
        (muxer / name).symlink_to(dotnet.parent / name, target_is_directory=True)
    host = muxer / dotnet.name
    shutil.copy2(dotnet, host)
    (managed / WORKER_DIRECTORY).rename(muxer / WORKER_DIRECTORY)
    subprocess.run([host, managed / f'{ADAPTER}.Tests.dll', '--expect-muxer-rejection'],
                   cwd=root, env=env, check=True, timeout=15)


def prove_publish(dotnet: Path, rid: str, archive: Path, core: Path,
                  root: Path, *, self_extract: bool) -> None:
    """Restore offline, publish one layout, and exercise the real adapter's success and failure paths."""
    layout = 'self-extract' if self_extract else 'bundled'
    consumer = root / 'consumer'
    expected = stage_consumer(archive, core, consumer)
    packages = root / 'packages'
    config, private_env = prepare_offline_nuget(dotnet, consumer, packages)
    private_env['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] = str(root / 'extracted')
    publish_settings = [f'-p:RuntimeIdentifier={rid}', f'-p:IncludeAllContentForSelfExtract={str(self_extract).lower()}',
                        '-m:1', '-nr:false']
    subprocess.run([dotnet, 'restore', '--configfile', str(config), *publish_settings],
                   cwd=consumer, env=private_env, check=True, timeout=60)
    verify_offline_restore(consumer / 'obj/project.assets.json', packages, exact_package_folder=True)
    published = root / 'publish'
    subprocess.run([dotnet, 'publish', '-c', 'Release', '--no-restore', '-o', published, *publish_settings],
                   cwd=consumer, env=private_env, check=True, timeout=60)
    external = published / WORKER_DIRECTORY
    if (published / f'{ADAPTER}.dll').exists() or prepared_file_manifest(external) != expected:
        raise ValueError(f'{layout}: expected a bundled adapter and the exact external worker payload')
    print(f'{layout}: all {len(expected)} external files match the packed worker.', flush=True)

    cwd = root / 'unrelated-cwd'
    decoy = cwd / WORKER_DIRECTORY
    decoy.mkdir(parents=True)
    for extension in ('dll', 'deps.json', 'runtimeconfig.json'):
        (decoy / f'{WORKER}.{extension}').write_text('wrong working directory')
    executable = published / f'{ADAPTER}.Tests'
    subprocess.run([executable, layout], cwd=cwd, env=private_env, check=True, timeout=15)
    if self_extract:
        executable = executable.rename(published / dotnet.name)
        subprocess.run([executable, layout], cwd=cwd, env=private_env, check=True, timeout=15)
        print(f'{layout}: lookup also succeeded with the AppHost named {executable.name}.', flush=True)
    runtimeconfig = external / f'{WORKER}.runtimeconfig.json'
    relocated = root / runtimeconfig.name
    runtimeconfig.rename(relocated)
    try:
        subprocess.run([executable, layout, '--expect-missing-runtimeconfig'],
                       cwd=cwd, env=private_env, check=True, timeout=15)
    finally:
        relocated.rename(runtimeconfig)
    if not self_extract:
        prove_muxer_rejects_worker(dotnet, private_env, consumer, root, rid)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dotnet', type=Path, required=True, help='Absolute path to the pinned SDK host')
    dotnet = parser.parse_args().dotnet.resolve(strict=True)
    repo = FIXTURE.parents[2]
    verify_sdk_version(dotnet, repo)
    platform_id = (platform.system(), platform.machine())
    rid = {('Darwin', 'arm64'): 'osx-arm64', ('Linux', 'x86_64'): 'linux-x64',
           ('Linux', 'aarch64'): 'linux-arm64'}.get(platform_id)
    if rid is None:
        raise ValueError(f'No packed single-file smoke runtime for {platform_id}')
    env = dict(os.environ, DOTNET_ROOT=str(dotnet.parent), DOTNET_CLI_TELEMETRY_OPTOUT='1')
    with tempfile.TemporaryDirectory(prefix='i888-packed-single-file-') as temporary:
        root = Path(temporary).resolve()
        artifacts = root / 'artifacts'
        feed = root / 'feed'
        project = repo / 'Auth' / ADAPTER / f'{ADAPTER}.csproj'
        pack_properties = ['-p:Configuration=Release', f'-p:ArtifactsPath={artifacts}',
                           '-p:PackageVersion=0.0.0-packed-worker-smoke.1',
                           '-p:UseSharedCompilation=false', '-m:1', '-nr:false']
        subprocess.run([dotnet, 'pack', project, '-o', feed, *pack_properties],
                       cwd=repo, env=env, check=True, timeout=240)
        archive, = feed.glob(f'{ADAPTER}.*.nupkg')
        target = subprocess.run([dotnet, 'msbuild', project, '-nologo', '-getProperty:TargetDir', *pack_properties],
                                cwd=repo, env=env, check=True, capture_output=True, text=True, timeout=30)
        core = (Path(target.stdout.strip()) / f'{CORE}.dll').resolve(strict=True)
        if not core.is_relative_to(artifacts):
            raise ValueError('The evaluated adapter output must stay inside the isolated pack artifacts')
        print(f'Using Core from the adapter TargetDir: {core}', flush=True)
        prove_publish(dotnet, rid, archive, core, root / 'bundled', self_extract=False)
        prove_publish(dotnet, rid, archive, core, root / 'self-extract', self_extract=True)


if __name__ == '__main__':
    main()
