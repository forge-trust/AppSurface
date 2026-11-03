"""Credentialed preparation and closeout data checks; never execute the replay here."""
from __future__ import annotations

from contextlib import contextmanager
from dataclasses import dataclass, field, fields
import hashlib
import json
import math
import os
from pathlib import Path
import stat
import time

import completed_c1
import completed_orchestration
import completed_run
import completed_source
import completed_validation

MAX_PREPARE_SECONDS = 1320
MAX_CLOSE_SECONDS = 180
MAX_SOURCE_SECONDS = 120
MAX_OBSERVATION_BYTES = 8 * 1024 * 1024
CLAIMS = frozenset((
    'admission_created', 'native_acceptance', 'qualification_complete', 'gate_eligible',
    'trusted_eligibility', 'coverage_credit', 'all_sixteen_claimed', 'validator_executed',
    'original_gate_success_proven', 'test_inventory_success_proven',
    'original_tests_success_proven', 'original_merge_success_proven',
    'original_workflow_success_proven',
    'cleanup_complete',
))


def _need(value, code):
    if not value:
        raise ValueError('completed-controller-' + code)


def _remaining(deadline, maximum=None):
    _need(type(deadline) in (int, float) and math.isfinite(deadline), 'deadline')
    remaining = deadline - time.monotonic()
    _need(remaining > 0 and (maximum is None or remaining <= maximum), 'deadline-bound')
    return remaining


def _phase_deadline(deadline, maximum):
    _remaining(deadline)
    return min(deadline, time.monotonic() + maximum)


def _canonical(value):
    encoder = json.JSONEncoder(sort_keys=True, separators=(',', ':'))
    body = bytearray()
    for part in encoder.iterencode(completed_orchestration._projection(value)):
        encoded = part.encode()
        _need(len(body) + len(encoded) <= MAX_OBSERVATION_BYTES, 'observation-size')
        body.extend(encoded)
    return bytes(body)


def _sha(raw):
    return hashlib.sha256(raw).hexdigest()


def _directory(value, *, private=False):
    path = Path(value)
    _need(path.is_absolute() and path.resolve(strict=True) == path, 'directory-path')
    info = path.lstat()
    _need(stat.S_ISDIR(info.st_mode) and info.st_uid == os.geteuid()
          and (not private or stat.S_IMODE(info.st_mode) == 0o700), 'directory-shape')
    return path


def _check_pins(pins):
    for fd, path, identity in pins:
        opened = os.fstat(fd)
        _need(stat.S_ISDIR(opened.st_mode) and opened.st_nlink >= 2
              and completed_c1._node_identity(opened) == identity
              and completed_c1._node_identity(path.lstat()) == identity, 'directory-custody')


@contextmanager
def _pin_roots(roots, expected=None):
    pins = []
    primary = None
    try:
        for path in roots:
            fd = os.open(path, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
            pins.append((fd, path, None))
            pins[-1] = (fd, path, completed_c1._node_identity(os.fstat(fd)))
        _check_pins(pins)
        identities = tuple((path, identity) for _fd, path, identity in pins)
        _need(expected is None or identities == expected, 'preparation-roots-changed')
        yield identities
        _check_pins(pins)
    except BaseException as error:
        primary = error
        raise
    finally:
        close_error = None
        for fd, _path, _identity in pins:
            try:
                os.close(fd)
            except OSError as error:
                if close_error is None:
                    close_error = error
        if close_error is not None:
            if primary is None:
                raise close_error
            if hasattr(primary, 'add_note'):
                primary.add_note('completed-controller-directory-close-failed')


def _roots(stage_directory, source_root, native_source_root):
    roots = (_directory(completed_source.ROOT), _directory(stage_directory, private=True),
             _directory(source_root), _directory(native_source_root, private=True))
    _need(all(not completed_c1._paths_overlap(left, right)
              for index, left in enumerate(roots) for right in roots[index + 1:]), 'root-overlap')
    return roots


@contextmanager
def _pin_archives(stage, directory, deadline):
    pins = []
    primary = None

    def check():
        for fd, path, item, identity in pins:
            opened = os.fstat(fd)
            _need(opened.st_nlink == 1 and completed_c1._node_identity(opened) == identity,
                  'archive-custody')
            completed_c1._open_bound_file(
                path, item.size, item.sha256, deadline=deadline,
                maximum=completed_run._artifact_limit(item.name), required_mode=0o600,
                expected_identity=identity)

    try:
        for item in stage.artifacts:
            _remaining(deadline)
            path = directory / item.staged_filename
            fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK | os.O_CLOEXEC)
            pins.append((fd, path, item, None))
            info = os.fstat(fd)
            _need(stat.S_ISREG(info.st_mode) and info.st_nlink == 1
                  and info.st_uid == os.geteuid() and stat.S_IMODE(info.st_mode) == 0o600
                  and info.st_size == item.size, 'archive-shape')
            pins[-1] = (fd, path, item, completed_c1._node_identity(info))
        check()
        yield
        check()
    except BaseException as error:
        primary = error
        raise
    finally:
        close_error = None
        for fd, _path, _item, _identity in pins:
            try:
                os.close(fd)
            except OSError as error:
                if close_error is None:
                    close_error = error
        if close_error is not None:
            if primary is None:
                raise close_error
            if hasattr(primary, 'add_note'):
                primary.add_note('completed-controller-archive-close-failed')


def _stage_binding(stage, binding):
    _need(type(binding) is completed_source.SourceBinding, 'source-binding-type')
    completed_run.require_completed_provider_run(stage)
    _need((stage.repository_id, stage.run_id, stage.run_attempt, stage.head_sha,
           stage.source_tree, stage.source_capture_sha256) ==
          (binding.repository_id, binding.run_id, binding.run_attempt, binding.head_sha,
           binding.source_tree, binding.source_capture_sha256), 'source-stage-binding')
    _need(all(getattr(binding, name) is False for name in
              ('admission_created', 'native_acceptance', 'qualification_complete', 'gate_eligible')),
          'source-binding-claim')


@dataclass(frozen=True)
class CompletedPreparation:
    """In-process source/provider/archive comparison data, never a portable credential."""
    stage: completed_run.CompletedRunStage
    source_binding: completed_source.SourceBinding
    inventory: completed_validation.ParsedCompletedRunInventory
    stage_directory: Path
    source_root: Path
    native_source_root: Path
    root_identities: tuple[tuple[Path, tuple], ...]


def prepare_completed_run(event_raw, environment, validator_capture, source_capture,
                          source_root, native_source_root, stage_directory, token, *, deadline):
    """Bind protected source and download/inspect fixed archives without running target code.

    This credentialed phase must remain separate from the token-free replay execution.
    Neither this API nor closeout calls a build, target callback, or native procedure.
    """
    _remaining(deadline, MAX_PREPARE_SECONDS)
    roots = _roots(stage_directory, source_root, native_source_root)
    _need(not os.listdir(roots[1]), 'stage-not-empty')
    with _pin_roots(roots) as identities:
        binding = completed_source.validate_completed_source(
            event_raw, environment, validator_capture, source_capture, roots[2], roots[3],
            deadline=_phase_deadline(deadline, MAX_SOURCE_SECONDS))
        workflows = {path: completed_c1._read_trusted_source_file(
            roots[2] / path, deadline=deadline, maximum=completed_run.MAX_WORKFLOW_BYTES)
            for path in (completed_run.WORKFLOW, completed_run.RUNTIME_WORKFLOW)}
        stage = completed_run.resolve_completed_run(
            event_raw, environment, binding.head_sha, source_capture, workflows, token, roots[1],
            total_timeout=min(completed_run.MAX_TOTAL_SECONDS, _remaining(deadline)))
        _stage_binding(stage, binding)
        inventory = completed_validation.inspect_completed_run(
            stage, roots[1], source_capture, deadline=deadline)
        _remaining(deadline)
        result = CompletedPreparation(stage, binding, inventory, roots[1], roots[2], roots[3], identities)
    _remaining(deadline)
    return result


def _false_claims(value):
    if type(value) is dict:
        for key, item in value.items():
            if key in CLAIMS:
                _need(item is False, 'replay-claim')
            _false_claims(item)
    elif type(value) is list:
        for item in value:
            _false_claims(item)


def _phase_value(phase, value):
    """Restore exact fixed result types from the local canonical comparison projection."""
    modules = completed_orchestration
    expected = {
        'c1': modules.completed_c1.C1CallbackProjection,
        'c2': modules.completed_c2.C2ReceiptProjection,
        'c3': modules.completed_c3.C3ReceiptProjection,
        'accounts': modules.completed_accounts.CompletedAccounts,
        'measurement': modules.completed_measurement_chain.CompletedMeasurementChainProjection,
        'tests': modules.completed_test_inventory.CompletedTestInventory,
        'native': modules.completed_native_replay.NativeReplayResult,
        'gate': modules.completed_gate_replay.GateReplayResult,
    }[phase]
    _need(set(value) == {item.name for item in fields(expected)}, 'projection-schema')

    def tuples(item):
        return tuple(tuples(child) for child in item) if type(item) is list else item

    restored = {key: tuples(item) for key, item in value.items()}
    if phase == 'accounts':
        restored['accounts'] = tuple(modules.completed_accounts.AccountReplay(**item)
                                     for item in value['accounts'])
    elif phase == 'tests':
        restored['projects'] = tuple(modules.completed_test_inventory.ProjectTestInventory(**item)
                                     for item in value['projects'])
    elif phase == 'measurement':
        descriptor = value['final_report_bytes']
        _need(type(descriptor) is dict and set(descriptor) == {'bytes', 'sha256'}
              and type(descriptor['bytes']) is int and 0 < descriptor['bytes']
              <= modules.completed_measurement_chain.MAX_MEMBER_BYTES
              and descriptor['sha256'] == value['final_report_sha256']
              and type(descriptor['sha256']) is str
              and completed_run.SHA256.fullmatch(descriptor['sha256']) is not None,
              'measurement-report-descriptor')
        restored['final_report_bytes'] = b''
    elif phase == 'gate':
        restored['retained_work_root'] = Path(value['retained_work_root'])
    return expected(**{item.name: restored[item.name] for item in fields(expected) if item.init})


def _replay_binding(preparation, replay):
    stage = preparation.stage
    _need(type(replay) is completed_orchestration.CompletedRunReplay, 'replay-type')
    _need((replay.run_id, replay.run_attempt, replay.head_sha, replay.source_tree,
           replay.source_capture_sha256) ==
          (stage.run_id, stage.run_attempt, stage.head_sha, stage.source_tree,
           stage.source_capture_sha256), 'replay-identity')
    _need(all(getattr(replay, name) is False for name in CLAIMS if hasattr(replay, name))
          and replay.cleanup_complete is False, 'replay-claim')
    jobs = completed_run.require_closed_jobs(stage)
    gate_job = next(job for job in jobs if job.name ==
        'Full current runtime proof and unchanged gates / Unchanged solution and codecov patch gates')
    _need(type(replay.gate_job_id) is int and replay.gate_job_id == gate_job.id, 'gate-job')
    _need(type(replay.phases) is tuple and len(replay.phases) == len(completed_orchestration.PHASES)
          and all(type(item) is completed_orchestration.PhaseObservation for item in replay.phases)
          and tuple(item.phase for item in replay.phases) == completed_orchestration.PHASES, 'replay-phases')
    parsed = {}
    for phase in replay.phases:
        _need(type(phase.projection_json) is bytes
              and _sha(phase.projection_json) == phase.projection_sha256, 'projection-digest')
        value = completed_run._json(phase.projection_json, completed_orchestration.MAX_PROJECTION_BYTES,
                                    'completed-controller-projection-json')
        _need(json.dumps(value, sort_keys=True, separators=(',', ':')).encode() == phase.projection_json,
              'projection-canonical')
        _false_claims(value)
        head_field = {'c1': 'source_sha', 'c2': 'source_sha', 'c3': 'source',
                      'measurement': 'source'}.get(phase.phase, 'head_sha')
        _need(type(value.get('run_id')) is int and value['run_id'] == stage.run_id
              and type(value.get('run_attempt')) is int and value['run_attempt'] == stage.run_attempt
              and value.get(head_field) == stage.head_sha, 'projection-identity')
        if phase.phase in ('c1', 'c2', 'measurement', 'tests', 'native', 'gate'):
            _need(value.get('source_capture_sha256') == stage.source_capture_sha256,
                  'projection-capture')
        if phase.phase in ('c2', 'c3'):
            _need(value.get('source_tree') == stage.source_tree, 'projection-tree')
        result = _phase_value(phase.phase, value)
        if phase.phase == 'measurement':
            _need(result.selectors == completed_orchestration.completed_c1.SELECTORS
                  and result.cases == completed_orchestration.completed_c1.CASES
                  and result.workflow_identity == f'github-actions-{stage.run_id}-{stage.run_attempt}',
                  'measurement-result')
        else:
            completed_orchestration._check_result(phase.phase, result, stage, preparation.inventory)
        parsed[phase.phase] = value
    _need(parsed['tests'].get('coverage_sha256') == parsed['gate'].get('original_coverage_sha256')
          == parsed['gate'].get('replay_coverage_sha256')
          and type(parsed['tests'].get('coverage_sha256')) is str
          and completed_run.SHA256.fullmatch(parsed['tests']['coverage_sha256']) is not None,
          'projection-coverage-binding')
    native = _directory(replay.retained_native_root, private=True)
    gate = _directory(replay.retained_gate_root, private=True)
    _need(native.name == 'native' and gate.name == 'gate' and native.parent == gate.parent,
          'replay-retained-roots')
    parent = _directory(native.parent, private=True)
    _need(set(os.listdir(parent)) == {'native', 'gate'}
          and all(not completed_c1._paths_overlap(parent, path)
                  for path in (completed_source.ROOT, preparation.stage_directory,
                               preparation.source_root, preparation.native_source_root)), 'replay-parent')
    _need(parsed['native'].get('retained_output_parent') == str(native)
          and parsed['native'].get('retained_test_output') ==
          str(native / completed_orchestration.completed_native_replay.OUTPUT_NAME)
          and parsed['native'].get('retained_logs') ==
          str(native / completed_orchestration.completed_native_replay.LOG_NAME)
          and parsed['native'].get('retained_work_root') == str(native / 'native-replay-state')
          and parsed['gate'].get('retained_work_root') == str(gate / 'coverage-gate-replay'),
          'projection-retained-paths')
    return parent, native, gate


@dataclass(frozen=True)
class CompletedValidationObservation:
    """Matched completion metadata and replay bytes; no qualification or authorization."""
    repository_id: int
    run_id: int
    run_attempt: int
    head_sha: str
    validator_head_sha: str
    source_capture_sha256: str
    validator_capture_sha256: str
    provider_revalidation_sha256: str
    replay_sha256: str
    inventory_sha256: str
    phase_sha256s: tuple[tuple[str, str], ...]
    cleanup_complete: bool = field(default=False, init=False)
    admission_created: bool = field(default=False, init=False)
    native_acceptance: bool = field(default=False, init=False)
    qualification_complete: bool = field(default=False, init=False)
    gate_eligible: bool = field(default=False, init=False)
    original_gate_success_proven: bool = field(default=False, init=False)
    test_inventory_success_proven: bool = field(default=False, init=False)


def close_completed_run(preparation, replay, validator_capture, source_capture, token, *, deadline):
    """Recheck fixed provider/source/archive facts after a separately contained token-free replay."""
    _remaining(deadline, MAX_CLOSE_SECONDS)
    _need(type(preparation) is CompletedPreparation, 'preparation-type')
    _stage_binding(preparation.stage, preparation.source_binding)
    roots = _roots(preparation.stage_directory, preparation.source_root, preparation.native_source_root)
    _need(type(preparation.inventory) is completed_validation.ParsedCompletedRunInventory, 'inventory-type')
    retained = _replay_binding(preparation, replay)
    with (_pin_roots(roots, preparation.root_identities), _pin_roots(retained),
          _pin_archives(preparation.stage, roots[1], deadline)):
        completed_source.verify_completed_source(
            preparation.source_binding, validator_capture, source_capture, roots[2], roots[3],
            deadline=_phase_deadline(deadline, MAX_SOURCE_SECONDS))
        inventory = completed_validation.inspect_completed_run(
            preparation.stage, roots[1], source_capture, deadline=deadline)
        _need(inventory == preparation.inventory, 'inventory-changed')
        provider = completed_run.revalidate_completed_run(
            preparation.stage, token,
            deadline=_phase_deadline(deadline, completed_run.MAX_REVALIDATION_SECONDS))
        _need(type(provider) is completed_run.ProviderRevalidation
              and provider.provider_run == preparation.stage.provider_run
              and (provider.repository_id, provider.run_id, provider.run_attempt, provider.head_sha)
              == (preparation.stage.repository_id, replay.run_id, replay.run_attempt, replay.head_sha),
              'provider-revalidation-binding')
        jobs = completed_run.require_closed_jobs(preparation.stage)
        _need(provider.jobs == jobs and provider.job_ids == tuple(job.id for job in jobs)
              and provider.workflow_id == preparation.stage.provider_run.workflow_id
              and provider.workflow_sha256s == tuple(sorted(preparation.stage.workflow_sha256s))
              and provider.artifact_ids == tuple(sorted(
                  (item.name, item.artifact_id, item.size, item.sha256)
                  for item in preparation.stage.artifacts)), 'provider-revalidation-facts')
        completed_source.verify_completed_source(
            preparation.source_binding, validator_capture, source_capture, roots[2], roots[3],
            deadline=_phase_deadline(deadline, MAX_SOURCE_SECONDS))
        _need(completed_validation.inspect_completed_run(
            preparation.stage, roots[1], source_capture, deadline=deadline) == inventory,
            'inventory-changed')
        raws = (_canonical(provider), _canonical(replay), _canonical(inventory))
        _need(all(len(raw) <= MAX_OBSERVATION_BYTES for raw in raws), 'observation-size')
        _remaining(deadline)
        result = CompletedValidationObservation(
            preparation.stage.repository_id, replay.run_id, replay.run_attempt, replay.head_sha,
            preparation.source_binding.validator_head_sha, replay.source_capture_sha256,
            preparation.source_binding.validator_capture_sha256, *(_sha(raw) for raw in raws),
            tuple((phase.phase, phase.projection_sha256) for phase in replay.phases))
    _remaining(deadline)
    return result
