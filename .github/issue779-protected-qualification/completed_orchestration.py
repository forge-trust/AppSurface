"""Fixed replay ordering over an already resolved completed-run stage; no authority."""
from __future__ import annotations

from dataclasses import dataclass, field, fields, is_dataclass
import hashlib
import json
import math
import os
from pathlib import Path
import stat
import time

import completed_accounts
import completed_c1
import completed_c2
import completed_c3
import completed_gate_replay
import completed_measurement_chain
import completed_native_replay
import completed_native_result
import completed_run
import completed_test_inventory
import completed_validation

MAX_SECONDS = 1200
PHASES = ('c1', 'c2', 'c3', 'accounts', 'measurement', 'tests', 'native', 'gate')
CALLBACK_PHASES = ('c1', 'c2', 'c3', 'measurement')
MAX_PROJECTION_BYTES = 1024 * 1024
APPLICATION_CASES = ('normal', 'sdk-normal', 'readiness', 'factory-stall', 'cancel',
                     'stuck-descendant', 'disposal-failure')
LIFECYCLE_CASES = ('configure-sync-stall', 'registered-disposer-stall', 'post-stop-guards')


def _need(value, code):
    if not value:
        raise ValueError('completed-orchestration-' + code)


def _remaining(deadline):
    _need(type(deadline) in (int, float) and math.isfinite(deadline)
          and time.monotonic() < deadline, 'deadline')


def _directory(value, code, *, private=False, empty=False):
    path = Path(value)
    _need(path.is_absolute() and path.resolve(strict=True) == path, code + '-path')
    info = path.lstat()
    _need(stat.S_ISDIR(info.st_mode) and info.st_uid == os.geteuid()
          and (not private or stat.S_IMODE(info.st_mode) == 0o700), code + '-shape')
    _need(not empty or not os.listdir(path), code + '-not-empty')
    return path


def _source(capture, root, stage, deadline):
    binding = completed_c1._load_trusted_current_binding(deadline=deadline)
    value = completed_c1._decode(capture, completed_c1.MAX_SOURCE_BYTES, 'orchestration-capture')
    _need(value.get('source') == stage.head_sha and value.get('tree') == stage.source_tree
          and hashlib.sha256(capture).hexdigest() == stage.source_capture_sha256,
          'source-attribution')
    # Use independently checked-out validator code before any completed-source import.
    binding.verify_source(value, root, deadline=deadline)


def _fresh_source(root, deadline):
    _need(completed_c1._git(root, 'status', '--porcelain=v1', '--untracked-files=all',
                          '--ignored', deadline=deadline) == '', 'source-not-fresh')


def _check_directories(pins):
    for fd, path, identity in pins:
        opened, named = os.fstat(fd), path.lstat()
        _need(stat.S_ISDIR(opened.st_mode) and opened.st_nlink >= 2
              and completed_c1._node_identity(opened) == identity
              and completed_c1._node_identity(named) == identity, 'directory-custody')


def _check_parent(parent, scratch):
    expected = {'native', 'gate'} | {path.name for path, _owned in scratch.values()}
    _need(set(os.listdir(parent)) == expected, 'retained-parent-inventory')


def _identity(result, stage, *, head='head_sha', capture=None, tree=None):
    _need(type(result.run_id) is int and result.run_id == stage.run_id
          and type(result.run_attempt) is int and result.run_attempt == stage.run_attempt
          and getattr(result, head) == stage.head_sha, 'result-attribution')
    if capture:
        _need(getattr(result, capture) == stage.source_capture_sha256, 'result-capture')
    if tree:
        _need(getattr(result, tree) == stage.source_tree, 'result-tree')


def _without_claims(result):
    for name in ('native_acceptance', 'trusted_eligibility', 'qualification_complete',
                 'admission_created', 'gate_eligible', 'coverage_credit', 'all_sixteen_claimed',
                 'original_gate_success_proven', 'test_inventory_success_proven', 'validator_executed',
                 'original_tests_success_proven', 'original_merge_success_proven',
                 'original_workflow_success_proven'):
        if hasattr(result, name):
            _need(getattr(result, name) is False, 'result-claim')


def _check_result(phase, result, stage, inventory):
    expected = {'c1': completed_c1.C1CallbackProjection, 'c2': completed_c2.C2ReceiptProjection,
                'c3': completed_c3.C3ReceiptProjection, 'accounts': completed_accounts.CompletedAccounts,
                'measurement': completed_measurement_chain.CompletedMeasurementChainProjection,
                'tests': completed_test_inventory.CompletedTestInventory,
                'native': completed_native_replay.NativeReplayResult,
                'gate': completed_gate_replay.GateReplayResult}[phase]
    _need(type(result) is expected, 'result-type')
    _without_claims(result)
    if phase == 'c1':
        _identity(result, stage, head='source_sha', capture='source_capture_sha256')
        _need(result.selectors == completed_c1.SELECTORS and result.cases == completed_c1.CASES
              and type(result.passed) is int and result.passed == 16
              and type(result.skipped) is int and result.skipped == 0
              and type(result.exit_code) is int and result.exit_code == 0, 'c1-result')
        _artifact(stage, f'issue779-checkpoint1-current-{stage.run_id}-{stage.run_attempt}',
                  result.input_aggregate_archive_sha256,
                  f'issue779-checkpoint1-current-{stage.run_id}-{stage.run_attempt}')
        _need(result.input_aggregate_body_sha256 == result.output_sha256
              == inventory.aggregate.aggregate_sha256, 'c1-aggregate-binding')
    elif phase == 'c2':
        _identity(result, stage, head='source_sha', capture='source_capture_sha256', tree='source_tree')
        _need(tuple(row[0] for row in result.case_rows) == completed_c2.CASE_NAMES
              and all(type(row) is tuple and len(row) == 4 for row in result.case_rows), 'c2-cases')
        _artifact(stage, result.artifact_name, result.artifact_sha256,
                  completed_c2.ARTIFACT_PREFIX + f'{stage.run_id}-{stage.run_attempt}')
    elif phase == 'c3':
        _identity(result, stage, head='source', tree='source_tree')
        _need(result.application_cases == APPLICATION_CASES and result.lifecycle_cases == LIFECYCLE_CASES,
              'c3-cases')
        for name, digest, size, prefix in (
                (result.application_artifact_name, result.application_artifact_sha256,
                 result.application_artifact_size, completed_c3.APPLICATION_PREFIX),
                (result.lifecycle_artifact_name, result.lifecycle_artifact_sha256,
                 result.lifecycle_artifact_size, completed_c3.LIFECYCLE_PREFIX)):
            selected = _artifact(stage, name, digest, prefix + f'{stage.run_id}-{stage.run_attempt}')
            _need(type(size) is int and size == selected.size, 'result-artifact-size')
    elif phase == 'accounts':
        _identity(result, stage)
        _need(tuple(row.variant for row in result.accounts) == completed_accounts.VARIANTS, 'account-order')
        for row in result.accounts:
            _need(type(row) is completed_accounts.AccountReplay, 'account-type')
            _without_claims(row)
            _need(row.source_sha256 == stage.source_capture_sha256, 'account-capture')
            _artifact(stage, row.artifact_name, row.archive_sha256,
                      f'issue779-c4-accounts-{row.variant}-{stage.run_id}-{stage.run_attempt}')
    elif phase == 'measurement':
        _identity(result, stage, head='source', capture='source_capture_sha256')
        _need(result.selectors == completed_c1.SELECTORS and result.cases == completed_c1.CASES
              and result.workflow_identity == f'github-actions-{stage.run_id}-{stage.run_attempt}'
              and type(result.final_report_bytes) is bytes and result.final_report_bytes
              and hashlib.sha256(result.final_report_bytes).hexdigest() == result.final_report_sha256,
              'measurement-result')
    elif phase == 'tests':
        _identity(result, stage, capture='source_capture_sha256')
        _need(result.retained_project_inventory_verified is True
              and type(result.project_count) is int
              and 0 < result.project_count <= completed_test_inventory.MAX_PROJECTS
              and type(result.projects) is tuple and len(result.projects) == result.project_count
              and all(type(row) is completed_test_inventory.ProjectTestInventory
                      for row in result.projects)
              and type(result.test_case_count) is int and result.test_case_count > 0
              and type(result.skipped_case_count) is int
              and 0 <= result.skipped_case_count <= result.test_case_count
              and sum(row.test_cases for row in result.projects) == result.test_case_count
              and sum(row.skipped_cases for row in result.projects) == result.skipped_case_count,
              'test-inventory-result')
        _need(all(type(digest) is str and completed_run.SHA256.fullmatch(digest) is not None
                  for digest in (result.solution_sha256, result.timings_sha256,
                                 result.coverage_sha256)), 'test-inventory-digests')
    elif phase == 'native':
        _identity(result, stage, capture='source_capture_sha256')
        _need(result.sdk_version == completed_native_replay.SDK_VERSION
              and result.test_name == completed_native_result.TEST_METHOD and result.outcome == 'Passed'
              and result.cleanup_complete is False, 'native-result')
    else:
        _identity(result, stage, capture='source_capture_sha256')
        _need(result.base_commit == completed_gate_replay.completed_gate.FIXED_DIFF_BASE
              and result.reports_match is True and type(result.exit_code) is int and result.exit_code == 0
              and result.cleanup_complete is False, 'gate-result')


def _artifact(stage, name, digest, expected):
    selected = next((item for item in stage.artifacts if item.name == expected), None)
    _need(selected is not None and name == expected and digest == selected.sha256, 'result-artifact')
    return selected


def _projection(value):
    if is_dataclass(value):
        return {item.name: _projection(getattr(value, item.name)) for item in fields(value)}
    if type(value) is bytes:
        return {'bytes': len(value), 'sha256': hashlib.sha256(value).hexdigest()}
    if isinstance(value, Path):
        return str(value)
    if type(value) in (tuple, list):
        return [_projection(item) for item in value]
    _need(value is None or type(value) in (str, int, bool), 'projection-type')
    return value


@dataclass(frozen=True)
class PhaseObservation:
    phase: str
    projection_sha256: str
    projection_json: bytes


@dataclass(frozen=True)
class CompletedRunReplay:
    """Fixed procedure observations; never support registration or an admission input."""
    run_id: int
    run_attempt: int
    head_sha: str
    source_tree: str
    source_capture_sha256: str
    phases: tuple[PhaseObservation, ...]
    retained_native_root: Path
    retained_gate_root: Path
    gate_job_id: int
    cleanup_complete: bool = field(default=False, init=False)
    qualification_complete: bool = field(default=False, init=False)
    native_acceptance: bool = field(default=False, init=False)
    admission_created: bool = field(default=False, init=False)
    gate_eligible: bool = field(default=False, init=False)
    original_gate_success_proven: bool = field(default=False, init=False)
    test_inventory_success_proven: bool = field(default=False, init=False)


def replay_completed_run(stage, stage_directory, source_capture, source_root,
                         native_source_root, private_parent, *, deadline):
    """Inspect one resolved stage and invoke all eight validators in fixed order.

    The caller resolves the protected completed run first, contains both disposable source
    checkouts, and owns retained stage/build output disposal. No token or callback is accepted.
    `source_root` is the original runner path and stays fresh until gate replay runs last.
    """
    _remaining(deadline)
    _need(0 < deadline - time.monotonic() <= MAX_SECONDS, 'deadline-bound')
    _need(type(stage) is completed_run.CompletedRunStage, 'stage-type')
    _need(completed_gate_replay._linux_supported(), 'linux-required')
    stage_root = _directory(stage_directory, 'stage', private=True)
    source = _directory(source_root, 'source')
    native = _directory(native_source_root, 'native-source', private=True)
    parent = _directory(private_parent, 'private-parent', private=True, empty=True)
    _need(source == completed_gate_replay.SOURCE_MOUNT, 'source-mount')
    roots = (stage_root, source, native, parent)
    _need(all(not completed_c1._paths_overlap(left, right)
              for index, left in enumerate(roots) for right in roots[index + 1:]), 'root-overlap')
    jobs = completed_run.require_closed_jobs(stage)
    completed_run.require_completed_provider_run(stage)
    inventory = completed_validation.inspect_completed_run(stage, stage_root, source_capture, deadline=deadline)
    gate_job = next(row for row in jobs if row.name ==
        'Full current runtime proof and unchanged gates / Unchanged solution and codecov patch gates')
    for root in (source, native):
        _source(source_capture, root, stage, deadline)
        _fresh_source(root, deadline)
    _need(deadline - time.monotonic() >= completed_c1.AGGREGATE_SECONDS
          + completed_c1.POST_AGGREGATE_RESERVE + 30, 'callback-reserve')
    pins, directory_pins, scratch_pins, scratch, retained = [], [], [], {}, {}
    observations = []
    test_inventory = None
    native_parent, gate_parent = parent / 'native', parent / 'gate'
    primary = result = None
    try:
        for path in roots:
            fd = os.open(path, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
            directory_pins.append((fd, path, None))
            identity = completed_c1._node_identity(os.fstat(fd))
            directory_pins[-1] = (fd, path, identity)
        _check_directories(directory_pins)
        for binding in stage.artifacts:
            _remaining(deadline)
            path = stage_root / binding.staged_filename
            fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK | os.O_CLOEXEC)
            pins.append((fd, path, binding, None))
            info = os.fstat(fd)
            identity = completed_c1._node_identity(info)
            pins[-1] = (fd, path, binding, identity)
            _need(stat.S_ISREG(info.st_mode) and info.st_nlink == 1 and info.st_uid == os.geteuid()
                  and stat.S_IMODE(info.st_mode) == 0o600 and info.st_size == binding.size, 'stage-pin')
            completed_c1._open_bound_file(path, binding.size, binding.sha256, deadline=deadline,
                maximum=completed_run._artifact_limit(binding.name), required_mode=0o600,
                expected_identity=identity)
        for phase in CALLBACK_PHASES:
            path = parent / phase
            owned = {}
            scratch[phase] = (path, owned)
            completed_c1._mkdir(path, owned=owned, ownership_root=path)
            fd = os.open(path, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
            scratch_pins.append((fd, path, owned[()]))
        for path in (native_parent, gate_parent):
            completed_c1._mkdir(path, owned=retained, ownership_root=parent)
            fd = os.open(path, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
            directory_pins.append((fd, path, retained[(path.name,)]))
        _check_directories(directory_pins + scratch_pins)
        for phase in PHASES:
            _remaining(deadline)
            _check_directories(directory_pins + scratch_pins)
            _check_parent(parent, scratch)
            if phase == 'c1':
                value = completed_c1.run_fixed_c1_callbacks(stage, inventory, stage_root, source,
                    scratch[phase][0], deadline=deadline)
            elif phase == 'c2':
                value = completed_c2.run_fixed_c2_receipt(stage, inventory, stage_root, source,
                    scratch[phase][0], deadline=deadline)
            elif phase == 'c3':
                value = completed_c3.run_fixed_c3_receipts(stage, inventory, stage_root, source,
                    scratch[phase][0], deadline=deadline)
            elif phase == 'accounts':
                value = completed_accounts.replay_completed_accounts(stage, inventory, stage_root, source,
                    source_capture, deadline=deadline)
            elif phase == 'measurement':
                value = completed_measurement_chain.run_completed_measurement_chain(
                    stage, inventory, stage_root, source, scratch[phase][0], deadline=deadline)
            elif phase == 'native':
                value = completed_native_replay.run_native_replay(stage, inventory, stage_root,
                    source_capture, native, native_parent, deadline=deadline)
            elif phase == 'tests':
                value = completed_test_inventory.inspect_completed_test_inventory(
                    stage, inventory, stage_root, source_capture, source, deadline=deadline)
            else:
                value = completed_gate_replay.replay_completed_gate(stage, inventory, stage_root,
                    source_capture, source, gate_parent, deadline=deadline)
            _check_result(phase, value, stage, inventory)
            _check_directories(directory_pins + scratch_pins)
            _check_parent(parent, scratch)
            if phase == 'tests':
                test_inventory = value
            elif phase == 'native':
                _need((value.retained_output_parent, value.retained_test_output,
                       value.retained_logs, value.retained_work_root) ==
                      (str(native_parent), str(native_parent / completed_native_replay.OUTPUT_NAME),
                       str(native_parent / completed_native_replay.LOG_NAME),
                       str(native_parent / 'native-replay-state')), 'native-retained-paths')
            elif phase == 'gate':
                _need(value.retained_work_root == gate_parent / 'coverage-gate-replay', 'gate-retained-path')
                _need(test_inventory is not None and value.original_coverage_sha256
                      == value.replay_coverage_sha256 == test_inventory.coverage_sha256,
                      'gate-test-coverage-binding')
            raw = json.dumps(_projection(value), sort_keys=True, separators=(',', ':')).encode()
            _need(len(raw) <= MAX_PROJECTION_BYTES, 'projection-size')
            observations.append(PhaseObservation(phase, hashlib.sha256(raw).hexdigest(), raw))
            for root in (source, native):
                _source(source_capture, root, stage, deadline)
            if phase != 'gate':
                _fresh_source(source, deadline)
            if phase in ('c1', 'c2', 'c3', 'accounts', 'measurement', 'tests'):
                _fresh_source(native, deadline)
        _need(completed_validation.inspect_completed_run(stage, stage_root, source_capture,
            deadline=deadline) == inventory, 'inventory-changed')
        for fd, path, binding, identity in pins:
            _need(completed_c1._node_identity(os.fstat(fd)) == identity
                  and os.fstat(fd).st_nlink == 1, 'stage-pin-changed')
            completed_c1._open_bound_file(path, binding.size, binding.sha256, deadline=deadline,
                maximum=completed_run._artifact_limit(binding.name), required_mode=0o600,
                expected_identity=identity)
        result = CompletedRunReplay(stage.run_id, stage.run_attempt, stage.head_sha, stage.source_tree,
            stage.source_capture_sha256, tuple(observations), native_parent, gate_parent, gate_job.id)
        _check_directories(directory_pins + scratch_pins)
    except BaseException as error:
        primary = error
    finally:
        for path, owned in reversed(tuple(scratch.values())):
            if () not in owned:
                continue
            try:
                completed_c1._finish_invocation(parent, path, owned[()], owned, None)
            except BaseException as error:
                if primary is None:
                    primary = error
                elif hasattr(primary, 'add_note'):
                    primary.add_note('completed-orchestration-scratch-cleanup-failed')
        if primary is None:
            try:
                _check_directories(directory_pins)
                _need(set(os.listdir(parent)) == {'native', 'gate'}, 'retained-parent-inventory')
            except BaseException as error:
                primary = error
        for fd, _path, _binding, _identity in pins:
            try:
                os.close(fd)
            except BaseException as error:
                if primary is None:
                    primary = error
                elif hasattr(primary, 'add_note'):
                    primary.add_note('completed-orchestration-pin-close-failed')
        for fd, _path, _identity in directory_pins + scratch_pins:
            try:
                os.close(fd)
            except BaseException as error:
                if primary is None:
                    primary = error
                elif hasattr(primary, 'add_note'):
                    primary.add_note('completed-orchestration-directory-pin-close-failed')
    if primary is not None:
        if hasattr(primary, 'add_note'):
            primary.add_note('completed-orchestration-retained-workspace:' + str(parent))
        raise primary
    _remaining(deadline)
    _need(result is not None and tuple(row.phase for row in result.phases) == PHASES, 'incomplete')
    return result
