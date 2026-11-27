#!/usr/bin/env python3
"""Remove only exact stopped normal-adapter proof fixtures; preserve every retained active marker."""
import argparse
import fcntl
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import time

from replay_environment import docker_endpoint

def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--receipt',type=Path,required=True)
    p.add_argument('--evidence',type=Path,required=True)
    p.add_argument('--docker-endpoint', help='Qualified local Docker Unix endpoint for older receipts')
    a=p.parse_args();os.umask(0o077)
    r=json.loads(a.receipt.read_text());root=Path(r['root']);volume=r['volume']
    if root.parent!=Path('/private/tmp') or root.resolve()!=root or root.is_symlink() or not root.name.startswith('i888-normal-run-') or root.stat().st_uid!=os.getuid():
        raise ValueError('unqualified fixture root')
    legacy = r.get('legacyBootstrap')
    if legacy:
        if (not r['passed'] or r.get('fixture') not in ('legacy', 'legacy-uid1000') or legacy['volume'] != volume
                or not legacy['activeCleared'] or (root/'legacy-bootstrap/active.json').exists()):
            raise ValueError('unqualified generated proof volume')
    elif not volume.startswith('appsurface-issue888-normal-') or len(volume.removeprefix('appsurface-issue888-normal-'))!=32:
        raise ValueError('unqualified fixture volume')
    started=time.monotonic();deadline=started+30
    docker=['docker','--host',docker_endpoint(a.docker_endpoint, r.get('dockerEndpoint'))]
    def command(*args):
        remaining=deadline-time.monotonic()
        if remaining<=0:raise TimeoutError('fixture cleanup deadline')
        c=subprocess.run([*docker,*args],capture_output=True,text=True,timeout=min(8,remaining))
        if c.returncode:raise RuntimeError('fixture inventory unavailable')
        return c.stdout.strip()
    if command('info','--format','{{.ID}}')!=r['daemon']:raise ValueError('daemon changed')
    key=hashlib.sha256((r['daemon']+'\n'+volume).encode()).hexdigest()
    lock=root/'leases'/key/'owner.lock';active=lock.with_name('active.json')
    before=active.read_bytes() if active.exists() else None
    records=[]
    for case in root.iterdir():
        if not case.is_dir() or not (case/'registration.json').exists():continue
        registration=json.loads((case/'registration.json').read_text());owner=registration['OwnerId']
        data=Path(registration['RunDirectory'])
        if len(owner)!=32 or data.name!=owner or not data.is_relative_to(root) or data.is_symlink():raise ValueError('owner record changed')
        records.append((owner,data))
    # No stop/kill is issued. Failure of any stopped observation leaves the fixture for inspection.
    held=lock.open('r+b') if lock.exists() else None
    try:
        if held:fcntl.flock(held,fcntl.LOCK_EX|fcntl.LOCK_NB)
        if legacy and command('ps','-aq','--no-trunc','--filter','label=appsurface.issue888.legacy='+legacy['label']):
            raise ValueError('legacy bootstrap still has a container')
        observations=[]
        for owner,data in records:
            for native in command('ps','-aq','--no-trunc','--filter','label=appsurface.run='+owner).split():
                d=json.loads(command('inspect',native))[0];state=d['State'];h=d['HostConfig']
                if d['Id']!=native or d['Config']['Labels']['appsurface.run']!=owner or not d['Name'].startswith('/appsurface-owned-'+owner+'-'):
                    raise ValueError('container identity changed')
                if state['Running'] or state['Paused'] or state['Restarting'] or state['Pid']!=0 or state['Status'] not in ['created','exited']:
                    raise ValueError('container is not definitely stopped')
                if h['Privileged'] or h['RestartPolicy']['Name']!='no':raise ValueError('container profile changed')
                for m in d['Mounts']:
                    if m['Type']=='volume':
                        if m['Name']!=volume:raise ValueError('foreign volume')
                    elif m['Type']=='bind':
                        source=Path(m['Source'])
                        if not source.is_relative_to(data) and source!=Path('/var/lib/docker/volumes')/volume/'_data':raise ValueError('foreign bind mount')
                    else:raise ValueError('unexpected mount')
                observations.append({'id':native,'owner':owner,'state':state['Status'],'pid':0})
        if before is not None:
            marker=json.loads(before);process=marker['OwnerProcess']
            try:os.kill(process['Pid'],0)
            except ProcessLookupError:pass
            else:raise ValueError('owner PID live or reused')
        # Complete the independent stopped census before any removal.
        for item in observations:command('rm',item['id'])
        if command('ps','-aq','--no-trunc','--filter','volume='+volume):raise ValueError('volume still attached')
        volumes=command('volume','ls','--format','{{.Name}}').splitlines()
        volumeRemoved=False
        if volume in volumes:
            v=json.loads(command('volume','inspect',volume))[0]
            if v['Name']!=volume or v['Driver']!='local' or v['Scope']!='local' or v.get('Options') not in [None,{}]:raise ValueError('volume profile changed')
            if legacy and (v['CreatedAt'] != legacy['createdAt']
                    or v['Labels'].get('appsurface.issue888.legacy') != legacy['label']):
                raise ValueError('legacy fixture volume identity changed')
            if before is not None:
                expected=json.loads(before).get('Volume')
                if expected and any(v[k]!=expected[k] for k in ['Name','CreatedAt','Mountpoint']):raise ValueError('volume metadata changed')
            command('volume','rm',volume);volumeRemoved=True
        for owner,data in records:
            if data.exists():
                for private in data.glob('private-*'):
                    if private.is_symlink() or not private.is_dir():raise ValueError('private directory changed')
                    shutil.rmtree(private)
        for case in root.iterdir():
            if not case.is_dir() or not (case/'input.json').exists():continue
            private=case/'private'
            if private.exists():
                if private.is_symlink():raise ValueError('fixture input link')
                shutil.rmtree(private)
            (case/'browser-private.json').unlink(missing_ok=True)
        after=active.read_bytes() if active.exists() else None
        if after!=before:raise ValueError('retained marker changed')
        if time.monotonic()>=deadline:raise TimeoutError('fixture cleanup deadline')
        a.evidence.mkdir(parents=True,exist_ok=False)
        result={'schema':1,'passed':True,'kind':'stopped-normal-fixture-cleanup','seconds':time.monotonic()-started,
                'workloadsStarted':False,'workloadsStopped':False,'containersRemoved':observations,'volumeRemoved':volumeRemoved,
                'retainedMarkerUnchanged':True,'activeMarkerRetained':before is not None,'originalReceiptUnchanged':hashlib.sha256(a.receipt.read_bytes()).hexdigest()}
        (a.evidence/'receipt.json').write_text(json.dumps(result,indent=2)+'\n');print(json.dumps({'passed':True,'removed':len(observations),'volumeRemoved':volumeRemoved,'activeRetained':before is not None}))
    finally:
        if held:held.close()

if __name__=='__main__':main()
