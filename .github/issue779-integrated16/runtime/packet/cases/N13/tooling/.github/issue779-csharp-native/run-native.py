#!/usr/bin/env python3
"""Reviewed test orchestration only; the candidate C# image owns the live lifecycle."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import signal
import stat
import subprocess
import sys
import io
import tarfile
import time
import uuid
import types
from datetime import datetime, timezone

SOURCE = 'fc59bb1254294d624f6779f928020edfe988dd5e'
PARENT = '1794a99f9f9857e923fdb2cc8cc81fe0bd5c8c38'
SOURCE_MAP = '02b833b330267cafc6e12e75ac82439c6ee80a7e839f7fad27d7097fac8da843'
CANONICAL_SOURCE_MAP = 'bf7334e8a273dc5693bb9f2226c4b44c90c988fb2f2c8d56ddb0b840adc494c6'
CAPTURE_SHA = '6e46183742801e864802ab8a7f3e64cd320139a500b7b215b17befe96616d2c1'
CAPTURE_PROJECTION_SHA = '8457d006fa5061f7249afd6038107907557732e6a51de3d8ba151923007e3ea0'
PARSER_SHA = '276df6fdd6cb9f8e623e9b09c62362d75d2b31adc07a6b2ca05e6a8c8e6ad4d1'
COMMON_SHA = '4d6913203730418f1005969bc754b664b758b9564a37eb74d3035e93a4d3bb4f'
PREPARATION_SHA = '544d884e20ab03886a568aa3b5c6da5f0fdae8854652eb93ddf38fb834d5c3ec'
WRAPPERS = {'N13': ('checkpoint-n13-owner-killed.sh','cd9e37d4f64a23471ea6c64f6286bcc36657c9fcdf7731678bddc108a986aafc'),
            'N14': ('checkpoint-n14-owner-stopped.sh','d82860a2c21ea1e5c80d6a0d34db1e5fecff03dbbde4bad08350ddcab9f450a2')}
# Zero slots are deliberate fail-closed external integration gates until the final input DAG is reviewed.
REVIEWED_FILES = {'acquire-inbound.sh':'3fb673aa42ca7cbb12804b8d92a87a35ee684152117fcf15468de4d57e8f6212','prepare-root-inputs-v2.sh':'050a73a76494574b96abc0c170d29c791f3645765dda7922171ccdc11834757f',
 'checkpoint-n13-owner-killed.sh':'cd9e37d4f64a23471ea6c64f6286bcc36657c9fcdf7731678bddc108a986aafc',
 'checkpoint-n14-owner-stopped.sh':'d82860a2c21ea1e5c80d6a0d34db1e5fecff03dbbde4bad08350ddcab9f450a2',
 'checkpoint-owner-death-common.sh':'4d6913203730418f1005969bc754b664b758b9564a37eb74d3035e93a4d3bb4f','check_owner_death_record.py':'276df6fdd6cb9f8e623e9b09c62362d75d2b31adc07a6b2ca05e6a8c8e6ad4d1',
 'preparation.json':PREPARATION_SHA,'README.md':'b731fd4d3ee12b17453a858c7f69d0fb8a20f315867c5f13d4871167aad8093c',
 'test_owner_death_data.py':'4b6075ddcf232a534f6451000f5c670c4b48495e0d506ba969a917964ee1e99f',
 'prepare-os-audit-v2.py':'b345b2be77b359af23ade0b334caf76d7fdeb14bd2d4a3f0837beb21232df5e0',
 'source-review.json':'a52dba81e8062175241d08a471cb87b15718dfcba5f8b08c679260685951812d',
 'prepare-case-workspace.py':'c52e8b5f2e08d281f8dafa50e770c5406bd7df1fc6a1537e2165c7fe12c56558',
 'test_build_receipt_contracts.py':'0a926f3423df2235bd249dd5b7603d4993ed60e60c8a9d55a3a9fd38984732b2',
 'test_workspace_arguments.py':'5b3996f098bffef13a9858ac08004d3c130af3b432f79904e1a72758c91183b6'}
SOURCE_REVIEW_SHA = REVIEWED_FILES['source-review.json']
SOURCE_REVIEW_CLEAR = FIXTURE_REVIEW_CLEAR = ROOT_TRANSPORT_REVIEW_CLEAR = RETENTION_REVIEW_CLEAR = NATIVE_HANDOFF_REVIEW_CLEAR = True
ROOT_PREFIX = ['/usr/bin/sudo', '-n', '/usr/bin/env', '-i', 'PATH=/usr/bin:/usr/sbin',
               'LANG=C', 'LC_ALL=C', '/usr/bin/bash', '--noprofile', '--norc']

WORKSPACE_HASH_READ = r'''\
import hashlib, os, re, stat, sys

def reject():
    raise SystemExit(1)
if len(sys.argv) != 2 or not re.fullmatch(r"[0-9a-f]{32}", sys.argv[1]): reject()
g = sys.argv[1]
private_root = "/run/appsurface-evidence-fixture"
stage_root = "/var/lib/appsurface-evidence-fixture"
private = private_root + "/" + g
stage = stage_root + "/" + g
policy = stage + "/tool/fixture-policy.json"
request = private + "/request.json"
checksums = private + "/request-policy.sha256"
def ident(s):
    return (s.st_dev, s.st_ino, s.st_mode, s.st_uid, s.st_gid, s.st_nlink, s.st_size, s.st_mtime_ns, s.st_ctime_ns)
def directory(path, mode):
    st = os.lstat(path)
    if not stat.S_ISDIR(st.st_mode) or st.st_uid != 0 or st.st_gid != 0 or stat.S_IMODE(st.st_mode) != mode: reject()
    return st
def read_file(path, cap, mode):
    before = os.lstat(path)
    if not stat.S_ISREG(before.st_mode) or before.st_uid != 0 or before.st_gid != 0 or before.st_nlink != 1 or stat.S_IMODE(before.st_mode) != mode or not 0 < before.st_size <= cap: reject()
    fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK | os.O_CLOEXEC)
    try:
        opened = os.fstat(fd)
        if ident(before) != ident(opened): reject()
        chunks = []; total = 0
        while True:
            block = os.read(fd, min(4096, cap + 1 - total))
            if not block: break
            total += len(block)
            if total > cap or total > before.st_size: reject()
            chunks.append(block)
        after = os.fstat(fd)
        if total != before.st_size or ident(before) != ident(after) or ident(before) != ident(os.lstat(path)): reject()
        return b"".join(chunks)
    finally:
        os.close(fd)
directory(private_root, 0o755); private_st = directory(private, 0o700)
directory(stage_root, 0o755); stage_st = directory(stage, 0o555)
directory(stage + "/tool", 0o555)
raw = read_file(checksums, 512, 0o600)
lines = raw.decode("ascii").splitlines()
if len(lines) != 2 or raw != (lines[0] + "\n" + lines[1] + "\n").encode(): reject()
matches = [re.fullmatch(r"([0-9a-f]{64})  (.+)", line) for line in lines]
if any(m is None for m in matches) or matches[0].group(2) != request or matches[1].group(2) != policy: reject()
request_bytes = read_file(request, 65536, 0o600)
policy_bytes = read_file(policy, 4096, 0o444)
req_sha, policy_sha = (m.group(1) for m in matches)
if hashlib.sha256(request_bytes).hexdigest() != req_sha or hashlib.sha256(policy_bytes).hexdigest() != policy_sha: reject()
if ident(private_st) != ident(os.lstat(private)) or ident(stage_st) != ident(os.lstat(stage)): reject()
print("CASE_WORKSPACE_HASHES:%s:%s" % (req_sha, policy_sha))
'''

LOG_CAP = 8 * 1024 * 1024
ARCHIVE_CAP = 16 * 1024 * 1024
class Rejected(ValueError):
    def __init__(self, category):
        super().__init__('native-test-rejected')
        self.category = category


def require(value, category):
    if not value:
        raise Rejected(category)


def parse_workspace_stage_marker(data, case, generation):
    require(case in ('N13', 'N14') and re.fullmatch('[0-9a-f]{32}', generation) is not None,
            'case-workspace-stage-identity')
    expected=('CASE_WORKSPACE_STAGED:%s:%s:REQUEST_AND_POLICY_PINNED:NO_OWNER_LAUNCH:NO_NATIVE_CREDIT\n' %
              (case, generation)).encode()
    require(type(data) is bytes and data == expected, 'case-workspace-stage-marker')
    return True


def parse_workspace_hash_marker(data):
    match=re.fullmatch(rb'CASE_WORKSPACE_HASHES:([0-9a-f]{64}):([0-9a-f]{64})\n', data)
    require(match is not None, 'case-workspace-hash-binding')
    return match.group(1).decode(), match.group(2).decode()


def unique(rows):
    result = {}
    for key, value in rows:
        require(key not in result, 'duplicate-json')
        result[key] = value
    return result


def ident(s):
    return (s.st_dev, s.st_ino, s.st_mode, s.st_uid, s.st_gid, s.st_nlink,
            s.st_size, s.st_mtime_ns, s.st_ctime_ns)


def read(path, cap, deadline):
    require(time.monotonic() < deadline, 'read-deadline')
    before = path.lstat()
    require(stat.S_ISREG(before.st_mode) and before.st_nlink == 1 and 0 <= before.st_size <= cap,
            'file-shape')
    fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK | os.O_CLOEXEC)
    chunks = []
    length = 0
    try:
        require(ident(before) == ident(os.fstat(fd)), 'opened-substitution')
        while True:
            require(time.monotonic() < deadline, 'read-deadline')
            data = os.read(fd, min(65536, cap + 1 - length))
            if not data:
                break
            chunks.append(data)
            length += len(data)
            require(length <= cap and length <= before.st_size, 'file-growth')
        require(length == before.st_size and ident(before) == ident(os.fstat(fd)) == ident(path.lstat()),
                'file-changed')
    finally:
        os.close(fd)
    require(time.monotonic() < deadline, 'read-deadline')
    return b''.join(chunks)


def save(path, value, deadline):
    data = value if isinstance(value, bytes) else (json.dumps(value, sort_keys=True, separators=(',', ':')) + '\n').encode()
    require(len(data) <= LOG_CAP and time.monotonic() < deadline, 'publication-bound')
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC, 0o600)
    try:
        pending = memoryview(data)
        while pending:
            require(time.monotonic() < deadline, 'publication-deadline')
            count = os.write(fd, pending)
            require(count > 0, 'publication-short-write')
            pending = pending[count:]
        os.fsync(fd)
    finally:
        os.close(fd)
    require(time.monotonic() < deadline, 'publication-deadline')


def validate_audit_failure_context(data):
    """Validate bounded diagnostic data only; it cannot change the failed audit result."""
    require(0 < len(data) <= 32768, 'diagnostic-bytes')
    value = decode(data)
    fields = {'schema', 'category', 'requester', 'soname', 'candidate_count',
              'runpath_count', 'cache_count', 'truncated', 'runpath_candidates',
              'cache_candidates', 'combined_candidates'}
    require(isinstance(value, dict) and set(value) == fields
            and value['schema'] == 'issue779-os-audit-dependency-failure-context-v1'
            and value['category'] == 'dependency-unresolved-or-ambiguous', 'diagnostic-schema')
    def path_data(path):
        require(isinstance(path, str) and path.startswith('/') and path != '/'
                and len(path.encode('utf-8')) <= 4096
                and all(ord(c) >= 32 and ord(c) != 127 for c in path)
                and all(part not in ('', '.', '..') for part in path[1:].split('/')), 'diagnostic-path')
    path_data(value['requester'])
    require(isinstance(value['soname'], str)
            and re.fullmatch(r'[A-Za-z0-9_.+-]{1,256}', value['soname']), 'diagnostic-soname')
    counts = []
    for count_name, rows_name in (('runpath_count', 'runpath_candidates'),
                                 ('cache_count', 'cache_candidates'),
                                 ('candidate_count', 'combined_candidates')):
        count, rows = value[count_name], value[rows_name]
        require(type(count) is int and 0 <= count <= 4096 and isinstance(rows, list)
                and len(rows) == min(count, 8), 'diagnostic-count')
        for path in rows:
            path_data(path)
        counts.append(count)
    require(type(value['truncated']) is bool and value['truncated'] == any(c > 8 for c in counts)
            and value['candidate_count'] != 1, 'diagnostic-truncation-or-not-rejected')
    return value


def retain_audit_failure_context(source, destination, deadline):
    """Copy only the optional fixed sidecar as private data; original audit failure remains fatal."""
    require(time.monotonic() < deadline, 'diagnostic-deadline')
    parent = source.parent.lstat()
    require(stat.S_ISDIR(parent.st_mode) and parent.st_uid == os.geteuid()
            and parent.st_gid == os.getegid() and stat.S_IMODE(parent.st_mode) == 0o700,
            'diagnostic-parent')
    before = source.lstat()
    require(stat.S_ISREG(before.st_mode) and before.st_uid == os.geteuid()
            and before.st_gid == os.getegid() and before.st_nlink == 1
            and stat.S_IMODE(before.st_mode) == 0o600 and 0 < before.st_size <= 32768,
            'diagnostic-file')
    data = read(source, 32768, deadline)
    validate_audit_failure_context(data)
    require(ident(before) == ident(source.lstat()) and ident(parent) == ident(source.parent.lstat()),
            'diagnostic-substitution')
    save(destination, data, deadline)
    after = destination.lstat()
    require(stat.S_ISREG(after.st_mode) and after.st_uid == os.geteuid()
            and after.st_gid == os.getegid() and after.st_nlink == 1
            and stat.S_IMODE(after.st_mode) == 0o600 and after.st_size == len(data),
            'diagnostic-copy')
    require(time.monotonic() < deadline, 'diagnostic-deadline')
    return {'file': destination.name, 'bytes': len(data), 'sha256': sha(data)}


def decode(data):
    return json.loads(data.decode('utf-8'), object_pairs_hook=unique)


def sha(data):
    return hashlib.sha256(data).hexdigest()


def audit_arguments(root, reviewed, output, deadline):
    """Build the fixed audit argv; helper is an empty sealed transport child."""
    return ['/usr/bin/python3', '-I', '-S', '-B', str(reviewed/'prepare-os-audit-v2.py'),
            '--runtime-root', str(root/'runtime'), '--tool-root', str(root/'tool'),
            '--helper-root', str(root/'empty-audit-helper'),
            '--output', str(output/'os-audit.json'), '--deadline-monotonic', str(deadline)]


def audit_output_directory(generation):
    require(type(generation) is str and re.fullmatch(r'[0-9a-f]{32}',generation) is not None,
            'audit-generation')
    return Path('/tmp')/('issue779-os-audit-'+generation)


def root_deadline(end):
    # /proc/uptime is CLOCK_BOOTTIME, which must not be assumed equal to Python's
    # CLOCK_MONOTONIC across a host suspend. Both are bounded by the original outer end.
    before = time.monotonic()
    with open('/proc/uptime', encoding='ascii') as source:
        stamp = source.read(128).split()[0]
    require(re.fullmatch(r'[0-9]+\.[0-9]+', stamp), 'root-clock-shape')
    require(time.monotonic() < end, 'root-clock-expiry')
    return str(int(float(stamp) * 1000) + max(0, int((end - time.monotonic()) * 1000) - 20))


def process_group_members(pgid, timeout=1.0):
    """Portable, non-signalling process-group census for the final ownership check."""
    value = subprocess.run(['/bin/ps', '-axo', 'pid=,pgid='], stdin=subprocess.DEVNULL,
                           stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=timeout,
                           env={'PATH': '/usr/bin:/bin', 'LANG': 'C', 'LC_ALL': 'C'})
    require(value.returncode == 0 and len(value.stdout) <= 2 * 1024 * 1024,
            'process-group-census')
    members = []
    for line in value.stdout.splitlines():
        fields = line.split()
        require(len(fields) == 2 and all(field.isdigit() for field in fields),
                'process-group-census-shape')
        if int(fields[1]) == pgid:
            members.append(int(fields[0]))
    return sorted(members)


def leader_exited_unreaped(process):
    """Observe a zombie Popen child without releasing its PID/PGID reservation."""
    require(process.returncode is None, 'child-already-reaped')
    value = subprocess.run(['/bin/ps', '-o', 'stat=', '-p', str(process.pid)],
                           stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                           stderr=subprocess.PIPE, timeout=1.0,
                           env={'PATH': '/usr/bin:/bin', 'LANG': 'C', 'LC_ALL': 'C'})
    require(value.returncode == 0 and len(value.stdout) <= 128,
            'unreaped-child-state-query')
    state = value.stdout.decode('ascii', 'strict').strip()
    require(state and process.returncode is None, 'child-ownership-lost-before-reap')
    # Popen still owns this child; a zombie keeps the PID/PGID reserved until wait().
    return state[0] == 'Z'


def settle_owned_process(process, end, force=False):
    """Keep the leader unreaped through a bounded quiet census and final group join."""
    require(process.returncode is None, 'child-already-reaped')
    forced = False
    quiet_since = None
    while True:
        left = end - time.monotonic()
        require(left > 0, 'process-settle-deadline')
        exited = leader_exited_unreaped(process)
        if not exited:
            if force:
                try:
                    os.killpg(process.pid, signal.SIGKILL)
                    forced = True
                except ProcessLookupError:
                    # The direct child remains our unreaped child; no numeric ID is reused.
                    pass
            time.sleep(min(.01, max(0.0, end - time.monotonic())))
            continue
        members = process_group_members(process.pid, timeout=min(1.0, max(.001, left)))
        others = [pid for pid in members if pid != process.pid]
        if others:
            quiet_since = None
            try:
                os.killpg(process.pid, signal.SIGKILL)
                forced = True
            except ProcessLookupError:
                # The direct child remains unreaped while the census settles.
                pass
        elif quiet_since is None:
            quiet_since = time.monotonic()
        elif time.monotonic() - quiet_since >= .05:
            # Reap only after repeated empty censuses while the leader still reserves its PGID.
            break
        time.sleep(min(.01, max(0.0, end - time.monotonic())))
    left = end - time.monotonic()
    require(left > 0, 'process-reap-deadline')
    process.wait(timeout=left)
    # Only a non-signalling census is permitted after wait() releases the PID.
    left = end - time.monotonic()
    require(left > 0 and not process_group_members(process.pid, timeout=min(1.0, left)),
            'process-group-survived-after-reap')
    return process.returncode, forced


def absent(pid):
    """Check group absence without sending even signal zero to a potentially reused PGID."""
    try:
        return not process_group_members(pid, timeout=1.0)
    except (OSError, subprocess.SubprocessError, Rejected):
        return False


UTILITY_SETTLED = r'''
set -euo pipefail
[[ $1 =~ ^issue779-native-tool-[0-9a-f]{32}-[0-9]{2}\.service$ ]]
unit=$1; group=/sys/fs/cgroup/system.slice/$unit
[[ ! -L /sys/fs/cgroup && ! -L /sys/fs/cgroup/system.slice && ! -L $group ]]
for checked in "$unit" "${unit%.service}-deadline.service" "${unit%.service}-deadline.timer"; do
# v255 show-properties returns zero for complete inactive/not-found facts;
# unlike status/help, no nonzero not-found code is a successful query here.
query_status=0
facts=$(/usr/bin/systemctl show --property=LoadState,ActiveState,SubState,Job,MainPID,ControlGroup "$checked") || query_status=$?
((query_status==0))
unset values; declare -A values=()
while IFS='=' read -r key value; do
 [[ $key == LoadState || $key == ActiveState || $key == SubState || $key == Job || $key == MainPID || $key == ControlGroup ]]
 [[ ! ${values[$key]+present} ]]; values[$key]=$value
done <<<"$facts"
[[ ( ${#values[@]} == 6 || ( $checked == *.timer && ${#values[@]} == 4 ) ) && ( ${values[LoadState]} == loaded || ${values[LoadState]} == not-found ) ]]
[[ ( ${values[ActiveState]} == inactive || ${values[ActiveState]} == failed ) && ( ${values[SubState]} == dead || ${values[SubState]} == failed ) ]]
[[ ( ${values[MainPID]-0} == 0 ) && ( -z ${values[Job]} || ${values[Job]} == 0 ) ]]
[[ -z ${values[ControlGroup]-} || ${values[ControlGroup]} == /system.slice/$checked ]]
group=/sys/fs/cgroup/system.slice/$checked
if [[ -e $group ]]; then
 [[ -d $group && $(/usr/bin/stat -f -c %t -- "$group") == 63677270 ]]
 /usr/bin/grep -qx 'populated 0' "$group/cgroup.events"
fi
done
printf 'ROOT_TOOL_CGROUP_SETTLED\n'
'''

GUARD_CREATE = r'''
set -euo pipefail; umask 077
[[ $1 =~ ^issue779-native-tool-([0-9a-f]{32})-([0-9]{2})\.service$ ]]
base=/run/issue779-native-tool-guards-${BASH_REMATCH[1]}; child=$base/${BASH_REMATCH[2]}
[[ ! -L $base ]]
if [[ ! -e $base ]]; then /usr/bin/mkdir -m0700 -- "$base"; fi
[[ $(/usr/bin/stat -c '%u:%g:%a' -- "$base") == 0:0:700 && ! -e $child && ! -L $child ]]
/usr/bin/mkdir -m0700 -- "$child"
printf 'armed\n' >"$child/armed"
[[ $(/usr/bin/stat -c '%u:%g:%a:%h' -- "$child/armed") == 0:0:600:1 ]]
[[ $2 =~ ^[0-9]+\.[0-9]+$ ]]
/usr/bin/systemd-run --quiet --unit="${1%.service}-deadline" --on-boot="$2" \
 --timer-property=AccuracySec=1us --timer-property=RandomizedDelaySec=0 \
 --property=Type=oneshot --property=TimeoutStartSec=1 --property=TimeoutStopSec=1 \
 --property=KillMode=control-group --property=RemainAfterExit=no \
 /usr/bin/systemctl kill --kill-whom=all --signal=KILL "$1"
timer=${1%.service}-deadline.timer; escaped=${timer//-/_2d}; escaped=${escaped//./_2e}
target=$(/usr/bin/busctl --system get-property org.freedesktop.systemd1 "/org/freedesktop/systemd1/unit/$escaped" org.freedesktop.systemd1.Timer NextElapseUSecMonotonic)
[[ $target =~ ^t\ ([1-9][0-9]*)$ ]]; actual=${BASH_REMATCH[1]}
whole=${2%%.*}; fraction=${2#*.}; [[ ${#fraction} == 6 ]]
expected=$((10#$whole*1000000+10#$fraction))
((actual>=expected-1 && actual<=expected+1))
'''

GUARD_CLOSE = r'''
set -euo pipefail
[[ $1 =~ ^issue779-native-tool-([0-9a-f]{32})-([0-9]{2})\.service$ ]]
base=/run/issue779-native-tool-guards-${BASH_REMATCH[1]}; child=$base/${BASH_REMATCH[2]}
[[ ! -L $base && ! -L $child && $(/usr/bin/stat -c '%u:%g:%a' -- "$base" "$child") == $'0:0:700\n0:0:700' ]]
[[ ! -L $child/armed ]]
if [[ -e $child/armed ]]; then
 [[ $(/usr/bin/stat -c '%u:%g:%a:%h' -- "$child/armed") == 0:0:600:1 ]]
 /usr/bin/rm -- "$child/armed"
fi
[[ ! -e $child/armed && ! -L $child/armed ]]
# Missing/GC units may make stop nonzero; subsequent exact state/job/cgroup checks decide settlement.
/usr/bin/systemctl stop --no-block "$1" "${1%.service}-deadline.timer" "${1%.service}-deadline.service" >/dev/null 2>&1 || :
'''

ABSOLUTE_ROOT_EXEC = r'''
set -euo pipefail
diagnostic_stage=guard
trap 'code=$?; if ((code!=0)); then printf "ROOT_TOOL_FAILURE:%s:%d\n" "$diagnostic_stage" "$code" >&2; fi' EXIT
[[ $1 =~ ^[1-9][0-9]{0,14}$ && $2 =~ ^issue779-native-tool-([0-9a-f]{32})-([0-9]{2})\.service$ ]] || exit 1
end=$1; unit=$2; guard=/run/issue779-native-tool-guards-${BASH_REMATCH[1]}/${BASH_REMATCH[2]}/armed; shift 2
now_ms() { local value other whole fraction; IFS=' ' read -r value other </proc/uptime; whole=${value%%.*}; fraction=${value#*.}000; printf '%s' "$((10#$whole*1000+10#${fraction:0:3}))"; }
check_guard() { [[ -f $guard && ! -L $guard && $(/usr/bin/stat -c '%u:%g:%a:%h' -- "$guard") == 0:0:600:1 ]]; }
check_guard; diagnostic_stage=clock; now=$(now_ms); ((now<end)); left=$((end-now)); printf -v seconds '%d.%03d' "$((left/1000))" "$((left%1000))"
check_guard; now=$(now_ms); ((now<end)); left=$((end-now)); printf -v seconds '%d.%03d' "$((left/1000))" "$((left%1000))"
diagnostic_stage=exec
printf 'ROOT_TOOL_PREEXEC\n' >&2
exec /usr/bin/timeout --signal=KILL "$seconds" /usr/bin/bash --noprofile --norc "$@"
'''


class Runner:
    def __init__(self, output, deadline):
        self.output = output
        self.deadline = deadline
        self.records = []
        self.total_bytes = 0
        self.phase = 'preflight'
        self.generation = uuid.uuid4().hex

    def root_metadata(self, source, unit, end, extra=()):
        left = end - time.monotonic()
        require(left > 0, 'root-metadata-deadline')
        value = subprocess.run(['/usr/bin/sudo', '-n', '/usr/bin/timeout', '--signal=KILL', str(left),
            '/usr/bin/env', '-i', 'PATH=/usr/bin:/usr/sbin', 'LANG=C', 'LC_ALL=C', '/usr/bin/bash',
            '--noprofile', '--norc', '-c', source, '--', unit, *extra], stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, timeout=left,
            env={'PATH': '/usr/bin:/usr/sbin', 'LANG': 'C', 'LC_ALL': 'C'})
        require(value.returncode == 0 and len(value.stdout) <= 4096 and time.monotonic() < end, 'root-metadata-terminal')
        return value.stdout

    def run(self, argv, deadline, allow_failure=False):
        deadline = min(deadline, self.deadline - 5)
        require(time.monotonic() < deadline, 'command-preflight-deadline')
        root_operation = argv[:len(ROOT_PREFIX)] == ROOT_PREFIX
        unit = 'issue779-native-tool-' + self.generation + '-' + f'{len(self.records):02d}' + '.service' if root_operation else None
        row = {'ordinal': len(self.records), 'phase': self.phase, 'argv': argv, 'pid': None, 'exit': None, 'root_utility_unit': unit,
               'waited': False, 'group_absent': False, 'forced_cleanup': False, 'timed_out': False, 'failure': None}
        self.records.append(row)
        logs = [self.output / (f'{row["ordinal"]:02d}-' + name + '.log') for name in ('stdout', 'stderr')]
        handles, process, error = [], None, None
        started = time.monotonic()
        cleanup_end = deadline
        work_end = deadline - 5
        require(started < work_end, 'phase-cleanup-reserve')
        try:
            for path in logs:
                handles.append(path.open('xb', buffering=0))
            selected = argv
            if root_operation:
                row['root_start_guard_created'] = True
                self.root_metadata(GUARD_CREATE, unit, work_end, (format(work_end, '.6f'),))
                remaining = work_end - time.monotonic()
                require(remaining > 0, 'root-operation-deadline')
                # systemd owns every ordinary/setsid descendant in the utility cgroup.
                # An independent ROOT timeout also bounds the --wait/--pipe client.
                selected = ['/usr/bin/sudo', '-n', '/usr/bin/env', '-i', 'PATH=/usr/bin:/usr/sbin', 'LANG=C', 'LC_ALL=C',
                    '/usr/bin/timeout', '--signal=KILL', str(max(.001, cleanup_end - time.monotonic() - .5)),
                    '/usr/bin/systemd-run', '--quiet', '--wait', '--pipe', '--expand-environment=no', '--unit=' + unit,
                    '--property=Type=exec', '--property=User=0', '--property=Group=0',
                    '--property=KillMode=control-group', '--property=Restart=no', '--property=RemainAfterExit=no',
                    '--property=ConditionPathExists=/run/issue779-native-tool-guards-' + self.generation + '/' + f'{row["ordinal"]:02d}' + '/armed',
                    '--property=RuntimeMaxSec=' + str(remaining), '--property=TimeoutStopSec=3',
                    '--property=SendSIGKILL=yes', '--property=FinalKillSignal=9',
                    '/usr/bin/env', '-i', 'PATH=/usr/bin:/usr/sbin', 'LANG=C', 'LC_ALL=C',
                    '/usr/bin/bash', '--noprofile', '--norc', '-c', ABSOLUTE_ROOT_EXEC, '--', root_deadline(work_end), unit] + argv[len(ROOT_PREFIX):]
            row['dispatched_argv'] = selected
            process = subprocess.Popen(selected, stdin=subprocess.DEVNULL, stdout=handles[0], stderr=handles[1],
                                       start_new_session=True, env={'PATH': '/usr/bin:/usr/sbin', 'LANG': 'C', 'LC_ALL': 'C'}, umask=0o077)
            row['pid'] = process.pid
            while True:
                require(all(path.stat().st_size <= LOG_CAP for path in logs)
                        and self.total_bytes + sum(path.stat().st_size for path in logs) <= 80 * 1024 * 1024,
                        'command-log-bound')
                if time.monotonic() >= work_end:
                    row['timed_out'] = True
                    raise Rejected('command-deadline')
                if leader_exited_unreaped(process):
                    row['exit'], row['forced_cleanup'] = settle_owned_process(process, cleanup_end)
                    row['waited'] = True
                    row['group_absent'] = True
                    require(not row['forced_cleanup'], 'command-group-survived')
                    break
                time.sleep(min(.02, max(0, deadline - time.monotonic())))
        except BaseException as caught:
            error = caught
        finally:
            root_close_failed = False
            if root_operation and row.get('root_start_guard_created'):
                try:
                    self.root_metadata(GUARD_CLOSE, unit, cleanup_end)
                    row['root_start_guard_closed'] = True
                except BaseException as caught:
                    error = error or caught
                    root_close_failed = True
            if process is not None:
                if process.returncode is None:
                    try:
                        if root_operation:
                            try:
                                unsettled = (not leader_exited_unreaped(process)
                                             or any(pid != process.pid for pid in
                                                    process_group_members(process.pid, timeout=max(.001, min(1.0, cleanup_end - time.monotonic())))))
                            except BaseException as caught:
                                error = error or caught
                                unsettled = True
                            root_ambiguous = (error is not None or row['exit'] != 0 or
                                              not row['waited'] or unsettled or root_close_failed)
                            if root_ambiguous:
                                left = max(.001, cleanup_end - time.monotonic())
                                stop = subprocess.run(['/usr/bin/sudo', '-n', '/usr/bin/timeout', '--signal=KILL', str(left),
                                    '/usr/bin/systemctl', 'stop', '--no-block', unit], stdin=subprocess.DEVNULL,
                                    stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=left,
                                    env={'PATH': '/usr/bin:/usr/sbin', 'LANG': 'C', 'LC_ALL': 'C'})
                                require(stop.returncode == 0, 'root-utility-stop-request')
                        row['exit'], forced = settle_owned_process(process, cleanup_end, force=True)
                        row['forced_cleanup'] = row['forced_cleanup'] or forced
                        row['waited'] = True
                        row['group_absent'] = True
                    except BaseException as caught:
                        error = error or caught
                else:
                    row['exit'], row['waited'] = process.returncode, True
                    row['group_absent'] = absent(process.pid)
                    if not row['group_absent']:
                        error = error or Rejected('command-group-unjoined-after-reap')
            if root_operation and row.get('root_start_guard_created'):
                try:
                    left = max(.001, cleanup_end - time.monotonic())
                    inspect = subprocess.run(['/usr/bin/sudo', '-n', '/usr/bin/timeout', '--signal=KILL', str(left),
                        '/usr/bin/env', '-i', 'PATH=/usr/bin:/usr/sbin', 'LANG=C', 'LC_ALL=C',
                        '/usr/bin/bash', '--noprofile', '--norc', '-c', UTILITY_SETTLED, '--', unit],
                        stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, timeout=left,
                        env={'PATH': '/usr/bin:/usr/sbin', 'LANG': 'C', 'LC_ALL': 'C'})
                    row['root_utility_cgroup_empty'] = inspect.returncode == 0 and inspect.stdout == b'ROOT_TOOL_CGROUP_SETTLED\n'
                    require(row['root_utility_cgroup_empty'], 'root-utility-cgroup-unjoined')
                except BaseException as caught:
                    error = error or caught
            for handle in handles:
                try:
                    handle.close()
                except OSError as caught:
                    error = error or caught
            row['logs'] = []
            for path in logs:
                try:
                    data = read(path, LOG_CAP, cleanup_end)
                    row['logs'].append({'name': path.name, 'bytes': len(data), 'sha256': sha(data)})
                    self.total_bytes += len(data)
                except BaseException as caught:
                    error = error or caught
            if time.monotonic() >= cleanup_end:
                error = error or Rejected('late-command-completion')
            row['elapsed_seconds'] = time.monotonic() - started
            row['failure'] = type(error).__name__ if error else None
            try:
                save(self.output / f'command-{row["ordinal"]:02d}.json', row, cleanup_end)
            except BaseException as caught:
                error = error or caught
            if time.monotonic() >= cleanup_end:
                error = error or Rejected('late-phase-record-publication')
        if error:
            raise error
        require(not row['forced_cleanup'] and row['group_absent'] and row['waited'], 'command-not-settled')
        require(allow_failure or row['exit'] == 0, 'command-nonzero')
        data = read(logs[0], LOG_CAP, cleanup_end)
        require(time.monotonic() < cleanup_end, 'late-stdout-return')
        return row['exit'], data


BOOTSTRAP = r'''
set -euo pipefail; umask 077
diagnostic_stage=base
trap 'code=$?; if ((code!=0)); then printf "ROOT_BOOTSTRAP_FAILURE:%s:%d\n" "$diagnostic_stage" "$code" >&2; fi' EXIT
[[ $1 =~ ^[0-9a-f]{32}$ ]] || exit 1
target=/var/lib/appsurface-evidence-bootstrap-$1; shift
for parent in / /var /var/lib; do
 [[ -d $parent && ! -L $parent && $(/usr/bin/stat -c '%u:%g' -- "$parent") == 0:0 ]]
 (( (8#$(/usr/bin/stat -c %a -- "$parent") & 0022)==0 ))
done
[[ ! -e $target && ! -L $target ]]
/usr/bin/mkdir -m0700 -- "$target"
while (($#)); do
 diagnostic_stage=source-shape
 source=$1; name=$2; hash=$3; shift 3
 [[ $name =~ ^[a-zA-Z0-9._-]+$ && $name != . && $name != .. && ${#name} -le 255 && $hash =~ ^[0-9a-f]{64}$ && -f $source && ! -L $source ]]
 cap=131072; [[ $name != source-capture.json ]] || cap=2097152
 [[ $(/usr/bin/stat -c %h -- "$source") == 1 && $(/usr/bin/stat -c %s -- "$source") -le $cap ]]
 diagnostic_stage=source-digest
 printf '%s  %s\n' "$hash" "$source" | /usr/bin/sha256sum --check --strict --status
 diagnostic_stage=copy
 /usr/bin/dd if="$source" of="$target/$name" iflag=nofollow,nonblock,count_bytes oflag=nofollow conv=fsync,excl count=$((cap+1)) status=none
 diagnostic_stage=seal
 /usr/bin/chown 0:0 -- "$target/$name"
 mode=0444; [[ $name != acquire-inbound.sh && $name != prepare-root-inputs-v2.sh ]] || mode=0500
 /usr/bin/chmod "$mode" -- "$target/$name"
 diagnostic_stage=destination-digest
 printf '%s  %s\n' "$hash" "$target/$name" | /usr/bin/sha256sum --check --strict --status
 diagnostic_stage=source-recheck
 printf '%s  %s\n' "$hash" "$source" | /usr/bin/sha256sum --check --strict --status
done
'''

AUDIT_COPY = r'''
set -euo pipefail; umask 077
[[ $1 =~ ^[0-9a-f]{32}$ && $3 =~ ^[0-9a-f]{64}$ ]]
target=/var/lib/appsurface-evidence-input-$1/control/os-audit.json
[[ -f $2 && ! -L $2 && $(/usr/bin/stat -c %h -- "$2") == 1 && $(/usr/bin/stat -c %s -- "$2") -le 1048576 ]]
printf '%s  %s\n' "$3" "$2" | /usr/bin/sha256sum --check --strict --status
/usr/bin/dd if="$2" of="$target" iflag=nofollow,nonblock,count_bytes oflag=nofollow conv=fsync,excl count=1048577 status=none
/usr/bin/chown 0:0 -- "$target"; /usr/bin/chmod 0600 -- "$target"
printf '%s  %s\n' "$3" "$target" | /usr/bin/sha256sum --check --strict --status
printf '%s  %s\n' "$3" "$2" | /usr/bin/sha256sum --check --strict --status
'''

DISPATCH = r'''
set -euo pipefail; umask 077
# This is the existing audited payload-file envelope, not the per-log budget.
ulimit -f 262144
fixture_stdout_pid=; fixture_stderr_pid=
fixture_stdout_fifo=; fixture_stderr_fifo=
fixture_stdout_fifo_owned=0; fixture_stderr_fifo_owned=0
finish_fixture_capture() {
 local status=$1 pump_status=0 pid
 trap - EXIT
 for pid in "$fixture_stdout_pid" "$fixture_stderr_pid"; do
  [[ -n $pid ]] || continue
  pump_status=0; wait "$pid" || pump_status=$?
  if ((status==0 && pump_status!=0)); then status=$pump_status; fi
 done
 if ((fixture_stdout_fifo_owned)); then
  rm -f -- "$fixture_stdout_fifo" || { ((status!=0)) || status=1; }
 fi
 if ((fixture_stderr_fifo_owned)); then
  rm -f -- "$fixture_stderr_fifo" || { ((status!=0)) || status=1; }
 fi
 # The prearmed root utility timer owns this entire join, including partial
 # setup. A missing/failed pump or any earlier fixture failure cannot pass.
 exit "$status"
}
capture_fixture() {
 local stdout=$1 stderr=$2 fixture_status=0
 shift 2
 fixture_stdout_fifo=$stdout.pipe; fixture_stderr_fifo=$stderr.pipe
 trap 'finish_fixture_capture "$?"' EXIT
 # Each FIFO is created exclusively in the fixed root-owned private control
 # directory. Retain every ordinary background child immediately for wait.
 mkfifo -m 600 -- "$fixture_stdout_fifo"; fixture_stdout_fifo_owned=1
 mkfifo -m 600 -- "$fixture_stderr_fifo"; fixture_stderr_fifo_owned=1
 (trap - EXIT; ulimit -f 8192; ulimit -c 0; set -C; exec cat <"$fixture_stdout_fifo" >"$stdout") &
 fixture_stdout_pid=$!
 (trap - EXIT; ulimit -f 8192; ulimit -c 0; set -C; exec cat <"$fixture_stderr_fifo" >"$stderr") &
 fixture_stderr_pid=$!
 "$@" >"$fixture_stdout_fifo" 2>"$fixture_stderr_fifo" || fixture_status=$?
 finish_fixture_capture "$fixture_status"
}
[[ $1 =~ ^[0-9a-f]{32}$ ]]; g=$1; name=$2; hash=$3; shift 3
root=/var/lib/appsurface-evidence-input-$g
stage=/var/lib/appsurface-evidence-fixture/$g; private=/run/appsurface-evidence-fixture/$g
[[ -d /run/appsurface-evidence-fixture && ! -L /run/appsurface-evidence-fixture ]]
[[ -d /var/lib/appsurface-evidence-fixture && ! -L /var/lib/appsurface-evidence-fixture ]]
[[ $(/usr/bin/stat -c '%u:%g:%a' -- "$stage") == 0:0:555 && $(/usr/bin/stat -c '%u:%g:%a' -- "$private") == 0:0:700 ]]
[[ -f $private/request.json && ! -L $private/request.json && $(/usr/bin/stat -c '%u:%g:%a:%h' -- "$private/request.json") == 0:0:600:1 ]]
[[ $(/usr/bin/stat -c '%u:%g:%a' -- "$root/control") == 0:0:700 ]]
/usr/bin/jq -ncS --arg g "$g" '{schema:"issue779-native-dispatch-marker-v1",generation:$g,fixture_parent_absent:true}' >"$root/control/fixture-dispatch-marker.json"
[[ $name == checkpoint-n13-owner-killed.sh || $name == checkpoint-n14-owner-stopped.sh ]]
printf '%s  %s\n' "$hash" "$root/reviewed/$name" | /usr/bin/sha256sum --check --strict --status
capture_fixture "$root/control/fixture-stdout.log" "$root/control/fixture-stderr.log" /usr/bin/bash --noprofile --norc "$root/reviewed/$name" "$@"
'''


READ_ROOT_ARCHIVE = r'''
set -euo pipefail; umask 077
[[ $1 =~ ^[0-9a-f]{32}$ ]]
control=/var/lib/appsurface-evidence-input-$1/control; path=$control/native.tar
[[ ! -L /var && ! -L /var/lib && ! -L /var/lib/appsurface-evidence-input-$1 && ! -L $control && ! -L $path ]]
[[ $(/usr/bin/stat -c '%u:%g:%a' -- "$control") == 0:0:700 && -f $path ]]
[[ $(/usr/bin/stat -c '%u:%g:%a:%h' -- "$path") == 0:0:600:1 ]]
before=$(/usr/bin/stat -c '%d:%i:%s:%f:%h:%u:%g:%y:%z' -- "$path")
size=$(/usr/bin/stat -c %s -- "$path"); ((size>0 && size<=33619968))
/usr/bin/dd if="$path" iflag=nofollow,nonblock,count_bytes count=$((size+1)) status=none
[[ $(/usr/bin/stat -c '%d:%i:%s:%f:%h:%u:%g:%y:%z' -- "$path") == "$before" ]]
'''

ROOT_ARCHIVE_SHA = r'''
set -euo pipefail
[[ $1 =~ ^[0-9a-f]{32}$ ]]; path=/var/lib/appsurface-evidence-input-$1/control/native.tar
[[ -f $path && ! -L $path && $(/usr/bin/stat -c '%u:%g:%a:%h' -- "$path") == 0:0:600:1 ]]
/usr/bin/sha256sum -- "$path" | /usr/bin/cut -c1-64
'''


ARCHIVE_NAMES={'fixture-stdout.log','fixture-stderr.log','root.stdout','root.stderr',
 'owner-death-result.json','QUARANTINED','product-output.list','transport-receipt.json'}
CAPS={'fixture-stdout.log':65536,'fixture-stderr.log':65536,'root.stdout':131072,'root.stderr':131072,
 'owner-death-result.json':65536,'QUARANTINED':256,'product-output.list':4096,'transport-receipt.json':65536}

def inspect_archive(data):
    require(type(data) is bytes and 0 < len(data) <= ARCHIVE_CAP, 'archive-size')
    values={}; total=0
    try:
        with tarfile.open(fileobj=io.BytesIO(data),mode='r:') as tar:
            members=tar.getmembers()
            require(len(members)==len(ARCHIVE_NAMES), 'archive-entry-count')
            names=[]; entries=[]
            for member in members:
                name=member.name
                require(name in ARCHIVE_NAMES and name not in values and member.isfile() and member.mode==0o600 and member.uid==member.gid==member.mtime==0 and not member.issym() and not member.islnk() and not member.linkname and not member.pax_headers and not member.uname and not member.gname and member.size<=CAPS[name], 'archive-member')
                names.append(name)
                stream=tar.extractfile(member); require(stream is not None, 'archive-read')
                raw=stream.read(CAPS[name]+1); require(len(raw)==member.size and len(raw)<=CAPS[name], 'archive-member-size')
                total+=len(raw); require(total<=32*1024*1024, 'archive-expanded-size')
                values[name]=raw; entries.append((name,raw))
            require(names==sorted(names), 'archive-member-order')
            canonical=io.BytesIO()
            with tarfile.open(fileobj=canonical,mode='w:',format=tarfile.USTAR_FORMAT) as rebuilt:
                for name,content in entries:
                    info=tarfile.TarInfo(name); info.size=len(content); info.mode=0o600; info.uid=info.gid=info.mtime=0
                    rebuilt.addfile(info,io.BytesIO(content))
            require(canonical.getvalue()==data, 'archive-noncanonical')
    except (tarfile.TarError, OSError, EOFError):
        raise Rejected('archive-invalid')
    require(set(values)==ARCHIVE_NAMES and values['product-output.list']==b''
            and values['QUARANTINED']==b'OWNER_DEATH_OBSERVED_OUTPUT_QUARANTINED\n', 'owner-death-retention-set')
    return values

def decode_data(raw):
    try: return json.loads(raw.decode('utf-8'), object_pairs_hook=unique)
    except (UnicodeError, json.JSONDecodeError, ValueError): raise Rejected('retained-json')

def validate_capture(capture):
    require(type(capture) is dict and capture.get('schema') == 'issue779-csharp-common-source-capture-v4'
        and capture.get('validation_commit') == SOURCE and capture.get('previous_validation_commit') == PARENT
        and capture.get('source_count') == 2872 and capture.get('source_tree') == 'c9ed8adcc08d050dc70af6885d31171038a36f86'
        and capture.get('clean') is True and capture.get('index_checksum_verified') is True
        and capture.get('index_tree_blob_modes_equal') is True and capture.get('no_alternates') is True
        and capture.get('native_credit') is False and capture.get('all16_final_integrated_native_verified') is False,
        'capture-identity')
    source = capture.get('source')
    require(type(source) is dict and set(source) == {'sha256', 'modes'}, 'capture-map-container')
    raw = source.get('sha256'); modes = source.get('modes')
    require(type(raw) is dict and type(modes) is dict and len(raw) == len(modes) == 2872 and set(raw) == set(modes), 'capture-map-shape')
    for name, digest in raw.items():
        require(type(name) is str and type(digest) is str and re.fullmatch('[0-9a-f]{64}', digest) is not None
            and type(modes[name]) is str and modes[name] in ('0644', '0755'), 'capture-map-value')
    full = {name: {'sha256': raw[name], 'mode': modes[name]} for name in sorted(raw)}
    require(sha(json.dumps(full, sort_keys=True, separators=(',', ':')).encode()) == SOURCE_MAP, 'capture-raw-full-map')
    require(sha(json.dumps(source, sort_keys=True, separators=(',', ':')).encode()) == CANONICAL_SOURCE_MAP, 'capture-compact-builder-map')
    projection = {'validation_commit': capture['validation_commit'],
        'previous_validation_commit': capture['previous_validation_commit'], 'source_count': capture['source_count'],
        'tree': capture['source_tree'], 'source': source}
    require(sha(json.dumps(projection, sort_keys=True, separators=(',', ':')).encode()) == CAPTURE_PROJECTION_SHA,
        'capture-projection')
    return True





# Byte-identical common prerequisite contract; no native acceptance authority.
INTEGRATED_BUILD_SOURCE = '"""Pure build prerequisites shared by the image builder and native case readers.\n\nThis module performs no I/O and issues no native credit. All fourteen images\nmust carry actual passing source-selected core and CLI test results. Commands\nare checked by their operation, project and selector, not an incidental count\nof unrelated Git/package queries. Runtime case acceptance remains separate.\n"""\n\nimport pathlib\nimport re\n\nSCHEMA = "issue779-integrated16-build-v1"\nSELECTORS = ("None", *(f"N{i:02d}" for i in range(4, 17)))\nPIPE_METHOD = "OwnedRawPipeReadWrappingUsesHandleModeAndJoinsBothEofs"\nFRAME_METHOD = "OwnerDeathVariantIsExclusiveAndFrameIsExactBoundedData"\nCLI_METHOD = "CompiledImageOwnsOnlyItsSelectedCheckpoint"\nCORE_PROJECT = "Evidence/ForgeTrust.AppSurface.Evidence.Supervision.Tests/ForgeTrust.AppSurface.Evidence.Supervision.Tests.csproj"\nCLI_PROJECT = "Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj"\nCLI_TEST_PROJECT = "Cli/ForgeTrust.AppSurface.Cli.Tests/ForgeTrust.AppSurface.Cli.Tests.csproj"\nCORE_CLASS = "ForgeTrust.AppSurface.Evidence.Supervision.Tests."\nCLI_CLASS = "ForgeTrust.AppSurface.Cli.Tests.EvidenceNativeQualificationTests"\nSOURCE_PHASES = ("source_before", "source_after_assets", "source_after_build", "source_final")\n\n\nclass BuildContractError(ValueError):\n    """Closed metadata rejection; never include caller values in its message."""\n\n\ndef require(ok, category):\n    if not ok:\n        raise BuildContractError(category)\n\n\ndef is_sha(value):\n    return type(value) is str and re.fullmatch("[0-9a-f]{64}", value) is not None\n\n\ndef expected_regressions(selector):\n    require(selector in SELECTORS, "build-selector")\n    if selector in ("N13", "N14"):\n        return (("owner_death_phase_frame_regression", CORE_CLASS + "LinuxOwnerDeathPhaseFrameTests", FRAME_METHOD),\n                ("owner_death_cli_qualification", CLI_CLASS, CLI_METHOD))\n    return (("linux_pipe_regression", CORE_CLASS + "LinuxOutputPipesTests", PIPE_METHOD),\n            ("compiled_cli_qualification", CLI_CLASS, CLI_METHOD))\n\n\ndef validate_command_records(commands, selector):\n    require(selector in SELECTORS, "build-selector")\n    require(type(commands) is list and 0 < len(commands) <= 128, "build-command-bound")\n    for ordinal, row in enumerate(commands):\n        require(type(row) is dict and type(row.get("ordinal")) is int and row["ordinal"] == ordinal,\n                "build-command-ordinal")\n        args = row.get("argv")\n        require(type(args) is list and args and all(type(arg) is str and arg for arg in args),\n                "build-command-arguments")\n        # MSBuild accepts /p:, -property:, case aliases and multi-property\n        # arguments. A second selector in any such form cannot be ignored.\n        # Response files are also forbidden: they can supply unseen properties.\n        canonical = "-p:EvidencePrivateQualification=" + selector\n        require(not any(arg.startswith("@") for arg in args)\n                and all("evidenceprivatequalification" not in arg.lower()\n                        or (selector != "None" and arg == canonical) for arg in args),\n                "build-selector-alias-or-response-file")\n        require(type(row.get("exit")) is int and row["exit"] == 0 and row.get("failure") is None\n                and row.get("waited") is True and row.get("group_absent") is True\n                and row.get("forced_cleanup") is False and row.get("timed_out") is False\n                and row.get("late_completion") is not True, "build-command-terminal")\n        for name, expected in (("pipe_eofs", [True, True]), ("read_errors", [False, False]),\n                               ("write_errors", [False, False]), ("discarded_bytes", [0, 0])):\n            values = row.get(name)\n            require(type(values) is list and len(values) == 2\n                    and all(type(actual) is type(wanted) and actual == wanted\n                            for actual, wanted in zip(values, expected)), "build-command-output")\n        logs = row.get("logs")\n        require(type(logs) is list and len(logs) == 2\n                and all(type(log) is dict and is_sha(log.get("sha256"))\n                        and type(log.get("bytes")) is int and 0 <= log["bytes"] <= 8 * 1024 * 1024\n                        for log in logs), "build-command-logs")\n    selected = [row for row in commands if pathlib.PurePosixPath(row["argv"][0]).name == "dotnet"\n                and len(row["argv"]) > 1 and row["argv"][1] in ("restore", "publish", "test", "build")]\n    require([row["argv"][1] for row in selected] == ["restore", "publish", "restore", "test", "restore", "test"],\n            "build-operation-sequence")\n    projects = (CLI_PROJECT, CLI_PROJECT, CORE_PROJECT, CORE_PROJECT, CLI_TEST_PROJECT, CLI_TEST_PROJECT)\n    for row, project in zip(selected, projects):\n        args = row["argv"]\n        require(len(args) > 2 and args[2].endswith("/" + project), "build-project")\n        flags = [arg for arg in args if arg.startswith("-p:EvidencePrivateQualification=")]\n        require(flags == ([] if selector == "None" else ["-p:EvidencePrivateQualification=" + selector]),\n                "build-command-selector")\n        require(("--locked-mode" in args) if args[1] == "restore" else ("--no-restore" in args),\n                "build-restore-mode")\n        if args[1] in ("test", "publish"):\n            require("-p:UseSharedCompilation=false" in args, "build-compiler-mode")\n    for row, (_, class_name, method) in zip((selected[3], selected[5]), expected_regressions(selector)):\n        args = row["argv"]\n        require(args.count("--filter") == 1 and args[args.index("--filter") + 1]\n                == "FullyQualifiedName=" + class_name + "." + method, "build-test-filter")\n    require(sum(arg.startswith("-p:EvidencePrivateQualification=") for row in commands for arg in row["argv"])\n            == (0 if selector == "None" else 6), "build-selector-cardinality")\n    return True\n\n\ndef validate_build_receipt(receipt, selector, source):\n    """Validate a measured receipt against the independently pinned source tuple.\n\n    ``source`` has commit, parent, tree, count, capture, projection, map and\n    raw_map fields. It comes from the immutable common capture, never from the\n    receipt being validated. File/archive checks remain with the I/O caller.\n    """\n    require(selector in SELECTORS and type(receipt) is dict and type(source) is dict, "build-receipt-shape")\n    require(set(source) == {"commit", "parent", "tree", "count", "capture", "projection", "map", "raw_map"},\n            "build-source-binding-shape")\n    require(receipt.get("schema") == SCHEMA and type(receipt.get("exit")) is int and receipt["exit"] == 0\n            and receipt.get("failure") is None and receipt.get("diagnostics") == [], "build-receipt-terminal")\n    require(receipt.get("private_qualification") == selector and receipt.get("selected_variant") == selector,\n            "build-receipt-selector")\n    for field, binding in (("source_commit", "commit"), ("source_revision", "commit"), ("source_parent", "parent"),\n                           ("source_count", "count"), ("capture_sha256", "capture"),\n                           ("source_capture_sha256", "capture"), ("capture_input_sha256", "capture"),\n                           ("capture_projection_sha256", "projection"), ("source_map_sha256", "map"),\n                           ("source_capture_map_sha256", "raw_map"), ("harness_parent", "commit"),\n                           ("harness_retry_parent", "commit"), ("harness_retry_parent_parent", "parent")):\n        require(type(receipt.get(field)) is type(source[binding]) and receipt[field] == source[binding],\n                "build-receipt-source")\n    require(receipt.get("build_prerequisite_only") is True and receipt.get("test_execution") is True\n            and all(receipt.get(field) is False for field in ("native_execution", "native_acceptance", "checkpoint_pass",\n                                                            "review_clear", "source_review_clear", "fixture_review_clear",\n                                                            "root_transport_review_clear"))\n            and receipt.get("os_audit") is None, "build-receipt-scope")\n    validate_command_records(receipt.get("commands"), selector)\n    for phase in SOURCE_PHASES:\n        fact = receipt.get(phase)\n        require(type(fact) is dict and fact.get("head") == source["commit"] and fact.get("tree") == source["tree"]\n                and type(fact.get("count")) is int and fact["count"] == source["count"]\n                and fact.get("source_map_sha256") == source["map"]\n                and all(fact.get(key) is True for key in ("index_tree_matches", "physical_git_sha1", "physical_sha256_modes")),\n                "build-source-recheck")\n    for key, class_name, method in expected_regressions(selector):\n        fact = receipt.get(key)\n        require(type(fact) is dict and fact.get("class_name") == class_name and fact.get("method") == method\n                and fact.get("native_acceptance") is False and fact.get("root_factory_exercised") is False\n                and fact.get("unprivileged_library_behavior_only") is True\n                and is_sha(fact.get("trx_sha256")), "build-regression-binding")\n        counters = fact.get("counters")\n        require(type(counters) is dict and all(counters.get(key) == value for key, value in\n                {"total": "1", "executed": "1", "passed": "1", "failed": "0", "error": "0",\n                 "notExecuted": "0", "timeout": "0", "aborted": "0"}.items()), "build-regression-outcome")\n    known = {"owner_death_phase_frame_regression", "owner_death_cli_qualification", "linux_pipe_regression",\n             "compiled_cli_qualification"}\n    expected = {row[0] for row in expected_regressions(selector)}\n    require(not (known - expected) & set(receipt), "build-regression-foreign-selection")\n    return True\n'
INTEGRATED_BUILD_SOURCE_SHA = '4e1e774f57d1b878a0985ac218a27490ae4de8da1f21f81cd696dbe6e9c399f8'
INTEGRATED_SOURCE_BINDING = {'commit': 'fc59bb1254294d624f6779f928020edfe988dd5e', 'parent': '1794a99f9f9857e923fdb2cc8cc81fe0bd5c8c38', 'tree': 'c9ed8adcc08d050dc70af6885d31171038a36f86', 'count': 2872, 'capture': '6e46183742801e864802ab8a7f3e64cd320139a500b7b215b17befe96616d2c1', 'projection': '8457d006fa5061f7249afd6038107907557732e6a51de3d8ba151923007e3ea0', 'map': 'bf7334e8a273dc5693bb9f2226c4b44c90c988fb2f2c8d56ddb0b840adc494c6', 'raw_map': '02b833b330267cafc6e12e75ac82439c6ee80a7e839f7fad27d7097fac8da843'}
_integrated_build_scope = {}
if hashlib.sha256(INTEGRATED_BUILD_SOURCE.encode()).hexdigest() != INTEGRATED_BUILD_SOURCE_SHA:
    raise ValueError('integrated-build-contract-pin')
exec(compile(INTEGRATED_BUILD_SOURCE, '<pinned-pure-build-contract>', 'exec'), _integrated_build_scope)
def validate_integrated_build(receipt, selector):
    _integrated_build_scope['validate_build_receipt'](receipt, selector, INTEGRATED_SOURCE_BINDING)
    validate_integrated_artifact_metadata(receipt)
def validate_integrated_artifact_metadata(receipt):
    artifacts = receipt.get('artifacts')
    check = _integrated_build_scope['require']
    check(all(name not in receipt or receipt[name] is False for name in
              ('runtime_acceptance', 'trusted_enabled', 'native_credit', 'all16_final_integrated_native_verified')),
          'build-supplemental-acceptance-claim')
    check(type(artifacts) is dict and set(artifacts) == {'source', 'tool', 'runtime'}, 'build-artifact-roles')
    for role, fact in artifacts.items():
        check(type(fact) is dict and _integrated_build_scope['is_sha'](fact.get('tsv_sha256'))
              and _integrated_build_scope['is_sha'](fact.get('nodes_sha256')), 'build-artifact-digests')
        check(type(fact.get('file_count')) is int and 0 < fact['file_count'] <= 8192
              and type(fact.get('node_count')) is int and fact['file_count'] <= fact['node_count'] <= 8192
              and type(fact.get('total_bytes')) is int and 0 <= fact['total_bytes'] <= 1073741824,
              'build-artifact-counts')
    check(artifacts['source']['file_count'] == INTEGRATED_SOURCE_BINDING['count'], 'build-artifact-source-count')


def validate_build(receipt, maps, expected_case):
    require(expected_case in ('N13', 'N14'), 'build-selector')
    validate_integrated_build(receipt, expected_case)
    require(set(receipt.get('artifacts',{}))=={'source','tool','runtime'}, 'build-artifact-set')
    for name,fact in receipt['artifacts'].items():
        tsv,nodes_raw=maps[name]; require(sha(tsv)==fact.get('tsv_sha256') and sha(nodes_raw)==fact.get('nodes_sha256'), 'build-map-digest')
        nodes=decode(nodes_raw)
        require(type(nodes) is dict and nodes.get('schema')=='issue779-build-node-inventory-v1' and nodes.get('root_name')==name and type(nodes.get('files')) is dict and type(nodes.get('directories')) is dict and len(nodes['files'])==fact.get('file_count') and len(nodes['files'])+len(nodes['directories'])==fact.get('node_count') and sum(v['bytes'] for v in nodes['files'].values())==fact.get('total_bytes'), 'build-map-shape')
    require(receipt['artifacts']['source']['file_count']==2872, 'build-source-count')
    for key in ('owner_death_phase_frame_regression','owner_death_cli_qualification'):
        test=receipt.get(key); counters=test.get('counters',{}) if type(test) is dict else {}
        require(type(test) is dict and test.get('native_acceptance') is False and re.fullmatch('[0-9a-f]{64}',test.get('trx_sha256','')) is not None and all(counters.get(k)==v for k,v in {'total':'1','executed':'1','passed':'1','failed':'0','error':'0','notExecuted':'0','timeout':'0','aborted':'0'}.items()), 'build-test-receipt')
    # Native 240-second runtime and 5-second stop bounds remain fixture/parser checks.
    return True


def parse_runner_arguments(argv):
    seen=set()
    for value in argv:
        if value.startswith('--'):
            name=value.split('=',1)[0]; require(name not in seen,'duplicate-orchestrator-option'); seen.add(name)
    ap=argparse.ArgumentParser(description=__doc__,allow_abbrev=False)
    ap.add_argument('--execute',action='store_true'); ap.add_argument('--case',choices=('N13','N14'),required=True)
    ap.add_argument('--workspace',required=True); ap.add_argument('--build-root',required=True); ap.add_argument('--reviewed-root',required=True)
    ap.add_argument('--output',required=True); ap.add_argument('--reviewed-script-sha256',required=True)
    return ap.parse_args(argv)


def acquisition_arguments(generation, build_root, bootstrap, case, deadline_ms):
    _,digest=WRAPPERS[case]
    return ['--execute','--case',case,'--generation',generation,'--build-root',str(build_root),'--reviewed-root',str(bootstrap),'--deadline-monotonic-ms',deadline_ms,'--reviewed-script-sha256',REVIEWED_FILES['acquire-inbound.sh'],'--transport-sha256',REVIEWED_FILES['prepare-root-inputs-v2.sh'],'--fixture-sha256',digest,'--source-capture-sha256',CAPTURE_SHA]


def transport_arguments(generation, inbound, receipt, build_sha, deadline_ms, case):
    name,digest=WRAPPERS[case]
    args=['--execute','--reviewed-script-sha256',REVIEWED_FILES['prepare-root-inputs-v2.sh'],'--generation',generation,'--deadline-monotonic-ms',deadline_ms,'--case',case,'--source-commit',SOURCE]
    for kind in ('source','tool','runtime'):
        fact=receipt['artifacts'][kind]
        args += ['--'+kind+'-root',str(inbound/kind),'--'+kind+'-map',str(inbound/'control'/(kind+'.tsv')),'--'+kind+'-map-sha256',fact['tsv_sha256'],'--'+kind+'-nodes',str(inbound/'control'/(kind+'-nodes.json')),'--'+kind+'-nodes-sha256',fact['nodes_sha256']]
    args += ['--build-receipt',str(inbound/'control/build-receipt.json'),'--build-receipt-sha256',build_sha,'--fixture',str(inbound/'scripts'/name),'--fixture-sha256',digest,'--fixture-common',str(inbound/'scripts/checkpoint-owner-death-common.sh'),'--fixture-common-sha256',COMMON_SHA,'--fixture-parser',str(inbound/'scripts/check_owner_death_record.py'),'--fixture-parser-sha256',PARSER_SHA,'--fixture-preparation',str(inbound/'control/fixture-preparation.json'),'--fixture-preparation-sha256',PREPARATION_SHA,'--source-capture',str(inbound/'control/source-capture.json'),'--source-capture-sha256',CAPTURE_SHA]
    return args


def fixture_arguments(root, receipt, maps, build_sha, case, generation, request_sha):
    tool=decode(maps['tool'][1])['files'].get('ForgeTrust.AppSurface.Cli.dll'); runtime=decode(maps['runtime'][1])['files'].get('dotnet')
    require(type(tool) is dict and type(runtime) is dict, 'selected-build-artifact')
    workspace=Path('/var/lib/appsurface-evidence-fixture')/generation
    private=Path('/run/appsurface-evidence-fixture')/generation
    return ['--execute','--source-capture',str(root/'control/source-capture.json'),'--source-capture-sha256',CAPTURE_SHA,'--request',str(private/'request.json'),'--request-sha256',request_sha,'--runtime-host',str(workspace/'runtime/dotnet'),'--runtime-host-sha256',runtime['sha256'],'--entry',str(workspace/'tool/ForgeTrust.AppSurface.Cli.dll'),'--entry-sha256',tool['sha256'],'--build-receipt',str(root/'control/build-receipt.json'),'--build-receipt-sha256',build_sha,'--output-root','/run/appsurface-evidence-'+generation+'/output','--parser',str(root/'reviewed/check_owner_death_record.py'),'--parser-sha256',PARSER_SHA,'--generation',generation]


def validate_captured_lines(values, case, record):
    require(values['fixture-stdout.log']==('OWNER_DEATH_MEASURED:'+case+':QUARANTINED:NOT_NATIVE_ACCEPTANCE\n').encode()
            and values['fixture-stderr.log']==b'', 'owner-death-wrapper-output')
    require(values['root.stdout']==(record['phase_line']+'\n').encode()
            and sha(values['root.stdout'])==record['pipes']['stdout_sha256']
            and sha(values['root.stderr'])==record['pipes']['stderr_sha256'], 'owner-death-pipe-binding')


def validate_owner_death_archive(values, parser_bytes, case, generation, build_sha, transport):
    require(set(values)==ARCHIVE_NAMES and sha(parser_bytes)==PARSER_SHA, 'owner-death-archive-shape')
    record=decode_data(values['owner-death-result.json']); parser=types.ModuleType('owner_death_pinned_parser'); exec(compile(parser_bytes,'<pinned-owner-death-record-parser>','exec',dont_inherit=True),parser.__dict__)
    try: parser.validate(record)
    except (TypeError,KeyError,ValueError): raise Rejected('owner-death-record-invalid')
    require(record.get('case')==case and record.get('generation')==generation and record.get('native_credit') is False, 'owner-death-record-binding')
    validate_captured_lines(values,case,record)
    require(transport.get('schema')=='issue779-owner-death-root-input-transport-v1' and transport.get('case')==case and transport.get('selected_variant')==case and transport.get('generation')==generation and transport.get('source_commit')==SOURCE and transport.get('source_parent')==PARENT and transport.get('source_count')==2872 and transport.get('source_map_sha256')==SOURCE_MAP and transport.get('build_receipt_sha256')==build_sha and transport.get('native_execution') is False and transport.get('native_acceptance_claim') is False, 'transport-record-binding')
    return record


OWNER_ARCHIVE = r'''import io,os,stat,sys,tarfile
G,CASE,LOCAL_OUT,LOCAL_ERR,LOCAL_UID=sys.argv[1:]
if len(G)!=32 or CASE not in ('N13','N14'): raise SystemExit(1)
BASE='/var/lib/appsurface-evidence-input-'+G; PRIVATE='/run/appsurface-evidence-fixture/'+G; OUTPUT='/run/appsurface-evidence-'+G+'/output'
def directory(path,uid):
 s=os.lstat(path)
 if not stat.S_ISDIR(s.st_mode) or s.st_uid!=uid or stat.S_IMODE(s.st_mode)!=0o700: raise SystemExit(1)
def fixed(path,cap,uid):
 before=os.lstat(path)
 if not stat.S_ISREG(before.st_mode) or before.st_uid!=uid or stat.S_IMODE(before.st_mode)!=0o600 or before.st_nlink!=1 or before.st_size>cap: raise SystemExit(1)
 fd=os.open(path,os.O_RDONLY|os.O_NOFOLLOW|os.O_NONBLOCK|os.O_CLOEXEC)
 try:
  opened=os.fstat(fd)
  if (opened.st_dev,opened.st_ino,opened.st_mode,opened.st_size)!=(before.st_dev,before.st_ino,before.st_mode,before.st_size): raise SystemExit(1)
  data=b''
  while True:
   block=os.read(fd,min(65536,cap+1-len(data)))
   if not block: break
   data+=block
   if len(data)>cap or len(data)>before.st_size: raise SystemExit(1)
  if len(data)!=before.st_size: raise SystemExit(1)
  return data
 finally: os.close(fd)
directory(PRIVATE,0)
record_data=fixed(PRIVATE+'/owner-death-result.json',65536,0); record=json.loads(record_data.decode('utf-8'))
if record.get('case')!=CASE or record.get('generation')!=G or type(record.get('worker')) is not dict: raise SystemExit(1)
worker_uid=record['worker'].get('uid')
if type(worker_uid) is not int or worker_uid<=0: raise SystemExit(1)
directory(OUTPUT,worker_uid)
listing=os.listdir(OUTPUT)
if listing: raise SystemExit(1)
items={'fixture-stdout.log':fixed(LOCAL_OUT,65536,int(LOCAL_UID)),'fixture-stderr.log':fixed(LOCAL_ERR,65536,int(LOCAL_UID)),'root.stdout':fixed(PRIVATE+'/root.stdout',131072,0),'root.stderr':fixed(PRIVATE+'/root.stderr',131072,0),'owner-death-result.json':fixed(PRIVATE+'/owner-death-result.json',65536,0),'QUARANTINED':fixed(PRIVATE+'/QUARANTINED',256,0),'product-output.list':b'','transport-receipt.json':fixed(BASE+'/control/transport-receipt.json',65536,0)}
buf=io.BytesIO()
with tarfile.open(fileobj=buf,mode='w:',format=tarfile.USTAR_FORMAT) as tar:
 for name,data in sorted(items.items()):
  info=tarfile.TarInfo(name); info.size=len(data); info.mode=0o600; info.uid=info.gid=info.mtime=0; tar.addfile(info,io.BytesIO(data))
data=buf.getvalue(); path=BASE+'/control/native.tar'; fd=os.open(path,os.O_WRONLY|os.O_CREAT|os.O_EXCL|os.O_NOFOLLOW|os.O_CLOEXEC,0o600)
try:
 view=memoryview(data)
 while view:
  n=os.write(fd,view)
  if n<=0: raise SystemExit(1)
  view=view[n:]
 os.fsync(fd)
finally: os.close(fd)
view=memoryview(data)
while view:
 n=os.write(1,view)
 if n<=0: raise SystemExit(1)
 view=view[n:]
'''


def collect_private_archive(runner, generation, case, output, fixture_logs, deadline):
    runner.phase='bounded-private-retention'
    _,archive=runner.run(ROOT_PREFIX+['-c',OWNER_ARCHIVE,'--',generation,case,str(fixture_logs[0]),str(fixture_logs[1]),str(os.geteuid())],deadline)
    values=inspect_archive(archive); save(output/'native-private.tar',archive,deadline)
    return values,{'private_archive_sha256':sha(archive),'private_archive_bytes':len(archive)}


def run_fixture_with_retention(runner, dispatch, dispatch_end, collect, result):
    failure=None; values=None
    try:
        code,_=runner.run(dispatch,dispatch_end,allow_failure=True); result['fixture_exit']=code
        require(code==0,'owner-death-fixture-nonzero')
    except BaseException as caught: failure=caught
    try: values,facts=collect(); result.update(facts)
    except BaseException as caught:
        result['retention_failure']={'category':getattr(caught,'category','retention-operation'),'error_class':type(caught).__name__}; failure=failure or caught
    if failure: raise failure
    return values


def publish_terminal(output, result, deadline):
    try:
        require(time.monotonic()<deadline,'terminal-deadline'); save(output/'native-receipt.json',result,deadline)
        require(time.monotonic()<deadline,'terminal-deadline'); print('NATIVE_TEST_TERMINAL:'+str(result['exit']),flush=True); return result['exit']
    except BaseException: return 1


def main():
    args=parse_runner_arguments(sys.argv[1:]); start=time.monotonic()
    job_end=int(os.environ.get('JOB_DEADLINE_MONOTONIC_NS','0'))/1_000_000_000; final=min(job_end,start+900)
    require(SOURCE_REVIEW_CLEAR and FIXTURE_REVIEW_CLEAR and ROOT_TRANSPORT_REVIEW_CLEAR and RETENTION_REVIEW_CLEAR and NATIVE_HANDOFF_REVIEW_CLEAR, 'independent-review-pending')
    require(args.execute and sys.platform=='linux' and os.uname().machine=='x86_64' and os.geteuid()>0 and os.getegid()>0 and final-start>850, 'native-platform-or-job-budget')
    workspace,build_root,reviewed,output=map(Path,(args.workspace,args.build_root,args.reviewed_root,args.output))
    require(all(x.is_absolute() for x in (workspace,build_root,reviewed,output)) and not output.exists() and not output.is_symlink(), 'absolute-paths-or-output-reuse')
    output.mkdir(mode=0o700); runner=Runner(output,final); result={'schema':'issue779-owner-death-native-run-v1','case':args.case,'source_commit':SOURCE,'source_parent':PARENT,'source_count':2872,'exit':1,'commands':runner.records,'native_dispatched':False,'native_execution':False,'native_credit':False,'native_acceptance':False,'fixture_result_verified':False,'review_clear':False}
    generation=uuid.uuid4().hex; root=Path('/var/lib/appsurface-evidence-input-'+generation); failure=None; retained=False
    try:
        require(re.fullmatch('[0-9a-f]{64}',args.reviewed_script_sha256) and sha(read(Path(__file__),131072,final))==args.reviewed_script_sha256,'orchestrator-pin')
        capture_raw=read(reviewed/'source-capture.json',4*1024*1024,final); require(sha(capture_raw)==CAPTURE_SHA,'capture-pin'); capture=decode(capture_raw); validate_capture(capture)
        prep_raw=read(reviewed/'preparation.json',131072,final); require(sha(prep_raw)==PREPARATION_SHA,'fixture-preparation-pin')
        case_name,case_sha=WRAPPERS[args.case]; wrapper=read(reviewed/case_name,131072,final); common=read(reviewed/'checkpoint-owner-death-common.sh',131072,final); parser_bytes=read(reviewed/'check_owner_death_record.py',131072,final)
        require(sha(wrapper)==case_sha and sha(common)==COMMON_SHA and sha(parser_bytes)==PARSER_SHA,'fixture-input-pins')
        raw=read(build_root/'receipts/build-receipt.json',4*1024*1024,final); receipt=decode(raw)
        maps={k:(read(build_root/f'handoff/{k}.tsv',1048576,final),read(build_root/f'handoff/{k}-nodes.json',4194304,final)) for k in ('source','tool','runtime')}
        validate_build(receipt,maps,args.case); build_sha=sha(raw); result.update(build_receipt_sha256=build_sha,build_artifacts=receipt['artifacts'],source_capture_sha256=CAPTURE_SHA)
        runner.phase='reviewed-inputs'
        for name,pin in REVIEWED_FILES.items(): require(re.fullmatch('[0-9a-f]{64}',pin) and sha(read(reviewed/name,131072,final))==pin,'reviewed-pin')
        review=decode(read(reviewed/'source-review.json',65536,final)); require(review.get('source_commit')==SOURCE and review.get('source_parent')==PARENT and review.get('source_count')==2872 and review.get('review_clear') is True,'source-review-binding')
        result['generation']=generation; bootstrap=Path('/var/lib/appsurface-evidence-bootstrap-'+generation)
        triples=[part for name,pin in REVIEWED_FILES.items() for part in (str(reviewed/name),name,pin)]+[str(reviewed/'source-capture.json'),'source-capture.json',CAPTURE_SHA]
        runner.phase='root-bootstrap'; runner.run(ROOT_PREFIX+['-c',BOOTSTRAP,'--',generation]+triples,min(final-5,time.monotonic()+30))
        runner.phase='root-acquisition'; acq_end=min(final-5,time.monotonic()+120); acq_ms=root_deadline(acq_end)
        runner.run(ROOT_PREFIX+[str(bootstrap/'acquire-inbound.sh')]+acquisition_arguments(generation,build_root,bootstrap,args.case,acq_ms),acq_end)
        inbound=Path('/var/lib/appsurface-evidence-inbound-'+generation); runner.phase='root-transport'
        runner.run(ROOT_PREFIX+[str(bootstrap/'prepare-root-inputs-v2.sh')]+transport_arguments(generation,inbound,receipt,build_sha,acq_ms,args.case),acq_end)
        runner.phase='same-host-fixed-tmp-os-audit'; audit_dir=audit_output_directory(generation); require(not os.path.lexists(audit_dir),'audit-output-reuse'); audit_dir.mkdir(mode=0o700)
        audit_end=min(final-5,time.monotonic()+60)
        runner.run(audit_arguments(root,reviewed,audit_dir,audit_end),audit_end)
        audit_raw=read(audit_dir/'os-audit.json',1048576,audit_end); audit_sha=sha(audit_raw); runner.run(ROOT_PREFIX+['-c',AUDIT_COPY,'--',generation,str(audit_dir/'os-audit.json'),audit_sha],audit_end)
        workflow='github-actions-'+os.environ['GITHUB_RUN_ID']+'-'+os.environ['GITHUB_RUN_ATTEMPT']
        deadline_utc=datetime.fromtimestamp(time.time()+240,timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ')
        boottime_ms=int(time.clock_gettime(time.CLOCK_BOOTTIME)*1000)+240000
        helper_path=bootstrap/'prepare-case-workspace.py'
        helper_args=['--generation',generation,'--case',args.case,
            '--source-root',str(inbound/'source'),'--source-map',str(inbound/'control/source.tsv'),
            '--source-map-sha256',receipt['artifacts']['source']['tsv_sha256'],
            '--source-nodes',str(inbound/'control/source-nodes.json'),'--source-nodes-sha256',receipt['artifacts']['source']['nodes_sha256'],
            '--tool-root',str(inbound/'tool'),'--tool-map',str(inbound/'control/tool.tsv'),
            '--tool-map-sha256',receipt['artifacts']['tool']['tsv_sha256'],
            '--tool-nodes',str(inbound/'control/tool-nodes.json'),'--tool-nodes-sha256',receipt['artifacts']['tool']['nodes_sha256'],
            '--runtime-root',str(inbound/'runtime'),'--runtime-map',str(inbound/'control/runtime.tsv'),
            '--runtime-map-sha256',receipt['artifacts']['runtime']['tsv_sha256'],
            '--runtime-nodes',str(inbound/'control/runtime-nodes.json'),'--runtime-nodes-sha256',receipt['artifacts']['runtime']['nodes_sha256'],
            '--build-receipt',str(inbound/'control/build-receipt.json'),'--build-receipt-sha256',build_sha,
            '--source-capture',str(inbound/'control/source-capture.json'),'--source-capture-sha256',CAPTURE_SHA,
            '--job-deadline-utc',deadline_utc,'--job-deadline-boottime-ms',str(boottime_ms),
            '--workflow-identity',workflow]
        runner.phase='case-workspace-materialization'
        helper_end=min(final-65,time.monotonic()+180)
        _,staged=runner.run(ROOT_PREFIX+['/usr/bin/python3','-B',str(helper_path)]+helper_args,helper_end)
        parse_workspace_stage_marker(staged,args.case,generation)
        runner.phase='case-workspace-hash-binding'
        _,hash_binding=runner.run(ROOT_PREFIX+['/usr/bin/python3','-B','-c',WORKSPACE_HASH_READ,generation],min(final-65,time.monotonic()+30))
        request_sha,policy_sha=parse_workspace_hash_marker(hash_binding)
        result['case_workspace']={'case':args.case,'generation':generation,'request_sha256':request_sha,'policy_sha256':policy_sha,'staged_only':True}
        fixture=fixture_arguments(root,receipt,maps,build_sha,args.case,generation,request_sha)
        result['native_dispatched']=True; runner.phase='actual-owner-death-'+args.case
        dispatch=ROOT_PREFIX+['-c',DISPATCH,'--',generation,case_name,case_sha]+fixture
        dispatch_end=min(final-35,time.monotonic()+630)
        # The command recorder owns the two joined wrapper-pump logs for retention.
        logbase=output/f'{len(runner.records):02d}'
        fixture_logs=(output/(f'{len(runner.records):02d}-stdout.log'),output/(f'{len(runner.records):02d}-stderr.log'))
        values=run_fixture_with_retention(runner,dispatch,dispatch_end,lambda:collect_private_archive(runner,generation,args.case,output,fixture_logs,min(final-5,time.monotonic()+30)),result)
        transport=decode_data(values['transport-receipt.json']); record=validate_owner_death_archive(values,parser_bytes,args.case,generation,build_sha,transport)
        result.update(fixture_generation=generation,owner_death_record_sha256=sha(values['owner-death-result.json']),transport_receipt_sha256=sha(values['transport-receipt.json']),fixture_result_verified=True,native_execution=True,native_credit=False,native_acceptance=False,measurement_summary={'case':args.case,'signal':record['signal']['name'],'owner_runtime_usec':record['systemd_bound']['runtime_max_usec'],'stop_usec':record['systemd_bound']['timeout_stop_usec'],'owner_exited':record['termination']['owner_exited'],'worker_exited':record['termination']['worker_exited'],'quarantined':record['workspace']['quarantined']})
        retained=True
    except BaseException as caught: failure=caught
    finally:
        result['failure']={'category':getattr(failure,'category','native-operation'),'error_class':type(failure).__name__} if failure else None
        result['elapsed_seconds']=time.monotonic()-start; result['exit']=0 if not failure and retained and result['fixture_result_verified'] else 1
    return publish_terminal(output,result,final-5)


if __name__=='__main__': raise SystemExit(main())
