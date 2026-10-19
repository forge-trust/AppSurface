#!/usr/bin/env python3
"""Bounded real Linux consumers. C# owns runtime policy, processes, custody and cleanup."""
import argparse, datetime, hashlib, importlib.util, json, os, platform, select, shutil, signal, socket, stat, struct, subprocess, sys, time, uuid
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parent))
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'issue779-checkpoint2'))
spec=importlib.util.spec_from_file_location('checkpoint2_helpers',Path(__file__).resolve().parents[1]/'issue779-checkpoint2/run-native.py')
c2=importlib.util.module_from_spec(spec);spec.loader.exec_module(c2)
require=c2.require;write=c2.write;canonical=c2.canonical;command=c2.command;sha=c2.sha
MAX_LOG=8*1024*1024
CASES=('normal','sdk-normal','readiness','factory-stall','cancel','stuck-descendant','disposal-failure')

def events(path,schema):
 if not path.is_file():return []
 require(path.stat().st_size<=MAX_LOG,'root-log-bound')
 result=[]
 for row in path.read_bytes().splitlines():
  try:value=json.loads(row)
  except (ValueError,UnicodeError):continue
  if type(value) is dict and value.get('schema')==schema:result.append(value)
 return result

def sealed_descriptor(path):
 """Share the same sealed-publication observer as C2, selecting only the v2 data schema."""
 return c2.sealed_descriptor(path,"evidence-worker-linux-v2")

def process_sample(pid,uid,gid,unit):
 require(type(pid) is int and pid>0,'actual-pid')
 root=Path('/proc')/str(pid)
 before=c2.parse_proc_starttime((root/'stat').read_bytes(),pid)
 status=(root/'status').read_bytes();threads=c2.parse_proc_status(status,uid,gid)
 group=(root/'cgroup').read_bytes()
 after=c2.parse_proc_starttime((root/'stat').read_bytes(),pid)
 require(before is not None and before==after and threads is not None and group==('0::/system.slice/'+unit+'\n').encode(),'actual-application-identity')
 return {'pid':pid,'uid':uid,'gid':gid,'starttime':before,'cgroup':'/system.slice/'+unit,'threads':threads}

class DescendantControl:
 """Fixture-only pidfd owner; confirms actual TERM resistance before cancellation is allowed."""
 def __init__(self,pid,uid,gid,unit):
  self.unit=unit;self.fd=None;self.term_seen=False
  fd=os.pidfd_open(pid,0)
  self.fd=fd
  try:
   self.require_pidfd_live()
   self.sample=process_sample(pid,uid,gid,unit)
   self.require_live()
   signal.pidfd_send_signal(fd,signal.SIGTERM,None,0)
  except BaseException:
   self.close();raise

 def require_pidfd_live(self):
  require(self.fd is not None,'descendant-pidfd-owned')
  poll=select.poll();poll.register(self.fd,select.POLLIN|select.POLLHUP|select.POLLERR)
  require(not poll.poll(0),'descendant-still-live')

 def require_live(self):
  self.require_pidfd_live()
  actual=process_sample(self.sample['pid'],self.sample['uid'],self.sample['gid'],self.unit)
  require(all(actual[key]==self.sample[key] for key in ('pid','uid','gid','starttime','cgroup')),'descendant-original-identity')
  self.require_pidfd_live()

 def confirm(self,scratch):
  self.require_live()
  if self.term_seen:return True
  marker=scratch/'descendant-term'
  if not marker.is_file():return False
  with marker.open('rb') as source:payload=source.read(65)
  # File.WriteAllText publishes the name before its bytes; incomplete/invalid bytes grant nothing.
  if payload!=b'SIGTERM handler ran\n':return False
  self.require_live()
  write(scratch/'descendant-verified',b'kernel-identity-and-term-checked\n',0o444)
  self.term_seen=True
  return True

 def close(self):
  if self.fd is not None:
   fd=self.fd;self.fd=None;os.close(fd)

def members(unit):
 p=Path('/sys/fs/cgroup/system.slice')/unit/'cgroup.procs'
 if not p.is_file():return []
 data=p.read_bytes();require(len(data)<=4096,'cgroup-procs-bound')
 return [int(row) for row in data.splitlines()]

def health(path,uid,gid,unit):
 with socket.socket(socket.AF_UNIX,socket.SOCK_STREAM) as client:
  client.settimeout(1);client.connect(str(path));pid,actual_uid,actual_gid=struct.unpack('3i',client.getsockopt(socket.SOL_SOCKET,socket.SO_PEERCRED,12))
  require(actual_uid==uid and actual_gid==gid,'health-kernel-peer')
  sample=process_sample(pid,uid,gid,unit)
  client.sendall(b'GET /health HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n');data=bytearray()
  while len(data)<=4096:
   part=client.recv(4097-len(data))
   if not part:break
   data.extend(part)
  require(len(data)<=4096 and data.startswith(b'HTTP/1.1 '),'health-bound')
  status=int(data.split(b' ',2)[1]);return sample,status,len(data)

def named_accounts(generation,expected,end):
 rows=[]
 for prefix,table in [('evw','passwd'),('evs','passwd'),('eva','passwd'),('evw','group'),('evs','group'),('eva','group'),('evr','group'),('evg','group')]:
  name=prefix+generation[:28];result=command(['/usr/bin/getent',table,name],end,allowed=(0,2))
  require(result.returncode==(0 if expected else 2),'original-account-custody')
  rows.append({'name':name,'table':table,'present':result.returncode==0})
 return rows

def output_accounting(stderr):
 rows=events(stderr,'evidence-coverage-output-v1');require(len(rows)==1,'actual-shared-output-receipt')
 row=rows[0];pairs=row['pairs']
 require(row['registered_pairs']==len(pairs) and len(pairs) in (2,3),'actual-registered-pairs')
 require(sum(pair['received_bytes'] for pair in pairs)==row['received_bytes'],'actual-byte-reconciliation')
 require(all(p['stdout_eof'] and p['stderr_eof'] and p['failure']=='None' for p in pairs),'actual-all-eof')
 return row

def stage(args,end,*,sdk_only=False):
 require(type(sdk_only) is bool,'closed-staging-selector')
 prep=json.loads(args.prepared.read_bytes());digests=json.loads(args.digests.read_bytes())
 require(prep['schema']=='issue779-c3-preparation-input-v1' and digests['schema']=='issue779-c3-registration-digests-v1','preparation-shape')
 entries={e['application_id']:e['application_entry_digest'] for e in digests['entries']}
 require(len(entries)==4 and set(prep['variants'])=={'normal','readiness','factory-stall','stuck-descendant'},'closed-four-variants')
 parent=Path('/var/lib')/('issue779-c3-input-'+uuid.uuid4().hex);c2.create_traversable_root(parent)
 runtime=parent/'runtime';inventory={'runtime':c2.copy_tree(args.runtime,runtime),'deployments':{}}
 tools={}
 for key in (('sdk-normal',) if sdk_only else ('normal','sdk-normal','readiness','factory-stall','stuck-descendant')):
  variant='normal' if key=='sdk-normal' else key;data=prep['variants'][variant]
  tool=parent/key;c2.copy_tree(args.sdk_tool if key=='sdk-normal' else args.tool,tool)
  bundle=tool/'application';c2.copy_tree(Path(data['application_path']),bundle)
  policy=Path(data['policy_path']);require(sha(policy)==data['policy_sha256'],'bound-policy-bytes')
  write(tool/'fixture-policy.json',policy.read_bytes(),0o444)
  resource=bundle/'resource/NativeHttpResource.dll';require(resource.is_file(),'actual-managed-probe-source')
  write(tool/'protected-tool.dll',resource.read_bytes(),0o444)
  for p in (tool,*tool.rglob('*')):
   m=p.lstat();require(not stat.S_ISLNK(m.st_mode) and (stat.S_ISDIR(m.st_mode) or stat.S_ISREG(m.st_mode)),'staged-type')
   os.chown(p,0,0,follow_symlinks=False);p.chmod(0o555 if p.is_dir() or m.st_mode&0o111 else 0o444)
  require(data['application_id'] in entries,'compiled-entry-digest')
  tools[key]=(tool,data,entries[data['application_id']]);inventory['deployments'][key]={'entry_sha256':sha(tool/'ForgeTrust.AppSurface.Cli.dll'),'policy_sha256':sha(tool/'fixture-policy.json'),'application_id':data['application_id'],'entry_digest':entries[data['application_id']]}
 for p in (runtime,*runtime.rglob('*')):
  m=p.lstat();require(not stat.S_ISLNK(m.st_mode),'runtime-links');os.chown(p,0,0,follow_symlinks=False);p.chmod(0o555 if p.is_dir() or m.st_mode&0o111 else 0o444)
 write(args.output/'staging.json',canonical(inventory));return tools,runtime

def retain_application_prefixes(generation,output,case):
 root=Path('/run/appsurface-app-'+generation+'/control')
 if not root.exists():return False
 before=root.lstat();require(stat.S_ISDIR(before.st_mode) and before.st_uid==before.st_gid==0 and stat.S_IMODE(before.st_mode)==0o700,'private-app-diagnostic-root')
 fd=os.open(root,os.O_RDONLY|os.O_DIRECTORY|os.O_NOFOLLOW|os.O_CLOEXEC)
 try:
  require((os.fstat(fd).st_dev,os.fstat(fd).st_ino)==(before.st_dev,before.st_ino),'private-app-diagnostic-identity')
  for name in ('stdout.prefix','stderr.prefix'):
   child=os.open(name,os.O_RDONLY|os.O_NOFOLLOW|os.O_CLOEXEC,dir_fd=fd)
   try:
    m=os.fstat(child);require(stat.S_ISREG(m.st_mode) and m.st_uid==m.st_gid==0 and m.st_nlink==1 and stat.S_IMODE(m.st_mode)==0o600 and m.st_size<=65536,'private-app-diagnostic-file')
    data=os.read(child,65537);after=os.fstat(child)
    require(len(data)==m.st_size and not os.read(child,1) and (m.st_dev,m.st_ino,m.st_size,m.st_mtime_ns,m.st_ctime_ns)==(after.st_dev,after.st_ino,after.st_size,after.st_mtime_ns,after.st_ctime_ns),'private-app-diagnostic-stability')
    write(output/(case+'-application-'+name),data)
   finally:os.close(child)
 finally:os.close(fd)
 return True

def remove_sealed_fixture_tree(path,generation,deadline):
 # Fixture housekeeping only AFTER original positive custody/account-absence checks.
 # Every leaf is root-owned, single-link regular data; no symlinks, devices or mounts are traversed.
 allowed={Path('/run')/(prefix+generation) for prefix in ('appsurface-evidence-producer-data-','appsurface-app-')}
 require(path in allowed and len(generation)==32 and all(c in '0123456789abcdef' for c in generation),'fixed-fixture-cleanup-root')
 root=path.lstat();require(stat.S_ISDIR(root.st_mode) and root.st_uid==root.st_gid==0 and root.st_mode&0o7022==0,'sealed-fixture-root')
 count=0;flags=os.O_RDONLY|os.O_DIRECTORY|os.O_NOFOLLOW|os.O_CLOEXEC
 def mount_id(fd):
  with open('/proc/self/fdinfo/'+str(fd),'rb') as info:data=info.read(4097)
  require(len(data)<=4096,'fixture-fdinfo-bound')
  values=[line[7:].strip() for line in data.splitlines() if line.startswith(b'mnt_id:')]
  require(len(values)==1 and values[0].isdigit() and int(values[0])>0,'fixture-mount-identity')
  return int(values[0])
 def walk(fd,expected_mount):
  nonlocal count
  require(time.monotonic()<deadline and mount_id(fd)==expected_mount,'fixture-cleanup-deadline-or-mount')
  with os.scandir(fd) as entries:
   for entry in entries:
    name=entry.name;count+=1
    require(count<=100000 and name not in ('.','..') and '/' not in name and time.monotonic()<deadline,'fixture-cleanup-bound')
    m=os.stat(name,dir_fd=fd,follow_symlinks=False)
    require(m.st_uid==m.st_gid==0 and m.st_dev==root.st_dev and m.st_mode&0o7022==0,'sealed-fixture-owner-mode-device')
    if stat.S_ISDIR(m.st_mode):
     child=os.open(name,flags,dir_fd=fd)
     try:
      actual=os.fstat(child);require((actual.st_dev,actual.st_ino)==(m.st_dev,m.st_ino),'sealed-fixture-dir-binding');walk(child,expected_mount)
     finally:os.close(child)
     os.rmdir(name,dir_fd=fd)
    else:
     require(stat.S_ISREG(m.st_mode) and m.st_nlink==1,'sealed-fixture-leaf')
     os.unlink(name,dir_fd=fd)
 fd=os.open(path,flags)
 try:
  actual=os.fstat(fd);require((actual.st_dev,actual.st_ino)==(root.st_dev,root.st_ino),'sealed-fixture-root-binding')
  parent=os.open('/run',flags)
  try:expected_mount=mount_id(parent)
  finally:os.close(parent)
  walk(fd,expected_mount)
 finally:os.close(fd)
 named=path.lstat();require((named.st_dev,named.st_ino)==(root.st_dev,root.st_ino),'sealed-fixture-final-root')
 path.rmdir()
 return count

def request_data(args,tool,runtime,definition,digest,subject,variant):
 """Fixed root-selected C3 request data; no caller command or runtime capability."""
 return {'schema':'evidence-supervisor-linux-v1','mode':'observation','tool_root':str(tool),'runtime_root':str(runtime),'runtime_host':str(runtime/'dotnet'),'entry_path':str(tool/'ForgeTrust.AppSurface.Cli.dll'),'policy_file':str(tool/'fixture-policy.json'),'subject_root':str(subject),'base_revision':args.base,'subject_revision':args.source,'workflow_identity':'issue779-csharp-checkpoint3','paths':['calculation/Calculation.cs'],'observation_profile_ids':['native-http-'+('normal' if variant=='sdk-normal' else variant)],'observation_producer_ids':['coverage'],'job_deadline_utc':(datetime.datetime.now(datetime.timezone.utc)+datetime.timedelta(seconds=1200)).isoformat().replace('+00:00','Z'),'admission_seconds':30,'start_seconds':120,'collection_seconds':60,'cleanup_seconds':120,'stopping_seconds':10,'solution':str(subject/'Subject.csproj'),'application_id':definition['application_id'],'application_entry_digest':digest}

def owner_command(runtime,tool,owner,armed,request_path):
 """The unchanged fixed systemd owner recipe, shared by finite native controls."""
 return ['/usr/bin/systemd-run','--quiet','--wait','--pipe','--unit='+owner,'--service-type=exec','--property=User=0','--property=Group=0','--property=Restart=no','--property=KillMode=control-group','--property=RuntimeMaxSec=1100','--property=TimeoutStopSec=10','--property=SendSIGKILL=yes','--property=FinalKillSignal=SIGKILL','--property=LimitCORE=0','--property=LimitNOFILE=65536','--property=ConditionPathExists='+str(armed),'--property=Environment='+' '.join(c2.FIXED),'--property=UnsetEnvironment='+' '.join(c2.ENV_NAMES),'/usr/bin/env','-i',*c2.FIXED,str(runtime/'dotnet'),str(tool/'ForgeTrust.AppSurface.Cli.dll'),'evidence','supervise','--request',str(request_path)]

def execute(args,case,tools,runtime,end):
 variant='sdk-normal' if case=='sdk-test-failure' else case if case in ('readiness','factory-stall','stuck-descendant','sdk-normal') else 'normal'
 tool,definition,digest=tools[variant];generation=uuid.uuid4().hex
 root=Path('/run')/('issue779-c3-case-'+generation);c2.create_traversable_root(root)
 subject=root/'subject';c2.copy_tree(args.subject,subject)
 scenario=subject/'Scenario.cs';kind='test-failure' if case=='sdk-test-failure' else 'cancel' if case in ('cancel','stuck-descendant') else case if case in ('scratch-fifo','scratch-hardlink','scratch-oversize') else 'normal'
 scenario.write_text('namespace NativePreparation.Subject; public static class Scenario { public static string Kind => "'+kind+'"; }\n')
 for p in (subject,*subject.rglob('*')):os.chown(p,0,0,follow_symlinks=False);p.chmod(0o555 if p.is_dir() else 0o444)
 owner='appsurface-evidence-owner-'+generation+'.service';appunit='appsurface-evidence-application-'+generation+'.service'
 units=[owner,'appsurface-evidence-worker-'+generation+'.service','appsurface-evidence-producer-'+generation+'.service',appunit]
 if case in ('scratch-fifo','scratch-hardlink','scratch-oversize','sdk-test-failure'):
  import workspace_guards
  deadline=workspace_guards.observation_deadline(end,480)
 else:deadline=min(end,time.monotonic()+480)
 result={'case':case,'generation':generation,'passed':False,'native_acceptance':False}
 request=request_data(args,tool,runtime,definition,digest,subject,variant)
 request_path=root/'request.json';write(request_path,canonical(request))
 guard_sentinel=None
 if case in ('scratch-fifo','scratch-hardlink','scratch-oversize'):
  import workspace_guards
  guard_sentinel=root/'outside-sentinel';write(guard_sentinel,b'outside-owned-workspace-guard-sentinel\n')
  guard_identity=workspace_guards.identity(guard_sentinel)
 guard=Path('/run/appsurface-evidence-owners');guard.mkdir(mode=0o700,exist_ok=True);require(guard.stat().st_uid==0 and stat.S_IMODE(guard.stat().st_mode)==0o700,'guard-parent')
 marker=guard/generation;marker.mkdir(mode=0o700);write(marker/'armed',(generation+'\n').encode())
 stdout=root/'owner.stdout';stderr=root/'owner.stderr';process=None;held=None;descriptor=None;app=None;producer=None;resource=None;dcp=None;descendant=None;descendant_control=None;cancelled=False;factory=False;probe503=None;term_seen=False
 tmp_observer=getattr(args,'sdk_tmp_observer',None) if case=='sdk-test-failure' else None
 launch=owner_command(runtime,tool,owner,marker/'armed',request_path)
 try:
  with stdout.open('xb') as out,stderr.open('xb') as err:
   process=subprocess.Popen(launch,stdin=subprocess.DEVNULL,stdout=out,stderr=err,start_new_session=True)
   while time.monotonic()<deadline:
    require(stdout.stat().st_size+stderr.stat().st_size<=MAX_LOG,'root-output-bound')
    path=Path('/run/appsurface-evidence-'+generation+'/worker/worker-control.json')
    if descriptor is None:descriptor=sealed_descriptor(path)
    scratch=Path('/run/appsurface-app-'+generation+'/scratch')
    if descriptor is not None:
     if tmp_observer is not None:
      try:tmp_observer.arm_once(descriptor,units[1],process_sample,deadline)
      except Exception:pass # Passive diagnostic errors never change the original case result.
     a=descriptor['application'];uid=a['application_uid'];gid=a['application_gid'];facts=c2.show(appunit,deadline)
     if app is None and int(facts.get('MainPID','0')):
      try:app=process_sample(int(facts['MainPID']),uid,gid,appunit)
      except (FileNotFoundError,ProcessLookupError,RuntimeError):pass
     if producer is None:
      producer=c2.sample_producer(units[2],descriptor,deadline)
     if app is not None:
      fm=scratch/'factory-entered'
      if fm.is_file():factory=fm.read_bytes()==b'factory-entered\n'
      if dcp is None:
       expected=Path('/run/appsurface-app-'+generation+'/bundle/dcp/dcp').stat()
       for pid in members(appunit):
        try:
         executable=(Path('/proc')/str(pid)/'exe').stat()
         if (executable.st_dev,executable.st_ino)==(expected.st_dev,expected.st_ino):dcp=process_sample(pid,uid,gid,appunit)
        except (FileNotFoundError,ProcessLookupError,RuntimeError):continue
      if case=='readiness' and probe503 is None and (scratch/'http.sock').exists():
       try:resource,status,size=health(scratch/'http.sock',uid,gid,appunit);probe503={'status':status,'received_bytes':size}
       except (OSError,RuntimeError,ValueError):pass
      if case=='stuck-descendant' and descendant_control is None:
       dm=scratch/'descendant-armed'
       if dm.is_file():
        try:
         pid=int(dm.read_text())
         require(pid!=app['pid'],'descendant-distinct')
         descendant_control=DescendantControl(pid,uid,gid,appunit)
        except (OSError,ValueError,RuntimeError):pass
      if descendant_control is not None and not term_seen and descendant_control.confirm(scratch):
       descendant=descendant_control.sample;term_seen=True
      if case=='disposal-failure' and held is None and any(e['phase']=='ready' for e in events(stderr,'evidence-application-native-v1')):
       held=os.open(scratch,os.O_RDONLY|os.O_DIRECTORY|os.O_NOFOLLOW|os.O_CLOEXEC);metadata=os.fstat(held)
       require(metadata.st_uid==uid and metadata.st_gid==gid,'actual-mounted-scratch-holder');result['scratch_hold']={'device':metadata.st_dev,'inode':metadata.st_ino,'uid':uid,'gid':gid}
      if case in ('cancel','stuck-descendant') and not cancelled and producer is not None and (case!='stuck-descendant' or term_seen):
       entered=Path('/run/appsurface-evidence-producer-data-'+generation+'/scratch/tmp/checkpoint2-case-entered')
       if entered.is_file() and entered.read_bytes()==b'cancel':
        if case=='stuck-descendant':descendant_control.require_live()
        command(['/usr/bin/systemctl','kill','--kill-whom=main','--signal=INT',owner],deadline);cancelled=True
    if tmp_observer is not None:
     try:tmp_observer.drain()
     except Exception:pass
    if process.poll() is not None:break
    time.sleep(.1)
   require(process.poll() is not None,'root-terminal-before-deadline');code=process.wait(timeout=5)
  result.update({'root_exit':code,'apphost':app,'dcp':dcp,'resource':resource,'producer':producer,'descendant':descendant,'factory_entered':factory,'cancel_sent':cancelled,'http_negative':probe503,'descendant_term_seen':term_seen})
  require(descriptor is not None and app is not None,'actual-apphost-before-control')
  require(c2.groups_empty(units),'original-all-groups-empty');result['groups_empty']=True
  observed=events(stderr,'evidence-application-native-v1');result['application_observations']=observed
  result['worker_measurements']=events(stderr,'evidence-worker-measurement-v3' if case=='sdk-test-failure' else 'evidence-worker-measurement-v1')
  settled=[e for e in observed if e['phase']=='settled'];require(len(settled)==1 and settled[0]['group_empty_twice'] and settled[0]['pumps_joined'] and settled[0]['pending_start_joined'],'actual-application-settlement')
  if case in ('normal','sdk-normal'):
   require(code==0,'positive-root-exit');summary=json.loads(stdout.read_bytes());require(summary.get('ExecutionVerdict')=='Passed' and summary.get('CleanupCompleted') is True and summary.get('ClaimKind')=='ObservationOnly','actual-root-result')
   require(producer is not None and dcp is not None and any(e['phase']=='ready' and e['dcp_checked'] for e in observed),'actual-dcp-resource-producer')
   require(settled[0]['main_code']==1 and settled[0]['main_status']==0,'cooperative-normal-application-exit')
   final=Path('/run/appsurface-evidence-'+generation+'/output/evidence/evidence-manifest.json');manifest=json.loads(final.read_bytes());require(len(manifest['ProducerResults'])==1,'exact-producer-result-count');pr=manifest['ProducerResults'][0];require(len(pr['Artifacts'])==1,'exact-artifact-count')
   require(pr['Outcome']=='Passed' and pr['Artifacts'][0]['RelativePath']=='merged/coverage.cobertura.xml','real-shared-coverage-result')
   report=final.parent/'coverage/merged/coverage.cobertura.xml';require(sha(report)==pr['Artifacts'][0]['Sha256'],'retained-report-digest');c2.verify_sealed_output(final.parent)
   import xml.etree.ElementTree as ET
   coverage=ET.fromstring(report.read_bytes());require(int(coverage.attrib['lines-valid'])>0 and int(coverage.attrib['branches-valid'])>0 and float(coverage.attrib['line-rate'])>=.945 and float(coverage.attrib['branch-rate'])>=.845,'genuine-positive-coverage')
   write(args.output/(case+'-manifest.json'),final.read_bytes());result['coverage']=coverage.attrib;result['output']=output_accounting(stderr);require(result['output']['registered_pairs']==3,'all-three-stream-pairs')
  else:
   require(code!=0 and not stdout.read_bytes(),'negative-no-root-success')
   if case=='sdk-test-failure':
    sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'issue779-checkpoint4'))
    import sdk_failure
    result['sdk_failure']=sdk_failure.inspect_case(generation,descriptor,producer,stderr,observed,end)
    require(settled[0]['main_code']==1 and settled[0]['main_status']==0,'failed-producer-cooperative-application-exit')
   if case=='readiness':require(dcp is not None and resource is not None and probe503 is not None and probe503['status']==503,'actual-authenticated-503')
   if case=='factory-stall':require(factory and dcp is None and not any(e['phase']=='ready' for e in observed),'actual-factory-stall-before-dcp')
   if case in ('cancel','stuck-descendant'):require(cancelled and producer is not None,'actual-running-cancellation')
   if case=='stuck-descendant':require(descendant is not None and term_seen,'actual-ignoring-descendant')
   if case=='disposal-failure':
    require(held is not None,'actual-scratch-hold-before-disposal')
    storage=events(stderr,'evidence-application-storage-v1');require(any(e.get('phase')=='unmount-rejected' and e.get('errno')==16 for e in storage),'actual-ebusy-unmount-rejection')
   if case!='disposal-failure':result['output']=output_accounting(stderr)
  quarantined=case in ('disposal-failure','scratch-fifo','scratch-hardlink','scratch-oversize')
  if case.startswith('scratch-'):
   import workspace_guards
   result['workspace_guard']=workspace_guards.require_producer_rejection(case,generation,descriptor,stderr)
   require(workspace_guards.identity(guard_sentinel)==guard_identity and guard_sentinel.read_bytes()==b'outside-owned-workspace-guard-sentinel\n','producer-outside-sentinel-unchanged')
   result['outside_sentinel_unchanged']=True;result['quarantined']=True
  result['accounts']=named_accounts(generation,quarantined,deadline)
  numeric=[('passwd',descriptor['worker_uid']),('passwd',descriptor['subject_uid']),('passwd',descriptor['application']['application_uid']),
      ('group',descriptor['worker_gid']),('group',descriptor['subject_gid']),('group',descriptor['application']['application_gid']),
      ('group',descriptor['application']['results_gid']),('group',descriptor['application']['resource_access_gid'])]
  for table,number in numeric:
   looked=command(['/usr/bin/getent',table,str(number)],deadline,allowed=(0,2))
   require(looked.returncode==(0 if quarantined else 2),'original-numeric-account-custody')
   if case=='sdk-test-failure':result.setdefault('numeric_accounts',[]).append({'table':table,'id':number,'absent':looked.returncode==2})
  result['passed']=True
 except BaseException as failure:
  result['failure_category']=str(failure) if isinstance(failure,RuntimeError) else type(failure).__name__
 finally:
  # No fixture containment/repair upgrades the original result.
  if tmp_observer is not None:
   try:tmp_observer.finish()
   except Exception:pass
  if descendant_control is not None:descendant_control.close()
  if held is not None:os.close(held)
  if process is not None and case in ('scratch-fifo','scratch-hardlink','scratch-oversize','sdk-test-failure') and (process.poll() is None or not result.get('passed')):
   import workspace_guards
   workspace_guards.contain_units(units,process,end)
  elif process is not None and process.poll() is None:
   for unit in units:
    try:command(['/usr/bin/systemctl','stop',unit],min(end,time.monotonic()+15),allowed=(0,1,5))
    except Exception:pass
   try:os.killpg(process.pid,signal.SIGKILL)
   except ProcessLookupError:pass
   process.wait(timeout=5)
  result['final_groups_empty']=c2.groups_empty(units)
  for role in (('worker','producer') if case in CASES else ()):
   try:result[role+'_diagnostics_retained']=c2.retain_producer_prefixes(generation,args.output,case,role)
   except (OSError,RuntimeError):result[role+'_diagnostics_retained']=False
  if case in CASES:
   try:result['application_prefixes_retained']=retain_application_prefixes(generation,args.output,case)
   except (OSError,RuntimeError):result['application_prefixes_retained']=False
  for name in ('owner.stdout','owner.stderr'):
   p=root/name
   if p.is_file() and p.stat().st_size<=MAX_LOG:shutil.copyfile(p,args.output/(case+'-'+name));(args.output/(case+'-'+name)).chmod(0o600)
  if result.get('passed') and case not in ('disposal-failure','scratch-fifo','scratch-hardlink','scratch-oversize'):
   try:
    result['fixture_reclaimed_nodes']={}
    for prefix in ('appsurface-evidence-producer-data-','appsurface-app-'):
     selected=Path('/run')/(prefix+generation)
     result['fixture_reclaimed_nodes'][prefix]=remove_sealed_fixture_tree(selected,generation,min(end,time.monotonic()+15))
   except (OSError,RuntimeError) as failure:
    result['passed']=False;result['failure_category']='fixture-sealed-tree-cleanup-failed';result['fixture_error']=type(failure).__name__
  write(args.output/(case+'.json'),canonical(result))
 return result

def run_partitions(args,end):
 """Run original seven under their supplied bound, then one fresh fixed 900-second guard partition.

 args is the original parsed CLI namespace; end is its absolute monotonic matrix end.
 The actual entry point authenticates the platform before calling this orchestration function.
 Its existing stage/execute operations retain every native guard. Detached test outcomes only
 verify ordering; they establish no owner, native execution, admission or measured coverage.
 The second clock is shared by all six guards, not renewed for each case. Exceptions retain
 a failed aggregate, and the original seven-case results are never promoted to a guard pass.
 """
 args.output.mkdir(mode=0o700,parents=True,exist_ok=True)
 receipt={'schema':'issue779-csharp-checkpoint3-native-v1','source':args.source,'base':args.base,'exit':1,'cases':[],'trusted_eligibility':False}
 try:
  tools,runtime=stage(args,end)
  for case in CASES:
   result=execute(args,case,tools,runtime,end);receipt['cases'].append(result)
   if not result['passed']:break
  receipt['exit']=0 if len(receipt['cases'])==7 and all(c['passed'] for c in receipt['cases']) else 1
  if receipt['exit']==0:
   receipt['exit']=1 # A guard setup/inspection exception must not retain the earlier seven-case success.
   guard_end=time.monotonic()+900
   import workspace_guards
   receipt['workspace_guards']=workspace_guards.run(args,tools,runtime,guard_end)
   receipt['exit']=0 if len(receipt['workspace_guards'])==6 and all(c['passed'] for c in receipt['workspace_guards']) else 1
 finally:write(args.output/'native-receipt.json',canonical(receipt))
 return receipt['exit']

def main():
 parser=argparse.ArgumentParser()
 for name in ('tool','sdk-tool','runtime','subject','prepared','digests','output'):parser.add_argument('--'+name,type=Path,required=True)
 parser.add_argument('--source',required=True);parser.add_argument('--base',required=True);args=parser.parse_args();os.umask(0o077)
 require(os.geteuid()==0 and sys.platform=='linux' and platform.machine()=='x86_64','native-root-linux-x64')
 # The original matrix still pays for protected SDK/bundle preparation within 1800 seconds.
 # These orchestration clocks do not replace job, owner, case or cleanup deadlines.
 end=time.monotonic()+1800;require(command(['/usr/bin/systemctl','--version'],end).stdout.splitlines()[0].startswith(b'systemd 255'),'systemd-255')
 return run_partitions(args,end)
if __name__=='__main__':raise SystemExit(main())
