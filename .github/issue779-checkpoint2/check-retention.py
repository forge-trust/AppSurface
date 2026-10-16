from pathlib import Path
import os,stat,sys
root=Path(sys.argv[1]);total=0;count=0
assert root.is_dir() and not root.is_symlink()
for p in root.rglob('*'):
 s=p.lstat();assert not stat.S_ISLNK(s.st_mode)
 if p.is_dir():p.chmod(0o700);continue
 assert stat.S_ISREG(s.st_mode) and s.st_nlink==1 and s.st_size<=8*1024*1024
 total+=s.st_size;count+=1;p.chmod(0o600)
assert count<=128 and total<=32*1024*1024
root.chmod(0o700)
