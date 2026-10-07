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

SOURCE = '5d1036b7035a7d9e980cdab5afc88879fca5048c'
PARENT = 'ed8dfcc228a59d4e6c058c3659fb1f0bf9d221f9'
SOURCE_MAP = 'ebc6e6815e7e951789fe58ec6bddb194013f8c9faaaa944a123231be69a6b1d0'
PINS = {
    'prepare-root-inputs-v2.sh': 'b181c7672fa3dbe9351fee15dcbf85b7ff99808d09077c8338bf4981c782ec6b',
    'checkpoint-n01-n02-v5.sh': '43c5c837cb152b284ac1bc23f58fbb36479a1e51539552ea4d9db952fd261cb0',
    'prepare-os-audit-v2.py': 'a76d3ec9521f021e799b8b56a6c57afdbeb6d4abdb21a55f42899a834eed4604',
    'source-review.json': 'ee6fa029cf2873a18b849f6e5590f3dd792ee71fa88f3409100ff802df3b4ca0',
    'acquire-inbound.sh': '849c6d0e9f6f0bf2b985a924f1f5c5b046799105170d55302fdd055f2e7f2f06',
    'retain-native.sh': 'a6bc3da869386b440d0b8a2ad93eb3cb888a4e48aabe52febf6a609b33d45331',
}
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


def validate_build(receipt, maps):
    require(receipt['schema'] == 'issue779-csharp-fdd-build-v5' and type(receipt['exit']) is int
            and receipt['exit'] == 0 and receipt.get('failure') is None and receipt['diagnostics'] == []
            and receipt['source_commit'] == SOURCE and receipt['harness_parent'] == PARENT
            and receipt['native_execution'] is False and receipt['checkpoint_pass'] is False, 'build-terminal')
    require(len(receipt['commands']) == 39 and [row['ordinal'] for row in receipt['commands']] == list(range(39)), 'build-commands')
    for row in receipt['commands']:
        require(type(row['exit']) is int and row['exit'] == 0 and row['failure'] is None
                and row['waited'] is True and row['group_absent'] is True and row['timed_out'] is False
                and row['forced_cleanup'] is False, 'build-command-terminal')
    for phase in ('source_before', 'source_after_assets', 'source_after_build', 'source_final'):
        fact = receipt[phase]
        require(fact['head'] == SOURCE and fact['count'] == 2819 and fact['source_map_sha256'] == SOURCE_MAP
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
    require(receipt['artifacts']['source']['file_count'] == 2819, 'source-count')


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
 [[ $name =~ ^[a-zA-Z0-9.-]+$ && $hash =~ ^[0-9a-f]{64}$ && -f $source && ! -L $source ]]
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

ARCHIVE_NAMES = {
    'retention-selection.json', 'fixture-stdout.log', 'fixture-stderr.log', 'fixture-result.json',
    'raw-evidence-plan.json', 'raw-evidence-manifest.json', 'raw-evidence-summary.json', 'worker-live.json',
    'request-policy.sha256', 'final-files.sha256', 'logs/n01.stdout', 'logs/n01.stderr', 'logs/n02.stdout',
    'logs/n02.stderr', 'logs/n02.trace', 'logs/observer.stdout', 'logs/observer.stderr', 'logs/kill.log', 'logs/stop.log',    'logs/n02.file-limit',
    'logs/n02.limits',
    'logs/n02.facts.json',
    'logs/n02.io.trace',
    'logs/n02.credentials.json',
    'logs/n02-higher.stdout',
    'logs/n02-higher.stderr',
    'logs/n02-higher.trace',
    'logs/n02-higher.file-limit',
    'logs/n02-higher.limits',
    'logs/n02-higher.facts.json',
    'logs/n02-higher.io.trace',
    'logs/n02-higher.credentials.json',
    'n02-startup-limit-diagnostic.pending',
    'n02-startup-limit-diagnostic.json',
}



def inspect_archive(data):
    require(0 < len(data) <= 33619968, 'archive-size')
    values, total, names = {}, 0, []
    with tarfile.open(fileobj=io.BytesIO(data), mode='r:') as archive:
        for member in archive:
            require(member.name in ARCHIVE_NAMES and member.name not in names and member.isfile()
                    and member.mode == 0o600 and member.uid == 0 and member.gid == 0 and member.mtime == 0
                    and not member.pax_headers and not member.linkname and 0 <= member.size <= 8388608,
                    'archive-member')
            names.append(member.name)
            require(len(names) <= len(ARCHIVE_NAMES), 'archive-count')
            total += member.size
            require(total <= 33554432 + 4096, 'archive-expanded-bound')
            stream = archive.extractfile(member)
            require(stream is not None, 'archive-file')
            with stream:
                content = stream.read(member.size + 1)
            require(len(content) == member.size, 'archive-short-read')
            if member.name in ('fixture-result.json', 'retention-selection.json'):
                require(len(content) <= 4096, 'archive-json-size')
                values[member.name] = decode(content)
    require(names == sorted(names) and 'retention-selection.json' in values, 'archive-order-or-selection')
    selection = values['retention-selection.json']
    require(set(selection) == {'schema', 'selection_status', 'data_file_count', 'data_bytes', 'missing_fixed_file_count'}
            and selection['schema'] == 'issue779-native-retention-selection-v1'
            and selection['selection_status'] in ('parent-absent', 'parent-empty', 'one-namespace')
            and type(selection['data_file_count']) is int and selection['data_file_count'] == len(names) - 1
            and type(selection['data_bytes']) is int and 0 <= selection['data_bytes'] <= 33554432,
            'selection-schema')
    return values


def validate_native_result(value):
    require(value['schema'] == 'issue779-native-n01-n02-fixture-v5' and value['source_revision'] == SOURCE
            and value['base_revision'] == '4dd992ec1bc2df8220c73149115c5b478edb0085'
            and re.fullmatch('[0-9a-f]{32}', value['generation']) and re.fullmatch('[0-9a-f]{64}', value['policy_sha256'])
            and value['owner_unit'] == 'appsurface-evidence-owner-' + value['generation'] + '.service'
            and value['worker_unit'] == 'appsurface-evidence-worker-' + value['generation'] + '.service'
            and type(value['n01_exit']) is int and value['n01_exit'] == 0
            and type(value['n02_exit']) is int and value['n02_exit'] == 1
            and value['native_controls_executed'] == ['N01', 'N02'] and value['owned_process_groups_joined'] is True
            and value['trusted_enabled'] is False and value['remaining_fourteen_controls'] == 'pending', 'native-case-receipt')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--execute', action='store_true')
    parser.add_argument('--workspace', required=True)
    parser.add_argument('--build-root', required=True)
    parser.add_argument('--output', required=True)
    parser.add_argument('--reviewed-script-sha256', required=True)
    args = parser.parse_args()
    job_end = int(os.environ['JOB_DEADLINE_MONOTONIC_NS']) / 1_000_000_000
    start = time.monotonic()
    final = min(job_end, start + 900)
    require(args.execute and sys.platform == 'linux' and os.uname().machine == 'x86_64'
            and os.geteuid() > 0 and os.getegid() > 0 and final - start > 850, 'native-platform-or-job-budget')
    workspace, build, output = map(Path, (args.workspace, args.build_root, args.output))
    require(all(path.is_absolute() for path in (workspace, build, output)), 'absolute-paths')
    require(not output.exists() and not output.is_symlink(), 'output-reuse')
    output.mkdir(mode=0o700)
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
        require(not os.path.lexists(build / 'receipts/late-publication-failure.json'), 'late-build-publication')
        maps = {name: (read(build / f'handoff/{name}.tsv', 1048576, final),
                       read(build / f'handoff/{name}-nodes.json', 4194304, final)) for name in ('source', 'tool', 'runtime')}
        validate_build(receipt, maps)
        result['build_receipt_sha256'] = sha(raw)
        result['generation'] = generation
        result['build_artifacts'] = receipt['artifacts']
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
            '--source-review-sha256', PINS['source-review.json']], acquisition_end)
        inbound = Path('/var/lib/appsurface-evidence-inbound-' + generation)
        transport = ['--execute', '--reviewed-script-sha256', PINS['prepare-root-inputs-v2.sh'], '--generation', generation,
                     '--deadline-monotonic-ms', acquisition_ms, '--source-commit', SOURCE]
        for name in ('source', 'tool', 'runtime'):
            fact = receipt['artifacts'][name]
            transport += ['--' + name + '-root', str(inbound / name), '--' + name + '-map', str(inbound / 'control' / (name + '.tsv')),
                          '--' + name + '-map-sha256', fact['tsv_sha256'], '--' + name + '-nodes', str(inbound / 'control' / (name + '-nodes.json')),
                          '--' + name + '-nodes-sha256', fact['nodes_sha256']]
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
            runner.run(['/usr/bin/python3', '-B', str(root / 'reviewed/prepare-os-audit-v2.py'), '--runtime-root', str(root / 'runtime'),
                        '--tool-root', str(root / 'tool'), '--output', str(audit_dir / 'os-audit.json'),
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
        runner.phase = 'actual-csharp-n01-n02'
        require(final - time.monotonic() > 665, 'fixture-plus-retention-budget')
        fixture = ['--execute', '--reviewed-script-sha256', PINS['checkpoint-n01-n02-v5.sh']]
        for name, flag in (('source', 'source'), ('tool', 'payload'), ('runtime', 'runtime')):
            fact = receipt['artifacts'][name]
            fixture += ['--' + flag + '-root', str(root / name), '--' + flag + '-manifest', str(root / 'control' / (name + '.tsv')),
                        '--' + flag + '-manifest-sha256', fact['tsv_sha256'], '--' + flag + '-nodes', str(root / 'control' / (name + '-nodes.json')),
                        '--' + flag + '-nodes-sha256', fact['nodes_sha256']]
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
                    result['native_case_receipt_verified'] = True
                    result['native_cases_passed'] = ['N01', 'N02']
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
