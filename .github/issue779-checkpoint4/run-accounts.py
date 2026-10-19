"""Preparation for independent genuine C4 account controls; Python issues no native authority.

API: bind -> pinned standard Coverlet target run -> finalize. Each job selects exactly one
compiled variant. C# opens the protected input, authenticates the owner, resolves its empty plan,
owns utilities and proves strict NSS absence. This driver validates those actual returned data;
portable receipt tests cannot establish execution or account deletion. No report counters are edited.
"""
from pathlib import Path
import argparse
import datetime
import hashlib
import importlib.util
import json
import os
import platform
import re
import stat
import subprocess
import sys
import time
import uuid

from measurement_arguments import MODULES

VARIANTS = ('C4Accounts', 'C4AccountRollback')
BASE = '416b30919e6c4d03b634d9d3fe558884b02f0b91'
NAMES = tuple(module + suffix for module in MODULES for suffix in ('.dll', '.pdb'))
LIMIT = 1200
MAX_LOG = 8 * 1024 * 1024
FIELDS = set(('schema variant creation_succeeded generation managed_entry_sha256 native_acceptance '
              'consumer_started shared_close_task closed_holder_rejected names_absent numeric_ids_absent '
              'utilities settled_utilities failed_utilities close_completed quarantined reservation_failed '
              'identities_published original_handles_closed ids operation_failure').split())


def require(value, category):
    if not value:
        raise RuntimeError('account-control-' + category)


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(',', ':')).encode('utf-8')


def strict_json(data):
    def pairs(items):
        result, folded = {}, set()
        for key, value in items:
            require(key.casefold() not in folded, 'json-members')
            folded.add(key.casefold())
            result[key] = value
        return result
    return json.loads(data, object_pairs_hook=pairs)


def read_file(path, maximum):
    """Read one stable regular single-link leaf, never adopting symlinks/FIFOs or partial publication."""
    fd = os.open(path, os.O_RDONLY | os.O_CLOEXEC | os.O_NOFOLLOW | os.O_NONBLOCK)
    signature = lambda m: (m.st_dev, m.st_ino, m.st_mode, m.st_uid, m.st_gid, m.st_nlink,
                           m.st_size, m.st_mtime_ns, m.st_ctime_ns)
    try:
        before = os.fstat(fd)
        require(stat.S_ISREG(before.st_mode) and before.st_nlink == 1 and 0 <= before.st_size <= maximum, 'file-bound')
        body = bytearray()
        while len(body) <= before.st_size:
            part = os.read(fd, min(65536, before.st_size + 1 - len(body)))
            if not part:
                break
            body.extend(part)
        require(len(body) == before.st_size and signature(before) == signature(os.fstat(fd))
                == signature(path.lstat()), 'file-stability')
        return bytes(body)
    finally:
        os.close(fd)


def write(path, value):
    with path.open('xb') as target:
        target.write(canonical(value))
    path.chmod(0o600)


def image_hashes(tool):
    rows = {}
    for name in NAMES:
        body = read_file(tool / name, 32 * 1024 * 1024)
        require(body, 'module-empty')
        rows[name] = hashlib.sha256(body).hexdigest()
    return rows


def helpers():
    path = Path(__file__).resolve().parents[1] / 'issue779-checkpoint3/run-native.py'
    spec = importlib.util.spec_from_file_location('account_control_c3', path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def policy():
    """Same valid empty Targeted / nonempty conservative shape as EvidenceEmptyObservationPlanTests."""
    return {'Id': 'checkpoint4-accounts', 'Version': '1', 'ConservativeProfileId': 'conservative',
            'Profiles': [{'Id': 'accounts-empty', 'Scope': 'Targeted', 'Resources': [], 'Producers': [], 'Obligations': []},
                         {'Id': 'conservative', 'Scope': 'Release', 'Resources': [
                             {'Id': 'build', 'Readiness': 'completion', 'DeadlineSeconds': 1, 'Requires': []}],
                          'Producers': [], 'Obligations': []}],
            'Rules': [{'Id': 'docs', 'Pattern': 'docs/**', 'ProfileId': 'accounts-empty'}]}


def request_data(tool, runtime, subject, source, remaining):
    require(1110 < remaining <= LIMIT, 'owner-reserve')
    return {'schema': 'evidence-supervisor-linux-v1', 'mode': 'observation',
            'tool_root': str(tool), 'runtime_root': str(runtime), 'runtime_host': str(runtime / 'dotnet'),
            'entry_path': str(tool / 'ForgeTrust.AppSurface.Cli.dll'), 'policy_file': str(tool / 'fixture-policy.json'),
            'subject_root': str(subject), 'base_revision': BASE, 'subject_revision': source,
            'workflow_identity': 'issue779-c4-accounts', 'paths': ['docs/accounts.md'],
            'observation_profile_ids': ['accounts-empty'], 'observation_producer_ids': [],
            'job_deadline_utc': (datetime.datetime.now(datetime.timezone.utc) + datetime.timedelta(seconds=remaining)).isoformat().replace('+00:00', 'Z'),
            'admission_seconds': 30, 'start_seconds': 120, 'collection_seconds': 60,
            'cleanup_seconds': 120, 'stopping_seconds': 10}


def validate_receipt(value, variant, generation, entry):
    """Validate actual receipt data only. No holder, account, backend or positive capability is created."""
    require(type(value) is dict and set(value) == FIELDS and value['schema'] == 'issue779-c4-accounts-v1'
            and value['variant'] == variant and value['generation'] == generation
            and value['managed_entry_sha256'] == entry, 'receipt-binding')
    require(re.fullmatch('[0-9a-f]{32}', generation) and re.fullmatch('[0-9a-f]{64}', entry), 'receipt-binding')
    for key in ('names_absent', 'numeric_ids_absent', 'close_completed', 'original_handles_closed'):
        require(value[key] is True, 'receipt-cleanup')
    for key in ('native_acceptance', 'consumer_started', 'quarantined'):
        require(value[key] is False, 'receipt-negative')
    ids = value['ids']
    require(type(ids) is dict and set(ids) == {'worker_uid', 'subject_uid', 'worker_gid', 'subject_gid', 'results_gid'}, 'receipt-ids')
    for keys in (('worker_uid', 'subject_uid'), ('worker_gid', 'subject_gid', 'results_gid')):
        values = [ids[key] for key in keys]
        require(all(type(number) is int and 0 < number < 4294967295 for number in values)
                and len(set(values)) == len(values), 'receipt-ids')
    rollback = variant == 'C4AccountRollback'
    require(variant in VARIANTS and value['creation_succeeded'] is (not rollback)
            and value['identities_published'] is (not rollback) and value['reservation_failed'] is rollback, 'receipt-variant')
    count = value['utilities']
    require(type(count) is int and (7 <= count <= 9 if rollback else 6 <= count <= 8)
            and type(value['settled_utilities']) is int and value['settled_utilities'] == count
            and type(value['failed_utilities']) is int and value['failed_utilities'] == int(rollback), 'receipt-utilities')
    if not rollback:
        require(value['shared_close_task'] is True and value['closed_holder_rejected'] is True
                and value['operation_failure'] is None, 'receipt-close')
    else:
        require(value['shared_close_task'] is None and value['closed_holder_rejected'] is None, 'receipt-close')
        failure = value['operation_failure']
        require(type(failure) is dict and set(failure) == {'account_failure', 'preparation_stage', 'utility_stage',
                'operation', 'error_kind', 'diagnostic_code', 'exec_main_code', 'exec_main_status', 'dbus_category'}, 'receipt-failure')
        require(failure['account_failure'] == 'OperationFailed' and failure['preparation_stage'] == 'UtilityExecute'
                and failure['utility_stage'] == 'TerminalCheck' and failure['operation'] == 'CreateResultsGroup'
                and failure['error_kind'] == 'Admission' and failure['diagnostic_code'] == 'ASEVD410'
                and type(failure['exec_main_code']) is int and failure['exec_main_code'] == 1
                and type(failure['exec_main_status']) is int and 1 <= failure['exec_main_status'] <= 255
                and failure['dbus_category'] is None, 'receipt-failure')


def binding(args):
    capture_body = read_file(args.evidence / 'source.json', MAX_LOG)
    capture = strict_json(capture_body)
    require(capture['source'] == args.source, 'source')
    return {'source': args.source, 'variant': args.variant, 'collector': 'coverlet.console/10.0.1',
            'source_capture_sha256': hashlib.sha256(capture_body).hexdigest(),
            'files': image_hashes(args.tool)}


def validate_instrumentation(original, instrumented, source, variant):
    for value in (original, instrumented):
        require(value['source'] == source and value['variant'] == variant
                and value['collector'] == 'coverlet.console/10.0.1' and set(value['files']) == set(NAMES)
                and all(type(digest) is str and re.fullmatch('[0-9a-f]{64}', digest) for digest in value['files'].values()), 'image-binding')
    require(original['source_capture_sha256'] == instrumented['source_capture_sha256']
            and all(instrumented['files'][name] != original['files'][name] for name in NAMES if name.endswith('.dll')), 'instrumentation')


def run(args):
    require(os.geteuid() == 0 and platform.system() == 'Linux' and platform.machine() == 'x86_64', 'platform')
    h = helpers()
    end = time.monotonic() + LIMIT
    original = strict_json(read_file(args.evidence / 'account-original.json', MAX_LOG))
    instrumented = binding(args)
    validate_instrumentation(original, instrumented, args.source, args.variant)
    write(args.evidence / 'account-instrumented.json', instrumented)
    generation = uuid.uuid4().hex
    root = Path('/var/lib') / ('issue779-c4-accounts-' + generation)
    result = {'schema': 'issue779-c4-account-driver-v1', 'source': args.source, 'variant': args.variant,
              'generation': generation, 'passed': False, 'native_acceptance': False}
    process = None
    units = ['appsurface-evidence-' + role + '-' + generation + '.service' for role in ('owner', 'worker', 'producer', 'application')]
    try:
        h.c2.create_traversable_root(root)
        tool, runtime, subject = root / 'tool', root / 'runtime', root / 'subject'
        inventory = {'tool': h.c2.copy_tree(args.tool, tool), 'runtime': h.c2.copy_tree(args.runtime, runtime)}
        h.c2.write(tool / 'fixture-policy.json', canonical(policy()), 0o444)
        subject.mkdir(mode=0o700)
        (subject / 'docs').mkdir(mode=0o700)
        h.c2.write(subject / 'docs/accounts.md', b'Private account control; no build or consumer.\n', 0o444)
        for parent in (tool, runtime, subject):
            for path in (parent, *parent.rglob('*')):
                metadata = path.lstat()
                require(stat.S_ISDIR(metadata.st_mode) or stat.S_ISREG(metadata.st_mode), 'staged-type')
                os.chown(path, 0, 0, follow_symlinks=False)
                path.chmod(0o555 if stat.S_ISDIR(metadata.st_mode) or metadata.st_mode & 0o111 else 0o444)
        require(image_hashes(tool) == instrumented['files'], 'staged-images')
        require(read_file(runtime / 'dotnet', 64 * 1024 * 1024)[:4] == b'\x7fELF', 'native-runtime')
        write(args.evidence / 'staging.json', inventory)
        request = request_data(tool, runtime, subject, args.source, end - time.monotonic())
        request_path = root / 'request.json'
        h.c2.write(request_path, canonical(request))
        guard = Path('/run/appsurface-evidence-owners')
        guard.mkdir(mode=0o700, exist_ok=True)
        metadata = guard.lstat()
        require(stat.S_ISDIR(metadata.st_mode) and metadata.st_uid == metadata.st_gid == 0
                and stat.S_IMODE(metadata.st_mode) == 0o700, 'guard-parent')
        marker = guard / generation
        marker.mkdir(mode=0o700)
        h.c2.write(marker / 'armed', (generation + '\n').encode())
        stdout, stderr = args.evidence / 'owner.stdout', args.evidence / 'owner.stderr'
        observe_end = end - 60  # The existing cumulative bound retains containment reserve.
        with stdout.open('xb') as out, stderr.open('xb') as err:
            process = subprocess.Popen(h.owner_command(runtime, tool, units[0], marker / 'armed', request_path),
                                       stdin=subprocess.DEVNULL, stdout=out, stderr=err, start_new_session=True)
            while time.monotonic() < observe_end and process.poll() is None:
                require(stdout.stat().st_size + stderr.stat().st_size <= MAX_LOG, 'log-bound')
                time.sleep(.1)
            require(process.poll() is not None, 'owner-terminal')
            result['wrapper_exit'] = process.wait(timeout=1)
            require(result['wrapper_exit'] == 0, 'owner-exit')
        require(stdout.stat().st_size + stderr.stat().st_size <= MAX_LOG, 'log-bound')
        require(h.c2.groups_empty(units), 'groups-empty')
        require(read_file(stderr, MAX_LOG) == b'', 'owner-diagnostic')
        body = read_file(stdout, 4097)  # The CLI adds one LF to the C# receipt's 4096-byte maximum.
        require(body.endswith(b'\n') and len(body[:-1]) <= 4096, 'receipt-size')
        value = strict_json(body)
        validate_receipt(value, args.variant, generation, instrumented['files']['ForgeTrust.AppSurface.Cli.dll'])
        for path in (Path('/run/appsurface-evidence-' + generation), Path('/run/appsurface-app-' + generation),
                     Path('/run/appsurface-evidence-producer-data-' + generation)):
            require(not os.path.lexists(path), 'no-consumer-storage')
        result.update({'passed': True, 'groups_empty': True, 'receipt': value,
                       'request_sha256': hashlib.sha256(canonical(request)).hexdigest(),
                       'policy_sha256': hashlib.sha256(canonical(policy())).hexdigest(),
                       'owner_stdout_sha256': hashlib.sha256(body).hexdigest()})
    except Exception as error:
        result['failure_kind'] = type(error).__name__ if type(error).__name__ in ('RuntimeError', 'TimeoutExpired', 'OSError', 'ValueError') else 'other'
        closed = {'account-control-' + name for name in ('file-bound', 'file-stability', 'owner-reserve', 'staged-type',
                  'staged-images', 'native-runtime', 'guard-parent', 'log-bound', 'owner-terminal', 'owner-exit',
                  'groups-empty', 'owner-diagnostic', 'receipt-size', 'receipt-binding', 'receipt-cleanup',
                  'receipt-negative', 'receipt-ids', 'receipt-variant', 'receipt-utilities', 'receipt-close',
                  'receipt-failure', 'no-consumer-storage', 'json-members')}
        result['failure_category'] = str(error) if isinstance(error, RuntimeError) and str(error) in closed else 'other'
    finally:
        if process is not None and (process.poll() is None or not result['passed']):
            try:
                import workspace_guards
                workspace_guards.contain_units(units, process, end)
            except Exception:
                result['containment_failed'] = True
                result['passed'] = False
        try:
            result['final_groups_empty'] = h.c2.groups_empty(units)
        except Exception:
            result['final_groups_empty'] = False
        if not result['final_groups_empty']:
            result['passed'] = False
        if process is not None and process.returncode is not None:
            result['wrapper_exit'] = process.returncode
        write(args.evidence / 'native-accounts-receipt.json', result)
    return 0 if result['passed'] else 1


def finalize(args):
    require(args.collector_exit == 0, 'collector-exit')
    original = strict_json(read_file(args.evidence / 'account-original.json', MAX_LOG))
    require(binding(args) == original, 'restored-originals')
    native = strict_json(read_file(args.evidence / 'native-accounts-receipt.json', MAX_LOG))
    instrumented = strict_json(read_file(args.evidence / 'account-instrumented.json', MAX_LOG))
    validate_instrumentation(original, instrumented, args.source, args.variant)
    require(native['passed'] is True and native['final_groups_empty'] is True and native['source'] == args.source
            and native['variant'] == args.variant and native['wrapper_exit'] == 0, 'native-result')
    validate_receipt(native['receipt'], args.variant, native['generation'], instrumented['files']['ForgeTrust.AppSurface.Cli.dll'])
    report = read_file(args.evidence / 'native-accounts.json', MAX_LOG)
    modules = json.loads(report)
    require(len(modules) == 6 and {Path(name).name for name in modules} == {name + '.dll' for name in MODULES}, 'report-modules')
    for expected in ('LinuxRunAccounts.cs', 'LinuxOwnerActivation.cs'):
        hits = [count for module in modules.values() for path, types in module.items() if Path(path).name == expected
                for methods in types.values() for method in methods.values() for count in method['Lines'].values()]
        require(any(type(count) is int and count > 0 for count in hits), 'report-account-hits')
    write(args.evidence / 'measurement-binding.json', {'schema': 'issue779-c4-account-measurement-v1',
          'source': args.source, 'variant': args.variant, 'collector': original['collector'], 'original': original['files'],
          'instrumented': instrumented['files'], 'native_acceptance': False,
          'source_capture_sha256': original['source_capture_sha256'],
          'coverage_sha256': hashlib.sha256(report).hexdigest(),
          'native_receipt_sha256': hashlib.sha256(read_file(args.evidence / 'native-accounts-receipt.json', MAX_LOG)).hexdigest()})


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('operation', choices=('bind', 'run', 'finalize'))
    parser.add_argument('--variant', choices=VARIANTS, required=True)
    for key in ('tool', 'runtime', 'evidence'):
        parser.add_argument('--' + key, type=Path, required=True)
    parser.add_argument('--source', required=True)
    parser.add_argument('--collector-exit', type=int, default=0)
    args = parser.parse_args()
    os.umask(0o077)
    require(re.fullmatch('[0-9a-f]{40}', args.source)
            and all(getattr(args, key).is_absolute() for key in ('tool', 'runtime', 'evidence')), 'arguments')
    if args.operation == 'bind':
        write(args.evidence / 'account-original.json', binding(args))
    elif args.operation == 'run':
        raise SystemExit(run(args))
    else:
        finalize(args)


if __name__ == '__main__':
    main()
