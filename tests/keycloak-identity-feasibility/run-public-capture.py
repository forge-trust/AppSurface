#!/usr/bin/env python3
"""Prove the public CaptureAsync boundary using an extracted payload and offline frozen inputs."""
import argparse
import base64
import fcntl
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import time
import uuid
import zipfile

SDK = 'mcr.microsoft.com/dotnet/sdk@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b'
WORKER = 'ForgeTrust.AppSurface.Auth.Aspire.Keycloak.RealmReadyWorker'
PURE = 'ForgeTrust.AppSurface.Auth.Keycloak'
PROBE = 'Issue888.PublicCaptureConsumer'


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def stage_frozen_fixtures(source, destination):
    shutil.copytree(source, destination, ignore=shutil.ignore_patterns('blobs-base64'))
    for variant in ('valid', 'invalid-signature', 'invalid-zstd'):
        decoded = destination / variant / 'blobs'
        decoded.mkdir()
        for entry in sorted((source / variant / 'blobs-base64').iterdir()):
            if entry.is_symlink() or not entry.is_file() or entry.suffix != '.b64':
                raise ValueError('Unexpected frozen fixture entry')
            data = base64.b64decode(entry.read_bytes().strip(), validate=True)
            if hashlib.sha256(data).hexdigest() != entry.stem:
                raise ValueError('Frozen fixture digest mismatch')
            with (decoded / entry.stem).open('xb') as output:
                output.write(data)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--package', type=Path, required=True)
    parser.add_argument('--evidence-root', type=Path, required=True)
    parser.add_argument('--phase', choices=['pilot', 'all'], default='all')
    args = parser.parse_args()
    os.umask(0o077)
    repository = Path(__file__).resolve().parents[2]
    args.evidence_root.mkdir(parents=True, exist_ok=True)
    evidence = Path(tempfile.mkdtemp(prefix='public-capture-', dir=args.evidence_root)).resolve()
    isolated = Path(tempfile.mkdtemp(prefix='issue888-public-capture-', dir='/private/tmp')).resolve()
    owner = (evidence / 'owner.lock').open('x+b')
    fcntl.flock(owner, fcntl.LOCK_EX | fcntl.LOCK_NB)
    active = evidence / 'active.json'
    docker = ['docker', '--host', 'unix:///Users/andrew/.docker/run/docker.sock']
    containers, checks, cases = [], [], []
    run_id = uuid.uuid4().hex
    fault, cleaned, shutdown = None, False, None
    daemon, package_hash, manifest = None, None, {}
    sources = ['tests/keycloak-identity-feasibility/run-public-capture.py',
               'tests/keycloak-identity-feasibility/public-capture/PublicCaptureProbe.cs']
    source_hashes = {path: digest(repository / path) for path in sources}
    fixture_source = repository / 'tests/keycloak-identity-feasibility/public-capture/fixtures'
    fixture_hashes, fixture_storage_hashes = {}, {}
    nuget = isolated / 'empty-nuget'
    nuget.mkdir()

    def check(name, value):
        checks.append({'name': name, 'passed': bool(value)})
        if not value:
            raise AssertionError(name)

    def command(arguments, timeout=15):
        result = subprocess.run(arguments, capture_output=True, text=True, timeout=timeout, stdin=subprocess.DEVNULL)
        if result.returncode:
            raise RuntimeError('tool exit ' + str(result.returncode) + ': ' + str(arguments[:3]))
        return result.stdout.strip()

    def marker():
        staged = active.with_suffix('.staged')
        with staged.open('w') as output:
            json.dump({'schema': 1, 'runId': run_id, 'daemon': daemon, 'containers': containers}, output)
            output.flush()
            os.fsync(output.fileno())
        os.replace(staged, active)

    def create(role, arguments, mounts, env=None):
        record = {'role': role, 'name': 'appsurface-issue888-public-' + run_id + '-' + role, 'id': None}
        containers.append(record)
        marker()
        variables = {'HOME': '/tmp/home', 'DOTNET_CLI_HOME': '/tmp/cli', 'NUGET_PACKAGES': '/nuget',
                     'DOTNET_NOLOGO': '1', 'DOTNET_SKIP_FIRST_TIME_EXPERIENCE': '1',
                     'DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE': 'true', 'DOTNET_MULTILEVEL_LOOKUP': '0',
                     'DOTNET_SHARED_STORE': '/no-shared-store'}
        variables.update(env or {})
        arguments_create = docker + ['create', '--name', record['name'], '--init', '--read-only',
                                    '--cap-drop', 'ALL', '--security-opt', 'no-new-privileges', '--ulimit', 'core=0',
                                    '--user', str(os.getuid()) + ':' + str(os.getgid()), '--network', 'none',
                                    '--label', 'appsurface.feasibility=issue888-public-capture',
                                    '--label', 'appsurface.run=' + run_id,
                                    '--tmpfs', '/tmp:rw,nosuid,mode=1777,size=256m']
        all_mounts = [(nuget, '/nuget', False), *mounts]
        for source, destination, writable in all_mounts:
            source = source.resolve()
            check(role + ':mount-owned-outside-checkout', source.is_relative_to(isolated) and not source.is_relative_to(repository))
            arguments_create += ['--mount', 'type=bind,source=' + str(source) + ',target=' + destination + ('' if writable else ',readonly')]
        for name, value in variables.items():
            arguments_create += ['--env', name + '=' + value]
        launch = ['-c', 'mkdir -p "$HOME" "$DOTNET_CLI_HOME"; exec dotnet "$@"', 'public-capture', *arguments]
        record['id'] = command(arguments_create + ['--entrypoint', '/bin/sh', SDK, *launch])
        marker()
        inspected = json.loads(command(docker + ['inspect', record['id']]))[0]
        check(role + ':exact-owner', inspected['Id'] == record['id'] and inspected['Name'] == '/' + record['name']
              and inspected['Config']['Labels']['appsurface.run'] == run_id)
        check(role + ':no-volumes', not inspected['Config'].get('Volumes') and not any(m['Type'] == 'volume' for m in inspected['Mounts']))
        check(role + ':isolation', inspected['HostConfig']['ReadonlyRootfs'] and inspected['HostConfig']['Init']
              and inspected['HostConfig']['NetworkMode'] == 'none' and inspected['HostConfig']['RestartPolicy']['Name'] == 'no')
        check(role + ':mount-inventory', all(Path(m['Source']).is_relative_to(isolated) for m in inspected['Mounts'] if m['Type'] == 'bind'))
        (evidence / (role + '-container.json')).write_text(json.dumps({
            'id': record['id'], 'name': record['name'], 'mounts': inspected['Mounts'],
            'network': inspected['HostConfig']['NetworkMode'], 'init': inspected['HostConfig']['Init'],
            'readOnlyRoot': inspected['HostConfig']['ReadonlyRootfs'], 'environment': inspected['Config']['Env']}, indent=2) + '\n')
        record['started'] = time.monotonic()
        command(docker + ['start', record['id']])
        return record

    def wait(record, expected, limit=25):
        started, code = record['started'], None
        while time.monotonic() - started < limit:
            remaining = limit - (time.monotonic() - started)
            state = json.loads(command(docker + ['inspect', record['id']], timeout=max(.01, remaining)))[0]['State']
            if not state['Running']:
                code = state['ExitCode']
                break
            time.sleep(.08)
        logged = subprocess.run(docker + ['logs', record['id']], capture_output=True, text=True, timeout=3)
        check(record['role'] + ':logs', logged.returncode == 0)
        logs = logged.stdout + logged.stderr
        (evidence / (record['role'] + '.log')).write_text(logs)
        check(record['role'] + ':terminal-under-bound', code is not None and time.monotonic() - started < 30)
        check(record['role'] + ':exit', code == expected if expected is not None else code != 0)
        cases.append({'case': record['role'], 'exit': code, 'elapsedSeconds': round(time.monotonic() - started, 6)})
        print(json.dumps(cases[-1]), flush=True)
        return logs

    def capture(role, scenario, variant='valid', removed=None, expected=20, code=None):
        work = isolated / ('case-' + role)
        work.mkdir()
        payload = isolated / ('payload-' + role)
        shutil.copytree(tool, payload)
        shutil.copyfile(build / 'out' / (PROBE + '.dll'), payload / (PROBE + '.dll'))
        if removed:
            (payload / (removed + '.dll')).unlink()
        relative_manifest = {p.relative_to(payload).as_posix(): digest(p) for p in payload.rglob('*') if p.is_file() and p.name != PROBE + '.dll'}
        expected_manifest = {path: value for path, value in manifest.items() if path != (removed + '.dll' if removed else '')}
        check(role + ':only-expected-payload-change', relative_manifest == expected_manifest)
        check(role + ':fresh-output', not (work / 'result.json').exists())
        entry = ['exec', '--runtimeconfig', '/proof/payload/' + WORKER + '.runtimeconfig.json',
                 '--depsfile', '/proof/payload/' + WORKER + '.deps.json', '/proof/payload/' + PROBE + '.dll',
                 '/proof/fixture', '/proof/work', scenario]
        record = create(role, entry, [(payload, '/proof/payload', False),
                                     (fixtures / variant, '/proof/fixture', False), (work, '/proof/work', True)],
                        {'DOTNET_HOST_TRACE': '1', 'DOTNET_HOST_TRACEFILE': '/proof/work/host.trace', 'DOTNET_HOST_TRACE_VERBOSITY': '4'})
        if scenario == 'signal-cancel':
            deadline = time.monotonic() + 10
            while not (work / 'capture-started').exists():
                if time.monotonic() > deadline:
                    raise TimeoutError('capture observer barrier')
                time.sleep(.08)
            check(role + ':marker-before-signal', active.exists())
            command(docker + ['kill', '--signal', 'TERM', record['id']])
        logs = wait(record, expected)
        if removed == PURE:
            if (work / 'result.json').exists():
                missing = json.loads((work / 'result.json').read_text())
                check(role + ':missing-pure-cause', missing['status'] == 'probe-error' and missing['type'] == 'FileNotFoundException' and missing['missingDependency'] == PURE)
                shutil.copyfile(work / 'result.json', evidence / (role + '-result.json'))
            else:
                check(role + ':missing-pure-cause', 'FileNotFoundException' in logs and PURE + ', Version=' in logs)
            return
        trace = (work / 'host.trace').read_bytes()
        check(role + ':trace-no-checkout', str(repository).encode() not in trace and b'/Users/andrew/.nuget/' not in trace)
        shutil.copyfile(work / 'host.trace', evidence / (role + '.trace'))
        outcome = json.loads((work / 'result.json').read_text())
        (evidence / (role + '-result.json')).write_text(json.dumps(outcome, indent=2) + '\n')
        references = outcome['directReferences']
        check(role + ':ordinary-reference-boundary', PURE in references and not any(name in references for name in ['BouncyCastle.Cryptography', 'ZstdSharp'])
              and not any('Aspire' in name for name in references))
        for assembly in outcome['assemblies']:
            check(role + ':assembly-origin:' + assembly['name'], assembly['location'].startswith(('/proof/payload/', '/usr/share/dotnet/shared/')))
        if expected == 0:
            check(role + ':public-captured', outcome['status'] == 'captured' and outcome['platform'] == 'linux/amd64' and outcome['mode'] == 'Security')
            expected_identity = json.loads((fixtures / variant / 'expectation.json').read_text())
            check(role + ':canonical-identities', outcome['recipeDigest'] == expected_identity['recipeDigest'] and outcome['policyDigest'] == expected_identity['policyDigest'])
            check(role + ':dependencies-loaded', all(name in [a['name'] for a in outcome['assemblies']] for name in [PURE, 'BouncyCastle.Cryptography', 'ZstdSharp']))
            check(role + ':snapshot-after-dispose', outcome['snapshotRetainedAfterDispose'] and (work / 'snapshot' / 'snapshot.json').exists())
            if scenario == 'valid':
                check(role + ':independent-copy', outcome['independentCopy'])
            if scenario == 'repeat':
                check(role + ':path-independent-repeat', outcome['planDigest'] == outcome['repeatedDigest'])
        else:
            check(role + ':typed-rejection', outcome['status'] == 'rejected' and outcome['code'] == code and outcome['phase'] == 'Capture' and outcome['effect'] == 'None')
            check(role + ':no-committed-snapshot', not outcome['committedSnapshot'] and outcome['snapshotExists'] is (scenario == 'existing-destination'))
        if scenario == 'signal-cancel':
            check(role + ':received-SIGTERM', outcome['signalReceived'] is True)
        stage_events = outcome['events']
        starts = [event for event in stage_events if event['outcome'] == 'Started']
        terminals = [event for event in stage_events if event['outcome'] != 'Started']
        check(role + ':public-terminal-events', len(starts) == len(terminals) and len(starts) == (2 if scenario == 'repeat' else 1) and all(event['phase'] == 'Capture' for event in stage_events) and all(event['outcome'] == ('Succeeded' if expected == 0 else 'Rejected') for event in terminals))
        check(role + ':event-correlations', sorted(event['correlation'] for event in starts) == sorted(event['correlation'] for event in terminals))
        if variant in ['invalid-signature', 'invalid-zstd']:
            check(role + ':copied-inputs-before-rejection', terminals[0]['materialCount'] == outcome.get('materialCount', len(json.loads((fixtures / variant / 'bindings.json').read_text()))))
            names = [assembly['name'] for assembly in outcome['assemblies']]
            check(role + ':deep-dependencies-loaded', 'BouncyCastle.Cryptography' in names and (variant != 'invalid-zstd' or 'ZstdSharp' in names))
        check(role + ':staging-removed', not list(work.glob('.capture-*')) and not list(work.glob('.capture-inputs-*')))


    try:
        daemon = command(docker + ['info', '--format', '{{.ID}}'])
        marker()
        check('isolated-root', not isolated.is_relative_to(repository))
        check('empty-cache-before', not list(nuget.iterdir()))
        package_copy = isolated / 'input.nupkg'
        shutil.copyfile(args.package.resolve(), package_copy)
        package_hash = digest(package_copy)
        extracted = isolated / 'package'
        with zipfile.ZipFile(package_copy) as archive:
            check('safe-package-paths', all(not Path(name).is_absolute() and '..' not in Path(name).parts for name in archive.namelist()))
            archive.extractall(extracted)
        tool = extracted / 'tools/net10.0/any/realm-ready'
        manifest = {path.relative_to(tool).as_posix(): digest(path) for path in tool.rglob('*') if path.is_file()}
        check('required-payload', all(name in manifest for name in [PURE + '.dll', 'BouncyCastle.Cryptography.dll', 'ZstdSharp.dll', WORKER + '.deps.json', WORKER + '.runtimeconfig.json']))
        (evidence / 'payload-manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
        fixtures = isolated / 'fixtures'
        frozen_files = list(fixture_source.rglob('*'))
        check('frozen-fixtures-present', bool(frozen_files) and not any(path.is_symlink() for path in frozen_files))
        fixture_storage_hashes = {path.relative_to(fixture_source).as_posix(): digest(path)
                                  for path in frozen_files if path.is_file()}
        stage_frozen_fixtures(fixture_source, fixtures)
        fixture_hashes = {path.relative_to(fixtures).as_posix(): digest(path)
                          for path in fixtures.rglob('*') if path.is_file()}
        for variant in ['valid', 'invalid-signature', 'invalid-zstd']:
            fixture = fixtures / variant
            recipe = json.loads((fixture / 'recipe.json').read_text())
            policy = json.loads((fixture / 'policy.json').read_text())
            expectation = json.loads((fixture / 'expectation.json').read_text())
            bindings = json.loads((fixture / 'bindings.json').read_text())
            check(variant + ':frozen-canonical-identities', digest(fixture / 'recipe.json') == expectation['recipeDigest'] and digest(fixture / 'policy.json') == expectation['policyDigest'])
            materials = {item['id']: item for item in recipe['materialReferences']}
            for binding in bindings:
                bound = fixture / binding['file']
                material = materials[binding['id']]
                check(variant + ':coherent-material:' + binding['id'], bound.resolve().is_relative_to(fixture.resolve())
                      and digest(bound) == binding['sha256'] == material['sha256'] == policy['materialSha256ByIdentity'][binding['id']]
                      and bound.stat().st_size == binding['maxBytes'] == material['maxBytes'])
            check(variant + ':expected-fixture-size', len(bindings) == expectation['materialCount'] == 12 and len(set(item['file'] for item in bindings)) == 8)
        source = isolated / 'probe-source'
        source.mkdir()
        shutil.copyfile(repository / sources[1], source / 'Program.cs')
        (source / 'NuGet.Config').write_text('<configuration><packageSources><clear/></packageSources></configuration>')
        (source / 'Probe.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><AssemblyName>' + PROBE + '</AssemblyName><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><EnableDefaultCompileItems>false</EnableDefaultCompileItems><OutputPath>/proof/build/out/</OutputPath><AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath></PropertyGroup><ItemGroup><Compile Include="/proof/source/Program.cs"/><Reference Include="' + PURE + '"><HintPath>/proof/payload/' + PURE + '.dll</HintPath><Private>false</Private></Reference></ItemGroup></Project>')
        build = isolated / 'build'
        build.mkdir()
        mounts = [(source, '/proof/source', False), (tool, '/proof/payload', False), (build, '/proof/build', True)]
        props = ['-p:BaseIntermediateOutputPath=/proof/build/obj/', '-p:MSBuildProjectExtensionsPath=/proof/build/obj/']
        facts = wait(create('runtime-facts', ['--info'], []), 0)
        (evidence / 'runtime-facts.txt').write_text(facts)
        wait(create('restore', ['restore', '/proof/source/Probe.csproj', '--configfile', '/proof/source/NuGet.Config', *props], mounts), 0)
        wait(create('build', ['build', '/proof/source/Probe.csproj', '--no-restore', '-c', 'Release', '-warnaserror', *props], mounts), 0)
        capture('valid', 'valid', expected=0)
        capture('repeat', 'repeat', expected=0)
        if args.phase == 'all':
            for name in ['invalid-signature', 'invalid-zstd']:
                capture(name, 'fixture-rejection', variant=name, code='DigestMismatch' if name == 'invalid-signature' else 'Unexpected')
            for name in ['outer-tamper', 'wrong-review', 'policy-denied', 'existing-destination']:
                capture(name, name, code='DigestMismatch')
            capture('pre-cancel', 'pre-cancel', code='Cancelled')
            capture('signal-cancel', 'signal-cancel', code='Cancelled')
            for name in ['BouncyCastle.Cryptography', 'ZstdSharp']:
                capture('missing-' + name.split('.')[0], 'fixture-rejection', removed=name, code='CaptureInvalid')
            capture('missing-pure', 'fixture-rejection', removed=PURE, expected=None)
        check('empty-cache-after', not list(nuget.iterdir()))
        check('package-unchanged', digest(package_copy) == package_hash)
        check('fixtures-unchanged', all(digest(fixtures / path) == value for path, value in fixture_hashes.items()))
        check('fixture-storage-unchanged', all(digest(fixture_source / path) == value
                                               for path, value in fixture_storage_hashes.items()))
    except Exception as error:
        fault = type(error).__name__ + ': ' + str(error)[:180]
        print(json.dumps({'failure': fault}), flush=True)
    finally:
        started = time.monotonic()
        try:
            def remaining():
                value = 30 - (time.monotonic() - started)
                if value <= 0:
                    raise TimeoutError('30-second stop deadline')
                return value
            if daemon is not None:
                check('cleanup-daemon', command(docker + ['info', '--format', '{{.ID}}'], remaining()) == daemon)
                for record in reversed(containers):
                    identifier = record['id'] or command(docker + ['ps', '-aq', '--no-trunc', '--filter', 'name=^/' + record['name'] + '$'], remaining())
                    if not identifier:
                        continue
                    inspected = json.loads(command(docker + ['inspect', identifier], remaining()))[0]
                    check(record['role'] + ':cleanup-owner', inspected['Id'] == identifier and inspected['Name'] == '/' + record['name'] and inspected['Config']['Labels']['appsurface.run'] == run_id)
                    if record['id'] is None:
                        record['id'] = identifier
                        marker()
                    if inspected['State']['Running']:
                        command(docker + ['stop', '--time', '3', identifier], remaining())
                    state = json.loads(command(docker + ['inspect', identifier], remaining()))[0]['State']
                    check(record['role'] + ':stopped-before-clean', state['Running'] is False)
                observer = 'import fcntl,sys,pathlib;assert pathlib.Path(sys.argv[2]).is_file();f=open(sys.argv[1],"r+b");\ntry: fcntl.flock(f,fcntl.LOCK_EX|fcntl.LOCK_NB);sys.exit(99)\nexcept BlockingIOError: print("held-and-active")'
                check('independent-preclean-lease-and-marker', command(['python3', '-c', observer, str(evidence / 'owner.lock'), str(active)], remaining()) == 'held-and-active')
                shutdown = round(time.monotonic() - started, 6)
                check('stop-under-30-seconds', shutdown < 30)
                active.unlink()
                cleaned = True
                for record in reversed(containers):
                    if record['id']:
                        command(docker + ['rm', record['id']])
            else:
                check('no-workloads-before-daemon', not containers and not active.exists())
                cleaned = True
            shutil.rmtree(isolated)
        except Exception as error:
            fault = (fault or '') + ' cleanup:' + type(error).__name__ + ': ' + str(error)[:120]
        owner.close()
    receipt = {'schema': 1, 'status': 'subset-passed' if fault is None and cleaned else 'failed', 'phase': args.phase,
               'packageSha256': package_hash, 'sdkImage': SDK, 'sourceHashes': source_hashes, 'fixtureHashes': fixture_hashes, 'fixtureStorageHashes': fixture_storage_hashes,
               'payloadFiles': len(manifest), 'daemon': daemon, 'cases': cases, 'assertions': checks, 'failure': fault,
               'shutdownSeconds': shutdown, 'cleaned': cleaned, 'privateRootRemoved': not isolated.exists(),
               'retainedLockFile': (evidence / 'owner.lock').exists(), 'isolatedRoot': str(isolated),
               'noClaim': ['Approach B generic realm reconciliation', 'materialization/image build or inspection',
                           'published packages/downstream adoption', 'other platforms/runtime hosts',
                           'full ownership/custody/recovery or worker timeout/retry gates']}
    (evidence / 'receipt.json').write_text(json.dumps(receipt, indent=2) + '\n')
    print(json.dumps({'receipt': str(evidence / 'receipt.json'), 'status': receipt['status'], 'assertions': len(checks)}), flush=True)
    if receipt['status'] != 'subset-passed':
        raise SystemExit(1)


if __name__ == '__main__':
    main()
