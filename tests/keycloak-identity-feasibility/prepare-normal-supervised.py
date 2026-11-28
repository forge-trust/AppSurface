#!/usr/bin/env python3
"""Prepare a copied normal-API AppHost with extracted candidate bytes and offline Aspire prerequisites."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import tempfile
import zipfile

from replay_environment import (SDK_VERSION, dotnet_host, prepared_file_manifest, run_offline_build,
                                verify_prepared_closure, verify_sdk_version)
from worker_payload import PURE_LIBRARY, read_worker_payload, worker_payload_target

def sha(p):return hashlib.sha256(p.read_bytes()).hexdigest()

def stage_verified_composition(composition: dict, root: Path) -> None:
    """Copy the composition inputs and check their staged bytes before applying candidate overrides."""
    prepared = Path(composition['root'])
    payload = root / 'payload'
    browser = root / 'browser'
    shutil.copytree(prepared / 'code', payload)
    verify_prepared_closure(payload, composition['codeManifest'])
    shutil.copytree(prepared / 'browser', browser)
    if sha(browser / 'supervised-browser.mjs') != composition['browserScriptSha256']:
        raise ValueError('prepared browser driver changed')

def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--pure',type=Path,required=True);p.add_argument('--adapter',type=Path,required=True)
    p.add_argument('--composition-preparation',type=Path,required=True);p.add_argument('--evidence',type=Path,required=True)
    p.add_argument('--sdk', type=Path, help='Pinned .NET SDK root, required for older composition receipts')
    p.add_argument('--aspire-packages', type=Path, required=True, help='NuGet package directory containing the pinned Aspire native tools')
    a=p.parse_args();os.umask(0o077);a.evidence.mkdir(exist_ok=False,parents=True)
    composition = json.loads(a.composition_preparation.read_text())
    dotnet = dotnet_host(a.sdk, composition.get('sdk'))
    aspire_packages = a.aspire_packages.resolve(strict=True)
    root=Path(tempfile.mkdtemp(prefix='i888-normal-prepared-',dir='/private/tmp')).resolve()
    verify_sdk_version(dotnet, root)
    repo=Path(__file__).resolve().parents[2]
    for name in ('host', 'source', 'probe-source'):
        (root/name).mkdir()
    prerequisite=repo/'tests/keycloak-identity-feasibility/acceptance-apphost/bin/Release/net10.0'
    shutil.copytree(prerequisite,root/'host',dirs_exist_ok=True)
    for old in (root/'host').glob('appsurface-keycloak-realm-ready*'):
        if old.is_dir():shutil.rmtree(old)
    worker_root = root / 'host' / 'appsurface-keycloak-realm-ready'
    with zipfile.ZipFile(a.pure) as pure_package:
        pure = pure_package.read('lib/net10.0/' + PURE_LIBRARY)
    worker_payload = read_worker_payload(a.adapter)
    if worker_payload.get(PURE_LIBRARY) != pure:
        raise ValueError('candidate pure and recursive worker closure differ')
    adapter_library = 'ForgeTrust.AppSurface.Auth.Aspire.Keycloak.dll'
    with zipfile.ZipFile(a.adapter) as adapter_package:
        (root/'host'/adapter_library).write_bytes(adapter_package.read('lib/net10.0/' + adapter_library))
    (root/'host'/PURE_LIBRARY).write_bytes(pure)
    for name, data in worker_payload.items():
        target = worker_payload_target(worker_root, name)
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(data)
    stage_verified_composition(composition, root)
    shutil.copyfile(root / 'host' / PURE_LIBRARY, root / 'payload' / PURE_LIBRARY)
    shutil.copytree(aspire_packages/'aspire.hosting.orchestration.osx-arm64/13.6.5/tools',root/'dcp')
    shutil.copytree(aspire_packages/'aspire.dashboard.sdk.osx-arm64/13.6.5/tools',root/'dashboard')
    shutil.copyfile(Path(__file__).with_name('normal-supervised-apphost')/'Program.cs',root/'source/Program.cs')
    metadata={'dcpclipath':str(root/'dcp/dcp'),'dcpextensionpaths':str(root/'dcp'),'apphostprojectpath':str(root/'source'),
              'apphostprojectname':'Normal.csproj','aspiredashboardpath':str(root/'dashboard/Aspire.Dashboard.dll')}
    (root/'source/Metadata.cs').write_text('using System.Reflection;\n'+''.join('[assembly: AssemblyMetadata('+json.dumps(k)+','+json.dumps(v)+')]\n' for k,v in metadata.items()))
    references=''.join('<Reference Include="'+d.stem+'"><HintPath>'+str(d)+'</HintPath><Private>false</Private></Reference>'
                       for d in (root/'host').glob('*.dll') if d.name!='ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests.dll')
    project=root/'source/Normal.csproj'
    project.write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><AssemblyName>ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests</AssemblyName><AssemblyVersion>0.2.0.0</AssemblyVersion><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><GenerateDependencyFile>false</GenerateDependencyFile><GenerateRuntimeConfigurationFiles>false</GenerateRuntimeConfigurationFiles><UseAppHost>false</UseAppHost></PropertyGroup><ItemGroup><FrameworkReference Include="Microsoft.AspNetCore.App"/>'+references+'</ItemGroup></Project>')
    apphost_cache = root/'apphost-build'
    result = run_offline_build(dotnet, project, cache_root=apphost_cache,
                               logs=(a.evidence/'restore.log', a.evidence/'build.log'))
    if result.failed_stage is not None:
        raise RuntimeError(result.failed_stage + ' failed')
    commands = [{'stage': step.stage, 'exit': step.exit_code} for step in result.commands]
    shutil.copyfile(root/'source/bin/Release/net10.0/ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests.dll',root/'host/ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests.dll')
    probe_source=root/'probe-source/Program.cs'
    shutil.copyfile(Path(__file__).with_name('lifecycle-probe')/'Program.cs',probe_source)
    probe_project=root/'probe-source/LifecycleProbe.csproj'
    probe_project.write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><AssemblyName>LifecycleProbe</AssemblyName><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><UseAppHost>false</UseAppHost><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup></Project>')
    probe_cache = root/'probe-build'
    probe_result = run_offline_build(dotnet, probe_project, cache_root=probe_cache,
                                     logs=(a.evidence/'lifecycle-probe-restore.log', a.evidence/'lifecycle-probe-build.log'))
    if probe_result.failed_stage is not None:
        raise RuntimeError('lifecycle probe ' + probe_result.failed_stage + ' failed')
    probe_commands = [{'stage': step.stage, 'exit': step.exit_code} for step in probe_result.commands]
    probe_config = probe_project.with_name('NuGet.Config')
    probe_dll=root/'probe-source/bin/Release/net10.0/LifecycleProbe.dll'
    payload_probe=root/'payload/LifecycleProbe.dll'
    if payload_probe.exists():raise ValueError('payload already contains LifecycleProbe.dll')
    shutil.copyfile(probe_dll,payload_probe)
    for suffix in ['deps.json','runtimeconfig.json']:
        shutil.copyfile(probe_dll.with_name('LifecycleProbe.'+suffix),root/'payload'/('LifecycleProbe.'+suffix))
    probe_manifest={'sourcePath':'tests/keycloak-identity-feasibility/lifecycle-probe/Program.cs','sourceSha256':sha(probe_source),
                    'projectSha256':sha(probe_project),'nugetConfigSha256':sha(probe_config),'assemblySha256':sha(payload_probe),
                    'targetFramework':'net10.0','sdkVersion':SDK_VERSION,'commands':probe_commands,'frameworkOnly':True,
                    'packageLibraries':[],'emptyPrivateCache':True,'noPackageFeeds':True,'compiledOutsideCheckout':True}
    (a.evidence/'lifecycle-probe-manifest.json').write_text(json.dumps(probe_manifest,indent=2)+'\n')
    receipt={'schema':1,'passed':True,'root':str(root),'sdk':str(dotnet.parent),
             'purePackageSha256':sha(a.pure),'adapterPackageSha256':sha(a.adapter),
             'sourceSha256':sha(root/'source/Program.cs'),'commands':commands,'emptyPackageCache':True,'noPackageFeeds':True,
             'lifecycleProbe':probe_manifest,
             **{kind+'Manifest':prepared_file_manifest(root/kind) for kind in ('host', 'payload', 'browser', 'dcp', 'dashboard')},
             'prerequisites':'copied existing Aspire13.6.5 closure,DCP,dashboard; complete package archives separately recorded in prior preparation',
             'noClaim':['installed consumer restore','clean CLI home','supported cross-platform API acceptance']}
    (a.evidence/'receipt.json').write_text(json.dumps(receipt,indent=2)+'\n');print(json.dumps({'passed':True,'root':str(root)}))

if __name__=='__main__':main()
