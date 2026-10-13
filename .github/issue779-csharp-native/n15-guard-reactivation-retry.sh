#!/usr/bin/env bash
set -euo pipefail
export PATH=/usr/bin:/usr/sbin LC_ALL=C LANG=C
umask 077
# Source-only candidate. The outer runner must pin and invoke this under the original work deadline.
fail() { printf '%s\n' 'N15_RETRY_MEASUREMENT_REJECTED' >&2; exit 1; }
[[ ${1-} == --execute ]] || fail
shift
# N15_FIXED_ARG_ABI_BEGIN
[[ $# == 22 ]] || fail
[[ ${1} == --generation && ${3} == --owner-unit && ${5} == --worker-unit && ${7} == --request \
   && ${9} == --runtime-host && ${11} == --entry && ${13} == --external-guard \
   && ${15} == --partial-record && ${17} == --work-deadline-boot-ms \
   && ${19} == --job-deadline-boot-ms && ${21} == --result-directory ]] || fail
generation=${2} owner=${4} worker=${6} request=${8} runtime_host=${10} entry=${12} external_guard=${14}
partial=${16} work_deadline=${18} job_deadline=${20} out=${22}
# N15_FIXED_ARG_ABI_END
[[ $generation =~ ^[0-9a-f]{32}$ \
   && $owner == "appsurface-evidence-owner-$generation.service" \
   && $worker == "appsurface-evidence-worker-$generation.service" \
   && $work_deadline =~ ^[1-9][0-9]{0,14}$ && $job_deadline =~ ^[1-9][0-9]{0,14}$ \
   && $out == /* && $out != *'..'* \
   && $request == /* && $request != *'..'* && $runtime_host == /* && $runtime_host != *'..'* \
   && $entry == /* && $entry != *'..'* && $external_guard == /* && $external_guard != *'..'* \
   && $partial == /* && $partial != *'..'* && $EUID == 0 && $OSTYPE == linux* ]] || fail
boot_ms() {
 local up rest whole frac
 IFS=' ' read -r up rest </proc/uptime || return 1
 [[ $up =~ ^[0-9]+\.[0-9]+$ ]] || return 1
 whole=${up%%.*}; frac=${up#*.}000; frac=${frac:0:3}
 printf '%s' "$((10#$whole*1000+10#$frac))"
}
remaining_ms() { local now; now=$(boot_ms) || return 1; ((now<$1)) || return 1; printf '%s' "$(( $1-now ))"; }
bounded() {
 local left=$1 sec; shift
 left=$(remaining_ms "$left") || fail
 sec=$(awk -v ms="$left" 'BEGIN{printf "%.3f",ms/1000}')
 /usr/bin/timeout --signal=KILL --kill-after=1 "$sec" "$@"
}
[[ -d $out && ! -L $out && $(stat -c '%F:%u:%g:%a' -- "$out") == 'directory:0:0:700' ]] || fail
[[ -f $request && ! -L $request && -f $partial && ! -L $partial ]] || fail
[[ -f $external_guard && ! -L $external_guard && $(stat -c '%u:%g:%a:%h' -- "$external_guard") == 0:0:600:1 ]] || fail
[[ $(cat -- "$external_guard") == "$generation" ]] || fail
owner_guard=/run/appsurface-evidence-owners/$generation
[[ -d $owner_guard && ! -L $owner_guard && $(stat -c '%F:%u:%g:%a' -- "$owner_guard") == 'directory:0:0:700' \
   && ! -e $owner_guard/armed && ! -L $owner_guard/armed \
   && -f $owner_guard/consumed && ! -L $owner_guard/consumed \
   && $(stat -c '%u:%g:%a:%h' -- "$owner_guard/consumed") == 0:0:600:1 \
   && $(cat -- "$owner_guard/consumed") == "$generation" ]] || fail
work_left=$(remaining_ms "$work_deadline") || fail
job_left=$(remaining_ms "$job_deadline") || fail
if (( work_left < job_left )); then retry_deadline=$work_deadline; else retry_deadline=$job_deadline; fi
initial_left=$(remaining_ms "$retry_deadline") || fail
(( initial_left > 12000 )) || fail
jq -e --arg g "$generation" --arg owner "$owner" --arg worker "$worker" '
 .schema=="issue779-n15-observer-partial-v1" and .case_generation==$g and
 .owner.unit==$owner and .worker.unit==$worker and .pidfd_signal.signal==9 and
 .owner.control_group==("/system.slice/"+$owner) and
 (.owner.starttime_ticks | type=="number" and .>0 and floor==.) and
 .pidfd_signal.syscall_succeeded==true and .owner_terminal.exec_main_status==9 and
 .guard.consumed_root_owned_exact_marker==true and .native_authority==false and .native_acceptance==false and .native_credit==false
' "$partial" >/dev/null || fail
# The observer's live owner identity is the authority for the generated cgroup path.
# Require the selected physical kernel cgroup to be absent or empty across two bounded scans
# before reading/resetting the pruned failed-unit metadata.
owner_cgroup="/system.slice/$owner"
bounded "$retry_deadline" /usr/bin/python3.12 -I -S -B -c '
# N15_PHYSICAL_CGROUP_CHECK_BEGIN
import os, re, stat, sys, time

def reject():
    raise SystemExit(1)

def scan(root, group, expected_uid):
    if re.fullmatch(r"/system\.slice/appsurface-evidence-owner-[0-9a-f]{32}\.service", group) is None:
        reject()
    rootfd = os.open(root, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
    try:
        rootst = os.fstat(rootfd)
        if not stat.S_ISDIR(rootst.st_mode) or rootst.st_uid != expected_uid or rootst.st_mode & 0o022:
            reject()
        base = os.dup(rootfd)
        try:
            parent = os.open("system.slice", os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=base)
            os.close(base)
            base = parent
            parent_before = os.fstat(base)
            if (not stat.S_ISDIR(parent_before.st_mode) or parent_before.st_uid != expected_uid
                    or parent_before.st_mode & 0o022):
                reject()
            name = group.rsplit("/", 1)[-1]
            try:
                child = os.open(name, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=base)
            except FileNotFoundError:
                # Only the authenticated generated leaf may be pruned. A missing or unsafe
                # system.slice ancestor, a symlink, and missing cgroup.procs still reject.
                try:
                    os.stat(name, dir_fd=base, follow_symlinks=False)
                except FileNotFoundError:
                    parent_after = os.fstat(base)
                    root_after = os.fstat(rootfd)
                    identity = lambda st: (st.st_dev, st.st_ino, st.st_mode, st.st_uid, st.st_gid)
                    if identity(parent_before) != identity(parent_after) or identity(rootst) != identity(root_after):
                        reject()
                    return []
                reject()
            os.close(base)
            base = child
            if os.fstat(base).st_uid != expected_uid:
                reject()
            pending = [base]
            base = -1
            members = []
            visited = 0
            while pending:
                directory = pending.pop()
                try:
                    dst = os.fstat(directory)
                    if not stat.S_ISDIR(dst.st_mode) or dst.st_uid != expected_uid:
                        reject()
                    saw_procs = False
                    for name in os.listdir(directory):
                        visited += 1
                        if visited > 4096 or name in (".", "..") or "/" in name:
                            reject()
                        item = os.stat(name, dir_fd=directory, follow_symlinks=False)
                        if stat.S_ISLNK(item.st_mode) or item.st_uid != expected_uid:
                            reject()
                        if stat.S_ISDIR(item.st_mode):
                            pending.append(os.open(name, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=directory))
                        elif name == "cgroup.procs" and stat.S_ISREG(item.st_mode):
                            if saw_procs:
                                reject()
                            saw_procs = True
                            fd = os.open(name, os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=directory)
                            try:
                                fst = os.fstat(fd)
                                if fst.st_uid != expected_uid or not stat.S_ISREG(fst.st_mode):
                                    reject()
                                raw = os.read(fd, 65537)
                                if len(raw) > 65536 or os.read(fd, 1):
                                    reject()
                            finally:
                                os.close(fd)
                            for row in raw.splitlines():
                                if not row.isdigit():
                                    reject()
                                members.append(int(row))
                        elif stat.S_ISREG(item.st_mode):
                            continue
                        else:
                            reject()
                    if not saw_procs:
                        reject()
                finally:
                    os.close(directory)
            return members
        finally:
            if base >= 0:
                os.close(base)
    finally:
        os.close(rootfd)

try:
    root, group, uid = sys.argv[1], sys.argv[2], int(sys.argv[3])
    first = scan(root, group, uid)
    time.sleep(0.05)
    second = scan(root, group, uid)
    if first or second:
        reject()
except (OSError, ValueError, OverflowError):
    reject()
# N15_PHYSICAL_CGROUP_CHECK_END
' /sys/fs/cgroup "$owner_cgroup" 0 || fail
worker_before=$out/worker-before.txt
worker_after=$out/worker-after.txt
retry_stdout=$out/retry.stdout
retry_stderr=$out/retry.stderr
owner_after=$out/retry-owner.txt
owner_before=$out/owner-before.txt
for f in "$worker_before" "$worker_after" "$retry_stdout" "$retry_stderr" "$owner_before" "$owner_after" "$out/reactivation-measurement.json"; do [[ ! -e $f && ! -L $f ]] || fail; done
bounded "$retry_deadline" systemctl show "$worker" -p LoadState -p InvocationID -p ActiveEnterTimestampMonotonic -p ExecMainStartTimestampMonotonic -p NRestarts >"$worker_before"
[[ -s $worker_before && $(stat -c '%u:%g:%a:%h' -- "$worker_before") == 0:0:600:1 ]] || fail
owner_pid=$(jq -er --arg unit "$owner" '.owner | select(.unit==$unit and .uid==0 and .gid==0 and .pidfd_opened==true and .identity_rechecked_before_signal==true) | .main_pid | select(type=="number" and .>1 and floor==.)' "$partial") || fail
[[ $owner_pid =~ ^[1-9][0-9]{0,9}$ ]] || fail
owner_properties='Id LoadState ActiveState SubState ControlGroup MainPID ExecMainPID ExecMainCode ExecMainStatus User Group Type KillMode RemainAfterExit RuntimeMaxUSec TimeoutStopUSec Restart SendSIGKILL FinalKillSignal ActiveEnterTimestampMonotonic ConditionResult Conditions ExecStart Environment UnsetEnvironment EnvironmentFiles PassEnvironment FragmentPath'
bounded "$retry_deadline" systemctl show --no-pager --property="${owner_properties// /,}" "$owner" >"$owner_before"
[[ $(stat -c '%u:%g:%a:%h' -- "$owner_before") == 0:0:600:1 && $(stat -c %s -- "$owner_before") -le 16384 ]] || fail
# Compare the failed unit with the complete immutable owner recipe and the same original deadlines.
# The inline verifier is data-only: it reads this bounded systemctl snapshot and dispatch arguments.
# N15_FAILED_OWNER_RECIPE_CHECK_BEGIN
/usr/bin/python3.12 -I -S -B -c '
import decimal, pathlib, re, sys, time

def reject():
    raise SystemExit(1)
try:
    path, generation, owner, runtime, entry, request, work_deadline, job_deadline, owner_pid, armed = sys.argv[1:]
    raw = pathlib.Path(path).read_bytes()
    if not 0 < len(raw) <= 16384 or not raw.endswith(b"\n"):
        reject()
    values = {}
    for line in raw.decode("ascii", "strict").splitlines():
        if "=" not in line:
            reject()
        key, value = line.split("=", 1)
        if key in values:
            reject()
        values[key] = value
    required = set("Id LoadState ActiveState SubState ControlGroup MainPID ExecMainPID ExecMainCode ExecMainStatus User Group Type KillMode RemainAfterExit RuntimeMaxUSec TimeoutStopUSec Restart SendSIGKILL FinalKillSignal ActiveEnterTimestampMonotonic ConditionResult Conditions ExecStart Environment UnsetEnvironment EnvironmentFiles PassEnvironment FragmentPath".split())
    if set(values) != required:
        reject()
    fixed = {
        "Id": owner, "LoadState": "loaded", "ActiveState": "failed", "SubState": "failed",
        "MainPID": "0", "ExecMainPID": owner_pid,
        "ExecMainCode": "2", "ExecMainStatus": "9", "User": "0", "Group": "0", "Type": "exec",
        "KillMode": "control-group", "RemainAfterExit": "no", "Restart": "no", "SendSIGKILL": "yes",
        "FinalKillSignal": "9", "ConditionResult": "yes", "EnvironmentFiles": "", "PassEnvironment": "",
        "FragmentPath": "/run/systemd/transient/" + owner,
        "Conditions": "ConditionPathExists=" + armed + " (success)",
    }
    if any(values[k] != v for k, v in fixed.items()):
        reject()
    if values["ControlGroup"] not in ("", "/system.slice/" + owner):
        reject()
    fixed_environment = ("PATH=/usr/bin:/bin", "HOME=/nonexistent", "LANG=C.UTF-8",
                         "DOTNET_CLI_TELEMETRY_OPTOUT=1", "DOTNET_CLI_HOME=/tmp")
    expected_env = set(fixed_environment)
    expected_unset = set("DOTNET_STARTUP_HOOKS DOTNET_ADDITIONAL_DEPS DOTNET_SHARED_STORE DOTNET_ROOT DOTNET_ROOT_X64 DOTNET_HOST_PATH DOTNET_ROLL_FORWARD DOTNET_ROLL_FORWARD_TO_PRERELEASE DOTNET_MULTILEVEL_LOOKUP CORECLR_ENABLE_PROFILING CORECLR_PROFILER CORECLR_PROFILER_PATH CORECLR_PROFILER_PATH_64 COR_ENABLE_PROFILING COR_PROFILER COR_PROFILER_PATH COMPlus_ReadyToRun COMPlus_ZapDisable".split())
    environment_values = values["Environment"].split()
    unset_values = values["UnsetEnvironment"].split()
    if len(environment_values) != len(expected_env) or set(environment_values) != expected_env \
            or len(unset_values) != len(expected_unset) or set(unset_values) != expected_unset:
        reject()
    if any(any(ch.isspace() or ord(ch) < 32 for ch in x) for x in (runtime, entry, request)):
        reject()
    argv = " ".join(("/usr/bin/env", "-i", *fixed_environment, runtime, entry,
                     "evidence", "supervise", "--request", request))
    line = values["ExecStart"]
    prefix = "{ path=/usr/bin/env ; argv[]=" + argv + " ; ignore_errors=no ; "
    if not line.startswith(prefix) or not line.endswith(" }"):
        reject()
    suffix = line[len(prefix):-2]
    if re.fullmatch(r"start_time=\[[^\]\r\n]*\] ; stop_time=\[[^\]\r\n]*\] ; pid=" + re.escape(owner_pid) + r" ; code=killed ; status=9", suffix) is None:
        reject()
    def usec(text):
        units = {"h": decimal.Decimal(3600000000), "min": decimal.Decimal(60000000),
                 "s": decimal.Decimal(1000000), "ms": decimal.Decimal(1000), "us": decimal.Decimal(1)}
        pieces = text.split(" ")
        total = decimal.Decimal(0)
        last = -1
        order = ["h", "min", "s", "ms", "us"]
        for piece in pieces:
            match = re.fullmatch(r"([0-9]+(?:\.[0-9]{1,6})?)(h|min|s|ms|us)", piece)
            if match is None:
                reject()
            number, unit = match.groups()
            rank = order.index(unit)
            if rank <= last:
                reject()
            last = rank
            total += decimal.Decimal(number) * units[unit]
        if total <= 0 or total != total.to_integral_value():
            reject()
        return int(total)
    runtime_us = usec(values["RuntimeMaxUSec"])
    stop_us = usec(values["TimeoutStopUSec"])
    if runtime_us > 3600000000 or stop_us > 30000000:
        reject()
    active_since = int(values["ActiveEnterTimestampMonotonic"])
    now_mono = time.clock_gettime_ns(time.CLOCK_MONOTONIC) // 1000
    elapsed = now_mono - active_since
    now_boot = time.clock_gettime_ns(time.CLOCK_BOOTTIME) // 1000000
    original_remaining_us = (min(int(work_deadline), int(job_deadline)) - now_boot) * 1000
    lifetime = runtime_us + stop_us
    if active_since <= 0 or elapsed < 0 or original_remaining_us <= 0 or elapsed >= runtime_us \
            or elapsed >= lifetime or lifetime - elapsed > original_remaining_us:
        reject()
except (OSError, UnicodeError, ValueError, decimal.InvalidOperation, OverflowError):
    reject()
' "$owner_before" "$generation" "$owner" "$runtime_host" "$entry" "$request" "$work_deadline" "$job_deadline" "$owner_pid" "$owner_guard/armed" || fail
# N15_FAILED_OWNER_RECIPE_CHECK_END
bounded "$retry_deadline" systemctl stop "$owner" >/dev/null || fail
bounded "$retry_deadline" systemctl reset-failed "$owner" >/dev/null || fail
# reset-failed may leave the failed transient unit loaded; never reuse it until not-found.
unloaded=0
for _ in {1..40}; do
 state=$(bounded "$retry_deadline" systemctl show "$owner" -p LoadState --value) || fail
 if [[ $state == not-found ]]; then unloaded=1; break; fi
 [[ $state == loaded ]] || fail
 bounded "$retry_deadline" /usr/bin/sleep 0.05 || fail
done
(( unloaded == 1 )) || fail
armed=$owner_guard/armed
[[ ! -e $armed && ! -L $armed ]] || fail
( set -o noclobber; printf '%s\n' "$generation" >"$armed" ) || fail
chmod 600 -- "$armed"
armed_inode=$(stat -c '%i' -- "$armed")
cleanup_armed() {
 if [[ -f $armed && ! -L $armed && $(stat -c '%u:%g:%a:%h:%i' -- "$armed" 2>/dev/null || :) == "0:0:600:1:$armed_inode" \
     && $(cat -- "$armed" 2>/dev/null || :) == "$generation" ]]; then rm -- "$armed"; fi
}
trap cleanup_armed EXIT HUP INT TERM
unsafe='DOTNET_STARTUP_HOOKS DOTNET_ADDITIONAL_DEPS DOTNET_SHARED_STORE DOTNET_ROOT DOTNET_ROOT_X64 DOTNET_HOST_PATH DOTNET_ROLL_FORWARD DOTNET_ROLL_FORWARD_TO_PRERELEASE DOTNET_MULTILEVEL_LOOKUP CORECLR_ENABLE_PROFILING CORECLR_PROFILER CORECLR_PROFILER_PATH CORECLR_PROFILER_PATH_64 COR_ENABLE_PROFILING COR_PROFILER COR_PROFILER_PATH COMPlus_ReadyToRun COMPlus_ZapDisable'
left=$(remaining_ms "$retry_deadline") || fail
(( left > 12000 )) || fail
# Runtime + the fixed five-second stop reserve and dispatch margin fit the lesser
# of the unchanged fixture-work and original job deadlines at dispatch time.
runtime_ms=$((left-7000))
runtime_seconds=$(awk -v ms="$runtime_ms" 'BEGIN{printf "%.3f",ms/1000}')
retry_exit=0
bounded "$retry_deadline" /usr/bin/systemd-run --quiet --wait --pipe --unit="$owner" \
 --property=Type=exec --property=User=0 --property=Group=0 --property=KillMode=control-group \
 --property=Restart=no --property=RemainAfterExit=no --property=SendSIGKILL=yes --property=FinalKillSignal=9 \
 --property=RuntimeMaxSec="$runtime_seconds" --property=TimeoutStopSec=5s \
 "--property=ConditionPathExists=$armed" --property=PassEnvironment= \
 "--property=UnsetEnvironment=$unsafe" \
 --property=Environment=PATH=/usr/bin:/bin\ HOME=/nonexistent\ LANG=C.UTF-8\ DOTNET_CLI_TELEMETRY_OPTOUT=1\ DOTNET_CLI_HOME=/tmp \
 /usr/bin/env -i PATH=/usr/bin:/bin HOME=/nonexistent LANG=C.UTF-8 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_CLI_HOME=/tmp \
 "$runtime_host" "$entry" evidence supervise --request "$request" >"$retry_stdout" 2>"$retry_stderr" || retry_exit=$?
[[ $(stat -c '%u:%g:%a:%h' -- "$retry_stdout") == 0:0:600:1 \
   && $(stat -c '%u:%g:%a:%h' -- "$retry_stderr") == 0:0:600:1 \
   && $(stat -c %s -- "$retry_stdout") -le 65536 && $(stat -c %s -- "$retry_stderr") -le 65536 ]] || fail
[[ $retry_exit == 1 ]] && grep -Fq 'ASEVD402' "$retry_stderr" || fail
bounded "$retry_deadline" systemctl show "$worker" -p LoadState -p InvocationID -p ActiveEnterTimestampMonotonic -p ExecMainStartTimestampMonotonic -p NRestarts >"$worker_after"
bounded "$retry_deadline" systemctl show "$owner" -p LoadState -p ActiveState -p SubState -p ExecMainCode -p ExecMainStatus >"$owner_after"
cmp -s -- "$worker_before" "$worker_after" || fail
grep -Fxq 'ActiveState=failed' "$owner_after" || fail
grep -Fxq 'ExecMainCode=1' "$owner_after" || fail
grep -Fxq 'ExecMainStatus=1' "$owner_after" || fail
cleanup_armed
trap - EXIT HUP INT TERM
[[ ! -e $armed && ! -L $armed && $(cat -- "$owner_guard/consumed") == "$generation" ]] || fail
stdout_sha=$(sha256sum -- "$retry_stdout"); stdout_sha=${stdout_sha%% *}
stderr_sha=$(sha256sum -- "$retry_stderr"); stderr_sha=${stderr_sha%% *}
worker_sha=$(sha256sum -- "$worker_before"); worker_sha=${worker_sha%% *}
owner_sha=$(sha256sum -- "$owner_after"); owner_sha=${owner_sha%% *}
jq -ncS --arg generation "$generation" --arg owner "$owner" --arg worker "$worker" \
 --arg stdout_sha "$stdout_sha" --arg stderr_sha "$stderr_sha" --arg worker_snapshot_sha "$worker_sha" \
 --arg owner_snapshot_sha "$owner_sha" --argjson retry_exit "$retry_exit" \
 '{schema:"issue779-n15-guard-reactivation-measurement-v1",generation:$generation,owner_unit:$owner,worker_unit:$worker,retry_exit:$retry_exit,retry_stdout_sha256:$stdout_sha,retry_stderr_sha256:$stderr_sha,worker_before_after_snapshot_sha256:$worker_snapshot_sha,retry_owner_terminal_snapshot_sha256:$owner_snapshot_sha,raw_logs_retained:true,synthetic_completion_flags:false}' >"$out/reactivation-measurement.json"
chmod 600 "$out/reactivation-measurement.json"
