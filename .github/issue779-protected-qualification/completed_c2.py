"""Bounded, non-authorizing replay of the fixed C2 retained producer receipts."""
from __future__ import annotations

from dataclasses import dataclass
import hashlib
import json
import math
import os
from pathlib import Path
import stat
import sys
import time
import types

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / '.github/issue779-protected-qualification'))
import completed_run
import completed_validation
import completed_c1

MAX_RETAINED_FILES = 128
MAX_RETAINED_BYTES = 32 * 1024 * 1024
MAX_RETAINED_LEAF = 8 * 1024 * 1024
MAX_RETAINED_REPORT = 32 * 1024 * 1024
MAX_RECEIPT_BYTES = MAX_RETAINED_LEAF
MAX_LOG_BYTES = MAX_RETAINED_LEAF
MAX_INVENTORY_BYTES = MAX_RETAINED_LEAF
CASE_NAMES = ('normal', 'test-failure', 'cancel', 'quota', 'ignored-cancel',
              'artifact-mutation', 'cleanup-failure')
OUTPUT_OBSERVATION_CASES = frozenset(('normal', 'cancel', 'quota', 'ignored-cancel'))
ARTIFACT_PREFIX = 'issue779-csharp-checkpoint2-'


def _need(value, code):
    if not value:
        raise ValueError('completed-c2-' + code)


def _remaining(deadline):
    _need(type(deadline) in (int, float) and math.isfinite(deadline)
          and time.monotonic() < deadline, 'deadline')
    return deadline - time.monotonic()


def _sha(raw):
    return hashlib.sha256(raw).hexdigest()


def _decode(raw, maximum, code):
    _need(type(raw) is bytes and 0 < len(raw) <= maximum, code)
    try:
        value = json.loads(raw.decode('utf-8'), object_pairs_hook=completed_c1._pairs,
                           parse_constant=lambda _value: (_ for _ in ()).throw(ValueError(code)))
    except (UnicodeError, json.JSONDecodeError, RecursionError, ValueError):
        raise ValueError('completed-c2-' + code) from None
    _need(type(value) is dict, code)
    return value


def _read_private(path, *, deadline, maximum):
    path = Path(path)
    _need(path.is_absolute() and path.resolve(strict=True) == path, 'file-path')
    fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK | os.O_CLOEXEC)
    try:
        before = os.fstat(fd)
        _need(stat.S_ISREG(before.st_mode) and before.st_nlink == 1 and before.st_uid == os.geteuid()
              and stat.S_IMODE(before.st_mode) == 0o600 and 0 <= before.st_size <= maximum, 'file-shape')
        data = bytearray()
        while len(data) <= before.st_size:
            _remaining(deadline)
            chunk = os.read(fd, min(64 * 1024, before.st_size + 1 - len(data)))
            if not chunk:
                break
            data.extend(chunk)
        after = os.fstat(fd)
        named = os.stat(path, follow_symlinks=False)
        identity = lambda info: (info.st_dev, info.st_ino, info.st_mode, info.st_uid, info.st_gid,
                                 info.st_nlink, info.st_size, info.st_mtime_ns, info.st_ctime_ns)
        _need(len(data) == before.st_size and identity(before) == identity(after) == identity(named),
              'file-changed')
        return bytes(data)
    finally:
        os.close(fd)


def _load_captured_helpers(source, source_raw, *, current_binding, deadline):
    """Load only capture-verified producer and C2 driver source after checkout verification."""
    capture, captured_binding = completed_c1._load_capture_and_binding(
        source_raw, source, current_binding=current_binding, deadline=deadline)
    files = capture['files']
    chain_path = '.github/issue779-checkpoint1/measurement_chain.py'
    args_path = '.github/issue779-checkpoint4/measurement_arguments.py'
    driver_path = '.github/issue779-checkpoint2/run-native.py'
    staging_path = '.github/issue779-checkpoint2/staging.py'
    smoke_path = '.github/issue779-checkpoint4/package_smoke_evidence.py'

    def read_captured(relative, maximum):
        row = files.get(relative)
        _need(type(row) is dict and type(row.get('sha256')) is str
              and len(row['sha256']) == 64, 'captured-source-row')
        raw = completed_c1._read_trusted_source_file(source / relative, deadline=deadline, maximum=maximum)
        _need(_sha(raw) == row['sha256'], 'captured-source-hash')
        return raw

    chain_raw = read_captured(chain_path, 2 * 1024 * 1024)
    args_raw = read_captured(args_path, 256 * 1024)
    driver_raw = read_captured(driver_path, 2 * 1024 * 1024)
    staging_raw = read_captured(staging_path, 1024 * 1024)
    smoke_raw = read_captured(smoke_path, 1024 * 1024)
    sys.modules['binding'] = captured_binding
    args_module = types.ModuleType('measurement_arguments')
    args_module.__file__ = str(source / args_path)
    exec(compile(args_raw, args_module.__file__, 'exec', dont_inherit=True), args_module.__dict__)
    sys.modules['measurement_arguments'] = args_module
    staging_module = types.ModuleType('staging')
    staging_module.__file__ = str(source / staging_path)
    exec(compile(staging_raw, staging_module.__file__, 'exec', dont_inherit=True), staging_module.__dict__)
    sys.modules['staging'] = staging_module
    chain_module = types.ModuleType('issue779_completed_c2_measurement_chain')
    chain_module.__file__ = str(source / chain_path)
    exec(compile(chain_raw, chain_module.__file__, 'exec', dont_inherit=True), chain_module.__dict__)
    captured, helper_bodies = chain_module.source(source_raw, capture['source'], deadline)
    _need(captured == capture, 'capture-consensus')
    producer_module, measured_module = chain_module.validators(helper_bodies)
    sys.path.insert(0, str(source / '.github/issue779-checkpoint2'))
    driver_module = types.ModuleType('issue779_completed_c2_run_native')
    driver_module.__file__ = str(source / driver_path)
    exec(compile(driver_raw, driver_module.__file__, 'exec', dont_inherit=True), driver_module.__dict__)
    smoke_module = types.ModuleType('issue779_completed_c2_package_smoke')
    smoke_module.__file__ = str(source / smoke_path)
    exec(compile(smoke_raw, smoke_module.__file__, 'exec', dont_inherit=True), smoke_module.__dict__)
    return capture, captured_binding, chain_module, producer_module, measured_module, driver_module, smoke_module


def _check_retention_readonly(root, deadline):
    """Apply the original C2 128-file/32-MiB retention bounds without chmod side effects."""
    root = Path(root)
    count = total = nodes = 0
    for directory, names, files in os.walk(root, topdown=True, followlinks=False):
        _remaining(deadline)
        current = Path(directory)
        info = current.lstat()
        _need(stat.S_ISDIR(info.st_mode) and not stat.S_ISLNK(info.st_mode)
              and stat.S_IMODE(info.st_mode) == 0o700 and info.st_uid == os.geteuid(), 'retention-directory')
        nodes += 1
        _need(nodes <= completed_validation.MAX_ARCHIVE_MEMBERS, 'retention-node-count')
        for name in names:
            child = current / name
            child_info = child.lstat()
            _need(stat.S_ISDIR(child_info.st_mode) and not stat.S_ISLNK(child_info.st_mode), 'retention-link')
            nodes += 1
        for name in files:
            child = current / name
            child_info = child.lstat()
            limit = (MAX_RETAINED_REPORT if current == root
                     and name in ('native-producer-roots-c2.json', 'native-producer-roots.json')
                     else MAX_RETAINED_LEAF)
            _need(stat.S_ISREG(child_info.st_mode) and child_info.st_nlink == 1
                  and child_info.st_uid == os.geteuid() and stat.S_IMODE(child_info.st_mode) == 0o600
                  and child_info.st_size <= limit, 'retention-file')
            count += 1
            total += child_info.st_size
            nodes += 1
            _need(count <= MAX_RETAINED_FILES and total <= MAX_RETAINED_BYTES
                  and nodes <= completed_validation.MAX_ARCHIVE_MEMBERS, 'retention-total')
    return count, total


def _validate_native(receipt_raw, raw_files, *, source_raw, capture_sha256, head, source_tree, run_id, run_attempt,
                     artifact_name, artifact_sha256, chain_projection, producer, driver, deadline):
    _need(0 < len(receipt_raw) <= MAX_RECEIPT_BYTES, 'native-receipt-size')
    receipt = producer.decode(receipt_raw)
    rows = producer.native(receipt, head, producer.BASE)
    _need(type(rows) is list and len(rows) == len(CASE_NAMES), 'case-count')
    _need(receipt['source'] == head and receipt['base'] == producer.BASE, 'receipt-source-base')
    _need(receipt['native_acceptance'] is False and receipt['trusted_eligibility'] is False,
          'fixed-nonauthority')
    inventory_raw = raw_files.get('native/input-inventory.json')
    _need(type(inventory_raw) is bytes and len(inventory_raw) <= MAX_INVENTORY_BYTES,
          'input-inventory-size')
    inventory_sha = _sha(inventory_raw)
    checked_cases = []
    for case, row in zip(CASE_NAMES, rows):
        _remaining(deadline)
        _need(row['case'] == case and type(row.get('input_inventory_sha256')) is str
              and row['input_inventory_sha256'] == inventory_sha, 'case-order-inventory')
        case_raw = raw_files.get('native/' + case + '.json')
        _need(type(case_raw) is bytes and len(case_raw) <= MAX_RECEIPT_BYTES, 'case-receipt-size')
        case_value = producer.decode(case_raw)
        _need(case_value == row, 'case-receipt-match')
        channels = {}
        for channel in ('stdout', 'stderr'):
            name = 'native/' + case + '-owner.' + channel
            body = raw_files.get(name)
            _need(type(body) is bytes and len(body) <= MAX_LOG_BYTES
                  and _sha(body) == row[channel + '_sha256'], 'owner-log-binding')
            channels[channel] = body
        observation = driver.read_output_observation(channels['stderr'])
        _need(type(observation.get('pairs')) is list and len(observation['pairs']) == 2
              and observation.get('registered_pairs') == 2, 'paired-stream-observation')
        for pair in observation['pairs']:
            _need(pair.get('stdout_eof') is True and pair.get('stderr_eof') is True,
                  'paired-stream-eof')
            expected_failure = 'QuotaExceeded' if case == 'quota' else 'None'
            _need(pair.get('failure') == expected_failure, 'paired-stream-failure')
        if case in OUTPUT_OBSERVATION_CASES:
            _need(row.get('output_observation') == observation, 'receipt-output-observation')
        else:
            _need('output_observation' not in row, 'unexpected-receipt-output-observation')
        if case == 'quota':
            _need(observation.get('quota_exceeded') is True
                  and type(observation.get('received_bytes')) is int
                  and type(observation.get('received_byte_limit')) is int
                  and observation['received_bytes'] > observation['received_byte_limit'] == 16 * 1024 * 1024,
                  'quota-semantics')
            _need(row.get('quota_disposition') == driver.require_quota_disposition(b'', None),
                  'quota-disposition')
        else:
            _need(observation.get('quota_exceeded') is False, 'unexpected-quota')
        if case in ('quota', 'cleanup-failure'):
            _need(channels['stdout'] == b'', 'expected-empty-owner-stdout')
        if case == 'artifact-mutation':
            events = driver.read_mutation_observation(channels['stderr'])
            _need(events == row.get('artifact_mutation'), 'mutation-semantics')
        checked_cases.append((case, _sha(case_raw), _sha(channels['stdout']), _sha(channels['stderr'])))
    _need(type(source_raw) is bytes and _sha(source_raw) == capture_sha256, 'capture-hash')
    _need(type(chain_projection) is dict and chain_projection.get('kind') == 'producer-roots'
          and chain_projection.get('artifact_name') == artifact_name
          and chain_projection.get('native_receipt_sha256') == _sha(receipt_raw)
          and all(type(chain_projection.get(key)) is str and len(chain_projection[key]) == 64
                  for key in ('binding_sha256', 'report_sha256', 'inner_report_sha256')),
          'chain-projection')
    return C2ReceiptProjection(receipt['schema'], head, source_tree, run_id, run_attempt, tuple(checked_cases),
                               artifact_name, artifact_sha256, capture_sha256,
                               _sha(receipt_raw), inventory_sha, False, False)


def replay_extracted_c2(source_root, artifact_root, source_raw, *, head, source_tree, run_id,
                        run_attempt, artifact_name, artifact_sha256, deadline):
    """Replay one safely extracted authentic C2 artifact; the caller owns its extracted tree."""
    source, extracted = Path(source_root), Path(artifact_root)
    _need(source.is_absolute() and source.resolve(strict=True) == source and stat.S_ISDIR(source.lstat().st_mode),
          'source-root')
    _need(extracted.is_absolute() and extracted.resolve(strict=True) == extracted
          and stat.S_ISDIR(extracted.lstat().st_mode) and stat.S_IMODE(extracted.lstat().st_mode) == 0o700
          and extracted.lstat().st_uid == os.geteuid(), 'artifact-root')
    _need(not completed_c1._paths_overlap(source, extracted), 'source-artifact-overlap')
    _need(type(run_id) is int and run_id > 0 and type(run_attempt) is int and run_attempt > 0
          and artifact_name == ARTIFACT_PREFIX + f'{run_id}-{run_attempt}', 'run-artifact-identity')
    capture = _decode(source_raw, completed_c1.MAX_SOURCE_BYTES, 'source-capture')
    _need(capture.get('schema') == 'issue779-checkpoint2-source-v1' and capture.get('source') == head
          and capture.get('tree') == source_tree,
          'capture-stage-binding')
    missing = object()
    previous_modules = {name: sys.modules.get(name, missing)
                        for name in ('binding', 'measurement_arguments', 'staging')}
    previous_path = list(sys.path)
    try:
        current_binding = completed_c1._load_trusted_current_binding(deadline=deadline)
        capture, _captured_binding, chain, producer, measured, driver, smoke = _load_captured_helpers(
            source, source_raw, current_binding=current_binding, deadline=deadline)
        _need(capture['source'] == completed_c1._git(source, 'rev-parse', 'HEAD', deadline=deadline)
              and capture['tree'] == completed_c1._git(source, 'rev-parse', 'HEAD^{tree}', deadline=deadline),
              'source-run-binding')
        _need((extracted / 'source.json').is_file()
              and _read_private(extracted / 'source.json', deadline=deadline,
                                maximum=completed_c1.MAX_SOURCE_BYTES) == source_raw, 'artifact-capture')
        _check_retention_readonly(extracted, deadline)
        raw_files = chain.scan(extracted, deadline)
        smoke_files = {name.removeprefix('package-smoke-details/'): body for name, body in raw_files.items()
                       if name.startswith('package-smoke-details/')}
        _need(set(smoke_files) == set(smoke.OUTPUTS)
              and all(len(body) <= smoke.LEAF_LIMIT for body in smoke_files.values()), 'package-smoke-leaves')
        smoke.validate(smoke_files, head)
        _need(smoke_files['receipt.json'] == raw_files.get('package-smoke.json'), 'package-smoke-receipt-binding')
        required = {'native/native-receipt.json', 'native/input-inventory.json'}
        required.update('native/' + case + '.json' for case in CASE_NAMES)
        required.update('native/' + case + '-owner.' + channel
                        for case in CASE_NAMES for channel in ('stdout', 'stderr'))
        _need(required <= set(raw_files), 'native-required-members')
        chain_projection = chain.c2(raw_files, source_raw, head,
                                    f'github-actions-{run_id}-{run_attempt}', producer, measured, capture)
        return _validate_native(raw_files['native/native-receipt.json'], raw_files, source_raw=source_raw,
                                capture_sha256=_sha(source_raw), head=head, source_tree=source_tree, run_id=run_id,
                                run_attempt=run_attempt, artifact_name=artifact_name,
                                artifact_sha256=artifact_sha256, chain_projection=chain_projection,
                                producer=producer, driver=driver, deadline=deadline)
    finally:
        sys.path[:] = previous_path
        for name, value in previous_modules.items():
            if value is missing:
                sys.modules.pop(name, None)
            else:
                sys.modules[name] = value


@dataclass(frozen=True)
class C2ReceiptProjection:
    schema: str
    source_sha: str
    source_tree: str
    run_id: int
    run_attempt: int
    case_rows: tuple[tuple[str, str, str, str], ...]
    artifact_name: str
    artifact_sha256: str
    source_capture_sha256: str
    native_receipt_sha256: str
    input_inventory_sha256: str
    native_acceptance: bool
    trusted_eligibility: bool


def run_fixed_c2_receipt(stage, inventory, stage_directory, source_root, staging, *, deadline):
    """Extract the fixed C2 artifact and invoke its original data validators; no authority is returned."""
    _remaining(deadline)
    _need(isinstance(stage, completed_run.CompletedRunStage)
          and isinstance(inventory, completed_validation.ParsedCompletedRunInventory), 'input-types')
    _need(inventory.repository_id == stage.repository_id and inventory.run_id == stage.run_id
          and inventory.run_attempt == stage.run_attempt and inventory.head_sha == stage.head_sha
          and inventory.source_tree == stage.source_tree
          and inventory.source_capture_sha256 == stage.source_capture_sha256, 'inventory-stage-binding')
    source = Path(source_root)
    stage_root = Path(stage_directory)
    target = Path(staging)
    for path, reason in ((source, 'source-root'), (stage_root, 'stage-root'), (target, 'staging-root')):
        info = path.lstat()
        _need(path.is_absolute() and path.resolve(strict=True) == path and stat.S_ISDIR(info.st_mode)
              and not stat.S_ISLNK(info.st_mode), reason)
    _need(source.is_absolute() and source.resolve(strict=True) == source, 'source-root-canonical')
    _need(stat.S_IMODE(stage_root.lstat().st_mode) == 0o700 and stage_root.lstat().st_uid == os.geteuid(),
          'stage-root-mode')
    _need(stat.S_IMODE(target.lstat().st_mode) == 0o700 and target.lstat().st_uid == os.geteuid()
          and not any(target.iterdir()), 'staging-root-mode')
    _need(not completed_c1._paths_overlap(source, stage_root)
          and not completed_c1._paths_overlap(source, target)
          and not completed_c1._paths_overlap(stage_root, target), 'root-overlap')
    expected_name = ARTIFACT_PREFIX + f'{stage.run_id}-{stage.run_attempt}'
    all_bindings, all_projections = completed_c1._assert_projection(stage, inventory)
    _need({entry.name for entry in stage_root.iterdir()} ==
          {item.staged_filename for item in all_bindings.values()}, 'stage-directory-entries')
    bindings, projections = all_bindings, all_projections
    _need(expected_name in bindings and expected_name in projections, 'artifact-missing')
    binding, projection = bindings[expected_name], projections[expected_name]
    _need(projection.artifact_id == binding.artifact_id and projection.archive_bytes == binding.size
          and projection.archive_sha256 == binding.sha256 and binding.staged_filename == expected_name + '.zip',
          'artifact-binding')
    raw = completed_c1._open_bound_file(stage_root / binding.staged_filename, binding.size, binding.sha256,
                                       deadline=deadline, maximum=completed_run._artifact_limit(expected_name),
                                       required_mode=0o600)
    rows, expanded = completed_validation._zip_directory(raw)
    projected = tuple((m.path, m.compressed_bytes, m.expanded_bytes, m.compression) for m in projection.members)
    parsed = tuple((row[0], row[3], row[4], row[6]) for row in rows)
    _need(parsed == projected, 'archive-projection')
    leaves = [row for row in rows if not row[0].endswith('/')]
    _need(len(leaves) <= MAX_RETAINED_FILES and expanded <= MAX_RETAINED_BYTES,
          'retention-projection-total')
    for row in leaves:
        maximum = (MAX_RETAINED_REPORT if row[0] in
                   ('native-producer-roots-c2.json', 'native-producer-roots.json') else MAX_RETAINED_LEAF)
        _need(row[4] <= maximum, 'retention-projection-leaf')
    work = target / 'c2-fixed-receipt'
    owned = {}
    completed_c1._mkdir(work, owned=owned, ownership_root=work)
    work_identity = owned[()]
    primary = None
    result = None
    try:
        extracted = work / 'artifact'
        completed_c1._extract_archive(raw, projection, extracted, deadline=deadline, expanded_used=0,
                                     owned=owned, ownership_root=work)
        source_raw = _read_private(extracted / 'source.json', deadline=deadline, maximum=completed_c1.MAX_SOURCE_BYTES)
        _need(_sha(source_raw) == stage.source_capture_sha256, 'capture-stage-binding')
        result = replay_extracted_c2(source, extracted, source_raw, head=stage.head_sha,
                                     source_tree=stage.source_tree, run_id=stage.run_id,
                                     run_attempt=stage.run_attempt, artifact_name=expected_name,
                                     artifact_sha256=binding.sha256, deadline=deadline)
        _remaining(deadline)
    except BaseException as exc:
        primary = exc
    completed_c1._finish_invocation(target, work, work_identity, owned, primary)
    _remaining(deadline)
    return result
