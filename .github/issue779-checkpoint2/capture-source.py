"""Bind the complete clean source tree before build and recheck original bytes/modes afterward."""
from pathlib import Path
import hashlib,json,os,stat,subprocess,sys

def git(*args):
 return subprocess.check_output(['git',*args],timeout=15)
def snapshot(expected):
 head=git('rev-parse','HEAD').decode().strip()
 assert head==expected and not git('status','--porcelain','--untracked-files=all').strip()
 assert git('write-tree').strip()==git('rev-parse','HEAD^{tree}').strip()
 rows={}
 for raw in filter(None,git('ls-files','--stage','-z').split(b'\0')):
  header,name=raw.split(b'\t',1);mode,blob,stage=header.split();p=Path(os.fsdecode(name));s=p.lstat()
  assert stage==b'0' and mode in (b'100644',b'100755') and stat.S_ISREG(s.st_mode) and s.st_nlink==1
  body=p.read_bytes();assert hashlib.sha1(b'blob '+str(len(body)).encode()+b'\0'+body).hexdigest().encode()==blob
  assert bool(s.st_mode&0o111)==(mode==b'100755')
  rows[str(p)]={'sha256':hashlib.sha256(body).hexdigest(),'mode':format(stat.S_IMODE(s.st_mode),'04o'),'git_blob':blob.decode()}
 return {'schema':'issue779-checkpoint2-source-v1','source':head,'tree':git('rev-parse','HEAD^{tree}').decode().strip(),'files':rows,'count':len(rows)}
def main():
 expected=sys.argv[1];dest=Path(sys.argv[2]);os.umask(0o077);observed=snapshot(expected)
 if '--verify' in sys.argv:
  assert json.loads(dest.read_bytes())==observed
 else:
  with dest.open('x') as f:json.dump(observed,f,sort_keys=True)
if __name__=='__main__':main()
