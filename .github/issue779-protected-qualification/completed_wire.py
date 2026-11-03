"""Strict canonical JSON codec for completed-run preparation and replay handoff.

Wire values are ordinary comparison data. Decoding does not authenticate provider facts,
verify files, execute validators, or create qualification/admission authority.
"""
from __future__ import annotations

import base64
import hashlib
import json
import re
import stat
from pathlib import Path

import completed_c1
import completed_controller
import completed_orchestration
import completed_run
import completed_source
import completed_validation

MAX_WIRE_BYTES = 16 * 1024 * 1024
MAX_NODES = 1_000_000
MAX_DEPTH = 20
MAX_PHASE_BYTES = 1024 * 1024
_HEX40 = re.compile(r'[0-9a-f]{40}\Z')
_HEX64 = re.compile(r'[0-9a-f]{64}\Z')
_FALSE = ('admission_created', 'native_acceptance', 'qualification_complete', 'gate_eligible',
          'cleanup_complete', 'coverage_credit', 'validator_executed',
          'original_gate_success_proven', 'test_inventory_success_proven',
          'original_tests_success_proven', 'original_merge_success_proven',
          'original_workflow_success_proven')


def _fail(code):
    raise ValueError('completed-wire-' + code)


def _need(ok, code):
    if not ok:
        _fail(code)


def _integer(value, *, minimum=0, maximum=(1 << 63) - 1, code='integer'):
    _need(type(value) is int and minimum <= value <= maximum, code)
    return value


def _digest(value, *, size=64, code='digest'):
    pattern = _HEX40 if size == 40 else _HEX64
    _need(type(value) is str and pattern.fullmatch(value) is not None, code)
    return value


def _path(value, code='path'):
    try:
        encoded_size = len(value.encode('utf-8')) if type(value) is str else 0
    except UnicodeEncodeError:
        _fail(code)
    _need(type(value) is str and value.startswith('/') and encoded_size <= 4095
          and all(ord(char) >= 32 and ord(char) != 127 for char in value)
          and '\\' not in value and '://' not in value
          and all(part not in ('', '.', '..') for part in value.split('/')[1:])
          and str(Path(value)) == value, code)
    return value


def _tree(value, *, depth=0, count=None):
    if count is None:
        count = [0]
    count[0] += 1
    _need(count[0] <= MAX_NODES and depth <= MAX_DEPTH, 'complexity')
    if type(value) is dict:
        folded = set()
        for key, child in value.items():
            _need(type(key) is str, 'key-type')
            alias = key.casefold()
            _need(alias not in folded, 'duplicate-key')
            folded.add(alias)
            _tree(child, depth=depth + 1, count=count)
    elif type(value) is list:
        for child in value:
            _tree(child, depth=depth + 1, count=count)
    else:
        _need(value is None or type(value) in (str, int, bool), 'value-type')
        if type(value) is int:
            _integer(value, minimum=-(1 << 63), maximum=(1 << 64) - 1, code='integer-range')
    return count[0]


def _false_claims(value):
    if type(value) is dict:
        for key, child in value.items():
            if key in completed_controller.CLAIMS:
                _need(child is False, 'authority-claim')
            _false_claims(child)
    elif type(value) is list:
        for child in value:
            _false_claims(child)


def _pairs(pairs):
    result = {}
    folded = set()
    for key, value in pairs:
        alias = key.casefold()
        _need(key not in result and alias not in folded, 'duplicate-key')
        result[key] = value
        folded.add(alias)
    return result


def _loads(raw, schema):
    _need(type(raw) is bytes and 0 < len(raw) <= MAX_WIRE_BYTES, 'wire-size')
    try:
        value = json.loads(raw.decode('utf-8'), object_pairs_hook=_pairs,
                           parse_constant=lambda _v: _fail('nonfinite'))
    except (UnicodeError, json.JSONDecodeError, RecursionError, ValueError):
        _fail('json')
    _tree(value)
    _need(type(value) is dict and value.get('schema') == schema, 'schema')
    _need(_dump(value) == raw, 'noncanonical')
    return value


def _dump(value):
    _tree(value)
    body = bytearray()
    encoder = json.JSONEncoder(sort_keys=True, separators=(',', ':'), ensure_ascii=True,
                               allow_nan=False)
    try:
        for part in encoder.iterencode(value):
            encoded = part.encode('ascii')
            _need(len(body) + len(encoded) <= MAX_WIRE_BYTES, 'wire-size')
            body.extend(encoded)
    except (TypeError, ValueError, RecursionError):
        _fail('encode')
    return bytes(body)


def _exact(value, keys, code):
    _need(type(value) is dict and set(value) == set(keys), code)


def _provider(value):
    if value is None:
        return None
    _exact(value, ('repository_id', 'repository_full_name', 'run_id', 'run_attempt', 'head_sha',
                   'workflow_id', 'workflow_path', 'workflow_name', 'event', 'head_branch',
                   'status', 'conclusion'), 'provider-shape')
    return completed_run.ProviderRunConclusion(
        _integer(value['repository_id'], minimum=1), value['repository_full_name'],
        _integer(value['run_id'], minimum=1), _integer(value['run_attempt'], minimum=1),
        _digest(value['head_sha'], size=40), _integer(value['workflow_id'], minimum=1),
        value['workflow_path'], value['workflow_name'], value['event'], value['head_branch'],
        value['status'], value['conclusion'])


def _stage(value):
    _exact(value, ('repository_id', 'run_id', 'run_attempt', 'head_sha', 'workflow_path',
                   'workflow_sha256s', 'source_tree', 'source_capture_sha256', 'job_ids',
                   'artifacts', 'total_archive_bytes', 'jobs', 'provider_run'), 'stage-shape')
    hashes = value['workflow_sha256s']
    _need(type(hashes) is list and all(type(row) is list and len(row) == 2
          and type(row[0]) is str for row in hashes), 'workflow-hashes')
    artifacts = value['artifacts']
    _need(type(artifacts) is list and len(artifacts) <= completed_run.MAX_ARTIFACTS,
          'artifact-count')
    artifact_rows = []
    for row in artifacts:
        _exact(row, ('artifact_id', 'name', 'size', 'sha256', 'staged_filename'), 'artifact-shape')
        _need(type(row['name']) is str and type(row['staged_filename']) is str,
              'artifact-text')
        artifact_rows.append(completed_run.ArtifactBinding(
            _integer(row['artifact_id'], minimum=1), row['name'],
            _integer(row['size'], maximum=completed_run.MAX_TOTAL_ARCHIVE_BYTES),
            _digest(row['sha256']), row['staged_filename']))
    jobs = value['jobs']
    _need(type(jobs) is list and len(jobs) <= 64, 'job-count')
    job_rows = []
    for row in jobs:
        _exact(row, ('name', 'id', 'head_sha', 'run_attempt', 'status', 'conclusion'), 'job-shape')
        _need(all(type(row[key]) is str for key in ('name', 'head_sha', 'status', 'conclusion')),
              'job-text')
        job_rows.append(completed_run.CompletedJob(
            row['name'], _integer(row['id'], minimum=1), _digest(row['head_sha'], size=40),
            _integer(row['run_attempt'], minimum=1), row['status'], row['conclusion']))
    job_ids = value['job_ids']
    _need(type(job_ids) is list and len(job_ids) <= 64, 'job-ids')
    stage = completed_run.CompletedRunStage(
        _integer(value['repository_id'], minimum=1), _integer(value['run_id'], minimum=1),
        _integer(value['run_attempt'], minimum=1), _digest(value['head_sha'], size=40),
        value['workflow_path'], tuple((row[0], _digest(row[1])) for row in hashes),
        _digest(value['source_tree'], size=40), _digest(value['source_capture_sha256']),
        tuple(_integer(item, minimum=1) for item in job_ids), tuple(artifact_rows),
        _integer(value['total_archive_bytes'], maximum=completed_run.MAX_TOTAL_ARCHIVE_BYTES),
        tuple(job_rows), _provider(value['provider_run']))
    completed_run.require_completed_provider_run(stage)
    return stage


def _source_binding(value):
    fields = ('repository_id', 'run_id', 'run_attempt', 'head_sha', 'source_tree',
              'source_capture_sha256', 'validator_head_sha', 'validator_tree',
              'validator_capture_sha256')
    _exact(value, fields + _FALSE[:4], 'source-binding-shape')
    _need(all(value[key] is False for key in _FALSE[:4]), 'authority-claim')
    return completed_source.SourceBinding(
        _integer(value['repository_id'], minimum=1), _integer(value['run_id'], minimum=1),
        _integer(value['run_attempt'], minimum=1), _digest(value['head_sha'], size=40),
        _digest(value['source_tree'], size=40), _digest(value['source_capture_sha256']),
        _digest(value['validator_head_sha'], size=40), _digest(value['validator_tree'], size=40),
        _digest(value['validator_capture_sha256']))


def _member(value):
    _exact(value, ('path', 'compressed_bytes', 'expanded_bytes', 'compression'), 'member-shape')
    try:
        completed_validation._safe_name(value['path'])
    except (UnicodeError, ValueError, TypeError):
        _fail('member-name')
    _need(type(value['compression']) is int and value['compression'] in (0, 8),
          'member-compression')
    compressed = _integer(value['compressed_bytes'],
                          maximum=completed_run.MAX_TOTAL_ARCHIVE_BYTES)
    expanded = _integer(value['expanded_bytes'],
                        maximum=completed_validation.MAX_EXPANDED_ARCHIVE_BYTES)
    return completed_validation.MemberProjection(
        value['path'], compressed, expanded, value['compression'])


def _archive(value):
    _exact(value, ('artifact_id', 'artifact_name', 'archive_sha256', 'archive_bytes',
                   'expanded_bytes', 'members', 'source_sha256'), 'archive-shape')
    members = value['members']
    _need(type(members) is list and len(members) <= completed_validation.MAX_ARCHIVE_MEMBERS,
          'member-count')
    source = value['source_sha256']
    _need(source is None or (type(source) is str and _HEX64.fullmatch(source)), 'archive-source')
    _need(type(value['artifact_name']) is str, 'archive-name')
    member_rows = tuple(_member(row) for row in members)
    expanded = _integer(value['expanded_bytes'],
                        maximum=completed_validation.MAX_EXPANDED_ARCHIVE_BYTES)
    _need(sum(row.expanded_bytes for row in member_rows) == expanded, 'archive-expanded-total')
    return completed_validation.ArchiveProjection(
        _integer(value['artifact_id'], minimum=1), value['artifact_name'],
        _digest(value['archive_sha256']),
        _integer(value['archive_bytes'], maximum=completed_run.MAX_TOTAL_ARCHIVE_BYTES),
        expanded, member_rows, source)


def _inventory(value):
    _exact(value, ('repository_id', 'run_id', 'run_attempt', 'head_sha', 'source_tree',
                   'source_capture_sha256', 'source_count', 'archives', 'aggregate',
                   'partial_receipt'), 'inventory-shape')
    archives = value['archives']
    _need(type(archives) is list and len(archives) == completed_run.MAX_ARTIFACTS,
          'archive-count')
    a = value['aggregate']
    _exact(a, ('schema', 'source', 'source_capture_sha256', 'selectors', 'cases', 'passed',
               'skipped', 'exit_code', 'aggregate_sha256'), 'aggregate-shape')
    p = value['partial_receipt']
    _exact(p, ('run_id', 'run_attempt', 'source_capture_sha256', 'aggregate_artifact_id',
               'aggregate_artifact_sha256', 'runtime_job_ids', 'claims', 'receipt_sha256'),
           'partial-shape')
    _need(type(a['schema']) is str and type(a['source']) is str
          and type(a['selectors']) is list and all(type(x) is str for x in a['selectors'])
          and type(a['cases']) is list and all(type(x) is str for x in a['cases']),
          'aggregate-types')
    claims = p['claims']
    _need(type(claims) is list and all(type(row) is list and len(row) == 2
          and type(row[0]) is str and type(row[1]) is bool for row in claims), 'partial-claims')
    runtime_ids = p['runtime_job_ids']
    _need(type(runtime_ids) is list and len(runtime_ids) <= 64, 'partial-job-ids')
    aggregate = completed_validation.AggregateProjection(
        a['schema'], a['source'], _digest(a['source_capture_sha256']), tuple(a['selectors']),
        tuple(a['cases']), _integer(a['passed']), _integer(a['skipped']),
        _integer(a['exit_code'], maximum=255), _digest(a['aggregate_sha256']))
    partial = completed_validation.PartialReceiptProjection(
        _integer(p['run_id'], minimum=1), _integer(p['run_attempt'], minimum=1),
        _digest(p['source_capture_sha256']), _integer(p['aggregate_artifact_id'], minimum=1),
        _digest(p['aggregate_artifact_sha256']), tuple(_integer(x, minimum=1) for x in runtime_ids),
        tuple((row[0], row[1]) for row in claims), _digest(p['receipt_sha256']))
    inventory = completed_validation.ParsedCompletedRunInventory(
        _integer(value['repository_id'], minimum=1), _integer(value['run_id'], minimum=1),
        _integer(value['run_attempt'], minimum=1), _digest(value['head_sha'], size=40),
        _digest(value['source_tree'], size=40), _digest(value['source_capture_sha256']),
        _integer(value['source_count'], minimum=1), tuple(_archive(row) for row in archives),
        aggregate, partial)
    return inventory


def _prep_data(preparation):
    _need(type(preparation) is completed_controller.CompletedPreparation, 'preparation-type')
    stage = preparation.stage
    binding = preparation.source_binding
    inventory = preparation.inventory
    _need(type(stage) is completed_run.CompletedRunStage
          and type(binding) is completed_source.SourceBinding
          and type(inventory) is completed_validation.ParsedCompletedRunInventory,
          'preparation-fields')
    completed_run.require_completed_provider_run(stage)
    _need((stage.repository_id, stage.run_id, stage.run_attempt, stage.head_sha, stage.source_tree,
           stage.source_capture_sha256) ==
          (binding.repository_id, binding.run_id, binding.run_attempt, binding.head_sha,
           binding.source_tree, binding.source_capture_sha256), 'source-binding-match')
    _need(all(getattr(binding, name) is False for name in _FALSE[:4]), 'source-claim')
    _need((inventory.repository_id, inventory.run_id, inventory.run_attempt, inventory.head_sha,
           inventory.source_tree, inventory.source_capture_sha256) ==
          (stage.repository_id, stage.run_id, stage.run_attempt, stage.head_sha, stage.source_tree,
           stage.source_capture_sha256), 'inventory-match')
    completed_c1._assert_projection(stage, inventory)
    expected_roots = (Path(completed_source.ROOT), Path(preparation.stage_directory),
                      Path(preparation.source_root), Path(preparation.native_source_root))
    _need(tuple(Path(row[0]) for row in preparation.root_identities) == expected_roots,
          'root-identity-paths')
    _need(all(not completed_c1._paths_overlap(left, right)
              for index, left in enumerate(expected_roots)
              for right in expected_roots[index + 1:]), 'root-overlap')
    identities = []
    _need(type(preparation.root_identities) is tuple and len(preparation.root_identities) == 4,
          'root-identities')
    for row in preparation.root_identities:
        _need(type(row) is tuple and len(row) == 2, 'root-identity-row')
        path, identity = row
        _path(str(path))
        _need(type(identity) is tuple and len(identity) == 6, 'root-identity')
        values = [_integer(x, maximum=(1 << 64) - 1) for x in identity]
        _need(values[1] > 0 and values[2] == stat.S_IFDIR and values[3] <= 0o7777,
              'root-identity-fields')
        identities.append([str(path), values])
    return {
        'schema': 'issue779-completed-preparation-v1',
        'stage': _stage_to_data(stage), 'source_binding': _source_to_data(binding),
        'inventory': _inventory_to_data(inventory), 'stage_directory': _path(str(preparation.stage_directory)),
        'source_root': _path(str(preparation.source_root)),
        'native_source_root': _path(str(preparation.native_source_root)), 'root_identities': identities,
    }


def _stage_to_data(s):
    return {'repository_id': s.repository_id, 'run_id': s.run_id, 'run_attempt': s.run_attempt,
            'head_sha': s.head_sha, 'workflow_path': s.workflow_path,
            'workflow_sha256s': [list(x) for x in s.workflow_sha256s], 'source_tree': s.source_tree,
            'source_capture_sha256': s.source_capture_sha256, 'job_ids': list(s.job_ids),
            'artifacts': [{'artifact_id': a.artifact_id, 'name': a.name, 'size': a.size,
                           'sha256': a.sha256, 'staged_filename': a.staged_filename} for a in s.artifacts],
            'total_archive_bytes': s.total_archive_bytes,
            'jobs': [{'name': j.name, 'id': j.id, 'head_sha': j.head_sha, 'run_attempt': j.run_attempt,
                      'status': j.status, 'conclusion': j.conclusion} for j in s.jobs],
            'provider_run': None if s.provider_run is None else {
                name: getattr(s.provider_run, name) for name in (
                    'repository_id', 'repository_full_name', 'run_id', 'run_attempt', 'head_sha',
                    'workflow_id', 'workflow_path', 'workflow_name', 'event', 'head_branch', 'status',
                    'conclusion')}}


def _source_to_data(b):
    return {name: getattr(b, name) for name in (
        'repository_id', 'run_id', 'run_attempt', 'head_sha', 'source_tree', 'source_capture_sha256',
        'validator_head_sha', 'validator_tree', 'validator_capture_sha256')} | {
        name: False for name in _FALSE[:4]}


def _inventory_to_data(i):
    return {'repository_id': i.repository_id, 'run_id': i.run_id, 'run_attempt': i.run_attempt,
            'head_sha': i.head_sha, 'source_tree': i.source_tree,
            'source_capture_sha256': i.source_capture_sha256, 'source_count': i.source_count,
            'archives': [{'artifact_id': a.artifact_id, 'artifact_name': a.artifact_name,
                          'archive_sha256': a.archive_sha256, 'archive_bytes': a.archive_bytes,
                          'expanded_bytes': a.expanded_bytes, 'source_sha256': a.source_sha256,
                          'members': [{'path': m.path, 'compressed_bytes': m.compressed_bytes,
                                      'expanded_bytes': m.expanded_bytes, 'compression': m.compression}
                                     for m in a.members]} for a in i.archives],
            'aggregate': {'schema': i.aggregate.schema, 'source': i.aggregate.source,
                          'source_capture_sha256': i.aggregate.source_capture_sha256,
                          'selectors': list(i.aggregate.selectors), 'cases': list(i.aggregate.cases),
                          'passed': i.aggregate.passed, 'skipped': i.aggregate.skipped,
                          'exit_code': i.aggregate.exit_code, 'aggregate_sha256': i.aggregate.aggregate_sha256},
            'partial_receipt': {'run_id': i.partial_receipt.run_id,
                                'run_attempt': i.partial_receipt.run_attempt,
                                'source_capture_sha256': i.partial_receipt.source_capture_sha256,
                                'aggregate_artifact_id': i.partial_receipt.aggregate_artifact_id,
                                'aggregate_artifact_sha256': i.partial_receipt.aggregate_artifact_sha256,
                                'runtime_job_ids': list(i.partial_receipt.runtime_job_ids),
                                'claims': [list(x) for x in i.partial_receipt.claims],
                                'receipt_sha256': i.partial_receipt.receipt_sha256}}


def encode_preparation(preparation):
    """Encode a preparation value as bounded canonical JSON comparison data."""
    raw = _dump(_prep_data(preparation))
    decode_preparation(raw)
    return raw


def decode_preparation(raw):
    """Decode a preparation handoff; filesystem/provider authentication remains external."""
    value = _loads(raw, 'issue779-completed-preparation-v1')
    _exact(value, ('schema', 'stage', 'source_binding', 'inventory', 'stage_directory',
                   'source_root', 'native_source_root', 'root_identities'), 'preparation-shape')
    stage = _stage(value['stage'])
    binding = _source_binding(value['source_binding'])
    inventory = _inventory(value['inventory'])
    identities = value['root_identities']
    _need(type(identities) is list and len(identities) == 4, 'root-identities')
    roots = []
    for row in identities:
        _need(type(row) is list and len(row) == 2 and type(row[1]) is list and len(row[1]) == 6,
              'root-identity-row')
        roots.append((Path(_path(row[0])), tuple(_integer(x, maximum=(1 << 64) - 1) for x in row[1])))
    result = completed_controller.CompletedPreparation(
        stage, binding, inventory, Path(_path(value['stage_directory'])), Path(_path(value['source_root'])),
        Path(_path(value['native_source_root'])), tuple(roots))
    # Keep identities and attribution coherent without asserting the paths still exist.
    _prep_data(result)
    return result


def _replay_data(replay):
    _need(type(replay) is completed_orchestration.CompletedRunReplay, 'replay-type')
    _need(all(getattr(replay, name) is False for name in _FALSE if hasattr(replay, name)),
          'authority-claim')
    _integer(replay.run_id, minimum=1)
    _integer(replay.run_attempt, minimum=1)
    _integer(replay.gate_job_id, minimum=1)
    _digest(replay.head_sha, size=40)
    _digest(replay.source_tree, size=40)
    _digest(replay.source_capture_sha256)
    phases = replay.phases
    _need(type(phases) is tuple and len(phases) == len(completed_orchestration.PHASES)
          and all(type(x) is completed_orchestration.PhaseObservation for x in phases)
          and tuple(x.phase for x in phases) == completed_orchestration.PHASES, 'phase-order')
    rows = []
    for phase in phases:
        raw = phase.projection_json
        _need(type(raw) is bytes and 0 < len(raw) <= MAX_PHASE_BYTES
              and hashlib.sha256(raw).hexdigest() == phase.projection_sha256, 'phase-digest')
        try:
            text = raw.decode('utf-8')
            parsed = json.loads(text, object_pairs_hook=_pairs,
                                parse_constant=lambda _v: _fail('phase-json'))
        except (UnicodeError, json.JSONDecodeError, RecursionError, ValueError):
            _fail('phase-json')
        _tree(parsed)
        _need(type(parsed) is dict and json.dumps(parsed, sort_keys=True, separators=(',', ':')).encode()
              == raw, 'phase-canonical')
        _false_claims(parsed)
        rows.append({'phase': phase.phase, 'projection_sha256': phase.projection_sha256,
                     'projection_base64': base64.b64encode(raw).decode('ascii')})
    data = {'schema': 'issue779-completed-replay-v1', 'run_id': replay.run_id,
            'run_attempt': replay.run_attempt, 'head_sha': replay.head_sha,
            'source_tree': replay.source_tree, 'source_capture_sha256': replay.source_capture_sha256,
            'phases': rows, 'retained_native_root': _path(str(replay.retained_native_root)),
            'retained_gate_root': _path(str(replay.retained_gate_root)), 'gate_job_id': replay.gate_job_id}
    data.update({name: False for name in _FALSE})
    return data


def encode_replay(replay):
    """Encode the fixed eight-phase replay and preserve projection bytes losslessly."""
    raw = _dump(_replay_data(replay))
    decode_replay(raw)
    return raw


def decode_replay(raw):
    """Decode replay comparison data without granting replay, gate, or admission authority."""
    value = _loads(raw, 'issue779-completed-replay-v1')
    keys = ('schema', 'run_id', 'run_attempt', 'head_sha', 'source_tree', 'source_capture_sha256',
            'phases', 'retained_native_root', 'retained_gate_root', 'gate_job_id', *_FALSE)
    _exact(value, keys, 'replay-shape')
    _need(all(value[key] is False for key in _FALSE), 'authority-claim')
    rows = value['phases']
    _need(type(rows) is list and len(rows) == len(completed_orchestration.PHASES), 'phase-count')
    phases = []
    for index, row in enumerate(rows):
        _exact(row, ('phase', 'projection_sha256', 'projection_base64'), 'phase-shape')
        _need(row['phase'] == completed_orchestration.PHASES[index], 'phase-order')
        digest = _digest(row['projection_sha256'])
        encoded = row['projection_base64']
        _need(type(encoded) is str and len(encoded) <= 4 * ((MAX_PHASE_BYTES + 2) // 3),
              'phase-base64-size')
        try:
            projection = base64.b64decode(encoded.encode('ascii'), validate=True)
        except (UnicodeError, ValueError):
            _fail('phase-base64')
        _need(base64.b64encode(projection).decode('ascii') == encoded, 'phase-base64-canonical')
        _need(0 < len(projection) <= MAX_PHASE_BYTES and hashlib.sha256(projection).hexdigest() == digest,
              'phase-digest')
        phases.append(completed_orchestration.PhaseObservation(row['phase'], digest, projection))
    result = completed_orchestration.CompletedRunReplay(
        _integer(value['run_id'], minimum=1), _integer(value['run_attempt'], minimum=1),
        _digest(value['head_sha'], size=40), _digest(value['source_tree'], size=40),
        _digest(value['source_capture_sha256']), tuple(phases),
        Path(_path(value['retained_native_root'])), Path(_path(value['retained_gate_root'])),
        _integer(value['gate_job_id'], minimum=1))
    _replay_data(result)
    return result
