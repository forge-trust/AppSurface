#!/usr/bin/env bash
# Unprivileged build/handoff only. Reviewed caller supplies the ORIGINAL absolute uptime end.
set -euo pipefail
umask 077
[[ $# == 6 && $1 == --execute ]] || { printf 'N07_HELPER_BUILD_REJECTED:arguments\n' >&2; exit 1; }
recipe=$(sha256sum -- "${BASH_SOURCE[0]}"); export N07_HELPER_RECIPE_SHA256=${recipe:0:64}
exec python3 -B - "$2" "$3" "$4" "$5" "$6" <<'PY'
import hashlib,json,os,pathlib,re,signal,stat,subprocess,sys,time
SOURCE,SDK,SDK_SHA,OUT,END=sys.argv[1:]
SOURCE,SDK,OUT=map(pathlib.Path,(SOURCE,SDK,OUT));END=int(END)
PINS={'Program.cs': '059246aa8acb0feb5c1172990661fc83cd1631f07ff34a1bfe8de736414a4a5d', 'PossibleStopRegistration.cs': '3c1e57f7030c2e2a1a54bfa2853744248bf3bad4cc71af5b94ca912a87d0d3af', 'N07CoordinatorData.cs': '6edf029c097f1e6bf902936d7418a7622d2654ad97483a88ea5f2ec10323679d', 'NativeRootCoordinator.csproj': '9ea64016a8932db2b62910d08a579236a8e3a2b20e2cd438a0b27bc92f5269d1', 'packages.lock.json': 'a29c6aa8cfb81874ff8bb78dc369d7416f28c9b8cc47e99592bfc019b20c41eb'}
RECORDS=[];FINAL=END;WORK=END-5000;RESULT={'schema':'issue779-n07-helper-build-handoff-v1','exit':1,'native_execution':False,'authority':False,'commands':RECORDS,'source_pins':PINS,'sdk_required':'10.0.401','runtime_required':'10.0.12','recipe_sha256':os.environ['N07_HELPER_RECIPE_SHA256']}
def now():
 b=pathlib.Path('/proc/uptime').read_bytes();assert len(b)<=128
 x=b.split(b' ',1)[0];assert re.fullmatch(rb'[0-9]+\.[0-9]+',x)
 a,f=x.split(b'.');return int(a)*1000+int((f+b'000')[:3])
def check(final=False):
 if now()>=(FINAL if final else WORK):raise TimeoutError('original-build-deadline')
def digest(b):return hashlib.sha256(b).hexdigest()
def identity(s):return(s.st_dev,s.st_ino,s.st_mode,s.st_uid,s.st_gid,s.st_nlink,s.st_size,s.st_mtime_ns,s.st_ctime_ns)
def read(p,cap):
 check();s=p.lstat();assert stat.S_ISREG(s.st_mode) and s.st_nlink==1 and s.st_size<=cap
 fd=os.open(p,os.O_RDONLY|os.O_NOFOLLOW|os.O_CLOEXEC);blocks=[];total=0
 try:
  assert identity(os.fstat(fd))==identity(s)
  while True:
   check();b=os.read(fd,65536)
   if not b:break
   total+=len(b);assert total<=s.st_size and total<=cap;blocks.append(b)
  assert total==s.st_size and identity(os.fstat(fd))==identity(s) and identity(p.lstat())==identity(s)
 finally:os.close(fd)
 return b''.join(blocks)
def absent(pid):
 try:os.killpg(pid,0);return False
 except ProcessLookupError:return True
def run(argv):
 proc=None;failure=None;forced=False;waited=False;group=None;stream=None
 log=OUT/('build-%02d.log'%len(RECORDS));size=None
 try:
  check();stream=log.open('xb');os.fchmod(stream.fileno(),0o600)
  proc=subprocess.Popen(argv,stdout=stream,stderr=subprocess.STDOUT,cwd=OUT/'project',start_new_session=True,env={**os.environ,'DOTNET_CLI_TELEMETRY_OPTOUT':'1','DOTNET_CLI_HOME':str(OUT/'dotnet-home'),'MSBUILDDISABLENODEREUSE':'1','DOTNET_CLI_USE_MSBUILD_SERVER':'0'})
  while True:
   check();assert log.stat().st_size<=8*1024*1024
   if proc.poll() is not None and absent(proc.pid):break
   time.sleep(.02);check()
  proc.wait(timeout=0);waited=True;assert proc.returncode==0
 except BaseException as e:failure=e
 finally:
  needs_stop=False
  if proc is not None:
   try:needs_stop=failure is not None or proc.poll() is None or not absent(proc.pid)
   except BaseException as e:failure=failure or e;needs_stop=True
   if needs_stop:
    forced=True
    try:os.killpg(proc.pid,signal.SIGKILL)
    except ProcessLookupError:pass
    except BaseException as e:failure=failure or e
   try:
    while True:
     check(final=True)
     try:proc.wait(timeout=min(.02,max(0,(FINAL-now())/1000)));waited=True;break
     except subprocess.TimeoutExpired:check(final=True)
   except BaseException as e:failure=failure or e
   try:
    while True:
     check(final=True);group=absent(proc.pid)
     if group:break
     time.sleep(.02);check(final=True)
   except BaseException as e:failure=failure or e;group=None
  if stream is not None:
   try:stream.close()
   except BaseException as e:failure=failure or e
  try:size=log.stat().st_size;assert 0<=size<=8*1024*1024
  except BaseException as e:failure=failure or e
  if proc is not None and (group is not True or not waited):failure=failure or ValueError('build-cleanup')
  try:check()
  except BaseException as e:failure=failure or e
  RECORDS.append({'exit':proc.returncode if proc else None,'waited':waited,'group_absent':group,'forced_cleanup':forced,'log':log.name,'log_bytes':size,'error':failure is not None})
 if failure is not None or forced:raise ValueError('build-command-rejected') from failure
 check();raw=read(log,8*1024*1024)
 if re.search(rb'(?:warning|error) [A-Z]+[0-9]+',raw):
  RECORDS[-1]['error']=True;raise ValueError('build-diagnostics')
 check();return raw
def inventory(root):
 dirs={};files={};total=0
 for here,children,names in os.walk(root,followlinks=False):
  check();p=pathlib.Path(here);s=p.lstat();assert stat.S_ISDIR(s.st_mode) and stat.S_IMODE(s.st_mode)==0o555
  rel=p.relative_to(root).as_posix();dirs[rel]={'mode':'0555'}
  for c in children:assert stat.S_ISDIR((p/c).lstat().st_mode)
  for n in names:
   q=p/n;rel=q.relative_to(root).as_posix();assert re.fullmatch(r'[A-Za-z0-9_.\-/]+',rel) and '..' not in rel.split('/') and len(rel.split('/'))<=8
   b=read(q,256*1024*1024);mode=stat.S_IMODE(q.lstat().st_mode);assert mode in (0o444,0o555);total+=len(b);assert total<=1024*1024*1024
   files[rel]={'mode':format(mode,'04o'),'bytes':len(b),'sha256':digest(b)}
  assert len(dirs)+len(files)<=8192
 return {'schema':'issue779-build-node-inventory-v1','root_name':'tool','directories':dict(sorted(dirs.items())),'files':dict(sorted(files.items()))}
assert sys.platform=='linux' and os.geteuid()!=0 and re.fullmatch(r'[0-9a-f]{64}',SDK_SHA)
assert all(p.is_absolute() and '..' not in p.parts for p in (SOURCE,SDK,OUT));assert not OUT.exists() and not OUT.is_symlink();check()
OUT.mkdir(mode=0o700);(OUT/'project').mkdir(mode=0o700)
try:
 assert digest(read(SDK,256*1024*1024))==SDK_SHA
 for n,h in PINS.items():
  b=read(SOURCE/n,65536);assert digest(b)==h;(OUT/'project'/n).write_bytes(b);(OUT/'project'/n).chmod(0o600)
 version=run([str(SDK),'--version']).decode().strip();assert version=='10.0.401'
 project=str(OUT/'project'/'NativeRootCoordinator.csproj');flags=['-p:UseAppHost=false','-p:SelfContained=false','-p:UseSharedCompilation=false','-p:RuntimeFrameworkVersion=10.0.12','-p:NuGetAudit=true','-p:ImportDirectoryBuildProps=false','-p:ImportDirectoryBuildTargets=false']
 run([str(SDK),'restore',project,'--locked-mode',*flags])
 run([str(SDK),'publish',project,'--no-restore','--configuration','Release','--output',str(OUT/'helper'),*flags])
 for n,h in PINS.items():assert digest(read(OUT/'project'/n,65536))==h and digest(read(SOURCE/n,65536))==h
 bundle=OUT/'helper';assert (bundle/'NativeRootCoordinator.dll').is_file() and not (bundle/'NativeRootCoordinator').exists()
 cfg=json.loads(read(bundle/'NativeRootCoordinator.runtimeconfig.json',65536));opts=cfg['runtimeOptions'];assert opts['tfm']=='net10.0' and opts['framework']=={'name':'Microsoft.NETCore.App','version':'10.0.12'}
 deps=json.loads(read(bundle/'NativeRootCoordinator.deps.json',1048576));assert deps['runtimeTarget']['name']=='.NETCoreApp,Version=v10.0'
 for p in sorted(bundle.rglob('*'),reverse=True):
  s=p.lstat();assert not p.is_symlink()
  if stat.S_ISDIR(s.st_mode):p.chmod(0o555)
  else:assert stat.S_ISREG(s.st_mode) and s.st_nlink==1;p.chmod(0o444)
 bundle.chmod(0o555);nodes=inventory(bundle);assert inventory(bundle)==nodes
 raw=json.dumps(nodes,sort_keys=True,separators=(',',':')).encode()+b'\n';(OUT/'helper-nodes.json').write_bytes(raw)
 rows=''.join(f"{f['mode']}\t{f['sha256']}\t{n}\n" for n,f in nodes['files'].items()).encode();(OUT/'helper.tsv').write_bytes(rows)
 RESULT.update(exit=0,helper_nodes_sha256=digest(raw),helper_tsv_sha256=digest(rows),helper_entry_sha256=nodes['files']['NativeRootCoordinator.dll']['sha256'],helper_files=len(nodes['files']),helper_directories=len(nodes['directories']),helper_bytes=sum(f['bytes'] for f in nodes['files'].values()),helper_root=str(bundle),sdk_sha256=SDK_SHA)
except BaseException:RESULT['failure']='helper-build-or-handoff-rejected'
def invalidate_publication():
 try:
  fd=os.open(OUT/'late-helper-publication-failure.json',os.O_WRONLY|os.O_CREAT|os.O_EXCL|os.O_NOFOLLOW|os.O_CLOEXEC,0o600)
  try:
   raw=b'{"exit":1,"failure":"late-helper-publication"}\n'
   if os.write(fd,raw)!=len(raw):raise OSError('short-invalidation')
  finally:os.close(fd)
 except BaseException:pass
 try:os.unlink(OUT/'helper-build-receipt.json')
 except BaseException:pass

def publish_result():
 try:
  check()
  if RESULT['exit']==0:
   assert len(RECORDS)==3 and all(type(r['exit']) is int and r['exit']==0 and r['waited'] is True and r['group_absent'] is True and r['forced_cleanup'] is False and r['error'] is False for r in RECORDS)
  raw=json.dumps(RESULT,sort_keys=True,separators=(',',':')).encode()+b'\n'
  assert len(raw)<=1048576
  pending=OUT/'helper-build-receipt.pending.json'
  fd=os.open(pending,os.O_WRONLY|os.O_CREAT|os.O_EXCL|os.O_NOFOLLOW|os.O_CLOEXEC,0o600)
  try:
   if os.write(fd,raw)!=len(raw):raise OSError('short-publication')
   os.fsync(fd)
  finally:os.close(fd)
  check();os.rename(pending,OUT/'helper-build-receipt.json');check()
  print(json.dumps({'exit':RESULT['exit'],'receipt_sha256':digest(raw),'native_execution':False}),flush=True)
  check()
  return RESULT['exit']
 except BaseException:
  RESULT['exit']=1;invalidate_publication();raise

raise SystemExit(publish_result())
PY
