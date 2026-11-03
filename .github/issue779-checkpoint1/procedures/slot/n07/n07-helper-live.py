import os,stat,json,sys,time

def require(v):
    if not v:raise ValueError('N07-data-rejected')
def fields(raw):
    end=raw.rfind(b') ');require(end>0)
    parts=raw[end+2:].split();require(len(parts)>=20)
    return int(raw.split(b' ',1)[0]),int(parts[19]),parts[0]
def read(fd,limit):
    out=b''
    while len(out)<=limit:
        chunk=os.read(fd,min(65536,limit+1-len(out)))
        if not chunk:break
        out+=chunk
    require(len(out)<=limit);return out
def finish_owned_reads(handles,parent,first_failure,clock):
    # Only original owned read descriptors are finalized; never retry a numeric FD.
    failure=first_failure
    for fd in [*handles,parent]:
        try:os.close(fd)
        except Exception as error:
            if failure is None:failure=error
    try:clock()
    except Exception as error:
        if failure is None:failure=error
    if failure is not None:raise failure
try:
    private,pidtext,unit,host,entry,*args=sys.argv[1:];pid=int(pidtext);end=int(args[-1])
    require(len(args)==13 and args[0]=='--execute' and pid>0)
    def clock():require(time.clock_gettime_ns(time.CLOCK_BOOTTIME)//1000000<end)
    clock();parent=os.open('/proc/'+str(pid),os.O_RDONLY|os.O_DIRECTORY|os.O_NOFOLLOW|os.O_CLOEXEC)
    handles=[]
    first_failure=None
    try:
        before=os.fstat(parent);require(stat.S_ISDIR(before.st_mode) and before.st_uid==0 and before.st_gid==0)
        values={}
        for name,limit in (('stat',8192),('status',65536),('cgroup',65536),('cmdline',65536)):
            clock();fd=os.open(name,os.O_RDONLY|os.O_NOFOLLOW|os.O_CLOEXEC,dir_fd=parent);handles.append(fd)
            values[name]=read(fd,limit)
        first=fields(values['stat']);require(first[0]==pid and first[1]>0 and first[2] in b'RSDTtKWPI')
        lines=values['status'].decode('ascii').splitlines()
        def ids(key):
            rows=[line for line in lines if line.startswith(key+':')];require(len(rows)==1)
            return [int(x) for x in rows[0].split(':',1)[1].split()]
        uids,gids,groups=ids('Uid'),ids('Gid'),ids('Groups')
        require(uids==gids==[0,0,0,0] and groups in ([],[0]))
        def policy(rows):
            result={}
            for key in ('NoNewPrivs','CapInh','CapPrm','CapEff','CapBnd','CapAmb'):
                selected=[line for line in rows if line.startswith(key+':')];require(len(selected)==1)
                text=selected[0].split(':',1)[1].strip()
                if key=='NoNewPrivs':require(text=='1');result[key]=1
                else:
                    require(len(text)==16 and all(c in '0123456789abcdef' for c in text))
                    result[key]=int(text,16)
            require(result=={'NoNewPrivs':1,'CapInh':0,'CapPrm':524333,'CapEff':524333,'CapBnd':524333,'CapAmb':0})
            return result
        first_policy=policy(lines)
        expected='/system.slice/'+unit
        require(values['cgroup']==('0::'+expected+'\n').encode('ascii'))
        require(values['cmdline']==b'\0'.join(x.encode('utf-8') for x in (host,entry,*args))+b'\0')
        require(os.path.samefile('/proc/'+str(pid)+'/exe',host))
        fd=os.open('stat',os.O_RDONLY|os.O_NOFOLLOW|os.O_CLOEXEC,dir_fd=parent);handles.append(fd)
        last=fields(read(fd,8192));require(first[:2]==last[:2] and last[2] in b'RSDTtKWPI')
        fd=os.open('status',os.O_RDONLY|os.O_NOFOLLOW|os.O_CLOEXEC,dir_fd=parent);handles.append(fd)
        repeated_lines=read(fd,65536).decode('ascii').splitlines()
        require(policy(repeated_lines)==first_policy)
        for key,expected_ids in (('Uid',uids),('Gid',gids),('Groups',groups)):
            repeated=[line for line in repeated_lines if line.startswith(key+':')];require(len(repeated)==1)
            require([int(x) for x in repeated[0].split(':',1)[1].split()]==expected_ids)
        after=os.fstat(parent);named=os.stat('/proc/'+str(pid),follow_symlinks=False)
        def identity(s):return(s.st_dev,s.st_ino,s.st_mode,s.st_uid,s.st_gid)
        require(identity(before)==identity(after)==identity(named));clock()
        value={'schema':'issue779-n07-helper-live-v2','pid':pid,'starttime_ticks':first[1],
               'uid4':uids,'gid4':gids,'groups':groups,'control_group':expected,
               'native_executable_matches':True,'managed_argv_matches':True,'native_authority':False,
               'no_new_privileges':first_policy['NoNewPrivs'],'cap_inheritable':first_policy['CapInh'],
               'cap_permitted':first_policy['CapPrm'],'cap_effective':first_policy['CapEff'],
               'cap_bounding':first_policy['CapBnd'],'cap_ambient':first_policy['CapAmb']}
        raw=json.dumps(value,separators=(',',':'),sort_keys=True).encode()+b'\n';require(len(raw)<=2048)
        fd=os.open(private+'/n07-helper-live.json',os.O_WRONLY|os.O_CREAT|os.O_EXCL|os.O_NOFOLLOW|os.O_CLOEXEC,0o600)
        with os.fdopen(fd,'wb') as f:f.write(raw);f.flush();os.fsync(f.fileno())
        clock()
    except Exception as error:
        first_failure=error
    finally:
        finish_owned_reads(handles,parent,first_failure,clock)
except Exception:
    sys.stderr.write('N07_DATA_REJECTED\n');sys.exit(1)
