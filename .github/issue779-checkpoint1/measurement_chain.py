#!/usr/bin/env python3
"""Data-only, source-bound segmented standard Coverlet chain custody.

No collector, native procedure, process or merge is executed here. Reports are
retained byte-for-byte. The separate all-sixteen C1 lane remains mandatory.
"""
from __future__ import annotations

import argparse
import ast
import base64
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import stat
import sys
import time
import types
from types import SimpleNamespace

import binding

ORDER = ('None', 'N04', 'N05', 'N06', 'N07', 'N08', 'N09', 'N10',
         'N11', 'N12', 'N13', 'N14', 'N15', 'N16')
CASES = {selector: (('N01', 'N02', 'N03') if selector == 'None' else (selector,))
         for selector in ORDER}
SCHEMA = 'issue779-c1-measurement-chain-v2'
MAX_REPORT = 32 * 1024 * 1024
MAX_LEAF = 8 * 1024 * 1024
MAX_ARCHIVE = 33_619_968
MAX_TOTAL = 192 * 1024 * 1024
MAX_FILES = 1024
ROOT = Path(__file__).resolve().parents[2]
WORKFLOW = re.compile(r'github-actions-([1-9][0-9]*)-([1-9][0-9]*)\Z')
FIELDS = ('schema', 'source', 'source_capture_sha256', 'workflow_identity', 'order', 'completed',
          'cases', 'predecessor', 'links', 'report_sha256', 'files', 'root_only', 'worker_imported',
          'native_acceptance', 'coverage_credit', 'all_sixteen_claimed')
LINK_FIELDS = ('selector', 'artifact_name', 'measurement_binding_sha256', 'report_sha256',
               'prior_report_sha256', 'prior_binding_sha256', 'native_result_sha256')
C2_FIELDS = ('kind', 'artifact_name', 'binding_sha256', 'report_sha256', 'inner_report_sha256',
             'native_receipt_sha256')
C1_FILES = ('source.json', 'build.json', 'measurement-original.json', 'build-measured.json',
            'measurement-target.json', 'measurement-binding.json', 'coverage.json',
            'coverage.cobertura.xml', 'measurement-prepare.log', 'measurement-native.log')
C2_FILES = ('source.json', 'producer-roots-binding.json', 'producer-roots-original.json',
            'producer-roots-instrumented.json', 'producer-roots-inner-exit.json',
            'producer-roots-target.json', 'native-producer-roots.json', 'native-producer-roots-c2.json',
            'package-smoke.json')


def need(value, reason):
    binding.require(value, 'measurement-chain-' + reason)


def sha(raw):
    return hashlib.sha256(raw).hexdigest()


def check(deadline):
    need(type(deadline) in (int, float) and time.monotonic() < deadline, 'deadline')


def rel(name):
    need(type(name) is str and name and len(name.encode()) <= 4095 and '\\' not in name
         and not any(ord(c) < 32 or ord(c) == 127 for c in name), 'relative-path')
    p = PurePosixPath(name)
    need(not p.is_absolute() and p.as_posix() == name and all(x not in ('', '.', '..') for x in p.parts),
         'relative-path')
    return name


def read(path, maximum, deadline):
    """Read stable single-link regular bytes through retained no-follow parents."""
    check(deadline)
    path = binding.absolute(str(path))
    fds = [os.open('/', os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC)]
    parents = []
    sig = lambda s: (s.st_dev, s.st_ino, s.st_mode, s.st_uid, s.st_gid)
    try:
        for name in path.parts[1:-1]:
            parent = fds[-1]
            before = os.stat(name, dir_fd=parent, follow_symlinks=False)
            need(stat.S_ISDIR(before.st_mode), 'parent')
            fd = os.open(name, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=parent)
            fds.append(fd)
            need(sig(os.fstat(fd)) == sig(before), 'parent-open')
            parents.append((parent, name, fd, sig(before)))
            check(deadline)
        parent = fds[-1]
        before = os.stat(path.name, dir_fd=parent, follow_symlinks=False)
        need(stat.S_ISREG(before.st_mode) and before.st_nlink == 1 and 0 <= before.st_size <= maximum,
             'leaf')
        fd = os.open(path.name, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK | os.O_CLOEXEC, dir_fd=parent)
        fds.append(fd)
        need(binding._identity(os.fstat(fd)) == binding._identity(before), 'leaf-open')
        data = bytearray()
        while True:
            check(deadline)
            part = os.read(fd, min(65536, before.st_size + 1 - len(data)))
            if not part:
                break
            data.extend(part)
            need(len(data) <= before.st_size, 'leaf-growth')
        need(len(data) == before.st_size and binding._identity(before) == binding._identity(os.fstat(fd))
             == binding._identity(os.stat(path.name, dir_fd=parent, follow_symlinks=False)), 'leaf-changed')
        for parent, name, fd, expected in parents:
            need(sig(os.fstat(fd)) == expected == sig(os.stat(name, dir_fd=parent, follow_symlinks=False)),
                 'parent-changed')
        return bytes(data)
    finally:
        for fd in reversed(fds):
            os.close(fd)


def leaf_limit(name):
    """Use the original retention contract's exact report/archive basenames."""
    leaf = PurePosixPath(rel(name)).name
    if leaf in ('native-private.tar', 'native.tar'):
        return MAX_ARCHIVE
    if leaf in ('coverage.json', 'coverage.cobertura.xml', 'prior-report.json',
                'native-producer-roots.json', 'native-producer-roots-c2.json'):
        return MAX_REPORT
    return MAX_LEAF


def scan(root, deadline):
    """Bound an existing artifact tree; never chmod or follow a link."""
    root = binding.absolute(str(root))
    need(root.resolve(strict=True) == root and stat.S_ISDIR(root.lstat().st_mode), 'tree-root')
    rows, total, directories = {}, 0, [(root, binding._identity(root.lstat()))]
    for directory, names, files in os.walk(root, followlinks=False):
        check(deadline)
        need(len(directories) <= MAX_FILES, 'directory-count')
        for name in names:
            p = Path(directory) / name
            s = p.lstat()
            need(stat.S_ISDIR(s.st_mode) and not p.is_symlink(), 'tree-directory')
            rel(p.relative_to(root).as_posix())
            directories.append((p, binding._identity(s)))
        for name in files:
            p = Path(directory) / name
            n = rel(p.relative_to(root).as_posix())
            limit = leaf_limit(n)
            raw = read(p, limit, deadline)
            total += len(raw)
            need(len(rows) < MAX_FILES and total <= MAX_TOTAL, 'tree-bound')
            rows[n] = raw
    for p, original in directories:
        need(binding._identity(p.lstat()) == original, 'tree-changed')
    return rows


def source(raw, head, deadline):
    value = binding.validate_capture(binding.decode(raw), head)
    # These are the only executable data helpers imported below. Their bytes are
    # checked against this full immutable capture before any helper import.
    required = ('.github/issue779-checkpoint1/binding.py',
                '.github/issue779-checkpoint1/measurement.py',
                '.github/issue779-checkpoint1/measurement_data_tests.py',
                '.github/issue779-checkpoint1/publish.py',
                '.github/issue779-checkpoint1/measurement_chain.py',
                '.github/issue779-checkpoint1/measurement_custody.py',
                '.github/issue779-checkpoint4/producer_roots.py',
                '.github/issue779-checkpoint4/measurement_arguments.py',
                'Evidence/ForgeTrust.AppSurface.Evidence.Supervision/LinuxRetainedReportSet.cs',
                'Evidence/ForgeTrust.AppSurface.Evidence.Supervision/SupervisionRunOutputBudget.cs')
    bodies = {}
    for name in required:
        need(name in value['files'], 'source-helper-missing')
        body = read(ROOT / name, MAX_LEAF, deadline)
        need(sha(body) == value['files'][name]['sha256']
             and format(stat.S_IMODE((ROOT / name).lstat().st_mode), '04o') == value['files'][name]['mode'],
             'source-helper-changed')
        bodies[name] = body
    return value, bodies


def validators(bodies):
    """Import only capture-verified fixed data modules; no helper CLI is invoked."""
    def module(name, path):
        result = types.ModuleType(name)
        result.__file__ = str(ROOT / path)
        exec(compile(bodies[path], str(ROOT / path), 'exec', dont_inherit=True), result.__dict__)
        return result
    old = sys.modules.get('measurement_arguments')
    arguments = module('measurement_arguments', '.github/issue779-checkpoint4/measurement_arguments.py')
    sys.modules['measurement_arguments'] = arguments
    try:
        producer = module('c1_chain_producer_data', '.github/issue779-checkpoint4/producer_roots.py')
        measured = module('c1_chain_measurement_data', '.github/issue779-checkpoint1/measurement.py')
        return producer, measured
    finally:
        if old is None:
            sys.modules.pop('measurement_arguments', None)
        else:
            sys.modules['measurement_arguments'] = old


def workflow(value):
    need(type(value) is str and WORKFLOW.fullmatch(value), 'workflow')
    return WORKFLOW.fullmatch(value).groups()


def artifact(selector, identity):
    run, attempt = workflow(identity)
    return ('issue779-csharp-checkpoint2-' if selector == 'C2' else 'issue779-c1-measured-' + selector + '-') + run + '-' + attempt


def flattened(prefix):
    return [case for selector in prefix for case in CASES[selector]]


def record(source_raw, head, identity, prefix, predecessor, links, report_sha, files):
    return {'schema': SCHEMA, 'source': head, 'source_capture_sha256': sha(source_raw),
            'workflow_identity': identity, 'order': list(ORDER), 'completed': list(prefix),
            'cases': flattened(prefix), 'predecessor': predecessor, 'links': links,
            'report_sha256': report_sha,
            'files': {name: {'sha256': sha(raw), 'bytes': len(raw)} for name, raw in sorted(files.items())},
            'root_only': True, 'worker_imported': False, 'native_acceptance': False,
            'coverage_credit': False, 'all_sixteen_claimed': False}


def validate_record_fields(value, source_raw, head, identity, prefix):
    """Validate manifest claims only; this does not establish their raw custody."""
    binding.exact(value, FIELDS, 'measurement-chain-fields')
    need(value['schema'] == SCHEMA and value['source'] == head
         and value['source_capture_sha256'] == sha(source_raw) and value['workflow_identity'] == identity
         and value['order'] == list(ORDER) and value['completed'] == list(prefix)
         and value['cases'] == flattened(prefix), 'identity-prefix')
    need(value['root_only'] is True and all(value[k] is False for k in
         ('worker_imported', 'native_acceptance', 'coverage_credit', 'all_sixteen_claimed')), 'authority')
    need(type(prefix) in (tuple, list) and tuple(prefix) == ORDER[:len(prefix)] and len(prefix) <= len(ORDER), 'expected-prefix')
    binding.exact(value['predecessor'], C2_FIELDS, 'measurement-chain-predecessor-fields')
    need(value['predecessor']['kind'] == 'producer-roots'
         and value['predecessor']['artifact_name'] == artifact('C2', identity), 'predecessor-identity')
    for key in C2_FIELDS[2:]:
        need(type(value['predecessor'][key]) is str and binding.HEX64.fullmatch(value['predecessor'][key]), 'predecessor-hash')
    need(type(value['report_sha256']) is str and binding.HEX64.fullmatch(value['report_sha256']), 'report-hash')
    need(type(value['links']) is list and len(value['links']) == len(prefix), 'link-count')
    need(type(value['files']) is dict and all(rel(n) and n.startswith('lineage/') for n in value['files']), 'file-map-path')
    previous = value['predecessor']['report_sha256']
    for selector, link in zip(prefix, value['links']):
        binding.exact(link, LINK_FIELDS, 'measurement-chain-link-fields')
        need(link['selector'] == selector and link['artifact_name'] == artifact(selector, identity)
             and link['prior_report_sha256'] == previous, 'link-order')
        for key in LINK_FIELDS[2:]:
            need(type(link[key]) is str and binding.HEX64.fullmatch(link[key]), 'link-hash')
        previous = link['report_sha256']
    need(previous == value['report_sha256'], 'last-report')
    total = 0
    need(0 < len(value['files']) <= MAX_FILES, 'manifest-count')
    for name, row in value['files'].items():
        binding.exact(row, ('sha256', 'bytes'), 'measurement-chain-file-fields')
        need(type(row['sha256']) is str and binding.HEX64.fullmatch(row['sha256'])
             and type(row['bytes']) is int and 0 <= row['bytes'] <= leaf_limit(name), 'manifest-leaf')
        total += row['bytes']
    need(total <= MAX_TOTAL, 'manifest-total')


def validate_record(value, source_raw, head, identity, prefix, files):
    """Bind every manifest row to the actual supplied unchanged bytes."""
    validate_record_fields(value, source_raw, head, identity, prefix)
    need(value['files'] == {n: {'sha256': sha(b), 'bytes': len(b)} for n, b in sorted(files.items())}, 'raw-file-map')


def c2(raws, source_raw, head, identity, producer, measured, capture):
    """Validate actual C2 endpoints; earlier C4 semantics stay independently gated."""
    def get(name):
        need(name in raws, 'c2-missing')
        return raws[name]
    need(get('source.json') == source_raw, 'c2-source')
    value = binding.decode(get('producer-roots-binding.json'))
    need(value['schema'] == 'issue779-producer-roots-measurement-v2'
         and value['root_only'] is True and value['worker_imported'] is False
         and value['native_acceptance'] is False and value['ready_for_gate'] is True, 'c2-eligible')
    binding.exact(value, ('schema', 'context', 'original', 'instrumented', 'restored', 'original_binding_sha256',
        'instrumented_binding_sha256', 'inner_exit_sha256', 'driver_exit_sha256', 'outer_collector_exit',
        'inner_collector_exit', 'driver_exit', 'native_receipt_sha256', 'native_receipt_bytes',
        'case_receipt_sha256', 'input_inventory_sha256', 'reports', 'current_witness', 'external_predecessor',
        'root_only', 'worker_imported', 'native_acceptance', 'ready_for_gate'), 'measurement-chain-c2-fields')
    ctx = value['context']
    binding.exact(ctx, ('source', 'base', 'source_capture_sha256', 'package_smoke_sha256', 'paths',
                        'external_predecessor'), 'measurement-chain-c2-context-fields')
    need(ctx['source'] == head and ctx['base'] == producer.BASE
         and ctx['source_capture_sha256'] == sha(source_raw), 'c2-context')
    ext = value['external_predecessor']
    binding.exact(ext, ('kind', 'variant', 'artifact_name', 'merge_order', 'source_capture_sha256',
                       'input_binding_sha256', 'coverage_sha256', 'native_receipt_sha256'), 'c2-external-fields')
    run, attempt = workflow(identity)
    need(ext == ctx['external_predecessor'] and ext['kind'] == 'accounts-rollback'
         and ext['variant'] == 'C4AccountRollback'
         and ext['artifact_name'] == 'issue779-c4-accounts-C4AccountRollback-' + run + '-' + attempt
         and ext['merge_order'] == list(producer.MERGE_ORDER)
         and ext['source_capture_sha256'] == sha(source_raw), 'c2-external')
    for key in ('input_binding_sha256', 'coverage_sha256', 'native_receipt_sha256'):
        need(type(ext[key]) is str and binding.HEX64.fullmatch(ext[key]), 'c2-external-hash')
    for key in ('outer_collector_exit', 'inner_collector_exit', 'driver_exit'):
        need(type(value[key]) is int and value[key] == 0, 'c2-exit')
    original = binding.decode(get('producer-roots-original.json'))
    armed = binding.decode(get('producer-roots-instrumented.json'))
    need(original['schema'] == 'issue779-producer-roots-original-v1' and original['context'] == ctx
         and original['original'] == value['original'], 'c2-original')
    producer.validate_instrumented(value['instrumented'], original)
    need(type(original['console_sha256']) is str and binding.HEX64.fullmatch(original['console_sha256']), 'c2-console')
    need(value['restored'] == value['original'] and armed['instrumented'] == value['instrumented']
         and armed['original_sha256'] == sha(get('producer-roots-original.json')), 'c2-restored')
    for maps in (value['original'], value['instrumented'], value['restored']):
        need(set(maps) == {'None', 'C2'} and all(set(v) == set(binding.MEASUREMENT_NAMES)
             and all(type(h) is str and binding.HEX64.fullmatch(h) for h in v.values()) for v in maps.values()), 'c2-images')
    for key, name in (('original_binding_sha256', 'producer-roots-original.json'),
                      ('instrumented_binding_sha256', 'producer-roots-instrumented.json'),
                      ('inner_exit_sha256', 'producer-roots-inner-exit.json'),
                      ('driver_exit_sha256', 'producer-roots-target.json')):
        need(value[key] == sha(get(name)), 'c2-input-hash')
    need(type(binding.decode(get('producer-roots-inner-exit.json'))['inner_collector_exit']) is int
         and type(binding.decode(get('producer-roots-target.json'))['driver_exit']) is int, 'c2-numeric-exits')
    need(binding.decode(get('producer-roots-inner-exit.json')) == {'inner_collector_exit': 0,
         'original_sha256': value['original_binding_sha256']}, 'c2-inner')
    need(binding.decode(get('producer-roots-target.json')) == {'driver_exit': 0,
         'instrumented_sha256': value['instrumented_binding_sha256']}, 'c2-driver')
    native_raw = get('native/native-receipt.json')
    need(value['native_receipt_sha256'] == sha(native_raw) and value['native_receipt_bytes'] == len(native_raw), 'c2-native-hash')
    rows = producer.native(binding.decode(native_raw), head, producer.BASE)
    need(set(value['case_receipt_sha256']) == set(producer.CASES), 'c2-case-set')
    inventory_raw = get('native/input-inventory.json')
    inventory = binding.decode(inventory_raw)
    need(sha(inventory_raw) == value['input_inventory_sha256'], 'c2-inventory')
    for row in rows:
        name = row['case']
        body = get('native/' + name + '.json')
        need(binding.decode(body) == row and sha(body) == value['case_receipt_sha256'][name]
             and row['input_inventory_sha256'] == sha(inventory_raw), 'c2-case')
        for channel in ('stdout', 'stderr'):
            need(sha(get('native/' + name + '-owner.' + channel)) == row[channel + '_sha256'], 'c2-owner-log')
    for variant, name in (('None', 'tool'), ('C2', 'mutation_tool')):
        need(all(inventory[name][leaf]['sha256'] == value['instrumented'][variant][leaf]
                 for leaf in binding.MEASUREMENT_NAMES), 'c2-staged-image')
    modules = {}
    for name in ('native-producer-roots.json', 'native-producer-roots-c2.json'):
        body = get(name)
        modules[name] = measured.report_json(body)
        # Apply the original producer's report validator as well.
        producer.report(body)
        need(value['reports'][name] == {'sha256': sha(body), 'bytes': len(body)}, 'c2-report-hash')
    witnesses = {'C2': producer.witness(modules['native-producer-roots-c2.json'], capture, True),
                 'None': producer.witness(modules['native-producer-roots.json'], capture, False)}
    need(value['current_witness'] == witnesses
         and producer.witness(modules['native-producer-roots.json'], capture, True) == witnesses['C2'], 'c2-witness')
    smoke = binding.decode(get('package-smoke.json'))
    need(smoke['schema'] == 'issue779-packaged-consumer-v1' and smoke['source'] == head
         and type(smoke['exit']) is int and smoke['exit'] == 0
         and ctx['package_smoke_sha256'] == sha(get('package-smoke.json')), 'c2-package')
    return {'kind': 'producer-roots', 'artifact_name': artifact('C2', identity),
            'binding_sha256': sha(get('producer-roots-binding.json')),
            'report_sha256': sha(get('native-producer-roots.json')),
            'inner_report_sha256': sha(get('native-producer-roots-c2.json')),
            'native_receipt_sha256': sha(native_raw)}


def captured_data(relative, capture, deadline):
    """Read only a fixed captured data helper before importing or parsing it."""
    raw = read(ROOT / relative, MAX_LEAF, deadline)
    need(relative in capture['files'] and sha(raw) == capture['files'][relative]['sha256']
         and format(stat.S_IMODE((ROOT / relative).lstat().st_mode), '04o') == capture['files'][relative]['mode'],
         'retained-helper-source')
    return raw


def literal_set(raw, name):
    """Read a fixed source literal without executing the native adapter."""
    current = None
    for node in ast.parse(raw).body:
        if isinstance(node, ast.Assign) and any(isinstance(t, ast.Name) and t.id == name for t in node.targets):
            expression = node.value
            if (isinstance(expression, ast.BinOp) and isinstance(expression.op, ast.BitOr)
                    and isinstance(expression.left, ast.Name) and expression.left.id == name):
                need(type(current) is set, 'literal-initializer')
                current |= ast.literal_eval(expression.right)
            else:
                current = ast.literal_eval(expression)
    need(type(current) in (set, dict) and current, 'literal-contract')
    return current


def slot_custody(raws, packet, bridge, allowed):
    """Reconcile original raw USTAR, every selected leaf and both utility streams."""
    need(type(packet) is dict and type(packet.get('files')) is dict, 'native-packet-files')
    archive = raws.get('native/native-private.tar')
    need(type(archive) is bytes and 0 < len(archive) <= MAX_ARCHIVE
         and packet.get('archive_sha256') == sha(archive), 'native-archive-hash')
    members = bridge.unpack(archive, allowed)
    need(set(packet['files']) == set(members)
         and {n.removeprefix('native/retained/') for n in raws if n.startswith('native/retained/')} == set(members),
         'native-archive-projection')
    for name, raw in members.items():
        rel(name)
        binding.exact(packet['files'][name], ('bytes', 'sha256'), 'native-retained-fields')
        row = packet['files'][name]
        need(type(row['bytes']) is int and row == {'bytes': len(raw), 'sha256': sha(raw)}
             and raws.get('native/retained/' + name) == raw, 'native-retained-bytes')
    commands = packet.get('commands')
    bridge.validate_commands(commands)
    for ordinal, command in enumerate(commands):
        path = 'native/commands/command-' + format(ordinal, '02d') + '.json'
        need(path in raws and binding.decode(raws[path]) == command, 'native-command-record')
        for stream in command['logs']:
            raw = raws.get('native/commands/' + stream['name'])
            need(type(raw) is bytes and len(raw) == stream['bytes']
                 and sha(raw) == stream['sha256'], 'native-command-stream')


def n16_custody(raws, packet, leaves):
    """Compare all original N16 embedded rows with their actual retained leaves."""
    need(type(packet.get('files')) is dict and set(packet['files']) == set(leaves), 'n16-retained-set')
    values = {}
    for name, limit in leaves.items():
        row = packet['files'][name]
        binding.exact(row, ('base64', 'sha256', 'bytes'), 'n16-retained-fields')
        need(binding.integer(row['bytes'], 0, limit) and type(row['base64']) is str, 'n16-retained-bound')
        raw = base64.b64decode(row['base64'], validate=True)
        path = name if name in ('command-00.json', '00-stdout.log', '00-stderr.log') else 'N16-' + name
        need(len(raw) == row['bytes'] and sha(raw) == row['sha256']
             and raws.get('native/' + path) == raw, 'n16-actual-retained')
        values[name] = raw
    command = binding.decode(values['command-00.json'])
    need(type(command.get('ordinal')) is int and command['ordinal'] == 0
         and type(command.get('exit')) is int and command['exit'] == 0
         and command.get('phase') == 'actual-csharp-n16'
         and all(command.get(k) is True for k in ('waited', 'group_absent', 'root_start_guard_created',
             'root_start_guard_closed', 'root_utility_cgroup_empty'))
         and command.get('forced_cleanup') is False and command.get('timed_out') is False
         and 'failure' in command and command['failure'] is None, 'n16-command-terminal')
    need(command.get('logs') == [{'name': n, 'bytes': len(values[n]), 'sha256': sha(values[n])}
         for n in ('00-stdout.log', '00-stderr.log')], 'n16-command-streams')


def native_custody(raws, result, source_raw, head, selector, identity, build_raw, execution, capture, deadline):
    """Reconcile the selected original family's raw native evidence as data."""
    path = '.github/issue779-checkpoint1/measurement_custody.py'
    module = types.ModuleType('c1_chain_native_custody')
    module.__file__ = str(ROOT / path)
    exec(compile(captured_data(path, capture, deadline), module.__file__, 'exec',
                 dont_inherit=True), module.__dict__)
    module.validate(raws, result, source_raw, head, selector, identity, build_raw,
                    execution, capture, deadline=deadline)


def stage(raws, source_raw, head, selector, identity, prior_raw, prior_report, measured, capture, *, deadline):
    """Read finalizer data, original/measured maps and unchanged native result bytes."""
    def get(name):
        need(name in raws, 'selector-missing')
        return raws[name]
    need(get('source.json') == source_raw, 'selector-source')
    value = binding.decode(get('measurement-binding.json'))
    expected = ('schema', 'source', 'selector', 'workflow_identity', 'collector', 'collector_exit',
        'collector_sha256', 'source_capture_sha256', 'original_binding_sha256', 'original_build_sha256',
        'measured_build_sha256', 'target_sha256', 'prior_report_sha256', 'prior_binding_sha256',
        'report_json_sha256', 'report_cobertura_sha256', 'instrumented_modules', 'restored_tool_sha256',
        'restored', 'commands', 'native_acceptance', 'coverage_credit', 'plan_sha256',
        'native_result_sha256', 'execution_sha256', 'cases', 'data_tests_sha256')
    binding.exact(value, expected, 'measurement-chain-final-fields')
    need(value['schema'] == 'issue779-c1-final-measurement-v2' and value['source'] == head
         and value['selector'] == selector and value['workflow_identity'] == identity
         and value['source_capture_sha256'] == sha(source_raw)
         and value['collector'] == 'coverlet.console/10.0.1'
         and type(value['collector_exit']) is int and value['collector_exit'] == 0
         and value['restored'] is True and value['native_acceptance'] is False
         and value['coverage_credit'] is False and value['cases'] == list(CASES[selector]), 'selector-identity')
    need(value['prior_binding_sha256'] == sha(prior_raw)
         and value['prior_report_sha256'] == sha(prior_report), 'selector-prior')
    original_raw, armed_raw = get('build.json'), get('build-measured.json')
    original, armed = binding.decode(original_raw), binding.decode(armed_raw)
    need(original['schema'] == 'issue779-checkpoint1-build-v1'
         and armed['schema'] == 'issue779-checkpoint1-build-v2', 'selector-build-schema')
    binding.validate_build(original, source_raw, head, selector)
    binding.validate_build(armed, source_raw, head, selector)
    need(armed['measurement']['original_tool'] == original['tool']
         and armed['measurement']['original_build_sha256'] == sha(original_raw)
         and value['original_build_sha256'] == sha(original_raw)
         and value['measured_build_sha256'] == sha(armed_raw)
         and value['instrumented_modules'] == armed['measurement']['instrumented_modules']
         and value['restored_tool_sha256'] == sha(binding.canonical(original['tool'])), 'selector-images')
    need(all(armed[k] == child for k, child in original.items() if k not in ('schema', 'tool')), 'selector-original-fields')
    baseline_raw = get('measurement-original.json')
    baseline = binding.decode(baseline_raw)
    binding.exact(baseline, measured.BASE_FIELDS, 'measurement-chain-original-fields')
    need(baseline['schema'] == 'issue779-c1-measurement-original-v1'
         and baseline['source'] == head and baseline['selector'] == selector
         and baseline['workflow_identity'] == identity and baseline['source_capture_sha256'] == sha(source_raw)
         and baseline['original_tool'] == original['tool']
         and baseline['original_build_sha256'] == sha(original_raw)
         and baseline['prior_report_sha256'] == sha(prior_report)
         and baseline['prior_binding_sha256'] == sha(prior_raw)
         and baseline['native_acceptance'] is False and baseline['coverage_credit'] is False,
         'selector-original')
    need(value['original_binding_sha256'] == sha(baseline_raw)
         and value['collector_sha256'] == baseline['collector_sha256'] == armed['measurement']['collector_sha256'], 'selector-collector')
    for name, key in (('coverage.json', 'report_json_sha256'), ('coverage.cobertura.xml', 'report_cobertura_sha256'),
                      ('measurement-target.json', 'target_sha256'), ('plan/plan.json', 'plan_sha256'),
                      ('native/result.json', 'native_result_sha256'), ('native/execution.json', 'execution_sha256')):
        need(sha(get(name)) == value[key], 'selector-leaf-hash')
    measured.report_json(get('coverage.json'))
    measured.report_xml(get('coverage.cobertura.xml'))
    result = binding.validate_result(binding.decode(get('native/result.json')), source_raw)
    need(result['selector'] == selector and result['plan_sha256'] == value['plan_sha256']
         and result['build_receipt_sha256'] == sha(armed_raw), 'selector-native')
    execution = binding.decode(get('native/execution.json'))
    binding.exact(execution, ('workflow_identity', 'generation'), 'measurement-chain-execution-fields')
    need(execution['workflow_identity'] == identity and type(execution['generation']) is str
         and re.fullmatch('[0-9a-f]{32}', execution['generation']), 'selector-execution')
    inventory_raw = get('native/procedure-inventory.json')
    need(sha(inventory_raw) == capture['files']['.github/issue779-checkpoint1/procedures/inventory.json']['sha256'], 'selector-inventory')
    plan_raw = get('plan/plan.json')
    plan = binding.decode(plan_raw)
    binding.exact(plan, ('schema', 'source', 'selector', 'cases', 'source_capture_sha256', 'build_receipt_sha256',
        'tool_map_sha256', 'runtime_map_sha256', 'procedure_inventory_sha256', 'ready_for_native', 'native_execution'), 'measurement-chain-plan-fields')
    need(plan['schema'] == 'issue779-checkpoint1-plan-v1' and plan['source'] == head
         and plan['selector'] == selector and plan['cases'] == list(CASES[selector])
         and plan['source_capture_sha256'] == sha(source_raw) and plan['build_receipt_sha256'] == sha(armed_raw)
         and plan['tool_map_sha256'] == sha(binding.canonical(armed['tool']))
         and plan['runtime_map_sha256'] == sha(binding.canonical(armed['runtime']))
         and plan['procedure_inventory_sha256'] == sha(inventory_raw)
         and plan['ready_for_native'] is True and plan['native_execution'] is False, 'selector-plan')
    for part in ('plan', 'native'):
        for name, raw in (('source.json', source_raw), ('build-receipt.json', armed_raw),
                          ('procedure-inventory.json', inventory_raw), ('plan.json', plan_raw)):
            need(get(part + '/' + name) == raw, 'selector-retained-input')
    for row in result['cases']:
        need(sha(get('native/' + row['case'] + '-evidence.json')) == row['evidence_sha256'], 'selector-case-hash')
    native_custody(raws, result, source_raw, head, selector, identity, armed_raw, execution, capture, deadline)
    target = binding.decode(get('measurement-target.json'))
    binding.exact(target, ('schema', 'source', 'selector', 'workflow_identity', 'original_binding_sha256',
        'measured_build_sha256', 'commands', 'exit', 'native_acceptance', 'coverage_credit', 'plan_sha256',
        'native_result_sha256', 'execution_sha256', 'cases', 'data_tests_sha256'), 'measurement-chain-target-fields')
    need(target['schema'] == 'issue779-c1-measurement-target-v2' and target['source'] == head
         and target['selector'] == selector and target['workflow_identity'] == identity
         and type(target['exit']) is int and target['exit'] == 0
         and target['native_acceptance'] is False and target['coverage_credit'] is False
         and all(target[k] == value[k] for k in ('original_binding_sha256', 'measured_build_sha256',
              'commands', 'plan_sha256', 'native_result_sha256', 'execution_sha256', 'cases', 'data_tests_sha256')), 'selector-target')
    need(type(value['commands']) is list and len(value['commands']) == 2, 'selector-command-count')
    binding.exact(baseline['paths'], measured.COMMON_PATHS, 'measurement-chain-path-fields')
    args = SimpleNamespace(**{k: Path(v) for k, v in baseline['paths'].items()}, source=head,
                           selector=selector, workflow_identity=identity)
    binding.exact(baseline['evidence_owner'], ('uid', 'gid'), 'measurement-chain-owner-fields')
    need(all(binding.integer(baseline['evidence_owner'][k], 0, 2**32 - 1) for k in ('uid', 'gid')), 'selector-owner-data')
    need(armed['measurement']['collector_path'] == baseline['paths']['collector']
         and baseline['paths']['source_capture'] == str(Path(baseline['paths']['evidence']) / 'source.json')
         and baseline['paths']['build_receipt'] == str(Path(baseline['paths']['evidence']) / 'build.json'), 'selector-paths')
    expected_commands = measured.command_argv(args, original)
    for row, argv, phase, name in zip(value['commands'], expected_commands, ('prepare', 'run'), ('measurement-prepare.log', 'measurement-native.log')):
        binding.exact(row, ('phase', 'argv', 'exit', 'timed_out', 'joined', 'log_path', 'log_sha256', 'log_bytes'), 'measurement-chain-command-fields')
        need(row['phase'] == phase and row['argv'] == argv and type(row['exit']) is int and row['exit'] == 0
             and row['timed_out'] is False and row['joined'] is True
             and row['log_path'] == str(Path(baseline['paths']['evidence']) / name)
             and row['log_sha256'] == sha(get(name)) and row['log_bytes'] == len(get(name)), 'selector-command')
    build_commands = list(original['commands'])
    for helper in original['helpers'].values(): build_commands.extend(helper['commands'])
    for row in build_commands:
        log_name = Path(row['log_path']).name
        need(row['log_path'] == str(Path(baseline['paths']['evidence']) / log_name)
             and sha(get(log_name)) == row['log_sha256'] and len(get(log_name)) == row['log_bytes'], 'selector-publish-log')
    data_raws = {name: raw for name, raw in raws.items() if name.startswith('data-tests/')}
    if selector in measured.DATA_SELECTORS:
        prepared = binding.decode(get('data-tests/prepared.json'))
        data_module = types.ModuleType('c1_chain_private_data_tests')
        data_path = '.github/issue779-checkpoint1/measurement_data_tests.py'
        data_module.__file__ = str(ROOT / data_path)
        exec(compile(captured_data(data_path, capture, deadline), str(ROOT / data_path),
                     'exec', dont_inherit=True), data_module.__dict__)
        data_result = data_module.validate(data_raws, args, original, prepared, deadline=deadline)
        need({name: value['sha256'] for name, value in data_result['instrumented'].items()}
             == armed['measurement']['instrumented_modules'], 'selector-data-image-binding')
        need(sha(get('data-tests/result.json')) == value['data_tests_sha256'], 'selector-data-tests')
    else:
        need(value['data_tests_sha256'] is None and not data_raws, 'selector-no-data-tests')
    return {'selector': selector, 'artifact_name': artifact(selector, identity),
            'measurement_binding_sha256': sha(get('measurement-binding.json')), 'report_sha256': sha(get('coverage.json')),
            'prior_report_sha256': sha(prior_report), 'prior_binding_sha256': sha(prior_raw),
            'native_result_sha256': sha(get('native/result.json'))}


def lineage(raws):
    return {name: raw for name, raw in raws.items() if name.startswith('lineage/')}


def seed_files(raws):
    """Select the raw initial C2 endpoint; no selector report becomes a new base."""
    return {n: raw for n, raw in raws.items() if n.startswith('lineage/c2/')}


def verify_segment(source_raw, raws, identity, prefix, deadline):
    """Verify this raw segment and its immediate prior manifest/report.

    Earlier manifests are claims until validate_final reads every actual segment.
    This function grants neither native acceptance nor final coverage eligibility.
    """
    need(type(prefix) in (tuple, list) and 0 < len(prefix) <= len(ORDER)
         and tuple(prefix) == ORDER[:len(prefix)], 'segment-prefix')
    head = binding.decode(source_raw)['source']
    capture, bodies = source(source_raw, head, deadline)
    producer, measured = validators(bodies)
    need(raws.get('source.json') == source_raw and 'chain-binding.json' in raws
         and 'coverage.json' in raws, 'chain-leaves')
    files = lineage(raws)
    need(set(raws) == {'source.json', 'chain-binding.json', 'coverage.json'} | set(files),
         'segment-extra')
    value = binding.decode(raws['chain-binding.json'])
    need(raws['chain-binding.json'] == binding.canonical(value), 'canonical-chain-record')
    validate_record(value, source_raw, head, identity, prefix, files)
    seeds = seed_files(files)
    base = {n.removeprefix('lineage/c2/'): raw for n, raw in seeds.items()}
    predecessor = c2(base, source_raw, head, identity, producer, measured, capture)
    need(value['predecessor'] == predecessor, 'chain-c2')
    selector = prefix[-1]
    stem = 'lineage/selectors/' + selector + '/'
    node = {n.removeprefix(stem): raw for n, raw in files.items() if n.startswith(stem)}
    need(set(files) == set(seeds) | {stem + n for n in node}, 'segment-selector-set')
    need('prior-binding.json' in node and 'prior-report.json' in node, 'raw-prior-inputs')
    prior_raw, prior_report = node['prior-binding.json'], node['prior-report.json']
    prior_value = binding.decode(prior_raw)
    need(prior_raw == binding.canonical(prior_value), 'canonical-prior-record')
    validate_record_fields(prior_value, source_raw, head, identity, prefix[:-1])
    need(prior_value['predecessor'] == predecessor
         and prior_value['links'] == value['links'][:-1]
         and prior_value['report_sha256'] == sha(prior_report), 'raw-prior-prefix')
    if len(prefix) == 1:
        validate_record(prior_value, source_raw, head, identity, (), seeds)
        need(prior_report == base['native-producer-roots.json'], 'raw-c2-report')
    link = stage(node, source_raw, head, selector, identity, prior_raw, prior_report,
                 measured, capture, deadline=deadline)
    need(link == value['links'][-1] and raws['coverage.json'] == node['coverage.json'],
         'segment-final-report')
    return value


def fresh(path, raws, deadline):
    """Publish only a fresh private bounded tree, with unchanged supplied bytes."""
    check(deadline)
    path = binding.absolute(str(path))
    need(path.parent.resolve(strict=True) == path.parent and not os.path.lexists(path), 'fresh-output')
    need(len(raws) <= MAX_FILES and sum(map(len, raws.values())) <= MAX_TOTAL, 'output-bound')
    for name in raws: rel(name)
    os.mkdir(path, 0o700)
    for name, raw in sorted(raws.items()):
        check(deadline)
        target = path / name
        parent = path
        for component in PurePosixPath(name).parts[:-1]:
            parent = parent / component
            if not parent.exists(): os.mkdir(parent, 0o700)
            need(not parent.is_symlink() and stat.S_ISDIR(parent.lstat().st_mode)
                 and stat.S_IMODE(parent.lstat().st_mode) == 0o700, 'publication-directory')
        fd = os.open(target, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC, 0o600)
        with os.fdopen(fd, 'wb') as stream:
            stream.write(raw)
            stream.flush()
            os.fsync(stream.fileno())
    need(scan(path, deadline) == raws, 'publication-changed')


def prepare(args):
    """Retain one verified predecessor for finalizer rechecking, without renewal."""
    end = time.monotonic() + 140
    source_raw = read(args.source_capture, MAX_LEAF, end)
    capture, bodies = source(source_raw, args.source, end)
    producer, measured = validators(bodies)
    identity = args.workflow_identity
    workflow(identity)
    index = ORDER.index(args.selector)
    expected = artifact('C2' if index == 0 else ORDER[index - 1], identity)
    need(args.input_artifact == expected, 'input-artifact')
    incoming = scan(args.input, end)
    need(incoming.get('source.json') == source_raw, 'input-source')
    previous = {}
    if index == 0:
        predecessor = c2(incoming, source_raw, args.source, identity, producer, measured, capture)
        selected = {name: incoming[name] for name in incoming if name in C2_FILES or name.startswith('native/')}
        files = {'lineage/c2/' + name: raw for name, raw in selected.items()}
        value = record(source_raw, args.source, identity, (), predecessor, [], predecessor['report_sha256'], files)
        report_raw = incoming['native-producer-roots.json']
    else:
        segment = {n.removeprefix('chain/'): raw for n, raw in incoming.items() if n.startswith('chain/')}
        value = verify_segment(source_raw, segment, identity, ORDER[:index], end)
        files = seed_files(segment)
        report_raw = segment['coverage.json']
        previous = {'previous/' + n: raw for n, raw in segment.items()}
    fresh(args.output, {'source.json': source_raw, 'prior-report.json': report_raw,
        'prior-binding.json': binding.canonical(value), **files, **previous}, end)
    return {'selector': args.selector, 'completed': list(ORDER[:index]), 'native_acceptance': False}


def finalize(args):
    """Repeat actual predecessor custody and publish only the current raw segment."""
    end = time.monotonic() + 140
    source_raw = read(args.source_capture, MAX_LEAF, end)
    capture, bodies = source(source_raw, args.source, end)
    producer, measured = validators(bodies)
    workflow(args.workflow_identity)
    index = ORDER.index(args.selector)
    prior = scan(args.input, end)
    need(prior.get('source.json') == source_raw and 'prior-binding.json' in prior
         and 'prior-report.json' in prior, 'prepared-input')
    files = seed_files(prior)
    value = binding.decode(prior['prior-binding.json'])
    need(prior['prior-binding.json'] == binding.canonical(value), 'canonical-prior-record')
    previous = {n.removeprefix('previous/'): raw for n, raw in prior.items() if n.startswith('previous/')}
    need(set(prior) == {'source.json', 'prior-binding.json', 'prior-report.json'}
         | set(files) | {'previous/' + n for n in previous}, 'prepared-extra')
    if index == 0:
        need(not previous, 'initial-no-selector')
        validate_record(value, source_raw, args.source, args.workflow_identity, (), files)
        base = {n.removeprefix('lineage/c2/'): raw for n, raw in files.items()}
        predecessor = c2(base, source_raw, args.source, args.workflow_identity, producer, measured, capture)
        need(value['predecessor'] == predecessor
             and prior['prior-report.json'] == base['native-producer-roots.json'], 'prepared-c2')
    else:
        checked = verify_segment(source_raw, previous, args.workflow_identity, ORDER[:index], end)
        need(checked == value and previous['chain-binding.json'] == prior['prior-binding.json']
             and previous['coverage.json'] == prior['prior-report.json']
             and seed_files(previous) == files, 'prepared-predecessor')
    evidence = scan(args.evidence, end)
    original_build = binding.decode(evidence['build.json'])
    log_names = {Path(row['log_path']).name for row in original_build['commands']}
    for helper in original_build['helpers'].values():
        log_names.update(Path(row['log_path']).name for row in helper['commands'])
    selected = {n: raw for n, raw in evidence.items() if n in C1_FILES or n in log_names or n.startswith(('plan/', 'native/', 'data-tests/'))}
    link = stage(selected, source_raw, args.source, args.selector, args.workflow_identity,
                 prior['prior-binding.json'], prior['prior-report.json'], measured, capture, deadline=end)
    selected['prior-binding.json'] = prior['prior-binding.json']
    selected['prior-report.json'] = prior['prior-report.json']
    stem = 'lineage/selectors/' + args.selector + '/'
    files.update({stem + n: raw for n, raw in selected.items()})
    result = record(source_raw, args.source, args.workflow_identity, ORDER[:index + 1],
                    value['predecessor'], [*value['links'], link], link['report_sha256'], files)
    outgoing = {'source.json': source_raw, 'coverage.json': selected['coverage.json'],
                'chain-binding.json': binding.canonical(result), **files}
    verify_segment(source_raw, outgoing, args.workflow_identity, ORDER[:index + 1], end)
    fresh(args.output, outgoing, end)
    return {'selector': args.selector, 'completed': list(ORDER[:index + 1]), 'cases': result['cases'],
            'native_acceptance': False, 'coverage_credit': False}


def validate_final(source_raw, segments_root, workflow_identity, deadline):
    """Read all fourteen actual segments, then return the final unchanged JSON.

    Every fixed selector directory is one separately bounded downloaded artifact.
    Earlier native evidence is never represented solely by a receipt or digest.
    Only standard Coverlet has merged the one C2 seed and subsequent executions.
    The caller's deadline is checked throughout and never renewed here.
    """
    root = binding.absolute(str(segments_root))
    need(root.resolve(strict=True) == root and stat.S_ISDIR(root.lstat().st_mode), 'segments-root')
    original = binding._identity(root.lstat())
    with os.scandir(root) as entries:
        observed = list(entries)
    need(len(observed) == len(ORDER) and {e.name for e in observed} == set(ORDER)
         and all(e.is_dir(follow_symlinks=False) for e in observed), 'segments-set')
    prior_binding = prior_report = seeds = None
    for index, selector in enumerate(ORDER):
        check(deadline)
        segment = scan(root / selector / 'chain', deadline)
        verify_segment(source_raw, segment, workflow_identity, ORDER[:index + 1], deadline)
        stem = 'lineage/selectors/' + selector + '/'
        if index:
            need(segment[stem + 'prior-binding.json'] == prior_binding
                 and segment[stem + 'prior-report.json'] == prior_report
                 and seed_files(segment) == seeds, 'actual-predecessor-segment')
        else:
            seeds = seed_files(segment)
        prior_binding, prior_report = segment['chain-binding.json'], segment['coverage.json']
    need(binding._identity(root.lstat()) == original, 'segments-root-changed')
    check(deadline)
    return root / ORDER[-1] / 'chain/coverage.json'


def parser():
    value = argparse.ArgumentParser(description=__doc__)
    commands = value.add_subparsers(dest='command', required=True)
    for name, action in (('prepare', prepare), ('finalize', finalize)):
        command = commands.add_parser(name)
        command.add_argument('--input', type=Path, required=True)
        command.add_argument('--source-capture', type=Path, required=True)
        command.add_argument('--source', required=True)
        command.add_argument('--selector', choices=ORDER, required=True)
        command.add_argument('--workflow-identity', required=True)
        command.add_argument('--output', type=Path, required=True)
        if name == 'prepare': command.add_argument('--input-artifact', required=True)
        else: command.add_argument('--evidence', type=Path, required=True)
        command.set_defaults(action=action)
    return value


def main(argv=None):
    os.umask(0o077)
    args = parser().parse_args(argv)
    try:
        print(json.dumps(args.action(args), sort_keys=True))
        return 0
    except (binding.Rejected, OSError, ValueError, KeyError, TypeError, RecursionError, OverflowError):
        print('C1_MEASUREMENT_CHAIN_REJECTED', file=sys.stderr)
        return 1


if __name__ == '__main__':
    raise SystemExit(main())
