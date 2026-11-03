"""Non-authorizing replay of retained C3 application and SDK lifecycle data."""
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

import completed_c1
import completed_run
import completed_validation

APPLICATION_PREFIX = 'issue779-checkpoint3-application-'
LIFECYCLE_PREFIX = 'issue779-c3sdk-lifecycle-'
BASE = '416b30919e6c4d03b634d9d3fe558884b02f0b91'
MAX_APPLICATION_FILES = 135
MAX_APPLICATION_BYTES = 96 * 1024 * 1024
MAX_LIFECYCLE_FILES = 26
MAX_LIFECYCLE_BYTES = 32 * 1024 * 1024
MAX_SELECTED_JSON = 32 * 1024 * 1024
MAX_COBERTURA_XML = 40 * 1024 * 1024


def _need(value, code):
    if not value:
        raise ValueError('completed-c3-' + code)


def _remaining(deadline):
    _need(type(deadline) in (int, float) and math.isfinite(deadline)
          and time.monotonic() < deadline, 'deadline')


def _sha(raw):
    return hashlib.sha256(raw).hexdigest()


def _read(path, *, deadline, maximum):
    path = Path(path)
    _need(path.is_absolute() and path.resolve(strict=True) == path, 'canonical-file')
    fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK | os.O_CLOEXEC)
    try:
        before = os.fstat(fd)
        _need(stat.S_ISREG(before.st_mode) and before.st_nlink == 1 and before.st_uid == os.geteuid()
              and stat.S_IMODE(before.st_mode) == 0o600 and 0 <= before.st_size <= maximum, 'file-shape')
        data = bytearray()
        while len(data) < before.st_size:
            _remaining(deadline)
            chunk = os.read(fd, min(64 * 1024, before.st_size - len(data)))
            _need(bool(chunk), 'short-read')
            data.extend(chunk)
        after = os.fstat(fd)
        named = os.stat(path, follow_symlinks=False)
        identity = lambda row: (row.st_dev, row.st_ino, row.st_mode, row.st_uid, row.st_gid,
                                row.st_nlink, row.st_size, row.st_mtime_ns, row.st_ctime_ns)
        _need(identity(before) == identity(after) == identity(named), 'file-changed')
        return bytes(data)
    finally:
        os.close(fd)


def _scan(root, *, deadline, maximum_files, maximum_bytes):
    root = Path(root)
    _need(root.is_absolute() and root.resolve(strict=True) == root and stat.S_ISDIR(root.lstat().st_mode)
          and stat.S_IMODE(root.lstat().st_mode) == 0o700 and root.lstat().st_uid == os.geteuid(), 'artifact-root')
    files = {}
    total = 0
    node_count = 0
    for directory, names, leaves in os.walk(root, topdown=True, followlinks=False):
        _remaining(deadline)
        parent = Path(directory)
        info = parent.lstat()
        _need(stat.S_ISDIR(info.st_mode) and not stat.S_ISLNK(info.st_mode)
              and stat.S_IMODE(info.st_mode) == 0o700 and info.st_uid == os.geteuid(), 'directory')
        node_count += 1
        for name in names:
            node_count += 1
            child = parent / name
            child_info = child.lstat()
            _need(stat.S_ISDIR(child_info.st_mode) and not stat.S_ISLNK(child_info.st_mode), 'directory-link')
        for name in leaves:
            child = parent / name
            relative = child.relative_to(root).as_posix()
            maximum = (MAX_SELECTED_JSON if child.name in ('native-supervision.json', 'native-sdk.json')
                       else MAX_COBERTURA_XML if child.name == 'native-supervision.cobertura.xml'
                       else 8 * 1024 * 1024)
            raw = _read(child, deadline=deadline, maximum=maximum)
            files[relative] = raw
            total += len(raw)
            node_count += 1
            _need(len(files) <= maximum_files and total <= maximum_bytes
                  and node_count <= completed_validation.MAX_ARCHIVE_MEMBERS, 'retention-bound')
    return files


def _strict_object(raw, *, maximum):
    _need(type(raw) is bytes and 0 < len(raw) <= maximum, 'json-size')
    def pairs(rows):
        value = {}
        for key, item in rows:
            _need(key not in value, 'json-duplicate')
            value[key] = item
        return value
    value = json.loads(raw.decode('utf-8'), object_pairs_hook=pairs,
                       parse_constant=lambda _: (_ for _ in ()).throw(ValueError('completed-c3-nonfinite')))
    _need(type(value) is dict, 'json-object')
    return value


def _load_callbacks(source, source_raw, *, head, source_tree, deadline):
    declared = _strict_object(source_raw, maximum=completed_c1.MAX_SOURCE_BYTES)
    _need(declared.get('schema') == 'issue779-checkpoint2-source-v1'
          and declared.get('source') == head and declared.get('tree') == source_tree,
          'capture-identity')
    _need(completed_c1._git(source, 'rev-parse', 'HEAD', deadline=deadline) == head
          and completed_c1._git(source, 'rev-parse', 'HEAD^{tree}', deadline=deadline) == source_tree,
          'source-binding')
    current = completed_c1._load_trusted_current_binding(deadline=deadline)
    capture, binding = completed_c1._load_capture_and_binding(
        source_raw, source, current_binding=current, deadline=deadline)
    paths = {
        'retained_application': '.github/issue779-checkpoint3/retained_application.py',
        'lifecycle_stalls': '.github/issue779-checkpoint3/lifecycle_stalls.py',
        'run_native': '.github/issue779-checkpoint3/run-native.py',
    }
    dependencies = ('.github/issue779-checkpoint2/run-native.py',
                    '.github/issue779-checkpoint2/staging.py')
    modules = {name: types.ModuleType('completed_c3_' + name) for name in paths}
    previous = {name: sys.modules.get(name, None) for name in
                ('binding', 'measurement_arguments', 'staging', 'checkpoint2_helpers', 'lifecycle_stalls',
                 'retained_application')}
    missing = object()
    previous = {name: (sys.modules[name] if name in sys.modules else missing) for name in previous}
    old_path = list(sys.path)
    try:
        sys.modules['binding'] = binding
        sys.modules.pop('measurement_arguments', None)
        sys.modules.pop('staging', None)
        sys.modules.pop('checkpoint2_helpers', None)
        sys.modules.pop('lifecycle_stalls', None)
        sys.modules.pop('retained_application', None)
        sys.path.insert(0, str(source / '.github/issue779-checkpoint3'))
        sys.path.insert(0, str(source / '.github/issue779-checkpoint2'))
        captured_bytes = {}
        for relative in (*paths.values(), *dependencies):
            _remaining(deadline)
            row = capture['files'].get(relative)
            _need(type(row) is dict, 'capture-callback-missing')
            raw = completed_c1._read_trusted_source_file(source / relative, deadline=deadline,
                                                         maximum=4 * 1024 * 1024)
            _need(_sha(raw) == row.get('sha256'), 'capture-callback-hash')
            captured_bytes[relative] = raw
        staging_module = types.ModuleType('staging')
        staging_module.__file__ = str(source / dependencies[1])
        exec(compile(captured_bytes[dependencies[1]], staging_module.__file__, 'exec',
                     dont_inherit=True), staging_module.__dict__)
        sys.modules['staging'] = staging_module
        for name, relative in paths.items():
            _remaining(deadline)
            module = modules[name]
            module.__file__ = str(source / relative)
            exec(compile(captured_bytes[relative], module.__file__, 'exec', dont_inherit=True), module.__dict__)
            if name in ('retained_application', 'lifecycle_stalls'):
                sys.modules[name] = module
        return modules, capture, previous, missing, old_path
    except BaseException:
        sys.path[:] = old_path
        for name, value in previous.items():
            if value is missing:
                sys.modules.pop(name, None)
            else:
                sys.modules[name] = value
        raise


@dataclass(frozen=True)
class C3ReceiptProjection:
    source: str
    source_tree: str
    run_id: int
    run_attempt: int
    application_artifact_name: str
    application_artifact_size: int
    application_artifact_sha256: str
    lifecycle_artifact_name: str
    lifecycle_artifact_size: int
    lifecycle_artifact_sha256: str
    application_receipt_sha256: str
    lifecycle_receipt_sha256: str
    application_cases: tuple[str, ...]
    lifecycle_cases: tuple[str, ...]
    native_acceptance: bool
    trusted_eligibility: bool


def replay_extracted_c3(source_root, application_root, lifecycle_root, application_source,
                        lifecycle_source, *, head, source_tree, run_id, run_attempt,
                        application_artifact_name, application_artifact_size, application_artifact_sha256,
                        lifecycle_artifact_name, lifecycle_artifact_size, lifecycle_artifact_sha256, deadline):
    """Invoke capture-verified fixed callbacks over two independently bound extracted artifacts.

    The caller must verify provider metadata and original ZIP size/SHA-256 before
    extraction and calling this helper. Extracted roots must be private and disjoint.
    """
    _remaining(deadline)
    source, app_root, life_root = map(Path, (source_root, application_root, lifecycle_root))
    _need(source.is_absolute() and source.resolve(strict=True) == source
          and stat.S_ISDIR(source.lstat().st_mode), 'source-root')
    _need(not completed_c1._paths_overlap(source, app_root)
          and not completed_c1._paths_overlap(source, life_root)
          and not completed_c1._paths_overlap(app_root, life_root), 'root-overlap')
    _need(type(run_id) is int and run_id > 0 and type(run_attempt) is int and run_attempt > 0,
          'run-identity')
    app_name = APPLICATION_PREFIX + f'{run_id}-{run_attempt}'
    life_name = LIFECYCLE_PREFIX + f'{run_id}-{run_attempt}'
    _need(application_artifact_name == app_name and lifecycle_artifact_name == life_name
          and type(application_artifact_size) is int and 0 < application_artifact_size
          <= completed_run._artifact_limit(app_name)
          and type(lifecycle_artifact_size) is int and 0 < lifecycle_artifact_size
          <= completed_run._artifact_limit(life_name)
          and all(type(value) is str and len(value) == 64
                  and all(c in '0123456789abcdef' for c in value)
                  for value in (application_artifact_sha256, lifecycle_artifact_sha256)), 'artifact-identity')
    _need(type(application_source) is bytes and type(lifecycle_source) is bytes
          and application_source == lifecycle_source, 'capture-match')
    capture = _strict_object(application_source, maximum=completed_c1.MAX_SOURCE_BYTES)
    _need(capture.get('schema') == 'issue779-checkpoint2-source-v1'
          and capture.get('source') == head and capture.get('tree') == source_tree, 'capture-identity')
    previous_modules = None
    try:
        modules, verified_capture, previous_modules, missing, old_path = _load_callbacks(
            source, application_source, head=head, source_tree=source_tree, deadline=deadline)
        _need(verified_capture == capture,
            'source-binding')
        app_files = _scan(app_root, deadline=deadline, maximum_files=MAX_APPLICATION_FILES,
                          maximum_bytes=MAX_APPLICATION_BYTES)
        life_files = _scan(life_root, deadline=deadline, maximum_files=MAX_LIFECYCLE_FILES,
                           maximum_bytes=MAX_LIFECYCLE_BYTES)
        _need(app_files.get('source.json') == application_source
              and life_files.get('source.json') == lifecycle_source, 'artifact-capture')
        app_receipt_raw = app_files.get('native/native-receipt.json')
        life_receipt_raw = life_files.get('lifecycle-stall-receipt.json')
        app_receipt = _strict_object(app_receipt_raw, maximum=8 * 1024 * 1024)
        life_receipt = _strict_object(life_receipt_raw, maximum=8 * 1024 * 1024)
        runner = modules['run_native']
        modules['retained_application'].validate(
            app_receipt, source=head, base=BASE, files=app_files,
            events=runner.events,
            output_accounting=runner.output_accounting,
            owner_reference_guards=runner.owner_reference_guards, deadline=deadline)
        modules['lifecycle_stalls'].validate_retained_receipt(
            life_receipt, {key: value for key, value in life_files.items()
                           if key != 'source.json' and not key.startswith('native/')},
            source=head, base=BASE, deadline=deadline)
        _need(app_receipt.get('base') == BASE and life_receipt.get('base') == BASE, 'fixed-base')
        return C3ReceiptProjection(head, source_tree, run_id, run_attempt, app_name,
                                   application_artifact_size, application_artifact_sha256,
                                   life_name, lifecycle_artifact_size, lifecycle_artifact_sha256,
                                   _sha(app_receipt_raw), _sha(life_receipt_raw),
                                   tuple(row['case'] for row in app_receipt['cases']),
                                   tuple(row['case'] for row in life_receipt['cases']), False, False)
    finally:
        if previous_modules is not None:
            sys.path[:] = old_path
            for name, value in previous_modules.items():
                if value is missing:
                    sys.modules.pop(name, None)
                else:
                    sys.modules[name] = value


def run_fixed_c3_receipts(stage, inventory, stage_directory, source_root, staging, *, deadline):
    """Select and safely extract the two fixed C3 artifacts from the closed 37-role stage."""
    _remaining(deadline)
    _need(isinstance(stage, completed_run.CompletedRunStage)
          and isinstance(inventory, completed_validation.ParsedCompletedRunInventory), 'input-types')
    _need(inventory.repository_id == stage.repository_id and inventory.run_id == stage.run_id
          and inventory.run_attempt == stage.run_attempt and inventory.head_sha == stage.head_sha
          and inventory.source_tree == stage.source_tree
          and inventory.source_capture_sha256 == stage.source_capture_sha256, 'inventory-stage-binding')
    source, stage_root, target = map(Path, (source_root, stage_directory, staging))
    for path in (source, stage_root, target):
        info = path.lstat()
        _need(path.is_absolute() and path.resolve(strict=True) == path and stat.S_ISDIR(info.st_mode)
              and not stat.S_ISLNK(info.st_mode), 'root-shape')
    _need(stat.S_IMODE(stage_root.lstat().st_mode) == 0o700 and stage_root.lstat().st_uid == os.geteuid()
          and stat.S_IMODE(target.lstat().st_mode) == 0o700 and target.lstat().st_uid == os.geteuid()
          and not any(target.iterdir()), 'private-root')
    _need(not completed_c1._paths_overlap(source, stage_root)
          and not completed_c1._paths_overlap(source, target)
          and not completed_c1._paths_overlap(stage_root, target), 'root-overlap')
    names = (APPLICATION_PREFIX + f'{stage.run_id}-{stage.run_attempt}',
             LIFECYCLE_PREFIX + f'{stage.run_id}-{stage.run_attempt}')
    bindings, projections = completed_c1._assert_projection(stage, inventory)
    expected_stage_files = {binding.staged_filename for binding in bindings.values()}
    stage_entries = list(stage_root.iterdir())
    _need({entry.name for entry in stage_entries} == expected_stage_files
          and all(entry.is_file() and not entry.is_symlink() for entry in stage_entries), 'stage-directory-entries')
    work = target / 'c3-fixed-receipt'
    owned = {}
    completed_c1._mkdir(work, owned=owned, ownership_root=work)
    work_identity = owned[()]
    primary = None
    result = None
    try:
        raw_archives = {}
        for name in names:
            _remaining(deadline)
            _need(name in bindings and name in projections, 'artifact-missing')
            binding, projection = bindings[name], projections[name]
            _need(binding.staged_filename == name + '.zip'
                  and projection.artifact_id == binding.artifact_id
                  and projection.archive_bytes == binding.size
                  and projection.archive_sha256 == binding.sha256, 'artifact-binding')
            raw = completed_c1._open_bound_file(stage_root / binding.staged_filename, binding.size,
                                                binding.sha256, deadline=deadline,
                                                maximum=completed_run._artifact_limit(name), required_mode=0o600)
            rows, _expanded = completed_validation._zip_directory(raw)
            projected = tuple((member.path, member.compressed_bytes, member.expanded_bytes, member.compression)
                              for member in projection.members)
            parsed = tuple((row[0], row[3], row[4], row[6]) for row in rows)
            _need(parsed == projected, 'archive-projection')
            leaves = [row for row in rows if not row[0].endswith('/')]
            max_files, max_bytes = ((MAX_APPLICATION_FILES, MAX_APPLICATION_BYTES)
                                    if name == names[0] else (MAX_LIFECYCLE_FILES, MAX_LIFECYCLE_BYTES))
            _need(len(leaves) <= max_files and sum(row[4] for row in leaves) <= max_bytes,
                  'retention-projection-total')
            for row in leaves:
                limit = (MAX_SELECTED_JSON if row[0] in ('native-supervision.json', 'native-sdk.json')
                         else MAX_COBERTURA_XML if row[0] == 'native-supervision.cobertura.xml'
                         else 8 * 1024 * 1024)
                _need(row[4] <= limit, 'retention-projection-leaf')
            raw_archives[name] = (raw, binding, projection)
        app_root = work / 'application'
        life_root = work / 'lifecycle'
        app_raw, app_binding, app_projection = raw_archives[names[0]]
        expanded = completed_c1._extract_archive(app_raw, app_projection, app_root, deadline=deadline,
                                                expanded_used=0, owned=owned, ownership_root=work)
        life_raw, life_binding, life_projection = raw_archives[names[1]]
        completed_c1._extract_archive(life_raw, life_projection, life_root, deadline=deadline,
                                      expanded_used=expanded, owned=owned, ownership_root=work)
        capture_raw = _read(app_root / 'source.json', deadline=deadline,
                            maximum=completed_c1.MAX_SOURCE_BYTES)
        _need(_sha(capture_raw) == stage.source_capture_sha256
              and _read(life_root / 'source.json', deadline=deadline,
                        maximum=completed_c1.MAX_SOURCE_BYTES) == capture_raw, 'artifact-capture')
        result = replay_extracted_c3(
            source, app_root, life_root, capture_raw, capture_raw,
            head=stage.head_sha, source_tree=stage.source_tree, run_id=stage.run_id,
            run_attempt=stage.run_attempt,
            application_artifact_name=names[0], application_artifact_size=app_binding.size,
            application_artifact_sha256=app_binding.sha256,
            lifecycle_artifact_name=names[1], lifecycle_artifact_size=life_binding.size,
            lifecycle_artifact_sha256=life_binding.sha256, deadline=deadline)
        _remaining(deadline)
    except BaseException as exc:
        primary = exc
    completed_c1._finish_invocation(target, work, work_identity, owned, primary)
    _remaining(deadline)
    return result
