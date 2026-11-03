"""Fixed root-selected private cancellation observation; cannot issue admission."""
import os,sys,pathlib,stat
# Isolated Python excludes script-directory imports. Admit only the actual fixed
# root-owned no-write module directory before importing the pinned data adapter.
p=pathlib.Path(__file__).parent
s=p.lstat()
if os.geteuid()!=0 or not stat.S_ISDIR(s.st_mode) or s.st_uid!=0 or s.st_gid!=0 or stat.S_IMODE(s.st_mode)!=0o700:
 raise SystemExit(65)
sys.path.insert(0,str(p))
from cancellation_adapter import capture,Rejected
import json
try:
 if len(sys.argv)!=9:raise Rejected('cancellation-capture-rejected')
 result=capture(*sys.argv[1:])
 raw=(json.dumps(result,sort_keys=True,separators=(',',':'))+'\n').encode()
 if len(raw)>8192:raise Rejected('cancellation-capture-rejected')
 sys.stdout.buffer.write(raw);sys.stdout.buffer.flush()
except (ValueError,TypeError,OSError,KeyError,OverflowError):
 sys.stderr.write('CANCELLATION_CAPTURE_REJECTED\n');raise SystemExit(65) from None
