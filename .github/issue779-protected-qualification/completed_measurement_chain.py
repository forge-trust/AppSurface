"""Bounded, non-authorizing custody for the fixed completed C1 measurement chain."""
from __future__ import annotations

from dataclasses import dataclass
import hashlib
import io
import math
import os
from pathlib import Path, PurePosixPath
import stat
import sys
import time
import types
import zipfile

import completed_c1
import completed_run
import completed_validation

ORDER = ('None', 'N04', 'N05', 'N06', 'N07', 'N08', 'N09', 'N10',
         'N11', 'N12', 'N13', 'N14', 'N15', 'N16')
MAX_SOURCE_BYTES = 1024 * 1024
MAX_MEMBER_BYTES = 64 * 1024 * 1024
MAX_SEGMENT_BYTES = 192 * 1024 * 1024
MAX_FILES = 1024
MAX_NODES = 2048
MAX_DEPTH = 64
MAX_WORK_BYTES = 512 * 1024 * 1024


def _need(ok, reason):
    if not ok:
        raise ValueError('completed-measurement-chain-' + reason)


def _remaining(deadline):
    _need(type(deadline) in (int, float) and math.isfinite(deadline) and time.monotonic() < deadline,
          'deadline')
    return deadline - time.monotonic()


def _sha(raw):
    return hashlib.sha256(raw).hexdigest()


def _measured_names(run_id, attempt):
    _need(type(run_id) is int and run_id > 0 and type(attempt) is int and attempt > 0, 'run-identity')
    return tuple(f'issue779-c1-measured-{selector}-{run_id}-{attempt}' for selector in ORDER)


@dataclass(frozen=True)
class CompletedMeasurementChainProjection:
    """Raw final report observation; it conveys no native or coverage authority."""

    run_id: int
    run_attempt: int
    source: str
    source_capture_sha256: str
    workflow_identity: str
    selectors: tuple[str, ...]
    cases: tuple[str, ...]
    final_report_bytes: bytes
    final_report_sha256: str
    native_acceptance: bool = False
    coverage_credit: bool = False
    all_sixteen_claimed: bool = False


def _projection_matches(raw, binding, projection):
    rows, expanded = completed_validation._zip_directory(raw)
    parsed = tuple((row[0], row[3], row[4], row[6]) for row in rows)
    projected = tuple((member.path, member.compressed_bytes, member.expanded_bytes, member.compression)
                      for member in projection.members)
    _need(parsed == projected and expanded == projection.expanded_bytes
          and len(raw) == binding.size == projection.archive_bytes
          and _sha(raw) == binding.sha256 == projection.archive_sha256
          and binding.name == projection.artifact_name, 'archive-projection')
    return rows, expanded


def _capture_member(raw, rows, deadline):
    matches = [row for row in rows if row[0] == 'source.json']
    _need(len(matches) == 1 and 0 < matches[0][4] <= MAX_SOURCE_BYTES, 'source-member')
    try:
        with zipfile.ZipFile(io.BytesIO(raw), 'r') as archive:
            info = next(item for item in archive.infolist() if item.filename == 'source.json')
            chunks = bytearray()
            with archive.open(info, 'r') as stream:
                while True:
                    _remaining(deadline)
                    part = stream.read(min(64 * 1024, MAX_SOURCE_BYTES + 1 - len(chunks)))
                    if not part:
                        break
                    chunks.extend(part)
                    _need(len(chunks) <= matches[0][4] <= MAX_SOURCE_BYTES, 'source-member-size')
            _need(len(chunks) == matches[0][4], 'source-member-size')
            return bytes(chunks)
    except (zipfile.BadZipFile, OSError, StopIteration):
        raise ValueError('completed-measurement-chain-source-member') from None


def _safe_member(name):
    _need(type(name) is str and name and '\\' not in name and not name.startswith('/'), 'member-path')
    canonical = name[:-1] if name.endswith('/') else name
    parts = canonical.split('/')
    _need(canonical and all(part not in ('', '.', '..') for part in parts), 'member-path')
    return tuple(parts)


def _extract_chain(raw, rows, destination, capture_raw, deadline, owned):
    """Read only top-level source.json and the selected chain/ payload from a fully checked ZIP."""
    node_paths = set()
    selected_file_count = 0
    selected_directory_count = 0
    selected = []
    for row in rows:
        name = row[0]
        parts = _safe_member(name)
        _need(len(parts) <= MAX_DEPTH, 'member-depth')
        if name == 'source.json' or name.startswith('chain/'):
            selected.append(row)
            node_paths.update(tuple(parts[:i]) for i in range(1, len(parts) + 1))
            if name.endswith('/'):
                selected_directory_count += 1
            else:
                selected_file_count += int(name.startswith('chain/'))
    _need(len(node_paths) <= MAX_NODES, 'node-count')
    _need(selected_file_count <= MAX_FILES and selected_directory_count <= MAX_NODES, 'segment-node-count')
    _need(any(row[0] == 'source.json' for row in selected)
          and any(row[0] == 'chain/source.json' for row in selected)
          and any(row[0] == 'chain/coverage.json' for row in selected), 'required-members')
    selected_bytes = sum(row[4] for row in selected if not row[0].endswith('/')
                         and row[0].startswith('chain/'))
    _need(selected_bytes <= MAX_SEGMENT_BYTES, 'segment-expanded-bound')
    destination = Path(destination)
    completed_c1._mkdir(destination, owned=owned, ownership_root=destination)
    root_info = completed_c1._node_identity(destination.lstat())
    total = 0
    source_bodies = {}
    try:
        with zipfile.ZipFile(io.BytesIO(raw), 'r') as archive:
            infos = archive.infolist()
            _need(len(infos) == len(rows), 'member-count')
            selected_names = {row[0] for row in selected}
            for info, row in zip(infos, rows):
                _remaining(deadline)
                name = row[0]
                if name not in selected_names:
                    continue
                parts = _safe_member(name)
                if name == 'source.json':
                    _need(info.file_size <= MAX_SOURCE_BYTES, 'source-member-size')
                    with archive.open(info, 'r') as stream:
                        body = bytearray()
                        while True:
                            _remaining(deadline)
                            part = stream.read(min(64 * 1024, info.file_size + 1 - len(body)))
                            if not part:
                                break
                            body.extend(part)
                            _need(len(body) <= info.file_size and len(body) <= MAX_SOURCE_BYTES,
                                  'source-member-size')
                    _need(len(body) == info.file_size, 'source-member-size')
                    source_bodies[name] = bytes(body)
                    continue
                if name.endswith('/'):
                    _need(info.file_size == 0 and info.compress_size == 0, 'directory-size')
                    relative = parts[1:] if parts and parts[0] == 'chain' else parts
                    parent = destination
                    for component in relative:
                        child = parent / component
                        if not child.exists():
                            completed_c1._mkdir(child, owned=owned, ownership_root=destination)
                        parent = child
                    continue
                _need(parts and parts[0] == 'chain', 'chain-member-path')
                relative = parts[1:]
                _need(relative, 'chain-member-path')
                parent = destination
                for component in relative[:-1]:
                    child = parent / component
                    if not child.exists():
                        completed_c1._mkdir(child, owned=owned, ownership_root=destination)
                    else:
                        info_child = child.lstat()
                        _need(stat.S_ISDIR(info_child.st_mode) and not stat.S_ISLNK(info_child.st_mode)
                              and info_child.st_uid == os.geteuid(), 'directory-alias')
                    parent = child
                _need(0 <= info.file_size <= MAX_MEMBER_BYTES and total + info.file_size <= MAX_SEGMENT_BYTES,
                      'segment-expanded-bound')
                chunks = bytearray()
                with archive.open(info, 'r') as stream:
                    while True:
                        _remaining(deadline)
                        part = stream.read(min(64 * 1024, info.file_size + 1 - len(chunks)))
                        if not part:
                            break
                        chunks.extend(part)
                        _need(len(chunks) <= info.file_size, 'member-growth')
                _need(len(chunks) == info.file_size, 'member-size')
                body = bytes(chunks)
                if name in ('source.json', 'chain/source.json'):
                    source_bodies[name] = body
                target = destination.joinpath(*relative)
                completed_c1._write_bytes(target, body, deadline=deadline,
                                          owned=owned, ownership_root=destination)
                total += len(body)
        _need(source_bodies.get('source.json') == capture_raw
              and source_bodies.get('chain/source.json') == capture_raw, 'source-consensus')
        _need(total <= MAX_SEGMENT_BYTES, 'segment-expanded-bound')
        return owned, root_info, total
    except BaseException:
        # Caller owns cleanup; retaining exact ownership makes substitutions fail closed.
        raise


def _load_chain_module(source_root, capture_raw, source_binding, deadline):
    source_root = Path(source_root)
    relative = '.github/issue779-checkpoint1/measurement_chain.py'
    capture = completed_c1._decode(capture_raw, MAX_SOURCE_BYTES, 'source-capture-json')
    row = capture.get('files', {}).get(relative)
    _need(type(row) is dict and type(row.get('sha256')) is str, 'chain-source-capture-row')
    path = source_root / relative
    raw = completed_c1._read_trusted_source_file(path, deadline=deadline, maximum=8 * 1024 * 1024)
    _need(_sha(raw) == row['sha256'], 'chain-source-hash')
    module = types.ModuleType('issue779_completed_measurement_chain_source')
    module.__file__ = str(path)
    missing = object()
    previous = sys.modules.get('binding', missing)
    previous_path = list(sys.path)
    sys.modules['binding'] = source_binding
    try:
        exec(compile(raw, str(path), 'exec', dont_inherit=True), module.__dict__)
    finally:
        sys.path[:] = previous_path
        if previous is missing:
            sys.modules.pop('binding', None)
        else:
            sys.modules['binding'] = previous
    return module


def _pin_source_imports(chain, source_capture, source_binding, deadline):
    """Pin the fixed captured helper imports and remember all ambient state for restoration."""
    root = Path(chain.ROOT).resolve(strict=True)
    source_dir = Path(chain.__file__).resolve(strict=True).parent
    _need(source_dir == root / '.github/issue779-checkpoint1', 'source-import-root')
    capture = completed_c1._decode(source_capture, MAX_SOURCE_BYTES, 'source-capture-json')
    paths = {
        'measurement_arguments': '.github/issue779-checkpoint4/measurement_arguments.py',
        'publish': '.github/issue779-checkpoint1/publish.py',
        'measurement': '.github/issue779-checkpoint1/measurement.py',
        'measurement_custody': '.github/issue779-checkpoint1/measurement_custody.py',
    }
    names = ('binding', *paths)
    missing = object()
    state = {'missing': missing, 'modules': {name: sys.modules.get(name, missing) for name in names},
             'path': list(sys.path)}
    try:
        sys.modules['binding'] = source_binding
        for name, relative in paths.items():
            row = capture.get('files', {}).get(relative)
            _need(type(row) is dict and type(row.get('sha256')) is str, 'source-import-capture-row')
            path = root / relative
            raw = completed_c1._read_trusted_source_file(path, deadline=deadline, maximum=8 * 1024 * 1024)
            _need(_sha(raw) == row['sha256']
                  and format(stat.S_IMODE(path.lstat().st_mode), '04o') == row['mode'], 'source-import-hash')
            module = types.ModuleType(name)
            module.__file__ = str(path)
            exec(compile(raw, str(path), 'exec', dont_inherit=True), module.__dict__)
            sys.modules[name] = module
        sys.path.insert(0, str(source_dir))
        return state
    except BaseException:
        _restore_source_imports(state)
        raise


def _restore_source_imports(state):
    sys.path[:] = state['path']
    for name, prior in state['modules'].items():
        if prior is state['missing']:
            sys.modules.pop(name, None)
        else:
            sys.modules[name] = prior


def _check_links(raws, selector, baseline_seeds, previous_binding, previous_report):
    current_seeds = {name: body for name, body in raws.items() if name.startswith('lineage/c2/')}
    _need(current_seeds == baseline_seeds, 'c2-seed-mismatch')
    if previous_binding is not None:
        stem = f'lineage/selectors/{selector}/'
        _need(raws.get(stem + 'prior-binding.json') == previous_binding
              and raws.get(stem + 'prior-report.json') == previous_report, 'predecessor-segment-mismatch')


def _replay_segments(run_id, run_attempt, bindings, projections, stage_root, staging,
                    source_head, source_capture, source_capture_sha256, workflow_identity, chain, *,
                    source_binding=None,
                    stage_archive_bytes, deadline):
    """Fixed-order replay seam; callers supply only the source-verified original module."""
    names = _measured_names(run_id, run_attempt)
    _need(type(bindings) is dict and type(projections) is dict
          and set(bindings) == set(names) == set(projections), 'artifact-set')
    _need(workflow_identity == f'github-actions-{run_id}-{run_attempt}', 'workflow-identity')
    _need(type(stage_archive_bytes) is int and 0 < stage_archive_bytes <= completed_run.MAX_TOTAL_ARCHIVE_BYTES
          and type(source_capture) is bytes and 0 < len(source_capture) <= MAX_SOURCE_BYTES
          and _sha(source_capture) == source_capture_sha256, 'stage-byte-binding')
    if hasattr(chain, 'ROOT'):
        capture_value = completed_c1._decode(source_capture, MAX_SOURCE_BYTES, 'source-capture-json')
        _need(capture_value.get('source') == source_head
              and completed_c1._git(chain.ROOT, 'rev-parse', 'HEAD', deadline=deadline) == source_head
              and completed_c1._git(chain.ROOT, 'rev-parse', 'HEAD^{tree}', deadline=deadline)
                  == capture_value.get('tree'), 'source-head-tree-binding')
    stage_root, target = Path(stage_root), Path(staging)
    _need(stage_root.is_absolute() and stage_root.resolve(strict=True) == stage_root
          and stat.S_ISDIR(stage_root.lstat().st_mode) and stat.S_IMODE(stage_root.lstat().st_mode) == 0o700
          and stage_root.lstat().st_uid == os.geteuid(), 'stage-directory')
    required_names = {bindings[name].staged_filename for name in names}
    _need(required_names <= {entry.name for entry in stage_root.iterdir()}, 'stage-directory-entries')
    _need(target.is_absolute() and target.resolve(strict=True) == target and stat.S_ISDIR(target.lstat().st_mode)
          and stat.S_IMODE(target.lstat().st_mode) == 0o700 and target.lstat().st_uid == os.geteuid()
          and not any(target.iterdir()), 'staging-directory')
    _need(not completed_c1._paths_overlap(stage_root, target), 'root-overlap')
    stage_identity = completed_c1._node_identity(target.lstat())
    baseline_seeds = None
    previous_binding = previous_report = None
    final_report = None
    final_cases = None
    source_state = None
    if hasattr(chain, 'ROOT'):
        _need(source_binding is not None, 'source-binding-required')
        source_state = _pin_source_imports(chain, source_capture, source_binding, deadline)
    try:
        for index, (selector, artifact_name) in enumerate(zip(ORDER, names)):
            _remaining(deadline)
            binding = bindings[artifact_name]
            projection = projections[artifact_name]
            raw = completed_c1._open_bound_file(stage_root / binding.staged_filename, binding.size,
                                                 binding.sha256, deadline=deadline,
                                                 maximum=completed_run._artifact_limit(artifact_name),
                                                 required_mode=0o600)
            rows, _ = _projection_matches(raw, binding, projection)
            _need(projection.source_sha256 == source_capture_sha256, 'archive-source-projection')
            chain_expanded = sum(row[4] for row in rows
                                 if row[0].startswith('chain/') and not row[0].endswith('/'))
            baseline_seed_bytes = (sum(map(len, baseline_seeds.values())) if baseline_seeds is not None else
                                   sum(row[4] for row in rows if row[0].startswith('chain/lineage/c2/')
                                       and not row[0].endswith('/')))
            _need(stage_archive_bytes + len(raw) + 2 * chain_expanded + baseline_seed_bytes
                  + len(previous_binding or b'') + len(previous_report or b'')
                  + len(source_capture) + MAX_MEMBER_BYTES <= MAX_WORK_BYTES,
                  'working-set-bound')
            segdir = target / 'current-segment'
            owned = {}
            seg_info = None
            primary = None
            try:
                owned, seg_info, expanded = _extract_chain(raw, rows, segdir, source_capture, deadline, owned)
                seeds = chain.seed_files(segment_raws := chain.scan(segdir, deadline))
                _need(expanded == chain_expanded and sum(map(len, seeds.values())) <= baseline_seed_bytes,
                      'working-set-projection')
                record = chain.verify_segment(source_capture, segment_raws, workflow_identity,
                                              ORDER[:index + 1], deadline)
                if index == 0:
                    baseline_seeds = seeds
                _check_links(segment_raws, selector, baseline_seeds, previous_binding, previous_report)
                previous_binding = segment_raws['chain-binding.json']
                previous_report = segment_raws['coverage.json']
                if selector == ORDER[-1]:
                    final_report = segment_raws['coverage.json']
                    final_cases = tuple(record['cases'])
            except BaseException as exc:
                primary = exc
                if seg_info is None and () in owned:
                    seg_info = owned[()]
            if seg_info is not None:
                completed_c1._finish_invocation(target, segdir, seg_info, owned, primary)
            elif primary is not None:
                raise primary
            _need(completed_c1._node_identity(target.lstat()) == stage_identity and not any(target.iterdir()),
                  'staging-clean')
            del raw, rows, segment_raws, seeds, record, owned, primary
    finally:
        if source_state is not None:
            _restore_source_imports(source_state)
    _need(final_report is not None and final_cases == tuple(f'N{i:02d}' for i in range(1, 17)), 'final-report')
    _remaining(deadline)
    return CompletedMeasurementChainProjection(run_id, run_attempt, source_head, source_capture_sha256,
        workflow_identity, ORDER, final_cases, final_report, _sha(final_report))


def run_completed_measurement_chain(stage, inventory, stage_directory, source_root, staging, *, deadline):
    """Sequentially revalidate fixed C1 segments while keeping peak bytes within 512 MiB.

    Every archive is matched to the full structural projection. Only the top-level source
    capture and each `chain/` tree are read; unrelated ZIP payloads are not validated here.
    """
    _remaining(deadline)
    _need(isinstance(stage, completed_run.CompletedRunStage), 'stage-type')
    _need(type(inventory) is completed_validation.ParsedCompletedRunInventory, 'inventory-type')
    bindings, projections = completed_c1._assert_projection(stage, inventory)
    names = _measured_names(stage.run_id, stage.run_attempt)
    _need(all(name in bindings for name in names), 'artifact-set')
    stage_root, source, target = Path(stage_directory), Path(source_root), Path(staging)
    _need(stage_root.is_absolute() and stage_root.resolve(strict=True) == stage_root
          and stat.S_ISDIR(stage_root.lstat().st_mode) and stat.S_IMODE(stage_root.lstat().st_mode) == 0o700
          and stage_root.lstat().st_uid == os.geteuid(), 'stage-directory')
    _need({entry.name for entry in stage_root.iterdir()} == {item.staged_filename for item in bindings.values()},
          'stage-directory-entries')
    _need(source.is_absolute() and source.resolve(strict=True) == source and stat.S_ISDIR(source.lstat().st_mode),
          'source-root')
    _need(target.is_absolute() and target.resolve(strict=True) == target and stat.S_ISDIR(target.lstat().st_mode)
          and stat.S_IMODE(target.lstat().st_mode) == 0o700 and target.lstat().st_uid == os.geteuid()
          and not any(target.iterdir()), 'staging-directory')
    _need(not completed_c1._paths_overlap(source, target) and not completed_c1._paths_overlap(stage_root, target)
          and not completed_c1._paths_overlap(source, stage_root), 'root-overlap')
    stage_identity = completed_c1._node_identity(target.lstat())
    first_name = names[0]
    first_binding = bindings[first_name]
    first_projection = projections[first_name]
    first_raw = completed_c1._open_bound_file(stage_root / first_binding.staged_filename,
        first_binding.size, first_binding.sha256, deadline=deadline,
        maximum=completed_run._artifact_limit(first_name), required_mode=0o600)
    first_rows, _ = _projection_matches(first_raw, first_binding, first_projection)
    capture_raw = _capture_member(first_raw, first_rows, deadline)
    del first_raw
    _need(_sha(capture_raw) == stage.source_capture_sha256, 'capture-stage-digest')
    current_binding = completed_c1._load_trusted_current_binding(deadline=deadline)
    capture, source_binding = completed_c1._load_capture_and_binding(
        capture_raw, source, current_binding=current_binding, deadline=deadline)
    _need(capture.get('source') == stage.head_sha and capture.get('tree') == stage.source_tree
          and _sha(capture_raw) == stage.source_capture_sha256
          and completed_c1._git(source, 'rev-parse', 'HEAD', deadline=deadline) == stage.head_sha
          and completed_c1._git(source, 'rev-parse', 'HEAD^{tree}', deadline=deadline) == stage.source_tree,
          'source-stage-binding')
    chain = _load_chain_module(source, capture_raw, source_binding, deadline)
    identity = f'github-actions-{stage.run_id}-{stage.run_attempt}'
    result = _replay_segments(stage.run_id, stage.run_attempt,
        {name: bindings[name] for name in names}, {name: projections[name] for name in names},
        stage_root, target, stage.head_sha, capture_raw, stage.source_capture_sha256, identity, chain,
        source_binding=source_binding,
        stage_archive_bytes=stage.total_archive_bytes, deadline=deadline)
    _need(result.source == stage.head_sha and result.source_capture_sha256 == stage.source_capture_sha256,
          'result-stage-binding')
    return result
