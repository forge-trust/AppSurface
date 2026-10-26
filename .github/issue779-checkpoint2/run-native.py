"""Finite Linux checkpoint-two consumer controls. Python prepares/observes; C# owns supervision."""
from pathlib import Path
import argparse,base64,datetime,hashlib,json,os,platform,re,shutil,stat,subprocess,sys,tempfile,time,urllib.request,uuid,zipfile

LIMIT=1800
MAX_LOG=4*1024*1024
MAX_CGROUP_PROCS_BYTES=4096
MAX_CGROUP_PROCESSES=32
MAX_PROCESS_THREADS=8192
MAX_TOTAL_THREADS=8192
PROCESS_SAMPLE_SECONDS=.015
ENV_NAMES=['DOTNET_STARTUP_HOOKS','DOTNET_ADDITIONAL_DEPS','DOTNET_SHARED_STORE','DOTNET_ROOT','DOTNET_ROOT_X64','DOTNET_HOST_PATH','DOTNET_ROLL_FORWARD','DOTNET_ROLL_FORWARD_TO_PRERELEASE','DOTNET_MULTILEVEL_LOOKUP','CORECLR_ENABLE_PROFILING','CORECLR_PROFILER','CORECLR_PROFILER_PATH','CORECLR_PROFILER_PATH_64','COR_ENABLE_PROFILING','COR_PROFILER','COR_PROFILER_PATH','COMPlus_ReadyToRun','COMPlus_ZapDisable']
FIXED=['PATH=/usr/bin:/bin','HOME=/nonexistent','LANG=C.UTF-8','DOTNET_CLI_TELEMETRY_OPTOUT=1','DOTNET_CLI_HOME=/tmp','XDG_RUNTIME_DIR=/tmp']

def require(ok,code):
 if not ok:raise RuntimeError(code)
def sha(p):return hashlib.sha256(p.read_bytes()).hexdigest()
def canonical(v):return json.dumps(v,sort_keys=True,separators=(',',':')).encode()
def require_quota_disposition(root_stdout,manifest):
 """Require no eligible claim after the actual quota case; detached data grants no authority.

 The caller has already observed original quota overflow and will still require
 group, pump and account settlement. A missing manifest is allowed; an existing
 failure manifest must explicitly carry no claim, no eligibility and no passing
 producer assertion. No private output or supplied diagnostic is echoed.
 """
 require(type(root_stdout) is bytes and root_stdout==b'','quota-no-root-result')
 if manifest is None:
  return {'manifest_present':False,'root_result_present':False,'execution_verdict':None,
          'claim_kind':None,'eligibility':None,'eligible_result':False}
 require(type(manifest) is dict and manifest.get('Mode')=='Observation'
         and manifest.get('ExecutionVerdict') in ('Incomplete','Invalid')
         and manifest.get('ClaimKind')=='None' and manifest.get('Eligibility')=='None',
         'quota-no-eligible-manifest')
 results=manifest.get('ProducerResults')
 require(type(results) is list and len(results)<=1,'quota-failed-producer-set')
 for result in results:
  require(type(result) is dict and result.get('Outcome') in
          ('Failed','Flaky','TimedOut','Unavailable','Cancelled','Invalid')
          and result.get('SatisfiedAssertionIds')==[],'quota-no-passing-producer-assertion')
 return {'manifest_present':True,'root_result_present':False,
         'execution_verdict':manifest['ExecutionVerdict'],'claim_kind':manifest['ClaimKind'],
         'eligibility':manifest['Eligibility'],'eligible_result':False}

def sealed_descriptor(path,expected_schema="evidence-worker-linux-v1",*,after_custody=False):
 """Observe publication data, or the root-only terminal seal after physical settlement; never authenticate."""
 require(expected_schema in ("evidence-worker-linux-v1","evidence-worker-linux-v2"),"actual-descriptor-expected-schema")
 require(type(after_custody) is bool,"actual-descriptor-phase")
 try:fd=os.open(path,os.O_RDONLY|os.O_NOFOLLOW|os.O_NONBLOCK|os.O_CLOEXEC)
 except FileNotFoundError:return None
 try:
  before=os.fstat(fd)
  require(stat.S_ISREG(before.st_mode) and before.st_nlink==1 and before.st_uid==os.geteuid() and before.st_size<=65536,'actual-descriptor-node')
  mode=stat.S_IMODE(before.st_mode)
  if mode in (0o600,0o640):return None # Root is still writing/fsyncing before its existing 0440 seal.
  terminal_seal=after_custody and mode==0o400 and before.st_uid==before.st_gid==0
  require((mode==0o440 or terminal_seal) and before.st_size>0,'actual-descriptor-seal')
  data=os.read(fd,65537);after=os.fstat(fd);named=path.lstat()
  identity=lambda value:(value.st_dev,value.st_ino,value.st_mode,value.st_nlink,value.st_uid,value.st_gid,value.st_size,value.st_mtime_ns,value.st_ctime_ns)
  require(identity(before)==identity(after)==identity(named) and len(data)==before.st_size,'actual-descriptor-snapshot')
  value=json.loads(data)
  require(type(value) is dict and value.get('schema')==expected_schema,'actual-descriptor-schema')
  return value
 finally:os.close(fd)

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

def read_mutation_observation(raw):
 events=[]
 for line in raw.splitlines():
  try:event=json.loads(line)
  except (ValueError,UnicodeError):continue
  if isinstance(event,dict) and event.get('schema')=='evidence-c2-artifact-mutation-v1':events.append(event)
 require(len(events)==2 and [e.get('phase') for e in events]==['mutated','read-rejected'],'actual-retention-mutation-and-rejection')
 mutation,rejection=events
 require(set(mutation)=={'schema','phase','original_sha256','changed_sha256','length','device_major','device_minor','inode'},'closed-mutation-observation')
 require(all(type(mutation.get(k)) is int and mutation[k]>=0 for k in ['device_major','device_minor','inode','length']) and mutation['inode']>0 and 0<mutation['length']<=1024*1024,'actual-retained-mutation-metadata')
 require(all(isinstance(mutation.get(k),str) and re.fullmatch('[0-9a-f]{64}',mutation[k]) for k in ['original_sha256','changed_sha256']) and mutation['original_sha256']!=mutation['changed_sha256'],'actual-retained-byte-change')
 require(type(rejection.get('returned_bytes')) is int and rejection=={'schema':'evidence-c2-artifact-mutation-v1','phase':'read-rejected','returned_bytes':0},'actual-zero-bytes-on-rejected-read')
 return events

def sample_producer(unit,descriptor,end,pending=None):
 facts=show(unit,end);pid=int(facts.get('MainPID','0'))
 if not pid:return None
 try:
  process=Path('/proc')/str(pid)
  before=(process/'stat').read_text().rsplit(')',1)[1].split()[19]
  fields=dict(line.split(':',1) for line in (process/'status').read_text().splitlines() if ':' in line)
  uids=[int(x) for x in fields['Uid'].split()];gids=[int(x) for x in fields['Gid'].split()]
  groups=(process/'cgroup').read_text().splitlines()
  after=(process/'stat').read_text().rsplit(')',1)[1].split()[19]
 except FileNotFoundError:return None
 matches=(uids==[descriptor['subject_uid']]*4 and gids==[descriptor['subject_gid']]*4
  and groups==['0::/system.slice/'+unit] and before==after
  and facts['User']==str(descriptor['subject_uid']) and facts['Group']==str(descriptor['subject_gid']))
 if not matches:
  if pending is not None:pending.update({'pid':pid,'uids':uids,'gids':gids,'cgroup_matches':groups==['0::/system.slice/'+unit],'same_starttime':before==after})
  return None # Never acceptance: keep bounded observation pending until actual credentials bind.
 final=show(unit,end)
 if final.get('MainPID')!=str(pid) or final.get('ExecMainPID')!=str(pid):return None
 return {'pid':pid,'uid':uids[0],'gid':gids[0],'starttime_ticks':int(before),'cgroup':groups[0][3:]}

def parse_cgroup_pids(raw):
 if len(raw)>MAX_CGROUP_PROCS_BYTES:return None
 try:lines=raw.decode('ascii').splitlines()
 except UnicodeDecodeError:return None
 if not lines or len(lines)>MAX_CGROUP_PROCESSES:return None
 if any(not line.isdecimal() or len(line)>10 or int(line)==0 for line in lines):return None
 pids=[int(line) for line in lines]
 return tuple(pids) if len(set(pids))==len(pids) else None

def parse_proc_starttime(raw,pid):
 try:
  text=raw.decode('ascii');prefix,suffix=text.rsplit(')',1);head,delimiter,comm=prefix.partition(' (');fields=suffix.split()
  if head!=str(pid) or delimiter!=' (' or len(comm)>256 or not suffix.startswith(' ') or len(fields)<=19 or not fields[19].isdecimal():return None
  return int(fields[19])
 except (UnicodeDecodeError,ValueError):return None

def parse_proc_status(raw,expected_uid,expected_gid):
 try:fields=dict(line.split(':',1) for line in raw.decode('ascii').splitlines() if ':' in line)
 except UnicodeDecodeError:return None
 try:
  uids=[int(v) for v in fields['Uid'].split()];gids=[int(v) for v in fields['Gid'].split()]
  threads=int(fields['Threads'])
 except (KeyError,ValueError):return None
 if uids!=[expected_uid]*4 or gids!=[expected_gid]*4:return None
 if not 1<=threads<=MAX_PROCESS_THREADS:return None
 return threads

def process_role(raw):
 roles={b'dotnet.dll':'dotnet.dll',b'MSBuild.dll':'MSBuild.dll',b'VBCSCompiler.dll':'VBCSCompiler.dll',
  b'csc.dll':'csc.dll',b'vstest.console.dll':'vstest.console.dll',b'datacollector.dll':'datacollector.dll',b'testhost.dll':'testhost.dll'}
 for arg in raw.split(b'\0'):
  basename=arg.rsplit(b'/',1)[-1]
  if basename in roles:return roles[basename]
 return 'other'

def _bounded_proc_read(path,limit,deadline):
 if time.monotonic()>=deadline:return None
 with path.open('rb') as source:raw=source.read(limit+1)
 if len(raw)>limit:return None
 return raw

def sample_process_tree(unit,identity,end):
 if not isinstance(identity,dict) or type(identity.get('uid')) is not int or type(identity.get('gid')) is not int:return None
 uid=identity['uid'];gid=identity['gid'];selected_pid=identity.get('pid')
 if type(selected_pid) is not int or identity.get('cgroup')!='/system.slice/'+unit:return None
 started=time.monotonic();deadline=min(end,started+PROCESS_SAMPLE_SECONDS) if end is not None else started+PROCESS_SAMPLE_SECONDS
 if started>=deadline:return None
 root=Path('/sys/fs/cgroup/system.slice')/unit
 raw=_bounded_proc_read(root/'cgroup.procs',MAX_CGROUP_PROCS_BYTES,deadline)
 pids=parse_cgroup_pids(raw) if raw is not None else None
 if pids is None or selected_pid not in pids:return None
 rows=[]
 for pid in pids:
  if time.monotonic()>=deadline:return None
  process=Path('/proc')/str(pid)
  before=_bounded_proc_read(process/'stat',4096,deadline)
  status=_bounded_proc_read(process/'status',8192,deadline)
  cgroup=_bounded_proc_read(process/'cgroup',4096,deadline)
  cmdline=_bounded_proc_read(process/'cmdline',4096,deadline)
  after=_bounded_proc_read(process/'stat',4096,deadline)
  if any(value is None for value in (before,status,cgroup,cmdline,after)):return None
  start_before=parse_proc_starttime(before,pid);start_after=parse_proc_starttime(after,pid)
  threads=parse_proc_status(status,uid,gid)
  try:groups=cgroup.decode('ascii').splitlines()
  except UnicodeDecodeError:return None
  if start_before is None or start_before!=start_after or threads is None or groups!=['0::/system.slice/'+unit]:return None
  rows.append({'pid':pid,'starttime_ticks':start_before,'threads':threads,'role':process_role(cmdline)})
 if time.monotonic()>=deadline:return None
 final=_bounded_proc_read(root/'cgroup.procs',MAX_CGROUP_PROCS_BYTES,deadline)
 final_pids=parse_cgroup_pids(final) if final is not None else None
 if final_pids is None or set(final_pids)!=set(pids):return None
 total=sum(row['threads'] for row in rows)
 if total>MAX_TOTAL_THREADS:return None
 by_role={}
 for row in rows:by_role[row['role']]=by_role.get(row['role'],0)+row['threads']
 return {'total_threads':total,'by_role_threads':by_role,'processes':rows}

def _record_process_facts(unit,samples,identity,end):
 state=samples.setdefault('process_facts',{'count':0,'last_total_threads':None,'peak_total_threads':None,
  'last_by_role_threads':{},'peak_by_role_threads':{},'peak_snapshot':None,'last_sample_available':False})
 state['last_sample_available']=False;state['last_total_threads']=None;state['last_by_role_threads']={}
 try:facts=sample_process_tree(unit,identity,end)
 except Exception:facts=None # Diagnostics are best-effort and never control actual execution.
 if facts is None:return
 state['count']+=1;state['last_sample_available']=True;state['last_total_threads']=facts['total_threads']
 state['last_by_role_threads']=facts['by_role_threads']
 for role,value in facts['by_role_threads'].items():
  state['peak_by_role_threads'][role]=max(state['peak_by_role_threads'].get(role,0),value)
 if state['peak_total_threads'] is None or facts['total_threads']>state['peak_total_threads']:
  state['peak_total_threads']=facts['total_threads'];state['peak_snapshot']=facts['processes']

def read_subject_marker(path,uid,gid,limit,permissions=0o600):
 try:
  fd=os.open(path,os.O_RDONLY|os.O_NOFOLLOW|os.O_CLOEXEC|os.O_NONBLOCK)
  try:
   before=os.fstat(fd)
   if not stat.S_ISREG(before.st_mode) or before.st_nlink!=1 or before.st_uid!=uid or before.st_gid!=gid or stat.S_IMODE(before.st_mode)!=permissions or not 0<before.st_size<=limit:return None
   data=os.read(fd,limit+1);after=os.fstat(fd);named=path.lstat()
   signature=lambda v:(v.st_dev,v.st_ino,v.st_size,v.st_mode,v.st_uid,v.st_gid,v.st_nlink,v.st_mtime_ns,v.st_ctime_ns)
   if len(data)!=before.st_size or signature(before)!=signature(after) or signature(after)!=signature(named):return None
   return data
  finally:os.close(fd)
 except OSError:return None

def sample_ignoring_descendant(unit,producer,scratch):
 uid=producer['uid'];gid=producer['gid']
 if read_subject_marker(scratch/'checkpoint2-child-ready',uid,gid,64)!=b'SIGTERM handler installed' or read_subject_marker(scratch/'checkpoint2-case-entered',uid,gid,32)!=b'ignored-cancel':return None
 raw=read_subject_marker(scratch/'checkpoint2-ignored-child-pid',uid,gid,10)
 if raw is None or not raw.isdigit():return None
 pid=int(raw)
 if pid<=0 or pid==producer['pid']:return None
 process=Path('/proc')/str(pid)
 try:
  deadline=time.monotonic()+PROCESS_SAMPLE_SECONDS
  before=_bounded_proc_read(process/'stat',4096,deadline);status=_bounded_proc_read(process/'status',8192,deadline);group=_bounded_proc_read(process/'cgroup',4096,deadline);after=_bounded_proc_read(process/'stat',4096,deadline)
 except FileNotFoundError:return None
 if any(x is None for x in (before,status,group,after)):return None
 start=parse_proc_starttime(before,pid)
 if start is None or start!=parse_proc_starttime(after,pid) or parse_proc_status(status,uid,gid) is None or group!=('0::/system.slice/'+unit+'\n').encode():return None
 return {'pid':pid,'uid':uid,'gid':gid,'starttime_ticks':start,'cgroup':'/system.slice/'+unit}

def sample_cgroup_counters(unit,samples,producer_identity=None,end=None):
 root=Path('/sys/fs/cgroup/system.slice')/unit
 fields={}
 for name in ['pids.current','pids.max','pids.events','memory.current','memory.max','memory.events']:
  try:
   raw=(root/name).read_bytes();require(len(raw)<=4096,'cgroup-counter-bound')
  except FileNotFoundError:continue
  if name.endswith('.events'):
   values={}
   for line in raw.decode('ascii').splitlines():
    key,value=line.split();require(re.fullmatch(r'[a-z_]{1,32}',key) and value.isdecimal(),'cgroup-event-shape');values[key]=int(value)
   fields[name]=values
  else:
   value=raw.strip();require(value==b'max' or value.isdigit(),'cgroup-counter-shape');fields[name]=None if value==b'max' else int(value)
 if fields:
  samples['count']=samples.get('count',0)+1;samples['last']=fields
  for name in ['pids.current','memory.current']:
   if type(fields.get(name)) is int:samples[name+'.peak']=max(samples.get(name+'.peak',0),fields[name])
  for name in ['pids.events','memory.events']:
   for key,value in fields.get(name,{}).items():samples[name+'.'+key]=max(samples.get(name+'.'+key,0),value)
 if producer_identity is not None:_record_process_facts(unit,samples,producer_identity,end)

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

def retain_producer_prefixes(generation,output,case,role='producer'):
 require(role in ('producer','worker'),'fixed-diagnostic-role')
 private=Path('/run/appsurface-evidence-'+role+'-diagnostics-'+generation)
 if private.exists():
  before=private.lstat();require(stat.S_ISDIR(before.st_mode) and before.st_uid==before.st_gid==0 and stat.S_IMODE(before.st_mode)==0o700,'private-producer-diagnostic-root')
  rootfd=os.open(private,os.O_RDONLY|os.O_DIRECTORY|os.O_NOFOLLOW|os.O_CLOEXEC)
  try:
   require((os.fstat(rootfd).st_dev,os.fstat(rootfd).st_ino)==(before.st_dev,before.st_ino),'private-producer-diagnostic-identity')
   for name in ['stdout.prefix','stderr.prefix','state.json']:
    fd=os.open(name,os.O_RDONLY|os.O_NOFOLLOW|os.O_CLOEXEC,dir_fd=rootfd)
    try:
     m=os.fstat(fd);require(stat.S_ISREG(m.st_mode) and m.st_uid==m.st_gid==0 and m.st_nlink==1 and stat.S_IMODE(m.st_mode)==0o600 and m.st_size<=1024*1024,'private-producer-diagnostic-file')
     data=os.read(fd,1024*1024+1);require(len(data)==m.st_size and not os.read(fd,1),'private-producer-diagnostic-bytes')
     after=os.fstat(fd);require((m.st_dev,m.st_ino,m.st_size,m.st_mtime_ns,m.st_ctime_ns)==(after.st_dev,after.st_ino,after.st_size,after.st_mtime_ns,after.st_ctime_ns),'private-producer-diagnostic-stability')
     write(output/(case+'-'+role+'-'+name),data)
    finally:os.close(fd)
  finally:os.close(rootfd)
  return True
 return False

def inspect_failed_workspace(root,subject_uid,subject_gid,end):
 # Diagnostics only, after original root return and independently empty selected groups.
 deadline=min(end,time.monotonic()+5);total=0;types={};violations=[];error=None
 flags=os.O_RDONLY|os.O_DIRECTORY|os.O_NOFOLLOW|os.O_CLOEXEC
 def walk(fd,relative,depth):
  nonlocal total
  require(depth<=32 and time.monotonic()<deadline,'diagnostic-workspace-bound')
  with os.scandir(fd) as source:
   names=[]
   for entry in source:
    total+=1;require(total<=8192 and time.monotonic()<deadline,'diagnostic-workspace-bound');names.append(entry.name)
  for name in sorted(names):
   require(time.monotonic()<deadline,'diagnostic-workspace-bound')
   value=os.stat(name,dir_fd=fd,follow_symlinks=False);kind=stat.S_IFMT(value.st_mode);mode=stat.S_IMODE(value.st_mode)
   label={stat.S_IFDIR:'directory',stat.S_IFREG:'regular',stat.S_IFSOCK:'socket',stat.S_IFIFO:'fifo',stat.S_IFLNK:'symlink'}.get(kind,'other')
   types[label]=types.get(label,0)+1
   reason='type' if kind not in (stat.S_IFDIR,stat.S_IFREG) else 'owner' if (value.st_uid,value.st_gid) not in ((0,0),(subject_uid,subject_gid)) else 'mode' if mode&0o7022 else 'links' if kind==stat.S_IFREG and value.st_nlink!=1 else 'size' if kind==stat.S_IFREG and value.st_size>256*1024*1024 else None
   path=relative+'/'+name if relative else name
   if reason is not None and len(violations)<64:violations.append({'path':path,'kind':label,'mode':format(mode,'04o'),'uid':value.st_uid,'gid':value.st_gid,'links':value.st_nlink,'bytes':value.st_size,'reason':reason})
   if kind==stat.S_IFDIR:
    child=os.open(name,flags,dir_fd=fd)
    try:
     opened=os.fstat(child);require((opened.st_dev,opened.st_ino)==(value.st_dev,value.st_ino),'diagnostic-workspace-identity');walk(child,path,depth+1)
    finally:os.close(child)
 try:
  fd=os.open(root,flags)
  try:walk(fd,'',0)
  finally:os.close(fd)
 except (OSError,RuntimeError) as failure:error=type(failure).__name__
 return {'schema':'issue779-producer-workspace-inspection-v1','nodes':total,'types':types,'violations':violations,'error_kind':error,'native_authority':False}

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
  tool=args.staged_mutation_tool if case=='artifact-mutation' else args.staged_tool;runtime=args.staged_runtime
  result['input_inventory_sha256']=args.input_inventory_sha256
  result['subject_inventory']=copy_tree(args.subject,subject)
  # Scenario selection is fixture data chosen by the root before immutable subject audit, never runtime authority.
  scenario=subject/'Scenario.cs';require(scenario.is_file(),'fixture-scenario-source')
  scenario_kind="normal" if case=="artifact-mutation" else case
  scenario.write_text('namespace NativePreparation.Subject; public static class Scenario { public static string Kind => "'+scenario_kind+'"; }\n');scenario.chmod(0o644)
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
   finished=None;cancel_sent=False;producer_observed=None;ignored_child=None
   try:
    while time.monotonic()<case_end:
     if 'observed_results_gid' not in result:
      raw=Path('/run/appsurface-evidence-'+generation+'/raw-results')
      if raw.exists() and raw.stat().st_gid!=0:result['observed_results_gid']=raw.stat().st_gid
     descriptor_path=Path('/run/appsurface-evidence-'+generation+'/worker/worker-control.json')
     if producer_observed is None:
      published=sealed_descriptor(descriptor_path)
      if published is not None:
       producer_observed=sample_producer(units[2],published,case_end,result.setdefault('pending_producer_identity',{}))
     if producer_observed is not None:
      try:sample_cgroup_counters(units[2],result.setdefault('producer_cgroup_counters',{}),producer_observed,case_end)
      except (OSError,ValueError,RuntimeError):result['counter_sample_failed']=True
     entered=Path('/run/appsurface-evidence-producer-data-'+generation+'/scratch/tmp/checkpoint2-case-entered')
     if case=='ignored-cancel' and ignored_child is None and producer_observed is not None:
      ignored_child=sample_ignoring_descendant(units[2],producer_observed,entered.parent)
     if case in ('cancel','ignored-cancel') and not cancel_sent and producer_observed is not None:
      if entered.is_file() and entered.read_bytes()==case.encode() and (case!='ignored-cancel' or ignored_child is not None):
       if case=='ignored-cancel':require(not os.path.lexists(entered.parent/'checkpoint2-term-observed'),'no-preexisting-term-marker')
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
  result['owner_terminal']=finished;result['cancel_sent']=cancel_sent;result['observed_producer']=producer_observed;result['observed_ignoring_child']=ignored_child
  require(producer_observed is not None,'actual-producer-observed')
  if case not in ('normal','artifact-mutation'):
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
   require(producer['Outcome']=='Passed' and producer['Artifacts'][0]['RelativePath']=='merged/coverage.cobertura.xml','actual-producer-result')
   artifact=final.parent/'coverage/merged/coverage.cobertura.xml'
   verify_sealed_output(final.parent)
   require(sha(artifact)==producer['Artifacts'][0]['Sha256'],'retained-artifact-digest')
   import xml.etree.ElementTree as ET
   coverage=ET.fromstring(artifact.read_bytes());require(int(coverage.attrib['lines-valid'])>0 and int(coverage.attrib['branches-valid'])>0,'real-coverage-positive-items')
   require(float(coverage.attrib['line-rate'])>=.945 and float(coverage.attrib['branch-rate'])>=.845,'actual-numeric-coverage')
   result['coverage']={k:coverage.attrib[k] for k in ['lines-valid','lines-covered','branches-valid','branches-covered','line-rate','branch-rate']}
  else:
   require(finished['wrapper_exit']!=0,'negative-root-exit')
   if final.exists() and case!='cleanup-failure':require(json.loads(final.read_bytes())['ExecutionVerdict']!='Passed','negative-no-passed-manifest')
   if case=='test-failure':
    require(final.is_file(),'test-failure-manifest')
    failed=json.loads(final.read_bytes())['ProducerResults']
    require(len(failed)==1 and failed[0]['Outcome']=='Failed' and failed[0]['Diagnostic']=='The restricted coverage process exited with code 1.','actual-test-failure-result')
   elif case in ('cancel','ignored-cancel'):
    require(cancel_sent,'actual-cancel-sent')
    if case=='ignored-cancel':
     require(ignored_child is not None,'actual-ignoring-child-observed')
     term=Path('/run/appsurface-evidence-producer-data-'+generation+'/scratch/tmp/checkpoint2-term-observed')
     metadata=term.lstat() if os.path.lexists(term) else None
     require(metadata is not None,'actual-descendant-term-marker-present')
     # Producer-workspace sealing makes mutable files root:root0600.
     require(read_subject_marker(term,metadata.st_uid,metadata.st_gid,64,permissions=0o600)==b'SIGTERM observed' and metadata.st_uid==metadata.st_gid==0,'actual-descendant-ignored-term')
     result['descendant_term_observed']=True
    observed=read_output_observation(stderr.read_bytes())
    require(all(p['stdout_eof'] and p['stderr_eof'] and p['failure']=='None' for p in observed['pairs']),'cancel-original-output-joined')
    result['output_observation']=observed
   elif case=='artifact-mutation':
    result['artifact_mutation']=read_mutation_observation(stderr.read_bytes())
   elif case=='cleanup-failure':
    require(final.is_file() and json.loads(final.read_bytes())['ExecutionVerdict']=='Passed','cleanup-failed-after-passing-procedure')
    require(not stdout.read_bytes(),'cleanup-no-successful-root-receipt')
    links=list(Path('/run/appsurface-evidence-producer-data-'+generation+'/scratch/tmp').glob('checkpoint2-cleanup-link-*'))
    require(len(links)==1 and links[0].is_symlink(),'actual-unsafe-scratch-quarantined')
    result['quarantined']=True
   elif case=='quota':
    observed=read_output_observation(stderr.read_bytes())
    require(observed['quota_exceeded'] is True and observed['received_bytes']>observed['received_byte_limit']==16*1024*1024,'actual-output-quota-crossed')
    result['output_observation']=observed
    result['quota_disposition']=require_quota_disposition(stdout.read_bytes(),
     json.loads(final.read_bytes()) if final.exists() else None)
  require(groups_empty(units),'all-owned-groups-empty')
  result['groups_empty']=True
  # Observe both generated names and numeric IDs through retained root-sealed directory metadata/descriptor.
  descriptor=Path('/run/appsurface-evidence-'+generation+'/worker/worker-control.json')
  d=sealed_descriptor(descriptor,after_custody=True)
  if d is not None:
   identities=[('worker_uid','passwd',d['worker_uid']),('subject_uid','passwd',d['subject_uid']),('worker_gid','group',d['worker_gid']),('subject_gid','group',d['subject_gid'])]
   identities.append(('results_gid','group',result['observed_results_gid']))
   for key,table,number in identities:
    c=command(['/usr/bin/getent',table,str(number)],case_end,allowed=(0,2));require(c.returncode==(0 if case=='cleanup-failure' else 2),'numeric-account-custody')
   for prefix,table in [('evw','passwd'),('evs','passwd'),('evw','group'),('evs','group'),('evr','group')]:
    c=command(['/usr/bin/getent',table,prefix+generation[:28]],case_end,allowed=(0,2));require(c.returncode==(0 if case=='cleanup-failure' else 2),'named-account-custody')
   result['accounts_absent']=case!='cleanup-failure';result['accounts_quarantined']=case=='cleanup-failure'
  else:raise RuntimeError('missing-retained-descriptor')
  result['passed']=True
 except Exception as e:
  result['failure_category']=str(e) if isinstance(e,RuntimeError) else type(e).__name__
  try:
   descriptor=Path('/run/appsurface-evidence-'+generation+'/worker/worker-control.json')
   identity=sealed_descriptor(descriptor)
   if groups_empty(units) and identity is not None:
    diagnostic=inspect_failed_workspace(Path('/run/appsurface-evidence-producer-data-'+generation),identity['subject_uid'],identity['subject_gid'],case_end)
    write(args.output/(case+'-workspace-inspection.json'),canonical(diagnostic))
  except Exception:pass # A diagnostic failure cannot replace the actual failed control.
 finally:
  # Root failure is never upgraded by fixture containment; retain paths/accounts on ambiguous custody.
  for u in units:
   try:command(['/usr/bin/systemctl','stop',u],min(end,time.monotonic()+15),allowed=(0,1,5))
   except Exception:pass
  result['final_groups_empty']=groups_empty(units)
  try:result['producer_diagnostics_retained']=retain_producer_prefixes(generation,args.output,case)
  except Exception:result['producer_diagnostics_retained']=False
  try:result['worker_diagnostics_retained']=retain_producer_prefixes(generation,args.output,case,'worker')
  except Exception:result['worker_diagnostics_retained']=False
  for n in ['owner.stdout','owner.stderr']:
   p=root/n
   if p.exists() and p.is_file() and p.stat().st_size<=MAX_LOG:shutil.copyfile(p,args.output/(case+'-'+n));(args.output/(case+'-'+n)).chmod(0o600)
  write(args.output/(case+'.json'),canonical(result))
 receipt['cases'].append(result)
 return result['passed']

def main():
 ap=argparse.ArgumentParser();ap.add_argument('--acquire-packages',type=Path);ap.add_argument('--package-cache',type=Path);ap.add_argument('--hash-runtime',type=Path);ap.add_argument('--package-hasher',type=Path)
 for n in ['tool','runtime','subject','output','mutation-tool']:ap.add_argument('--'+n,type=Path)
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
 require(args.mutation_tool is not None,'compiled-c2-image-required')
 args.staged_tool=common/'tool';args.staged_runtime=common/'runtime';args.staged_mutation_tool=common/'mutation-tool'
 inventory={'tool':copy_tree(args.tool,args.staged_tool),'runtime':copy_tree(args.runtime,args.staged_runtime),'mutation_tool':copy_tree(args.mutation_tool,args.staged_mutation_tool)}
 for name,tool_path in [('tool',args.staged_tool),('mutation_tool',args.staged_mutation_tool)]:
  write(tool_path/'fixture-policy.json',canonical(policy()),0o644)
  inventory[name]['fixture-policy.json']={'sha256':sha(tool_path/'fixture-policy.json'),'bytes':(tool_path/'fixture-policy.json').stat().st_size,'mode':'0444'}
 for tree in [args.staged_tool,args.staged_mutation_tool,args.staged_runtime]:
  for p in [tree,*tree.rglob('*')]:
   os.chown(p,0,0,follow_symlinks=False);p.chmod(0o555 if p.is_dir() or p.stat().st_mode&0o111 else 0o444)
 write(args.output/'input-inventory.json',canonical(inventory))
 args.input_inventory_sha256=sha(args.output/'input-inventory.json')

 try:
  for case in ['normal','test-failure','cancel','quota','ignored-cancel','artifact-mutation','cleanup-failure']:
   if not execute_case(args,case,end,receipt):break
  receipt['exit']=0 if len(receipt['cases'])==7 and all(c['passed'] for c in receipt['cases']) else 1
 finally:write(args.output/'native-receipt.json',canonical(receipt))
 return receipt['exit']
if __name__=='__main__':raise SystemExit(main())
