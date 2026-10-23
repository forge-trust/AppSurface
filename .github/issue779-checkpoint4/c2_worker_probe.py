"""One private status-zero C2 worker measurement; never package or matrix acceptance.

The unchanged checkpoint-two native procedure supplies real supervision, producer
execution and cleanup. A separate C2Measure image retains original worker hit bytes
through its existing authenticated pidfd/private-tmp handoff. Standard Coverlet owns
instrumentation and report generation; this module never edits counters or merges.
"""
from pathlib import Path
from types import SimpleNamespace
import argparse
import hashlib
import importlib.util
import json
import os
import platform
import math
import re
import signal
import stat
import subprocess
import sys
import time
import uuid

from measurement_arguments import MODULES, arguments
from producer_roots import BASE, NAMES, TOTAL, decode, image, read, source, write

ROOT = Path(__file__).resolve().parents[2]
SELF = Path(__file__).resolve()
DRIVER = ROOT / '.github/issue779-checkpoint2/run-native.py'
MAX_LOG = 8 * 1024 * 1024
MAX_REPORT = 32 * 1024 * 1024


class ProbeCancelled(BaseException):
    """Interrupt the procedure through its original finally blocks, without success conversion."""


def cancel_target(signum, frame):
    signal.signal(signal.SIGTERM, signal.SIG_IGN)
    signal.signal(signal.SIGINT, signal.SIG_IGN)
    raise ProbeCancelled()


def require(value, reason):
    if not value:
        raise ValueError('c2-worker-probe-' + reason)


def digest(body):
    return hashlib.sha256(body).hexdigest()


def driver():
    """Import the source-bound fixed native body, without invoking its seven-case main."""
    sys.path.insert(0, str(DRIVER.parent))
    try:
        spec = importlib.util.spec_from_file_location('c2_probe_native_driver', DRIVER)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        return module
    finally:
        sys.path.pop(0)


def closed_events(raw):
    require(type(raw) is bytes and len(raw) <= MAX_LOG, 'owner-log-bound')
    rows = []
    for line in raw.splitlines():
        try:
            value = decode(line)
        except (ValueError, UnicodeError):
            continue
        if type(value) is dict and value.get('schema') == 'evidence-worker-measurement-v1':
            rows.append(value)
    require(len(rows) == 1, 'one-normal-worker-witness')
    return rows[0]


def validate_handoff(value, descriptor, instrumented):
    """Validate retained data; only the source-bound native monitor creates the witness."""
    require(type(value) is dict and set(value) == {
        'schema', 'pid', 'start_time', 'uid', 'gid', 'cgroup',
        'original_pidfd_exit', 'modules', 'native_acceptance'}, 'witness-fields')
    require(value['schema'] == 'evidence-worker-measurement-v1'
            and value['original_pidfd_exit'] is True
            and value['native_acceptance'] is False, 'witness-state')
    require(all(type(value[key]) is int and value[key] > 0
                for key in ('pid', 'start_time', 'uid', 'gid')), 'witness-integers')
    require(value['pid'] == descriptor['worker_pid']
            and value['uid'] == descriptor['worker_uid']
            and value['gid'] == descriptor['worker_gid']
            and value['cgroup'] == descriptor['cgroup'], 'worker-descriptor-binding')
    require(type(value['cgroup']) is str
            and re.fullmatch(r'/system\.slice/appsurface-evidence-worker-[0-9a-f]{32}\.service',
                         value['cgroup']) is not None, 'worker-cgroup')
    rows = value['modules']
    require(type(rows) is list and len(rows) == len(MODULES)
            and all(type(row) is dict for row in rows)
            and [row.get('module') for row in rows] == list(MODULES), 'module-list')
    handed = []
    for row in rows:
        name = row['module']
        require(row.get('module_sha256') == instrumented[name + '.dll'], 'module-image')
        if row.get('outcome') == 'unchanged-worker-handoff':
            require(set(row) == {'module', 'outcome', 'candidates', 'worker_sha256',
                                 'host_sha256', 'module_sha256'}, 'handoff-fields')
            require(type(row['candidates']) is int and 0 < row['candidates'] <= 1048575
                    and type(row['worker_sha256']) is str
                    and re.fullmatch('[0-9a-f]{64}', row['worker_sha256']) is not None
                    and row['worker_sha256'] == row['host_sha256'], 'unchanged-hits')
            handed.append(name)
        else:
            require(row.get('outcome') == 'worker-file-absent'
                    and set(row) == {'module', 'outcome', 'module_sha256'}, 'no-prior-root-import')
    require(all(name in handed for name in (
        'ForgeTrust.AppSurface.Cli', 'ForgeTrust.AppSurface.Evidence.Contracts',
        'ForgeTrust.AppSurface.Evidence.Coverage')), 'required-worker-modules')
    return handed


def inputs(args):
    require(args.base == BASE and re.fullmatch('[0-9a-f]{40}', args.source) is not None, 'source')
    require(all(path.is_absolute() for path in (args.tool, args.runtime, args.subject,
                                               args.evidence, args.console)), 'absolute-inputs')
    capture_raw, capture = source(args)
    require(all(name in capture['files'] for name in (
        str(SELF.relative_to(ROOT)), 'Directory.Build.props',
        'Evidence/ForgeTrust.AppSurface.Evidence.Supervision/LinuxWorkerProcess.cs',
        '.github/issue779-checkpoint4/measurement/LinuxWorkerMeasurement.cs')), 'source-members')
    return digest(capture_raw)


def run_target(args):
    """Run precisely one genuine normal procedure under the collector's original bound."""
    require(args.work_deadline is not None and args.cleanup_deadline is not None
            and math.isfinite(args.work_deadline) and math.isfinite(args.cleanup_deadline)
            and time.monotonic() < args.work_deadline < args.cleanup_deadline
            and args.cleanup_deadline - args.work_deadline <= 60.001
            and args.cleanup_deadline - time.monotonic() <= 960, 'original-deadlines')
    end = args.cleanup_deadline
    signal.signal(signal.SIGTERM, cancel_target)
    signal.signal(signal.SIGINT, cancel_target)
    require(os.geteuid() == 0 and sys.platform == 'linux'
            and platform.machine() == 'x86_64', 'root-linux-x64')
    capture_sha = inputs(args)
    original = decode(read(args.evidence / 'original.json'))
    require(original['source_capture_sha256'] == capture_sha
            and original['console_sha256'] == digest(read(args.console, TOTAL)), 'source-context')
    measured = image(args.tool)
    require(set(measured) == set(NAMES)
            and all(measured[name + '.dll'] != original['original'][name + '.dll']
                    for name in MODULES), 'actual-instrumentation')
    write(args.evidence / 'instrumented.json', {'source_capture_sha256': capture_sha,
                                               'instrumented': measured})
    native = driver()
    require(Path('/sys/fs/cgroup/cgroup.controllers').is_file(), 'unified-cgroup-v2')
    require(native.command(['/usr/bin/systemctl', '--version'], end).stdout.splitlines()[0]
            .startswith(b'systemd 255'), 'systemd-255')
    for name in ('/usr/bin/env', '/usr/bin/systemd-run', '/usr/bin/systemctl', '/usr/bin/getent'):
        path = Path(name).resolve(strict=True)
        node = path.stat()
        require(node.st_uid == 0 and node.st_mode & 0o022 == 0
                and path.read_bytes()[:4] == b'\x7fELF', 'trusted-os-bootstrap')
    staging = Path('/run') / ('issue779-c2-private-probe-' + uuid.uuid4().hex)
    native.create_traversable_root(staging)
    tool, runtime = staging / 'tool', staging / 'runtime'
    inventory = {'tool': native.copy_tree(args.tool, tool),
                 'runtime': native.copy_tree(args.runtime, runtime)}
    native.write(tool / 'fixture-policy.json', native.canonical(native.policy()), 0o644)
    inventory['tool']['fixture-policy.json'] = {
        'sha256': native.sha(tool / 'fixture-policy.json'),
        'bytes': (tool / 'fixture-policy.json').stat().st_size, 'mode': '0444'}
    for tree in (tool, runtime):
        for path in (tree, *tree.rglob('*')):
            os.chown(path, 0, 0, follow_symlinks=False)
            path.chmod(0o555 if path.is_dir() or path.stat().st_mode & 0o111 else 0o444)
    output = args.evidence / 'native'
    output.mkdir(mode=0o700)
    native.write(output / 'input-inventory.json', native.canonical(inventory))
    case_args = SimpleNamespace(staged_tool=tool, staged_runtime=runtime,
        staged_mutation_tool=tool, subject=args.subject, output=output,
        base=args.base, source=args.source,
        input_inventory_sha256=native.sha(output / 'input-inventory.json'))
    receipt = {'schema': 'issue779-c2-private-worker-probe-v1', 'source': args.source,
               'base': args.base, 'cases': [], 'native_acceptance': False,
               'package_acceptance': False, 'matrix_acceptance': False}
    write(args.evidence / 'native-started.json', {'source': args.source,
                                                'source_capture_sha256': capture_sha})
    try:
        require(native.execute_case(case_args, 'normal', end, receipt), 'normal-procedure')
        require(len(receipt['cases']) == 1 and receipt['cases'][0]['case'] == 'normal', 'one-case')
        actual = receipt['cases'][0]
        require(actual['passed'] is True and actual['groups_empty'] is True
                and actual['final_groups_empty'] is True and actual['accounts_absent'] is True
                and actual['owner_terminal']['wrapper_exit'] == 0, 'physical-procedure')
        descriptor_path = Path('/run') / ('appsurface-evidence-' + actual['generation']) / 'worker/worker-control.json'
        descriptor = native.sealed_descriptor(descriptor_path, after_custody=True)
        require(descriptor is not None, 'retained-worker-descriptor')
        descriptor_raw = read(descriptor_path, 65536)
        require(decode(descriptor_raw) == descriptor, 'worker-descriptor-stable')
        native.write(args.evidence / 'worker-descriptor.json', descriptor_raw)
        witness = closed_events(read(output / 'normal-owner.stderr', MAX_LOG))
        handed = validate_handoff(witness, descriptor, measured)
        write(args.evidence / 'worker-handoff.json', {
            'source_capture_sha256': capture_sha, 'instrumented': measured,
            'worker_descriptor_sha256': digest(descriptor_raw),
            'witness': witness, 'handed_modules': handed, 'native_acceptance': False})
        receipt['exit'] = 0
        return 0
    finally:
        receipt.setdefault('exit', 1)
        write(args.evidence / 'normal-probe.json', receipt)
        settle_native(args.evidence, native, end)


def settle_native(evidence, native, end):
    """Recheck the exact generation recorded by the original procedure after its containment."""
    require(time.monotonic() < end, 'native-cleanup-deadline')
    raw = read(evidence / 'native/normal.json')
    actual = decode(raw)
    generation = actual.get('generation')
    require(type(generation) is str and re.fullmatch('[0-9a-f]{32}', generation) is not None
            and actual.get('case') == 'normal' and actual.get('final_groups_empty') is True,
            'native-final-settlement')
    units = ['appsurface-evidence-' + role + '-' + generation + '.service'
             for role in ('owner', 'worker', 'producer')]
    require(native.groups_empty(units), 'native-groups-empty')
    write(evidence / 'native-settlement.json', {
        'generation': generation, 'units': units, 'physical_groups_empty': True,
        'normal_result_sha256': digest(raw), 'native_acceptance': False})


def common(args):
    return [item for name in ('tool', 'runtime', 'subject', 'evidence', 'console', 'source', 'base')
            for item in ('--' + name, str(getattr(args, name)))]


def run_collector(args):
    """Instrument one private image and validate restoration; no existing report is consumed."""
    started = time.monotonic()
    args.work_deadline = started + 900
    args.cleanup_deadline = started + 960
    require(os.geteuid() == 0 and sys.platform == 'linux', 'root-collector')
    capture_sha = inputs(args)
    require(not any((args.evidence / name).exists() for name in (
        'original.json', 'instrumented.json', 'native', 'worker-handoff.json',
        'normal-probe.json', 'normal-report.json', 'probe-result.json',
        'worker-descriptor.json', 'native-started.json', 'native-settlement.json')), 'fresh-output')
    original = image(args.tool)
    write(args.evidence / 'original.json', {'source_capture_sha256': capture_sha,
        'original': original, 'console_sha256': digest(read(args.console, TOTAL)),
        'qualification': 'C2Measure', 'native_acceptance': False})
    target = ['-B', str(SELF), 'target', *common(args),
              '--work-deadline', str(args.work_deadline),
              '--cleanup-deadline', str(args.cleanup_deadline)]
    argv = [str(args.runtime / 'dotnet'), str(args.console),
            str(args.tool / (MODULES[0] + '.dll')), '--target', '/usr/bin/python3',
            '--targetargs', arguments(target), '--include', *('[' + name + ']*' for name in MODULES),
            '--include-test-assembly', '--format', 'json',
            '--output', str(args.evidence / 'normal-report.json')]
    code = wait_collector(argv, args.evidence / 'collector.log',
                          args.work_deadline, args.cleanup_deadline)
    restored = image(args.tool) == original
    write(args.evidence / 'probe-result.json', {'source': args.source,
        'source_capture_sha256': capture_sha, 'collector_exit': code,
        'original_images_restored': restored, 'merge_with': None,
        'native_acceptance': False, 'package_acceptance': False, 'matrix_acceptance': False})
    require(restored and code == 0, 'collector-terminal')
    report = read(args.evidence / 'normal-report.json', MAX_REPORT)
    value = json.loads(report)
    require(type(value) is dict and value and all(name + '.dll' in value for name in MODULES), 'report-modules')
    require(len(read(args.evidence / 'collector.log', MAX_LOG)) <= MAX_LOG, 'collector-log')
    return 0


def wait_collector(argv, log_path, work_end, cleanup_end):
    """Give the target its original cleanup reserve before joining or force containment."""
    require(work_end < cleanup_end and cleanup_end - work_end <= 60.001, 'collector-reserve')
    with log_path.open('xb') as log:
        process = subprocess.Popen(argv, stdin=subprocess.DEVNULL, stdout=log,
                                   stderr=subprocess.STDOUT, start_new_session=True)
        try:
            while True:
                require(log_path.stat().st_size <= MAX_LOG, 'collector-output-bound')
                remaining = work_end - time.monotonic()
                require(remaining > 0, 'collector-deadline')
                try:
                    code = process.wait(timeout=min(0.25, remaining))
                    require(log_path.stat().st_size <= MAX_LOG, 'collector-output-bound')
                    try:
                        os.killpg(process.pid, 0)
                    except ProcessLookupError:
                        return code
                    raise ValueError('c2-worker-probe-unjoined-collector-group')
                except subprocess.TimeoutExpired:
                    continue
        except BaseException as failure:
            try:
                os.killpg(process.pid, signal.SIGTERM)
            except ProcessLookupError:
                pass
            while time.monotonic() < cleanup_end:
                process.poll()
                try:
                    os.killpg(process.pid, 0)
                except ProcessLookupError:
                    process.wait(timeout=max(0.001, cleanup_end - time.monotonic()))
                    raise failure
                time.sleep(min(0.05, max(0, cleanup_end - time.monotonic())))
            try:
                os.killpg(process.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
            process.wait(timeout=5)
            raise ValueError('c2-worker-probe-collector-cleanup-incomplete')


def retain(root):
    """Bound all private probe leaves without following links; this grants no control success."""
    root = Path(root)
    require(root.is_dir() and not root.is_symlink(), 'retention-root')
    if (root / 'native-started.json').exists():
        settlement = decode(read(root / 'native-settlement.json'))
        require(settlement.get('physical_groups_empty') is True
                and settlement.get('native_acceptance') is False
                and settlement.get('normal_result_sha256') == digest(read(root / 'native/normal.json')),
                'retention-settlement')
        generation = settlement['generation']
        require(type(generation) is str and re.fullmatch('[0-9a-f]{32}', generation) is not None,
                'retention-generation')
        units = ['appsurface-evidence-' + role + '-' + generation + '.service'
                 for role in ('owner', 'worker', 'producer')]
        require(settlement['units'] == units and driver().groups_empty(units), 'retention-physical-exit')
    total = count = 0
    for path in root.rglob('*'):
        node = path.lstat()
        require(not stat.S_ISLNK(node.st_mode), 'retention-link')
        if stat.S_ISDIR(node.st_mode):
            path.chmod(0o700)
            continue
        maximum = MAX_REPORT if path.parent == root and path.name == 'normal-report.json' else MAX_LOG
        require(stat.S_ISREG(node.st_mode) and node.st_nlink == 1
                and node.st_size <= maximum, 'retention-leaf')
        total += node.st_size
        count += 1
        path.chmod(0o600)
    require(count <= 64 and total <= 48 * 1024 * 1024, 'retention-total')
    root.chmod(0o700)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('phase', choices=('run', 'target', 'retain'))
    for name in ('tool', 'runtime', 'subject', 'evidence', 'console'):
        parser.add_argument('--' + name, type=Path, required=True)
    parser.add_argument('--source', required=True)
    parser.add_argument('--base', default=BASE)
    parser.add_argument('--work-deadline', type=float)
    parser.add_argument('--cleanup-deadline', type=float)
    args = parser.parse_args()
    os.umask(0o077)
    if args.phase == 'retain':
        retain(args.evidence)
        return 0
    return run_collector(args) if args.phase == 'run' else run_target(args)


if __name__ == '__main__':
    raise SystemExit(main())
