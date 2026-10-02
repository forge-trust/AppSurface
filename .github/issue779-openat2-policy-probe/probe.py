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
print(json.dumps({'uid':os.getuid(),'gid':os.getgid(),'openat2_succeeded':fd>=0,'errno':err,'no_new_privileges':int(status['NoNewPrivs']),'effective_capabilities':int(status['CapEff'].strip(),16)}))
'''
def run(argv,**kw):return subprocess.run(argv,check=True,env=ENV,timeout=15,**kw)
def properties(unit):
 r=run(['systemctl','show',unit,'--property=ControlGroup','--property=User','--property=Group','--property=MainPID','--property=ActiveState','--property=SubState','--property=ExecMainCode','--property=ExecMainStatus'],stdout=subprocess.PIPE)
 return dict(x.split('=',1) for x in r.stdout.decode().splitlines() if '=' in x)
def empty(group):
 if not group:return True
 if not re.fullmatch(r'/system.slice/issue779-openat2-[a-f0-9]{32}-(yes|no)\.service',group):return False
 p=pathlib.Path('/sys/fs/cgroup'+group)
 if not p.exists():return True
 return all(not f.read_text().strip() for f in p.rglob('cgroup.procs'))
def main():
 assert os.getuid()==0 and sys.platform=='linux' and os.uname().machine=='x86_64'
 assert run(['systemctl','--version'],stdout=subprocess.PIPE).stdout.splitlines()[0].startswith(b'systemd 255 ')
 token=uuid.uuid4().hex; name='eh-op-'+token[:12]; work=pathlib.Path('/run/issue779-openat2-'+token)
 work.mkdir(mode=0o711);os.chmod(work,0o711); units=[];created=False; results=[]; cleanup=False
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
    run(['systemctl','stop',unit],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
    stdout,stderr=process.communicate(timeout=3)
    assert process.returncode==0 and len(stdout)<=4096 and len(stderr)<=4096
   finally:
    if process.poll() is None:
     subprocess.run(['systemctl','kill','--kill-whom=all','--signal=KILL',unit],env=ENV,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,timeout=5)
     process.kill();process.communicate(timeout=3)
   value=json.loads(stdout); assert set(value)=={'uid','gid','openat2_succeeded','errno','no_new_privileges','effective_capabilities'}
   assert value['uid']==account.pw_uid and value['gid']==account.pw_gid and value['no_new_privileges']==1 and value['effective_capabilities']==0
   value.update(restrict_suid_sgid=restricted)
   assert (not value['openat2_succeeded'] and value['errno']==38) if restricted=='yes' else (value['openat2_succeeded'] and value['errno']==0)
   actual=properties(unit);assert actual['User']==name and actual['Group']==name and actual.get('MainPID')=='0' and empty(actual.get('ControlGroup',''))
   results.append(value)
 finally:
  for unit in units:
   for argv in (['systemctl','stop','--no-block',unit],['systemctl','kill','--kill-whom=all','--signal=KILL',unit]):
    subprocess.run(argv,env=ENV,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,timeout=5)
  deadline=time.monotonic()+3
  while time.monotonic()<deadline:
   if all(empty(properties(unit).get('ControlGroup','')) for unit in units):cleanup=True;break
   time.sleep(.05)
  if created and cleanup:run(['userdel',name]);shutil.rmtree(work)
  receipt={'scope':'syscall policy compatibility only; no runtime acceptance','source_commit':'02ca14024d3f7d051c186925a69151786705702e','systemd':'255','cases':results,'cleanup_complete':cleanup,'native_evidence_authority':False,'Trust':False,'exit':0 if len(results)==2 and cleanup else 1}
  output.write_text(json.dumps(receipt,indent=2)+'\n');output.chmod(0o600)
 assert len(results)==2 and cleanup
if __name__=='__main__':main()
