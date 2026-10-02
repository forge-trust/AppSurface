#!/usr/bin/env python3
"""Two finite kernel controls for mandatory openat2 versus systemd255 RestrictSUIDSGID; no evidence authority."""
import ctypes,importlib.util,json,os,pathlib,re,shutil,stat,subprocess,sys,time,uuid
ENV={'PATH':'/usr/sbin:/usr/bin:/sbin:/bin','LANG':'C.UTF-8'}
ROOT=pathlib.Path(__file__).resolve().parents[2]
spec=importlib.util.spec_from_file_location('pinned_launcher',ROOT/'scripts/evidencehost-linux-launcher.py')
launcher=importlib.util.module_from_spec(spec); spec.loader.exec_module(launcher)
PAYLOAD=r'''
import ctypes,json,os,pathlib
class How(ctypes.Structure): _fields_=[('flags',ctypes.c_ulonglong),('mode',ctypes.c_ulonglong),('resolve',ctypes.c_ulonglong)]
lib=ctypes.CDLL(None,use_errno=True);lib.syscall.restype=ctypes.c_long
how=How(os.O_RDONLY|os.O_DIRECTORY|os.O_CLOEXEC,0,0)
ctypes.set_errno(0);fd=lib.syscall(437,ctypes.c_int(-100),ctypes.c_char_p(b'/'),ctypes.byref(how),ctypes.sizeof(how));err=ctypes.get_errno() if fd<0 else 0
if fd>=0:os.close(fd)
status=dict(line.split(':',1) for line in pathlib.Path('/proc/self/status').read_text().splitlines() if ':' in line)
membership=pathlib.Path('/proc/self/cgroup').read_text().splitlines()
assert len(membership)==1 and membership[0].startswith('0::')
print(json.dumps({'uid':os.getuid(),'gid':os.getgid(),'cgroup':membership[0][3:],'openat2_succeeded':fd>=0,'errno':err,'no_new_privileges':int(status['NoNewPrivs']),'effective_capabilities':int(status['CapEff'].strip(),16)}))
'''
def run(argv,**kw):return subprocess.run(argv,check=True,env=ENV,timeout=15,**kw)
def properties(unit):
 r=run(['systemctl','show',unit,'--property=ControlGroup','--property=User','--property=Group','--property=MainPID','--property=ActiveState','--property=SubState','--property=ExecMainCode','--property=ExecMainStatus'],stdout=subprocess.PIPE)
 return dict(x.split('=',1) for x in r.stdout.decode().splitlines() if '=' in x)
def empty(group):
 if not re.fullmatch(r'/system.slice/issue779-openat2-[a-f0-9]{32}-(yes|no)\.service',group):return False
 p=pathlib.Path('/sys/fs/cgroup'+group)
 if not p.exists():return True
 return all(not f.read_text().strip() for f in p.rglob('cgroup.procs'))
def main():
 assert os.getuid()==0 and sys.platform=='linux' and os.uname().machine=='x86_64'
 assert run(['systemctl','--version'],stdout=subprocess.PIPE).stdout.splitlines()[0].startswith(b'systemd 255 ')
 token=uuid.uuid4().hex; name='eh-op-'+token[:12]; work=pathlib.Path('/run/issue779-openat2-'+token)
 work.mkdir(mode=0o711);os.chmod(work,0o711); units=[];groups=[];created=False; results=[]; cleanup=False; controls_complete=False; failure_stage=None; stage='account-setup'
 output=pathlib.Path(sys.argv[1]); output.parent.mkdir(parents=True,exist_ok=True)
 try:
  run(['useradd','--system','--user-group','--no-create-home','--shell','/usr/sbin/nologin',name]);created=True
  import pwd
  account=pwd.getpwnam(name); assert account.pw_uid>0 and account.pw_gid>0
  for child in ('tool','subject','test-output'):
   p=work/child;p.mkdir(mode=0o755)
  writable=work/'output';writable.mkdir(mode=0o700);os.chown(writable,account.pw_uid,account.pw_gid)
  for restricted in ('yes','no'):
   unit='issue779-openat2-'+token+'-'+restricted+'.service';units.append(unit)
   expected_group='/system.slice/'+unit;groups.append(expected_group)
   stage='main-completion'
   props=launcher.worker_unit_properties(name,work/'tool',work/'subject',work/'test-output',writable,10)
   props['RestrictSUIDSGID']=restricted
   argv=['systemd-run','--quiet','--wait','--pipe','--expand-environment=no','--unit='+unit,*['--property='+k+'='+v for k,v in props.items()],'/usr/bin/env','-i','PATH=/usr/bin:/bin','LANG=C.UTF-8','/usr/bin/python3','-I','-c',PAYLOAD]
   process=subprocess.Popen(argv,env=ENV,stdin=subprocess.DEVNULL,stdout=subprocess.PIPE,stderr=subprocess.PIPE)
   deadline=time.monotonic()+12
   main_finished=False
   try:
    while time.monotonic()<deadline:
     try:
      actual=properties(unit)
     except subprocess.CalledProcessError:
      time.sleep(.05);continue
     if actual.get('ActiveState')=='active' and actual.get('SubState')=='exited' and actual.get('MainPID')=='0' and actual.get('ExecMainCode')=='1' and actual.get('ExecMainStatus')=='0':
      main_finished=True;break
     if process.poll() is not None:break
     time.sleep(.05)
    assert main_finished
    stage='pre-stop-identity'
    # RemainAfterExit keeps the unit loaded, but SERVICE_EXITED can prune its
    # empty cgroup. The fixed payload reports its own kernel membership while alive.
    pre_stop_identity=(actual.get('User')==name and actual.get('Group')==name and actual.get('MainPID')=='0'
                       and actual.get('ControlGroup','') in ('',expected_group))
    stage='stop-and-pipe-completion'
    run(['systemctl','stop',unit],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
    stdout,stderr=process.communicate(timeout=3)
    assert process.returncode==0 and len(stdout)<=4096 and len(stderr)<=4096
   finally:
    if process.poll() is None:
     subprocess.run(['systemctl','kill','--kill-whom=all','--signal=KILL',unit],env=ENV,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,timeout=5)
     process.kill();process.communicate(timeout=3)
   stage='payload-schema'
   value=json.loads(stdout); assert set(value)=={'uid','gid','cgroup','openat2_succeeded','errno','no_new_privileges','effective_capabilities'}
   assert type(value['openat2_succeeded']) is bool
   assert type(value['cgroup']) is str and re.fullmatch(r'/system.slice/issue779-openat2-[a-f0-9]{32}-(yes|no)\.service',value['cgroup'])
   assert all(type(value[key]) is int and 0<=value[key]<=2**64-1 for key in ('uid','gid','errno','no_new_privileges','effective_capabilities'))
   captured_group=value['cgroup']
   value.update(restrict_suid_sgid=restricted,pre_stop_identity_confirmed=pre_stop_identity,post_stop_group_empty=False,case_passed=False)
   # Preserve actual numeric facts even if a later policy or cleanup check fails.
   results.append(value)
   stage='kernel-policy-facts'
   assert pre_stop_identity and captured_group==expected_group
   assert value['uid']==account.pw_uid and value['gid']==account.pw_gid and value['no_new_privileges']==1 and value['effective_capabilities']==0
   assert (not value['openat2_succeeded'] and value['errno']==38) if restricted=='yes' else (value['openat2_succeeded'] and value['errno']==0)
   stage='post-stop-cgroup'
   deadline=time.monotonic()+3
   while time.monotonic()<deadline and not empty(captured_group):time.sleep(.05)
   value['post_stop_group_empty']=empty(captured_group);assert value['post_stop_group_empty']
   value['case_passed']=True
  controls_complete=True
 except Exception:
  failure_stage=stage
 finally:
  for unit in units:
   for argv in (['systemctl','stop','--no-block',unit],['systemctl','kill','--kill-whom=all','--signal=KILL',unit]):
    try:subprocess.run(argv,env=ENV,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,timeout=5)
    except (OSError,subprocess.SubprocessError):
     if failure_stage is None:failure_stage='cleanup-command'
  deadline=time.monotonic()+3
  while time.monotonic()<deadline:
   if all(empty(group) for group in groups):cleanup=True;break
   time.sleep(.05)
  if created and cleanup:
   try:run(['userdel',name]);shutil.rmtree(work)
   except (OSError,subprocess.SubprocessError):
    cleanup=False
    if failure_stage is None:failure_stage='account-cleanup'
  passed=controls_complete and len(results)==2 and all(value['case_passed'] for value in results) and cleanup and failure_stage is None
  receipt={'scope':'syscall policy compatibility only; no runtime acceptance','source_commit':'02ca14024d3f7d051c186925a69151786705702e','systemd':'255','cases':results,'cleanup_complete':cleanup,'failure_stage':failure_stage,'native_evidence_authority':False,'Trust':False,'exit':0 if passed else 1}
  output.write_text(json.dumps(receipt,indent=2)+'\n');output.chmod(0o600)
 return receipt['exit']
if __name__=='__main__':sys.exit(main())
