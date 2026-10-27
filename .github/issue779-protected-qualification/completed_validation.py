"""Bounded non-authorizing inspection of staged completed-run archives.

This slice checks staging bytes, ZIP structure, shared source snapshots, the fixed C1
aggregate, and the earlier partial receipt. It does not validate the original callbacks,
run Native.Tests validators, close the coverage gate, or decide qualification.
"""
from __future__ import annotations

from dataclasses import dataclass
import hashlib
import io
import os
from pathlib import Path, PurePosixPath
import stat
import struct
import sys
import time
import unicodedata
import zipfile

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / '.github/issue779-protected-consumer'))
sys.path.insert(0, str(ROOT / '.github/issue779-protected-qualification'))
import completed_run
import entrypoint

MIB = 1024 * 1024
MAX_ARCHIVE_MEMBERS = 8192
MAX_CENTRAL_DIRECTORY_BYTES = 8 * MIB
MAX_EXPANDED_ARCHIVE_BYTES = 512 * MIB
MAX_PATH_BYTES = 4095
MAX_SOURCE_BYTES = 1024 * 1024
MAX_AGGREGATE_BYTES = entrypoint.MAX_DOCUMENT
MAX_AGGREGATE_ARCHIVE_BYTES = entrypoint.MAX_ARCHIVE
MAX_PARTIAL_RECEIPT_BYTES = 64 * 1024
PARTIAL_MEMBERS = frozenset(('source.json', 'aggregate.zip', 'aggregate.json', 'partial-qualification.json'))
PARTIAL_FLAGS = ('selector_callbacks_independently_verified', 'qualification_complete',
                 'admission_created', 'trusted_manifest_created', 'gate_claimed',
                 'native_acceptance_claimed')


def _need(value, reason):
    if not value:
        raise ValueError('completed-validation-' + reason)


def _check(deadline):
    _need(type(deadline) in (int, float) and time.monotonic() < deadline, 'deadline')


def _sha(raw):
    return hashlib.sha256(raw).hexdigest()


def _safe_name(name):
    _need(type(name) is str and 0 < len(name.encode('utf-8')) <= MAX_PATH_BYTES
          and unicodedata.normalize('NFC', name) == name and not name.startswith('/')
          and '\\' not in name and not any(ord(c) < 32 or ord(c) == 127 for c in name),
          'zip-name')
    directory = name.endswith('/')
    canonical = name[:-1] if directory else name
    parts = canonical.split('/')
    path = PurePosixPath(canonical)
    _need(canonical and not canonical.endswith('/') and not path.is_absolute()
          and path.as_posix() == canonical and all(part not in ('', '.', '..') for part in parts),
          'zip-name')
    return name


def _zip_directory(raw):
    """Prebound central-directory objects and local extents before ZipFile allocation."""
    _need(type(raw) is bytes and 0 < len(raw) <= completed_run.MAX_TOTAL_ARCHIVE_BYTES, 'archive-size')
    tail_start = max(0, len(raw) - (22 + 65535))
    tail = raw[tail_start:]
    sig = b'PK\x05\x06'
    candidates = []
    offset = 0
    while True:
        found = tail.find(sig, offset)
        if found < 0:
            break
        absolute = tail_start + found
        if found + 22 <= len(tail):
            comment = struct.unpack_from('<H', raw, absolute + 20)[0]
            if absolute + 22 + comment == len(raw):
                candidates.append(absolute)
        offset = found + 1
    _need(len(candidates) == 1, 'zip-eocd')
    eocd = candidates[0]
    disk, central_disk, disk_count, count, central_size, central_offset, _comment = struct.unpack_from(
        '<HHHHIIH', raw, eocd + 4)
    _need(0 < count <= MAX_ARCHIVE_MEMBERS and disk == central_disk == 0 and disk_count == count
          and count != 0xFFFF and central_size != 0xFFFFFFFF and central_offset != 0xFFFFFFFF
          and central_size <= MAX_CENTRAL_DIRECTORY_BYTES and central_offset <= eocd
          and central_size == eocd - central_offset, 'zip-central-bounds')
    entries = []
    cursor = central_offset
    expanded_total = 0
    for _ in range(count):
        _need(cursor <= eocd and eocd - cursor >= 46 and raw[cursor:cursor + 4] == b'PK\x01\x02',
              'zip-central-header')
        flags = struct.unpack_from('<H', raw, cursor + 8)[0]
        method = struct.unpack_from('<H', raw, cursor + 10)[0]
        crc = struct.unpack_from('<I', raw, cursor + 16)[0]
        compressed, expanded = struct.unpack_from('<II', raw, cursor + 20)
        name_size, extra_size, entry_comment, disk_start = struct.unpack_from('<HHHH', raw, cursor + 28)
        external = struct.unpack_from('<I', raw, cursor + 38)[0]
        local_offset = struct.unpack_from('<I', raw, cursor + 42)[0]
        record_size = 46 + name_size + extra_size + entry_comment
        _need(0 < name_size <= MAX_PATH_BYTES and extra_size <= 4096 and entry_comment <= 4096
              and record_size <= eocd - cursor and disk_start == 0 and local_offset != 0xFFFFFFFF
              and compressed != 0xFFFFFFFF and expanded != 0xFFFFFFFF
              and method in (zipfile.ZIP_STORED, zipfile.ZIP_DEFLATED) and not flags & 1
              and not flags & ~0x080E, 'zip-central-fields')
        name_raw = raw[cursor + 46:cursor + 46 + name_size]
        try:
            name = name_raw.decode('utf-8' if flags & 0x800 else 'cp437')
        except UnicodeDecodeError:
            raise ValueError('completed-validation-zip-name') from None
        _safe_name(name)
        extra_start = cursor + 46 + name_size
        extra_end = extra_start + extra_size
        extra_cursor = extra_start
        while extra_cursor < extra_end:
            _need(extra_end - extra_cursor >= 4, 'zip-extra')
            field, size = struct.unpack_from('<HH', raw, extra_cursor)
            extra_cursor += 4
            _need(field != 1 and size <= extra_end - extra_cursor, 'zip-extra')
            extra_cursor += size
        _need(local_offset <= central_offset and central_offset - local_offset >= 30
              and raw[local_offset:local_offset + 4] == b'PK\x03\x04', 'zip-local-header')
        local_flags, local_method = struct.unpack_from('<HH', raw, local_offset + 6)
        local_compressed, local_expanded = struct.unpack_from('<II', raw, local_offset + 18)
        local_name_size, local_extra_size = struct.unpack_from('<HH', raw, local_offset + 26)
        local_header_size = 30 + local_name_size + local_extra_size
        _need(local_flags == flags and local_method == method and local_name_size == name_size
              and local_extra_size <= 4096 and local_header_size <= central_offset - local_offset
              and raw[local_offset + 30:local_offset + 30 + name_size] == name_raw
              and (flags & 8 or (local_compressed == compressed and local_expanded == expanded)),
              'zip-local-fields')
        local_extra_start = local_offset + 30 + local_name_size
        local_extra_end = local_offset + local_header_size
        extra_cursor = local_extra_start
        while extra_cursor < local_extra_end:
            _need(local_extra_end - extra_cursor >= 4, 'zip-local-extra')
            field, size = struct.unpack_from('<HH', raw, extra_cursor)
            extra_cursor += 4
            _need(field != 1 and size <= local_extra_end - extra_cursor, 'zip-local-extra')
            extra_cursor += size
        data_start = local_offset + local_header_size
        data_end = data_start + compressed
        _need(data_end <= central_offset, 'zip-local-extent')
        mode = (external >> 16) & 0xFFFF
        kind = stat.S_IFMT(mode)
        is_directory = name.endswith('/')
        _need(kind in (0, stat.S_IFREG, stat.S_IFDIR)
              and (not is_directory or kind in (0, stat.S_IFDIR))
              and (is_directory or kind in (0, stat.S_IFREG)), 'zip-entry-kind')
        if is_directory:
            _need(expanded == 0 and compressed == 0, 'zip-directory-size')
        else:
            expanded_total += expanded
        _need(expanded_total <= MAX_EXPANDED_ARCHIVE_BYTES, 'zip-expanded-total')
        entries.append((name, local_offset, data_end, compressed, expanded, crc, method, flags))
        cursor += record_size
    canonical_names = [row[0][:-1] if row[0].endswith('/') else row[0] for row in entries]
    _need(cursor == eocd and len({row[0] for row in entries}) == count
          and len({name.casefold() for name in canonical_names}) == count, 'zip-duplicate-members')
    files = {row[0].rstrip('/') for row in entries if not row[0].endswith('/')}
    for name in files:
        parts = name.split('/')
        _need(all('/'.join(parts[:index]) not in files for index in range(1, len(parts))),
              'zip-file-directory-conflict')
    ordered = sorted((row[1], row[2]) for row in entries)
    _need(all(left[1] <= right[0] for left, right in zip(ordered, ordered[1:])), 'zip-overlapping-members')
    return tuple(entries), expanded_total


def _read_stage_file(stage, binding, deadline):
    path = stage / binding.staged_filename
    _check(deadline)
    flags = os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK | os.O_CLOEXEC
    fd = os.open(path, flags)
    try:
        before = os.fstat(fd)
        _need(stat.S_ISREG(before.st_mode) and before.st_nlink == 1
              and stat.S_IMODE(before.st_mode) == 0o600 and before.st_uid == os.geteuid()
              and before.st_size == binding.size and 0 < before.st_size <= completed_run._artifact_limit(binding.name),
              'stage-file')
        data = bytearray()
        while True:
            _check(deadline)
            part = os.read(fd, min(64 * 1024, binding.size + 1 - len(data)))
            if not part:
                break
            data.extend(part)
            _need(len(data) <= binding.size, 'stage-growth')
        after = os.fstat(fd)
        identity = lambda row: (row.st_dev, row.st_ino, row.st_mode, row.st_nlink, row.st_size,
                                row.st_mtime_ns, row.st_ctime_ns)
        named = os.stat(path, follow_symlinks=False)
        _need(len(data) == binding.size and identity(before) == identity(after) == identity(named)
              and _sha(data) == binding.sha256, 'stage-binding')
        return bytes(data)
    finally:
        os.close(fd)


def _member_bodies(raw, rows, wanted, deadline):
    _need(set(name for name, *_ in rows if not name.endswith('/')) >= set(wanted), 'zip-required-members')
    bodies = {}
    try:
        with zipfile.ZipFile(io.BytesIO(raw), 'r') as archive:
            infos = archive.infolist()
            _need(len(infos) == len(rows), 'zip-member-count')
            by_name = {info.filename: info for info in infos}
            _need(len(by_name) == len(infos) and set(by_name) == {row[0] for row in rows}, 'zip-member-index')
            for name in wanted:
                _check(deadline)
                info = by_name[name]
                maximum = (MAX_SOURCE_BYTES if name == 'source.json' else
                           MAX_AGGREGATE_BYTES if name == 'aggregate.json' else
                           MAX_AGGREGATE_ARCHIVE_BYTES if name == 'aggregate.zip' else
                           MAX_PARTIAL_RECEIPT_BYTES if name == 'partial-qualification.json' else
                           completed_run.MAX_TOTAL_ARCHIVE_BYTES)
                _need(info.file_size <= maximum, 'zip-projection-size')
                with archive.open(info, 'r') as stream:
                    body = stream.read(maximum + 1)
                    _need(len(body) == info.file_size <= maximum and stream.read(1) == b'', 'zip-projection-read')
                bodies[name] = body
    except ValueError:
        raise
    except Exception:
        raise ValueError('completed-validation-zip-invalid') from None
    return bodies


@dataclass(frozen=True)
class MemberProjection:
    path: str
    compressed_bytes: int
    expanded_bytes: int
    compression: int


@dataclass(frozen=True)
class ArchiveProjection:
    artifact_id: int
    artifact_name: str
    archive_sha256: str
    archive_bytes: int
    expanded_bytes: int
    members: tuple[MemberProjection, ...]
    source_sha256: str | None


@dataclass(frozen=True)
class AggregateProjection:
    schema: str
    source: str
    source_capture_sha256: str
    selectors: tuple[str, ...]
    cases: tuple[str, ...]
    passed: int
    skipped: int
    exit_code: int
    aggregate_sha256: str


@dataclass(frozen=True)
class PartialReceiptProjection:
    run_id: int
    run_attempt: int
    source_capture_sha256: str
    aggregate_artifact_id: int
    aggregate_artifact_sha256: str
    runtime_job_ids: tuple[int, ...]
    claims: tuple[tuple[str, bool], ...]
    receipt_sha256: str


@dataclass(frozen=True)
class ParsedCompletedRunInventory:
    """Immutable parse result only; explicitly not a qualification or admission verdict."""

    repository_id: int
    run_id: int
    run_attempt: int
    head_sha: str
    source_tree: str
    source_capture_sha256: str
    source_count: int
    archives: tuple[ArchiveProjection, ...]
    aggregate: AggregateProjection
    partial_receipt: PartialReceiptProjection


def _validate_partial(raw, stage, capture, aggregate_binding, aggregate_body):
    value = completed_run._json(raw, MAX_PARTIAL_RECEIPT_BYTES, 'completed-validation-partial-json')
    fields = {'schema', 'repository', 'repository_id', 'workflow_path', 'workflow_commit', 'workflow_sha256',
              'event', 'ref', 'runtime_workflow_path', 'runtime_workflow_sha256', 'runtime_workflow_git_blob',
              'runtime_job_result', 'run_id', 'run_attempt', 'observed_run_status', 'source_capture_sha256',
              'source_count', 'source_tree', 'artifact_id', 'artifact_name', 'artifact_bytes', 'artifact_sha256',
              'aggregate_sha256', 'runtime_job_ids', *PARTIAL_FLAGS}
    _need(set(value) == fields and value.get('schema') == 'issue779-protected-full-runtime-partial-v1'
          and value.get('repository') == completed_run.REPOSITORY
          and value.get('repository_id') == stage.repository_id
          and value.get('workflow_path') == completed_run.WORKFLOW
          and value.get('workflow_commit') == stage.head_sha
          and value.get('workflow_sha256') == dict(stage.workflow_sha256s)[completed_run.WORKFLOW]
          and value.get('event') == 'workflow_dispatch' and value.get('ref') == 'refs/heads/main'
          and value.get('runtime_workflow_path') == completed_run.RUNTIME_WORKFLOW
          and value.get('runtime_workflow_sha256') == dict(stage.workflow_sha256s)[completed_run.RUNTIME_WORKFLOW]
          and value.get('runtime_workflow_git_blob') == capture['files'][completed_run.RUNTIME_WORKFLOW]['git_blob']
          and value.get('runtime_job_result') == 'success'
          and type(value.get('repository_id')) is int and type(value.get('run_id')) is int
          and type(value.get('run_attempt')) is int and type(value.get('source_count')) is int
          and type(value.get('artifact_id')) is int and type(value.get('artifact_bytes')) is int
          and value.get('run_id') == stage.run_id and value.get('run_attempt') == stage.run_attempt
          and value.get('observed_run_status') == 'in_progress'
          and value.get('source_capture_sha256') == stage.source_capture_sha256
          and value.get('source_count') == capture['count'] and value.get('source_tree') == stage.source_tree
          and value.get('artifact_id') == aggregate_binding.artifact_id
          and value.get('artifact_name') == aggregate_binding.name
          and value.get('artifact_bytes') == aggregate_binding.size
          and value.get('artifact_sha256') == aggregate_binding.sha256
          and value.get('aggregate_sha256') == _sha(aggregate_body)
          and type(value.get('runtime_job_ids')) is list
          and all(type(item) is int and item > 0 for item in value['runtime_job_ids'])
          and len(value['runtime_job_ids']) == len(set(value['runtime_job_ids'])) == 36
          and set(value['runtime_job_ids']) <= set(stage.job_ids)
          and all(value.get(flag) is False for flag in PARTIAL_FLAGS), 'partial-receipt-fields')
    return PartialReceiptProjection(stage.run_id, stage.run_attempt, stage.source_capture_sha256,
                                    aggregate_binding.artifact_id, aggregate_binding.sha256,
                                    tuple(value['runtime_job_ids']), tuple((flag, value[flag]) for flag in PARTIAL_FLAGS),
                                    _sha(raw))


def inspect_completed_run(stage, stage_directory, source_capture, *, deadline):
    """Project fixed staged ZIP members and shared source/aggregate/partial records, without qualification."""
    _need(isinstance(stage, completed_run.CompletedRunStage), 'stage-type')
    _need(type(source_capture) is bytes and 0 < len(source_capture) <= MAX_SOURCE_BYTES
          and _sha(source_capture) == stage.source_capture_sha256, 'source-capture-hash')
    capture = completed_run._json(source_capture, MAX_SOURCE_BYTES, 'source-capture-json')
    _need(type(capture.get('files')) is dict and capture.get('schema') == 'issue779-checkpoint2-source-v1'
          and capture.get('source') == stage.head_sha and capture.get('tree') == stage.source_tree
          and type(capture.get('count')) is int
          and capture['count'] == len(capture['files']) > 0
          and completed_run.SHA1.fullmatch(capture['tree']) is not None,
          'source-capture-binding')
    _need({completed_run.WORKFLOW, completed_run.RUNTIME_WORKFLOW} <= set(capture['files']),
          'source-capture-workflow-paths')
    _need(type(stage.repository_id) is int and stage.repository_id > 0 and type(stage.run_id) is int
          and stage.run_id > 0 and type(stage.run_attempt) is int and stage.run_attempt > 0
          and stage.workflow_path == completed_run.WORKFLOW
          and type(stage.job_ids) is tuple and len(stage.job_ids) == 37
          and all(type(item) is int and item > 0 for item in stage.job_ids)
          and len(set(stage.job_ids)) == len(stage.job_ids), 'stage-identity')
    expected_names = set(completed_run.expected_artifact_names(stage.run_id, stage.run_attempt))
    artifacts = tuple(stage.artifacts)
    _need(len(artifacts) == completed_run.MAX_ARTIFACTS
          and {item.name for item in artifacts} == expected_names
          and len({item.artifact_id for item in artifacts}) == len(artifacts)
          and all(type(item.artifact_id) is int and item.artifact_id > 0 and type(item.size) is int
                  and 0 < item.size <= completed_run._artifact_limit(item.name)
                  and type(item.sha256) is str and completed_run.SHA256.fullmatch(item.sha256)
                  and item.staged_filename == item.name + '.zip'
                  for item in artifacts), 'stage-artifact-set')
    _need(type(stage.total_archive_bytes) is int
          and stage.total_archive_bytes == sum(item.size for item in artifacts)
          and stage.total_archive_bytes <= completed_run.MAX_TOTAL_ARCHIVE_BYTES
          and type(stage.source_capture_sha256) is str
          and completed_run.SHA256.fullmatch(stage.source_capture_sha256), 'stage-total')
    _need(type(stage.workflow_sha256s) is tuple
          and all(type(row) is tuple and len(row) == 2 for row in stage.workflow_sha256s),
          'stage-workflow-binding')
    workflow_digests = dict(stage.workflow_sha256s)
    _need(len(stage.workflow_sha256s) == 2 and len(workflow_digests) == 2
          and set(workflow_digests) == {completed_run.WORKFLOW, completed_run.RUNTIME_WORKFLOW}
          and all(type(value) is str and completed_run.SHA256.fullmatch(value)
                  for value in workflow_digests.values()), 'stage-workflow-binding')
    stage_root = Path(stage_directory)
    _need(stage_root.is_absolute() and stage_root.resolve(strict=True) == stage_root
          and stat.S_ISDIR(stage_root.lstat().st_mode) and stat.S_IMODE(stage_root.lstat().st_mode) == 0o700
          and stage_root.lstat().st_uid == os.geteuid(), 'stage-directory')
    _need({entry.name for entry in stage_root.iterdir()} == {item.staged_filename for item in artifacts},
          'stage-directory-entries')
    _check(deadline)
    names = {item.name: item for item in artifacts}
    aggregate_name = f'issue779-checkpoint1-current-{stage.run_id}-{stage.run_attempt}'
    partial_name = f'issue779-protected-full-runtime-partial-{stage.run_id}-{stage.run_attempt}'
    aggregate_binding = names[aggregate_name]
    partial_binding = names[partial_name]
    projections = []
    aggregate_result = None
    aggregate_body = None
    partial_result = None
    archive_cache = {}
    for binding in sorted(artifacts, key=lambda item: item.name):
        _check(deadline)
        raw = _read_stage_file(stage_root, binding, deadline)
        rows, expanded = _zip_directory(raw)
        ordinary_names = {name for name, *_ in rows if not name.endswith('/')}
        if binding.name == aggregate_name:
            _need(ordinary_names == {'aggregate.json'}, 'aggregate-exact-members')
            bodies = _member_bodies(raw, rows, {'aggregate.json'}, deadline)
            body = entrypoint.aggregate_archive(
                raw, {'size_in_bytes': binding.size, 'digest': 'sha256:' + binding.sha256},
                stage.head_sha, stage.source_capture_sha256)
            _need(body == bodies['aggregate.json'], 'aggregate-projection')
            aggregate_value = completed_run._json(body, MAX_AGGREGATE_BYTES, 'aggregate-json')
            aggregate_result = AggregateProjection(
                aggregate_value['schema'], aggregate_value['source'], aggregate_value['source_capture_sha256'],
                tuple(aggregate_value['selectors']), tuple(aggregate_value['cases']), aggregate_value['passed'],
                aggregate_value['skipped'], aggregate_value['exit'], _sha(body))
            source_digest = None
        elif binding.name == partial_name:
            _need(ordinary_names == set(PARTIAL_MEMBERS), 'partial-exact-members')
            bodies = _member_bodies(raw, rows, set(PARTIAL_MEMBERS), deadline)
            _need(bodies['source.json'] == source_capture, 'partial-source')
            _need(bodies['aggregate.zip'] == archive_cache.get(aggregate_name), 'partial-aggregate-archive')
            _need(aggregate_body is not None and bodies['aggregate.json'] == aggregate_body,
                  'partial-aggregate-body')
            partial_result = _validate_partial(bodies['partial-qualification.json'], stage, capture,
                                               aggregate_binding, bodies['aggregate.json'])
            source_digest = _sha(bodies['source.json'])
        else:
            _need('source.json' in ordinary_names, 'artifact-source-member')
            bodies = _member_bodies(raw, rows, {'source.json'}, deadline)
            _need(bodies['source.json'] == source_capture, 'artifact-source-binding')
            source_digest = _sha(bodies['source.json'])
        projections.append(ArchiveProjection(binding.artifact_id, binding.name, binding.sha256, binding.size,
                                             expanded, tuple(MemberProjection(row[0], row[3], row[4], row[6])
                                                             for row in rows), source_digest))
        if binding.name == aggregate_name:
            archive_cache[aggregate_name] = raw
        if binding.name == aggregate_name:
            aggregate_body = body
        if binding.name == partial_name:
            _need(aggregate_result is not None and partial_result is not None, 'partial-ordering')
    _need(aggregate_result is not None and partial_result is not None, 'required-projections')
    return ParsedCompletedRunInventory(stage.repository_id, stage.run_id, stage.run_attempt, stage.head_sha,
                                       stage.source_tree, stage.source_capture_sha256, capture['count'],
                                       tuple(projections), aggregate_result, partial_result)
