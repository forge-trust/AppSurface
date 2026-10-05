#!/usr/bin/env python3
"""PRIVATE root controller: real filesystem/Coverlet task procedure, no admission.

main is the only native entry. A parent absolute process owner must bound this
controller, including blocking kernel calls. Internal watchdog seals dispatch and
kills registered children/unit. Tests are data/procedure only and never call main.
"""
from __future__ import annotations
import argparse, grp, hashlib, importlib.util, json, os, platform, pwd, re
import selectors, signal, stat, subprocess, sys, threading, time, uuid
from pathlib import Path
import xml.etree.ElementTree as ET

LAUNCHER_SHA = 'e1f6ca67cadbcc510213dcc3f31f8cf8a1caf478bda89f77e1738aeffb367b6c'
UID, GID, LIMIT, LOG_LIMIT = 65010, 65011, 1048576, 65536
FIELDS = ('LoadState','User','Group','Type','ActiveState','SubState','MainPID','ExecMainCode','ExecMainStatus','ControlGroup')
REPORTS = ('coverage.cobertura.xml','coverage.json')
BUILD_CULTURES = frozenset(('cs','de','es','fr','it','ja','ko','pl','pt-BR','ru','tr','zh-Hans','zh-Hant'))
BUILD_RESOURCE = 'Microsoft.Build.Utilities.Core.resources.dll'
BUILD_RESOURCES = frozenset((BUILD_RESOURCE,'Microsoft.Build.Framework.resources.dll','Microsoft.NET.StringTools.resources.dll'))
class Failure(Exception):
    """Only fixed categories, never native exception text, leave the controller."""
def require(ok, category):
    if not ok: raise Failure(category)
def left(deadline):
    n = deadline-time.monotonic(); require(n > 0, 'deadline'); return n
def name(value):
    require(type(value) is str and re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9_.-]{0,127}',value) and '..' not in value, 'basename'); return value
def snap(s):
    return (s.st_dev,s.st_ino,s.st_uid,s.st_gid,stat.S_IMODE(s.st_mode),s.st_nlink,s.st_size,s.st_mtime_ns,s.st_ctime_ns)
def names(fd, deadline, cap=16):
    out=[]
    with os.scandir(fd) as it:
        for x in it:
            left(deadline); require(len(out)<cap,'count'); out.append(name(x.name))
    return sorted(out)
def read_file(fd, n, deadline, uid, gid=None, mode=None, cap=LIMIT):
    """Borrow parent FD; pin one no-follow/nonblocking single-link regular file."""
    left(deadline); name(n); f=os.open(n,os.O_RDONLY|os.O_NOFOLLOW|os.O_NONBLOCK,dir_fd=fd)
    try:
        before=os.fstat(f)
        require(stat.S_ISREG(before.st_mode) and before.st_nlink==1 and before.st_uid==uid and before.st_dev==os.fstat(fd).st_dev,'file-shape')
        require(gid is None or before.st_gid==gid,'file-gid'); require(mode is None or stat.S_IMODE(before.st_mode)==mode,'file-mode')
        require(0<=before.st_size<=cap,'size'); data=bytearray()
        while True:
            left(deadline); part=os.read(f,min(65536,cap+1-len(data)))
            if not part: break
            data.extend(part); require(len(data)<=cap,'size')
        require(len(data)==before.st_size and snap(before)==snap(os.fstat(f))==snap(os.stat(n,dir_fd=fd,follow_symlinks=False)),'file-changed')
        return bytes(data),snap(before)
    finally: os.close(f)
def kernel(path, deadline, cap=LIMIT):
    left(deadline); f=os.open(path,os.O_RDONLY|os.O_NOFOLLOW|os.O_NONBLOCK)
    try:
        require(stat.S_ISREG(os.fstat(f).st_mode),'kernel-type'); b=bytearray()
        while True:
            left(deadline); x=os.read(f,min(65536,cap+1-len(b)))
            if not x: return bytes(b)
            b.extend(x); require(len(b)<=cap,'kernel-size')
    finally: os.close(f)
def packet(data, stage):
    require(len(data)<=4096,'packet-size')
    def unique(pairs):
        d={}
        for k,v in pairs: require(k not in d,'packet-duplicate'); d[k]=v
        return d
    try: d=json.loads(data,object_pairs_hook=unique)
    except (ValueError,UnicodeError): raise Failure('packet-json') from None
    shapes={'prepared':{'stage','state','temp'},'role-ready':{'stage'},'completed':{'stage','denials_passed','value'}}
    require(type(d) is dict and set(d)==shapes[stage] and d['stage']==stage,'packet-schema')
    if stage=='prepared': require(type(d['state']) is str and type(d['temp']) is str and len(d['state']) <= 4096 and len(d['temp']) <= 4096,'packet-path')
    if stage=='completed': require(d['denials_passed'] is True and type(d['value']) is int and d['value']==3,'worker-result')
    return d
def unit_data(data):
    d={}
    for line in data.decode('ascii').splitlines():
        k,sep,v=line.partition('='); require(sep and k in FIELDS and k not in d,'unit-schema'); d[k]=v
    require(set(d)==set(FIELDS),'unit-schema')
    for k in ('MainPID','ExecMainCode','ExecMainStatus'): require(d[k].isdigit(),'unit-numeric'); d[k]=int(d[k])
    return d
def confirmed_unit_stop(stop_exit, query_exit, facts):
    """Pure cleanup data check; physical cgroup emptiness is checked separately."""
    require(type(stop_exit) is int and type(query_exit) is int and query_exit==0,'stop-query')
    require(set(facts)=={'LoadState','ActiveState','SubState','MainPID'},'stop-schema')
    require(facts['MainPID']=='0' and facts['ActiveState']=='inactive' and facts['SubState']=='dead','stop-state')
    if stop_exit==0: require(facts['LoadState'] in ('loaded','not-found'),'stop-load'); return 'checked-stop'
    require(facts['LoadState']=='not-found','stop-unconfirmed'); return 'checked-absent-unit'
def mount_data(text, path, dev,selected_mount_id=None):
    """Exact selected mountpoint, actual tmpfs/device and private mount flags."""
    require(len(text)<=LIMIT,'mountinfo-size'); rows=[]
    for line in text.splitlines():
        a,sep,b=line.partition(' - '); x,y=a.split(),b.split()
        if sep and len(x)>=6 and x[4]==str(path): rows.append((x,y))
    if selected_mount_id is not None:
        require(type(selected_mount_id) is int and selected_mount_id>0,'mount-id')
        rows=[(x,y)for x,y in rows if x[0]==str(selected_mount_id)]
    require(len(rows)==1,'mount-visible'); x,y=rows[0]
    require(len(y)>=3 and y[0]=='tmpfs' and x[2]==f'{os.major(dev)}:{os.minor(dev)}','mount-type-device')
    require({'nosuid','nodev','noexec'}<=set(x[5].split(','))|set(y[2].split(',')),'mount-flags')
    return {'visible':True,'tmpfs':True,'nosuid':True,'nodev':True,'noexec':True}
def mount_id_data(data):
    """Parse only the actual opened FD's kernel mount identifier."""
    require(len(data)<=4096,'mount-fdinfo-size'); values=[]
    for line in data.decode('ascii').splitlines():
        key,sep,value=line.partition(':')
        if key=='mnt_id': require(sep and value.strip().isdigit(),'mount-fdinfo'); values.append(int(value.strip()))
    require(len(values)==1 and values[0]>0,'mount-fdinfo'); return values[0]
def worker_mount(pid,session,expected,d):
    """Use the kernel's target process root; pin its selected path to our mount FD."""
    left(d); f=os.open(f'/proc/{pid}/root'+str(session),os.O_RDONLY|os.O_DIRECTORY|os.O_NOFOLLOW)
    try:
        s=os.fstat(f); identity=(s.st_dev,s.st_ino,s.st_uid,s.st_gid,stat.S_IMODE(s.st_mode))
        require(identity==expected and stat.S_ISDIR(s.st_mode),'worker-session-identity')
        selected=mount_id_data(kernel(f'/proc/self/fdinfo/{f}',d,4096)); left(d)
        return selected,{'mount_id':selected,'directory_identity_matches':True,'root_owner':s.st_uid==0,'worker_group':s.st_gid==GID,'mode':format(stat.S_IMODE(s.st_mode),'04o')}
    finally: os.close(f)
def mount_evidence(text,path,dev):
    """Bounded selected-path diagnostics only; does not change mount acceptance."""
    require(len(text)<=LIMIT,'mountinfo-size'); selected=[]; covering=[]
    for line in text.splitlines():
        a,sep,b=line.partition(' - '); x,y=a.split(),b.split()
        if not sep or len(x)<6 or len(y)<3: continue
        exact=x[4]==str(path); contains=str(path).startswith(x[4].rstrip('/')+'/')
        if not exact and not contains: continue
        require(len(selected)+len(covering)<64,'mount-diagnostic-count')
        options=set(x[5].split(','))|set(y[2].split(','))
        row={'mount_id':int(x[0]),'parent_id':int(x[1]),'exact_path':exact,
             'device_expected':x[2]==f'{os.major(dev)}:{os.minor(dev)}',
             'tmpfs':y[0]=='tmpfs','read_write':'rw' in x[5].split(','),
             'nosuid':'nosuid' in options,'nodev':'nodev' in options,'noexec':'noexec' in options}
        (selected if exact else covering).append(row)
    return {'exact_count':len(selected),'exact_mounts':selected,'covering_mounts':covering}
def proc_start(pid, deadline):
    d=kernel(f'/proc/{pid}/stat',deadline,8192).decode('ascii'); fields=d[d.rfind(')')+2:].split()
    require(len(fields)>=20 and fields[19].isdigit(),'proc-stat'); return int(fields[19])
def live(pid, cg, session, dev, deadline,evidence=None,session_identity=None):
    first=proc_start(pid,deadline); d={}
    for line in kernel(f'/proc/{pid}/status',deadline,16384).decode('ascii').splitlines():
        k,sep,v=line.partition(':')
        if sep and k in ('Uid','Gid','Groups','CapEff','NoNewPrivs'): require(k not in d,'proc-duplicate'); d[k]=v.strip()
    require(set(d)=={'Uid','Gid','Groups','CapEff','NoNewPrivs'},'proc-status')
    require(d['Uid'].split()==[str(UID)]*4 and d['Gid'].split()==[str(GID)]*4,'proc-identity')
    # No foreign supplementary identity; primary GID may appear in Groups.
    require(d['Groups'].split() in ([],[str(GID)]),'proc-supplements')
    require(re.fullmatch('[0-9a-fA-F]+',d['CapEff']) and int(d['CapEff'],16)==0 and d['NoNewPrivs']=='1','proc-privileges')
    require(kernel(f'/proc/{pid}/cgroup',deadline,4096).decode('ascii').splitlines()==['0::'+cg],'proc-cgroup')
    text=kernel(f'/proc/{pid}/mountinfo',deadline).decode('ascii')
    if evidence is not None:
        evidence.update({'pid':pid,'starttime':first,'uid4':[UID]*4,'gid4':[GID]*4,
                         'cap_eff':0,'no_new_privs':1,'cgroup_exact':True,
                         'mount_inspection':mount_evidence(text,session,dev)})
    require(session_identity is not None,'worker-session-binding')
    selected,opened=worker_mount(pid,session,session_identity,deadline)
    if evidence is not None: evidence['opened_session']=opened
    m=mount_data(text,session,dev,selected)
    require(proc_start(pid,deadline)==first,'proc-replaced')
    return {'pid':pid,'starttime':first,'uid4':[UID]*4,'gid4':[GID]*4,'supplementary_groups':[int(x) for x in d['Groups'].split()],'cap_eff':0,'no_new_privs':1,'cgroup_exact':True,'session_mount':m,'opened_session':opened}
class Process:
    """Owned child process with bounded memory and EOF, explicit stdin commands."""
    def __init__(self, argv, env):
        self.p=subprocess.Popen(argv,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,env=env)
        self.data={'stdout':bytearray(),'stderr':bytearray()}; self.offset=0; self.closed=False; self.sel=selectors.DefaultSelector()
        for k,f in (('stdout',self.p.stdout),('stderr',self.p.stderr)):
            os.set_blocking(f.fileno(),False); self.sel.register(f,selectors.EVENT_READ,k)
    def signal(self,s):
        try: os.kill(self.p.pid,s)
        except ProcessLookupError: pass
    def absent(self):
        try: os.kill(self.p.pid,0); return False
        except ProcessLookupError: return True
    def pump(self,deadline):
        left(deadline)
        for key,_ in self.sel.select(min(.05,left(deadline))):
            b=os.read(key.fileobj.fileno(),8192)
            if not b: self.sel.unregister(key.fileobj)
            else: require(sum(map(len,self.data.values()))+len(b)<=LOG_LIMIT,'log-limit'); self.data[key.data].extend(b)
    def receive(self,stage,deadline):
        while True:
            left(deadline); end=self.data['stdout'].find(b'\n',self.offset)
            if end>=0:
                d=packet(bytes(self.data['stdout'][self.offset:end]),stage); self.offset=end+1; return d
            require(self.p.poll() is None or bool(self.sel.get_map()),'packet-eof'); self.pump(deadline)
    def send(self,b,deadline):
        left(deadline); require(b in (b'continue\n',b'collect\n'),'stdin-contract'); self.p.stdin.write(b); self.p.stdin.flush()
    def join(self,deadline,expected=0):
        self.p.stdin.close()
        while self.p.poll() is None or self.sel.get_map(): self.pump(deadline)
        self.p.wait(timeout=left(deadline)); require(self.p.returncode==expected and self.absent(),'process-terminal'); left(deadline)
        return {'exit_code':self.p.returncode,'pipe_eof':True,'process_absent':True}
    def cleanup(self,deadline):
        if self.closed: require(self.absent(),'process-residue'); return
        if not self.absent():
            self.signal(signal.SIGTERM); until=min(deadline,time.monotonic()+.3)
            while time.monotonic()<until and not self.absent(): self.p.poll(); time.sleep(.01)
            if not self.absent(): self.signal(signal.SIGKILL)
        while self.p.poll() is None or self.sel.get_map(): self.pump(deadline)
        self.p.wait(timeout=left(deadline)); require(self.absent(),'process-residue')
    def close(self):
        if self.closed: return
        self.closed=True
        for f in (self.p.stdin,self.p.stdout,self.p.stderr): f.close()
        self.sel.close()
class Owner:
    """Lock prevents dispatch after first failure; watchdog owns all registered children."""
    def __init__(self,deadline,unit):
        self.deadline,self.unit=deadline,unit; self.lock=threading.RLock(); self.children=[]; self.failure=None; self.done=threading.Event()
        self.watchdog=threading.Thread(target=self.watch,daemon=True); self.watchdog.start()
    def fail(self,category):
        with self.lock:
            if self.failure is None: self.failure=category
    def spawn(self,argv,env=None,cleanup=False):
        with self.lock:
            if not cleanup: left(self.deadline); require(self.failure is None,'owner-failed')
            require(len(self.children)<256,'process-count'); p=Process(argv,env); self.children.append(p); return p
    def command(self,argv,deadline,cleanup=False):
        p=self.spawn(argv,cleanup=cleanup)
        try: p.join(deadline); return bytes(p.data['stdout'])
        finally:
            if p.p.poll() is not None and p.absent() and not p.sel.get_map(): p.close()
    def watch(self):
        if self.done.wait(max(0,self.deadline-time.monotonic()-5)): return
        self.fail('watchdog-deadline')
        with self.lock: children=list(self.children)
        for p in children: p.signal(signal.SIGKILL)
        end=time.monotonic()+2
        for a in (['systemctl','kill','--kill-whom=all','--signal=SIGKILL',self.unit],['systemctl','--no-block','stop',self.unit]):
            try: self.command(a,end,cleanup=True)
            except BaseException: self.fail('watchdog-stop')
    def finish(self,deadline):
        self.done.set(); self.watchdog.join(timeout=max(0,deadline-time.monotonic())); require(not self.watchdog.is_alive(),'watchdog-join')
class Accounts:
    """Fresh same-name group/user; reserve pending names BEFORE utility I/O."""
    def __init__(self,n): self.name=n; self.pending=[]
    def create(self,o,d):
        for lookup,key in ((pwd.getpwuid,UID),(grp.getgrgid,GID),(pwd.getpwnam,self.name),(grp.getgrnam,self.name)):
            try: lookup(key)
            except KeyError: continue
            raise Failure('identity-occupied')
        self.pending.append('group'); o.command(['groupadd','--gid',str(GID),self.name],d)
        self.pending.append('user'); o.command(['useradd','--uid',str(UID),'--gid',str(GID),'--no-user-group','--no-create-home','--home-dir','/nonexistent','--shell','/usr/sbin/nologin','--groups','',self.name],d)
        p,g=pwd.getpwnam(self.name),grp.getgrnam(self.name)
        require(p.pw_uid==UID and p.pw_gid==GID and g.gr_gid==GID and not g.gr_mem and os.getgrouplist(self.name,GID)==[GID],'identity-created')
    def cleanup(self,o,d):
        for kind in reversed(self.pending):
            lookup=pwd.getpwnam if kind=='user' else grp.getgrnam
            try: row=lookup(self.name)
            except KeyError: continue
            require(row.pw_uid==UID and row.pw_gid==GID if kind=='user' else row.gr_gid==GID,'identity-replaced')
            o.command(['userdel' if kind=='user' else 'groupdel',self.name],d,cleanup=True)
            try: lookup(self.name)
            except KeyError: continue
            raise Failure('identity-cleanup')
        for lookup,key in ((pwd.getpwuid,UID),(grp.getgrgid,GID)):
            try: lookup(key)
            except KeyError: continue
            raise Failure('identity-residue')
def directory(path,uid,gid,mode):
    f=os.open(path,os.O_RDONLY|os.O_DIRECTORY|os.O_NOFOLLOW)
    try:
        s=os.fstat(f); n=os.lstat(path)
        require((s.st_uid,s.st_gid,stat.S_IMODE(s.st_mode))==(uid,gid,mode) and (s.st_dev,s.st_ino)==(n.st_dev,n.st_ino),'directory'); return f
    except BaseException: os.close(f); raise
def directory_facts(s):
    """Closed setup metadata only; these facts never replace the pinned checks."""
    return {'uid':s.st_uid,'gid':s.st_gid,'mode':format(stat.S_IMODE(s.st_mode),'04o'),
            'directory':stat.S_ISDIR(s.st_mode),'symlink':stat.S_ISLNK(s.st_mode)}
def create_private_directory(path,uid,gid):
    """Own one fresh directory explicitly, including inherited parent group bits."""
    parent=os.open(path.parent,os.O_RDONLY|os.O_DIRECTORY|os.O_NOFOLLOW); f=None
    try:
        ps=os.fstat(parent); pn=os.lstat(path.parent)
        require(ps.st_uid==uid and not ps.st_mode&0o022 and stat.S_ISDIR(pn.st_mode)
                and (ps.st_dev,ps.st_ino)==(pn.st_dev,pn.st_ino),'directory-parent')
        leaf=name(path.name); os.mkdir(leaf,mode=0o700,dir_fd=parent)
        made=os.stat(leaf,dir_fd=parent,follow_symlinks=False)
        f=os.open(leaf,os.O_RDONLY|os.O_DIRECTORY|os.O_NOFOLLOW,dir_fd=parent); before=os.fstat(f)
        require(before.st_uid==uid and stat.S_IMODE(before.st_mode)&0o777==0o700
                and snap(made)==snap(before),'directory-created')
        os.fchown(f,uid,gid); os.fchmod(f,0o700); after=os.fstat(f)
        named=os.stat(leaf,dir_fd=parent,follow_symlinks=False); pn=os.lstat(path.parent)
        require((after.st_uid,after.st_gid,stat.S_IMODE(after.st_mode))==(uid,gid,0o700)
                and stat.S_ISDIR(named.st_mode) and (after.st_dev,after.st_ino)==(named.st_dev,named.st_ino)
                and (ps.st_dev,ps.st_ino)==(pn.st_dev,pn.st_ino),'directory')
        return {'before':directory_facts(before),'after':directory_facts(after)}
    finally:
        if f is not None: os.close(f)
        os.close(parent)
def cg_empty(path,identity,d):
    left(d)
    try: f=os.open(path,os.O_RDONLY|os.O_DIRECTORY|os.O_NOFOLLOW)
    except FileNotFoundError: return True  # exact group was positively captured live
    try:
        s=os.fstat(f); require((s.st_dev,s.st_ino)==identity,'cgroup-replaced')
        with os.scandir(f) as it:
            for x in it: left(d); require(not x.is_dir(follow_symlinks=False),'cgroup-child')
        return not kernel(str(path/'cgroup.procs'),d,4096).strip()
    finally: os.close(f)
def freeze(fd,ready,session,d):
    require(ready['temp']==str(session)+'/' and Path(ready['state']).parent==session,'ready-temp'); ns=names(fd,d); state=name(Path(ready['state']).name)
    ds=[n for n in ns if re.fullmatch(r'CounterFixture_[0-9a-fA-F-]{36}\.dll',n)]
    require(len(ds)==1,'backup-selection'); dll=ds[0]; uuid.UUID(dll[len('CounterFixture_'):-4]); pdb=dll[:-4]+'.pdb'
    require(set(ns)=={state,dll,pdb},'root-inventory'); saved={}
    for n in ns:
        f=os.open(n,os.O_RDONLY|os.O_NOFOLLOW|os.O_NONBLOCK,dir_fd=fd)
        try:
            s=os.fstat(f); require(stat.S_ISREG(s.st_mode) and s.st_uid==0 and s.st_nlink==1 and s.st_dev==os.fstat(fd).st_dev and s.st_size<=LIMIT,'root-file'); os.fchmod(f,0o600)
        finally: os.close(f)
        b,s=read_file(fd,n,d,0,mode=0o600); saved[n]=(hashlib.sha256(b).hexdigest(),s)
    return state,dll,pdb,saved
def helper(root):
    p=root/'scripts/evidencehost-linux-launcher.py'; require(hashlib.sha256(p.read_bytes()).hexdigest()==LAUNCHER_SHA,'source-pin')
    spec=importlib.util.spec_from_file_location('issue779_frozen_native_probe',p); require(spec is not None and spec.loader is not None,'import-spec')
    m=importlib.util.module_from_spec(spec); sys.modules[spec.name]=m; spec.loader.exec_module(m); return m
def runtime_helper():
    """Load only the committed private runtime-copy helper, never caller code."""
    p=Path(__file__).with_name('sealed-runtime.py')
    spec=importlib.util.spec_from_file_location('issue779_sealed_runtime',p); require(spec is not None and spec.loader is not None,'runtime-import')
    m=importlib.util.module_from_spec(spec); sys.modules[spec.name]=m; spec.loader.exec_module(m); return m

def baseline(n,t,s,i,a):
    """Equality check only; never mutate/imported policy or add a writable path."""
    return {'User':n,'Group':n,'Type':'exec','KillMode':'control-group','RuntimeMaxSec':'900','TimeoutStopSec':'2','SendSIGKILL':'yes','NoNewPrivileges':'yes','CapabilityBoundingSet':'','AmbientCapabilities':'','ProtectControlGroups':'yes','RestrictSUIDSGID':'no','PrivateTmp':'yes','ProtectSystem':'strict','ProtectHome':'yes','LimitCORE':'0','TasksMax':'64','MemoryMax':'1G','Restart':'no','RemainAfterExit':'yes','ReadOnlyPaths':f'{t} {s}','ReadWritePaths':str(a),'InaccessiblePaths':str(i)}
def copy_build(build,tool,d,diagnostics=None):
    source=os.open(build,os.O_RDONLY|os.O_DIRECTORY|os.O_NOFOLLOW); target=os.open(tool,os.O_RDONLY|os.O_DIRECTORY|os.O_NOFOLLOW)
    try:
        ns=names(source,d,128); required={'CounterFixture.dll','CounterFixture.pdb','CounterFixture.runtimeconfig.json','CounterFixture.deps.json','OfficialTaskHost.dll','OfficialTaskHost.runtimeconfig.json','OfficialTaskHost.deps.json'}
        require(required<=set(ns),'build-shape'); total=0; files=[]; dirs=[]; inventory=[]
        def copy_file(from_fd,to_fd,n,relative):
            nonlocal total
            observed=os.stat(n,dir_fd=from_fd,follow_symlinks=False)
            b,_=read_file(from_fd,n,d,observed.st_uid,cap=min(32<<20,(128<<20)-total)); total+=len(b); require(total<=128<<20,'build-total')
            f=os.open(n,os.O_WRONLY|os.O_CREAT|os.O_EXCL|os.O_NOFOLLOW,0o600,dir_fd=to_fd)
            try:
                view=memoryview(b)
                while view: left(d); written=os.write(f,view); require(written>0,'copy-write'); view=view[written:]
                os.fchmod(f,0o444)
            finally: os.close(f)
            files.append(relative); inventory.append({'name':relative,'kind':'file','bytes':len(b),'sha256':hashlib.sha256(b).hexdigest()})
        for n in ns:
            s=os.stat(n,dir_fd=source,follow_symlinks=False)
            if not stat.S_ISDIR(s.st_mode): copy_file(source,target,n,n); continue
            require(n in BUILD_CULTURES,'build-directory')
            child=os.open(n,os.O_RDONLY|os.O_DIRECTORY|os.O_NOFOLLOW,dir_fd=source); dest=None
            try:
                require(snap(s)==snap(os.fstat(child)),'build-directory-changed')
                resources=names(child,d,4)
                if diagnostics is not None: diagnostics.append({'culture':n,'names':resources})
                require(resources and set(resources)<=BUILD_RESOURCES,'build-resource-inventory')
                os.mkdir(n,mode=0o700,dir_fd=target); dirs.append(n)
                dest=os.open(n,os.O_RDONLY|os.O_DIRECTORY|os.O_NOFOLLOW,dir_fd=target)
                ds=os.fstat(dest); require(ds.st_uid==os.geteuid() and stat.S_IMODE(ds.st_mode)==0o700,'build-created-directory')
                for resource in resources: copy_file(child,dest,resource,n+'/'+resource)
                require(names(child,d,4)==resources and snap(s)==snap(os.fstat(child))==snap(os.stat(n,dir_fd=source,follow_symlinks=False)),'build-directory-changed')
                os.fchmod(dest,0o555); require(snap(os.fstat(dest))==snap(os.stat(n,dir_fd=target,follow_symlinks=False)),'build-target-directory-changed')
            finally:
                if dest is not None: os.close(dest)
                os.close(child)
        require(names(source,d,128)==ns,'build-inventory-changed')
        return {'files':files,'directories':dirs,'inventory':inventory,'total_bytes':total}
    finally: os.close(source); os.close(target)
def dotnet(selected):
    require(selected.is_absolute() and str(selected)==str(selected.resolve(strict=True)) and not str(selected).startswith(('/home/','/root/','/run/user/')),'dotnet-path')
    for n in (str(selected),):
        if os.path.lexists(n):
            s=os.lstat(n); require(stat.S_ISREG(s.st_mode) and s.st_uid==0 and s.st_nlink==1 and not s.st_mode&0o022 and s.st_mode&0o111,'dotnet'); return Path(n)
    raise Failure('dotnet-missing')
def save(output,n,b):
    name(n); f=os.open(output/n,os.O_WRONLY|os.O_CREAT|os.O_EXCL|os.O_NOFOLLOW,0o600)
    with os.fdopen(f,'wb') as stream: stream.write(b)
def run(source,build,output,selected_dotnet,tag):
    require(re.fullmatch('[0-9a-f]{32}',tag) is not None,'tag'); start=time.monotonic(); deadline=start+90; unit=f'issue779-existing-permissions-{tag}.service'; cg='/system.slice/'+unit; cpath=Path('/sys/fs/cgroup'+cg)
    account=Accounts('i779p'+tag[:12]); owner=None; runtime=None; sf=tf=None; mounted=False; unmount_safe=False; created=False; copied=[]; copied_dirs=[]; failure=None; roles={}
    work=Path('/run/issue779-existing-permissions-'+tag)
    result={'schema':'issue779-existing-permissions-coverlet-mechanism-v1','mechanism_passed':False,'evidence_admission':False,'trusted_claim':False,'qualification_claim':False,'coverage_gate_claim':False,'launcher_sha256':LAUNCHER_SHA,'guard_seconds':90,'cleanup_guard_seconds':5}
    try:
        require(os.geteuid()==0 and platform.system()=='Linux','root-linux'); os.umask(0o077); require(not os.path.lexists(output),'output-exists')
        result['directory_preflight']={'output':create_private_directory(output,0,0)}
        result['directory_preflight']['host_parent']=directory_facts(os.lstat('/run'))
        f=directory(output,0,0,0o700); os.close(f); f=directory(Path('/run'),0,0,0o755); os.close(f)
        release=kernel('/usr/lib/os-release',deadline,8192); require(b'ID=ubuntu' in release and b'VERSION_ID="24.04"' in release,'ubuntu')
        owner=Owner(deadline,unit); version=owner.command(['systemctl','--version'],deadline); require(re.match(rb'systemd 255(?:\s|\.)',version),'systemd-version')
        launcher=helper(source)
        work.mkdir(mode=0o711); os.chmod(work,0o711); created=True
        tool,subject,inaccessible,outer=(work/x for x in ('tool','source','test-output','output'))
        for p,mode in ((tool,0o555),(subject,0o555),(inaccessible,0o700),(outer,0o711)): p.mkdir(mode=mode); os.chmod(p,mode)
        sealing=runtime_helper()
        try: runtime=sealing.seal_runtime(selected_dotnet,work/'runtime',deadline)
        except sealing.SealError as error:
            # The helper supplies only its closed category, never native text/path.
            result['runtime_copy_failure_category']=error.category
            raise Failure('runtime-copy') from None
        executable=dotnet(runtime.dotnet); result['sealed_runtime']=runtime.record; account.create(owner,deadline)
        anchor=outer/('run-'+tag[:12]); anchor.mkdir(mode=0o700); os.chown(anchor,UID,GID); os.chmod(anchor,0o700)
        session=anchor/'coverage-session'; session.mkdir(mode=0o700)
        owner.command(['mount','-t','tmpfs','-o','size=32M,nr_inodes=64,nosuid,nodev,noexec,uid=0,gid=65011,mode=1770','tmpfs',str(session)],deadline); mounted=True
        sf=directory(session,0,GID,0o1770); device=os.fstat(sf).st_dev; v=os.fstatvfs(sf); require(0<v.f_blocks*v.f_frsize<=32<<20 and 0<v.f_files<=64,'tmpfs-bounds')
        result['root_session_mount']=mount_data(kernel('/proc/self/mountinfo',deadline).decode('ascii'),session,device)
        result['build_resource_inventory']=[]
        build_copy=copy_build(build,tool,deadline,result['build_resource_inventory']); copied=build_copy['files']; copied_dirs=build_copy['directories']; result['build_copy']=build_copy
        tf=directory(tool,0,0,0o555); originals={}
        for n in ('CounterFixture.dll','CounterFixture.pdb'):
            b,s=read_file(tf,n,deadline,0,mode=0o444); originals[n]={'sha256':hashlib.sha256(b).hexdigest(),'mode':s[4]}
        env={'PATH':f'{executable.parent}:/usr/bin:/bin','TMPDIR':str(session),'DOTNET_EnableDiagnostics':'0','DOTNET_NOLOGO':'1','DOTNET_CLI_TELEMETRY_OPTOUT':'1','HOME':'/nonexistent'}
        host=owner.spawn([str(executable),str(tool/'OfficialTaskHost.dll'),str(tool/'CounterFixture.dll'),str(session/'coverage')],env)
        roles['host']=host; ready=host.receive('prepared',deadline); state,dll,pdb,rootfiles=freeze(sf,ready,session,deadline)
        props=launcher.worker_unit_properties(account.name,tool,subject,inaccessible,anchor,900); require(props==baseline(account.name,tool,subject,inaccessible,anchor),'policy-drift')
        result['unit_policy_sha256']=hashlib.sha256(json.dumps(props,separators=(',',':')).encode()).hexdigest()
        argv=['systemd-run','--wait','--pipe','--quiet','--unit='+unit]+['--property='+k+'='+v for k,v in props.items()]+[str(executable),str(tool/'CounterFixture.dll'),str(session),state,dll,pdb]
        worker=owner.spawn(argv); roles['worker']=worker; worker.receive('role-ready',deadline)
        def inspect(): return unit_data(owner.command(['systemctl','show',unit,'--property='+','.join(FIELDS)],deadline))
        while True:
            row=inspect(); require(row['LoadState']=='loaded' and row['User']==account.name and row['Group']==account.name and row['Type']=='exec','unit-identity')
            if row['MainPID']>0 and row['ActiveState']=='active' and row['SubState']=='running': require(row['ControlGroup']==cg,'unit-cgroup'); break
            require(worker.p.poll() is None,'worker-start-exit'); worker.pump(deadline)
        result['worker_pre_mount']={}
        ss=os.fstat(sf); session_identity=(ss.st_dev,ss.st_ino,ss.st_uid,ss.st_gid,stat.S_IMODE(ss.st_mode))
        result['live_worker']=live(row['MainPID'],cg,session,device,deadline,result['worker_pre_mount'],session_identity); s=os.lstat(cpath); require(stat.S_ISDIR(s.st_mode),'cgroup-type'); identity=(s.st_dev,s.st_ino)
        worker.send(b'continue\n',deadline); worker.receive('completed',deadline)
        while True:
            row=inspect(); require(row['LoadState']=='loaded' and row['User']==account.name and row['Group']==account.name and row['Type']=='exec' and row['ControlGroup'] in ('',cg),'unit-terminal-identity')
            if row['MainPID']==0 and row['ExecMainCode']==1: require(row['ExecMainStatus']==0,'worker-exit'); break
            worker.pump(deadline)
        result['main_terminal']={'pid':0,'code':1,'status':0,'user_group_retained':True}
        owner.command(['systemctl','stop',unit],deadline) # stop BEFORE waiting --wait client
        result['worker_launcher']=worker.join(deadline)
        while not cg_empty(cpath,identity,deadline): left(deadline); time.sleep(.01)
        result['selected_physical_cgroup_empty']=True
        s=os.fstat(sf); require((s.st_uid,s.st_gid,stat.S_IMODE(s.st_mode))==(0,GID,0o1770),'session-after-worker')
        for n,(h,ss) in rootfiles.items():
            b,s=read_file(sf,n,deadline,0,mode=0o600); require(hashlib.sha256(b).hexdigest()==h and s==ss,'root-file-changed')
        hit=dll[:-4]; require(set(names(sf,deadline))==set(rootfiles)|{hit},'hits-inventory'); b,s=read_file(sf,hit,deadline,UID,GID); require(len(b)>=8,'hits-empty')
        result['genuine_hits']={'uid':s[2],'gid':s[3],'mode':s[4],'bytes':len(b),'sha256':hashlib.sha256(b).hexdigest(),'single_link':True}
        host.send(b'collect\n',deadline); result['taskhost']=host.join(deadline); reports={}
        for n in REPORTS:
            b,_=read_file(sf,n,deadline,0); reports[n]=b; save(output,n,b)
        xml=ET.fromstring(reports['coverage.cobertura.xml']); require(xml.tag=='coverage','report-format'); counts={}
        for k in ('lines-valid','lines-covered','branches-valid','branches-covered'): require(re.fullmatch('[0-9]+',xml.attrib.get(k,'')),'report-count'); counts[k]=int(xml.attrib[k])
        require(0<counts['branches-covered']<=counts['branches-valid'] and counts['lines-covered']<=counts['lines-valid'],'report-branches'); json.loads(reports['coverage.json']); result['official_counts']=counts
        result['reports']={n:{'bytes':len(b),'sha256':hashlib.sha256(b).hexdigest()} for n,b in reports.items()}; restored_modes={}
        for n,original in originals.items():
            b,s=read_file(tf,n,deadline,0); require(hashlib.sha256(b).hexdigest()==original['sha256'],'restore-hash'); restored_modes[n]=s[4]
            f=os.open(n,os.O_RDONLY|os.O_NOFOLLOW|os.O_NONBLOCK,dir_fd=tf)
            try: require(snap(os.fstat(f))==s,'restore-replaced'); os.fchmod(f,original['mode'])
            finally: os.close(f)
        result['originals_restored']=originals; result['modes_before_reseal']=restored_modes; unmount_safe=True; left(deadline); require(owner.failure is None,'owner-failed')
    except BaseException as e:
        failure=str(e) if isinstance(e,Failure) else 'controller-error'
        if owner: owner.fail(failure)
    finally:
        end=time.monotonic()+5; clean=True
        if owner:
            if not unmount_safe:
                for argv in (['systemctl','kill','--kill-whom=all','--signal=SIGKILL',unit],['systemctl','stop',unit]):
                    try: owner.command(argv,end,cleanup=True)
                    except BaseException: clean=False
            # Watchdog is joined before any cleanup publication or unmount.
            try: owner.finish(end)
            except BaseException: clean=False
            for p in list(owner.children):
                try: p.cleanup(end)
                except BaseException: clean=False
                finally:
                    try:
                        i=owner.children.index(p)
                        for stream,b in p.data.items(): save(output,f'process-{i:03d}-{stream}.log',bytes(b))
                        p.close()
                    except BaseException: clean=False
        for role,child in roles.items():
            for stream,b in child.data.items():
                try: save(output,role+'-'+stream+'.log',bytes(b))
                except BaseException: clean=False
        for f in (sf,tf):
            if f is not None:
                try: os.close(f)
                except OSError: clean=False
        # Failure does not bypass host restoration/exit: preserve mount/accounts.
        if mounted and unmount_safe and clean:
            try: owner.command(['umount',str(session)],end,cleanup=True); mounted=False
            except BaseException: clean=False
        if not mounted and clean and owner:
            try: account.cleanup(owner,end)
            except BaseException: clean=False
        if created and not mounted and clean:
            try:
                if runtime is not None: runtime.cleanup(end)
                for n in copied: left(end); os.unlink(tool/n)
                for n in copied_dirs: left(end); (tool/n).rmdir()
                for p in (tool,subject,inaccessible,session,anchor,outer,work): left(end); p.rmdir()
            except BaseException: clean=False
        # Record only closed categories and facts; all failures permanently fail.
        result['cleanup_complete']=clean and not mounted; result['workspace_quarantined']=mounted or not clean
        result['failure_category']=failure or (owner.failure if owner else None) or (None if clean and not mounted else 'cleanup')
        result['mechanism_passed']=result['failure_category'] is None and unmount_safe and result['cleanup_complete'] and time.monotonic()<deadline
        result['exit_code']=0 if result['mechanism_passed'] else 1; result['elapsed_seconds']=time.monotonic()-start
        try: save(output,'receipt.json',(json.dumps(result,sort_keys=True,separators=(',',':'))+'\n').encode())
        except BaseException: result['exit_code']=1
    return result['exit_code']
def main():
    p=argparse.ArgumentParser(description=__doc__)
    for n in ('source-root','probe-build','output','dotnet'): p.add_argument('--'+n,type=Path,required=True)
    p.add_argument('--tag',required=True); a=p.parse_args(); return run(a.source_root,a.probe_build,a.output,a.dotnet,a.tag)
if __name__=='__main__': raise SystemExit(main())
