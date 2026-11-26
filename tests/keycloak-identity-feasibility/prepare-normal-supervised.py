#!/usr/bin/env python3
"""Prepare a copied normal-API AppHost with extracted candidate bytes and offline Aspire prerequisites."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import zipfile

def sha(p):return hashlib.sha256(p.read_bytes()).hexdigest()

def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--pure',type=Path,required=True);p.add_argument('--adapter',type=Path,required=True)
    p.add_argument('--composition-preparation',type=Path,required=True);p.add_argument('--evidence',type=Path,required=True)
    a=p.parse_args();os.umask(0o077);a.evidence.mkdir(exist_ok=False,parents=True)
    root=Path(tempfile.mkdtemp(prefix='i888-normal-prepared-',dir='/private/tmp'))
    repo=Path(__file__).resolve().parents[2]
    for name in ['host','source','nuget','http','scratch','probe-source','probe-nuget','probe-http','probe-scratch']: (root/name).mkdir()
    prerequisite=repo/'tests/keycloak-identity-feasibility/acceptance-apphost/bin/Release/net10.0'
    shutil.copytree(prerequisite,root/'host',dirs_exist_ok=True)
    for old in (root/'host').glob('appsurface-keycloak-realm-ready*'):
        if old.is_dir():shutil.rmtree(old)
    for package in [a.pure,a.adapter]:
        with zipfile.ZipFile(package) as z:
            library=next(n for n in z.namelist() if n.startswith('lib/net10.0/') and n.endswith('.dll'))
            (root/'host'/Path(library).name).write_bytes(z.read(library))
            for entry in z.infolist():
                if entry.filename.startswith('tools/net10.0/any/realm-ready/') and not entry.is_dir():
                    relative=entry.filename.removeprefix('tools/net10.0/any/realm-ready/')
                    if '..' in Path(relative).parts:raise ValueError('unsafe package entry')
                    target=root/'host/appsurface-keycloak-realm-ready'/relative
                    target.parent.mkdir(parents=True,exist_ok=True);target.write_bytes(z.read(entry))
    assert (root/'host/ForgeTrust.AppSurface.Auth.Keycloak.dll').read_bytes()==(root/'host/appsurface-keycloak-realm-ready/ForgeTrust.AppSurface.Auth.Keycloak.dll').read_bytes()
    composition=json.loads(a.composition_preparation.read_text());prepared=Path(composition['root'])
    shutil.copytree(prepared/'code',root/'payload')
    shutil.copyfile(root/'host/ForgeTrust.AppSurface.Auth.Keycloak.dll',root/'payload/ForgeTrust.AppSurface.Auth.Keycloak.dll')
    shutil.copytree(prepared/'browser',root/'browser')
    sdk=Path('/Users/andrew/.dotnet/appsurface-sdk-10.0.402')
    dcp=Path('/Users/andrew/.nuget/packages/aspire.hosting.orchestration.osx-arm64/13.6.5/tools')
    shutil.copytree(dcp,root/'dcp')
    shutil.copytree('/Users/andrew/.nuget/packages/aspire.dashboard.sdk.osx-arm64/13.6.5/tools',root/'dashboard')
    shutil.copyfile(Path(__file__).with_name('normal-supervised-apphost')/'Program.cs',root/'source/Program.cs')
    metadata={'dcpclipath':str(root/'dcp/dcp'),'dcpextensionpaths':str(root/'dcp'),'apphostprojectpath':str(root/'source'),
              'apphostprojectname':'Normal.csproj','aspiredashboardpath':str(root/'dashboard/Aspire.Dashboard.dll')}
    (root/'source/Metadata.cs').write_text('using System.Reflection;\n'+''.join('[assembly: AssemblyMetadata('+json.dumps(k)+','+json.dumps(v)+')]\n' for k,v in metadata.items()))
    references=''.join('<Reference Include="'+d.stem+'"><HintPath>'+str(d)+'</HintPath><Private>false</Private></Reference>'
                       for d in (root/'host').glob('*.dll') if d.name!='ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests.dll')
    project=root/'source/Normal.csproj'
    project.write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><AssemblyName>ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests</AssemblyName><AssemblyVersion>0.2.0.0</AssemblyVersion><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><GenerateDependencyFile>false</GenerateDependencyFile><GenerateRuntimeConfigurationFiles>false</GenerateRuntimeConfigurationFiles><UseAppHost>false</UseAppHost></PropertyGroup><ItemGroup><FrameworkReference Include="Microsoft.AspNetCore.App"/>'+references+'</ItemGroup></Project>')
    (root/'NuGet.Config').write_text('<configuration><packageSources><clear/></packageSources></configuration>')
    env=dict(os.environ,DOTNET_ROOT=str(sdk),NUGET_PACKAGES=str(root/'nuget'),NUGET_HTTP_CACHE_PATH=str(root/'http'),NUGET_SCRATCH=str(root/'scratch'))
    commands=[]
    for stage,args in [('restore',['restore',str(project),'--configfile',str(root/'NuGet.Config')]),('build',['build',str(project),'--no-restore','-c','Release','-m:1','-nr:false','-p:UseSharedCompilation=false'])]:
        with (a.evidence/(stage+'.log')).open('x') as output:
            r=subprocess.run([str(sdk/'dotnet'),*args],cwd=root/'source',env=env,stdout=output,stderr=subprocess.STDOUT,timeout=120)
        commands.append({'stage':stage,'exit':r.returncode})
        if r.returncode:raise RuntimeError(stage+' failed')
    assert not json.loads((root/'source/obj/project.assets.json').read_text())['libraries'] and not any((root/'nuget').iterdir())
    shutil.copyfile(root/'source/bin/Release/net10.0/ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests.dll',root/'host/ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests.dll')
    probe_source=root/'probe-source/Program.cs'
    shutil.copyfile(Path(__file__).with_name('lifecycle-probe')/'Program.cs',probe_source)
    probe_project=root/'probe-source/LifecycleProbe.csproj'
    probe_project.write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><AssemblyName>LifecycleProbe</AssemblyName><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><UseAppHost>false</UseAppHost><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup></Project>')
    probe_config=root/'probe-source/NuGet.Config'
    probe_config.write_text('<configuration><packageSources><clear/></packageSources></configuration>')
    probe_env=dict(env,NUGET_PACKAGES=str(root/'probe-nuget'),NUGET_HTTP_CACHE_PATH=str(root/'probe-http'),NUGET_SCRATCH=str(root/'probe-scratch'))
    probe_commands=[]
    for stage,args in [('restore',['restore',str(probe_project),'--configfile',str(probe_config)]),('build',['build',str(probe_project),'--no-restore','-c','Release','-m:1','-nr:false','-p:UseSharedCompilation=false'])]:
        with (a.evidence/('lifecycle-probe-'+stage+'.log')).open('x') as output:
            r=subprocess.run([str(sdk/'dotnet'),*args],cwd=root/'probe-source',env=probe_env,stdout=output,stderr=subprocess.STDOUT,timeout=120)
        probe_commands.append({'stage':stage,'exit':r.returncode})
        if r.returncode:raise RuntimeError('lifecycle probe '+stage+' failed')
    assert not json.loads((root/'probe-source/obj/project.assets.json').read_text())['libraries']
    assert not any((root/'probe-nuget').iterdir())
    probe_dll=root/'probe-source/bin/Release/net10.0/LifecycleProbe.dll'
    payload_probe=root/'payload/LifecycleProbe.dll'
    if payload_probe.exists():raise ValueError('payload already contains LifecycleProbe.dll')
    shutil.copyfile(probe_dll,payload_probe)
    for suffix in ['deps.json','runtimeconfig.json']:
        shutil.copyfile(probe_dll.with_name('LifecycleProbe.'+suffix),root/'payload'/('LifecycleProbe.'+suffix))
    probe_manifest={'sourcePath':'tests/keycloak-identity-feasibility/lifecycle-probe/Program.cs','sourceSha256':sha(probe_source),
                    'projectSha256':sha(probe_project),'nugetConfigSha256':sha(probe_config),'assemblySha256':sha(payload_probe),
                    'targetFramework':'net10.0','sdkVersion':'10.0.402','commands':probe_commands,'frameworkOnly':True,
                    'packageLibraries':[],'emptyPrivateCache':True,'noPackageFeeds':True,'compiledOutsideCheckout':True}
    (a.evidence/'lifecycle-probe-manifest.json').write_text(json.dumps(probe_manifest,indent=2)+'\n')
    receipt={'schema':1,'passed':True,'root':str(root),'purePackageSha256':sha(a.pure),'adapterPackageSha256':sha(a.adapter),
             'sourceSha256':sha(root/'source/Program.cs'),'commands':commands,'emptyPackageCache':True,'noPackageFeeds':True,
             'lifecycleProbe':probe_manifest,
             'hostManifest':{p.relative_to(root/'host').as_posix():sha(p) for p in (root/'host').rglob('*') if p.is_file()},
             'payloadManifest':{p.relative_to(root/'payload').as_posix():sha(p) for p in (root/'payload').rglob('*') if p.is_file()},
             **{kind+'Manifest':{p.relative_to(root/kind).as_posix():sha(p) for p in (root/kind).rglob('*') if p.is_file()}
                for kind in ('browser', 'dcp', 'dashboard')},
             'prerequisites':'copied existing Aspire13.6.5 closure,DCP,dashboard; complete package archives separately recorded in prior preparation',
             'noClaim':['installed consumer restore','clean CLI home','supported cross-platform API acceptance']}
    (a.evidence/'receipt.json').write_text(json.dumps(receipt,indent=2)+'\n');print(json.dumps({'passed':True,'root':str(root)}))

if __name__=='__main__':main()
