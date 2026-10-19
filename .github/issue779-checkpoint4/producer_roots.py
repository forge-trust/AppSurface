"""Standalone standard C2/None root measurement, never native admission or worker import.

Closed API: bind -> outer collector target(inner) -> inner target(driver) -> finalize.
Both images are instrumented before the unchanged driver stages either one. The outer
collector merges only the completed inner report. An optional closed v2 predecessor
is validated at every boundary and consumed only by the inner standard collector.
"""
from pathlib import Path, PurePosixPath
import argparse
import hashlib
import importlib.util
import json
import os
import re
import runpy
import stat
import subprocess
import sys

from measurement_arguments import MODULES, arguments

BASE = '416b30919e6c4d03b634d9d3fe558884b02f0b91'
LEAF = 8 * 1024 * 1024
TOTAL = 32 * 1024 * 1024
REPORT = 32 * 1024 * 1024
NAMES = tuple(m + suffix for m in MODULES for suffix in ('.dll', '.pdb'))
CASES = ('normal', 'test-failure', 'cancel', 'quota', 'ignored-cancel', 'artifact-mutation', 'cleanup-failure')
FILES = ('producer-roots-original.json', 'producer-roots-instrumented.json',
         'producer-roots-inner-exit.json', 'producer-roots-target.json',
         'native-producer-roots-c2.json', 'native-producer-roots.json', 'producer-roots-binding.json')
ROOT = Path(__file__).resolve().parents[2]
SELF = ROOT / '.github/issue779-checkpoint4/producer_roots.py'
DRIVER = ROOT / '.github/issue779-checkpoint2/run-native.py'
SUP = 'ForgeTrust.AppSurface.Evidence.Supervision'
MUTATION_DOC = 'Evidence/' + SUP + '/LinuxRetainedReportSet.cs'
QUOTA_DOC = 'Evidence/' + SUP + '/SupervisionRunOutputBudget.cs'
PRIOR_OPTIONS = ('prior_report', 'prior_binding', 'prior_source', 'prior_native', 'prior_artifact_name')
MERGE_ORDER = ('C3', 'C4Accounts', 'C4AccountRollback', 'C2', 'None')
ACCOUNT_DRIVER = '.github/issue779-checkpoint4/run-accounts.py'


def require(value, reason):
    if not value:
        raise ValueError('producer-roots-' + reason)


def digest(body):
    return hashlib.sha256(body).hexdigest()


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(',', ':'), allow_nan=False).encode()


def decode(body):
    def pairs(items):
        result = {}
        for key, value in items:
            require(key.casefold() not in {k.casefold() for k in result}, 'duplicate-member')
            result[key] = value
        return result
    return json.loads(body, object_pairs_hook=pairs,
                      parse_constant=lambda _: require(False, 'json-number'))


def read(path, maximum=LEAF):
    """Bounded stable no-follow single-link leaf; data observations grant no authority."""
    path = Path(path)
    fd = os.open(path, os.O_RDONLY | os.O_CLOEXEC | os.O_NOFOLLOW | os.O_NONBLOCK)
    signature = lambda s: (s.st_dev, s.st_ino, s.st_mode, s.st_uid, s.st_gid, s.st_nlink,
                           s.st_size, s.st_mtime_ns, s.st_ctime_ns)
    try:
        before = os.fstat(fd)
        require(stat.S_ISREG(before.st_mode) and before.st_nlink == 1
                and 0 <= before.st_size <= maximum, 'leaf')
        data = bytearray()
        while len(data) <= before.st_size:
            chunk = os.read(fd, min(65536, before.st_size + 1 - len(data)))
            if not chunk:
                break
            data.extend(chunk)
        require(len(data) == before.st_size and signature(before) == signature(os.fstat(fd))
                == signature(path.lstat()), 'leaf-changed')
        return bytes(data)
    finally:
        os.close(fd)


def write(path, value):
    body = canonical(value)
    require(len(body) <= LEAF, 'publication-bound')
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC, 0o600)
    with os.fdopen(fd, 'wb') as target:
        target.write(body)
        target.flush()
        os.fsync(target.fileno())


def image(tool):
    rows = {}
    for name in NAMES:
        body = read(tool / name, TOTAL)
        require(body, 'empty-module')
        rows[name] = digest(body)
    return rows


def source(args):
    body = read(args.evidence / 'source.json')
    value = decode(body)
    require(value['schema'] == 'issue779-checkpoint2-source-v1' and value['source'] == args.source
            and type(value['count']) is int and 0 < value['count'] <= 10000
            and value['count'] == len(value['files']), 'source-capture')
    for name, row in value['files'].items():
        path = PurePosixPath(name)
        require(not path.is_absolute() and path.parts and all(p not in ('', '.', '..') for p in path.parts)
                and str(path) == name and '\\' not in name, 'source-path')
        actual = ROOT / name
        require(digest(read(actual)) == row['sha256']
                and format(stat.S_IMODE(actual.lstat().st_mode), '04o') == row['mode'], 'source-bytes')
    for name in (str(SELF.relative_to(ROOT)), str(DRIVER.relative_to(ROOT)), MUTATION_DOC, QUOTA_DOC):
        require(name in value['files'], 'source-required')
    return body, value


def context(args):
    body, capture = source(args)
    smoke = read(args.evidence / 'package-smoke.json')
    packaged = decode(smoke)
    require(packaged['schema'] == 'issue779-packaged-consumer-v1' and packaged['source'] == args.source
            and type(packaged['exit']) is int and packaged['exit'] == 0
            and Path(packaged['tool_image']) == args.tool, 'package-image')
    value = {'source': args.source, 'base': args.base, 'source_capture_sha256': digest(body),
            'package_smoke_sha256': digest(smoke),
            'paths': {key: str(getattr(args, key)) for key in ('tool', 'mutation_tool', 'runtime', 'subject', 'evidence')}}
    prior = predecessor(args, body, capture)
    if prior is not None:
        value['external_predecessor'] = prior
    return value


def prior_enabled(args):
    """Require all five immutable predecessor inputs together; absence preserves v1."""
    supplied = [getattr(args, key, None) is not None for key in PRIOR_OPTIONS]
    require(all(supplied) or not any(supplied), 'prior-arguments')
    if not any(supplied):
        return False
    for key in PRIOR_OPTIONS[:-1]:
        path = getattr(args, key)
        require(isinstance(path, Path) and path.is_absolute(), 'prior-path')
        # Incoming leaves are not part of the outgoing bounded artifact inventory.
        require(not path.resolve().is_relative_to(args.evidence.resolve()), 'prior-outside-evidence')
    require(len({getattr(args, key).resolve() for key in PRIOR_OPTIONS[:-1]}) == 4, 'prior-distinct-leaves')
    require(type(args.prior_artifact_name) is str and re.fullmatch(
        'issue779-c4-accounts-C4AccountRollback-[1-9][0-9]*-[1-9][0-9]*', args.prior_artifact_name), 'prior-artifact')
    return True


def account_data():
    """Load only the fixed captured account driver's pure data validators, never its CLI."""
    spec = importlib.util.spec_from_file_location('producer_roots_account_data', ROOT / ACCOUNT_DRIVER)
    require(spec is not None and spec.loader is not None, 'account-data-module')
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def prior_contribution(modules, capture):
    """Reject inherited C2 declarations or quota hits, without manufacturing missing zeros.

    A missing quota method/point is accepted as absent, not recorded as a zero hit.
    A declared point must be exactly zero. Finalization still requires actual current
    positive witnesses. These comparisons do not subtract, edit or merge counters.
    """
    for mutation in (True, False):
        doc = MUTATION_DOC if mutation else QUOTA_DOC
        body = read(ROOT / doc)
        require(digest(body) == capture['files'][doc]['sha256'], 'prior-witness-source')
        typ = SUP + ('.LinuxRetainedReportSet' if mutation else '.SupervisionRunOutputBudget')
        method = ('System.Byte[] ' + typ + '::MutateRetainedReportForC2(System.Threading.CancellationToken)' if mutation
                  else 'System.Boolean ' + typ + '::Charge(System.Int32)')
        candidates = []
        for module, docs in modules.items():
            if Path(module).name == SUP + '.dll':
                for name, types in docs.items():
                    if mutation or name.replace('\\', '/') == doc or name.replace('\\', '/').endswith('/' + doc):
                        if typ in types and method in types[typ]:
                            candidates.append(types[typ][method])
        if mutation:
            require(not candidates, 'prior-c2-method-present')
        else:
            anchors = [n for n, line in enumerate(body.decode().splitlines(), 1)
                       if line.strip() == 'Fail(SupervisionRunOutputFailure.QuotaExceeded);']
            require(len(anchors) == 1 and len(candidates) <= 1, 'prior-quota-mapping')
            if candidates:
                points = [hits for line, hits in candidates[0]['Lines'].items() if int(line) == anchors[0]]
                require(len(points) <= 1 and all(type(hits) is int and hits == 0 for hits in points),
                        'prior-quota-positive')


def predecessor(args, current_source, capture):
    """Validate a completed rollback v2 packet, returning only the approved hash lineage.

    The immediate creation ancestry is structurally bound here. The caller's independent
    C# AccountMeasurementLineage joins its actual bytes back to raw C3 before any gate
    consumption. This reader cannot confer native acceptance or account authority.
    """
    if not prior_enabled(args):
        return None
    require(ACCOUNT_DRIVER in capture['files'], 'account-data-capture')
    source_body = read(args.prior_source)
    require(source_body == current_source, 'prior-source-bytes')
    require(capture['schema'] == 'issue779-checkpoint2-source-v1' and capture['source'] == args.source
            and type(capture['count']) is int and 0 < capture['count'] <= 10000
            and capture['count'] == len(capture['files']), 'prior-source')
    require(digest(read(ROOT / ACCOUNT_DRIVER)) == capture['files'][ACCOUNT_DRIVER]['sha256'], 'account-data-source')
    report_body = read(args.prior_report, REPORT)
    modules = report(report_body)
    binding_body = read(args.prior_binding)
    value = decode(binding_body)
    native_body = read(args.prior_native)
    observed = decode(native_body)
    require(value['schema'] == 'issue779-c4-account-measurement-v2'
            and value['source'] == args.source and value['variant'] == 'C4AccountRollback'
            and value['collector'] == 'coverlet.console/10.0.1' and value['native_acceptance'] is False
            and value['source_capture_sha256'] == digest(source_body)
            and value['coverage_sha256'] == digest(report_body)
            and value['native_receipt_sha256'] == digest(native_body), 'prior-binding')
    require(value['prior_kind'] == 'accounts' and value['prior_variant'] == 'C4Accounts'
            and value['prior_root_trx_sha256'] is None, 'prior-order')
    suffix = args.prior_artifact_name.removeprefix('issue779-c4-accounts-C4AccountRollback-')
    require(value['prior_artifact_name'] == 'issue779-c4-accounts-C4Accounts-' + suffix, 'prior-run-attempt')
    for key in ('prior_coverage_sha256', 'prior_input_binding_sha256', 'prior_source_capture_sha256',
                'prior_native_receipt_sha256'):
        require(type(value[key]) is str and re.fullmatch('[0-9a-f]{64}', value[key]), 'prior-lineage-hash')
    require(value['prior_source_capture_sha256'] == digest(source_body), 'prior-lineage-source')
    helpers = account_data()
    helpers.image_binding(value)
    require(observed['schema'] == 'issue779-c4-account-driver-v1' and observed['source'] == args.source
            and observed['variant'] == 'C4AccountRollback' and observed['passed'] is True
            and observed['groups_empty'] is True and observed['final_groups_empty'] is True
            and observed['native_acceptance'] is False
            and type(observed['wrapper_exit']) is int and observed['wrapper_exit'] == 0, 'prior-native')
    helpers.validate_receipt(observed['receipt'], 'C4AccountRollback', observed['generation'],
                             value['instrumented'][MODULES[0] + '.dll'])
    require(value['current_witness'] == helpers.require_current_witness(modules, capture, 'C4AccountRollback'),
            'prior-current-witness')
    prior_contribution(modules, capture)
    return {'kind': 'accounts-rollback', 'variant': 'C4AccountRollback',
            'artifact_name': args.prior_artifact_name, 'merge_order': list(MERGE_ORDER),
            'source_capture_sha256': digest(source_body), 'input_binding_sha256': digest(binding_body),
            'coverage_sha256': digest(report_body), 'native_receipt_sha256': digest(native_body)}


def bound(args):
    original = decode(read(args.evidence / FILES[0]))
    require(original['context'] == context(args), 'context-changed')
    return original


def common(args):
    keys = ('tool', 'mutation_tool', 'runtime', 'subject', 'evidence', 'source', 'base')
    if prior_enabled(args):
        keys += PRIOR_OPTIONS
    return [item for key in keys
            for item in ('--' + key.replace('_', '-'), str(getattr(args, key)))]


def console(args):
    return args.evidence.parent / 'checkpoint2-coverlet/.store/coverlet.console/10.0.1/coverlet.console/10.0.1/tools/net10.0/any/coverlet.console.dll'


def collector(args, inner):
    """Return argv, not shell text. The outer merges the inner's completed standard JSON."""
    target = ['-B', str(SELF), 'target', '--stage', 'driver' if inner else 'inner', *common(args)]
    values = [str(args.runtime / 'dotnet'), str(console(args)),
              str((args.mutation_tool if inner else args.tool) / (MODULES[0] + '.dll')),
              '--target', '/usr/bin/python3', '--targetargs', arguments(target),
              '--include', *('[' + m + ']*' for m in MODULES), '--include-test-assembly', '--format', 'json',
              '--output', str(args.evidence / (FILES[4] if inner else FILES[5]))]
    if not inner:
        values += ['--merge-with', str(args.evidence / FILES[4])]
    elif prior_enabled(args):
        values += ['--merge-with', str(args.prior_report)]
    return values


def bind(args):
    require(args.tool != args.mutation_tool and not (args.evidence / 'native').exists(), 'fresh-inputs')
    require(all(not os.path.lexists(args.evidence / name) for name in FILES), 'fresh-leaves')
    write(args.evidence / FILES[0], {'schema': 'issue779-producer-roots-original-v1', 'context': context(args),
          'original': {'None': image(args.tool), 'C2': image(args.mutation_tool)},
          'console_sha256': digest(read(console(args), TOTAL))})


def instrumented(args, original):
    maps = {'None': image(args.tool), 'C2': image(args.mutation_tool)}
    validate_instrumented(maps, original)
    return maps


def validate_instrumented(maps, original):
    require(set(maps) == {'None', 'C2'}, 'instrumented-variants')
    for variant, rows in maps.items():
        require(set(rows) == set(NAMES)
                and all(type(h) is str and re.fullmatch('[0-9a-f]{64}', h) for h in rows.values())
                and all(rows[m + '.dll'] != original['original'][variant][m + '.dll']
                                             for m in MODULES), 'instrumentation')


def target(args):
    original = bound(args)
    require(digest(read(console(args), TOTAL)) == original['console_sha256'], 'console-changed')
    if args.stage == 'inner':
        # Outer process has already instrumented None. Inner instruments C2 before driver staging.
        require(image(args.mutation_tool) == original['original']['C2'], 'inner-original')
        code = subprocess.run(collector(args, True), stdin=subprocess.DEVNULL, check=False).returncode
        if prior_enabled(args):
            require(original['context'] == context(args), 'context-changed-after-inner')
        write(args.evidence / FILES[2], {'inner_collector_exit': code,
              'original_sha256': digest(read(args.evidence / FILES[0]))})
        return code
    maps = instrumented(args, original)
    write(args.evidence / FILES[1], {'original_sha256': digest(read(args.evidence / FILES[0])), 'instrumented': maps})
    previous = sys.argv
    sys.argv = [str(DRIVER), '--tool', str(args.tool), '--mutation-tool', str(args.mutation_tool),
                '--runtime', str(args.runtime), '--subject', str(args.subject), '--output', str(args.evidence / 'native'),
                '--source', args.source, '--base', args.base]
    sys.path.insert(0, str(DRIVER.parent))
    code = 1
    try:
        try:
            runpy.run_path(str(DRIVER), run_name='__main__')
            code = 0
        except SystemExit as terminal:
            require(type(terminal.code) is int, 'driver-exit-shape')
            code = terminal.code
    finally:
        sys.argv = previous
        sys.path.pop(0)
        write(args.evidence / FILES[3], {'driver_exit': code,
              'instrumented_sha256': digest(read(args.evidence / FILES[1]))})
    return code


def native(value, head, base):
    """Validate the unchanged driver's returned data, including expected quarantined cleanup fault."""
    require(value['schema'] == 'issue779-csharp-checkpoint2-native-v1' and value['source'] == head
            and value['base'] == base and type(value['exit']) is int and value['exit'] == 0
            and value['native_acceptance'] is False and value['trusted_eligibility'] is False, 'native-header')
    rows = value['cases']
    require(type(rows) is list and [row['case'] for row in rows] == list(CASES), 'native-cases')
    generations = set()
    for row in rows:
        generation = row['generation']
        require(type(generation) is str and re.fullmatch('[0-9a-f]{32}', generation)
                and generation not in generations, 'native-generation')
        generations.add(generation)
        cleanup = row['case'] == 'cleanup-failure'
        require(all(row.get(key) is True for key in ('passed', 'groups_empty', 'final_groups_empty'))
                and row['native_acceptance'] is False and 'failure_category' not in row
                and row['accounts_absent'] is (not cleanup) and row['accounts_quarantined'] is cleanup,
                'native-cleanup')
        code = row['owner_terminal']['wrapper_exit']
        require(type(code) is int and (code == 0 if row['case'] == 'normal' else code != 0), 'native-exit')
        if cleanup:
            require(row.get('quarantined') is True, 'expected-cleanup-fault')
        if row['case'] in ('cancel', 'ignored-cancel'):
            require(row.get('cancel_sent') is True, 'cancel')
        if row['case'] == 'ignored-cancel':
            require(row.get('descendant_term_observed') is True, 'ignored-cancel')
        if row['case'] == 'quota':
            observed = row['output_observation']
            require(observed['quota_exceeded'] is True and type(observed['received_bytes']) is int
                    and observed['received_bytes'] > observed['received_byte_limit'] == 16 * 1024 * 1024, 'quota')
        if row['case'] == 'artifact-mutation':
            events = row['artifact_mutation']
            require(len(events) == 2 and events[0]['schema'] == 'evidence-c2-artifact-mutation-v1'
                    and events[0]['phase'] == 'mutated' and events[0]['original_sha256'] != events[0]['changed_sha256']
                    and all(type(events[0][key]) is str and re.fullmatch('[0-9a-f]{64}', events[0][key])
                            for key in ('original_sha256', 'changed_sha256'))
                    and type(events[0]['length']) is int and 0 < events[0]['length'] <= 1024 * 1024
                    and all(type(events[0][key]) is int and events[0][key] >= 0
                            for key in ('device_major', 'device_minor'))
                    and type(events[0]['inode']) is int and events[0]['inode'] > 0
                    and events[1] == {'schema': 'evidence-c2-artifact-mutation-v1', 'phase': 'read-rejected', 'returned_bytes': 0},
                    'mutation')
    return rows


def report(body):
    """Validate standard Coverlet shape only; never add, subtract or rewrite counters."""
    value = decode(body)
    require(type(value) is dict and {Path(k).name for k in value} == {m + '.dll' for m in MODULES}
            and len(value) == len(MODULES), 'report-modules')
    for docs in value.values():
        require(type(docs) is dict, 'report-documents')
        for types in docs.values():
            for methods in types.values():
                for data in methods.values():
                    require(type(data['Lines']) is dict and type(data['Branches']) is list, 'report-method')
                    for line, hits in data['Lines'].items():
                        require(line.isdecimal() and int(line) > 0 and type(hits) is int and 0 <= hits < 2**63, 'report-line')
                    for branch in data['Branches']:
                        require(set(branch) == {'Line', 'Offset', 'EndOffset', 'Path', 'Ordinal', 'Hits'}
                                and all(type(n) is int and 0 <= n < 2**63 for n in branch.values()), 'report-branch')
    return value


def witness(value, capture, mutation):
    doc = MUTATION_DOC if mutation else QUOTA_DOC
    text = read(ROOT / doc).decode().splitlines()
    marker = 'internal byte[] MutateRetainedReportForC2(CancellationToken token)' if mutation else 'Fail(SupervisionRunOutputFailure.QuotaExceeded);'
    anchors = [n for n, line in enumerate(text, 1) if line.strip() == marker]
    require(len(anchors) == 1, 'witness-source-anchor')
    start = anchors[0]
    end = next(n for n in range(start + 1, len(text) + 1) if text[n - 1] == '#endif') if mutation else start
    typ = SUP + ('.LinuxRetainedReportSet' if mutation else '.SupervisionRunOutputBudget')
    method = ('System.Byte[] ' + typ + '::MutateRetainedReportForC2(System.Threading.CancellationToken)' if mutation
              else 'System.Boolean ' + typ + '::Charge(System.Int32)')
    require(digest(read(ROOT / doc)) == capture['files'][doc]['sha256'], 'witness-source-hash')
    candidates = []
    for module, docs in value.items():
        if Path(module).name != SUP + '.dll':
            continue
        for name, types in docs.items():
            if name.endswith('/' + doc) and typ in types and method in types[typ]:
                candidates.append(types[typ][method])
    require(len(candidates) == 1 and any(int(line) >= start and int(line) <= end and hits > 0
                                       for line, hits in candidates[0]['Lines'].items()), 'current-witness')
    return {'document': doc, 'type': typ, 'method': method, 'start': start, 'end': end,
            'source_sha256': capture['files'][doc]['sha256']}


def retention(root, extra=0):
    count, size = 0, extra
    for path in root.rglob('*'):
        node = path.lstat()
        require(not stat.S_ISLNK(node.st_mode), 'retention-link')
        if stat.S_ISDIR(node.st_mode):
            continue
        maximum = REPORT if path.parent == root and path.name in FILES[4:6] else LEAF
        require(stat.S_ISREG(node.st_mode) and node.st_nlink == 1 and node.st_size <= maximum, 'retention-leaf')
        count += 1
        size += node.st_size
    require(count + int(extra > 0) <= 128 and size <= TOTAL, 'retention-total')


def finalize(args):
    original = bound(args)
    require(type(args.collector_exit) is int and args.collector_exit == 0, 'outer-exit')
    inner = decode(read(args.evidence / FILES[2]))
    armed = decode(read(args.evidence / FILES[1]))
    validate_instrumented(armed['instrumented'], original)
    terminal = decode(read(args.evidence / FILES[3]))
    original_sha = digest(read(args.evidence / FILES[0]))
    require(inner == {'inner_collector_exit': 0, 'original_sha256': original_sha}
            and type(inner['inner_collector_exit']) is int and type(terminal['driver_exit']) is int
            and armed['original_sha256'] == original_sha
            and terminal == {'driver_exit': 0, 'instrumented_sha256': digest(read(args.evidence / FILES[1]))}, 'joined-exits')
    restored = {'None': image(args.tool), 'C2': image(args.mutation_tool)}
    require(restored == original['original'] and digest(read(console(args), TOTAL)) == original['console_sha256'], 'restoration')
    native_bytes = read(args.evidence / 'native/native-receipt.json')
    rows = native(decode(native_bytes), args.source, args.base)
    case_hashes = {}
    for row in rows:
        name = row['case']
        body = read(args.evidence / 'native' / (name + '.json'))
        require(decode(body) == row, 'case-receipt')
        case_hashes[name] = digest(body)
        for channel in ('stdout', 'stderr'):
            require(digest(read(args.evidence / 'native' / (name + '-owner.' + channel)))
                    == row[channel + '_sha256'], 'owner-log')
    inventory_bytes = read(args.evidence / 'native/input-inventory.json')
    inventory = decode(inventory_bytes)
    for variant, name in (('None', 'tool'), ('C2', 'mutation_tool')):
        require(set(armed['instrumented'][variant]) == set(NAMES)
                and all(inventory[name][leaf]['sha256'] == armed['instrumented'][variant][leaf] for leaf in NAMES), 'staged-image')
    require(all(row['input_inventory_sha256'] == digest(inventory_bytes) for row in rows), 'native-staging')
    _, capture = source(args)
    raw, combined = [read(args.evidence / name, REPORT) for name in FILES[4:6]]
    combined_report = report(combined)
    witnesses = {'C2': witness(report(raw), capture, True), 'None': witness(combined_report, capture, False)}
    require(witness(combined_report, capture, True) == witnesses['C2'], 'merged-current-witness')
    prior = original['context'].get('external_predecessor')
    if prior is not None:
        require(original['context'] == context(args), 'context-changed-before-publication')
    value = {'schema': 'issue779-producer-roots-measurement-v2' if prior is not None else 'issue779-producer-roots-measurement-v1',
             'context': original['context'],
             'original': original['original'], 'instrumented': armed['instrumented'], 'restored': restored,
             'original_binding_sha256': original_sha,
             'instrumented_binding_sha256': digest(read(args.evidence / FILES[1])),
             'inner_exit_sha256': digest(read(args.evidence / FILES[2])),
             'driver_exit_sha256': digest(read(args.evidence / FILES[3])),
             'outer_collector_exit': 0, 'inner_collector_exit': 0, 'driver_exit': 0,
             'native_receipt_sha256': digest(native_bytes), 'native_receipt_bytes': len(native_bytes),
             'case_receipt_sha256': case_hashes,
             'input_inventory_sha256': digest(inventory_bytes),
             'reports': {FILES[4]: {'sha256': digest(raw), 'bytes': len(raw)},
                         FILES[5]: {'sha256': digest(combined), 'bytes': len(combined)}},
             'current_witness': witnesses, 'external_predecessor': prior,
             'root_only': True, 'worker_imported': False, 'native_acceptance': False, 'ready_for_gate': prior is not None}
    retention(args.evidence, len(canonical(value)))
    write(args.evidence / FILES[6], value)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('operation', choices=('bind', 'target', 'finalize'))
    for name in ('tool', 'mutation-tool', 'runtime', 'subject', 'evidence'):
        parser.add_argument('--' + name, type=Path, required=True)
    parser.add_argument('--source', required=True)
    parser.add_argument('--base', required=True, choices=(BASE,))
    parser.add_argument('--stage', choices=('inner', 'driver'))
    parser.add_argument('--collector-exit', type=int)
    for name in PRIOR_OPTIONS:
        parser.add_argument('--' + name.replace('_', '-'), type=str if name == 'prior_artifact_name' else Path)
    args = parser.parse_args()
    require(re.fullmatch('[0-9a-f]{40}', args.source), 'head')
    require(all(getattr(args, n).is_absolute() for n in ('tool', 'mutation_tool', 'runtime', 'subject', 'evidence')), 'absolute-path')
    require((args.stage is not None) == (args.operation == 'target')
            and (args.collector_exit is not None) == (args.operation == 'finalize'), 'operation-arguments')
    prior_enabled(args)
    os.umask(0o077)
    return {'bind': bind, 'target': target, 'finalize': finalize}[args.operation](args) or 0


if __name__ == '__main__':
    raise SystemExit(main())
