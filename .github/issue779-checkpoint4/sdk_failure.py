"""One finite SDK failed-test observation, not an application/process owner or acceptance authority."""
from pathlib import Path
import argparse
import hashlib
import importlib.util
import json
import math
import os
import platform
import re
import stat
import time

from measurement_arguments import MODULES

NAMES = tuple(name + suffix for name in MODULES for suffix in ('.dll', '.pdb'))
STATE_FIELDS = set(('schema stage normal_code normal_status stopped_code stopped_status start_reserved '
                    'start_joined stop_joined pending_failure physically_settled cleanup_failed '
                    'group_empty_twice pump_joined captured_pid last_main_pid last_exec_pid last_code '
                    'last_status last_active last_substate output_drained').split())
PREFIX_LIMIT = 1024 * 1024
REPORT_LIMIT = 8 * 1024 * 1024


def require(value, category):
    if not value:
        raise RuntimeError('sdk-failure-' + category)


def strict_json(data):
    def pairs(items):
        result, folded = {}, set()
        for key, value in items:
            require(key.casefold() not in folded, 'json-members')
            folded.add(key.casefold())
            result[key] = value
        return result
    return json.loads(data, object_pairs_hook=pairs)


def check_deadline(end):
    require(type(end) in (float, int) and math.isfinite(end) and time.monotonic() < end, 'deadline')


def identity(metadata):
    return (metadata.st_dev, metadata.st_ino, metadata.st_mode, metadata.st_uid, metadata.st_gid,
            metadata.st_nlink, metadata.st_size, metadata.st_mtime_ns, metadata.st_ctime_ns)


def read_leaf(root, name, limit, uid, gid, end):
    check_deadline(end)
    fd = os.open(name, os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC | os.O_NONBLOCK, dir_fd=root)
    try:
        before = os.fstat(fd)
        require(stat.S_ISREG(before.st_mode) and before.st_uid == uid and before.st_gid == gid
                and before.st_nlink == 1 and stat.S_IMODE(before.st_mode) == 0o600
                and 0 <= before.st_size <= limit, 'diagnostic-file')
        body = bytearray()
        while len(body) <= before.st_size:
            check_deadline(end)
            part = os.read(fd, min(65536, before.st_size + 1 - len(body)))
            if not part:
                break
            body.extend(part)
        require(len(body) == before.st_size and identity(before) == identity(os.fstat(fd)), 'diagnostic-stability')
        require(identity(before) == identity(os.stat(name, dir_fd=root, follow_symlinks=False)), 'diagnostic-name')
        check_deadline(end)
        return bytes(body)
    finally:
        os.close(fd)


def read_failure(generation, expected_pid, end, *, parent=Path('/run'), uid=0, gid=0):
    """Read fixed diagnostic data; portable owner/parent parameters issue no process or admission capability."""
    require(type(generation) is str and re.fullmatch('[0-9a-f]{32}', generation), 'generation')
    require(type(expected_pid) is int and 0 < expected_pid <= 2147483647, 'producer-pid')
    name = 'appsurface-evidence-producer-diagnostics-' + generation
    parent_fd = os.open(parent, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
    try:
        metadata = os.fstat(parent_fd)
        require(metadata.st_uid == uid and metadata.st_gid == gid and not metadata.st_mode & 0o022, 'diagnostic-parent')
        root = os.open(name, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=parent_fd)
        try:
            before = os.fstat(root)
            require(before.st_uid == uid and before.st_gid == gid and stat.S_IMODE(before.st_mode) == 0o700, 'diagnostic-root')
            require(set(os.listdir(root)) == {'state.json', 'stdout.prefix', 'stderr.prefix'}, 'diagnostic-names')
            state_bytes = read_leaf(root, 'state.json', 4096, uid, gid, end)
            stdout = read_leaf(root, 'stdout.prefix', PREFIX_LIMIT, uid, gid, end)
            stderr = read_leaf(root, 'stderr.prefix', PREFIX_LIMIT, uid, gid, end)
            state = strict_json(state_bytes)
            require(set(state) == STATE_FIELDS and state['schema'] == 'evidence-producer-failure-state-v1'
                    and state['stage'] == 'settlement', 'producer-state-shape')
            require(type(state['normal_code']) is int and state['normal_code'] == 1
                    and type(state['normal_status']) is int and state['normal_status'] == 1
                    and type(state['captured_pid']) is int and state['captured_pid'] == expected_pid, 'producer-normal-exit')
            for field in ('start_reserved', 'start_joined', 'stop_joined', 'physically_settled',
                          'group_empty_twice', 'pump_joined', 'output_drained'):
                require(state[field] is True, 'producer-joined')
            require(state['cleanup_failed'] is False and state['pending_failure'] == 'None', 'producer-cleanup')
            summaries = re.findall(rb'Failed!\s*-\s*Failed:\s*(\d+),\s*Passed:\s*(\d+),\s*Skipped:\s*(\d+),\s*Total:\s*(\d+),', stdout + b'\n' + stderr)
            require(len(summaries) == 1 and tuple(map(int, summaries[0])) == (1, 5, 0, 6), 'actual-test-summary')
            require(b'Root-selected test-failure scenario.' in stdout + stderr, 'actual-test-failure')
            require(identity(before) == identity(os.fstat(root))
                    and identity(before) == identity(os.stat(name, dir_fd=parent_fd, follow_symlinks=False)), 'diagnostic-root-stability')
            check_deadline(end)
            return {'normal_code': 1, 'normal_status': 1, 'captured_pid': expected_pid,
                    'physically_settled': True, 'group_empty_twice': True, 'pump_joined': True,
                    'output_drained': True, 'cleanup_failed': False, 'pending_failure': 'None',
                    'tests': {'failed': 1, 'passed': 5, 'skipped': 0, 'total': 6},
                    'files': {name: {'bytes': len(data), 'sha256': hashlib.sha256(data).hexdigest()}
                              for name, data in [('state.json', state_bytes), ('stdout.prefix', stdout), ('stderr.prefix', stderr)]},
                    'native_acceptance': False}
        finally:
            os.close(root)
    finally:
        os.close(parent_fd)


def driver():
    path = Path(__file__).resolve().parents[1] / 'issue779-checkpoint3/run-native.py'
    spec = importlib.util.spec_from_file_location('sdk_failure_driver', path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def inspect_case(generation, descriptor, producer, stderr, observations, end):
    r = driver()
    require(producer is not None, 'actual-producer')
    packets = r.events(stderr, 'evidence-sdk-producer-failure-v1')
    require(len(packets) == 1, 'ready-guard-count')
    packet = packets[0]
    require(set(packet) == {'schema', 'producer_exit_code', 'application_unit', 'original_application_ready_guard', 'native_acceptance'}
            and type(packet['producer_exit_code']) is int and packet['producer_exit_code'] == 1
            and packet['original_application_ready_guard'] is True and packet['native_acceptance'] is False
            and packet['application_unit'] == 'appsurface-evidence-application-' + generation + '.service', 'ready-guard')
    require(any(row.get('phase') == 'ready' and row.get('dcp_checked') is True for row in observations), 'original-readiness')
    state = read_failure(generation, producer['pid'], end)
    path = Path('/run/appsurface-evidence-' + generation + '/output/evidence/evidence-manifest.json')
    require(path.stat().st_size <= 1024 * 1024, 'manifest-bound')
    manifest = strict_json(path.read_bytes())
    require(manifest['ExecutionVerdict'] == 'Incomplete' and manifest['Metrics']['CleanupCompleted'] is True
            and manifest['ClaimKind'] == 'None' and manifest['Eligibility'] == 'None', 'failed-manifest')
    rows = manifest['ProducerResults']
    require(len(rows) == 1 and rows[0]['Outcome'] == 'Failed' and rows[0].get('Artifacts') in (None, [])
            and rows[0]['SatisfiedAssertionIds'] == [] and rows[0]['Diagnostic'] == 'The restricted coverage process exited with code 1.', 'failed-producer')
    r.c2.verify_sealed_output(path.parent)
    return {'original_application_ready_guard': packet, 'producer': state,
            'manifest_sha256': hashlib.sha256(path.read_bytes()).hexdigest(),
            'execution_verdict': 'Incomplete', 'claim_kind': 'None', 'eligibility': 'None', 'cleanup_completed': True,
            'producer_result': {'outcome': rows[0]['Outcome'], 'diagnostic': rows[0]['Diagnostic'],
                                'artifact_count': 0, 'assertion_count': 0},
            'worker': {key: descriptor[key] for key in ('worker_pid', 'worker_uid', 'worker_gid', 'cgroup')},
            'account_binding': {**{key: descriptor[key] for key in ('worker_uid', 'worker_gid', 'subject_uid', 'subject_gid')},
                                **{key: descriptor['application'][key] for key in ('application_uid', 'application_gid', 'results_gid', 'resource_access_gid')}}}


def image_rows(tool):
    result = {}
    for name in NAMES:
        path = tool / name
        require(path.is_file() and not path.is_symlink() and 0 < path.stat().st_size <= 32 * 1024 * 1024, 'module-size')
        result[name] = hashlib.sha256(path.read_bytes()).hexdigest()
    return result


def require_worker(worker, expected, instrumented):
    require(worker['schema'] == 'evidence-worker-measurement-v2' and type(worker['normal_exit_code']) is int
            and worker['normal_exit_code'] == 1 and worker['original_pidfd_exit'] is True
            and worker['native_acceptance'] is False, 'failed-worker-measurement')
    for key, descriptor_key in [('pid', 'worker_pid'), ('uid', 'worker_uid'), ('gid', 'worker_gid')]:
        value = worker[key]
        require(type(value) is int and 0 < value < 4294967295 and value == expected[descriptor_key], 'worker-binding')
    require(type(worker['start_time']) is int and worker['start_time'] > 0 and worker['cgroup'] == expected['cgroup'], 'worker-identity')
    rows = worker['modules']
    require(len(rows) == 6 and {row['module'] for row in rows} == {name + '.dll' for name in MODULES}, 'worker-modules')
    required = {'ForgeTrust.AppSurface.' + name + '.dll' for name in ('Cli', 'Evidence.Contracts', 'Evidence.Coverage', 'Evidence.Aspire')}
    for row in rows:
        require(row['module_sha256'] == instrumented[row['module']], 'worker-image')
        if row['module'] in required or row['outcome'] == 'unchanged-worker-handoff':
            require(row['outcome'] == 'unchanged-worker-handoff'
                    and re.fullmatch('[0-9a-f]{64}', row['worker_sha256'])
                    and row['worker_sha256'] == row['host_sha256'], 'worker-handoff')
        else:
            require(row['outcome'] in ('worker-file-absent', 'prior-root-file-unchanged'), 'worker-unused-module')


def require_failed_report(body, workspace, source_capture):
    # Standard collector module/class names are case sensitive. Do not apply state-field alias rules here.
    report = json.loads(body)
    require(len(report) == 6 and {Path(name).name for name in report} == {name + '.dll' for name in MODULES}, 'report-modules')
    relative = 'Evidence/ForgeTrust.AppSurface.Evidence.Coverage/EvidenceRestrictedCoverageProducer.cs'
    source = (workspace / relative).read_bytes()
    require(hashlib.sha256(source).hexdigest() == source_capture['files'][relative]['sha256'], 'producer-source')
    lines = source.decode().splitlines()
    targets = [index for index, text in enumerate(lines) if 'The restricted coverage process exited with code {process.ExitCode' in text]
    require(len(targets) == 1 and targets[0] > 0
            and 'result = CreateResult(producer, EvidenceProducerOutcome.Failed' in lines[targets[0] - 1], 'producer-source-arm')
    expected = {str(targets[0]), str(targets[0] + 1)}
    hits = [value for module in report.values() for path, types in module.items()
            if Path(path).name == 'EvidenceRestrictedCoverageProducer.cs'
            for methods in types.values() for name, method in methods.items() if 'RunAsync' in name
            for line, value in method['Lines'].items() if line in expected]
    require(any(type(value) is int and value > 0 for value in hits), 'producer-failure-arm-hit')


def execute(args):
    r = driver()
    end = time.monotonic() + 840
    originals = strict_json((args.evidence / 'original-supervision.json').read_bytes())
    require(originals['source'] == args.source and set(originals['sdk_files']) == set(NAMES), 'source-originals')
    instrumented = image_rows(args.sdk_tool)
    require(all(instrumented[name] != originals['sdk_files'][name] for name in NAMES if name.endswith('.dll')), 'instrumentation')
    temporary = args.evidence.parent / 'checkpoint3-sdk-failure-work'
    temporary.mkdir(mode=0o700)  # Fresh, unretained working directory; an existing candidate is never adopted.
    case_args = argparse.Namespace(**vars(args), output=temporary, subject=args.workspace / '.github/issue779-checkpoint3/subject')
    case_args.prepared = args.prepared / 'payload-preparation.json'
    case_args.digests = args.evidence / 'registration-digests.json'
    tools, runtime = r.stage(case_args, end, sdk_only=True)
    result = r.execute(case_args, 'sdk-test-failure', tools, runtime, end)
    require(result.get('passed') is True and result.get('groups_empty') is True and result.get('final_groups_empty') is True, 'original-control')
    require(result.get('root_exit') == 1 and len(result.get('worker_measurements', [])) == 1, 'natural-failed-worker')
    require_worker(result['worker_measurements'][0], result['sdk_failure']['worker'], instrumented)
    result.update({'schema': 'issue779-sdk-failed-producer-v1', 'source': args.source,
                   'sdk_original': originals['sdk_files'], 'sdk_instrumented': instrumented, 'finalized': False})
    r.write(temporary / 'provisional.json', r.canonical(result))


def finalize(args):
    r = driver()
    temporary = args.evidence.parent / 'checkpoint3-sdk-failure-work'
    # Collector and original owner streams are combined only AFTER both writers have joined.
    # This fixed private diagnostic is not a source of native outcome or counter authority.
    log = args.evidence / 'sdk-test-failure-diagnostics.log'
    owner_log = temporary / 'sdk-test-failure-owner.stderr'
    if owner_log.is_file():
        require(not owner_log.is_symlink() and owner_log.stat().st_size <= r.MAX_LOG, 'owner-log-bound')
        data = owner_log.read_bytes()
        fd = os.open(log, os.O_WRONLY | os.O_APPEND | os.O_NOFOLLOW | os.O_CLOEXEC)
        try:
            before = os.fstat(fd)
            separator = b'\nOriginal CSharp owner stderr (private):\n'
            require(stat.S_ISREG(before.st_mode) and before.st_nlink == 1
                    and before.st_size + len(separator) + len(data) <= r.MAX_LOG, 'combined-log-bound')
            body = memoryview(separator + data)
            while body:
                count = os.write(fd, body)
                require(count > 0, 'diagnostic-write')
                body = body[count:]
        finally:
            os.close(fd)
    require(args.collector_exit == 0, 'collector-exit')
    require(owner_log.is_file() and not owner_log.is_symlink(), 'original-owner-log')
    originals = strict_json((args.evidence / 'original-supervision.json').read_bytes())
    result = strict_json((temporary / 'provisional.json').read_bytes())
    require(result['source'] == args.source == originals['source'] and result['sdk_original'] == originals['sdk_files'], 'final-source')
    require(image_rows(args.sdk_tool) == originals['sdk_files'], 'restored-originals')
    report = args.evidence / 'native-sdk-failure.json'
    require(report.is_file() and not report.is_symlink() and 0 < report.stat().st_size <= REPORT_LIMIT, 'completed-report')
    capture = strict_json((args.evidence / 'source.json').read_bytes())
    require(capture['source'] == args.source, 'report-source')
    require_failed_report(report.read_bytes(), args.workspace, capture)
    result.update({'finalized': True, 'restored_originals': True,
                   'coverage_sha256': hashlib.sha256(report.read_bytes()).hexdigest(), 'coverage_bytes': report.stat().st_size,
                   'diagnostics_sha256': hashlib.sha256(log.read_bytes()).hexdigest(), 'diagnostics_bytes': log.stat().st_size})
    output = args.evidence / 'native'
    output.mkdir(mode=0o700, exist_ok=True)
    r.write(output / 'sdk-test-failure.json', r.canonical(result))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('operation', choices=('run', 'finalize'))
    parser.add_argument('--collector-exit', type=int, default=0)
    for name in ('evidence', 'tool', 'sdk-tool', 'runtime', 'prepared', 'workspace'):
        parser.add_argument('--' + name, type=Path, required=True)
    for name in ('source', 'base'):
        parser.add_argument('--' + name, required=True)
    args = parser.parse_args()
    os.umask(0o077)
    require(os.geteuid() == 0 and platform.system() == 'Linux' and platform.machine() == 'x86_64', 'native-owner-platform')
    require(re.fullmatch('[0-9a-f]{40}', args.source) and re.fullmatch('[0-9a-f]{40}', args.base), 'revisions')
    require(all(getattr(args, name).is_absolute() for name in ('evidence', 'tool', 'sdk_tool', 'runtime', 'prepared', 'workspace')), 'absolute-inputs')
    (execute if args.operation == 'run' else finalize)(args)


if __name__ == '__main__':
    main()
