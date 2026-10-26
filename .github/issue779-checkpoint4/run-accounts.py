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
from xml.etree import ElementTree

from measurement_arguments import MODULES

VARIANTS = ('C4Accounts', 'C4AccountRollback')
BASE = '416b30919e6c4d03b634d9d3fe558884b02f0b91'
NAMES = tuple(module + suffix for module in MODULES for suffix in ('.dll', '.pdb'))
LIMIT = 1200
MAX_LOG = 8 * 1024 * 1024
MAX_REPORT = 32 * 1024 * 1024
LINEAGE_SCHEMA = 'issue779-c4-account-measurement-v2'
C3_CASES = ('normal', 'sdk-normal', 'readiness', 'factory-stall', 'cancel', 'stuck-descendant', 'disposal-failure')
GUARD_CASES = ('bundle-extra-directory', 'bundle-input-bytes', 'bundle-input-mode',
               'scratch-fifo', 'scratch-hardlink', 'scratch-oversize')
WITNESS_SOURCE = '.github/issue779-checkpoint4/measurement/LinuxAccountMeasurement.cs'
CURRENT_WITNESSES = {
    'C4Accounts': (
        'ForgeTrust.AppSurface.Evidence.Supervision.LinuxAccountMeasurement/<RunAsync>d__2',
        'internal static async Task<byte[]> RunAsync(string requestPath, CancellationToken originalToken)',
        '        return receipt;\n    }'),
    'C4AccountRollback': (
        'ForgeTrust.AppSurface.Evidence.Supervision.LinuxRunAccounts/<PrepareAccountMeasurementCollisionAsync>d__56',
        'private async Task PrepareAccountMeasurementCollisionAsync(LinuxRunAccountCommand command, CancellationToken token)',
        '        accountMeasurementSnapshot = await LinuxRunAccountNss.ReadAsync(_owner, token, includeApplication: false)\n'
        '            .ConfigureAwait(false);\n    }'),
}
FIELDS = set(('schema variant creation_succeeded generation managed_entry_sha256 native_acceptance '
              'consumer_started cleanup_identity_guards_checked shared_close_task closed_holder_rejected '
              'names_absent numeric_ids_absent '
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
    require(value['cleanup_identity_guards_checked'] is (None if rollback else True), 'receipt-cleanup-identity-guards')
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
            'files': image_hashes(args.tool), **prior_binding(args)}


def digest(body):
    return hashlib.sha256(body).hexdigest()


def image_binding(value):
    """Validate data bindings, never create an image or account capability."""
    for key in ('original', 'instrumented'):
        require(type(value[key]) is dict and set(value[key]) == set(NAMES)
                and all(type(item) is str and re.fullmatch('[0-9a-f]{64}', item)
                        for item in value[key].values()), 'prior-images')
    require(all(value['original'][name] != value['instrumented'][name]
                for name in NAMES if name.endswith('.dll')), 'prior-images')


def require_current_witness(modules, capture, variant):
    """Require a current-variant-only, source-bound standard Coverlet method hit.

    Raw source29 reports establish these exact async state-machine names. RunAsync
    is absent from the C3 predecessor; the collision method is absent from the
    C4Accounts predecessor. Both are physically declared in the private partial
    LinuxAccountMeasurement.cs, including the LinuxRunAccounts collision member.
    Compiler-name or source-layout changes reject until this explicit mapping is
    reviewed. This observes unchanged counters and never computes a merge/delta.
    """
    require(variant in CURRENT_WITNESSES, 'current-witness-variant')
    repo = Path(__file__).resolve().parents[2]
    source = read_file(repo / WITNESS_SOURCE, MAX_LOG)
    source_sha = digest(source)
    require(source_sha == capture['files'][WITNESS_SOURCE]['sha256'], 'current-witness-source')
    # The generated ordinal also depends on the other production partial's members.
    partial = 'Evidence/ForgeTrust.AppSurface.Evidence.Supervision/LinuxRunAccounts.cs'
    require(digest(read_file(repo / partial, MAX_LOG)) == capture['files'][partial]['sha256'], 'current-witness-source')
    text = source.decode('utf-8', 'strict')
    declaring, signature, ending = CURRENT_WITNESSES[variant]
    require(text.count(signature) == 1 and text.count(ending) == 1, 'current-witness-mapping')
    first = text.index(signature)
    last = text.index(ending)
    require(first < last, 'current-witness-mapping')
    low = text[:first].count('\n') + 1
    high = text[:last + len(ending)].count('\n') + 1
    selected = [value for name, value in modules.items() if Path(name).name == 'ForgeTrust.AppSurface.Evidence.Supervision.dll']
    require(len(selected) == 1, 'current-witness-module')
    documents = [types for path, types in selected[0].items()
                 if path.replace('\\', '/') == WITNESS_SOURCE
                 or path.replace('\\', '/').endswith('/' + WITNESS_SOURCE)]
    require(len(documents) == 1, 'current-witness-document')
    method = 'System.Void ' + declaring + '::MoveNext()'
    details = documents[0].get(declaring, {}).get(method)
    require(type(details) is dict and type(details.get('Lines')) is dict, 'current-witness-method')
    require(any(type(line) is str and re.fullmatch('[1-9][0-9]*', line)
                and low <= int(line) <= high and type(count) is int and count > 0
                for line, count in details['Lines'].items()), 'current-witness-hit')
    return {'document': WITNESS_SOURCE, 'declaring_type': declaring, 'method': method,
            'source_sha256': source_sha, 'partial_source_sha256': capture['files'][partial]['sha256']}


def prior_binding(args):
    """Revalidate immutable prior bytes at bind, target entry and finalization.

    Only Coverlet consumes/merges the report. This observer checks source/receipt
    lineage and returns hashes; it never rewrites or combines report counters.
    """
    require((args.variant, args.prior_kind) in (('C4Accounts', 'c3'), ('C4AccountRollback', 'accounts')), 'prior-order')
    expected = ('issue779-checkpoint3-application-' if args.prior_kind == 'c3'
                else 'issue779-c4-accounts-C4Accounts-')
    require(re.fullmatch(re.escape(expected) + '[1-9][0-9]*-[1-9][0-9]*', args.prior_artifact_name), 'prior-artifact')
    current = read_file(args.evidence / 'source.json', MAX_LOG)
    source = read_file(args.merge_source, MAX_LOG)
    require(source == current, 'prior-source-bytes')
    capture = strict_json(source)
    require(capture['schema'] == 'issue779-checkpoint2-source-v1' and capture['source'] == args.source
            and type(capture['count']) is int and capture['count'] > 0
            and capture['count'] == len(capture['files']), 'prior-source')
    report = read_file(args.merge_report, MAX_REPORT)
    require(report, 'prior-report')
    modules = json.loads(report)  # Case-sensitive Coverlet module/class names.
    require(type(modules) is dict and len(modules) == len(MODULES)
            and {Path(name).name for name in modules} == {name + '.dll' for name in MODULES}, 'prior-modules')
    binding_body = read_file(args.merge_binding, MAX_LOG)
    value = strict_json(binding_body)
    native_body = read_file(args.merge_native, MAX_LOG)
    native = strict_json(native_body)
    require(value['source'] == args.source and value['collector'] == 'coverlet.console/10.0.1'
            and value['source_capture_sha256'] == digest(source)
            and value['coverage_sha256'] == digest(report)
            and value['native_receipt_sha256'] == digest(native_body), 'prior-binding')
    image_binding(value)
    root_trx_sha = None
    if args.prior_kind == 'c3':
        require(value['schema'] == 'issue779-native-instrumentation-v1', 'prior-schema')
        require(native['schema'] == 'issue779-csharp-checkpoint3-native-v1'
                and native['source'] == args.source and native['base'] == BASE
                and type(native['exit']) is int and native['exit'] == 0
                and native['trusted_eligibility'] is False, 'prior-native')
        for key, expected_cases in (('cases', C3_CASES), ('workspace_guards', GUARD_CASES)):
            rows = native[key]
            require(type(rows) is list and tuple(row['case'] for row in rows) == expected_cases
                    and all(row['passed'] is True and row['groups_empty'] is True
                            and row['final_groups_empty'] is True and row['native_acceptance'] is False
                            for row in rows), 'prior-native')
        require(args.merge_root_trx is not None, 'prior-root')
        trx = read_file(args.merge_root_trx, MAX_LOG)
        root_trx_sha = digest(trx)
        root_tests = value['root_file_tests']
        require(root_tests['trx_sha256'] == root_trx_sha and type(root_tests['executed']) is int
                and root_tests['executed'] == 41 and type(root_tests['passed']) is int
                and root_tests['passed'] == 41 and root_tests['native_acceptance'] is False, 'prior-root')
        ns = '{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}'
        document = ElementTree.fromstring(trx)
        counters = document.find('.//' + ns + 'Counters')
        rows = document.findall('.//' + ns + 'UnitTestResult')
        require(counters is not None and all(counters.get(key) == '41' for key in ('total', 'executed', 'passed'))
                and all(counters.get(key, '0') == '0' for key in ('failed', 'error', 'notExecuted', 'warning'))
                and len(rows) == 41 and all(row.get('outcome') == 'Passed' for row in rows), 'prior-root')
        prior_variant = 'C3'
    else:
        require(args.merge_root_trx is None and value['schema'] == LINEAGE_SCHEMA
                and value['variant'] == 'C4Accounts' and value['native_acceptance'] is False
                and value['prior_kind'] == 'c3' and value['prior_variant'] == 'C3', 'prior-schema')
        for key in ('prior_coverage_sha256', 'prior_input_binding_sha256', 'prior_source_capture_sha256',
                    'prior_native_receipt_sha256', 'prior_root_trx_sha256'):
            require(type(value[key]) is str and re.fullmatch('[0-9a-f]{64}', value[key]), 'prior-lineage')
        require(value['prior_source_capture_sha256'] == digest(source)
                and re.fullmatch('issue779-checkpoint3-application-[1-9][0-9]*-[1-9][0-9]*',
                                 value['prior_artifact_name']), 'prior-lineage')
        require(native['schema'] == 'issue779-c4-account-driver-v1' and native['source'] == args.source
                and native['variant'] == 'C4Accounts' and native['passed'] is True
                and native['groups_empty'] is True and native['final_groups_empty'] is True
                and native['native_acceptance'] is False
                and type(native['wrapper_exit']) is int and native['wrapper_exit'] == 0, 'prior-native')
        validate_receipt(native['receipt'], 'C4Accounts', native['generation'],
                         value['instrumented']['ForgeTrust.AppSurface.Cli.dll'])
        require(value['current_witness'] == require_current_witness(modules, capture, 'C4Accounts'), 'prior-current-witness')
        prior_variant = 'C4Accounts'
    return {'prior_kind': args.prior_kind, 'prior_variant': prior_variant,
            'prior_artifact_name': args.prior_artifact_name,
            'prior_coverage_sha256': digest(report), 'prior_input_binding_sha256': digest(binding_body),
            'prior_source_capture_sha256': digest(source), 'prior_native_receipt_sha256': digest(native_body),
            'prior_root_trx_sha256': root_trx_sha}


def validate_instrumentation(original, instrumented, source, variant):
    for value in (original, instrumented):
        require(value['source'] == source and value['variant'] == variant
                and value['collector'] == 'coverlet.console/10.0.1' and set(value['files']) == set(NAMES)
                and all(type(digest) is str and re.fullmatch('[0-9a-f]{64}', digest) for digest in value['files'].values()), 'image-binding')
    require(original['source_capture_sha256'] == instrumented['source_capture_sha256']
            and all(instrumented['files'][name] != original['files'][name] for name in NAMES if name.endswith('.dll')), 'instrumentation')
    require({key: value for key, value in original.items() if key.startswith('prior_')}
            == {key: value for key, value in instrumented.items() if key.startswith('prior_')}, 'prior-unchanged')


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
    require(native['schema'] == 'issue779-c4-account-driver-v1' and native['passed'] is True
            and native['groups_empty'] is True and native['final_groups_empty'] is True
            and native['source'] == args.source and native['variant'] == args.variant
            and native['native_acceptance'] is False
            and type(native['wrapper_exit']) is int and native['wrapper_exit'] == 0, 'native-result')
    validate_receipt(native['receipt'], args.variant, native['generation'], instrumented['files']['ForgeTrust.AppSurface.Cli.dll'])
    report = read_file(args.evidence / 'native-accounts.json', MAX_REPORT)
    modules = json.loads(report)
    require(len(modules) == len(MODULES) and {Path(name).name for name in modules} == {name + '.dll' for name in MODULES}, 'report-modules')
    capture = strict_json(read_file(args.evidence / 'source.json', MAX_LOG))
    witness = require_current_witness(modules, capture, args.variant)
    prior = prior_binding(args)
    require(prior == {key: value for key, value in original.items() if key.startswith('prior_')}, 'prior-unchanged')
    write(args.evidence / 'measurement-binding.json', {'schema': LINEAGE_SCHEMA,
          'source': args.source, 'variant': args.variant, 'collector': original['collector'], 'original': original['files'],
          'instrumented': instrumented['files'], 'native_acceptance': False,
          'source_capture_sha256': original['source_capture_sha256'],
          'coverage_sha256': hashlib.sha256(report).hexdigest(),
          'native_receipt_sha256': hashlib.sha256(read_file(args.evidence / 'native-accounts-receipt.json', MAX_LOG)).hexdigest(),
          'current_witness': witness, **prior})


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('operation', choices=('bind', 'run', 'finalize'))
    parser.add_argument('--variant', choices=VARIANTS, required=True)
    for key in ('tool', 'runtime', 'evidence'):
        parser.add_argument('--' + key, type=Path, required=True)
    parser.add_argument('--source', required=True)
    parser.add_argument('--prior-kind', choices=('c3', 'accounts'), required=True)
    parser.add_argument('--prior-artifact-name', required=True)
    for key in ('merge-report', 'merge-binding', 'merge-source', 'merge-native'):
        parser.add_argument('--' + key, type=Path, required=True)
    parser.add_argument('--merge-root-trx', type=Path)
    parser.add_argument('--collector-exit', type=int, default=0)
    args = parser.parse_args()
    os.umask(0o077)
    require(re.fullmatch('[0-9a-f]{40}', args.source)
            and all(getattr(args, key).is_absolute() for key in ('tool', 'runtime', 'evidence',
                     'merge_report', 'merge_binding', 'merge_source', 'merge_native'))
            and (args.merge_root_trx is None or args.merge_root_trx.is_absolute()), 'arguments')
    if args.operation == 'bind':
        write(args.evidence / 'account-original.json', binding(args))
    elif args.operation == 'run':
        raise SystemExit(run(args))
    else:
        finalize(args)


if __name__ == '__main__':
    main()
