"""Fixed, non-authorizing execution of the existing C1 retained-evidence callbacks."""
from __future__ import annotations

from dataclasses import dataclass
import hashlib
import io
import json
import math
import os
from pathlib import Path, PurePosixPath
import stat
import sys
import time
import types
import zipfile

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / '.github/issue779-protected-consumer'))
sys.path.insert(0, str(ROOT / '.github/issue779-protected-qualification'))
import completed_run
import completed_validation

SELECTORS = ('None', 'N04', 'N05', 'N06', 'N07', 'N08', 'N09', 'N10', 'N11', 'N12', 'N13', 'N14', 'N15', 'N16')
CASES = tuple(f'N{index:02d}' for index in range(1, 17))
MAX_SOURCE_BYTES = 1024 * 1024
MAX_MEMBER_BYTES = 64 * 1024 * 1024
MAX_TOTAL_EXPANDED = 512 * 1024 * 1024
AGGREGATE_SECONDS = 120
POST_AGGREGATE_RESERVE = 5
MAX_STAGING_DEPTH = 64


def _need(value, code):
    if not value:
        raise ValueError('completed-c1-' + code)


def _remaining(deadline):
    _need(type(deadline) in (int, float) and math.isfinite(deadline)
          and time.monotonic() < deadline, 'deadline')
    return deadline - time.monotonic()


def _sha(raw):
    return hashlib.sha256(raw).hexdigest()


def _pairs(items):
    result = {}
    folded = set()
    for key, value in items:
        alias = key.casefold() if type(key) is str else None
        _need(type(key) is str and key not in result and alias not in folded, 'json-duplicate-key')
        result[key] = value
        folded.add(alias)
    return result


def _decode(raw, maximum, code):
    _need(type(raw) is bytes and 0 < len(raw) <= maximum, code)
    try:
        result = json.loads(raw.decode('utf-8'), object_pairs_hook=_pairs,
                            parse_constant=lambda _value: (_ for _ in ()).throw(ValueError(code)))
    except (UnicodeError, json.JSONDecodeError, RecursionError, ValueError):
        raise ValueError('completed-c1-' + code) from None
    _need(type(result) is dict, code)
    return result


def _open_bound_file(path, expected_size, expected_sha, *, deadline, maximum, required_mode=None,
                     expected_identity=None):
    path = Path(path)
    _need(path.is_absolute() and path.resolve(strict=True) == path, 'file-path')
    fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK | os.O_CLOEXEC)
    try:
        before = os.fstat(fd)
        _need(stat.S_ISREG(before.st_mode) and before.st_nlink == 1 and before.st_uid == os.geteuid()
              and before.st_size == expected_size and 0 < before.st_size <= maximum
              and (required_mode is None or stat.S_IMODE(before.st_mode) == required_mode)
              and (expected_identity is None or _node_identity(before) == expected_identity), 'file-shape')
        data = bytearray()
        while True:
            _remaining(deadline)
            part = os.read(fd, min(64 * 1024, expected_size + 1 - len(data)))
            if not part:
                break
            data.extend(part)
            _need(len(data) <= expected_size, 'file-growth')
        after = os.fstat(fd)
        named = os.stat(path, follow_symlinks=False)
        identity = lambda s: (s.st_dev, s.st_ino, s.st_mode, s.st_uid, s.st_gid, s.st_nlink,
                              s.st_size, s.st_mtime_ns, s.st_ctime_ns)
        _need(len(data) == expected_size and identity(before) == identity(after) == identity(named)
              and _sha(data) == expected_sha, 'file-binding')
        return bytes(data)
    finally:
        os.close(fd)


def _read_private_file(path, *, deadline, maximum, required_mode=0o600):
    path = Path(path)
    _need(path.is_absolute() and path.resolve(strict=True) == path, 'file-path')
    fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK | os.O_CLOEXEC)
    try:
        before = os.fstat(fd)
        _need(stat.S_ISREG(before.st_mode) and before.st_nlink == 1 and before.st_uid == os.geteuid()
              and stat.S_IMODE(before.st_mode) == required_mode and 0 < before.st_size <= maximum,
              'file-shape')
        data = bytearray()
        while True:
            _remaining(deadline)
            part = os.read(fd, min(64 * 1024, maximum + 1 - len(data)))
            if not part:
                break
            data.extend(part)
            _need(len(data) <= maximum, 'file-growth')
        after = os.fstat(fd)
        named = os.stat(path, follow_symlinks=False)
        _need(before.st_size == len(data) and _node_identity(before) == _node_identity(after)
              and _node_identity(after) == _node_identity(named)
              and before.st_nlink == after.st_nlink == named.st_nlink == 1
              and before.st_mtime_ns == after.st_mtime_ns == named.st_mtime_ns
              and before.st_ctime_ns == after.st_ctime_ns == named.st_ctime_ns,
              'file-changed')
        return bytes(data)
    finally:
        os.close(fd)


def _node_identity(info):
    return (info.st_dev, info.st_ino, stat.S_IFMT(info.st_mode), stat.S_IMODE(info.st_mode),
            info.st_uid, info.st_gid)


def _remember_owned(path, ownership_root, owned):
    path = Path(path)
    relative = path.relative_to(ownership_root)
    key = tuple(relative.parts)
    info = path.lstat()
    _remember_info(key, info, owned)


def _remember_info(key, info, owned):
    _need(key not in owned and info.st_uid == os.geteuid()
          and (stat.S_ISDIR(info.st_mode) or stat.S_ISREG(info.st_mode) and info.st_nlink == 1),
          'owned-node-shape')
    owned[key] = _node_identity(info)


def _mkdir(path, *, owned=None, ownership_root=None):
    path = Path(path)
    _need(path.is_absolute() and path.parent.resolve(strict=True) == path.parent, 'directory-path')
    parent_fd = os.open(path.parent, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
    made = False
    created = None
    key = None
    fd = None
    try:
        os.mkdir(path.name, 0o700, dir_fd=parent_fd)
        made = True
        key = tuple(path.relative_to(ownership_root).parts) if owned is not None else None
        created = os.stat(path.name, dir_fd=parent_fd, follow_symlinks=False)
        if owned is not None:
            # Record the exclusively-created node before opening it. If open fails,
            # the caller can still clean this exact inode without adopting a later
            # replacement. The parent descriptor pins the namespace for both reads.
            _remember_info(key, created, owned)
        fd = os.open(path.name, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC,
                     dir_fd=parent_fd)
        try:
            info = os.fstat(fd)
            _need(stat.S_ISDIR(info.st_mode)
                  and _node_identity(info) == _node_identity(created), 'directory-open-binding')
            try:
                os.fchmod(fd, 0o700)
            except BaseException:
                if owned is not None:
                    current = os.fstat(fd)
                    if (current.st_dev, current.st_ino, stat.S_IFMT(current.st_mode)) == \
                            (created.st_dev, created.st_ino, stat.S_IFMT(created.st_mode)):
                        owned[key] = _node_identity(current)
                raise
            info, named = os.fstat(fd), os.stat(path.name, dir_fd=parent_fd, follow_symlinks=False)
            _need(stat.S_ISDIR(info.st_mode) and stat.S_IMODE(info.st_mode) == 0o700
                  and info.st_uid == os.geteuid() and info.st_nlink >= 2
                  and _node_identity(info) == _node_identity(named), 'directory-custody')
            if owned is not None:
                owned[key] = _node_identity(info)
        finally:
            opened_fd, fd = fd, None
            os.close(opened_fd)
    except BaseException as error:
        if made:
            cleaned = False
            try:
                if created is not None:
                    named = os.stat(path.name, dir_fd=parent_fd, follow_symlinks=False)
                    same_node = (named.st_dev, named.st_ino, stat.S_IFMT(named.st_mode)) == \
                                (created.st_dev, created.st_ino, stat.S_IFMT(created.st_mode))
                    if stat.S_ISDIR(named.st_mode) and same_node:
                        os.rmdir(path.name, dir_fd=parent_fd)
                        cleaned = True
            except BaseException:
                cleaned = False
            if cleaned and owned is not None:
                recorded = owned.get(key)
                if recorded is not None and recorded[:3] == \
                        (created.st_dev, created.st_ino, stat.S_IFMT(created.st_mode)):
                    owned.pop(key, None)
            if not cleaned:
                if hasattr(error, 'add_note'):
                    error.add_note('completed-c1-cleanup-failed')
                else:
                    error.completed_c1_cleanup_failed = True
        if fd is not None:
            try:
                os.close(fd)
            except BaseException:
                if hasattr(error, 'add_note'):
                    error.add_note('completed-c1-cleanup-failed')
                else:
                    error.completed_c1_cleanup_failed = True
        raise
    finally:
        os.close(parent_fd)


def _write_bytes(path, raw, *, deadline, owned=None, ownership_root=None):
    path = Path(path)
    _need(path.is_absolute() and path.parent.resolve(strict=True) == path.parent, 'file-path')
    parent_fd = os.open(path.parent, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
    try:
        fd = os.open(path.name, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC,
                     0o600, dir_fd=parent_fd)
    except BaseException:
        os.close(parent_fd)
        raise
    try:
        created = os.fstat(fd)
        named = os.stat(path.name, dir_fd=parent_fd, follow_symlinks=False)
        _need(stat.S_ISREG(created.st_mode) and created.st_nlink == 1 and created.st_uid == os.geteuid()
              and stat.S_IMODE(created.st_mode) == 0o600 and _node_identity(created) == _node_identity(named),
              'file-write-custody')
        if owned is not None:
            _remember_info(tuple(path.relative_to(ownership_root).parts), created, owned)
        view = memoryview(raw)
        while view:
            _remaining(deadline)
            amount = os.write(fd, view)
            _need(amount > 0, 'short-write')
            view = view[amount:]
        os.fsync(fd)
        opened = os.fstat(fd)
        named = os.stat(path.name, dir_fd=parent_fd, follow_symlinks=False)
        _need(stat.S_ISREG(opened.st_mode) and opened.st_nlink == 1 and opened.st_uid == os.geteuid()
              and stat.S_IMODE(opened.st_mode) == 0o600 and opened.st_size == len(raw)
              and (opened.st_dev, opened.st_ino, opened.st_size) == (named.st_dev, named.st_ino, named.st_size),
              'file-write-custody')
    finally:
        os.close(fd)
        os.close(parent_fd)


def _safe_relative(name):
    _need(type(name) is str and name and not name.startswith('/') and '\\' not in name
          and len(name.encode('utf-8')) <= completed_validation.MAX_PATH_BYTES, 'member-path')
    canonical = name[:-1] if name.endswith('/') else name
    parts = canonical.split('/')
    _need(canonical and not canonical.endswith('/') and all(part not in ('', '.', '..') for part in parts),
          'member-path')
    return PurePosixPath(canonical)


def selector_artifact_names(run_id, attempt):
    _need(type(run_id) is int and run_id > 0 and type(attempt) is int and attempt > 0, 'run-identity')
    return tuple(f'issue779-checkpoint1-selector-{selector}-{run_id}-{attempt}' for selector in SELECTORS)


def _selector_from_name(name, run_id, attempt):
    prefix, suffix = 'issue779-checkpoint1-selector-', f'-{run_id}-{attempt}'
    _need(type(name) is str and name.startswith(prefix) and name.endswith(suffix), 'selector-artifact-name')
    selector = name[len(prefix):-len(suffix)]
    _need(selector in SELECTORS, 'selector-artifact-name')
    return selector


def _assert_projection(stage, inventory):
    _need(inventory.repository_id == stage.repository_id and inventory.run_id == stage.run_id
          and inventory.run_attempt == stage.run_attempt and inventory.head_sha == stage.head_sha
          and inventory.source_tree == stage.source_tree
          and inventory.source_capture_sha256 == stage.source_capture_sha256, 'inventory-stage-binding')
    _need(type(stage.artifacts) is tuple and type(inventory.archives) is tuple
          and len(stage.artifacts) == completed_run.MAX_ARTIFACTS
          and len(inventory.archives) == completed_run.MAX_ARTIFACTS, 'inventory-artifact-count')
    bindings = {item.name: item for item in stage.artifacts}
    projections = {item.artifact_name: item for item in inventory.archives}
    _need(len(bindings) == completed_run.MAX_ARTIFACTS and len(projections) == completed_run.MAX_ARTIFACTS
          and set(bindings) == set(projections)
          and set(bindings) == set(completed_run.expected_artifact_names(stage.run_id, stage.run_attempt)),
          'inventory-artifact-set')
    for name, binding in bindings.items():
        projection = projections[name]
        _need(projection.artifact_id == binding.artifact_id and projection.archive_bytes == binding.size
              and projection.archive_sha256 == binding.sha256
              and binding.staged_filename == name + '.zip', 'inventory-artifact-binding')
    names = selector_artifact_names(stage.run_id, stage.run_attempt)
    _need(set(names) <= set(bindings), 'selector-artifact-set')
    _need(all(projections[name].source_sha256 == stage.source_capture_sha256 for name in names),
          'selector-source-projection')
    return bindings, projections


def _read_expected_aggregate(stage, stage_root, bindings, projections, inventory, *, deadline):
    name = f'issue779-checkpoint1-current-{stage.run_id}-{stage.run_attempt}'
    binding = bindings[name]
    projection = projections[name]
    raw = _open_bound_file(stage_root / binding.staged_filename, binding.size, binding.sha256,
                           deadline=deadline, maximum=completed_run._artifact_limit(name),
                           required_mode=0o600)
    rows, _expanded = completed_validation._zip_directory(raw)
    _need({row[0] for row in rows if not row[0].endswith('/')} == {'aggregate.json'},
          'aggregate-archive-members')
    body = completed_validation.entrypoint.aggregate_archive(
        raw, {'size_in_bytes': binding.size, 'digest': 'sha256:' + binding.sha256},
        stage.head_sha, stage.source_capture_sha256)
    _need(_sha(body) == inventory.aggregate.aggregate_sha256, 'aggregate-body-projection')
    value = _decode(body, completed_validation.MAX_AGGREGATE_BYTES, 'aggregate-body-json')
    _need(type(value.get('selectors')) is list and tuple(value['selectors']) == SELECTORS
          and type(value.get('cases')) is list and tuple(value['cases']) == CASES
          and value.get('passed') == 16 and type(value.get('passed')) is int
          and value.get('skipped') == 0 and type(value.get('skipped')) is int
          and value.get('exit') == 0 and type(value.get('exit')) is int
          and value.get('native_acceptance') is False and value.get('coverage_credit') is False,
          'aggregate-body-fields')
    projected = inventory.aggregate
    _need(projected.schema == value['schema'] and projected.source == value['source']
          and projected.source_capture_sha256 == value['source_capture_sha256']
          and projected.selectors == tuple(value['selectors']) and projected.cases == tuple(value['cases'])
          and projected.passed == value['passed'] and projected.skipped == value['skipped']
          and projected.exit_code == value['exit'] and projected.aggregate_sha256 == _sha(body),
          'aggregate-inventory-projection')
    return value, binding.sha256, body


def _extract_archive(raw, projection, destination, *, deadline, expanded_used,
                     owned=None, ownership_root=None):
    rows, expanded = completed_validation._zip_directory(raw)
    node_paths = set()
    for row in rows:
        name = row[0]
        relative = _safe_relative(name)
        _need(len(relative.parts) <= MAX_STAGING_DEPTH, 'staging-node-depth')
        parts = relative.parts if name.endswith('/') else relative.parts[:-1]
        node_paths.update(tuple(relative.parts[:index]) for index in range(1, len(relative.parts) + 1))
    _need(len(node_paths) <= completed_validation.MAX_ARCHIVE_MEMBERS, 'staging-node-count')
    projected = tuple((m.path, m.compressed_bytes, m.expanded_bytes, m.compression) for m in projection.members)
    parsed = tuple((row[0], row[3], row[4], row[6]) for row in rows)
    _need(parsed == projected and expanded == projection.expanded_bytes, 'archive-projection-mismatch')
    _need(expanded_used + expanded <= MAX_TOTAL_EXPANDED, 'expanded-total-bound')
    total = expanded_used
    destination = Path(destination)
    _mkdir(destination, owned=owned, ownership_root=ownership_root)
    with zipfile.ZipFile(io.BytesIO(raw), 'r') as archive:
        infos = archive.infolist()
        _need(len(infos) == len(rows), 'archive-member-count')
        for info, row in zip(infos, rows):
            _remaining(deadline)
            name = row[0]
            relative = _safe_relative(name)
            target = destination.joinpath(*relative.parts)
            if name.endswith('/'):
                current = destination
                for part in relative.parts:
                    current = current / part
                    if not current.exists():
                        _mkdir(current, owned=owned, ownership_root=ownership_root)
                    else:
                        st = current.lstat()
                        _need(stat.S_ISDIR(st.st_mode) and not stat.S_ISLNK(st.st_mode)
                              and st.st_uid == os.geteuid(), 'directory-alias')
                _need(info.file_size == 0 and info.compress_size == 0, 'directory-entry-size')
                continue
            current = destination
            for part in relative.parts[:-1]:
                current = current / part
                if not current.exists():
                    _mkdir(current, owned=owned, ownership_root=ownership_root)
                else:
                    st = current.lstat()
                    _need(stat.S_ISDIR(st.st_mode) and not stat.S_ISLNK(st.st_mode)
                          and st.st_uid == os.geteuid(), 'directory-alias')
            _need(0 <= info.file_size <= MAX_MEMBER_BYTES and total + info.file_size <= MAX_TOTAL_EXPANDED,
                  'member-size-bound')
            fd = os.open(target, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC, 0o600)
            written = 0
            try:
                created = os.fstat(fd)
                named = os.stat(target, follow_symlinks=False)
                _need(stat.S_ISREG(created.st_mode) and created.st_nlink == 1
                      and created.st_uid == os.geteuid() and stat.S_IMODE(created.st_mode) == 0o600
                      and _node_identity(created) == _node_identity(named), 'member-custody')
                if owned is not None:
                    _remember_info(tuple(target.relative_to(ownership_root).parts), created, owned)
                with archive.open(info, 'r') as source:
                    while True:
                        _remaining(deadline)
                        chunk = source.read(min(64 * 1024, info.file_size + 1 - written))
                        if not chunk:
                            break
                        written += len(chunk)
                        total += len(chunk)
                        _need(written <= info.file_size and total <= MAX_TOTAL_EXPANDED,
                              'member-growth')
                        view = memoryview(chunk)
                        while view:
                            count = os.write(fd, view)
                            _need(count > 0, 'member-write')
                            view = view[count:]
                os.fsync(fd)
                opened, named = os.fstat(fd), os.stat(target, follow_symlinks=False)
                _need(written == info.file_size and stat.S_ISREG(opened.st_mode)
                      and opened.st_nlink == 1 and opened.st_uid == os.geteuid()
                      and stat.S_IMODE(opened.st_mode) == 0o600 and opened.st_size == info.file_size
                      and (opened.st_dev, opened.st_ino) == (named.st_dev, named.st_ino), 'member-custody')
            finally:
                os.close(fd)
    _need(total - expanded_used == expanded, 'archive-expanded-byte-count')
    return total


def _load_trusted_current_binding(*, deadline):
    path = Path(__file__).resolve().parents[2] / '.github' / 'issue779-checkpoint1' / 'binding.py'
    raw = _read_trusted_source_file(path, deadline=deadline, maximum=1024 * 1024)
    module = types.ModuleType('issue779_completed_c1_current_binding')
    module.__file__ = str(path)
    exec(compile(raw, str(path), 'exec', dont_inherit=True), module.__dict__)
    return module


def _read_trusted_source_file(path, *, deadline, maximum):
    path = Path(path)
    _need(path.is_absolute() and path.resolve(strict=True) == path, 'trusted-source-path')
    fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK | os.O_CLOEXEC)
    try:
        before = os.fstat(fd)
        _need(stat.S_ISREG(before.st_mode) and stat.S_IMODE(before.st_mode) in (0o644, 0o755)
              and 0 < before.st_size <= maximum, 'trusted-source-shape')
        chunks = []
        length = 0
        while True:
            _remaining(deadline)
            part = os.read(fd, min(64 * 1024, maximum + 1 - length))
            if not part:
                break
            length += len(part)
            _need(length <= maximum, 'trusted-source-size')
            chunks.append(part)
        after = os.fstat(fd)
        named = os.stat(path, follow_symlinks=False)
        _need(length == before.st_size and _node_identity(before) == _node_identity(after)
              and _node_identity(after) == _node_identity(named)
              and before.st_nlink == after.st_nlink == named.st_nlink == 1
              and before.st_size == after.st_size == named.st_size
              and before.st_mtime_ns == after.st_mtime_ns == named.st_mtime_ns
              and before.st_ctime_ns == after.st_ctime_ns == named.st_ctime_ns,
              'trusted-source-changed')
        return b''.join(chunks)
    finally:
        os.close(fd)


def _load_capture_and_binding(source_raw, source_root, *, current_binding, deadline):
    capture = _decode(source_raw, MAX_SOURCE_BYTES, 'source-capture-json')
    _need(set(capture) == {'schema', 'source', 'tree', 'count', 'files'}
          and capture['schema'] == 'issue779-checkpoint2-source-v1'
          and type(capture['source']) is str and type(capture['tree']) is str
          and type(capture['files']) is dict and type(capture['count']) is int
          and capture['count'] == len(capture['files']), 'source-capture-shape')
    binding_path = '.github/issue779-checkpoint1/binding.py'
    row = capture['files'].get(binding_path)
    _need(type(row) is dict and type(row.get('sha256')) is str
          and len(row['sha256']) == 64, 'binding-capture-row')
    # Verify the complete target checkout with validator-owned code before loading
    # any executable binding bytes from the completed-run checkout.
    current_binding.verify_source(capture, Path(source_root), deadline=deadline)
    module_path = Path(source_root) / binding_path
    raw = _read_trusted_source_file(module_path, deadline=deadline, maximum=1024 * 1024)
    _need(_sha(raw) == row['sha256'], 'binding-source-hash')
    module = types.ModuleType('issue779_completed_c1_binding')
    module.__file__ = str(Path(source_root) / binding_path)
    exec(compile(raw, module.__file__, 'exec', dont_inherit=True), module.__dict__)
    return capture, module


def _load_run_native(source_root, capture, *, deadline):
    path = Path(source_root) / '.github/issue779-checkpoint1/run-native.py'
    row = capture['files'].get('.github/issue779-checkpoint1/run-native.py')
    _need(type(row) is dict and type(row.get('sha256')) is str, 'run-native-capture-row')
    raw = _read_trusted_source_file(path, deadline=deadline, maximum=2 * 1024 * 1024)
    _need(_sha(raw) == row['sha256'], 'run-native-source-hash')
    module = types.ModuleType('issue779_completed_c1_run_native')
    module.__file__ = str(path)
    sys.path.insert(0, str(path.parent))
    try:
        exec(compile(raw, str(path), 'exec', dont_inherit=True), module.__dict__)
    finally:
        sys.path.pop(0)
    return module


def _check_aggregate(summary, expected):
    _need(summary == expected, 'aggregate-content-mismatch')


def _validate_aggregate_identity(raw, source, capture_sha256):
    value = _decode(raw, 16 * 1024, 'aggregate-body')
    _need(value.get('schema') == 'issue779-checkpoint1-aggregate-v1'
          and value.get('source') == source and value.get('source_capture_sha256') == capture_sha256
          and type(value.get('selectors')) is list and tuple(value['selectors']) == SELECTORS
          and type(value.get('cases')) is list and tuple(value['cases']) == CASES
          and type(value.get('passed')) is int and value['passed'] == 16
          and type(value.get('skipped')) is int and value['skipped'] == 0
          and type(value.get('exit')) is int and value['exit'] == 0
          and value.get('native_acceptance') is False and value.get('coverage_credit') is False,
          'aggregate-identity')
    return value


def _identity_fd(info):
    return _node_identity(info)


def _validate_receipt_identities(source, receipt_dirs, binding, run_id, run_attempt, *, deadline):
    _need(type(receipt_dirs) in (list, tuple) and len(receipt_dirs) == len(SELECTORS), 'selector-count')
    expected_workflow = f'github-actions-{run_id}-{run_attempt}'
    normalized = []
    for expected_selector, receipt in zip(SELECTORS, receipt_dirs):
        directory = Path(receipt)
        _need(directory.is_absolute() and directory.resolve(strict=True) == directory
              and stat.S_ISDIR(directory.lstat().st_mode), 'selector-directory')
        native = directory if directory.name == 'native' else directory / 'native'
        _need(native.resolve(strict=True) == native and stat.S_ISDIR(native.lstat().st_mode),
              'selector-native-directory')
        result = binding.decode(_read_private_file(native / 'result.json', deadline=deadline,
                                                   maximum=1024 * 1024))
        build = binding.decode(_read_private_file(native / 'build-receipt.json', deadline=deadline,
                                                  maximum=1024 * 1024))
        execution = binding.decode(_read_private_file(native / 'execution.json', deadline=deadline,
                                                      maximum=64 * 1024))
        _need(result.get('selector') == expected_selector, 'selector-order')
        _need(build.get('source_root') == str(source), 'source-root-receipt')
        _need(execution.get('workflow_identity') == expected_workflow, 'workflow-identity')
        normalized.append(native)
    return normalized


def _reconcile_callbacks(source_root, receipt_dirs, capture_raw, aggregate_output_root,
                         expected_aggregate_body, *, run_id, run_attempt,
                         input_aggregate_archive_sha256, deadline,
                         owned=None, ownership_root=None):
    """Invoke only the fixed current C1 aggregate and its original retained callbacks."""
    _remaining(deadline)
    source = Path(source_root)
    _need(source.is_absolute() and source.resolve(strict=True) == source
          and stat.S_ISDIR(source.lstat().st_mode), 'source-root')
    current_binding = _load_trusted_current_binding(deadline=deadline)
    capture, captured_binding = _load_capture_and_binding(
        capture_raw, source, current_binding=current_binding, deadline=deadline)
    _need(capture['source'] == _git(source, 'rev-parse', 'HEAD', deadline=deadline)
          and capture['tree'] == _git(source, 'rev-parse', 'HEAD^{tree}', deadline=deadline),
          'source-run-binding')
    _need(type(run_id) is int and run_id > 0 and type(run_attempt) is int and run_attempt > 0,
          'run-identity')
    _need(_remaining(deadline) >= AGGREGATE_SECONDS + POST_AGGREGATE_RESERVE,
          'aggregate-deadline-reserve')
    normalized = _validate_receipt_identities(source, receipt_dirs, captured_binding,
                                              run_id, run_attempt, deadline=deadline)
    missing = object()
    previous_binding = sys.modules.get('binding', missing)
    previous_path = list(sys.path)
    try:
        sys.modules['binding'] = captured_binding
        module = _load_run_native(source, capture, deadline=deadline)
        callback_root = Path(aggregate_output_root)
        _need(callback_root.is_absolute() and callback_root.resolve(strict=True) == callback_root
              and stat.S_ISDIR(callback_root.lstat().st_mode) and stat.S_IMODE(callback_root.lstat().st_mode) == 0o700
              and callback_root.lstat().st_uid == os.geteuid() and not any(callback_root.iterdir())
              and not _paths_overlap(source, callback_root)
              and all(not _paths_overlap(receipt, callback_root) for receipt in normalized),
              'callback-root')
        args = types.SimpleNamespace(source_capture=Path(capture_file := str(callback_root / 'source.json')),
                                     receipts=normalized, output=callback_root / 'result')
        _write_bytes(capture_file, capture_raw, deadline=deadline)
        if owned is not None:
            _remember_owned(capture_file, ownership_root, owned)
        def tracked_fresh_output(path):
            result_path = Path(path)
            _need(result_path == callback_root / 'result', 'callback-output-path')
            _mkdir(result_path, owned=owned, ownership_root=ownership_root)
            return result_path

        def tracked_save(path, raw):
            result_path = Path(path)
            _need(result_path == callback_root / 'result' / 'aggregate.json'
                  and type(raw) is bytes and len(raw) <= module.binding.MAX_JSON,
                  'callback-output-file')
            _write_bytes(result_path, raw, deadline=deadline, owned=owned,
                         ownership_root=ownership_root)
            return None

        module.fresh_output = tracked_fresh_output
        module.save = tracked_save
        summary = module.aggregate(args)
        output_path = args.output / 'aggregate.json'
        info = output_path.lstat()
        output = _open_bound_file(output_path, info.st_size, _sha(module.binding.canonical(summary)),
                                  deadline=deadline, maximum=16 * 1024, required_mode=0o600)
    finally:
        sys.path[:] = previous_path
        if previous_binding is missing:
            sys.modules.pop('binding', None)
        else:
            sys.modules['binding'] = previous_binding
    expected = _validate_aggregate_identity(expected_aggregate_body, capture['source'], _sha(capture_raw))
    _check_aggregate(summary, expected)
    _need(output == expected_aggregate_body, 'aggregate-output-archive-mismatch')
    _need(type(input_aggregate_archive_sha256) is str
          and completed_run.SHA256.fullmatch(input_aggregate_archive_sha256), 'aggregate-archive-binding')
    return C1CallbackProjection(run_id, run_attempt, capture['source'], _sha(capture_raw), SELECTORS,
                                tuple(expected['cases']), expected['passed'], expected['skipped'],
                                expected['exit'], input_aggregate_archive_sha256,
                                _sha(expected_aggregate_body), _sha(output))


def _git(root, *args, deadline):
    _remaining(deadline)
    import subprocess
    result = subprocess.run(['git', '--no-optional-locks', '-c', 'core.fsmonitor=false',
                             '-c', 'safe.directory=' + str(root), *args], cwd=root,
                            stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                            timeout=max(0.001, min(10, deadline - time.monotonic())), check=False)
    _need(result.returncode == 0 and len(result.stdout) <= 4096, 'source-git')
    return result.stdout.decode('ascii').strip()


def _paths_overlap(first, second):
    first, second = Path(first), Path(second)
    return first == second or first in second.parents or second in first.parents


def _cleanup_invocation(parent, work, work_identity, owned):
    parent, work = Path(parent), Path(work)
    parent_fd = None
    work_fd = None
    try:
        parent_fd = os.open(parent, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
        work_info = os.stat(work.name, dir_fd=parent_fd, follow_symlinks=False)
        if _identity_fd(work_info) != work_identity or not stat.S_ISDIR(work_info.st_mode):
            os.close(parent_fd)
            parent_fd = None
            return False
        work_fd = os.open(work.name, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC,
                          dir_fd=parent_fd)
        if _identity_fd(os.fstat(work_fd)) != work_identity:
            os.close(work_fd)
            work_fd = None
            os.close(parent_fd)
            parent_fd = None
            return False
    except OSError:
        if work_fd is not None:
            os.close(work_fd)
        if parent_fd is not None:
            os.close(parent_fd)
        return False
    ok = True
    try:
        for parts, expected in sorted(owned.items(), key=lambda row: (-len(row[0]), row[0])):
            if not parts:
                continue
            current_fd = os.dup(work_fd)
            try:
                for depth, component in enumerate(parts[:-1], start=1):
                    next_fd = os.open(component, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC,
                                      dir_fd=current_fd)
                    expected_parent = owned.get(parts[:depth])
                    if expected_parent is None or _identity_fd(os.fstat(next_fd)) != expected_parent:
                        os.close(next_fd)
                        ok = False
                        raise OSError('owned-directory-substitution')
                    os.close(current_fd)
                    current_fd = next_fd
                try:
                    info = os.stat(parts[-1], dir_fd=current_fd, follow_symlinks=False)
                except OSError:
                    ok = False
                    continue
                if _identity_fd(info) != expected:
                    ok = False
                    continue
                try:
                    if stat.S_ISDIR(info.st_mode):
                        os.rmdir(parts[-1], dir_fd=current_fd)
                    elif stat.S_ISREG(info.st_mode) and info.st_nlink == 1:
                        os.unlink(parts[-1], dir_fd=current_fd)
                    else:
                        ok = False
                except OSError:
                    ok = False
            except OSError:
                ok = False
            finally:
                os.close(current_fd)
        try:
            if os.listdir(work_fd):
                ok = False
            final = os.stat(work.name, dir_fd=parent_fd, follow_symlinks=False)
            if _identity_fd(final) != work_identity or not stat.S_ISDIR(final.st_mode):
                ok = False
            elif ok:
                os.rmdir(work.name, dir_fd=parent_fd)
        except OSError:
            ok = False
    finally:
        if work_fd is not None:
            os.close(work_fd)
        if parent_fd is not None:
            os.close(parent_fd)
    return ok


def _finish_invocation(parent, work, work_identity, owned, primary):
    cleaned = _cleanup_invocation(parent, work, work_identity, owned)
    if primary is not None:
        if not cleaned:
            if hasattr(primary, 'add_note'):
                primary.add_note('completed-c1-cleanup-failed')
            else:
                primary.completed_c1_cleanup_failed = True
        raise primary
    if not cleaned:
        raise ValueError('completed-c1-cleanup') from None


@dataclass(frozen=True)
class C1CallbackProjection:
    """Observed output of the existing callback aggregator; it grants no authority."""

    run_id: int
    run_attempt: int
    source_sha: str
    source_capture_sha256: str
    selectors: tuple[str, ...]
    cases: tuple[str, ...]
    passed: int
    skipped: int
    exit_code: int
    input_aggregate_archive_sha256: str
    input_aggregate_body_sha256: str
    output_sha256: str


def run_fixed_c1_callbacks(stage, inventory, stage_directory, source_root, staging, *, deadline):
    """Stage the fourteen fixed selector ZIPs and call the source-owned aggregate in process.

    Every archive, member path, size, and body is matched to the earlier parsed inventory;
    source code is loaded only from source_root, after its complete source capture passes
    the captured `binding.verify_source` implementation. The return is only a frozen
    observation of callback output, never a qualification/admission verdict.
    """
    _need(isinstance(stage, completed_run.CompletedRunStage), 'stage-type')
    _need(type(inventory) is completed_validation.ParsedCompletedRunInventory, 'inventory-type')
    _remaining(deadline)
    bindings, projections = _assert_projection(stage, inventory)
    stage_root, source, target = Path(stage_directory), Path(source_root), Path(staging)
    _need(stage_root.is_absolute() and stage_root.resolve(strict=True) == stage_root
          and stat.S_ISDIR(stage_root.lstat().st_mode) and stat.S_IMODE(stage_root.lstat().st_mode) == 0o700
          and stage_root.lstat().st_uid == os.geteuid(), 'stage-directory')
    _need({entry.name for entry in stage_root.iterdir()} ==
          {binding.staged_filename for binding in bindings.values()}, 'stage-directory-entries')
    _need(source.is_absolute() and source.resolve(strict=True) == source
          and stat.S_ISDIR(source.lstat().st_mode), 'source-root')
    _need(target.is_absolute() and target.resolve(strict=True) == target
          and stat.S_ISDIR(target.lstat().st_mode) and stat.S_IMODE(target.lstat().st_mode) == 0o700
          and target.lstat().st_uid == os.geteuid() and not any(target.iterdir()), 'staging-directory')
    _need(not (source == target or source in target.parents or target in source.parents)
          and not (stage_root == target or stage_root in target.parents or target in stage_root.parents)
          and not (source == stage_root or source in stage_root.parents or stage_root in source.parents),
          'root-overlap')
    expected_aggregate, input_aggregate_sha, expected_aggregate_body = _read_expected_aggregate(
        stage, stage_root, bindings, projections, inventory, deadline=deadline)
    work = target / 'c1-fixed-callbacks'
    owned = {}
    _mkdir(work, owned=owned, ownership_root=work)
    work_identity = owned[()]
    primary = None
    result = None
    try:
        selectors_root = work / 'selectors'
        _mkdir(selectors_root, owned=owned, ownership_root=work)
        callback_root = work / 'aggregate-output'
        _mkdir(callback_root, owned=owned, ownership_root=work)
        capture_raw = None
        receipt_dirs = []
        expanded = 0
        for selector, name in zip(SELECTORS, selector_artifact_names(stage.run_id, stage.run_attempt)):
            _remaining(deadline)
            binding, projection = bindings[name], projections[name]
            raw = _open_bound_file(stage_root / binding.staged_filename, binding.size, binding.sha256,
                                   deadline=deadline, maximum=completed_run._artifact_limit(name),
                                   required_mode=0o600)
            _need(_sha(raw) == projection.archive_sha256, 'selector-archive-sha')
            destination = selectors_root / ('selector-' + selector)
            expanded = _extract_archive(raw, projection, destination, deadline=deadline,
                                         expanded_used=expanded, owned=owned, ownership_root=work)
            root_capture = destination / 'source.json'
            capture_bytes = _read_private_file(root_capture, deadline=deadline, maximum=MAX_SOURCE_BYTES)
            _need(_sha(capture_bytes) == stage.source_capture_sha256, 'selector-source-capture')
            if capture_raw is None:
                capture_raw = capture_bytes
            else:
                _need(capture_bytes == capture_raw, 'selector-source-consensus')
            native = destination / 'native'
            _need(native.resolve(strict=True) == native and stat.S_ISDIR(native.lstat().st_mode)
                  and stat.S_IMODE(native.lstat().st_mode) == 0o700, 'selector-native-directory')
            receipt_dirs.append(native)
        _need(capture_raw is not None, 'selector-source-missing')
        capture = _decode(capture_raw, MAX_SOURCE_BYTES, 'source-capture-json')
        _need(capture.get('source') == stage.head_sha and capture.get('tree') == stage.source_tree,
              'source-run-binding')
        _need(_remaining(deadline) >= AGGREGATE_SECONDS + POST_AGGREGATE_RESERVE,
              'aggregate-deadline-reserve')
        result = _reconcile_callbacks(source, receipt_dirs, capture_raw, callback_root, expected_aggregate_body,
                                      run_id=stage.run_id, run_attempt=stage.run_attempt,
                                      input_aggregate_archive_sha256=input_aggregate_sha,
                                      deadline=deadline,
                                      owned=owned, ownership_root=work)
        _need(result.source_sha == stage.head_sha and result.source_capture_sha256 == stage.source_capture_sha256
              and result.input_aggregate_archive_sha256 == input_aggregate_sha, 'callback-projection-binding')
        _remaining(deadline)
    except BaseException as exc:
        primary = exc
    _finish_invocation(target, work, work_identity, owned, primary)
    _remaining(deadline)
    return result
