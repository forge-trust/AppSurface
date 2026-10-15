#!/usr/bin/env bash
# Fixed N13/N14 outer owner-death measurement. Validation-shell custody only.
set -euo pipefail
export LC_ALL=C PATH=/usr/sbin:/usr/bin:/sbin:/bin
umask 077
: "${OWNER_DEATH_CASE:?fixed wrapper must set OWNER_DEATH_CASE}"
[[ $OWNER_DEATH_CASE == N13 || $OWNER_DEATH_CASE == N14 ]] || exit 64
readonly ORIGINAL_JOB_SECONDS=240 FIXTURE_SECONDS=600 CLEANUP_RESERVE=30 FIXTURE_REVIEW_CLEAR=1 SOURCE_REVIEW_CLEAR=1 ROOT_REVIEW_CLEAR=1
readonly EXPECTED_HEAD=5b4ed381badb91de47d793715d05b6ae7737e39e EXPECTED_PARENT=914046e1e998ca89a5f0f5be39a90bbfb88a431d
readonly EXPECTED_COUNT=2872 EXPECTED_CAPTURE_SHA=4ad23a2fdeccf9abbcd06724b9c97e3a4c1751ecb45e8321cc35bd79c036d540
mode=prepare-only; selected=0; capture= capture_sha= request= request_sha= host= host_sha= entry= entry_sha= build= build_sha= output= parser= parser_sha= generation=
fail(){ printf 'OWNER_DEATH_FIXTURE_REJECTED:%s\n' "$1" >&2; exit 1; }
while (($#)); do
 case "$1" in
  --prepare-only|--execute) ((selected==0)) || fail duplicate-mode; mode=${1#--}; selected=1; shift ;;
  --source-capture|--source-capture-sha256|--request|--request-sha256|--runtime-host|--runtime-host-sha256|--entry|--entry-sha256|--build-receipt|--build-receipt-sha256|--output-root|--parser|--parser-sha256|--generation)
   (($#>=2)) || fail missing-value
   case "$1" in
    --source-capture) key=capture ;; --source-capture-sha256) key=capture_sha ;;
    --request) key=request ;; --request-sha256) key=request_sha ;;
    --runtime-host) key=host ;; --runtime-host-sha256) key=host_sha ;;
    --entry) key=entry ;; --entry-sha256) key=entry_sha ;;
    --build-receipt) key=build ;; --build-receipt-sha256) key=build_sha ;;
    --output-root) key=output ;; --parser) key=parser ;; --parser-sha256) key=parser_sha ;;
    --generation) key=generation ;;
   esac
   [[ -z ${!key} ]] || fail duplicate-option; printf -v "$key" '%s' "$2"; shift 2 ;;
  *) fail unknown-option ;;
 esac
done
sha(){ [[ $1 =~ ^[0-9a-f]{64}$ ]] || fail sha256-syntax; }
pin(){ local p=$1 h=$2 limit=$3 before after got size; [[ $p == /* && $p != *'/../'* && $p != *'/./'* && -f $p && ! -L $p && $(realpath -e -- "$p") == "$p" ]] || fail pinned-file-path; sha "$h"; before=$(stat -Lc '%d:%i:%s:%f:%h:%u:%g' -- "$p") || fail file-stat; size=$(stat -Lc %s -- "$p") || fail file-stat; ((size<=limit)) || fail file-bound; [[ $(stat -Lc %h -- "$p") == 1 && $(stat -Lc '%u:%g' -- "$p") == 0:0 ]] || fail file-custody; got=$(sha256sum -- "$p"); after=$(stat -Lc '%d:%i:%s:%f:%h:%u:%g' -- "$p") || fail file-stat; [[ $before == "$after" && ${got:0:64} == "$h" ]] || fail file-pin; }
for v in capture capture_sha request request_sha host host_sha entry entry_sha build build_sha output parser parser_sha generation; do [[ -n ${!v} ]] || fail "missing-$v"; done
[[ $capture_sha == "$EXPECTED_CAPTURE_SHA" ]] || fail capture-pin
pin "$capture" "$capture_sha" 8388608; pin "$request" "$request_sha" 65536; pin "$host" "$host_sha" 2147483648; pin "$entry" "$entry_sha" 2147483648; pin "$build" "$build_sha" 4194304; pin "$parser" "$parser_sha" 131072
[[ $generation =~ ^[0-9a-f]{32}$ ]] || fail generation
jq -e --arg h "$EXPECTED_HEAD" --arg p "$EXPECTED_PARENT" --arg t "3bafd0fcd5321f1b29323f46df94b77eb48fa859" --argjson n "$EXPECTED_COUNT" ' . as $r | $r.schema=="issue779-csharp-common-source-capture-v4" and $r.validation_commit==$h and $r.previous_validation_commit==$p and $r.source_count==$n and $r.source_tree==$t and $r.clean==true and $r.index_checksum_verified==true and $r.index_tree_blob_modes_equal==true and $r.no_alternates==true and $r.native_credit==false and $r.all16_final_integrated_native_verified==false and ($r.source|type=="object" and keys==["modes","sha256"]) and ($r.source.sha256|type=="object" and length==$n) and ($r.source.modes|type=="object" and length==$n) and ($r.source.sha256|keys)==($r.source.modes|keys)' "$capture" >/dev/null || fail source-capture-metadata
jq -e --arg c "$OWNER_DEATH_CASE" --arg h "$EXPECTED_HEAD" --argjson n "$EXPECTED_COUNT" '.source_revision==$h and .source_count==$n and .private_qualification==$c' "$build" >/dev/null || fail build-variant-binding
[[ $host != *'$'* && $host != *'%'* && $host != *'='* && $entry != *'$'* && $entry != *'%'* ]] || fail bootstrap-path
if [[ $mode == prepare-only ]]; then printf 'PREPARATION_PINS_MATCHED:%s:NOT_RUN\n' "$OWNER_DEATH_CASE"; exit 0; fi
((EUID==0)) || fail root-required
((SOURCE_REVIEW_CLEAR==1 && FIXTURE_REVIEW_CLEAR==1 && ROOT_REVIEW_CLEAR==1)) || fail independent-review-pending
[[ $OSTYPE == linux* && $(uname -m) == x86_64 ]] || fail linux-x64-required
for t in systemd-run systemctl jq stat sha256sum awk grep sleep install getent realpath timeout setsid cmp cut cat bash date; do command -v "$t" >/dev/null || fail missing-tool; done
mono_ms(){ local up rest frac; IFS=' ' read -r up rest </proc/uptime || return 1; [[ $up =~ ^[0-9]+\.[0-9]+$ ]] || return 1; frac=${up#*.}000; frac=${frac:0:3}; printf '%s' "$((10#${up%%.*}*1000+10#$frac))"; }
start_ms=$(mono_ms) || fail monotonic-clock; hard_end_ms=$((start_ms+FIXTURE_SECONDS*1000)); work_end_ms=$((hard_end_ms-CLEANUP_RESERVE*1000))
remaining(){ local n=$(((work_end_ms-$(mono_ms))/1000)); ((n>0)) || return 1; printf '%s' "$n"; }
remaining_ms(){ local n=$((work_end_ms-$(mono_ms))); ((n>0)) || return 1; printf '%s' "$n"; }
bounded_exec(){ local ms duration; ms=$(remaining_ms) || return 124; ((ms>5000)) && ms=5000; printf -v duration '%d.%03ds' "$((ms/1000))" "$((ms%1000))"; timeout --foreground --signal=KILL "$duration" "$@"; }
systemctl_bounded(){ bounded_exec systemctl "$@"; }
# A lowered two-minute owner plus its original five-second stop reserve must fit
# the already-created request deadline. Integer UTC sampling leaves one second
# of margin; a delayed/unsafe activation still rejects in the C# owner guard.
owner_deadline_fits(){ [[ $1 =~ ^[1-9][0-9]{0,11}$ && $2 =~ ^[1-9][0-9]{0,11}$ ]] && ((10#$1>10#$2 && 10#$1-10#$2>126 && 10#$1-10#$2<=ORIGINAL_JOB_SECONDS)); }
require_original_owner_budget(){
 local utc end now
 pin "$request" "$request_sha" 65536
 utc=$(bounded_exec jq -er '.job_deadline_utc' "$request") || fail owner-original-deadline
 [[ $utc =~ ^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$ ]] || fail owner-original-deadline
 end=$(bounded_exec date -u -d "$utc" +%s) || fail owner-original-deadline
 now=$(bounded_exec date -u +%s) || fail owner-original-deadline
 owner_deadline_fits "$end" "$now" || fail owner-original-budget
}
unit_sample(){ systemctl_bounded show "$1" -p MainPID -p ControlGroup -p ActiveState -p SubState -p Type -p Restart -p RemainAfterExit -p RuntimeMaxUSec -p TimeoutStopUSec -p KillMode -p SendSIGKILL -p FinalKillSignal -p BindsTo -p After -p ActiveEnterTimestampMonotonic -p ExecMainCode -p ExecMainStatus; }
field(){ bounded_exec awk -F= -v k="$2" '$1==k{print substr($0,index($0,"=")+1)}' <<<"$1"; }
systemd_policy_usec(){ case "$1:$2" in RuntimeMaxUSec:2min|RuntimeMaxUSec:120000000) printf '%s' 120000000 ;; TimeoutStopUSec:5s|TimeoutStopUSec:5000000) printf '%s' 5000000 ;; *) return 1 ;; esac; }
unit_absent(){ [[ $(systemctl_bounded show "$1" -p LoadState --value 2>/dev/null || :) == not-found ]]; }
name_absent(){ local kind=$1 name=$2 code=0; bounded_exec getent "$kind" "$name" >/dev/null 2>&1 || code=$?; [[ $code == 2 ]]; }
proc_facts(){ local pid=$1 ms duration; ms=$(remaining_ms) || return 124; ((ms>5000)) && ms=5000; printf -v duration '%d.%03ds' "$((ms/1000))" "$((ms%1000))"; timeout --foreground --signal=KILL "$duration" /bin/bash -c 'set -euo pipefail; pid=$1; [[ -r /proc/$pid/stat && -r /proc/$pid/status && -r /proc/$pid/cgroup ]]; IFS= read -r text </proc/$pid/stat; [[ ${text%% *} == "$pid" ]]; tail=${text##*) }; read -r -a f <<<"$tail"; [[ ${f[19]:-} =~ ^[1-9][0-9]*$ ]]; uid=$(awk '\''/^Uid:/{print $3}'\'' /proc/$pid/status); gid=$(awk '\''/^Gid:/{print $3}'\'' /proc/$pid/status); cg=$(awk -F: '\''$1==0{print $3}'\'' /proc/$pid/cgroup); [[ $uid =~ ^[0-9]+$ && $gid =~ ^[0-9]+$ && $cg == /system.slice/* ]]; printf "%s %s %s %s\n" "${f[19]}" "$uid" "$gid" "$cg"' _ "$pid"; }
pid_absent(){ bounded_exec /bin/bash -c '[[ ! -e /proc/$1 && ! -L /proc/$1 ]]' _ "$1"; }
G=$generation; owner=appsurface-evidence-owner-$G.service; worker=appsurface-evidence-worker-$G.service
expected_output=/run/appsurface-evidence-$G/output
[[ $output == "$expected_output" ]] || fail output-root-binding
names=("evw${G:0:28}" "evs${G:0:28}" "evr${G:0:28}")
base=/var/lib/appsurface-evidence-fixture; stage=$base/$G; private=/run/appsurface-evidence-fixture/$G; guard=/run/appsurface-evidence-owners/$G
for d in /var/lib/appsurface-evidence-fixture /run/appsurface-evidence-fixture; do
 [[ -d $d && ! -L $d && $(stat -Lc '%u:%g:%a' "$d") == 0:0:755 ]] || fail materialized-parent-custody
done
[[ ! -L /run/appsurface-evidence-owners ]] || fail deployment-symlink
install -d -o 0 -g 0 -m 700 /run/appsurface-evidence-owners
[[ -d $stage && ! -L $stage && $(stat -Lc '%u:%g:%a' "$stage") == 0:0:555 && -d $private && ! -L $private && $(stat -Lc '%u:%g:%a' "$private") == 0:0:700 && ! -e $guard && ! -L $guard ]] || fail workspace-materialization-binding
install -d -o 0 -g 0 -m 700 "$guard"
# Account and unit collision checks precede arming the one-use guard and launching C#.
for n in "${names[@]}"; do name_absent passwd "$n" && name_absent group "$n" || fail account-name-collision; done
unit_absent "$owner" && unit_absent "$worker" || fail unit-name-collision
log=$private; result=$private/owner-death-result.json; quarantine=$private/QUARANTINED
printf '%s\n' 'OWNER_DEATH_MEASUREMENT_PENDING_QUARANTINE' >"$quarantine"; chmod 600 "$quarantine"
printf '%s\n' "$G" >"$guard/armed"; chmod 600 "$guard/armed"
[[ $(stat -Lc '%u:%g:%a:%h' "$guard/armed") == 0:0:600:1 ]] || fail armed-guard
launched=0; measurement_done=0; signal_sent=0; success=0; launch_pid=; launch_pgid=; launch_status=; launch_ready_file=; launch_guard_pid=; launch_owner_socket=; phase_seen=0
owner_pid= worker_pid= owner_start= owner_uid= owner_gid= owner_cgroup= worker_start= worker_uid= worker_gid= worker_cgroup=
mark_failure(){ printf '%s\n' 'N13_N14_MEASUREMENT_FAILED_OUTPUT_QUARANTINED' >"$quarantine" 2>/dev/null || :; }
read -r -d '' LAUNCHER_OWNER_CODE <<'LAUNCHER_OWNER_PY' || :
import errno, os, resource, select, signal, socket, stat, sys, time
pidfile, control, ready, deadline_text, *argv = sys.argv[1:]
deadline = int(deadline_text) / 1000
def clock():
    return time.clock_gettime(time.CLOCK_BOOTTIME) if sys.platform == 'linux' else time.monotonic()
child = None
listener = None
joined = False
signal.signal(signal.SIGCHLD, signal.SIG_DFL)
def remaining():
    value = deadline - clock()
    if value <= 0: raise TimeoutError()
    return value
def state():
    result = os.waitid(os.P_PID, child, os.WEXITED | os.WNOHANG | os.WNOWAIT)
    return result
def group_others():
    # The unreaped child reserves both its PID and its eventual PGID.
    if os.path.isdir('/proc/self'):
        for name in os.listdir('/proc'):
            remaining()
            if not name.isdecimal() or int(name) == child: continue
            try:
                with open('/proc/' + name + '/stat', 'r') as stream: text = stream.read(8192)
            except FileNotFoundError: continue
            fields = text.rsplit(')', 1)[1].split()
            if int(fields[2]) == child: return 1
        return 0
    # Portable procedure tests use ps only; native qualification requires procfs.
    import subprocess
    rows = subprocess.run(['/bin/ps', '-axo', 'pid=,pgid='], capture_output=True,
                          check=True, timeout=min(1, remaining())).stdout.decode().splitlines()
    return int(any(int(row.split()[1]) == child and int(row.split()[0]) != child for row in rows if row.strip()))
def send(sig, final_cleanup=False):
    # No waitpid or SIGCHLD handler reaps the child in this interval.
    exited = state() is not None
    if not final_cleanup and exited and group_others() == 0: return
    try:
        group = os.getpgid(child) == child
    except ProcessLookupError:
        # Some portable kernels hide getpgid for an unreaped zombie. The
        # child-only exclusive marker still binds its own reserved group ID.
        if not exited: raise
        group = False
        if os.path.exists(ready):
            with open(ready, 'r') as stream: group = stream.read(32).strip() == str(child)
    if group:
        try: os.killpg(child, sig)
        except ProcessLookupError: pass
    elif not exited:
        os.kill(child, sig)
def summary(label):
    value = state()
    code = '-' if value is None else str(value.si_status if value.si_code == os.CLD_EXITED else 128 + value.si_status)
    return ('live' if value is None else label) + ' ' + str(group_others()) + ' ' + code + '\n'
try:
    if not argv or clock() >= deadline: raise ValueError()
    listener = socket.socket(socket.AF_UNIX)
    listener.bind(control); os.chmod(control, 0o600); listener.listen(1)
    gate_r, gate_w = os.pipe()
    child = os.fork()
    if child == 0:
        listener.close(); os.close(gate_w)
        if os.read(gate_r, 1) != b'S': os._exit(125)
        os.close(gate_r); os.setsid()
        fd = os.open(ready, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
        os.write(fd, (str(os.getpid()) + '\n').encode()); os.close(fd)
        resource.setrlimit(resource.RLIMIT_CORE, (0, 0))
        resource.setrlimit(resource.RLIMIT_FSIZE, (134217728, 134217728))
        os.execvp(argv[0], argv)
    os.close(gate_r)
    fd = os.open(pidfile, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    os.write(fd, (str(child) + '\n').encode()); os.close(fd)
    os.write(gate_w, b'S'); os.close(gate_w)
    while not joined:
        if remaining() <= 1: raise TimeoutError()
        listener.settimeout(min(1, remaining() - .5))
        try: connection, _ = listener.accept()
        except socket.timeout: continue
        with connection:
            connection.settimeout(min(1, remaining()))
            wire = b''
            while not wire.endswith(b'\n'):
                chunk = connection.recv(64 - len(wire))
                if not chunk: raise ValueError()
                wire += chunk
                if len(wire) >= 64: raise ValueError()
            op, expected = wire.decode('ascii').strip().split(' ')
            if expected != str(child): raise ValueError()
            if op in ('term', 'kill'):
                send(signal.SIGTERM if op == 'term' else signal.SIGKILL)
            elif op == 'join':
                if state() is None or group_others(): raise ValueError()
                _, status = os.waitpid(child, 0)
                code = os.waitstatus_to_exitcode(status)
                code = code if code >= 0 else 128 - code
                connection.sendall(('joined 0 ' + str(code) + '\n').encode())
                joined = True
                break
            elif op != 'state': raise ValueError()
            connection.sendall(summary('exited').encode())
finally:
    if not joined and child is not None:
        # Even deadline/error cleanup uses the still-owned child, never a reaped PID.
        try: send(signal.SIGKILL, final_cleanup=True)
        except (ChildProcessError, ProcessLookupError, PermissionError): pass
        while clock() < deadline:
            try:
                if state() is not None and group_others() == 0:
                    os.waitpid(child, 0)
                    break
            except ChildProcessError: break
            except TimeoutError: break
            time.sleep(.01)
    if listener is not None: listener.close()
LAUNCHER_OWNER_PY
read -r -d '' LAUNCHER_OWNER_CLIENT <<'LAUNCHER_CLIENT_PY' || :
import socket, sys, time
path, operation, expected, deadline = sys.argv[1:]
clock = time.clock_gettime(time.CLOCK_BOOTTIME) if sys.platform == 'linux' else time.monotonic()
remaining = int(deadline) / 1000 - clock
if remaining <= 0: raise TimeoutError()
with socket.socket(socket.AF_UNIX) as stream:
    stream.settimeout(min(1, remaining)); stream.connect(path)
    stream.sendall((operation + ' ' + expected + '\n').encode('ascii'))
    data = b''
    while not data.endswith(b'\n'):
        chunk = stream.recv(64 - len(data))
        if not chunk: raise ValueError()
        data += chunk
        if len(data) >= 64: raise ValueError()
    sys.stdout.write(data.decode('ascii'))
LAUNCHER_CLIENT_PY
# BEGIN PREMEASUREMENT CLEANUP PROCEDURE
cleanup_deadline_ms=0 cleanup_cgroup_root=/sys/fs/cgroup/system.slice
cleanup_remaining_ms(){ local now; now=$(mono_ms) || return 1; ((now<cleanup_deadline_ms)) || return 1; printf '%s' "$((cleanup_deadline_ms-now))"; }
cleanup_exec(){ local ms duration; ms=$(cleanup_remaining_ms) || return 124; ((ms>5000)) && ms=5000; printf -v duration '%d.%03ds' "$((ms/1000))" "$((ms%1000))"; timeout --foreground --signal=KILL "$duration" "$@"; }
cleanup_field(){ local facts=$1 key=$2 line; while IFS= read -r line; do if [[ ${line%%=*} == "$key" ]]; then printf '%s' "${line#*=}"; return 0; fi; done <<<"$facts"; return 1; }
cleanup_unit_snapshot(){ cleanup_exec systemctl show "$1" -p LoadState -p ActiveState -p SubState -p MainPID -p ControlGroup; }
cleanup_job_state(){ local unit=$1 rows job candidate rest found=0; rows=$(cleanup_exec systemctl list-jobs --no-legend --no-pager) || return 2; while read -r job candidate rest; do [[ $candidate == "$unit" ]] && found=1; done <<<"$rows"; ((found==1)) && return 0 || return 1; }
cleanup_cgroup_state(){
 local unit=$1 cg=$2 path="$cleanup_cgroup_root/$1" events key value populated=
 if [[ -e $path || -L $path ]]; then
  [[ ! -L $path && -d $path && -r $path/cgroup.events && $cg == "/system.slice/$unit" ]] || return 2
  events=$(cleanup_exec cat "$path/cgroup.events") || return 2
  while read -r key value; do if [[ $key == populated ]]; then [[ -z $populated ]] || return 2; populated=$value; fi; done <<<"$events"
  [[ $populated == 0 || $populated == 1 ]] || return 2
  [[ $populated == 0 ]] && return 0 || return 1
 fi
 [[ ! -L $path && ( -z $cg || $cg == "/system.slice/$unit" ) ]] || return 2
 return 0
}
cleanup_probe(){
 local unit=$1 expected facts load state sub main cg jrc crc
 [[ $G =~ ^[0-9a-f]{32}$ ]] || return 2
 case "$unit" in "appsurface-evidence-owner-$G.service"|"appsurface-evidence-worker-$G.service") ;; *) return 2 ;; esac
 expected="/system.slice/$unit"; facts=$(cleanup_unit_snapshot "$unit") || return 2
 load=$(cleanup_field "$facts" LoadState) || return 2; state=$(cleanup_field "$facts" ActiveState) || return 2
 sub=$(cleanup_field "$facts" SubState) || return 2; main=$(cleanup_field "$facts" MainPID) || return 2
 cg=$(cleanup_field "$facts" ControlGroup) || return 2
 [[ -z $cg || $cg == "$expected" ]] || return 2
 case "$load" in
  not-found) [[ -z $cg && ( -z $main || $main == 0 ) && ( -z $state || $state == inactive ) && ( -z $sub || $sub == dead ) ]] || return 2
   if cleanup_job_state "$unit"; then jrc=0; else jrc=$?; fi
   ((jrc!=2)) || return 2
   cleanup_cgroup_state "$unit" "$expected"; crc=$?; ((crc==0)) || return 2
   ((jrc==0)) && return 1
   return 0 ;;
  loaded) ;;
  *) return 2 ;;
 esac
 [[ $main =~ ^(0|[1-9][0-9]*)$ && -n $state && -n $sub ]] || return 2
 case "$state" in active|activating|deactivating|inactive|failed) ;; *) return 2 ;; esac
 if [[ $state == active || $state == activating || $state == deactivating ]]; then ((main==0)) || [[ $cg == "$expected" ]] || return 2; return 1; fi
 if [[ $main != 0 || ( $sub != dead && $sub != failed ) ]]; then [[ $cg == "$expected" ]] || return 2; return 1; fi
 if cleanup_job_state "$unit"; then jrc=0; else jrc=$?; fi
 ((jrc!=2)) || return 2
 ((jrc==0)) && return 1
 cleanup_cgroup_state "$unit" "$cg"; crc=$?; case $crc in 0) return 0 ;; 1) return 1 ;; *) return 2 ;; esac
}
cleanup_targets(){
 local unit rc; CLEANUP_TARGET_LIST=()
 for unit in "$worker" "$owner"; do
  if cleanup_probe "$unit"; then rc=0; else rc=$?; fi
  ((rc!=2)) || return 1
  ((rc==0)) || CLEANUP_TARGET_LIST+=("$unit")
 done
}
cleanup_wait_settled(){
 local until_ms=$1 unit rc now pending
 while :; do pending=0
  for unit in "$worker" "$owner"; do
   if cleanup_probe "$unit"; then rc=0; else rc=$?; fi
   ((rc!=2)) || return 2
   ((rc==0)) || pending=1
  done
  ((pending==0)) && return 0
  now=$(mono_ms) || return 2; ((now<until_ms)) || return 1
  cleanup_exec sleep .05 || return 2
 done
}
# The fixture-only parent keeps its launcher unreaped until final group join.
# Numeric child/group IDs therefore cannot be recycled between query and signal.
launcher_owner_start(){
 local pidfile=$1 control=$2 ready=$3 deadline=$4; shift 4
 [[ ! -e $pidfile && ! -L $pidfile && ! -e $control && ! -L $control ]] || return 1
 /usr/bin/python3.12 -I -S -B -c "$LAUNCHER_OWNER_CODE" "$pidfile" "$control" "$ready" "$deadline" "$@" & launch_guard_pid=$!
 launch_owner_socket=$control; launch_pid=; launch_pgid=
 local now
 while [[ ! -f $pidfile ]]; do
  now=$(mono_ms) || return 1; ((now<deadline)) || return 1
  /bin/sleep .01
 done
 launch_pid=$(<"$pidfile")
 [[ $launch_pid =~ ^[1-9][0-9]*$ ]] || return 1
 launcher_owner_call state || return 1
}
launcher_owner_call(){
 local op=$1 until=${cleanup_deadline_ms:-0} answer
 [[ $until =~ ^[0-9]+$ && $until != 0 ]] || until=$hard_end_ms
 [[ -n ${launch_owner_socket:-} && $launch_pid =~ ^[1-9][0-9]*$ ]] || return 1
 answer=$(/usr/bin/python3.12 -I -S -B -c "$LAUNCHER_OWNER_CLIENT" "$launch_owner_socket" "$op" "$launch_pid" "$until") || return 1
 read -r launcher_state launcher_others launcher_exit <<<"$answer"
 [[ $launcher_state == live || $launcher_state == exited || $launcher_state == joined ]] || return 1
 [[ $launcher_others == 0 || $launcher_others == 1 ]] || return 1
 [[ $launcher_exit == - || $launcher_exit =~ ^[0-9]+$ ]] || return 1
}
cleanup_child_zombie(){
 [[ $1 == "$launch_pid" ]] || return 2
 launcher_owner_call state || return 2
 [[ $launcher_state == exited ]]
}
cleanup_launch_ready(){
 local ready_pid
 [[ -n ${launch_ready_file:-} && -f $launch_ready_file && ! -L $launch_ready_file ]] || return 1
 ready_pid=$(<"$launch_ready_file")
 [[ $ready_pid =~ ^[1-9][0-9]*$ && $ready_pid == "$launch_pid" ]] || return 2
 launch_pgid=$ready_pid
 return 0
}
cleanup_guard_exited(){
 local text tail state pid=$launch_guard_pid
 [[ $pid =~ ^[1-9][0-9]*$ ]] || return 2
 [[ ! -e /proc/$pid && ! -L /proc/$pid ]] && return 0
 [[ -r /proc/$pid/stat && ! -L /proc/$pid/stat ]] || return 2
 IFS= read -r text </proc/$pid/stat || return 2
 [[ ${text%% *} == "$pid" ]] || return 2
 tail=${text##*)}; read -r state _ <<<"$tail"
 [[ $state == Z ]]
}
cleanup_reap_launcher(){
 launcher_owner_call join || return 1
 [[ $launcher_state == joined && $launcher_others == 0 && $launcher_exit =~ ^[0-9]+$ ]] || return 1
 local guard_status=0 observed=0
 # No signals target this queried guard PID. Absence or zombie precedes wait;
 # a reused PID can only delay/reject cleanup, never authorize a signal.
 while :; do
  if cleanup_guard_exited; then break; else observed=$?; fi
  ((observed==1)) || return 1
  cleanup_remaining_ms >/dev/null || return 1
  cleanup_exec sleep .01 || return 1
 done
 wait "$launch_guard_pid" || guard_status=$?
 ((guard_status==0)) || return 1
 launch_status=$launcher_exit; launch_pid= launch_pgid= launch_guard_pid=
}
cleanup_unready_launcher(){ cleanup_launch_group 1; }
cleanup_launch_group(){
 local final=${1:-0} now grace_end rc
 [[ -n $launch_pid ]] || return 0
 if cleanup_launch_ready; then :; else rc=$?; ((rc==1)) || return 1; fi
 launcher_owner_call state || return 1
 if [[ $launcher_state == exited && $launcher_others == 0 ]]; then cleanup_reap_launcher; return $?; fi
 # A non-final pass preserves an unregistered live child until units settle.
 [[ -n $launch_pgid || $final == 1 ]] || return 1
 launcher_owner_call term || return 1
 local grace_ms=1000; [[ -n $launch_pgid ]] || grace_ms=250
 now=$(mono_ms) || return 1; grace_end=$((now+grace_ms)); ((grace_end<cleanup_deadline_ms)) || grace_end=$cleanup_deadline_ms
 while :; do
  launcher_owner_call state || return 1
  if [[ $launcher_state == exited && $launcher_others == 0 ]]; then cleanup_reap_launcher; return $?; fi
  now=$(mono_ms) || return 1; ((now<grace_end)) || break
  cleanup_exec sleep .05 || return 1
 done
 launcher_owner_call kill || return 1
 while :; do
  launcher_owner_call state || return 1
  if [[ $launcher_state == exited && $launcher_others == 0 ]]; then cleanup_reap_launcher; return $?; fi
  cleanup_remaining_ms >/dev/null || return 1
  cleanup_exec sleep .05 || return 1
 done
}
cleanup_final_unit_join(){
 local now stop_end stop_rc=0 rc kill_rc=0
 # A launcher may have submitted a start while its child/group was joining.
 # Reinspect the same generated names only after that original launcher is gone.
 [[ -z $launch_pid && -z $launch_pgid ]] || return 1
 cleanup_targets || return 1
 local -a targets=("${CLEANUP_TARGET_LIST[@]+"${CLEANUP_TARGET_LIST[@]}"}")
 if ((${#targets[@]}==0)); then return 0; fi
 cleanup_exec systemctl stop --no-block "${targets[@]}" || stop_rc=$?
 now=$(mono_ms) || return 1; stop_end=$((now+5000)); ((stop_end<cleanup_deadline_ms)) || stop_end=$cleanup_deadline_ms
 if ((stop_rc==0)); then
  if cleanup_wait_settled "$stop_end"; then return 0; else rc=$?; fi
  ((rc==1)) || return 1
 fi
 cleanup_targets || return 1; targets=("${CLEANUP_TARGET_LIST[@]+"${CLEANUP_TARGET_LIST[@]}"}")
 if ((${#targets[@]})); then cleanup_exec systemctl kill --kill-whom=all --signal=KILL "${targets[@]}" || kill_rc=$?; fi
 cleanup_wait_settled "$cleanup_deadline_ms" || return 1
 return 0
}
premeasurement_cleanup(){
 local now stop_rc=0 unit rc stop_end kill_rc=0 group_rc=0
 [[ $G =~ ^[0-9a-f]{32}$ && $owner == "appsurface-evidence-owner-$G.service" && $worker == "appsurface-evidence-worker-$G.service" ]] || return 1
 now=$(mono_ms) || return 1; cleanup_deadline_ms=$((now+CLEANUP_RESERVE*1000)); ((cleanup_deadline_ms<hard_end_ms)) || cleanup_deadline_ms=$hard_end_ms
 cleanup_targets || return 1; local -a targets=("${CLEANUP_TARGET_LIST[@]+"${CLEANUP_TARGET_LIST[@]}"}")
 if ((${#targets[@]})); then cleanup_exec systemctl stop --no-block "${targets[@]}" || stop_rc=$?; fi
 cleanup_launch_group || group_rc=$?
 now=$(mono_ms) || return 1; stop_end=$((now+5000)); ((stop_end<cleanup_deadline_ms)) || stop_end=$cleanup_deadline_ms
 if ((stop_rc==0)); then
  if cleanup_wait_settled "$stop_end"; then rc=0; else rc=$?; fi
  if ((rc==0)); then
   if ((group_rc!=0)); then cleanup_launch_group 1 || return 1; fi
   cleanup_final_unit_join
   return $?
  fi
  ((rc==1)) || return 1
 fi
 cleanup_targets || return 1; targets=("${CLEANUP_TARGET_LIST[@]+"${CLEANUP_TARGET_LIST[@]}"}")
 if ((${#targets[@]})); then cleanup_exec systemctl kill --kill-whom=all --signal=KILL "${targets[@]}" || kill_rc=$?; fi
 cleanup_wait_settled "$cleanup_deadline_ms" || return 1
 if ((group_rc!=0)); then cleanup_launch_group 1 || return 1; fi
 cleanup_final_unit_join
 return $?
}
cleanup(){
 local rc=$? wait_status=0 cleanup_status=0
 trap - EXIT INT TERM; if ((rc!=0 || success!=1)); then mark_failure; fi
 if ((launched && measurement_done==0 && success==0)); then
  if premeasurement_cleanup; then :; else cleanup_status=$?; fi
  if ((cleanup_status!=0)); then mark_failure; ((rc!=0)) || rc=1; printf '%s\n' 'OWNER_DEATH_CLEANUP_UNRESOLVED_OUTPUT_QUARANTINED' >&2; fi
 elif ((launched && measurement_done==1 && success==0)); then
  bounded_exec systemctl kill --kill-whom=all --signal=KILL "$worker" "$owner" >/dev/null 2>&1 || :
  if [[ -n $launch_pid ]]; then cleanup_deadline_ms=$hard_end_ms; cleanup_launch_group 1 || cleanup_status=$?; fi
 fi
 # Never delete accounts or workspace: preserve generation ownership and prevent UID reuse.
 exit "$rc"
}
# END PREMEASUREMENT CLEANUP PROCEDURE
trap cleanup EXIT; trap 'exit 1' INT TERM
# Inherited launch recipe is fixed to the existing root-owned owner service; C# creates the dependent worker.
fixed_env=(PATH=/usr/bin:/bin HOME=/nonexistent LANG=C.UTF-8 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_CLI_HOME=/tmp)
unsafe='DOTNET_STARTUP_HOOKS DOTNET_ADDITIONAL_DEPS DOTNET_SHARED_STORE DOTNET_ROOT DOTNET_ROOT_X64 DOTNET_HOST_PATH DOTNET_ROLL_FORWARD DOTNET_ROLL_FORWARD_TO_PRERELEASE DOTNET_MULTILEVEL_LOOKUP CORECLR_ENABLE_PROFILING CORECLR_PROFILER CORECLR_PROFILER_PATH CORECLR_PROFILER_PATH_64 COR_ENABLE_PROFILING COR_PROFILER COR_PROFILER_PATH COMPlus_ReadyToRun COMPlus_ZapDisable'
props=(--property=Type=exec --property=User=0 --property=Group=0 --property=KillMode=control-group --property=Restart=no --property=RemainAfterExit=no --property=SendSIGKILL=yes --property=FinalKillSignal=9 --property=RuntimeMaxSec=120s --property=TimeoutStopSec=5s "--property=ConditionPathExists=$guard/armed" --property=PassEnvironment= "--property=UnsetEnvironment=$unsafe" "--property=Environment=${fixed_env[*]}")
# The pinned case-workspace materializer completed full source/tool/runtime copy verification.
# This entrypoint consumes its sealed request/runtime/entry outputs; it never restages trees.
((work_end_ms-$(mono_ms)>0)) || fail original-deadline
require_original_owner_budget
launched=1
launch_ready_file=$log/launcher.setsid-ready
# The launcher can be delayed after the parent preflight. Repeat the pinned
# request check in its actual owned child immediately before exec of systemd-run.
read -r -d '' OWNER_LAUNCH_BUDGET_CODE <<'OWNER_LAUNCH_BUDGET_SH' || :
set -euo pipefail
request=$1; expected_sha=$2; generation=$3; shift 3
[[ $generation =~ ^[0-9a-f]{32}$ && $expected_sha =~ ^[0-9a-f]{64}$
   && $request == /run/appsurface-evidence-fixture/$generation/request.json
   && -f $request && ! -L $request
   && $(/usr/bin/stat -c '%u:%g:%a:%h' -- "$request") == 0:0:600:1
   && $(/usr/bin/stat -c %s -- "$request") -le 65536 ]]
before=$(/usr/bin/stat -c '%d:%i:%s:%f:%h:%u:%g:%y:%z' -- "$request")
hash=$(/usr/bin/sha256sum -- "$request"); [[ ${hash:0:64} == "$expected_sha" ]]
utc=$(/usr/bin/jq -er '.job_deadline_utc' "$request")
[[ $utc =~ ^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$ ]]
end=$(/usr/bin/date -u -d "$utc" +%s); now=$(/usr/bin/date -u +%s)
[[ $end =~ ^[1-9][0-9]{0,11}$ && $now =~ ^[1-9][0-9]{0,11}$ ]]
((10#$end>10#$now && 10#$end-10#$now>126 && 10#$end-10#$now<=240))
[[ $(/usr/bin/stat -c '%d:%i:%s:%f:%h:%u:%g:%y:%z' -- "$request") == "$before" ]]
exec "$@"
OWNER_LAUNCH_BUDGET_SH
launcher_owner_start "$log/launcher.child-pid" "$log/launcher.control.sock" "$launch_ready_file" "$hard_end_ms" timeout --signal=KILL "$(remaining)" /usr/bin/bash --noprofile --norc -c "$OWNER_LAUNCH_BUDGET_CODE" -- "$request" "$request_sha" "$G" systemd-run --quiet --wait --pipe --unit="$owner" "${props[@]}" /usr/bin/env -i "${fixed_env[@]}" "$host" "$entry" evidence supervise --request "$request" >"$log/root.stdout" 2>"$log/root.stderr" || fail launcher-owner-start

# Source-owned phase is emitted after the authenticated accepted reply commits; no case-specific
# STOP/WAIT/EXIT is sent. It only arms the following independent identity measurements.
expected="NATIVE_PRIVATE_PHASE:$OWNER_DEATH_CASE:ACCEPTED_BLOCKED_WORK"
while (( $(mono_ms) < work_end_ms )); do
 if [[ -f $log/root.stdout && ! -L $log/root.stdout ]]; then
  [[ $(stat -Lc %s "$log/root.stdout") -le 4096 ]] || fail phase-output-bound
  if grep -Fxq "$expected" "$log/root.stdout"; then phase_seen=1; break; fi
 fi
 sleep .02
done
((phase_seen==1)) || fail accepted-phase-timeout
 # The worker itself creates this generation output directory. Never precreate or proxy it.
 [[ -d $output && ! -L $output && $(bounded_exec realpath -e -- "$output") == "$expected_output" ]] || fail output-root-after-phase
 positive_before=$(bounded_exec find -P "$output" -mindepth 1 -maxdepth 1 -print -quit) || fail output-scan-before
 [[ -z $positive_before ]] || fail positive-output-before-signal
[[ $(stat -Lc '%u:%g:%a' "$guard") == 0:0:700 ]] || fail armed-guard-directory
owner_facts=$(unit_sample "$owner") || fail owner-unit-sample; worker_facts=$(unit_sample "$worker") || fail worker-unit-sample
owner_pid=$(field "$owner_facts" MainPID); worker_pid=$(field "$worker_facts" MainPID)
[[ $owner_pid =~ ^[1-9][0-9]*$ && $worker_pid =~ ^[1-9][0-9]*$ ]] || fail mainpid
owner_cgroup=$(field "$owner_facts" ControlGroup); worker_cgroup=$(field "$worker_facts" ControlGroup)
[[ $owner_cgroup == "/system.slice/$owner" && $worker_cgroup == "/system.slice/$worker" ]] || fail cgroup-binding
[[ " $(field "$worker_facts" BindsTo) " == *" $owner "* && " $(field "$worker_facts" After) " == *" $owner "* ]] || fail dependent-unit-binding
[[ $(field "$owner_facts" Type) == exec && $(field "$owner_facts" Restart) == no && $(field "$owner_facts" RemainAfterExit) == no && $(field "$owner_facts" KillMode) == control-group && $(field "$owner_facts" SendSIGKILL) == yes && $(field "$owner_facts" FinalKillSignal) == 9 ]] || fail owner-runtime-policy
read -r owner_start owner_uid owner_gid owner_cg < <(proc_facts "$owner_pid") || fail owner-proc-bind
read -r worker_start worker_uid worker_gid worker_cg < <(proc_facts "$worker_pid") || fail worker-proc-bind
[[ $(field "$owner_facts" ActiveState) == active && $(field "$owner_facts" SubState) == running && $(field "$worker_facts" ActiveState) == active && $(field "$worker_facts" SubState) == running ]] || fail unit-not-running
[[ $owner_uid == 0 && $owner_gid == 0 && $owner_cg == "$owner_cgroup" && $worker_uid =~ ^[1-9][0-9]*$ && $worker_gid =~ ^[1-9][0-9]*$ && $worker_cg == "$worker_cgroup" ]] || fail process-unit-bind
[[ $(stat -Lc '%u:%g:%a:%h' "$guard/consumed") == 0:0:600:1 && $(cat "$guard/consumed") == "$G" ]] || fail activation-guard-not-consumed
active_enter_usec=$(field "$owner_facts" ActiveEnterTimestampMonotonic)
[[ $active_enter_usec =~ ^[1-9][0-9]*$ ]] || fail owner-activation-time
runtime_show=$(field "$owner_facts" RuntimeMaxUSec); timeout_show=$(field "$owner_facts" TimeoutStopUSec)
runtime_usec=$(systemd_policy_usec RuntimeMaxUSec "$runtime_show") || fail owner-runtime-policy
timeout_usec=$(systemd_policy_usec TimeoutStopUSec "$timeout_show") || fail owner-runtime-policy
[[ $runtime_usec == 120000000 && $timeout_usec == 5000000 ]] || fail owner-runtime-policy
# Immediate pre-signal reread binds PID/starttime/UID/GID/cgroup; signal exactly the owner PID.
read -r check_start check_uid check_gid check_cg < <(proc_facts "$owner_pid") || fail owner-presignal-reread
[[ $check_start == "$owner_start" && $check_uid == "$owner_uid" && $check_gid == "$owner_gid" && $check_cg == "$owner_cg" ]] || fail owner-presignal-changed
read -r worker_check_start worker_check_uid worker_check_gid worker_check_cg < <(proc_facts "$worker_pid") || fail worker-presignal-reread
[[ $worker_check_start == "$worker_start" && $worker_check_uid == "$worker_uid" && $worker_check_gid == "$worker_gid" && $worker_check_cg == "$worker_cg" ]] || fail worker-presignal-changed
owner_pre_facts=$(unit_sample "$owner") || fail owner-presignal-unit
worker_pre_facts=$(unit_sample "$worker") || fail worker-presignal-unit
[[ $(field "$owner_pre_facts" MainPID) == "$owner_pid" && $(field "$worker_pre_facts" MainPID) == "$worker_pid" && $(field "$owner_pre_facts" ActiveState) == active && $(field "$worker_pre_facts" ActiveState) == active ]] || fail presignal-unit-changed
for property in RuntimeMaxUSec TimeoutStopUSec KillMode SendSIGKILL FinalKillSignal Restart RemainAfterExit ActiveEnterTimestampMonotonic; do [[ $(field "$owner_pre_facts" "$property") == "$(field "$owner_facts" "$property")" ]] || fail owner-policy-changed; done
signal_start=$(mono_ms) || fail monotonic-clock
if [[ $OWNER_DEATH_CASE == N13 ]]; then kill -KILL "$owner_pid" || fail owner-sigkill; else kill -STOP "$owner_pid" || fail owner-sigstop; fi
signal_sent=1
signal_from_activation_usec=$((signal_start*1000-active_enter_usec)); ((signal_from_activation_usec>=0 && signal_from_activation_usec<runtime_usec)) || fail signal-outside-original-runtime
# N14 sends no follow-up signal and creates no timer: wait for the already-running systemd
# RuntimeMax/TimeoutStop/final-kill policy, within the original monotonic job bound.
owner_stop_deadline_ms=$(((active_enter_usec+runtime_usec+timeout_usec)/1000))
while (( $(mono_ms) < owner_stop_deadline_ms )); do
 owner_facts_live=$(unit_sample "$owner") || fail owner-terminal-sample
 worker_facts_live=$(unit_sample "$worker") || fail worker-terminal-sample
 owner_state=$(field "$owner_facts_live" ActiveState); worker_state=$(field "$worker_facts_live" ActiveState)
 if [[ $owner_state =~ ^(inactive|failed)$ && $worker_state =~ ^(inactive|failed)$ && $(field "$owner_facts_live" MainPID) == 0 && $(field "$worker_facts_live" MainPID) == 0 ]]; then break; fi
 sleep .05
done
owner_terminal=$(unit_sample "$owner") || fail owner-terminal-sample
worker_terminal=$(unit_sample "$worker") || fail worker-terminal-sample
owner_state=$(field "$owner_terminal" ActiveState); worker_state=$(field "$worker_terminal" ActiveState)
[[ $owner_state =~ ^(inactive|failed)$ && $worker_state =~ ^(inactive|failed)$ && $(field "$owner_terminal" MainPID) == 0 && $(field "$worker_terminal" MainPID) == 0 ]] || fail units-not-terminal
[[ $(field "$owner_terminal" ExecMainCode) == 2 && $(field "$owner_terminal" ExecMainStatus) == 9 ]] || fail owner-terminal-not-sigkill
[[ $(field "$owner_terminal" SubState) =~ ^(dead|failed)$ && $(field "$worker_terminal" SubState) =~ ^(dead|failed)$ ]] || fail unit-terminal-substate
[[ -d $stage && ! -L $stage && $(stat -Lc '%u:%g:%a' "$stage") == 0:0:555 && -d $private && ! -L $private && $(stat -Lc '%u:%g:%a' "$private") == 0:0:700 ]] || fail quarantine-workspace-custody
for pid in "$owner_pid" "$worker_pid"; do pid_absent "$pid" || fail original-process-remains; done
for unit in "$owner" "$worker"; do
 cg=/sys/fs/cgroup/system.slice/$unit
 if [[ -e $cg || -L $cg ]]; then [[ ! -L $cg && -r $cg/cgroup.events && $(bounded_exec awk '$1=="populated"{print $2}' "$cg/cgroup.events") == 0 ]] || fail cgroup-not-empty
 else [[ $(systemctl_bounded show "$unit" -p ControlGroup --value 2>/dev/null || :) == '' ]] || fail cgroup-disappearance-unbound; fi
done
# wait is the actual systemd-run EOF/join boundary; preserve its numeric status as data.
cleanup_deadline_ms=$work_end_ms
while :; do
 launcher_owner_call state || fail launcher-owner-query
 [[ $launcher_state == exited && $launcher_others == 0 ]] && break
 remaining_ms >/dev/null || fail launcher-join-deadline
 /bin/sleep .02
done
cleanup_reap_launcher || fail launcher-owner-join
[[ -f $log/root.stdout && -f $log/root.stderr && ! -L $log/root.stdout && ! -L $log/root.stderr ]] || fail root-streams
[[ $(stat -Lc %s "$log/root.stdout") -le 4096 && $(stat -Lc %s "$log/root.stderr") -le 131072 ]] || fail root-stream-bound
printf '%s\n' "$expected" | cmp -s - "$log/root.stdout" || fail unexpected-root-stdout
positive_after=$(bounded_exec find -P "$output" -mindepth 1 -maxdepth 1 -print -quit) || fail output-scan-after
[[ -z $positive_after ]] || fail positive-output-present
worker_passwd=$(bounded_exec getent passwd "${names[0]}") || fail worker-account-not-retained
subject_passwd=$(bounded_exec getent passwd "${names[1]}") || fail subject-account-not-retained
worker_group=$(bounded_exec getent group "${names[0]}") || fail worker-group-not-retained
subject_group=$(bounded_exec getent group "${names[1]}") || fail subject-group-not-retained
results_group=$(bounded_exec getent group "${names[2]}") || fail results-group-not-retained
IFS=: read -r pw_name _ worker_account_uid worker_account_gid _ _ _ <<<"$worker_passwd"
IFS=: read -r subj_name _ subject_account_uid subject_account_gid _ _ _ <<<"$subject_passwd"
IFS=: read -r wg_name _ worker_group_gid _ <<<"$worker_group"
IFS=: read -r sg_name _ subject_group_gid _ <<<"$subject_group"
IFS=: read -r rg_name _ results_group_gid _ <<<"$results_group"
[[ $pw_name == "${names[0]}" && $subj_name == "${names[1]}" && $wg_name == "${names[0]}" && $sg_name == "${names[1]}" && $rg_name == "${names[2]}" && $worker_account_uid == "$worker_uid" && $worker_account_gid == "$worker_gid" && $worker_group_gid == "$worker_gid" && $subject_account_uid =~ ^[1-9][0-9]*$ && $subject_account_gid == "$subject_group_gid" && $results_group_gid =~ ^[1-9][0-9]*$ && $worker_account_uid != "$subject_account_uid" && $results_group_gid != "$worker_gid" && $results_group_gid != "$subject_group_gid" ]] || fail account-identity-binding
elapsed_from_activation_usec=$(( $(mono_ms)*1000-active_enter_usec )); ((elapsed_from_activation_usec>=0 && elapsed_from_activation_usec<=runtime_usec+timeout_usec)) || fail original-deadline-exceeded
measurement_done=1
# Keep a bounded, nonsecret result record. Raw root output stays private and is not copied into JSON.
bounded_exec jq -cnS \
 --arg case "$OWNER_DEATH_CASE" --arg gen "$G" --arg phase "$expected" \
 --arg owner_unit "$owner" --arg worker_unit "$worker" \
 --arg owner_active "$(field "$owner_terminal" ActiveState)" --arg worker_active "$(field "$worker_terminal" ActiveState)" \
 --arg owner_sub "$(field "$owner_terminal" SubState)" --arg worker_sub "$(field "$worker_terminal" SubState)" \
 --arg owner_main_live "$(field "$owner_terminal" MainPID)" --arg worker_main_live "$(field "$worker_terminal" MainPID)" \
 --argjson owner_pid "$owner_pid" --argjson worker_pid "$worker_pid" \
 --argjson owner_start "$owner_start" --argjson worker_start "$worker_start" \
 --argjson owner_uid "$owner_uid" --argjson owner_gid "$owner_gid" \
 --argjson worker_uid "$worker_uid" --argjson worker_gid "$worker_gid" \
 --arg owner_cgroup "$owner_cgroup" --arg worker_cgroup "$worker_cgroup" \
 --argjson signal_elapsed "$((signal_from_activation_usec/1000000))" \
 --argjson signal_activation_usec "$signal_from_activation_usec" \
 --argjson elapsed "$((elapsed_from_activation_usec/1000000))" \
 --argjson activation_elapsed_usec "$elapsed_from_activation_usec" \
 --argjson activation_usec "$active_enter_usec" --argjson runtime_us "$runtime_usec" \
 --argjson timeout_us "$timeout_usec" --argjson launch_status "$launch_status" \
 --arg runtime_show "$runtime_show" --arg timeout_show "$timeout_show" \
 --arg worker_name "${names[0]}" --arg subject_name "${names[1]}" --arg results_name "${names[2]}" \
 --argjson subject_uid "$subject_account_uid" --argjson subject_gid "$subject_account_gid" --argjson results_gid "$results_group_gid" \
 --arg final_policy "SendSIGKILL=$(field "$owner_facts" SendSIGKILL);FinalKillSignal=$(field "$owner_facts" FinalKillSignal);KillMode=$(field "$owner_facts" KillMode)" \
 --arg owner_exit_code "$(field "$owner_terminal" ExecMainCode)" --arg owner_exit_status "$(field "$owner_terminal" ExecMainStatus)" \
 --arg stdout_sha "$(sha256sum "$log/root.stdout" | cut -d' ' -f1)" --arg stderr_sha "$(sha256sum "$log/root.stderr" | cut -d' ' -f1)" \
 --argjson stdout_bytes "$(stat -Lc %s "$log/root.stdout")" --argjson stderr_bytes "$(stat -Lc %s "$log/root.stderr")" \
 --argjson output_before 0 --argjson output_after 0 \
 '{schema:"issue779-owner-death-native-fixture-v1",case:$case,generation:$gen,phase_line:$phase,accepted_reply_committed:true,original_request_pending:true,owner:{pid:$owner_pid,starttime:$owner_start,uid:$owner_uid,gid:$owner_gid,unit:$owner_unit,cgroup:$owner_cgroup},worker:{pid:$worker_pid,starttime:$worker_start,uid:$worker_uid,gid:$worker_gid,unit:$worker_unit,cgroup:$worker_cgroup},signal:{name:(if $case=="N13" then "SIGKILL" else "SIGSTOP" end),target_pid:$owner_pid,pre_signal_owner:{pid:$owner_pid,starttime:$owner_start,uid:$owner_uid,gid:$owner_gid,unit:$owner_unit,cgroup:$owner_cgroup},syscall_succeeded:true,elapsed_seconds:$signal_elapsed,from_activation_usec:$signal_activation_usec},systemd_bound:{active_enter_usec:$activation_usec,runtime_max_usec:$runtime_us,runtime_max_show:$runtime_show,timeout_stop_usec:$timeout_us,timeout_stop_show:$timeout_show,final_kill_policy:$final_policy,timer_rearmed:false,new_acceptance:false},termination:{owner_exited:true,worker_exited:true,owner_unit_terminal:true,worker_unit_terminal:true,owner_active_state:$owner_active,worker_active_state:$worker_active,owner_sub_state:$owner_sub,worker_sub_state:$worker_sub,owner_terminal_main_pid:($owner_main_live|tonumber),worker_terminal_main_pid:($worker_main_live|tonumber),owner_cgroup_empty:true,worker_cgroup_empty:true,elapsed_seconds:$elapsed,from_activation_usec:$activation_elapsed_usec,owner_exec_main_code:($owner_exit_code|tonumber),owner_exec_main_status:($owner_exit_status|tonumber)},pipes:{root_stdout_eof:true,root_stderr_eof:true,stdout_bytes:$stdout_bytes,stderr_bytes:$stderr_bytes,stdout_sha256:$stdout_sha,stderr_sha256:$stderr_sha,launch_exit:$launch_status},positive_files_before:$output_before,positive_files_after:$output_after,accounts:{generation_bound:true,retained:true,reusable:false,names:[$worker_name,$subject_name,$results_name],worker_uid:$worker_uid,worker_gid:$worker_gid,subject_uid:$subject_uid,subject_gid:$subject_gid,results_gid:$results_gid},workspace:{generation_bound:true,quarantined:true,retained:true},owner_death_outcome_preserved:true,native_credit:false}' >"$result" || fail result-encode
printf '%s\n' 'OWNER_DEATH_OBSERVED_OUTPUT_QUARANTINED' >"$quarantine"; chmod 600 "$quarantine"
chmod 600 "$result"; bounded_exec /usr/bin/python3.12 -I -S -B "$parser" "$result" >/dev/null || fail receipt-parser
success=1
printf 'OWNER_DEATH_MEASURED:%s:QUARANTINED:NOT_NATIVE_ACCEPTANCE\n' "$OWNER_DEATH_CASE"
