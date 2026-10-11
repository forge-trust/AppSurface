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
from datetime import datetime

NEGATIVE_CASE = 'N16'
N16_SOURCE_REVIEW_CLEAR = True
N16_FIXTURE_REVIEW_CLEAR = True
N16_ROOT_CLEANUP_REVIEW_CLEAR = True
SOURCE = '0c100a94d93f82fd633e5674009a41659940d010'
PARENT = '03ff51b361d686e6e2590a27e9a4f43529cdbaf2'
SOURCE_MAP = '07e4be7c1c59989a70d88c345ff360e68a0a394a6729877afa7981d6d913d238'
CAPTURE_SHA = '653d30169f6fc3aed53274349304dfebc3aff2b529e42bf3a4524a37fa546fd2'
PARSER_SHA = '4a9f268279e74c0c07828f1b1d3d7fbec603e07e196b0f7827884b59f42f8de2'
REVIEWED_FILES = {'acquire-inbound.sh': 'c9f8ff2f3f9afc4415a678a6ed5289c7a877723da8fa4bf0efed4abf9154eb31', 'prepare-root-inputs-v2.sh': 'dc0b34d7c746460e10d6c77e254411d0f0707793619d4a368626358c949caf08', 'checkpoint-n16-accepted-work.sh': 'dba3b8a6d70a24f5699c26ff8decd7de1dca659c7893af9b7e6dd2de82c68e58', 'check_n16_records.py': '4a9f268279e74c0c07828f1b1d3d7fbec603e07e196b0f7827884b59f42f8de2', 'prepare-os-audit-v2.py': 'b345b2be77b359af23ade0b334caf76d7fdeb14bd2d4a3f0837beb21232df5e0', 'retain-native.sh': 'cdb9b1dde8f37dad5afe784178f3db414b2561d438d7a47941dfa1d68c3534ef', 'source-review.json': '6d0a4874f2bd07a9dbc85e06120bdcdcf69af8fa4bcf8cf5f05a932f8a81e33c'}
SOURCE_REVIEW_SHA = '6d0a4874f2bd07a9dbc85e06120bdcdcf69af8fa4bcf8cf5f05a932f8a81e33c'
ROOT_PREFIX = ['/usr/bin/sudo', '-n', '/usr/bin/env', '-i', 'PATH=/usr/bin:/usr/sbin',
               'LANG=C', 'LC_ALL=C', '/usr/bin/bash', '--noprofile', '--norc']
LOG_CAP = 40 * 1024 * 1024
ARCHIVE_CAP = 33619968
class Rejected(ValueError):
    def __init__(self, category):
        super().__init__('native-test-rejected')
        self.category = category


def require(value, category):
    if not value:
        raise Rejected(category)


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


def validate_private_build_commands(commands):
    require(type(commands) is list and 0 < len(commands) <= 64, 'private-build-command-list')
    selected=[]
    for row in commands:
        require(type(row) is dict and type(row.get('argv')) is list and all(type(x) is str for x in row['argv']), 'private-build-command-shape')
        argv=row['argv']
        if len(argv)>2 and argv[1] in ('restore','publish') and argv[2].endswith('/Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj'):
            require([x for x in argv if x.startswith('-p:EvidencePrivateQualification=')]==['-p:EvidencePrivateQualification=N16'], 'private-build-selection')
            selected.append(argv[1])
    require(sorted(selected)==['publish','restore'], 'private-build-complete-graph')

def validate_build(receipt, maps):
    require(receipt.get('private_qualification')=='N16', 'private-build-receipt-selection')
    validate_private_build_commands(receipt['commands'])
    require(receipt.get('schema')=='issue779-csharp-fdd-build-v5' and type(receipt.get('exit')) is int and receipt['exit']==0 and receipt.get('failure') is None and receipt.get('diagnostics')==[] and receipt.get('source_commit')==SOURCE and receipt.get('harness_parent')==SOURCE and receipt.get('harness_retry_parent')==SOURCE and receipt.get('harness_retry_parent_parent')==PARENT and receipt.get('native_execution') is False and receipt.get('checkpoint_pass') is False and receipt.get('selected_variant')=='N16' and receipt.get('build_prerequisite_only') is True and receipt.get('capture_input_sha256')==CAPTURE_SHA and receipt.get('capture_projection_sha256')=='0c5f9605840c134d9b21866f2eb813111884a431540f0da2eb516ff0146e0c8c', 'build-terminal')
    require(len(receipt['commands'])==43 and [r.get('ordinal') for r in receipt['commands']]==list(range(43)), 'build-commands')
    for row in receipt['commands']:
        require(type(row.get('exit')) is int and row['exit']==0 and row.get('failure') is None and row.get('waited') is True and row.get('group_absent') is True and row.get('timed_out') is False and row.get('forced_cleanup') is False, 'build-command-terminal')
    for phase in ('source_before','source_after_assets','source_after_build','source_final'):
        fact=receipt.get(phase,{})
        require(fact.get('head')==SOURCE and fact.get('count')==2865 and fact.get('source_map_sha256')==SOURCE_MAP and all(fact.get(k) is True for k in ('index_tree_matches','physical_git_sha1','physical_sha256_modes')), 'build-source')
    require(set(receipt.get('artifacts',{}))=={'source','tool','runtime'}, 'build-map-set')
    for name,fact in receipt['artifacts'].items():
        tsv,raw=maps[name]
        require(sha(tsv)==fact.get('tsv_sha256') and sha(raw)==fact.get('nodes_sha256'), 'build-map-digest')
        nodes=decode(raw)
        require(set(nodes)=={'schema','root_name','files','directories'} and nodes['schema']=='issue779-build-node-inventory-v1' and nodes['root_name']==name, 'node-schema')
        require(len(nodes['files'])==fact.get('file_count') and len(nodes['files'])+len(nodes['directories'])==fact.get('node_count') and sum(x['bytes'] for x in nodes['files'].values())==fact.get('total_bytes'), 'node-counts')
    require(receipt['artifacts']['source']['file_count']==2865, 'source-count')
    pipe=receipt.get('linux_pipe_regression')
    require(type(pipe) is dict and set(pipe)=={'method','counters','trx_sha256','unprivileged_library_behavior_only','root_factory_exercised','native_acceptance','runtime_basis'}, 'pipe-regression-schema')
    require(pipe['method']=='OwnedRawPipeReadWrappingUsesHandleModeAndJoinsBothEofs' and type(pipe['counters']) is dict and all(pipe['counters'].get(k)=='1' for k in ('total','executed','passed')) and all(pipe['counters'].get(k)=='0' for k in ('failed','error','notExecuted','timeout','aborted')) and re.fullmatch('[0-9a-f]{64}',pipe.get('trx_sha256','')) is not None and pipe['unprivileged_library_behavior_only'] is True and pipe['root_factory_exercised'] is False and pipe['native_acceptance'] is False and pipe['runtime_basis']=='selected SDK dotnet host', 'pipe-regression-terminal')

def absent(pid):
    try:
        os.killpg(pid, 0)
        return False
    except ProcessLookupError:
        return True
    except PermissionError:
        # Existence without signal permission is not absence and must not bypass cleanup.
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
                if process.poll() is not None:
                    row['exit'] = process.wait()
                    row['waited'] = True
                    require(absent(process.pid), 'command-group-survived')
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
                try:
                    unsettled = process.poll() is None or not absent(process.pid)
                except BaseException as caught:
                    error = error or caught
                    unsettled = True
                root_ambiguous = root_operation and (error is not None or row['exit'] != 0 or not row['waited'] or unsettled or root_close_failed)
                if unsettled or root_ambiguous:
                    row['forced_cleanup'] = True
                    try:
                        if root_operation:
                            left = max(.001, cleanup_end - time.monotonic())
                            stop = subprocess.run(['/usr/bin/sudo', '-n', '/usr/bin/timeout', '--signal=KILL', str(left),
                                '/usr/bin/systemctl', 'stop', '--no-block', unit], stdin=subprocess.DEVNULL,
                                stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=left,
                                env={'PATH': '/usr/bin:/usr/sbin', 'LANG': 'C', 'LC_ALL': 'C'})
                            require(stop.returncode == 0, 'root-utility-stop-request')
                        else:
                            os.killpg(process.pid, signal.SIGKILL)
                    except ProcessLookupError:
                        pass
                    except BaseException as caught:
                        error = error or caught
                try:
                    process.wait(timeout=max(.001, cleanup_end - time.monotonic()))
                    row['exit'], row['waited'] = process.returncode, True
                    while not absent(process.pid) and time.monotonic() < cleanup_end:
                        time.sleep(.01)
                    row['group_absent'] = absent(process.pid)
                    require(row['group_absent'], 'command-group-unjoined')
                except BaseException as caught:
                    error = error or caught
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
 cap=131072; [[ $name != capture-receipt.json ]] || cap=2097152
 [[ $(/usr/bin/stat -c %h -- "$source") == 1 && $(/usr/bin/stat -c %s -- "$source") -le $cap ]]
 diagnostic_stage=source-digest
 printf '%s  %s\n' "$hash" "$source" | /usr/bin/sha256sum --check --strict --status
 diagnostic_stage=copy
 /usr/bin/dd if="$source" of="$target/$name" iflag=nofollow,nonblock,count_bytes oflag=nofollow conv=fsync,excl count=$((cap+1)) status=none
 diagnostic_stage=seal
 /usr/bin/chown 0:0 -- "$target/$name"
 mode=0444; [[ $name != acquire-inbound.sh && $name != retain-native.sh ]] || mode=0500
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
[[ $1 =~ ^[0-9a-f]{32}$ ]]; g=$1; hash=$2; shift 2
root=/var/lib/appsurface-evidence-input-$g
[[ ! -e /run/appsurface-evidence-fixture && ! -L /run/appsurface-evidence-fixture ]]
[[ ! -e /var/lib/appsurface-evidence-fixture && ! -L /var/lib/appsurface-evidence-fixture ]]
[[ $(/usr/bin/stat -c '%u:%g:%a' -- "$root/control") == 0:0:700 ]]
/usr/bin/jq -ncS --arg g "$g" '{schema:"issue779-native-dispatch-marker-v1",generation:$g,fixture_parent_absent:true}' >"$root/control/fixture-dispatch-marker.json"
printf '%s  %s\n' "$hash" "$root/reviewed/checkpoint-n16-accepted-work.sh" | /usr/bin/sha256sum --check --strict --status
capture_fixture "$root/control/fixture-stdout.log" "$root/control/fixture-stderr.log" /usr/bin/bash --noprofile --norc "$root/reviewed/checkpoint-n16-accepted-work.sh" "$@"
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


ARCHIVE_NAMES = {
 'fixture-stdout.log','fixture-stderr.log','retention-selection.json',
 'logs/n16.stdout','logs/n16.stderr','n16-record-summary.json','n16-fixture-result.json',
 'request.json','request-policy.sha256','logs/kill.log','logs/stop.log'}
CAPS={'fixture-stdout.log':8388608,'fixture-stderr.log':8388608,'retention-selection.json':4096,
 'logs/n16.stdout':0,'logs/n16.stderr':114688,'n16-record-summary.json':4096,
 'n16-fixture-result.json':4096,'request.json':65536,'request-policy.sha256':4096,
 'logs/kill.log':8388608,'logs/stop.log':8388608}

def inspect_archive(data):
    require(type(data) is bytes and 0 < len(data) <= ARCHIVE_CAP, 'archive-size')
    values={}; total=0
    try:
        with tarfile.open(fileobj=io.BytesIO(data),mode='r:') as tar:
            members=tar.getmembers()
            require(0 < len(members) <= len(ARCHIVE_NAMES), 'archive-entry-count')
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
    require('retention-selection.json' in values, 'retention-selection-missing')
    selection=decode_data(values['retention-selection.json'])
    require(type(selection) is dict and set(selection)=={'schema','selection_status','data_file_count','data_bytes','missing_fixed_file_count'} and selection.get('schema')=='issue779-native-retention-selection-v1', 'retention-selection-shape')
    require(selection['selection_status'] in ('parent-absent','parent-empty','one-namespace') and type(selection['data_file_count']) is int and selection['data_file_count']==len(values)-1 and type(selection['data_bytes']) is int and selection['data_bytes']==sum(len(v) for k,v in values.items() if k!='retention-selection.json') and type(selection['missing_fixed_file_count']) is int and selection['missing_fixed_file_count']==10-selection['data_file_count'], 'retention-selection-values')
    return values

def decode_data(raw):
    try: return json.loads(raw.decode('utf-8'), object_pairs_hook=unique)
    except (UnicodeError, json.JSONDecodeError, ValueError): raise Rejected('retained-json')

def validate_fixture_result(value, generation, build_sha):
    keys={'schema','generation','source_commit','source_capture_sha256','root_projection_sha256','root_exit','root_stdout_bytes','stderr_sha256','worker_unit','worker_pid','worker_uid','worker_gid','results_gid','owner_load_state','worker_load_state','generated_passwd_group_name_checks','worker_uid_reverse_lookup_exit','worker_gid_reverse_lookup_exit','results_gid_reverse_lookup_exit','root_launch_group_absent','root_launch_fd_directory_absent','workspace_absent','observation_only','native_authority','native_acceptance'}
    require(type(generation) is str and re.fullmatch('[0-9a-f]{32}',generation) is not None and type(build_sha) is str and re.fullmatch('[0-9a-f]{64}',build_sha) is not None, 'n16-fixture-expected-binding')
    require(type(value) is dict and set(value)==keys and value.get('schema')=='issue779-n16-native-fixture-observation-v1' and value.get('generation')==generation and value.get('source_commit')==SOURCE and value.get('source_capture_sha256')==CAPTURE_SHA and value.get('root_projection_sha256')==build_sha, 'n16-fixture-result-shape')
    require(type(value['root_exit']) is int and value['root_exit']==1 and type(value['root_stdout_bytes']) is int and value['root_stdout_bytes']==0 and value['owner_load_state']=='not-found' and value['worker_load_state']=='not-found' and all(value[k] is True for k in ('root_launch_group_absent','root_launch_fd_directory_absent','workspace_absent','observation_only')) and value['native_authority'] is False and value['native_acceptance'] is False, 'n16-fixture-terminal')
    require(re.fullmatch('[0-9a-f]{64}',value['stderr_sha256']) is not None and type(value['worker_unit']) is str and value['worker_unit']=='appsurface-evidence-worker-'+generation+'.service' and type(value['worker_pid']) is int and value['worker_pid']>1, 'n16-fixture-identity')
    require(all(type(value[k]) is int and 0<value[k]<4294967295 for k in ('worker_uid','worker_gid','results_gid')) and value['worker_gid']!=value['results_gid'] and all(type(value[k]) is int and value[k]==2 for k in ('worker_uid_reverse_lookup_exit','worker_gid_reverse_lookup_exit','results_gid_reverse_lookup_exit')) and type(value['generated_passwd_group_name_checks']) is int and value['generated_passwd_group_name_checks']==5, 'n16-fixture-os-observations')

FIXTURE_POLICY = {
    'Id':'checkpoint-empty','Version':'1','ConservativeProfileId':'conservative',
    'Profiles':[{'Id':'empty','Scope':'Targeted','Resources':[],'Producers':[],'Obligations':[]},
        {'Id':'conservative','Scope':'Release','Resources':[{'Id':'build','Readiness':'completion','DeadlineSeconds':1,'Requires':[]}],'Producers':[],'Obligations':[]}],
    'Rules':[{'Id':'docs','Pattern':'docs/**','ProfileId':'empty','Precedence':0}]}


def validate_request_binding(request_raw, checksum_raw, generation, workflow):
    """Check retained request and both checksum rows as data, never as admission authority."""
    require(type(request_raw) is bytes and 0<len(request_raw)<=65536 and type(checksum_raw) is bytes and 0<len(checksum_raw)<=4096, 'request-policy-bounds')
    request=decode_data(request_raw)
    base='/var/lib/appsurface-evidence-fixture/'+generation
    expected={'schema':'evidence-supervisor-linux-v1','mode':'observation','tool_root':base+'/tool',
        'runtime_root':base+'/runtime','runtime_host':base+'/runtime/dotnet','entry_path':base+'/tool/ForgeTrust.AppSurface.Cli.dll',
        'policy_file':base+'/tool/fixture-policy.json','subject_root':base+'/subject',
        'base_revision':PARENT,'subject_revision':SOURCE,'workflow_identity':workflow,
        'paths':['docs/designs/issue-779-csharp-supervision-core.md'],'observation_profile_ids':['empty'],
        'observation_producer_ids':[],'admission_seconds':10,'start_seconds':30,'collection_seconds':10,
        'cleanup_seconds':60,'stopping_seconds':5}
    require(type(request) is dict and set(request)==set(expected)|{'job_deadline_utc'}, 'request-shape')
    require(all(type(request[k]) is type(v) and request[k]==v for k,v in expected.items()), 'request-binding')
    stamp=request['job_deadline_utc']
    require(type(stamp) is str and re.fullmatch(r'[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z',stamp) is not None, 'request-deadline-shape')
    try: datetime.strptime(stamp,'%Y-%m-%dT%H:%M:%SZ')
    except ValueError: raise Rejected('request-deadline-shape')
    policy_raw=json.dumps(FIXTURE_POLICY,sort_keys=True,separators=(',',':')).encode()
    expected_checksums=(sha(request_raw)+'  /run/appsurface-evidence-fixture/'+generation+'/request.json\n'
        +sha(policy_raw)+'  '+base+'/tool/fixture-policy.json\n').encode()
    require(checksum_raw==expected_checksums, 'request-policy-binding')


def validate_n16_archive(values, parser_bytes, generation, build_sha, workflow):
    require(all(name in values for name in ('fixture-stdout.log','fixture-stderr.log','retention-selection.json','logs/n16.stdout','logs/n16.stderr','n16-record-summary.json','n16-fixture-result.json','request.json','request-policy.sha256')), 'n16-retained-required-files')
    require(values['logs/n16.stdout']==b'', 'n16-worker-stdout')
    result=decode_data(values['n16-fixture-result.json']); validate_fixture_result(result,generation,build_sha)
    require(values['fixture-stdout.log']==b'N16_SOURCE_AND_OS_OBSERVATION_TERMINAL:NOT_ACCEPTANCE\n' and values['fixture-stderr.log']==b'', 'n16-wrapper-output')
    require(type(parser_bytes) is bytes and sha(parser_bytes)==PARSER_SHA, 'parser-pin')
    parser=types.ModuleType('n16_pinned_parser'); exec(compile(parser_bytes,'<pinned-N16-record-parser>','exec',dont_inherit=True),parser.__dict__)
    summary=parser.validate_bytes(values['logs/n16.stderr'],generation,1,SOURCE,CAPTURE_SHA,build_sha)
    retained_summary=decode_data(values['n16-record-summary.json'])
    require(retained_summary==summary and summary.get('stderr_sha256')==result['stderr_sha256'] and summary.get('generation')==generation, 'n16-record-summary-binding')
    require(all(summary.get(k)==result[k] for k in ('worker_unit','worker_pid','worker_uid','worker_gid','results_gid')), 'n16-record-identities')
    validate_request_binding(values['request.json'],values['request-policy.sha256'],generation,workflow)
    return result,summary,values.get('fixture-stdout.log',b'')


def acquisition_arguments(generation, build, bootstrap, deadline_ms):
    return ['--execute','--generation',generation,'--build-root',str(build),'--reviewed-root',str(bootstrap),
        '--deadline-monotonic-ms',deadline_ms,'--reviewed-script-sha256',REVIEWED_FILES['acquire-inbound.sh'],
        '--transport-sha256',REVIEWED_FILES['prepare-root-inputs-v2.sh'],
        '--fixture-sha256',REVIEWED_FILES['checkpoint-n16-accepted-work.sh'],
        '--audit-sha256',REVIEWED_FILES['prepare-os-audit-v2.py'],'--source-review-sha256',SOURCE_REVIEW_SHA,
        '--source-capture-sha256',CAPTURE_SHA,'--n16-parser-sha256',PARSER_SHA]


def transport_arguments(generation, inbound, receipt, build_sha, deadline_ms):
    args=['--execute','--reviewed-script-sha256',REVIEWED_FILES['prepare-root-inputs-v2.sh'],
        '--generation',generation,'--deadline-monotonic-ms',deadline_ms,'--source-commit',SOURCE]
    for name in ('source','tool','runtime'):
        fact=receipt['artifacts'][name]
        args += ['--'+name+'-root',str(inbound/name),'--'+name+'-map',str(inbound/'control'/(name+'.tsv')),
            '--'+name+'-map-sha256',fact['tsv_sha256'],'--'+name+'-nodes',str(inbound/'control'/(name+'-nodes.json')),
            '--'+name+'-nodes-sha256',fact['nodes_sha256']]
    args += ['--source-review',str(inbound/'control/source-review.json'),'--source-review-sha256',SOURCE_REVIEW_SHA,
        '--build-receipt',str(inbound/'control/build-receipt.json'),'--build-receipt-sha256',build_sha,
        '--fixture',str(inbound/'scripts/checkpoint-n16-accepted-work.sh'),'--fixture-sha256',REVIEWED_FILES['checkpoint-n16-accepted-work.sh'],
        '--os-audit-script',str(inbound/'scripts/prepare-os-audit-v2.py'),'--os-audit-script-sha256',REVIEWED_FILES['prepare-os-audit-v2.py'],
        '--n16-parser-root',str(inbound/'scripts'),'--n16-parser-sha256',PARSER_SHA,
        '--source-capture',str(inbound/'control/source-capture.json'),'--source-capture-sha256',CAPTURE_SHA]
    return args


def fixture_arguments(root, receipt, build_sha, audit_sha, entry_sha, workflow):
    args=['--execute','--reviewed-script-sha256',REVIEWED_FILES['checkpoint-n16-accepted-work.sh']]
    for name,flag in (('source','source'),('tool','payload'),('runtime','runtime')):
        fact=receipt['artifacts'][name]
        args += ['--'+flag+'-root',str(root/name),'--'+flag+'-manifest',str(root/'control'/(name+'.tsv')),
            '--'+flag+'-manifest-sha256',fact['tsv_sha256'],'--'+flag+'-nodes',str(root/'control'/(name+'-nodes.json')),
            '--'+flag+'-nodes-sha256',fact['nodes_sha256']]
    args += ['--source-review',str(root/'control/source-review.json'),'--source-review-sha256',SOURCE_REVIEW_SHA,
        '--build-receipt',str(root/'control/build-receipt.json'),'--build-receipt-sha256',build_sha,
        '--os-audit',str(root/'control/os-audit.json'),'--os-audit-sha256',audit_sha,
        '--source-revision',SOURCE,'--base-revision',PARENT,'--workflow-identity',workflow,
        '--entry','ForgeTrust.AppSurface.Cli.dll','--entry-sha256',entry_sha,'--runtime-host','dotnet',
        '--source-count','2865','--source-capture',str(root/'control/source-capture.json'),
        '--source-capture-sha256',CAPTURE_SHA,'--root-projection',str(root/'control/build-receipt.json'),
        '--root-projection-sha256',build_sha,'--n16-parser-root',str(root/'reviewed'),'--n16-parser-sha256',PARSER_SHA]
    return args


def collect_private_archive(runner, bootstrap, generation, output, deadline):
    """Retain bounded diagnostic bytes; callers preserve the original fixture failure."""
    require(time.monotonic()<deadline,'retention-deadline')
    runner.phase='bounded-private-retention'
    runner.run(ROOT_PREFIX+[str(bootstrap/'retain-native.sh'),'--execute','--generation',generation,
        '--deadline-monotonic-ms',root_deadline(deadline),'--reviewed-script-sha256',REVIEWED_FILES['retain-native.sh']],deadline)
    _,before=runner.run(ROOT_PREFIX+['-c',ROOT_ARCHIVE_SHA,'--',generation],deadline)
    _,archive=runner.run(ROOT_PREFIX+['-c',READ_ROOT_ARCHIVE,'--',generation],deadline)
    _,after=runner.run(ROOT_PREFIX+['-c',ROOT_ARCHIVE_SHA,'--',generation],deadline)
    digest=sha(archive)
    require(before==after==(digest+'\n').encode(),'root-archive-digest')
    values=inspect_archive(archive)
    save(output/'native-private.tar',archive,deadline)
    return values,{'private_archive_sha256':digest,'private_archive_bytes':len(archive),
        'retention_selection':decode_data(values['retention-selection.json'])}


def run_fixture_with_retention(runner, dispatch, dispatch_end, collect, result):
    """Attempt retention after any dispatch outcome without changing a failed outcome."""
    failure=None; values=None
    try:
        code,_=runner.run(dispatch,dispatch_end,allow_failure=True)
        result['fixture_exit']=code
        require(code==0,'native-fixture-wrapper-nonzero')
    except BaseException as caught: failure=caught
    try:
        values,facts=collect()
        result.update(facts)
    except BaseException as caught:
        result['retention_failure']={'category':getattr(caught,'category','retention-operation'),
            'error_class':type(caught).__name__}
        failure=failure or caught
    if failure: raise failure
    return values


def publish_terminal(output, result, deadline):
    """Publication failure always returns failure; it cannot leave a zero exit without a receipt."""
    try:
        require(time.monotonic()<deadline,'terminal-deadline')
        save(output/'native-receipt.json',result,deadline)
        require(time.monotonic()<deadline,'terminal-deadline')
        print('NATIVE_TEST_TERMINAL:'+str(result['exit']),flush=True)
        require(time.monotonic()<deadline,'terminal-deadline')
        return result['exit']
    except BaseException:
        return 1

def parse_runner_arguments(argv):
    """Reject repeated option names before argparse can replace a reviewed value."""
    seen=set()
    for value in argv:
        if value.startswith('--'):
            name=value.split('=',1)[0]
            require(name not in seen,'duplicate-orchestrator-option')
            seen.add(name)
    ap=argparse.ArgumentParser(description=__doc__,allow_abbrev=False)
    ap.add_argument('--execute',action='store_true'); ap.add_argument('--workspace',required=True)
    ap.add_argument('--build-root',required=True); ap.add_argument('--output',required=True)
    ap.add_argument('--reviewed-script-sha256',required=True)
    return ap.parse_args(argv)


def main():
    args=parse_runner_arguments(sys.argv[1:]); start=time.monotonic(); job_end=int(os.environ['JOB_DEADLINE_MONOTONIC_NS'])/1_000_000_000; final=min(job_end,start+900)
    require(N16_SOURCE_REVIEW_CLEAR and N16_FIXTURE_REVIEW_CLEAR and N16_ROOT_CLEANUP_REVIEW_CLEAR,'n16-independent-review-pending')
    require(args.execute and sys.platform=='linux' and os.uname().machine=='x86_64' and os.geteuid()>0 and os.getegid()>0 and final-start>850,'native-platform-or-job-budget')
    workspace,build,output=map(Path,(args.workspace,args.build_root,args.output))
    require(all(x.is_absolute() for x in (workspace,build,output)) and not output.exists() and not output.is_symlink(),'absolute-paths-or-output-reuse')
    output.mkdir(mode=0o700); runner=Runner(output,final); result={'schema':'issue779-csharp-n16-native-run-v1','source_commit':SOURCE,'exit':1,'commands':runner.records,'native_dispatched':False,'native_execution':False,'native_credit':False,'native_acceptance':False,'fixture_result_verified':False}
    generation=uuid.uuid4().hex; root=Path('/var/lib/appsurface-evidence-input-'+generation); failure=None; retained=False
    try:
        require(re.fullmatch('[0-9a-f]{64}',args.reviewed_script_sha256) and sha(read(Path(__file__),131072,final))==args.reviewed_script_sha256,'orchestrator-pin')
        reviewed=workspace/'.github/issue779-csharp-native'; cap_path=workspace/'.github/issue779-csharp-build/capture-receipt.json'
        capture_raw=read(cap_path,4*1024*1024,final); require(sha(capture_raw)==CAPTURE_SHA,'capture-pin'); capture=decode(capture_raw)
        require(capture.get('schema')=='issue779-csharp-common-source-capture-v4' and capture.get('validation_commit')==SOURCE and capture.get('previous_validation_commit')==PARENT and capture.get('source_count')==2865 and capture.get('source_tree')=='3ab6be00223c9259466f1b1c191d6d2158a35e2e' and type(capture.get('source')) is dict and set(capture['source'])=={'sha256','modes'} and len(capture['source']['sha256'])==2865 and len(capture['source']['modes'])==2865 and sha(json.dumps(capture['source'],sort_keys=True,separators=(',',':')).encode())==SOURCE_MAP,'capture-identity')
        runner.phase='reviewed-inputs'
        for name,pin in REVIEWED_FILES.items():
            require(re.fullmatch('[0-9a-f]{64}',pin) is not None and sha(read(reviewed/name,131072,final))==pin,'reviewed-pin')
        sr_raw=read(reviewed/'source-review.json',65536,final); require(sha(sr_raw)==SOURCE_REVIEW_SHA,'source-review-pin'); sr=decode(sr_raw)
        expected_flags={'source_capture','fixture','root_cleanup','parser','root_transport','retention','native_handoff'}
        require(sr.get('schema')=='issue779-n16-native-source-review-v1' and sr.get('case')=='N16' and sr.get('source_commit')==SOURCE and sr.get('source_parent')==PARENT and sr.get('source_count')==2865 and sr.get('source_tree')=='3ab6be00223c9259466f1b1c191d6d2158a35e2e' and sr.get('source_capture_sha256')==CAPTURE_SHA and type(sr.get('review_flags')) is dict and set(sr['review_flags'])==expected_flags and all(value is True for value in sr['review_flags'].values()), 'n16-source-review-gates')
        raw=read(build/'receipts/build-receipt.json',1048576,final); receipt=decode(raw)
        maps={name:(read(build/f'handoff/{name}.tsv',1048576,final),read(build/f'handoff/{name}-nodes.json',4194304,final)) for name in ('source','tool','runtime')}
        validate_build(receipt,maps); result['build_receipt_sha256']=sha(raw); result['build_artifacts']=receipt['artifacts']; result['source_capture_sha256']=CAPTURE_SHA
        parser_bytes=read(reviewed/'check_n16_records.py',131072,final); require(sha(parser_bytes)==PARSER_SHA,'parser-pin')
        result['generation']=generation
        runner.phase='root-bootstrap'; bootstrap=Path('/var/lib/appsurface-evidence-bootstrap-'+generation)
        triples=[part for name,pin in REVIEWED_FILES.items() for part in (str(reviewed/name),name,pin)]
        triples += [str(cap_path),'capture-receipt.json',CAPTURE_SHA]
        runner.run(ROOT_PREFIX+['-c',BOOTSTRAP,'--',generation]+triples,time.monotonic()+30)
        runner.phase='root-acquisition-and-transport'; acquisition_end=min(final-5,time.monotonic()+120); acq_ms=root_deadline(acquisition_end)
        runner.run(ROOT_PREFIX+[str(bootstrap/'acquire-inbound.sh')]+acquisition_arguments(generation,build,bootstrap,acq_ms),acquisition_end)
        inbound=Path('/var/lib/appsurface-evidence-inbound-'+generation); build_sha=sha(raw)
        runner.run(ROOT_PREFIX+[str(bootstrap/'prepare-root-inputs-v2.sh')]+transport_arguments(generation,inbound,receipt,build_sha,acq_ms),acquisition_end)
        runner.phase='same-host-unprivileged-os-audit'
        audit_dir=audit_output_directory(generation)
        require(not os.path.lexists(audit_dir),'audit-output-reuse')
        audit_dir.mkdir(mode=0o700)
        audit_info=audit_dir.lstat()
        require(stat.S_ISDIR(audit_info.st_mode) and audit_info.st_uid==os.geteuid()
                and stat.S_IMODE(audit_info.st_mode)==0o700,'audit-output-create')
        audit_end=min(final-5,time.monotonic()+60)
        try:
            runner.run(['/usr/bin/python3','-I','-S','-B',str(root/'reviewed/prepare-os-audit-v2.py'),
                '--runtime-root',str(root/'runtime'),'--tool-root',str(root/'tool'),
                '--helper-root',str(root/'empty-audit-helper'),'--output',str(audit_dir/'os-audit.json'),
                '--deadline-monotonic',str(audit_end)],audit_end)
        except BaseException:
            # This optional closed sidecar records data only and never upgrades the failed audit.
            try:
                result['os_audit_failure_context']=retain_audit_failure_context(
                    audit_dir/'os-audit-failure-context.json',output/'os-audit-failure-context.json',audit_end)
            except BaseException: pass
            raise
        audit_raw=read(audit_dir/'os-audit.json',1048576,audit_end); audit_sha=sha(audit_raw); result['os_audit_sha256']=audit_sha
        runner.run(ROOT_PREFIX+['-c',AUDIT_COPY,'--',generation,str(audit_dir/'os-audit.json'),audit_sha],audit_end)
        runner.phase='actual-csharp-n16'
        require(final-time.monotonic()>665,'fixture-plus-retention-budget')
        tool_nodes=decode(maps['tool'][1])
        workflow='github-actions-'+os.environ['GITHUB_RUN_ID']+'-'+os.environ['GITHUB_RUN_ATTEMPT']
        fixture=fixture_arguments(root,receipt,build_sha,audit_sha,tool_nodes['files']['ForgeTrust.AppSurface.Cli.dll']['sha256'],workflow)
        result['native_dispatched']=True
        dispatch=ROOT_PREFIX+['-c',DISPATCH,'--',generation,REVIEWED_FILES['checkpoint-n16-accepted-work.sh']]+fixture
        values=run_fixture_with_retention(runner,dispatch,min(final-35,time.monotonic()+630),
            lambda:collect_private_archive(runner,bootstrap,generation,output,min(final-5,time.monotonic()+30)),result)
        fixture_result,summary,_=validate_n16_archive(values,parser_bytes,decode_data(values['n16-fixture-result.json'])['generation'],build_sha,workflow)
        result.update(fixture_generation=fixture_result['generation'],record_summary_sha256=sha(values['n16-record-summary.json']),
            fixture_result_sha256=sha(values['n16-fixture-result.json']),fixture_result_verified=True,native_execution=True)
        retained=True
    except BaseException as caught: failure=caught
    finally:
        result['failure']={'category':getattr(failure,'category','native-operation'),'error_class':type(failure).__name__} if failure else None
        result['elapsed_seconds']=time.monotonic()-start; result['exit']=0 if not failure and retained and result['fixture_result_verified'] else 1
    return publish_terminal(output,result,final-5)

if __name__=='__main__': raise SystemExit(main())
