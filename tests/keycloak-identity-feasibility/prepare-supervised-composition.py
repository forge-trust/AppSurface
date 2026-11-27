#!/usr/bin/env python3
"""Build copied real web/seed fixtures offline against extracted local packages, with fresh private caches."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import tempfile
import zipfile

from replay_environment import dotnet_host, run_offline_build, verify_offline_restore, verify_sdk_version
from worker_payload import read_worker_payload, worker_payload_target

def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()

def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--packages', type=Path, required=True)
    p.add_argument('--evidence', type=Path, required=True)
    p.add_argument('--sdk', type=Path, required=True)
    p.add_argument('--playwright-modules', type=Path, required=True, help='Directory containing playwright and playwright-core')
    p.add_argument('--chromium-engine', type=Path, required=True, help='Root of the pinned Chromium headless shell distribution')
    p.add_argument('--version', required=True)
    p.add_argument('--identity-version', help='Use a coordinated pure Keycloak/adapter pair with an existing OIDC package closure')
    a = p.parse_args()
    dotnet = dotnet_host(a.sdk)
    os.umask(0o077)
    a.evidence.mkdir(parents=True, exist_ok=False)
    root = Path(tempfile.mkdtemp(prefix='i888-compose-prepared-', dir='/private/tmp')).resolve()
    verify_sdk_version(dotnet, root)
    for name in ('code', 'source', 'browser'):
        (root/name).mkdir()
    repo = Path(__file__).resolve().parents[2]
    packages = {}
    payload = {}
    def extract(path, member):
        with zipfile.ZipFile(path) as z:
            data = z.read(member)
        name = Path(member).name
        target = root/'code'/name
        if target.exists() and target.read_bytes() != data:
            raise ValueError('conflicting extracted assembly: '+name)
        target.write_bytes(data)
        payload[name] = {'package': path.name, 'member': member, 'sha256': sha(target)}
        packages[path.name] = sha(path)
    names = ['ForgeTrust.AppSurface.Core','ForgeTrust.AppSurface.Auth','ForgeTrust.AppSurface.Auth.AspNetCore',
             'ForgeTrust.AppSurface.Auth.AspNetCore.Oidc','ForgeTrust.AppSurface.Auth.Keycloak']
    for name in names:
        version = a.identity_version if name == 'ForgeTrust.AppSurface.Auth.Keycloak' and a.identity_version else a.version
        extract(a.packages/(name+'.'+version+'.nupkg'), 'lib/net10.0/'+name+'.dll')
    adapter = a.packages/('ForgeTrust.AppSurface.Auth.Aspire.Keycloak.'+(a.identity_version or a.version)+'.nupkg')
    closure = read_worker_payload(adapter)
    for name, data in closure.items():
        target = worker_payload_target(root/'code', name)
        if target.exists() and target.read_bytes()!=data:
            raise ValueError('worker/pure candidate mismatch')
        target.parent.mkdir(parents=True,exist_ok=True)
        target.write_bytes(data)
    packages[adapter.name]=sha(adapter)
    assets = json.loads((repo/'Auth/ForgeTrust.AppSurface.Auth.AspNetCore.Oidc/obj/project.assets.json').read_text())
    target = next(iter(assets['targets'].values()))
    package_cache = Path(os.environ['NUGET_PACKAGES'])
    for library, value in target.items():
        if value['type']!='package':
            continue
        package_dir = package_cache/assets['libraries'][library]['path']
        path = package_dir/(library.replace('/','.').lower()+'.nupkg')
        for member in value.get('runtime',{}):
            if member.endswith('.dll'):
                extract(path,member)
    outcomes=[]
    for folder,assembly,web in [('supervised-web','SupervisedWeb',True),('supervised-seed','SupervisedSeed',False)]:
        source=root/'source'/folder
        source.mkdir()
        shutil.copyfile(Path(__file__).with_name(folder)/'Program.cs',source/'Program.cs')
        refs=''.join('<Reference Include="'+dll.stem+'"><HintPath>'+str(dll)+'</HintPath></Reference>' for dll in (root/'code').glob('*.dll')
                     if dll.stem!='ForgeTrust.AppSurface.Auth.Aspire.Keycloak.RealmReadyWorker')
        project = source/'Fixture.csproj'
        project.write_text('<Project Sdk="Microsoft.NET.Sdk'+('.Web' if web else '')+'"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><AssemblyName>'+assembly+'</AssemblyName><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><UseAppHost>false</UseAppHost><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup><ItemGroup>'+refs+'</ItemGroup></Project>')
        cache = source/'build-cache'
        steps = run_offline_build(dotnet, project, cache_root=cache, publish_to=source/'publish',
                                  logs=(a.evidence/(folder+'-restore.log'), a.evidence/(folder+'-publish.log')))
        if steps[-1].exit_code:
            raise RuntimeError(folder+' '+steps[-1].stage+' failed; inspect saved log')
        outcomes.extend({'command': step.command, 'exitCode': step.exit_code} for step in steps)
        verify_offline_restore(source/'obj/project.assets.json', cache/'nuget')
        for path in (source/'publish').iterdir():
            if path.suffix in ['.dll','.json']:
                destination=root/'code'/path.name
                if destination.exists() and destination.read_bytes()!=path.read_bytes():
                    raise ValueError('fixture replaces candidate bytes')
                shutil.copyfile(path,destination)
    for module in ['playwright','playwright-core']:
        shutil.copytree(a.playwright_modules/module,root/'browser/node_modules'/module)
    shutil.copytree(a.chromium_engine,root/'browser/engine')
    shutil.copyfile(Path(__file__).with_name('supervised-browser.mjs'),root/'browser/supervised-browser.mjs')
    receipt={'schema':1,'passed':True,'root':str(root),'sdk':str(dotnet.parent),'packages':packages,'extractedAssemblies':payload,
             'workerClosure':{name:hashlib.sha256(data).hexdigest() for name,data in closure.items()},
             'commands':outcomes,'emptyPackageCache':True,'noPackageFeeds':True,'projectReferences':[],
             'codeManifest':{p.relative_to(root/'code').as_posix():sha(p) for p in (root/'code').rglob('*') if p.is_file()},
             'sourceHashes':{folder:sha(root/'source'/folder/'Program.cs') for folder in ['supervised-web','supervised-seed']},
             'browserScriptSha256':sha(root/'browser/supervised-browser.mjs')}
    (a.evidence/'receipt.json').write_text(json.dumps(receipt,indent=2)+'\n')
    print(json.dumps({'passed':True,'root':str(root)}))

if __name__=='__main__':
    main()
