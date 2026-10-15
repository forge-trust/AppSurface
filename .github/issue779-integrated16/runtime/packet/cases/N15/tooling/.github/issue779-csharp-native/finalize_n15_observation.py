#!/usr/bin/python3
"""N15 root-side finalizer: measured cleanup and nested record assembly only."""
import argparse, grp, hashlib, importlib.util, json, os, pwd, re, stat, subprocess, sys, time
from pathlib import Path
SOURCE='5b4ed381badb91de47d793715d05b6ae7737e39e'
CAPTURE="4ad23a2fdeccf9abbcd06724b9c97e3a4c1751ecb45e8321cc35bd79c036d540"
CAP=65536
class Reject(Exception):
    def __init__(self, category):
        super().__init__(category); self.category=category
def need(ok, category):
    if not ok: raise Reject(category)
def now_ms():
    return int(time.clock_gettime(time.CLOCK_BOOTTIME)*1000)
def canonical(v):
    return (json.dumps(v,sort_keys=True,separators=(",",":"),ensure_ascii=True,allow_nan=False)+"\n").encode("ascii")
def read_fixed(path, cap, deadline, expected_mode=0o600, *, allow_empty=False):
    """Read one bounded root-owned regular file, rejecting empty by default.

    Only the fixed retry.stdout binding passes allow_empty=True: its successful
    denial contract requires zero bytes. Ownership/mode/link/deadline/hash checks
    are unchanged and remain independent prerequisites.
    """
    need(type(allow_empty) is bool and now_ms()<deadline,"deadline")
    st=path.lstat()
    need(stat.S_ISREG(st.st_mode) and st.st_uid==st.st_gid==0 and stat.S_IMODE(st.st_mode)==expected_mode and st.st_nlink==1 and (0 if allow_empty else 1)<=st.st_size<=cap,"record-shape")
    fd=os.open(path,os.O_RDONLY|os.O_NOFOLLOW|os.O_NONBLOCK|os.O_CLOEXEC)
    try:
        opened=os.fstat(fd); ident=lambda x:(x.st_dev,x.st_ino,x.st_size,x.st_mode,x.st_uid,x.st_gid,x.st_nlink)
        need(ident(st)==ident(opened),"record-substitution")
        data=b""
        while True:
            need(now_ms()<deadline,"deadline")
            block=os.read(fd,min(8192,cap+1-len(data)))
            if not block: break
            data+=block; need(len(data)<=cap and len(data)<=st.st_size,"record-growth")
        need(len(data)==st.st_size and ident(os.fstat(fd))==ident(st),"record-changed")
        return data
    finally: os.close(fd)
def save_fixed(path, raw, deadline):
    need(0<len(raw)<=CAP and now_ms()<deadline,"publication-bound")
    fd=os.open(path,os.O_WRONLY|os.O_CREAT|os.O_EXCL|os.O_NOFOLLOW|os.O_CLOEXEC,0o600)
    try:
        view=memoryview(raw)
        while view:
            need(now_ms()<deadline,"publication-deadline")
            n=os.write(fd,view); need(n>0,"short-write"); view=view[n:]
        os.fsync(fd)
    finally: os.close(fd)
    st=path.lstat()
    need(stat.S_ISREG(st.st_mode) and st.st_uid==st.st_gid==0 and stat.S_IMODE(st.st_mode)==0o600 and st.st_nlink==1 and st.st_size==len(raw),"publication-shape")
def decode(raw):
    def pairs(rows):
        out={}
        for k,v in rows:
            need(k not in out,"duplicate-json"); out[k]=v
        return out
    try:
        return json.loads(raw.decode("utf-8","strict"),object_pairs_hook=pairs,parse_constant=lambda _:(_ for _ in ()).throw(Reject("nonfinite-json")))
    except (UnicodeError,ValueError,RecursionError): raise Reject("record-json") from None
def lookup(kind, number):
    try: return pwd.getpwuid(number) if kind=="user" else grp.getgrgid(number)
    except KeyError: return None
def nss_identity_matches(kind,name,number,by_name,by_number):
    if by_name is None or by_number is None: return False
    name_attr="pw_name" if kind=="user" else "gr_name"
    number_attr="pw_uid" if kind=="user" else "gr_gid"
    return getattr(by_name,name_attr,None)==name and getattr(by_name,number_attr,None)==number and getattr(by_number,name_attr,None)==name
def digest_matches(raw,expected):
    return type(raw) is bytes and type(expected) is str and re.fullmatch(r"[0-9a-f]{64}",expected) is not None and hashlib.sha256(raw).hexdigest()==expected
def utility_succeeded(exit_code):
    return type(exit_code) is int and exit_code==0
def validate_utility_rows(rows,names):
    expected=[("userdel",names["worker"]),("userdel",names["subject"]),
              ("groupdel",names["worker"]),("groupdel",names["subject"]),("groupdel",names["results"])]
    need(type(rows) is list and len(rows)==len(expected),"cleanup-utility-count")
    success=True
    for row,(utility,name) in zip(rows,expected):
        need(type(row) is dict and row.get("utility")==utility and row.get("name")==name,"cleanup-utility-order")
        if row.get("already_absent") is True:
            need(utility=="groupdel" and set(row)=={"utility","name","exit","already_absent"} and row.get("exit") is None,"cleanup-utility-absence")
        else:
            need(set(row)=={"utility","name","exit"} and type(row.get("exit")) is int,"cleanup-utility-exit-shape")
            success &= utility_succeeded(row["exit"])
    return success
def validate_join_observation(pid,proc_present):
    need(type(pid) is int and pid>1 and type(proc_present) is bool and not proc_present,"launcher-not-joined")
def validate_replay_observation(measurement,partial,stdout,stderr,worker_before,worker_after,owner_terminal):
    need(measurement["owner_unit"]==partial["owner"]["unit"] and measurement["worker_unit"]==partial["worker"]["unit"]
         and type(measurement["retry_exit"]) is int and measurement["retry_exit"]==1,"replay-generation-or-exit")
    need(stdout==b"" and b"ASEVD402" in stderr and b'"phase":"start-transient-unit-reply"' not in stderr,
         "replay-output-or-phase")
    need(worker_before==worker_after and owner_terminal=={
         "LoadState":"loaded","ActiveState":"failed","SubState":"failed","ExecMainCode":"1","ExecMainStatus":"1"},
         "replay-worker-or-owner-terminal")
    return {"attempted_same_owner_generation":True,"owner_exit_nonzero":True,
        "reply_phase_absent":True,"worker_start_not_repeated":True,"failure_before_worker_start":True}
def account_rows(snapshot,generation,worker):
    need(type(snapshot) is dict and set(snapshot)=={"names","ids"},"account-shape")
    suffix=generation[:28]
    names={"worker":"evw"+suffix,"subject":"evs"+suffix,"results":"evr"+suffix}
    ids=snapshot["ids"]
    need(snapshot["names"]==names and type(ids) is dict and set(ids)=={"worker_uid","worker_gid","subject_uid","subject_gid","results_gid"},"account-binding")
    need(all(type(v) is int and 0<v<2**32-1 for v in ids.values()) and len(set(ids.values()))==5 and ids["worker_uid"]==worker["uid"] and ids["worker_gid"]==worker["gid"],"account-identities")
    return [("user",names["worker"],ids["worker_uid"]),("user",names["subject"],ids["subject_uid"]),
            ("group",names["worker"],ids["worker_gid"]),("group",names["subject"],ids["subject_gid"]),
            ("group",names["results"],ids["results_gid"])]
def verify_present(rows):
    for kind,name,num in rows:
        by_name=pwd.getpwnam(name) if kind=="user" else grp.getgrnam(name)
        by_num=lookup(kind,num)
        need(nss_identity_matches(kind,name,num,by_name,by_num),"account-reuse-or-missing")
        if kind=="group": need(not by_name.gr_mem,"group-membership")
def verify_absent(rows):
    for kind,name,num in rows:
        try: by_name=pwd.getpwnam(name) if kind=="user" else grp.getgrnam(name)
        except KeyError: by_name=None
        need(by_name is None and lookup(kind,num) is None,"account-remains-or-reused")
def workspace(path):
    st=path.lstat()
    need(stat.S_ISDIR(st.st_mode) and st.st_uid==st.st_gid==0 and stat.S_IMODE(st.st_mode)==0o700,"workspace-custody")
    return {"device":st.st_dev,"inode":st.st_ino,"uid":st.st_uid,"gid":st.st_gid,"mode":stat.S_IMODE(st.st_mode)}
def main():
    ap=argparse.ArgumentParser(allow_abbrev=False)
    ap.add_argument("--execute",action="store_true",required=True); ap.add_argument("--generation",required=True)
    ap.add_argument("--result-directory",required=True); ap.add_argument("--joined-launch-pid",required=True,type=int)
    ap.add_argument("--job-deadline-boot-ms",required=True,type=int); a=ap.parse_args()
    need(os.geteuid()==0 and os.getegid()==0 and sys.platform=="linux","root-platform")
    need(re.fullmatch(r"[0-9a-f]{32}",a.generation) and a.result_directory=="/run/appsurface-evidence-fixture/"+a.generation and a.joined_launch_pid>1,"fixed-arguments")
    deadline=a.job_deadline_boot_ms; need(now_ms()<deadline,"original-job-deadline")
    root=Path(a.result_directory); ws_before=workspace(root)
    partial_raw=read_fixed(root/"partial.json",CAP,deadline); measure_raw=read_fixed(root/"reactivation-measurement.json",CAP,deadline)
    partial=decode(partial_raw); m=decode(measure_raw)
    parser_path=Path(__file__).with_name("check_n15_records.py")
    parser_raw=read_fixed(parser_path,131072,deadline,0o444)
    need(hashlib.sha256(parser_raw).hexdigest()=="fa1fc74882dd5018d5e04128adf3ef30ae19570f4e1eee075eca2c9b57cfa877","parser-pin")
    spec=importlib.util.spec_from_file_location("n15_fixed_parser",parser_path); parser=importlib.util.module_from_spec(spec); spec.loader.exec_module(parser)
    parser.validate_partial_observation(partial,a.generation)
    keys={"schema","generation","owner_unit","worker_unit","retry_exit","retry_stdout_sha256","retry_stderr_sha256","worker_before_after_snapshot_sha256","retry_owner_terminal_snapshot_sha256","raw_logs_retained","synthetic_completion_flags"}
    need(type(m) is dict and set(m)==keys and m["schema"]=="issue779-n15-guard-reactivation-measurement-v1" and m["generation"]==a.generation and m["owner_unit"]==partial["owner"]["unit"] and m["worker_unit"]==partial["worker"]["unit"] and type(m["retry_exit"]) is int and m["retry_exit"]==1 and m["raw_logs_retained"] is True and m["synthetic_completion_flags"] is False,"measurement-binding")
    bindings={"retry.stdout":m["retry_stdout_sha256"],"retry.stderr":m["retry_stderr_sha256"],"worker-before.txt":m["worker_before_after_snapshot_sha256"],"worker-after.txt":m["worker_before_after_snapshot_sha256"],"retry-owner.txt":m["retry_owner_terminal_snapshot_sha256"]}
    raws={}
    for name,digest in bindings.items():
        need(type(digest) is str and re.fullmatch(r"[0-9a-f]{64}",digest),"measurement-digest")
        raws[name]=read_fixed(root/name,65536,deadline,allow_empty=(name=="retry.stdout"))
        need(hashlib.sha256(raws[name]).hexdigest()==digest,"measurement-raw-digest")
    need(all(digest_matches(raws[name],bindings[name]) for name in bindings),"measurement-raw-digest")
    owner_lines=raws["retry-owner.txt"].decode("ascii","strict").splitlines(); owner={}
    for line in owner_lines:
        need("=" in line,"owner-snapshot"); k,v=line.split("=",1)
        need(k in {"LoadState","ActiveState","SubState","ExecMainCode","ExecMainStatus"} and k not in owner,"owner-fields"); owner[k]=v
    need(owner=={"LoadState":"loaded","ActiveState":"failed","SubState":"failed","ExecMainCode":"1","ExecMainStatus":"1"},"retry-owner-terminal")
    try: os.stat(f"/proc/{a.joined_launch_pid}",follow_symlinks=False); launcher_present=True
    except FileNotFoundError: launcher_present=False
    validate_join_observation(a.joined_launch_pid,launcher_present)
    reactivation=validate_replay_observation(m,partial,raws["retry.stdout"],raws["retry.stderr"],
        raws["worker-before.txt"],raws["worker-after.txt"],owner)
    rows=account_rows(partial["account_snapshot"],a.generation,partial["worker"]); verify_present(rows)
    utilities=[]; utility_failed=False
    for kind,name,num in rows[:2]:
        verify_present([(kind,name,num)])
        need(now_ms()<deadline,"cleanup-deadline")
        try:
            cp=subprocess.run(["/usr/sbin/userdel","--",name],stdin=subprocess.DEVNULL,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,timeout=max(.05,min(5,(deadline-now_ms())/1000)),check=False,close_fds=True)
            code=cp.returncode
        except subprocess.TimeoutExpired: code=124
        utilities.append({"utility":"userdel","name":name,"exit":code}); utility_failed |= not utility_succeeded(code)
    for kind,name,num in rows[2:]:
        try: by_name=grp.getgrnam(name)
        except KeyError: by_name=None
        by_num=lookup("group",num)
        if by_name is None and by_num is None:
            utilities.append({"utility":"groupdel","name":name,"exit":None,"already_absent":True})
            continue
        need(nss_identity_matches("group",name,num,by_name,by_num),"group-identity-reused")
        need(not by_name.gr_mem,"group-membership")
        need(now_ms()<deadline,"cleanup-deadline")
        try:
            cp=subprocess.run(["/usr/sbin/groupdel","--",name],stdin=subprocess.DEVNULL,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,timeout=max(.05,min(5,(deadline-now_ms())/1000)),check=False,close_fds=True)
            code=cp.returncode
        except subprocess.TimeoutExpired: code=124
        utilities.append({"utility":"groupdel","name":name,"exit":code}); utility_failed |= not utility_succeeded(code)
    try: verify_absent(rows); absent=True
    except (Reject,KeyError): absent=False
    ws_after=workspace(root); need(ws_after==ws_before,"workspace-changed")
    cleanup={"schema":"issue779-n15-root-account-cleanup-v1","generation":a.generation,"source_commit":SOURCE,"source_capture_sha256":CAPTURE,
        "partial_sha256":hashlib.sha256(partial_raw).hexdigest(),"measurement_sha256":hashlib.sha256(measure_raw).hexdigest(),
        "names":partial["account_snapshot"]["names"],"ids":partial["account_snapshot"]["ids"],"workspace":ws_after,
        "joined_launcher_pid":a.joined_launch_pid,"launcher_absent_after_wait":True,
        "worker_before_after_sha256":m["worker_before_after_snapshot_sha256"],
        "retry_owner_terminal_sha256":m["retry_owner_terminal_snapshot_sha256"],"retry_stdout_sha256":m["retry_stdout_sha256"],
        "retry_stderr_sha256":m["retry_stderr_sha256"],"owner_before_sha256":hashlib.sha256(read_fixed(root/"owner-before.txt",16384,deadline)).hexdigest(),
        "worker_snapshot_unchanged":raws["worker-before.txt"]==raws["worker-after.txt"],"retry_owner_terminal":owner,
        "utilities":utilities,"numeric_and_name_lookup_absent":absent,"native_credit":False}
    utility_rows_succeeded=validate_utility_rows(utilities,partial["account_snapshot"]["names"])
    save_fixed(root/"account-cleanup.json",canonical(cleanup),deadline)
    need(not utility_failed and utility_rows_succeeded and absent,"account-cleanup-failed")
    completion={"schema":parser.COMPLETION_SCHEMA,"case_generation":a.generation,"source_commit":SOURCE,"source_capture_sha256":CAPTURE,"reactivation":reactivation}
    final=parser.assemble_final_record(partial,completion,a.generation); raw=canonical(final)
    save_fixed(root/"n15-final-record.json",raw,deadline)
    save_fixed(root/"n15-record-summary.json",canonical(parser.validate_result(final,a.generation)),deadline)
    print("N15_FINAL_RECORD_MEASURED:NO_NATIVE_CREDIT",flush=True)
    return 0
if __name__=="__main__":
    try: raise SystemExit(main())
    except (Reject,OSError,ValueError,subprocess.SubprocessError) as e:
        code=e.category if isinstance(e,Reject) else "finalizer-operation"
        print("N15_FINALIZATION_REJECTED:"+code,file=sys.stderr); raise SystemExit(1)
