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

NEGATIVE_CASE = 'N08'
CANCELLATION_INTEGRATION_REVIEW_CLEAR = True
CANDIDATE_ENTRY_SHA = None
cancellation_archive = None
N03_INDEPENDENT_REVIEW_CLEAR = True
N03_SOURCE_REBIND_PENDING = False
SOURCE = 'dbfc4cbda2aa7de0d5a544b47895f209138767e7'
PARENT = 'dbfc4cbda2aa7de0d5a544b47895f209138767e7'
SOURCE_MAP = 'e2a1502f1e45b831208de5199fbf2621f51b9f1f49c34ba1540e90d7c4a6b988'
PINS = {'build-helper.sh': '7bc0906967cfba542b3919e12855fb5f0e8946d0fe062a5d8371d4a5142101f4', 'Program.cs': '02a28424a2d57c56213fae8618d50df1019986cadf89246e2ce11411a23c967a', 'NativePeerBroker.csproj': '92da6c96d4c88fae754ece54af775a00c1d713c7e44b9a76bda7cc768773514e', 'packages.lock.json': 'a29c6aa8cfb81874ff8bb78dc369d7416f28c9b8cc47e99592bfc019b20c41eb', 'prepare-root-inputs-v2.sh': '453af8a1b339204f5b3f81e00f2ce89bcfa785ce7e52c0be0e4f266e98fdee2b', 'checkpoint-n01-n02-v5.sh': '0f0981cd32aa20617675961c6eab48a21be941157ccca7795f3f811d8755f8a6', 'prepare-os-audit-v2.py': 'b345b2be77b359af23ade0b334caf76d7fdeb14bd2d4a3f0837beb21232df5e0', 'source-review.json': '1119ce98f52299445aceca62492414f033be9849d400e2593a7177e0787e17ef', 'acquire-inbound.sh': '403ea9887af04b3bd645a963d4b5e937a5141d421115a825e0a9837422e5b7a9', 'retain-native.sh': 'f1e1851742b098c92c03adda7588593b9b9efb80198e928dbf68ffa918d3074a', 'check_kernel_record.py': '90da0002db90ddb8b769bd3530551f10363ce772bc99327bdd27976be71cf3ab', 'check_cancellation_records.py': 'c45238cc3bd129a19af3d04dfa12bc83a82345b130e43f7296808ecee7e5cee6', 'cancellation_adapter.py': 'e9b8a2cfe0053f93df03006c727cc1c005be32b64897ea13340fb057e294ec70', 'capture_cancellation.py': 'fc9e57334ff18d854d8819524d2ceef9e5d5e04e464234285c978a0329d9b9cf', 'parser-pins.tsv': 'b40214fe5ffbe4ada214d832592985acfbae5a44af4d9e54b845dd56cd3a0eea', 'check_cancellation_archive.py': '53dd388058231fbc995328ac8ca47cf7b7e5e971f28ceba9f638ac5a429b6145'}
ROOT_PREFIX = ['/usr/bin/sudo', '-n', '/usr/bin/env', '-i', 'PATH=/usr/bin:/usr/sbin',
               'LANG=C', 'LC_ALL=C', '/usr/bin/bash', '--noprofile', '--norc']
LOG_CAP = 40 * 1024 * 1024


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
    """Bind actual CLI restore/publish argv to the closed private N08 graph before native I/O."""
    require(type(commands) is list and 0 < len(commands) <= 64, 'private-build-command-list')
    selected = []
    for row in commands:
        require(type(row) is dict and type(row.get('argv')) is list
                and all(type(value) is str for value in row['argv']), 'private-build-command-shape')
        argv = row['argv']
        if len(argv) > 2 and argv[1] in ('restore', 'publish') and argv[2].endswith('/Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj'):
            require([value for value in argv if value.startswith('-p:EvidencePrivateQualification=')]
                    == ['-p:EvidencePrivateQualification=N08'], 'private-build-selection')
            selected.append(argv[1])
    require(sorted(selected) == ['publish', 'restore'], 'private-build-complete-graph')


def validate_build(receipt, maps):
    require(receipt.get('private_qualification') == 'N08', 'private-build-receipt-selection')
    validate_private_build_commands(receipt['commands'])
    require(receipt['schema'] == 'issue779-csharp-fdd-build-v5' and type(receipt['exit']) is int
            and receipt['exit'] == 0 and receipt.get('failure') is None and receipt['diagnostics'] == []
            and receipt['source_commit'] == SOURCE and receipt['harness_parent'] == PARENT
            and receipt['native_execution'] is False and receipt['checkpoint_pass'] is False, 'build-terminal')
    require(len(receipt['commands']) == 43 and [row['ordinal'] for row in receipt['commands']] == list(range(43)), 'build-commands')
    pipe = receipt.get('linux_pipe_regression')
    require(type(pipe) is dict and set(pipe) == {'method', 'counters', 'trx_sha256', 'unprivileged_library_behavior_only', 'root_factory_exercised', 'native_acceptance', 'runtime_basis'}, 'pipe-regression-schema')
    require(pipe['method'] == 'OwnedRawPipeReadWrappingUsesHandleModeAndJoinsBothEofs'
            and type(pipe['counters']) is dict and all(pipe['counters'].get(k) == '1' for k in ('total', 'executed', 'passed'))
            and all(pipe['counters'].get(k) == '0' for k in ('failed', 'error', 'notExecuted', 'timeout', 'aborted'))
            and type(pipe['trx_sha256']) is str and re.fullmatch(r'[0-9a-f]{64}', pipe['trx_sha256']) is not None
            and pipe['unprivileged_library_behavior_only'] is True and pipe['root_factory_exercised'] is False
            and pipe['native_acceptance'] is False and pipe['runtime_basis'] == 'selected SDK dotnet host', 'pipe-regression-terminal')
    for row in receipt['commands']:
        require(type(row['exit']) is int and row['exit'] == 0 and row['failure'] is None
                and row['waited'] is True and row['group_absent'] is True and row['timed_out'] is False
                and row['forced_cleanup'] is False, 'build-command-terminal')
    for phase in ('source_before', 'source_after_assets', 'source_after_build', 'source_final'):
        fact = receipt[phase]
        require(fact['head'] == SOURCE and fact['count'] == 2848 and fact['source_map_sha256'] == SOURCE_MAP
                and all(fact[name] is True for name in ('index_tree_matches', 'physical_git_sha1', 'physical_sha256_modes')), 'build-source')
    require(set(receipt['artifacts']) == {'source', 'tool', 'runtime'}, 'build-map-set')
    for name, fact in receipt['artifacts'].items():
        tsv, raw = maps[name]
        require(sha(tsv) == fact['tsv_sha256'] and sha(raw) == fact['nodes_sha256'], 'build-map-digest')
        nodes = decode(raw)
        require(set(nodes) == {'schema', 'root_name', 'files', 'directories'}
                and nodes['schema'] == 'issue779-build-node-inventory-v1' and nodes['root_name'] == name,
                'node-schema')
        require(len(nodes['files']) == fact['file_count'] and len(nodes['files']) + len(nodes['directories']) == fact['node_count']
                and sum(row['bytes'] for row in nodes['files'].values()) == fact['total_bytes'], 'node-counts')
    require(receipt['artifacts']['source']['file_count'] == 2848, 'source-count')


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
 [[ $(/usr/bin/stat -c %h -- "$source") == 1 && $(/usr/bin/stat -c %s -- "$source") -le 131072 ]]
 diagnostic_stage=source-digest
 printf '%s  %s\n' "$hash" "$source" | /usr/bin/sha256sum --check --strict --status
 diagnostic_stage=copy
 /usr/bin/dd if="$source" of="$target/$name" iflag=nofollow,nonblock,count_bytes oflag=nofollow conv=fsync,excl count=131073 status=none
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
[[ $(/usr/bin/stat -c '%u:%g:%a' -- "$root/control") == 0:0:700 ]]
/usr/bin/jq -ncS --arg g "$g" '{schema:"issue779-native-dispatch-marker-v1",generation:$g,fixture_parent_absent:true}' >"$root/control/fixture-dispatch-marker.json"
printf '%s  %s\n' "$hash" "$root/reviewed/checkpoint-n01-n02-v5.sh" | /usr/bin/sha256sum --check --strict --status
capture_fixture "$root/control/fixture-stdout.log" "$root/control/fixture-stderr.log" /usr/bin/bash --noprofile --norc "$root/reviewed/checkpoint-n01-n02-v5.sh" "$@"
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

ARCHIVE_NAMES = {'logs/n02-higher.file-limit', 'logs/n03/worker.stderr', 'negative-setup.json', 'logs/n02.credentials.json', 'logs/n03/worker-live.json', 'logs/n02-higher.stderr', 'logs/n03/result.json', 'worker-live.json', 'logs/n03/broker.stderr', 'logs/n02.stderr', 'logs/n03/worker.stdout', 'fixture-stderr.log', 'negative-kernel-observation.json', 'n05-worker.stderr', 'logs/kill.log', 'final-files.sha256', 'fixture-result.json', 'logs/observer.stdout', 'negative-post-join.json', 'fixture-stdout.log', 'logs/n02-higher.credentials.json', 'logs/n03/broker-live.json.argv', 'logs/n03/worker-peer.trace', 'n02-startup-limit-diagnostic.pending', 'negative-root-terminal.txt', 'logs/n03/broker.events.jsonl', 'logs/n03/failure.json', 'raw-evidence-plan.json', 'logs/n02-higher.io.trace', 'logs/n02.stdout', 'n02-startup-limit-diagnostic.json', 'raw-evidence-manifest.json', 'n05-worker.stdout', 'negative-account-ids.tsv', 'logs/n03/broker-live.json', 'logs/n01.stderr', 'logs/n02.io.trace', 'logs/n02-higher.limits', 'logs/n02.facts.json', 'logs/n03/broker-live-after.json', 'negative-root-failure.json', 'logs/n02-higher.trace', 'raw-evidence-summary.json', 'request-policy.sha256', 'logs/n03/broker-live-after.json.argv', 'logs/n02-higher.facts.json', 'logs/n02.trace', 'logs/observer.stderr', 'logs/n03/worker-live.json.argv', 'logs/n02.file-limit', 'negative-descriptor.json', 'negative-worker-raw.json', 'negative-worker-projection.json', 'logs/n02-higher.stdout', 'n06-worker.stderr', 'logs/stop.log', 'retention-selection.json', 'logs/n01.stdout', 'n06-worker.stdout', 'logs/n02.limits'}



"""Closed fixture data checks only. Never authenticate native actors or grant acceptance."""
import base64
import hashlib
import io
import json
import re
import tarfile

# Replaced by the renderer from the exact frozen a358 C# enum declarations.
ENUMS = {'EvidenceControlOperation': ['Ready', 'Stop', 'Wait', 'Exit', 'Run', 'Artifacts', 'Artifact', 'ApplicationStart', 'ResourceWait'], 'EvidenceNativeObservationErrorKind': ['Unknown', 'Admission', 'Accounts', 'OutputPipe', 'ControlLine', 'Cancelled', 'Timeout', 'Io', 'AccessDenied', 'Unsupported', 'Disposed', 'Argument', 'InvalidData', 'InvalidOperation'], 'EvidenceNativeObservationPhase': ['Unknown', 'CallerCancellation', 'ProtectedInput', 'JobDeadline', 'BackendConnect', 'OwnerActivation', 'Plan', 'AccountCreate', 'WorkspaceCreate', 'ListenerBind', 'WorkerCreate', 'WorkerStart', 'ServerCreate', 'ServerLifetime', 'ServerRun', 'ServerCompletion', 'WorkerExit', 'WorkerStop', 'WorkerCompletion', 'BeginTeardown', 'Custody', 'FileVerification', 'CleanupBegin', 'ServerCancel', 'ListenerClose', 'ServerJoin', 'WorkerJoin', 'CleanupCustody', 'AccountsClose', 'WorkerClose', 'CustodyClose', 'WorkspaceClose', 'OwnerFinalCheck', 'ServerLifetimeClose', 'JobClose', 'OwnerClose', 'InputClose', 'BackendClose', 'FinalDeadline', 'ResultCheck'], 'LinuxAccountPreparationStage': ['Unknown', 'NamesAbsent', 'ReserveUtility', 'UtilityCreate', 'UtilityExecute', 'IdentityRead', 'OwnershipVerify', 'CleanupCheck', 'CleanupNameCheck', 'CleanupUtility', 'FinalNamesAbsent', 'FinalOwnershipCheck'], 'LinuxAccountUtilityStage': ['Unknown', 'OwnerCheck', 'Pipes', 'BackendConnect', 'Recipe', 'Start', 'CloseWrites', 'UnitRead', 'TerminalCheck', 'ObservationDelay', 'BeginTeardown', 'Stop', 'GroupRead', 'OutputJoin', 'PipeDispose', 'BackendDispose', 'PhysicalSettlement', 'FinalOwnerCheck'], 'LinuxControlFailureStage': ['Unknown', 'PeerCheck', 'WorkerExitTask', 'RequestLifetime', 'AcceptLoop', 'HandlerJoin', 'CapacityWait', 'Accept', 'AcceptJoin', 'ControlRegistration', 'HandlerDispatch', 'RequestRead', 'RequestClassify', 'CleanupRegistration', 'Stop', 'WaitJoin', 'ReplyGate', 'ReadyAuthorization', 'ReadyClaim', 'ReadyData', 'ResponseData', 'WaitClaim', 'ExitClaim', 'ResponseWrite', 'ConnectionRelease', 'PostWriteCheck', 'ReplyCommit', 'HandlerFailureCommit', 'ReplyGateRelease', 'ControlRelease', 'CleanupRegistrationClose', 'ExitCommit', 'AcceptCancel', 'ListenerClose', 'PendingAcceptJoin', 'HandlersJoin', 'DescendantsStop', 'ControlsJoin', 'FinalCancellation', 'CleanupBound', 'OwnerCheck', 'ProtocolIncomplete', 'WorkerTerminalTaskCompleted', 'ReplyGateClose', 'ListenerState', 'ListenerCancellation', 'ListenerWorkspace', 'ListenerParent', 'ListenerSocketMetadata', 'ListenerSocketName', 'ListenerEndpoint', 'ListenerWorkerSelection', 'ListenerOwnerIdentity', 'ListenerWorkerIdentity', 'ListenerDescriptor', 'ListenerNativeAccept', 'ListenerAcceptedPeer', 'ProcessState', 'ProcessSelection', 'ProcessExpectedSample', 'ProcessExpectedPid', 'ProcessExpectedStartTime', 'ProcessExpectedLiveState', 'ProcessExpectedUid', 'ProcessExpectedGid', 'ProcessExpectedCgroup', 'ProcessInitialContinuity', 'ProcessRepeatedContinuity', 'ProcessReadContinuity', 'ProcessRetainedProcRoot', 'ProcessRetainedProcess', 'ProcessRetainedStatus', 'ProcessRetainedStat', 'ProcessRetainedCgroup', 'ProcessNamedProcRoot', 'ProcessNamedProcess', 'ProcessNamedStatus', 'ProcessNamedStat', 'ProcessNamedCgroup', 'ProcessFirstStatRead', 'ProcessFirstStatParse', 'ProcessStatusRead', 'ProcessStatusParse', 'ProcessCgroupRead', 'ProcessCgroupParse', 'ProcessLastStatRead', 'ProcessLastStatParse', 'ProcessFileSystemInspect', 'ProcessFileSystemType', 'ProcessDirectoryStat', 'ProcessDirectoryInode', 'ProcessDirectoryType', 'ProcessRetainedProcessDeviceMajor', 'ProcessRetainedProcessDeviceMinor', 'ProcessRetainedProcessInode', 'ProcessRetainedProcessUid', 'ProcessRetainedProcessGid', 'ProcessRetainedProcessMode', 'ProcessRetainedProcessMetadata', 'ListenerAdmissionDrain', 'ListenerSocketClose', 'ListenerNamedSocketClose', 'ListenerParentClose', 'ListenerNativeAcceptOperationAborted', 'ListenerNativeAcceptInterrupted', 'ListenerNativeAcceptConnectionAborted', 'ListenerNativeAcceptSocketOther', 'ListenerAcceptedClose'], 'LinuxCustodyNodeKind': ['Generation', 'Control', 'Broker', 'Output', 'RawResults', 'Slot', 'Descriptor', 'Socket', 'Plan', 'Manifest', 'Summary'], 'LinuxCustodyOperation': ['Unknown', 'Platform', 'Settlement', 'AccountOwner', 'NodeSelection', 'Open', 'RetainedStat', 'AncestorPolicy', 'OriginalPolicy', 'BaselineComparison', 'NamedOpen', 'NamedStat', 'NamedComparison', 'InventoryRead', 'InventoryDecode', 'InventoryPolicy', 'InventoryComparison', 'HashRead', 'HashEof', 'HashFinalize', 'HashComparison', 'Chown', 'Chmod', 'TerminalPolicy', 'OriginalOwnerClose', 'FileRead', 'FileVerification', 'AccountRelease', 'RootRecheck', 'Cancellation', 'HolderState', 'OwnerIdentity'], 'LinuxRunAccountFailure': ['InvalidData', 'IdentityMismatch', 'NssFailed', 'UnsupportedPlatform', 'OperationFailed', 'CleanupFailed'], 'LinuxRunAccountOperation': ['CreateUser', 'CreateResultsGroup', 'DeleteUser', 'DeleteGroup'], 'LinuxSystemdStartError': ['Other', 'AccessDenied', 'InvalidArgs', 'NoReply', 'ServiceUnknown', 'UnknownMethod', 'UnitExists', 'LoadFailed', 'NoSuchUnit'], 'SupervisionCustodyFailure': ['None', 'Cancelled', 'SettlementValidationFailed', 'PreflightFailed', 'MutationFailed', 'LocalCloseFailed', 'FinalNativeRecheckFailed']}
CODES = frozenset(('ASEVD402','ASEVD404','ASEVD407','ASEVD409','ASEVD410','ASEVD420','ASEVD421'))
ROOT_TERMINAL = b'ASEVD410: The protected empty Observation execution or final cleanup could not be established. Fix: use an explicit mode and supported protected worker. See start-here/evidencehost.md.\n'
WORKER_TERMINAL = b'ASEVD409: Fresh output allocation or activation failed. Fix: use an explicit mode and supported protected worker. See start-here/evidencehost.md.\n'
NEGATIVE_CAPS = {
    'negative-setup.json':1024, 'negative-post-join.json':1024,
    'negative-worker-projection.json':1024, 'negative-kernel-observation.json':4097,
    'negative-worker-raw.json':4097, 'negative-root-failure.json':1024,
    'negative-root-terminal.txt':1024, 'negative-descriptor.json':65536,
    'negative-account-ids.tsv':128,
    'n05-worker.stdout':0, 'n05-worker.stderr':2048,
    'n06-worker.stdout':0, 'n06-worker.stderr':2048,
}

ARCHIVE_NAMES = ARCHIVE_NAMES | set(['cancellation-root-join.json', 'root-cleanup.json', 'cancellation-capture.json', 'filesystem-nss-observation.json', 'joined-streams.json', 'kernel.json', 'logs/n01.stderr', 'logs/n01.stdout', 'root-failure.json', 'signal.json', 'worker-control.json', 'worker.stderr', 'worker.stdout'])
NEGATIVE_CAPS.update({'cancellation-root-join.json': 1024, 'root-cleanup.json': 1024, 'signal.json': 1024, 'kernel.json': 4097, 'joined-streams.json': 98304, 'root-failure.json': 1024, 'worker.stdout': 0, 'worker.stderr': 65536, 'worker-control.json': 65536, 'filesystem-nss-observation.json': 8192, 'cancellation-capture.json': 8192, 'logs/n01.stdout': 0, 'logs/n01.stderr': 114688})

def require_data(value):
    if not value: raise ValueError('negative-fixture-data-rejected')

def unique_pairs(items):
    result={}; folded=set()
    for key,value in items:
        require_data(key.casefold() not in folded)
        folded.add(key.casefold()); result[key]=value
    return result

def decode_data(raw):
    return json.loads(raw.decode('utf-8'),object_pairs_hook=unique_pairs,
                      parse_constant=lambda _:require_data(False))

def exact_object(value,keys):
    require_data(type(value) is dict and set(value)==set(keys))

def enum_data(value,name,nullable=False):
    require_data(value is None and nullable or type(value) is str and value in ENUMS[name])

def code_data(value):
    require_data(value is None or type(value) is str and value in CODES)

def family(value):
    enum_data(value['error_kind'],'EvidenceNativeObservationErrorKind')
    code_data(value['diagnostic_code'])

def validate_failure(value):
    """Exact v4 serializer member sets and finite values, including null projections."""
    exact_object(value,('schema','phase','error_kind','diagnostic_code','account_failure','control_failure','custody_failure'))
    require_data(value['schema']=='evidence-native-observation-failure-v4')
    enum_data(value['phase'],'EvidenceNativeObservationPhase'); family(value)
    account=value['account_failure']
    if account is not None:
        exact_object(account,('preparation_stage','utility_stage','operation','error_kind','diagnostic_code','account_code','exec_main_code','exec_main_status','dbus_category'))
        enum_data(account['preparation_stage'],'LinuxAccountPreparationStage')
        enum_data(account['utility_stage'],'LinuxAccountUtilityStage')
        enum_data(account['operation'],'LinuxRunAccountOperation',True);family(account)
        enum_data(account['account_code'],'LinuxRunAccountFailure',True)
        enum_data(account['dbus_category'],'LinuxSystemdStartError',True)
        code,status=account['exec_main_code'],account['exec_main_status']
        require_data(code is None and status is None or type(code) is int and 1<=code<=6 and type(status) is int and 0<=status<=255)
    control=value['control_failure']
    if control is not None:
        require_data(value['phase'] in ('ServerRun','ServerCompletion'))
        exact_object(control,('stage','operation','error_kind','diagnostic_code'))
        enum_data(control['stage'],'LinuxControlFailureStage')
        enum_data(control['operation'],'EvidenceControlOperation',True);family(control)
    custody=value['custody_failure']
    if custody is not None:
        require_data(value['phase'] in ('Custody','CleanupCustody','FileVerification','AccountsClose'))
        exact_object(custody,('procedure','node_kind','operation','error_kind','diagnostic_code'))
        enum_data(custody['procedure'],'SupervisionCustodyFailure')
        enum_data(custody['node_kind'],'LinuxCustodyNodeKind',True)
        enum_data(custody['operation'],'LinuxCustodyOperation');family(custody)
    return value

def validate_fixture_frames(raw,case):
    """Seven exact LF frames, real producer raw bytes; returns detached data only."""
    require_data(case in ('N05','N06') and type(raw) is bytes and 0<len(raw)<=14336)
    require_data(raw.endswith(b'\n') and b'\r' not in raw)
    lines=raw.splitlines(keepends=True)
    require_data(len(lines)==7 and all(x.endswith(b'\n') for x in lines))
    require_data(all(0<len(x)<=n for x,n in zip(lines,(1024,1024,1024,4097,4097,1024,1024))))
    require_data(lines[6]==ROOT_TERMINAL)
    setup,after,projection,kernel,worker,failure=[decode_data(x) for x in lines[:6]]
    schema='issue779-n05-slot-inspection-v1' if case=='N05' else 'issue779-n06-symlink-inspection-v1'
    keys=('schema','phase','parent','slot','sentinel','sha256') if case=='N05' else ('schema','phase','parent','target','link','target_sha256','sentinel','sha256')
    exact_object(setup,keys);exact_object(after,keys)
    require_data(setup['schema']==after['schema']==schema and setup['phase']=='setup' and after['phase']=='post_join')
    require_data({k:v for k,v in setup.items() if k!='phase'}=={k:v for k,v in after.items() if k!='phase'})
    exact_object(projection,('schema','origin','allocation','terminal_diagnostic','stdout_bytes','stderr_bytes','native_authority'))
    require_data(projection['schema']=='issue779-'+case.lower()+'-joined-worker-output-v1'
                 and projection['origin']=='joined-worker-output' and projection['terminal_diagnostic']=='ASEVD409'
                 and type(projection['stdout_bytes']) is int and projection['stdout_bytes']==0
                 and type(projection['stderr_bytes']) is int and 0<projection['stderr_bytes']<=2048
                 and projection['native_authority'] is False)
    alloc=projection['allocation'];exact_object(alloc,('schema','phase','operation','stageOutcome','terminalCode','errorClass','nativeErrno'))
    require_data(alloc['schema']=='evidence-allocation-failure-v1' and alloc['phase']=='Allocation'
                 and alloc['operation']=='CreateSlot' and alloc['stageOutcome']=='Failed'
                 and alloc['terminalCode']=='StageFailed' and alloc['errorClass']=='Io'
                 and (alloc['nativeErrno'] is None or type(alloc['nativeErrno']) is int and 1<=alloc['nativeErrno']<=4095))
    exact_object(worker,('schema','stdout_base64','stderr_base64','stdout_bytes','stderr_bytes','native_authority'))
    require_data(worker['schema']=='issue779-'+case.lower()+'-joined-worker-raw-v1' and worker['native_authority'] is False
                 and worker['stdout_base64']=='' and type(worker['stdout_bytes']) is int and worker['stdout_bytes']==0
                 and type(worker['stderr_bytes']) is int and 0<worker['stderr_bytes']<=2048
                 and type(worker['stderr_base64']) is str)
    stdout=base64.b64decode(worker['stdout_base64'],validate=True)
    stderr=base64.b64decode(worker['stderr_base64'],validate=True)
    require_data(base64.b64encode(stdout).decode('ascii')==worker['stdout_base64']
                 and base64.b64encode(stderr).decode('ascii')==worker['stderr_base64']
                 and stdout==b'' and len(stderr)==worker['stderr_bytes']==projection['stderr_bytes'])
    worker_lines=stderr.splitlines(keepends=True)
    require_data(len(worker_lines)==2 and worker_lines[1]==WORKER_TERMINAL and decode_data(worker_lines[0])==alloc)
    validate_failure(failure)
    return {'lines':lines,'kernel':kernel,'stdout':stdout,'stderr':stderr,'failure':failure}

def inspect_retention(data,allowed):
    """Canonical bounded USTAR whitelist inspection only, not a native result verifier."""
    require_data(type(data) is bytes and 0<len(data)<=33619968)
    names=[];total=0;values={};entries=[]
    with tarfile.open(fileobj=io.BytesIO(data),mode='r:') as archive:
        for member in archive:
            require_data(member.name in allowed and member.name not in names and member.isfile()
                         and member.mode==0o600 and member.uid==member.gid==member.mtime==0
                         and not member.linkname and not member.pax_headers and not member.uname and not member.gname
                         and 0<=member.size<=NEGATIVE_CAPS.get(member.name,4096 if member.name in ('fixture-result.json','retention-selection.json','logs/n03/result.json','logs/n03/worker-live.json','logs/n03/broker-live.json','logs/n03/broker-live-after.json') else 8388608))
            names.append(member.name);total+=member.size;require_data(total<=33554432+4096 and len(names)<=len(allowed))
            stream=archive.extractfile(member);require_data(stream is not None)
            with stream: content=stream.read(member.size+1)
            require_data(len(content)==member.size);values[member.name]=content;entries.append((member.name,content))
    require_data(names==sorted(names) and 'retention-selection.json' in values)
    selection=decode_data(values['retention-selection.json'])
    exact_object(selection,('schema','selection_status','data_file_count','data_bytes','missing_fixed_file_count'))
    require_data(selection['schema']=='issue779-native-retention-selection-v1'
                 and selection['selection_status'] in ('parent-absent','parent-empty','one-namespace')
                 and type(selection['data_file_count']) is int and selection['data_file_count']==len(names)-1
                 and type(selection['data_bytes']) is int and selection['data_bytes']==total-len(values['retention-selection.json'])
                 and type(selection['missing_fixed_file_count']) is int and selection['missing_fixed_file_count']>=0)
    canonical=io.BytesIO()
    with tarfile.open(fileobj=canonical,mode='w:',format=tarfile.USTAR_FORMAT) as rebuilt:
        for name,content in entries:
            info=tarfile.TarInfo(name);info.size=len(content);info.mode=0o600;info.uid=info.gid=info.mtime=0
            rebuilt.addfile(info,io.BytesIO(content))
    require_data(canonical.getvalue()==data)
    return values


"""Strict detached negative-record consistency checks; no OS access or native authority."""
import json
import re

MAX_JSON_BYTES = 4096
MAX_STREAM_BYTES = 1024 * 1024
MAX_RECEIVED_LIMIT = 16 * 1024 * 1024
UINT32_MAX = (1 << 32) - 1
UINT64_MAX = (1 << 64) - 1
TOP = frozenset(('schema', 'generation', 'worker_unit', 'process', 'ready', 'terminal',
                 'cgroup', 'pumps', 'joins', 'observation_only', 'native_authority', 'native_acceptance'))


class KernelRecordRejected(ValueError):
    """Closed data rejection, deliberately retaining no supplied values or cause text."""


def _reject():
    raise KernelRecordRejected('negative-kernel-data-rejected')


def _require(value):
    if not value:
        _reject()


def _uint(value, low, high):
    _require(type(value) is int and low <= value <= high)


def _digest(value):
    _require(type(value) is str and re.fullmatch('[0-9a-f]{64}', value) is not None)


def _object(value, keys):
    _require(type(value) is dict and set(value) == set(keys))


def _pairs(pairs):
    result = {}
    folded = set()
    for key, value in pairs:
        canonical = key.casefold()
        _require(canonical not in folded)
        folded.add(canonical)
        result[key] = value
    return result


def _stream(value, expected_sha256, expected_bytes):
    _object(value, ('received_bytes', 'retained_bytes', 'discarded_bytes', 'eof', 'failure', 'sha256'))
    for key in ('received_bytes', 'retained_bytes'):
        _uint(value[key], 0, MAX_STREAM_BYTES)
        _require(value[key] == expected_bytes)
    _uint(value['discarded_bytes'], 0, 0)
    _require(value['eof'] is True and value['failure'] == 'None')
    _digest(value['sha256'])
    _require(value['sha256'] == expected_sha256)


def check_kernel_record(raw, *, expected_generation, expected_uid, expected_gid,
                        expected_pid, expected_starttime_ticks, expected_descriptor_sha256,
                        expected_stdout_sha256, expected_stdout_bytes,
                        expected_stderr_sha256, expected_stderr_bytes):
    """Return True for detached data consistency only; never issue admission or acceptance.

    ``raw`` is exactly a JSON object of at most 4096 UTF-8 bytes, optionally followed
    by one LF. Expected values are caller data, not authenticated identities. The
    original source-built holder and root-private stream capture must independently
    establish provenance. No path, command, timer, descriptor or mutable lease is
    consumed or created. Every rejection has one fixed message and no chained cause.
    Both stream hashes/counts must come from full captured bytes outside this parser.
    The negative-case terminal is fixed CLD_EXITED(1), status 1; zero and signals reject.
    """
    try:
        _require(type(raw) is bytes and 0 < len(raw) <= MAX_JSON_BYTES + 1)
        body = raw[:-1] if raw.endswith(b'\n') else raw
        _require(0 < len(body) <= MAX_JSON_BYTES and body.startswith(b'{') and body.endswith(b'}'))
        _require(type(expected_generation) is str
                 and re.fullmatch('[0-9a-f]{32}', expected_generation) is not None
                 and expected_generation != '0' * 32)
        _uint(expected_uid, 1, UINT32_MAX - 1)
        _uint(expected_gid, 1, UINT32_MAX - 1)
        _uint(expected_pid, 1, (1 << 31) - 1)
        _uint(expected_starttime_ticks, 1, UINT64_MAX)
        for value in (expected_descriptor_sha256, expected_stdout_sha256, expected_stderr_sha256):
            _digest(value)
        _uint(expected_stdout_bytes, 0, MAX_STREAM_BYTES)
        _uint(expected_stderr_bytes, 0, MAX_STREAM_BYTES)
        value = json.loads(body.decode('utf-8', errors='strict'), object_pairs_hook=_pairs,
                           parse_constant=lambda _: _reject())
        _object(value, TOP)
        _require(value['schema'] == 'issue779-negative-kernel-observation-v1')
        _require(value['generation'] == expected_generation)
        unit = 'appsurface-evidence-worker-' + expected_generation + '.service'
        group = '/system.slice/' + unit
        _require(value['worker_unit'] == unit)
        process = value['process']
        _object(process, ('pid', 'starttime_ticks', 'uid4', 'gid4', 'control_group'))
        _uint(process['pid'], 1, (1 << 31) - 1)
        _uint(process['starttime_ticks'], 1, UINT64_MAX)
        _require(process['pid'] == expected_pid and process['starttime_ticks'] == expected_starttime_ticks)
        for name, expected in (('uid4', expected_uid), ('gid4', expected_gid)):
            ids = process[name]
            _require(type(ids) is list and len(ids) == 4)
            for identity in ids:
                _uint(identity, 1, UINT32_MAX - 1)
                _require(identity == expected)
        _require(process['control_group'] == group)
        ready = value['ready']
        _object(ready, ('committed', 'descriptor_sha256'))
        _require(ready['committed'] is True)
        _digest(ready['descriptor_sha256'])
        _require(ready['descriptor_sha256'] == expected_descriptor_sha256)
        terminal = value['terminal']
        _object(terminal, ('exec_main_pid', 'exec_main_code', 'exec_main_status', 'active_state', 'sub_state'))
        _uint(terminal['exec_main_pid'], 1, (1 << 31) - 1)
        _uint(terminal['exec_main_code'], 1, 1)
        _uint(terminal['exec_main_status'], 1, 1)
        _require(terminal['exec_main_pid'] == expected_pid)
        _require((terminal['active_state'], terminal['sub_state']) in
                 (('active', 'exited'), ('inactive', 'dead'), ('failed', 'failed')))
        cg = value['cgroup']
        _object(cg, ('exists', 'populated', 'frozen', 'device_major', 'device_minor', 'inode'))
        _require(type(cg['exists']) is bool)
        if cg['exists']:
            _require(cg['populated'] is False and cg['frozen'] is False)
            _uint(cg['device_major'], 0, UINT32_MAX)
            _uint(cg['device_minor'], 0, UINT32_MAX)
            _uint(cg['inode'], 1, UINT64_MAX)
        else:
            _require(all(cg[k] is None for k in ('populated', 'frozen', 'device_major', 'device_minor', 'inode')))
        pumps = value['pumps']
        _object(pumps, ('stdout', 'stderr', 'received_bytes', 'received_byte_limit', 'failure', 'discarded_bytes'))
        _stream(pumps['stdout'], expected_stdout_sha256, expected_stdout_bytes)
        _stream(pumps['stderr'], expected_stderr_sha256, expected_stderr_bytes)
        _uint(pumps['received_bytes'], 0, MAX_RECEIVED_LIMIT)
        _uint(pumps['received_byte_limit'], 1, MAX_RECEIVED_LIMIT)
        _uint(pumps['discarded_bytes'], 0, 0)
        _require(pumps['received_bytes'] == expected_stdout_bytes + expected_stderr_bytes
                 and pumps['received_bytes'] <= pumps['received_byte_limit'] and pumps['failure'] == 'None')
        _object(value['joins'], ('startup', 'pending_stop', 'monitor', 'server', 'pumps'))
        _require(all(x is True for x in value['joins'].values()))
        _require(value['observation_only'] is True and value['native_authority'] is False
                 and value['native_acceptance'] is False)
        return True
    except (ValueError, TypeError, KeyError, OverflowError, RecursionError, UnicodeError):
        raise KernelRecordRejected('negative-kernel-data-rejected') from None


def inspect_archive(data):
    """Inspect bounded root-retained canonical bytes; raw data remains private."""
    raw = inspect_retention(data, ARCHIVE_NAMES)
    selected = ('fixture-result.json', 'retention-selection.json', 'logs/n03/result.json',
                'logs/n03/worker-live.json', 'logs/n03/broker-live.json', 'logs/n03/broker-live-after.json')
    require_data(all(len(raw[name]) <= 4096 for name in selected if name in raw))
    values = {name: decode_data(raw[name]) for name in selected if name in raw}
    if 'logs/n03/result.json' in raw:
        values['n03-result-sha256'] = sha(raw['logs/n03/result.json'])
    values['_retained_raw'] = raw
    return values


def validate_native_result(value):
    """Closed fixture result; the entire retained archive must also be verified."""
    require(type(value) is dict and value.get('schema') == 'issue779-cancellation-fixture-v1'
            and value.get('source_revision') == SOURCE and value.get('case') == NEGATIVE_CASE,
            'cancellation-fixture-identity')


def validate_negative_archive(values):
    """Data binding only, after separately checked original native terminal."""
    return cancellation_archive.check_archive(values['_retained_raw'], case=NEGATIVE_CASE,
        source=SOURCE, base='4dd992ec1bc2df8220c73149115c5b478edb0085',
        entry_sha=CANDIDATE_ENTRY_SHA)


def validate_helper_handoff(value, recipe):
    fields = {'authority','commands','exit','helper_bytes','helper_directories','helper_entry_sha256','helper_files','helper_nodes_sha256','helper_root','helper_tsv_sha256','native_execution','recipe_sha256','runtime_required','schema','sdk_required','sdk_sha256','source_pins'}
    require(set(value) == fields and value['schema'] == 'issue779-n03-helper-build-handoff-v1' and type(value['exit']) is int and value['exit'] == 0 and value['authority'] is False and value['native_execution'] is False and value['recipe_sha256'] == recipe and value['sdk_required'] == '10.0.401' and value['runtime_required'] == '10.0.12', 'helper-receipt-schema')
    require(value['source_pins'] == {k:PINS[k] for k in ('Program.cs','NativePeerBroker.csproj','packages.lock.json')}, 'helper-source-pins')
    require(all(type(value[k]) is int and value[k]>0 for k in ('helper_files','helper_directories')) and value['helper_files']+value['helper_directories']<=8192 and type(value['helper_bytes']) is int and 0<=value['helper_bytes']<=1073741824, 'helper-bounds')
    require(all(type(value[k]) is str and re.fullmatch('[0-9a-f]{64}',value[k]) for k in ('helper_tsv_sha256','helper_nodes_sha256','helper_entry_sha256','sdk_sha256')), 'helper-digests')
    require(type(value['commands']) is list and len(value['commands'])==3, 'helper-command-count')
    for i,row in enumerate(value['commands']):
        require(set(row)=={'error','exit','forced_cleanup','group_absent','log','log_bytes','waited'} and type(row['exit']) is int and row['exit']==0 and row['waited'] is True and row['group_absent'] is True and row['forced_cleanup'] is False and row['error'] is False and row['log']=='build-%02d.log'%i and type(row['log_bytes']) is int and 0<=row['log_bytes']<=8388608, 'helper-command')

"""Closed data validation candidate. It issues no admission, lease or native proof."""

KEYS = {"asevd402_observed", "broker_exit", "broker_pid", "control", "descriptor_supplied", "kernel_peer_bound_to_owned_worker", "native_acceptance", "owned_process_groups_joined", "ready_request_observed", "request_bytes", "schema", "sent_bytes", "source_commit", "status", "stream_custody", "systemd_worker_authority", "worker_exit", "worker_pid", "worker_stdout_bytes"}

def require_n03_result(raw, source):
    def reject():
        raise ValueError("n03-closed-data-rejected")
    def unique(rows):
        result = {}
        for key, value in rows:
            if key in result: reject()
            result[key] = value
        return result
    def invalid_constant(value):
        reject()
    if not isinstance(raw, bytes) or not 0 < len(raw) <= 4096 or raw[-1:] != b"\n": reject()
    value = json.loads(raw, object_pairs_hook=unique, parse_constant=invalid_constant)
    if not isinstance(value, dict) or set(value) != KEYS: reject()
    expected = {"schema":"issue779-n03-peer-observation-v1", "control":"N03", "status":"observed", "source_commit":source, "worker_exit":1, "broker_exit":0, "worker_stdout_bytes":0, "asevd402_observed":True, "kernel_peer_bound_to_owned_worker":True, "request_bytes":0, "sent_bytes":0, "ready_request_observed":False, "descriptor_supplied":False, "owned_process_groups_joined":True, "native_acceptance":False, "systemd_worker_authority":False, "stream_custody":"private-files-after-original-process-reap"}
    if any(type(value[k]) is not type(v) or value[k] != v for k,v in expected.items()): reject()
    if any(type(value[k]) is not int or not 0 < value[k] <= 2147483647 for k in ("worker_pid", "broker_pid")): reject()
    if value["worker_pid"] == value["broker_pid"]: reject()
    # Caller must independently require fixture numeric0, exact three case receipt,
    # authenticated archive/custody, raw peer trace and live identity bindings.
    return value


def validate_n03_archive(values):
    require('logs/n03/result.json' in values, 'n03-result-missing')
    result = values['logs/n03/result.json']
    require_n03_result((json.dumps(result,separators=(',',':'))+'\n').encode(), SOURCE)
    require(values['fixture-result.json']['n03_result_sha256']==values['n03-result-sha256'], 'n03-result-binding')
    fields={'schema','pid','start_ticks','uid4','gid4','cgroup','exact_managed_argv','image_pinned','empty_groups','no_new_privs','effective_capabilities_zero'}
    for name,pid in [('worker-live.json',result['worker_pid']),('broker-live.json',result['broker_pid']),('broker-live-after.json',result['broker_pid'])]:
        row=values.get('logs/n03/'+name)
        require(type(row) is dict and set(row)==fields and row['schema']=='issue779-n03-owned-image-sample-v1' and type(row['pid']) is int and row['pid']==pid and type(row['start_ticks']) is str and re.fullmatch('[1-9][0-9]*',row['start_ticks']) and row['exact_managed_argv'] is True and row['image_pinned'] is True and row['empty_groups'] is True and type(row['no_new_privs']) is int and row['no_new_privs']==1 and row['effective_capabilities_zero'] is True, 'n03-live-binding')
        for field in ('uid4','gid4'):
            parts=row[field].split(':');require(len(parts)==4 and all(re.fullmatch('[1-9][0-9]*',x) for x in parts) and len(set(parts))==1,'n03-credentials')
        require(re.fullmatch(r'0::/system.slice/issue779-native-tool-[0-9a-f]{32}-[0-9]+\.service',row['cgroup']), 'n03-cgroup')
    require(values['logs/n03/broker-live.json']==values['logs/n03/broker-live-after.json'], 'n03-broker-continuity')
    worker=values['logs/n03/worker-live.json'];broker=values['logs/n03/broker-live.json']
    require(worker['uid4']!=broker['uid4'] and worker['gid4']==broker['gid4']=='65534:65534:65534:65534' and worker['cgroup']==broker['cgroup'], 'n03-selection-continuity')


def main():
    global CANDIDATE_ENTRY_SHA, cancellation_archive
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--execute', action='store_true')
    parser.add_argument('--workspace', required=True)
    parser.add_argument('--build-root', required=True)
    parser.add_argument('--helper-build-root', required=True)
    parser.add_argument('--output', required=True)
    parser.add_argument('--reviewed-script-sha256', required=True)
    args = parser.parse_args()
    job_end = int(os.environ['JOB_DEADLINE_MONOTONIC_NS']) / 1_000_000_000
    start = time.monotonic()
    final = min(job_end, start + 900)
    require(CANCELLATION_INTEGRATION_REVIEW_CLEAR, 'cancellation-integration-review-pending')
    require(N03_INDEPENDENT_REVIEW_CLEAR and not N03_SOURCE_REBIND_PENDING, 'n03-review-or-source-rebind-pending')
    require(args.execute and sys.platform == 'linux' and os.uname().machine == 'x86_64'
            and os.geteuid() > 0 and os.getegid() > 0 and final - start > 850, 'native-platform-or-job-budget')
    workspace, build, output = map(Path, (args.workspace, args.build_root, args.output))
    require(all(path.is_absolute() for path in (workspace, build, output)), 'absolute-paths')
    require(not output.exists() and not output.is_symlink(), 'output-reuse')
    output.mkdir(mode=0o700)
    helper_build = Path(args.helper_build_root)
    require(helper_build.is_absolute() and helper_build not in (workspace, build, output), 'helper-build-root')
    runner = Runner(output, final)
    result = {'schema': 'issue779-csharp-n01-n02-run-v1', 'source_commit': SOURCE, 'exit': 1,
              'commands': runner.records, 'native_dispatched': False, 'native_case_receipt_verified': False,
              'trusted_enabled': False, 'remaining_fourteen_controls': 'pending'}
    generation = uuid.uuid4().hex
    root = Path('/var/lib/appsurface-evidence-input-' + generation)
    retained = False
    failure = None
    try:
        require(re.fullmatch('[0-9a-f]{64}', args.reviewed_script_sha256)
                and sha(read(Path(__file__), 131072, final)) == args.reviewed_script_sha256, 'orchestrator-pin')
        reviewed = workspace / '.github/issue779-csharp-native'
        for name, pin in PINS.items():
            require(re.fullmatch('[0-9a-f]{64}', pin) and sha(read(reviewed / name, 131072, final)) == pin, 'reviewed-pin')
        raw = read(build / 'receipts/build-receipt.json', 1048576, final)
        receipt = decode(raw)
        import importlib.util
        for name in ('check_kernel_record', 'check_cancellation_records', 'cancellation_adapter', 'check_cancellation_archive'):
            specification = importlib.util.spec_from_file_location(name, reviewed / (name + '.py'))
            module = importlib.util.module_from_spec(specification)
            sys.modules[name] = module
            specification.loader.exec_module(module)
        cancellation_archive = sys.modules['check_cancellation_archive']
        require(not os.path.lexists(build / 'receipts/late-publication-failure.json'), 'late-build-publication')
        maps = {name: (read(build / f'handoff/{name}.tsv', 1048576, final),
                       read(build / f'handoff/{name}-nodes.json', 4194304, final)) for name in ('source', 'tool', 'runtime')}
        validate_build(receipt, maps)
        result['build_receipt_sha256'] = sha(raw)
        result['generation'] = generation
        result['build_artifacts'] = receipt['artifacts']
        CANDIDATE_ENTRY_SHA = decode(maps['tool'][1])['files']['ForgeTrust.AppSurface.Cli.dll']['sha256']
        require(not os.path.lexists(helper_build / 'late-helper-publication-failure.json'), 'late-helper-publication')
        helper_raw = read(helper_build / 'helper-build-receipt.json', 1048576, final)
        helper = decode(helper_raw)
        validate_helper_handoff(helper, PINS['build-helper.sh'])
        helper_maps = (read(helper_build / 'helper.tsv', 1048576, final), read(helper_build / 'helper-nodes.json', 4194304, final))
        require(sha(helper_maps[0]) == helper['helper_tsv_sha256'] and sha(helper_maps[1]) == helper['helper_nodes_sha256'], 'helper-map-pins')
        helper_nodes = decode(helper_maps[1])
        require(set(helper_nodes) == {'schema','root_name','files','directories'} and helper_nodes['schema'] == 'issue779-build-node-inventory-v1' and helper_nodes['root_name'] == 'tool', 'helper-node-schema')
        require(len(helper_nodes['files']) == helper['helper_files'] and len(helper_nodes['directories']) == helper['helper_directories'] and sum(v['bytes'] for v in helper_nodes['files'].values()) == helper['helper_bytes'] and helper_nodes['files']['NativePeerBroker.dll']['sha256'] == helper['helper_entry_sha256'], 'helper-membership-receipt')
        result['helper_build_receipt_sha256'] = sha(helper_raw)
        helper_root = Path('/var/lib/appsurface-evidence-n03-helpers') / generation / 'bundle'
        runner.phase = 'root-bootstrap'
        bootstrap = Path('/var/lib/appsurface-evidence-bootstrap-' + generation)
        triples = [part for name, pin in PINS.items() for part in (str(reviewed / name), name, pin)]
        runner.run(ROOT_PREFIX + ['-c', BOOTSTRAP, '--', generation] + triples, time.monotonic() + 30)
        runner.phase = 'root-acquisition-and-transport'
        acquisition_end = min(final - 5, time.monotonic() + 120)
        acquisition_ms = root_deadline(acquisition_end)
        runner.run(ROOT_PREFIX + [str(bootstrap / 'acquire-inbound.sh'), '--execute', '--generation', generation,
            '--build-root', str(build), '--reviewed-root', str(bootstrap), '--deadline-monotonic-ms', acquisition_ms,
            '--reviewed-script-sha256', PINS['acquire-inbound.sh'], '--transport-sha256', PINS['prepare-root-inputs-v2.sh'],
            '--fixture-sha256', PINS['checkpoint-n01-n02-v5.sh'], '--audit-sha256', PINS['prepare-os-audit-v2.py'],
            '--source-review-sha256', PINS['source-review.json'], '--helper-build-root', str(helper_build), '--helper-build-receipt-sha256', sha(helper_raw)], acquisition_end)
        inbound = Path('/var/lib/appsurface-evidence-inbound-' + generation)
        transport = ['--execute', '--reviewed-script-sha256', PINS['prepare-root-inputs-v2.sh'], '--generation', generation,
                     '--deadline-monotonic-ms', acquisition_ms, '--source-commit', SOURCE]
        for name in ('source', 'tool', 'runtime'):
            fact = receipt['artifacts'][name]
            transport += ['--' + name + '-root', str(inbound / name), '--' + name + '-map', str(inbound / 'control' / (name + '.tsv')),
                          '--' + name + '-map-sha256', fact['tsv_sha256'], '--' + name + '-nodes', str(inbound / 'control' / (name + '-nodes.json')),
                          '--' + name + '-nodes-sha256', fact['nodes_sha256']]
        transport += ['--helper-root', str(inbound / 'helper'), '--helper-map', str(inbound / 'control/helper.tsv'), '--helper-map-sha256', helper['helper_tsv_sha256'], '--helper-nodes', str(inbound / 'control/helper-nodes.json'), '--helper-nodes-sha256', helper['helper_nodes_sha256'], '--helper-build-receipt', str(inbound / 'control/helper-build-receipt.json'), '--helper-build-receipt-sha256', sha(helper_raw), '--helper-entry-sha256', helper['helper_entry_sha256']]
        transport += ['--cancellation-parser-root', str(inbound / 'scripts')]
        transport += ['--source-review', str(inbound / 'control/source-review.json'), '--source-review-sha256', PINS['source-review.json'],
                      '--build-receipt', str(inbound / 'control/build-receipt.json'), '--build-receipt-sha256', sha(raw),
                      '--fixture', str(inbound / 'scripts/checkpoint-n01-n02-v5.sh'), '--fixture-sha256', PINS['checkpoint-n01-n02-v5.sh'],
                      '--os-audit-script', str(inbound / 'scripts/prepare-os-audit-v2.py'), '--os-audit-script-sha256', PINS['prepare-os-audit-v2.py']]
        runner.run(ROOT_PREFIX + [str(bootstrap / 'prepare-root-inputs-v2.sh')] + transport, acquisition_end)
        runner.phase = 'same-host-unprivileged-os-audit'
        audit_dir = Path('/tmp/issue779-os-audit-' + generation)
        require(not os.path.lexists(audit_dir), 'audit-output-reuse')
        audit_dir.mkdir(mode=0o700)
        audit_end = min(final - 5, time.monotonic() + 60)
        try:
            runner.run(['/usr/bin/python3', '-I', '-S', '-B', str(root / 'reviewed/prepare-os-audit-v2.py'), '--runtime-root', str(root / 'runtime'),
                        '--tool-root', str(root / 'tool'), '--helper-root', str(helper_root), '--output', str(audit_dir / 'os-audit.json'),
                        '--deadline-monotonic', str(audit_end)], audit_end)
        except BaseException:
            try:
                result['os_audit_failure_context'] = retain_audit_failure_context(
                    audit_dir / 'os-audit.json.failure-context.json', output / 'os-audit-failure-context.json', audit_end)
            except BaseException:
                pass
            raise
        audit = read(audit_dir / 'os-audit.json', 1048576, audit_end)
        require(decode(audit)['schema'] == 'issue779-fixture-os-elf-pins-v2', 'audit-schema')
        audit_hash = sha(audit)
        runner.run(ROOT_PREFIX + ['-c', AUDIT_COPY, '--', generation, str(audit_dir / 'os-audit.json'), audit_hash], audit_end)
        result['os_audit_sha256'] = audit_hash
        runner.phase = 'actual-csharp-n08'
        require(final - time.monotonic() > 665, 'fixture-plus-retention-budget')
        fixture = ['--execute', '--reviewed-script-sha256', PINS['checkpoint-n01-n02-v5.sh']]
        for name, flag in (('source', 'source'), ('tool', 'payload'), ('runtime', 'runtime')):
            fact = receipt['artifacts'][name]
            fixture += ['--' + flag + '-root', str(root / name), '--' + flag + '-manifest', str(root / 'control' / (name + '.tsv')),
                        '--' + flag + '-manifest-sha256', fact['tsv_sha256'], '--' + flag + '-nodes', str(root / 'control' / (name + '-nodes.json')),
                        '--' + flag + '-nodes-sha256', fact['nodes_sha256']]
        fixture += ['--n03-helper-root', str(helper_root), '--n03-helper-map', str(root / 'control/helper.tsv'), '--n03-helper-map-sha256', helper['helper_tsv_sha256'], '--n03-helper-nodes', str(root / 'control/helper-nodes.json'), '--n03-helper-nodes-sha256', helper['helper_nodes_sha256'], '--n03-helper-entry-sha256', helper['helper_entry_sha256'], '--n03-helper-generation', generation]
        fixture += ['--cancellation-parser-root', str(root / 'reviewed'), '--cancellation-parser-pins', str(root / 'reviewed/parser-pins.tsv')]
        fixture += ['--source-review', str(root / 'control/source-review.json'), '--source-review-sha256', PINS['source-review.json'],
                    '--build-receipt', str(root / 'control/build-receipt.json'), '--build-receipt-sha256', sha(raw),
                    '--os-audit', str(root / 'control/os-audit.json'), '--os-audit-sha256', audit_hash,
                    '--source-revision', SOURCE, '--base-revision', '4dd992ec1bc2df8220c73149115c5b478edb0085',
                    '--workflow-identity', 'github-actions-' + os.environ['GITHUB_RUN_ID'] + '-' + os.environ['GITHUB_RUN_ATTEMPT'],
                    '--entry', 'ForgeTrust.AppSurface.Cli.dll', '--entry-sha256', decode(maps['tool'][1])['files']['ForgeTrust.AppSurface.Cli.dll']['sha256'],
                    '--runtime-host', 'dotnet', '--n02-uid', str(os.geteuid()), '--n02-gid', str(os.getegid())]
        result['native_dispatched'] = True
        code, _ = runner.run(ROOT_PREFIX + ['-c', DISPATCH, '--', generation, PINS['checkpoint-n01-n02-v5.sh']] + fixture,
                             time.monotonic() + 630, allow_failure=True)
        result['fixture_exit'] = code
        require(code == 0, 'native-fixture-nonzero')
    except BaseException as caught:
        failure = caught
    finally:
        # Retention cannot upgrade a failed native operation, and cannot stop/clean its units.
        if result['native_dispatched'] and time.monotonic() + 35 < final:
            try:
                runner.phase = 'bounded-private-retention'
                retain_end = min(final - 5, time.monotonic() + 30)
                _, packet = runner.run(ROOT_PREFIX + [str(bootstrap / 'retain-native.sh'), '--execute', '--generation', generation,
                    '--deadline-monotonic-ms', root_deadline(retain_end), '--reviewed-script-sha256', PINS['retain-native.sh']], retain_end)
                result['retention_terminal'] = packet.decode('ascii').strip()
                _, before_sha = runner.run(ROOT_PREFIX + ['-c', ROOT_ARCHIVE_SHA, '--', generation], retain_end)
                _, archive = runner.run(ROOT_PREFIX + ['-c', READ_ROOT_ARCHIVE, '--', generation], retain_end)
                _, after_sha = runner.run(ROOT_PREFIX + ['-c', ROOT_ARCHIVE_SHA, '--', generation], retain_end)
                archive_hash = sha(archive)
                require(before_sha.decode('ascii').strip() == after_sha.decode('ascii').strip() == archive_hash, 'root-archive-digest')
                values = inspect_archive(archive)
                save(output / 'native-private.tar', archive, retain_end)
                result['private_archive_sha256'] = archive_hash
                result['private_archive_bytes'] = len(archive)
                result['retention_selection'] = values['retention-selection.json']
                if not failure:
                    require(result.get('fixture_exit') == 0 and 'fixture-result.json' in values, 'successful-native-receipt-missing')
                    validate_native_result(values['fixture-result.json'])
                    validate_negative_archive(values)
                    result['native_case_receipt_verified'] = True
                    result['native_cases_passed'] = [NEGATIVE_CASE]
                retained = True
            except BaseException as caught:
                failure = failure or caught
        elif result['native_dispatched']:
            failure = failure or Rejected('retention-budget-unavailable')
        result['retention_completed'] = retained
        result['failure'] = {'category': getattr(failure, 'category', 'native-operation'), 'error_class': type(failure).__name__} if failure else None
        result['elapsed_seconds'] = time.monotonic() - start
        result['exit'] = 0 if not failure and retained and result['native_case_receipt_verified'] else 1
        # Original five-second final reserve belongs to invalidation, not another phase.
        try:
            save(output / 'native-receipt.pending.json', result, final - 5)
            require(time.monotonic() < final - 5, 'late-pending-publication')
            os.rename(output / 'native-receipt.pending.json', output / 'native-receipt.json')
            require(time.monotonic() < final - 5, 'late-receipt-rename')
            print('NATIVE_TEST_TERMINAL:' + str(result['exit']), flush=True)
            require(time.monotonic() < final - 5, 'late-terminal-output')
        except BaseException:
            # A published zero is invalid if final output/clock validation fails.
            # The separately captured outer numeric terminal must also be zero.
            result['exit'] = 1
            if time.monotonic() < final:
                try:
                    save(output / 'native-late-failure.json', {'exit': 1, 'category': 'late-publication'}, final)
                except BaseException:
                    pass
    return result['exit']


if __name__ == '__main__':
    raise SystemExit(main())
