"""Strict data-only comparison for original and replayed public coverage gate reports."""
from __future__ import annotations

from dataclasses import dataclass
from copy import deepcopy
from decimal import Decimal, DecimalException
import hashlib
import json
import math
import os
from pathlib import Path
import stat
import time

import completed_c1
import completed_run
import completed_validation

MAX_REPORT_BYTES = 1024 * 1024
FIXED_DIFF_BASE = '1991280e8af049db592ebcb705cbf66f4b4c6e8b'
MAX_COUNTER = (1 << 63) - 1
MAX_SOURCE_BYTES = 1024 * 1024
MAX_GATE_BYTES = 1024 * 1024
MAX_COBERTURA_BYTES = 40 * 1024 * 1024
MAX_TRANSCRIPT_BYTES = 8 * 1024 * 1024
MAX_COMPARISON_BASE_BYTES = 256
COVERAGE_MEMBER = 'AppSurface/AppSurface/TestResults/coverage-merged/coverage.cobertura.xml'
GATE_MEMBER = 'AppSurface/AppSurface/TestResults/coverage-merged/coverage-gate.json'
BASE_MEMBER = '_temp/checkpoint4-evidence/comparison-base.txt'
TRANSCRIPT_MEMBER = '_temp/checkpoint4-evidence/coverage.log'
CAPTURE_MEMBER = completed_validation.COVERAGE_SOURCE_MEMBER
ORIGINAL_COVERAGE_PATH = '/home/runner/work/AppSurface/AppSurface/TestResults/coverage-merged/coverage.cobertura.xml'
TOP_KEYS = frozenset(('passed', 'coverage', 'tolerancePercent', 'thresholds', 'effectiveThresholds',
                      'patchLineMode', 'patchDiffSource', 'line', 'branch', 'patchLine', 'patchBranch'))
THRESHOLD_KEYS = frozenset(('line', 'branch', 'patchLine', 'patchBranch'))
SOURCE_KEYS = frozenset(('kind', 'label', 'strictness', 'diffBase', 'path', 'bytes', 'sha256', 'empty'))
METRIC_KEYS = frozenset(('covered', 'valid', 'percent'))
PATCH_LINE_KEYS = frozenset(('diffBase', 'changed', 'measurable', 'covered', 'percent'))
PATCH_BRANCH_KEYS = PATCH_LINE_KEYS


def _need(value, reason):
    if not value:
        raise ValueError('completed-gate-' + reason)


def _remaining(deadline):
    _need(type(deadline) in (int, float) and math.isfinite(deadline)
          and time.monotonic() < deadline, 'deadline')


def _pairs(pairs):
    result = {}
    for key, value in pairs:
        _need(key not in result, 'duplicate-key')
        result[key] = value
    return result


def _parse(raw, deadline):
    _remaining(deadline)
    _need(type(raw) is bytes and 0 < len(raw) <= MAX_REPORT_BYTES, 'report-size')
    try:
        value = json.loads(raw.decode('utf-8'), object_pairs_hook=_pairs, parse_float=Decimal,
                           parse_constant=lambda _value: (_ for _ in ()).throw(ValueError('constant')))
    except (UnicodeError, json.JSONDecodeError, RecursionError, ValueError, DecimalException):
        raise ValueError('completed-gate-report-json') from None
    _remaining(deadline)
    _need(type(value) is dict and set(value) == TOP_KEYS, 'report-schema')
    return value


def _number(value):
    return (type(value) is int and abs(value) <= MAX_COUNTER
            or type(value) is Decimal and value.is_finite())


def _percentage(value):
    return _number(value) and 0 <= value <= 100


def _validate(value):
    _need(type(value['passed']) is bool and value['passed'], 'report-not-passing')
    _need(type(value['coverage']) is str and value['coverage'], 'coverage-path')
    _need(_number(value['tolerancePercent']) and value['tolerancePercent'] == 0.5, 'tolerance')
    for key in ('thresholds', 'effectiveThresholds'):
        row = value[key]
        _need(type(row) is dict and set(row) == THRESHOLD_KEYS, key + '-schema')
        expected_values = ((94.5, 84.5, 94.5, 84.5) if key == 'effectiveThresholds'
                           else (95.0, 85.0, 95.0, 85.0))
        for dimension, expected in zip(('line', 'branch', 'patchLine', 'patchBranch'), expected_values):
            _need(_number(row[dimension]) and row[dimension] == expected, key + '-value')
    _need(value['patchLineMode'] == 'codecov', 'patch-line-mode')
    source = value['patchDiffSource']
    _need(type(source) is dict and set(source) == SOURCE_KEYS, 'source-schema')
    kind = source['kind']
    _need(kind == 'git-base' and source['label'] == FIXED_DIFF_BASE
          and source['strictness'] == 'local-git' and source['diffBase'] == FIXED_DIFF_BASE
          and source['path'] is None
          and type(source['bytes']) is int and 0 <= source['bytes'] <= MAX_COUNTER
          and type(source['sha256']) is str and len(source['sha256']) == 64
          and all(char in '0123456789abcdef' for char in source['sha256'])
          and type(source['empty']) is bool and source['empty'] == (source['bytes'] == 0),
          'source-value')
    for key in ('line', 'branch'):
        metric = value[key]
        _need(type(metric) is dict and set(metric) == METRIC_KEYS, key + '-schema')
        paired_counts = ((metric['covered'] is None and metric['valid'] is None)
                         or (type(metric['covered']) is int and type(metric['valid']) is int
                             and 0 <= metric['covered'] <= MAX_COUNTER
                             and 0 <= metric['valid'] <= MAX_COUNTER))
        _need(paired_counts
              and _percentage(metric['percent']), key + '-value')
    for key, schema in (('patchLine', PATCH_LINE_KEYS), ('patchBranch', PATCH_BRANCH_KEYS)):
        metric = value[key]
        _need(type(metric) is dict and set(metric) == schema, key + '-schema')
        _need(type(metric['diffBase']) is str and metric['diffBase'] == FIXED_DIFF_BASE
              and all(type(metric[name]) is int and metric[name] >= 0
                      for name in ('changed', 'measurable', 'covered'))
              and all(metric[name] <= MAX_COUNTER for name in ('changed', 'measurable', 'covered'))
              and _percentage(metric['percent']), key + '-value')


@dataclass(frozen=True)
class GateReportComparison:
    """Data comparison only; no evaluator, workflow-success or authority claim."""
    original_sha256: str
    replay_sha256: str
    compared_fields: tuple[str, ...]
    reports_match: bool
    validator_executed: bool = False
    original_gate_success_proven: bool = False
    gate_eligible: bool = False
    admission_created: bool = False
    qualification_complete: bool = False


@dataclass(frozen=True)
class GateArchiveInputs:
    """Source-bound original gate inputs; presence does not prove workflow success."""
    run_id: int
    run_attempt: int
    head_sha: str
    artifact_name: str
    artifact_sha256: str
    source_capture_sha256: str
    source_capture: bytes
    comparison_base: bytes
    coverage_xml: bytes
    original_report: bytes
    transcript: bytes
    input_sha256s: tuple[tuple[str, str], ...]
    original_report_passed: bool
    validator_executed: bool = False
    original_gate_success_proven: bool = False
    gate_eligible: bool = False
    admission_created: bool = False
    qualification_complete: bool = False


def inspect_completed_gate_archive(stage, inventory, stage_directory, source_capture, *, deadline):
    """Read fixed source-bound gate inputs from the completed runtime coverage archive.

    It checks the full completed-run projection, raw archive binding, actual ZIP projection,
    source bytes, fixed comparison base, and all selected member sizes before extraction.
    It does not prove successful workflow/test execution and does not execute a gate.
    """
    _remaining(deadline)
    _need(isinstance(stage, completed_run.CompletedRunStage)
          and isinstance(inventory, completed_validation.ParsedCompletedRunInventory), 'stage-type')
    bindings, projections = completed_c1._assert_projection(stage, inventory)
    _need(type(stage.repository_id) is int and stage.repository_id > 0
          and type(stage.run_id) is int and stage.run_id > 0
          and type(stage.run_attempt) is int and stage.run_attempt > 0
          and completed_run.SHA1.fullmatch(stage.head_sha) is not None
          and completed_run.SHA1.fullmatch(stage.source_tree) is not None
          and type(stage.total_archive_bytes) is int
          and stage.total_archive_bytes == sum(item.size for item in stage.artifacts)
          and stage.total_archive_bytes <= completed_run.MAX_TOTAL_ARCHIVE_BYTES
          and all(type(item.size) is int and 0 < item.size <= completed_run._artifact_limit(item.name)
                  and completed_run.SHA256.fullmatch(item.sha256) is not None for item in stage.artifacts),
          'stage-bounds')
    _need(type(source_capture) is bytes and 0 < len(source_capture) <= MAX_SOURCE_BYTES
          and hashlib.sha256(source_capture).hexdigest() == stage.source_capture_sha256,
          'source-capture-binding')
    capture = completed_run._json(source_capture, MAX_SOURCE_BYTES, 'source-capture-json')
    _need(set(capture) == {'schema', 'source', 'tree', 'count', 'files'}
          and capture.get('schema') == 'issue779-checkpoint2-source-v1'
          and type(capture.get('files')) is dict and type(capture.get('count')) is int
          and capture['count'] == len(capture['files']) > 0
          and inventory.source_count == capture['count']
          and capture.get('source') == stage.head_sha and capture.get('tree') == stage.source_tree,
          'source-capture-binding')
    artifact_name = f'issue779-checkpoint4-coverage-{stage.run_id}-{stage.run_attempt}'
    binding = bindings[artifact_name]
    projection = projections[artifact_name]
    _need(projection.source_sha256 == stage.source_capture_sha256, 'archive-source-projection')
    stage_root = Path(stage_directory)
    _need(stage_root.is_absolute() and stage_root.resolve(strict=True) == stage_root, 'stage-path')
    root_info = stage_root.lstat()
    _need(stat.S_ISDIR(root_info.st_mode) and not stat.S_ISLNK(root_info.st_mode)
          and stat.S_IMODE(root_info.st_mode) == 0o700 and root_info.st_uid == os.geteuid(), 'stage-shape')
    _need(set(os.listdir(stage_root)) == {item.staged_filename for item in stage.artifacts},
          'stage-directory-inventory')
    raw = completed_c1._open_bound_file(stage_root / binding.staged_filename, binding.size,
        binding.sha256, deadline=deadline, maximum=32 * 1024 * 1024, required_mode=0o600)
    rows, expanded = completed_validation._zip_directory(raw)
    actual_members = tuple(completed_validation.MemberProjection(row[0], row[3], row[4], row[6])
                           for row in rows)
    _need(projection.members == actual_members and projection.expanded_bytes == expanded,
          'archive-projection')
    _remaining(deadline)
    required = {CAPTURE_MEMBER, BASE_MEMBER, TRANSCRIPT_MEMBER, COVERAGE_MEMBER, GATE_MEMBER}
    member_rows = {row[0]: row for row in rows if not row[0].endswith('/')}
    _need(required <= set(member_rows), 'required-members')
    bounds = {CAPTURE_MEMBER: MAX_SOURCE_BYTES, BASE_MEMBER: MAX_COMPARISON_BASE_BYTES,
              TRANSCRIPT_MEMBER: MAX_TRANSCRIPT_BYTES, COVERAGE_MEMBER: MAX_COBERTURA_BYTES,
              GATE_MEMBER: MAX_GATE_BYTES}
    _need(all(member_rows[name][4] > 0 and member_rows[name][4] <= maximum
              for name, maximum in bounds.items())
          and sum(member_rows[name][4] for name in required) <= sum(bounds.values()),
          'selected-member-bounds')
    bodies = completed_validation._member_bodies(raw, rows, required, deadline)
    _need(bodies[CAPTURE_MEMBER] == source_capture, 'coverage-source-capture')
    _need(bodies[BASE_MEMBER] == FIXED_DIFF_BASE.encode('ascii') + b'\n', 'comparison-base')
    _need(bodies[TRANSCRIPT_MEMBER], 'transcript-empty')
    report = _parse(bodies[GATE_MEMBER], deadline)
    _validate(report)
    _need(report['coverage'] == ORIGINAL_COVERAGE_PATH, 'original-report-coverage-path')
    _need(bodies[COVERAGE_MEMBER], 'coverage-empty')
    hashes = tuple((name, hashlib.sha256(bodies[name]).hexdigest()) for name in sorted(required))
    _remaining(deadline)
    return GateArchiveInputs(stage.run_id, stage.run_attempt, stage.head_sha, artifact_name,
        binding.sha256, stage.source_capture_sha256, bodies[CAPTURE_MEMBER], bodies[BASE_MEMBER],
        bodies[COVERAGE_MEMBER], bodies[GATE_MEMBER], bodies[TRANSCRIPT_MEMBER], hashes, report['passed'])


def compare_gate_reports(original_raw, replay_raw, *, original_role_paths, replay_role_paths,
                         deadline):
    """Compare strict report data, normalizing only explicitly declared machine paths.

    `*_role_paths` maps `coverage` to the expected path in that report's own root.
    This fixed runtime lane uses a local Git base and no diff path. No evaluator is invoked.
    """
    _remaining(deadline)
    original = _parse(original_raw, deadline)
    replay = _parse(replay_raw, deadline)
    _validate(original)
    _validate(replay)
    _need(type(original_role_paths) is dict and set(original_role_paths) == {'coverage'}
          and type(replay_role_paths) is dict and set(replay_role_paths) == {'coverage'},
          'path-roles')
    _need(original['coverage'] == original_role_paths['coverage']
          and replay['coverage'] == replay_role_paths['coverage'], 'coverage-role-path')
    original_copy = deepcopy(original)
    replay_copy = deepcopy(replay)
    original_copy['coverage'] = replay_copy['coverage'] = '<coverage-role>'
    _need(original_copy == replay_copy, 'report-mismatch')
    _remaining(deadline)
    return GateReportComparison(hashlib.sha256(original_raw).hexdigest(),
        hashlib.sha256(replay_raw).hexdigest(), tuple(sorted(TOP_KEYS)), True)
