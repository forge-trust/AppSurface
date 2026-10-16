"""Finite Linux checkpoint-two consumer controls. Python prepares/observes; C# owns supervision."""
from pathlib import Path
import argparse,base64,datetime,hashlib,json,os,platform,re,shutil,stat,subprocess,sys,tempfile,time,urllib.request,uuid,zipfile

LIMIT=1800
MAX_LOG=4*1024*1024
ENV_NAMES=['DOTNET_STARTUP_HOOKS','DOTNET_ADDITIONAL_DEPS','DOTNET_SHARED_STORE','DOTNET_ROOT','DOTNET_ROOT_X64','DOTNET_HOST_PATH','DOTNET_ROLL_FORWARD','DOTNET_ROLL_FORWARD_TO_PRERELEASE','DOTNET_MULTILEVEL_LOOKUP','CORECLR_ENABLE_PROFILING','CORECLR_PROFILER','CORECLR_PROFILER_PATH','CORECLR_PROFILER_PATH_64','COR_ENABLE_PROFILING','COR_PROFILER','COR_PROFILER_PATH','COMPlus_ReadyToRun','COMPlus_ZapDisable']
FIXED=['PATH=/usr/bin:/bin','HOME=/nonexistent','LANG=C.UTF-8','DOTNET_CLI_TELEMETRY_OPTOUT=1','DOTNET_CLI_HOME=/tmp']

def require(ok,code):
 if not ok:raise RuntimeError(code)
def sha(p):return hashlib.sha256(p.read_bytes()).hexdigest()
def canonical(v):return json.dumps(v,sort_keys=True,separators=(',',':')).encode()
def create_traversable_root(path):
 # mkdir's requested mode is filtered by the private harness umask; set exact traversal afterward.
 path.mkdir(mode=0o700)
 fd=os.open(path,os.O_RDONLY|os.O_DIRECTORY|os.O_NOFOLLOW|os.O_CLOEXEC)
 try:
  before=os.fstat(fd);require(before.st_uid==os.geteuid() and stat.S_ISDIR(before.st_mode),'fresh-root-owner')
  os.fchmod(fd,0o755)
  after=os.fstat(fd);named=path.lstat()
  require((after.st_dev,after.st_ino)==(named.st_dev,named.st_ino)==(before.st_dev,before.st_ino) and stat.S_IMODE(after.st_mode)==0o755,'fresh-root-traversal')
 finally:os.close(fd)

def write(p,b,mode=0o600):
 p.parent.mkdir(parents=True,exist_ok=True)
 with p.open('xb') as f:f.write(b)
 p.chmod(mode)
def command(args,end,log=None,allowed=(0,)):
 require(time.monotonic()<end,'command-deadline')
 if log is None:
  c=subprocess.run(args,stdin=subprocess.DEVNULL,stdout=subprocess.PIPE,stderr=subprocess.PIPE,timeout=min(30,end-time.monotonic()),check=False)
  require(len(c.stdout)+len(c.stderr)<=1024*1024,'command-output-bound')
 else:
  with log.open('xb') as f:c=subprocess.run(args,stdin=subprocess.DEVNULL,stdout=f,stderr=subprocess.STDOUT,timeout=max(.01,end-time.monotonic()),check=False)
  log.chmod(0o600);require(log.stat().st_size<=MAX_LOG,'private-command-log-bound')
 require(c.returncode in allowed,'command-exit')
 return c

from staging import copy_tree

def package_rows(lock):
 nodes=json.loads(lock.read_bytes())['dependencies']['net10.0']
 require(type(nodes) is dict and len(nodes)<=15,'package-lock-shape')
 rows=[]
 for name,row in nodes.items():
  require(type(name) is str and type(row) is dict,'package-lock-shape')
  if row.get('type')=='Project':continue
  require(set(['resolved','contentHash','type'])<=row.keys() and row['type'] in ('Direct','Transitive'),'package-lock-shape')
  version=row['resolved'];digest=row['contentHash']
  require(re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9_.-]{0,99}',name) is not None and '..' not in name,'package-id')
  require(type(version) is str and re.fullmatch(r'[0-9]+(?:\.[0-9]+){1,3}(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?(?:\+[0-9A-Za-z.-]+)?',version) is not None and '..' not in version and len(version)<=100,'package-version')
  require(type(digest) is str and len(digest)==88 and len(base64.b64decode(digest,validate=True))==64,'package-content-hash-shape')
  rows.append((name.lower(),version.lower(),digest))
 require(len(rows)==14 and len({n for n,v,h in rows})==14,'exact-subject-package-count')
 return rows

def acquire_packages(lock,dest,end,runtime,hasher):
 rows=package_rows(lock)
 require(not dest.exists(),'fresh-package-cache');dest.mkdir(mode=0o700)
 for ident,version,content_hash in rows:
  url=f'https://api.nuget.org/v3-flatcontainer/{ident}/{version}/{ident}.{version}.nupkg'
  require(time.monotonic()<end,'package-deadline')
  with urllib.request.urlopen(url,timeout=min(30,end-time.monotonic())) as response:data=response.read(128*1024*1024+1)
  require(0<len(data)<=128*1024*1024,'package-size')
  # NuGet signed packages use their unsigned-content hash for lock binding.
  # Reuse the pinned SDK implementation rather than reimplement ZIP signature normalization.
  with tempfile.TemporaryDirectory(prefix='checkpoint2-package-') as tmp:
   package=Path(tmp)/'package.nupkg';write(package,data)
   observed=json.loads(command([str(runtime),str(hasher),str(package)],end).stdout)
  require(set(observed)=={'content_hash','archive_sha512','bytes'} and observed['bytes']==len(data),'package-hasher-shape')
  archive_hash=base64.b64encode(hashlib.sha512(data).digest()).decode()
  require(observed['archive_sha512']==archive_hash and observed['content_hash']==content_hash,'package-lock-content-hash')
  parent=dest/ident/version;parent.mkdir(parents=True,mode=0o755)
  from io import BytesIO
  with zipfile.ZipFile(BytesIO(data)) as z:
   require(len(z.infolist())<=8192 and sum(i.file_size for i in z.infolist())<=256*1024*1024,'package-expanded-bounds')
   names=set()
   for i in z.infolist():
    parts=Path(i.filename).parts
    require(parts and not i.filename.startswith('/') and all(p not in ('..','.') for p in parts) and '\\' not in i.filename and i.filename not in names,'package-path')
    names.add(i.filename)
    mode=i.external_attr>>16
    require(not stat.S_ISLNK(mode),'package-link')
    target=parent.joinpath(*parts)
    if i.is_dir():target.mkdir(parents=True,exist_ok=True);continue
    target.parent.mkdir(parents=True,exist_ok=True)
    body=z.read(i);require(len(body)==i.file_size,'package-size-exact')
    if body:write(target,body,0o644)
  write(parent/f'{ident}.{version}.nupkg',data,0o644)
  write(parent/f'{ident}.{version}.nupkg.sha512',archive_hash.encode(),0o644)
  write(parent/'.nupkg.metadata',canonical({'version':2,'contentHash':content_hash,'source':'https://api.nuget.org/v3/index.json'}),0o644)
 return {'packages':len(rows),'lock_sha256':sha(lock)}

def policy():
 assertion='appsurface/coverage/behavioral-patch@1'
 return {'Id':'checkpoint2-native','Version':'1','ConservativeProfileId':'conservative','Profiles':[
  {'Id':'coverage','Scope':'Targeted','Resources':[],'Producers':[{'Id':'coverage','Kind':'coverage','Version':'1.0.0','RequiredResources':[],'AssertionIds':[assertion],'ArtifactSlots':[{'LogicalName':'coverage-report','RelativeRoot':'merged','MediaType':'application/xml','Required':True,'MaximumBytes':20971520}],'TimeoutSeconds':180,'CoverageGate':{'MinLinePercent':95,'MinBranchPercent':85,'MinPatchLinePercent':None,'MinPatchBranchPercent':None,'PatchLineMode':'measurable','TolerancePercent':0.5}}],'Obligations':[{'Id':'covered','RiskClass':'behavioral-change','Rationale':'Actual subject calculation requires genuine coverage.','RequiredProducerIds':['coverage'],'RequiredAssertionId':assertion}]},
  {'Id':'conservative','Scope':'Release','Resources':[{'Id':'build','Readiness':'completion','DeadlineSeconds':1,'Requires':[]}],'Producers':[],'Obligations':[]}],
  'Rules':[{'Id':'source','Pattern':'calculation/**','ProfileId':'coverage'}]}

def read_output_observation(raw):
 values=[]
 for line in raw.splitlines():
  try:value=json.loads(line)
  except (ValueError,UnicodeError):continue
  if isinstance(value,dict) and value.get('schema')=='evidence-coverage-output-v1':values.append(value)
 require(len(values)==1,'actual-output-observation')
 observed=values[0]
 require(type(observed['received_bytes']) is int and type(observed['received_byte_limit']) is int,'actual-output-counter-types')
 require(len(observed['pairs'])==observed['registered_pairs']==2,'actual-registered-stream-pairs')
 require(sum(p['received_bytes'] for p in observed['pairs'])==observed['received_bytes'],'actual-byte-reconciliation')
 return observed

def sample_producer(unit,descriptor,end):
 facts=show(unit,end);pid=int(facts.get('MainPID','0'))
 if not pid:return None
 try:
  fields=dict(line.split(':',1) for line in (Path('/proc')/str(pid)/'status').read_text().splitlines() if ':' in line)
  uids=[int(x) for x in fields['Uid'].split()];gids=[int(x) for x in fields['Gid'].split()]
  groups=(Path('/proc')/str(pid)/'cgroup').read_text().splitlines()
 except FileNotFoundError:return None
 require(uids==[descriptor['subject_uid']]*4 and gids==[descriptor['subject_gid']]*4,'actual-producer-identity')
 require(groups==['0::/system.slice/'+unit],'actual-producer-cgroup')
 require(facts['User']==str(descriptor['subject_uid']) and facts['Group']==str(descriptor['subject_gid']),'actual-unit-producer-identities')
 return {'pid':pid,'uid':uids[0],'gid':gids[0],'cgroup':groups[0][3:]}

def verify_sealed_output(root):
 require(root.is_dir() and not root.is_symlink(),'sealed-output-root')
 for p in [root,*root.rglob('*')]:
  m=p.lstat();require(m.st_uid==0 and m.st_gid==0,'sealed-output-ownership')
  require((stat.S_ISDIR(m.st_mode) and stat.S_IMODE(m.st_mode)==0o700) or (stat.S_ISREG(m.st_mode) and stat.S_IMODE(m.st_mode)==0o400 and m.st_nlink==1),'sealed-output-mode-type-links')

def show(unit,end):
 c=command(['/usr/bin/systemctl','show',unit,'--property=LoadState,ActiveState,SubState,MainPID,ExecMainPID,ExecMainCode,ExecMainStatus,User,Group,ControlGroup','--no-pager'],end,allowed=(0,1))
 return dict(line.split('=',1) for line in c.stdout.decode().splitlines() if '=' in line)
def groups_empty(units):
 for u in units:
  p=Path('/sys/fs/cgroup/system.slice')/u
  if p.exists():
   facts=dict(x.split() for x in (p/'cgroup.events').read_text().splitlines())
   if facts.get('populated')!='0':return False
 return True

def retain_producer_prefixes(generation,output,case):
 private=Path('/run/appsurface-evidence-producer-diagnostics-'+generation)
 if private.exists():
  before=private.lstat();require(stat.S_ISDIR(before.st_mode) and before.st_uid==before.st_gid==0 and stat.S_IMODE(before.st_mode)==0o700,'private-producer-diagnostic-root')
  rootfd=os.open(private,os.O_RDONLY|os.O_DIRECTORY|os.O_NOFOLLOW|os.O_CLOEXEC)
  try:
   require((os.fstat(rootfd).st_dev,os.fstat(rootfd).st_ino)==(before.st_dev,before.st_ino),'private-producer-diagnostic-identity')
   for name in ['stdout.prefix','stderr.prefix']:
    fd=os.open(name,os.O_RDONLY|os.O_NOFOLLOW|os.O_CLOEXEC,dir_fd=rootfd)
    try:
     m=os.fstat(fd);require(stat.S_ISREG(m.st_mode) and m.st_uid==m.st_gid==0 and m.st_nlink==1 and stat.S_IMODE(m.st_mode)==0o600 and m.st_size<=1024*1024,'private-producer-diagnostic-file')
     data=os.read(fd,1024*1024+1);require(len(data)==m.st_size and not os.read(fd,1),'private-producer-diagnostic-bytes')
     after=os.fstat(fd);require((m.st_dev,m.st_ino,m.st_size,m.st_mtime_ns,m.st_ctime_ns)==(after.st_dev,after.st_ino,after.st_size,after.st_mtime_ns,after.st_ctime_ns),'private-producer-diagnostic-stability')
     write(output/(case+'-producer-'+name),data)
    finally:os.close(fd)
  finally:os.close(rootfd)
  return True
 return False

def execute_case(args,case,end,receipt):
 generation=uuid.uuid4().hex
 root=Path('/run')/('issue779-csharp-coverage-'+generation)
 create_traversable_root(root)
 tool=root/'tool';runtime=root/'runtime';subject=root/'subject'
 result={'case':case,'generation':generation,'passed':False,'native_acceptance':False}
 owner='appsurface-evidence-owner-'+generation+'.service'
 units=[owner,'appsurface-evidence-worker-'+generation+'.service','appsurface-evidence-producer-'+generation+'.service']
 case_end=min(end,time.monotonic()+540)
 try:
  tool=args.staged_tool;runtime=args.staged_runtime
  result['input_inventory_sha256']=args.input_inventory_sha256
  result['subject_inventory']=copy_tree(args.subject,subject)
  # Scenario selection is fixture data chosen by the root before immutable subject audit, never runtime authority.
  scenario=subject/'Scenario.cs';require(scenario.is_file(),'fixture-scenario-source')
  scenario.write_text('namespace NativePreparation.Subject; public static class Scenario { public static string Kind => "'+case+'"; }\n');scenario.chmod(0o644)
  result['subject_inventory']['Scenario.cs']={'sha256':sha(scenario),'bytes':scenario.stat().st_size,'mode':'0444'}
  policy_file=tool/'fixture-policy.json'
  for p in [subject,*subject.rglob('*')]:
   os.chown(p,0,0,follow_symlinks=False)
   p.chmod(0o555 if p.is_dir() or p.stat().st_mode&0o111 else 0o444)
  entry=tool/'ForgeTrust.AppSurface.Cli.dll';host=runtime/'dotnet'
  require(entry.is_file() and host.read_bytes()[:4]==b'\x7fELF','actual-entry-native-runtime')
  request={'schema':'evidence-supervisor-linux-v1','mode':'observation','tool_root':str(tool),'runtime_root':str(runtime),'runtime_host':str(host),'entry_path':str(entry),'policy_file':str(policy_file),'subject_root':str(subject),'base_revision':args.base,'subject_revision':args.source,'workflow_identity':'issue779-csharp-checkpoint2','paths':['calculation/Calculation.cs'],'observation_profile_ids':['coverage'],'observation_producer_ids':['coverage'],'job_deadline_utc':(datetime.datetime.now(datetime.timezone.utc)+datetime.timedelta(seconds=530)).isoformat().replace('+00:00','Z'),'admission_seconds':30,'start_seconds':60,'collection_seconds':60,'cleanup_seconds':120,'stopping_seconds':10,'solution':str(subject/'Subject.csproj')}
  request_path=root/'request.json';write(request_path,canonical(request))
  guard=Path('/run/appsurface-evidence-owners');guard.mkdir(mode=0o700,exist_ok=True)
  require(guard.stat().st_uid==0 and guard.stat().st_mode&0o777==0o700,'guard-parent')
  marker=guard/generation;marker.mkdir(mode=0o700);write(marker/'armed',(generation+'\n').encode())
  stdout=root/'owner.stdout';stderr=root/'owner.stderr'
  launch=['/usr/bin/systemd-run','--quiet','--wait','--pipe','--unit='+owner,'--service-type=exec','--property=User=0','--property=Group=0','--property=Restart=no','--property=KillMode=control-group','--property=RuntimeMaxSec=500','--property=TimeoutStopSec=10','--property=SendSIGKILL=yes','--property=FinalKillSignal=SIGKILL','--property=LimitCORE=0','--property=LimitNOFILE=65536','--property=ConditionPathExists='+str(marker/'armed'),'--property=Environment='+' '.join(FIXED),'--property=UnsetEnvironment='+' '.join(ENV_NAMES),'/usr/bin/env','-i',*FIXED,str(host),str(entry),'evidence','supervise','--request',str(request_path)]
  with stdout.open('xb') as out,stderr.open('xb') as err:
   process=subprocess.Popen(launch,stdin=subprocess.DEVNULL,stdout=out,stderr=err,start_new_session=True)
   finished=None;cancel_sent=False;producer_observed=None
   try:
    while time.monotonic()<case_end:
     if 'observed_results_gid' not in result:
      raw=Path('/run/appsurface-evidence-'+generation+'/raw-results')
      if raw.exists() and raw.stat().st_gid!=0:result['observed_results_gid']=raw.stat().st_gid
     descriptor_path=Path('/run/appsurface-evidence-'+generation+'/worker/worker-control.json')
     if producer_observed is None and descriptor_path.is_file():
      producer_observed=sample_producer(units[2],json.loads(descriptor_path.read_bytes()),case_end)
     entered=Path('/run/appsurface-evidence-producer-data-'+generation+'/scratch/tmp/checkpoint2-case-entered')
     if case=='cancel' and not cancel_sent and producer_observed is not None:
      if entered.is_file() and entered.read_bytes()==b'cancel':
       command(['/usr/bin/systemctl','kill','--kill-whom=main','--signal=INT',owner],case_end);cancel_sent=True
     code=process.poll()
     if code is not None:
      finished={'wrapper_exit':code};break
     require(stdout.stat().st_size+stderr.stat().st_size<=MAX_LOG,'root-output-bound')
     time.sleep(.1)
    require(finished is not None,'actual-root-terminal')
   finally:
    if process.poll() is None:
     for u in units:
      try:command(['/usr/bin/systemctl','stop',u],min(end,time.monotonic()+10),allowed=(0,1,5))
      except Exception:pass
     process.kill()
    process.wait(timeout=10)
  result['owner_terminal']=finished;result['cancel_sent']=cancel_sent;result['observed_producer']=producer_observed
  require(producer_observed is not None,'actual-producer-observed')
  if case!='normal':
   entered=Path('/run/appsurface-evidence-producer-data-'+generation+'/scratch/tmp/checkpoint2-case-entered')
   require(entered.is_file() and entered.read_bytes()==case.encode(),'actual-test-case-entered')
  require(stdout.is_file() and stderr.is_file(),'root-output-files')
  require(stdout.stat().st_size+stderr.stat().st_size<=MAX_LOG,'root-output-bound')
  for p in [stdout,stderr]:p.chmod(0o600)
  result['stdout_sha256']=sha(stdout);result['stderr_sha256']=sha(stderr)
  final=Path('/run/appsurface-evidence-'+generation+'/output/evidence/evidence-manifest.json')
  if case=='normal':
   require(finished['wrapper_exit']==0,'normal-root-exit')
   summary=json.loads(stdout.read_bytes());require(summary.get('ExecutionVerdict')=='Passed' and summary.get('CleanupCompleted') is True and summary.get('Mode')=='Observation' and summary.get('Eligibility')=='Informational' and summary.get('ClaimKind')=='ObservationOnly','actual-root-success')
   observation=read_output_observation(stderr.read_bytes());require(observation['quota_exceeded'] is False and observation['failure']=='None' and all(p['stdout_eof'] and p['stderr_eof'] and p['failure']=='None' for p in observation['pairs']),'actual-joined-output-accounting')
   result['output_observation']=observation
   require(final.is_file(),'actual-final-manifest')
   manifest=json.loads(final.read_bytes());producer=manifest['ProducerResults'][0]
   require(producer['Outcome']=='Passed' and producer['Artifacts'][0]['RelativePath']=='merged/Cobertura.xml','actual-producer-result')
   artifact=final.parent/'coverage/merged/Cobertura.xml'
   verify_sealed_output(final.parent)
   require(sha(artifact)==producer['Artifacts'][0]['Sha256'],'retained-artifact-digest')
   import xml.etree.ElementTree as ET
   coverage=ET.fromstring(artifact.read_bytes());require(int(coverage.attrib['lines-valid'])>0 and int(coverage.attrib['branches-valid'])>0,'real-coverage-positive-items')
   require(float(coverage.attrib['line-rate'])>=.945 and float(coverage.attrib['branch-rate'])>=.845,'actual-numeric-coverage')
   result['coverage']={k:coverage.attrib[k] for k in ['lines-valid','lines-covered','branches-valid','branches-covered','line-rate','branch-rate']}
  else:
   require(finished['wrapper_exit']!=0,'negative-root-exit')
   if final.exists():require(json.loads(final.read_bytes())['ExecutionVerdict']!='Passed','negative-no-passed-manifest')
   if case=='test-failure':
    require(final.is_file(),'test-failure-manifest')
    failed=json.loads(final.read_bytes())['ProducerResults']
    require(len(failed)==1 and failed[0]['Outcome']=='Failed' and failed[0]['Diagnostic']=='The restricted coverage process exited with code 1.','actual-test-failure-result')
   elif case=='cancel':require(cancel_sent,'actual-cancel-sent')
   elif case=='quota':
    observed=read_output_observation(stderr.read_bytes())
    require(observed['quota_exceeded'] is True and observed['received_bytes']>observed['received_byte_limit']==16*1024*1024,'actual-output-quota-crossed')
    result['output_observation']=observed
  require(groups_empty(units),'all-owned-groups-empty')
  result['groups_empty']=True
  # Observe both generated names and numeric IDs through retained root-sealed directory metadata/descriptor.
  descriptor=Path('/run/appsurface-evidence-'+generation+'/worker/worker-control.json')
  if descriptor.exists():
   d=json.loads(descriptor.read_bytes())
   identities=[('worker_uid','passwd',d['worker_uid']),('subject_uid','passwd',d['subject_uid']),('worker_gid','group',d['worker_gid']),('subject_gid','group',d['subject_gid'])]
   identities.append(('results_gid','group',result['observed_results_gid']))
   for key,table,number in identities:
    c=command(['/usr/bin/getent',table,str(number)],case_end,allowed=(0,2));require(c.returncode==2,'numeric-account-still-reserved')
   for prefix,table in [('evw','passwd'),('evs','passwd'),('evw','group'),('evs','group'),('evr','group')]:
    c=command(['/usr/bin/getent',table,prefix+generation[:28]],case_end,allowed=(0,2));require(c.returncode==2,'named-account-still-reserved')
   result['accounts_absent']=True
  else:raise RuntimeError('missing-retained-descriptor')
  result['passed']=True
 except Exception as e:
  result['failure_category']=str(e) if isinstance(e,RuntimeError) else type(e).__name__
 finally:
  # Root failure is never upgraded by fixture containment; retain paths/accounts on ambiguous custody.
  for u in units:
   try:command(['/usr/bin/systemctl','stop',u],min(end,time.monotonic()+15),allowed=(0,1,5))
   except Exception:pass
  result['final_groups_empty']=groups_empty(units)
  try:result['producer_diagnostics_retained']=retain_producer_prefixes(generation,args.output,case)
  except Exception:result['producer_diagnostics_retained']=False
  for n in ['owner.stdout','owner.stderr']:
   p=root/n
   if p.exists() and p.is_file() and p.stat().st_size<=MAX_LOG:shutil.copyfile(p,args.output/(case+'-'+n));(args.output/(case+'-'+n)).chmod(0o600)
  write(args.output/(case+'.json'),canonical(result))
 receipt['cases'].append(result)
 return result['passed']

def main():
 ap=argparse.ArgumentParser();ap.add_argument('--acquire-packages',type=Path);ap.add_argument('--package-cache',type=Path);ap.add_argument('--hash-runtime',type=Path);ap.add_argument('--package-hasher',type=Path)
 for n in ['tool','runtime','subject','output']:ap.add_argument('--'+n,type=Path)
 ap.add_argument('--source');ap.add_argument('--base');args=ap.parse_args()
 end=time.monotonic()+LIMIT;os.umask(0o077)
 if args.acquire_packages:
  print(json.dumps(acquire_packages(args.acquire_packages,args.package_cache,end,args.hash_runtime,args.package_hasher)));return 0
 require(os.geteuid()==0 and platform.machine()=='x86_64' and sys.platform=='linux','linux-root-x64')
 require(Path('/sys/fs/cgroup/cgroup.controllers').is_file(),'unified-cgroup-v2')
 require(command(['/usr/bin/systemctl','--version'],end).stdout.splitlines()[0].startswith(b'systemd 255'),'systemd-255')
 for path in ['/usr/bin/env','/usr/bin/systemd-run','/usr/bin/systemctl','/usr/bin/getent']:
  q=Path(path).resolve(strict=True);m=q.stat()
  require(m.st_uid==0 and m.st_mode&0o022==0 and q.read_bytes()[:4]==b'\x7fELF','trusted-os-bootstrap')

 args.output.mkdir(mode=0o700,parents=True,exist_ok=True)
 receipt={'schema':'issue779-csharp-checkpoint2-native-v1','source':args.source,'base':args.base,'exit':1,'cases':[],'native_acceptance':False,'trusted_eligibility':False}
 common=Path('/run')/('issue779-csharp-checkpoint2-input-'+uuid.uuid4().hex);create_traversable_root(common)
 args.staged_tool=common/'tool';args.staged_runtime=common/'runtime'
 inventory={'tool':copy_tree(args.tool,args.staged_tool),'runtime':copy_tree(args.runtime,args.staged_runtime)}
 write(args.staged_tool/'fixture-policy.json',canonical(policy()),0o644)
 inventory['tool']['fixture-policy.json']={'sha256':sha(args.staged_tool/'fixture-policy.json'),'bytes':(args.staged_tool/'fixture-policy.json').stat().st_size,'mode':'0444'}
 for p in [args.staged_tool,args.staged_runtime,*args.staged_tool.rglob('*'),*args.staged_runtime.rglob('*')]:
  os.chown(p,0,0,follow_symlinks=False);p.chmod(0o555 if p.is_dir() or p.stat().st_mode&0o111 else 0o444)
 write(args.output/'input-inventory.json',canonical(inventory))
 args.input_inventory_sha256=sha(args.output/'input-inventory.json')

 try:
  for case in ['normal','test-failure','cancel','quota']:
   if not execute_case(args,case,end,receipt):break
  receipt['exit']=0 if len(receipt['cases'])==4 and all(c['passed'] for c in receipt['cases']) else 1
 finally:write(args.output/'native-receipt.json',canonical(receipt))
 return receipt['exit']
if __name__=='__main__':raise SystemExit(main())
