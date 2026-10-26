"""Validation-only parser for a protected parent run's fixed evidence archive."""
from dataclasses import dataclass
import hashlib
import json
import re
import stat
import struct
from pathlib import PurePosixPath
import zipfile
from io import BytesIO


MAX_EXPECTATION_BYTES = 16 * 1024
MAX_METADATA_BYTES = 16 * 1024
MAX_ARCHIVE_BYTES = 32 * 1024 * 1024
MAX_ZIP_MEMBERS = 2
MAX_ZIP_CENTRAL_DIRECTORY_BYTES = 16 * 1024
MAX_ZIP_NAME_BYTES = 128
MAX_ZIP_EXTRA_BYTES = 4096
MAX_ZIP_COMMENT_BYTES = 4096
MAX_DOCUMENT_BYTES = 1024 * 1024
MAX_EXPANDED_BYTES = 2 * MAX_DOCUMENT_BYTES

EXPECTED_KEYS = frozenset((
    'schema', 'repository', 'run_id', 'run_attempt', 'head_sha', 'workflow_path',
    'workflow_sha', 'base_sha', 'subject_sha', 'artifact_id', 'artifact_name',
    'artifact_sha256', 'artifact_bytes',
))
RUN_KEYS = frozenset((
    'repository', 'run_id', 'run_attempt', 'head_sha', 'workflow_path',
    'workflow_sha', 'base_sha', 'subject_sha', 'event',
))
ARTIFACT_KEYS = frozenset((
    'repository', 'run_id', 'artifact_id', 'artifact_name', 'expired', 'size_in_bytes', 'digest',
))
SELECTED_MEMBERS = ('plan.json', 'manifest.json')
MAX_ID = (1 << 63) - 1
_LOWER_SHA1 = re.compile(r'[0-9a-f]{40}\Z')
_LOWER_SHA256 = re.compile(r'[0-9a-f]{64}\Z')
_REPOSITORY = re.compile(r'[A-Za-z0-9_.-]{1,100}/[A-Za-z0-9_.-]{1,100}\Z')
_ARTIFACT_NAME = re.compile(r'[A-Za-z0-9_.-]{1,128}\Z')


@dataclass(frozen=True)
class ProtectedParentDocuments:
    """Immutable document bytes only; this value grants no admission or gate result."""

    plan_bytes: bytes
    manifest_bytes: bytes


def _require(condition, code):
    if not condition:
        raise ValueError(code)


def _pairs_without_duplicates(pairs):
    value = {}
    folded = set()
    for key, item in pairs:
        _require(type(key) is str and key not in value and key.casefold() not in folded,
                 'protected-parent-duplicate-or-case-alias')
        value[key] = item
        folded.add(key.casefold())
    return value


def _decode(raw, limit, code):
    _require(type(raw) is bytes and 0 < len(raw) <= limit, code)
    try:
        value = json.loads(raw.decode('utf-8'), object_pairs_hook=_pairs_without_duplicates,
                           parse_constant=lambda _: (_ for _ in ()).throw(ValueError(code)))
    except (UnicodeDecodeError, json.JSONDecodeError, RecursionError):
        raise ValueError(code) from None
    _require(type(value) is dict, code)
    return value


def _exact_keys(value, keys, code):
    _require(set(value) == keys, code)


def _text(value, maximum, code):
    _require(type(value) is str and 0 < len(value) <= maximum
             and not any(ord(character) < 0x20 or ord(character) == 0x7f for character in value), code)
    return value


def _integer(value, minimum, maximum, code):
    _require(type(value) is int and minimum <= value <= maximum, code)
    return value


def _sha(value, pattern, code):
    _require(type(value) is str and pattern.fullmatch(value) is not None, code)
    return value


def _workflow_path(value):
    value = _text(value, 256, 'protected-parent-workflow-path')
    _require('\\' not in value and ':' not in value and value.startswith('.github/workflows/')
             and value.endswith(('.yml', '.yaml')), 'protected-parent-workflow-path')
    parts = PurePosixPath(value).parts
    _require(parts and all(part not in ('', '.', '..') for part in parts)
             and PurePosixPath(value).as_posix() == value, 'protected-parent-workflow-path')
    return value


def _repository(value):
    value = _text(value, 202, 'protected-parent-repository')
    _require(_REPOSITORY.fullmatch(value) is not None, 'protected-parent-repository')
    return value


def _expectation(raw):
    value = _decode(raw, MAX_EXPECTATION_BYTES, 'protected-parent-expectation-json')
    _exact_keys(value, EXPECTED_KEYS, 'protected-parent-expectation-fields')
    _require(value['schema'] == 'issue779-protected-parent-expectation-v1',
             'protected-parent-expectation-schema')
    _repository(value['repository'])
    _integer(value['run_id'], 1, MAX_ID, 'protected-parent-run-id')
    _integer(value['run_attempt'], 1, 1000, 'protected-parent-run-attempt')
    _sha(value['head_sha'], _LOWER_SHA1, 'protected-parent-head-sha')
    _workflow_path(value['workflow_path'])
    _require(type(value['workflow_sha']) is str and (
        _LOWER_SHA1.fullmatch(value['workflow_sha']) is not None
        or _LOWER_SHA256.fullmatch(value['workflow_sha']) is not None),
        'protected-parent-workflow-sha')
    _sha(value['base_sha'], _LOWER_SHA1, 'protected-parent-base-sha')
    _sha(value['subject_sha'], _LOWER_SHA1, 'protected-parent-subject-sha')
    _integer(value['artifact_id'], 1, MAX_ID, 'protected-parent-artifact-id')
    _text(value['artifact_name'], 128, 'protected-parent-artifact-name')
    _require(_ARTIFACT_NAME.fullmatch(value['artifact_name']) is not None
             and value['artifact_name'] not in ('.', '..'), 'protected-parent-artifact-name')
    _sha(value['artifact_sha256'], _LOWER_SHA256, 'protected-parent-artifact-sha256')
    _integer(value['artifact_bytes'], 1, MAX_ARCHIVE_BYTES, 'protected-parent-artifact-bytes')
    return value


def _actual_run(raw):
    value = _decode(raw, MAX_METADATA_BYTES, 'protected-parent-run-json')
    _exact_keys(value, RUN_KEYS, 'protected-parent-run-fields')
    _repository(value['repository'])
    _integer(value['run_id'], 1, MAX_ID, 'protected-parent-run-id')
    _integer(value['run_attempt'], 1, 1000, 'protected-parent-run-attempt')
    _sha(value['head_sha'], _LOWER_SHA1, 'protected-parent-head-sha')
    _workflow_path(value['workflow_path'])
    _require(type(value['workflow_sha']) is str and (
        _LOWER_SHA1.fullmatch(value['workflow_sha']) is not None
        or _LOWER_SHA256.fullmatch(value['workflow_sha']) is not None),
        'protected-parent-workflow-sha')
    _sha(value['base_sha'], _LOWER_SHA1, 'protected-parent-base-sha')
    _sha(value['subject_sha'], _LOWER_SHA1, 'protected-parent-subject-sha')
    _require(_text(value['event'], 64, 'protected-parent-run-event') in ('push', 'workflow_dispatch'),
             'protected-parent-run-event')
    return value


def _actual_artifact(raw):
    value = _decode(raw, MAX_METADATA_BYTES, 'protected-parent-artifact-json')
    _exact_keys(value, ARTIFACT_KEYS, 'protected-parent-artifact-fields')
    _repository(value['repository'])
    _integer(value['run_id'], 1, MAX_ID, 'protected-parent-artifact-run-id')
    _integer(value['artifact_id'], 1, MAX_ID, 'protected-parent-artifact-id')
    _text(value['artifact_name'], 128, 'protected-parent-artifact-name')
    _require(_ARTIFACT_NAME.fullmatch(value['artifact_name']) is not None,
             'protected-parent-artifact-name')
    _require(type(value['expired']) is bool and value['expired'] is False,
             'protected-parent-artifact-expired')
    _integer(value['size_in_bytes'], 1, MAX_ARCHIVE_BYTES, 'protected-parent-artifact-bytes')
    digest = _text(value['digest'], 71, 'protected-parent-artifact-digest')
    _require(re.fullmatch(r'sha256:[0-9a-f]{64}', digest) is not None,
             'protected-parent-artifact-digest')
    return value


def _safe_zip_name(name):
    _require(type(name) is str and 0 < len(name) <= 128 and '\\' not in name and ':' not in name,
             'protected-parent-zip-name')
    path = PurePosixPath(name)
    _require(not path.is_absolute() and path.as_posix() == name
             and all(part not in ('', '.', '..') for part in path.parts),
             'protected-parent-zip-name')


def _precheck_zip_structure(raw):
    """Bound the central directory before ZipFile can allocate one ZipInfo per entry."""
    size = len(raw)
    tail_start = max(0, size - (22 + 65535))
    tail = raw[tail_start:]
    signature = b'PK\x05\x06'
    candidates = []
    cursor = 0
    while True:
        found = tail.find(signature, cursor)
        if found < 0:
            break
        absolute = tail_start + found
        if found + 22 <= len(tail):
            comment_bytes = struct.unpack_from('<H', raw, absolute + 20)[0]
            if absolute + 22 + comment_bytes == size:
                candidates.append(absolute)
        cursor = found + 1
    _require(len(candidates) == 1, 'protected-parent-zip-eocd')
    eocd = candidates[0]
    disk, central_disk, disk_entries, total_entries, central_size, central_offset, comment_size = \
        struct.unpack_from('<HHHHIIH', raw, eocd + 4)
    _require(comment_size <= MAX_ZIP_COMMENT_BYTES, 'protected-parent-zip-comment-bounds')
    _require(disk == 0 and central_disk == 0 and disk_entries == MAX_ZIP_MEMBERS
             and total_entries == MAX_ZIP_MEMBERS, 'protected-parent-zip-disk-or-count')
    _require(central_size not in (0xFFFFFFFF,) and central_offset not in (0xFFFFFFFF,)
             and central_size <= MAX_ZIP_CENTRAL_DIRECTORY_BYTES
             and central_offset <= eocd and central_size == eocd - central_offset,
             'protected-parent-zip-central-bounds')

    end = central_offset + central_size
    position = central_offset
    entries = []
    for _ in range(MAX_ZIP_MEMBERS):
        _require(position <= end and end - position >= 46
                 and raw[position:position + 4] == b'PK\x01\x02',
                 'protected-parent-zip-central-header')
        flags = struct.unpack_from('<H', raw, position + 8)[0]
        method = struct.unpack_from('<H', raw, position + 10)[0]
        compressed = struct.unpack_from('<I', raw, position + 20)[0]
        expanded = struct.unpack_from('<I', raw, position + 24)[0]
        name_size, extra_size, entry_comment_size, disk_start = struct.unpack_from(
            '<HHHH', raw, position + 28)
        local_offset = struct.unpack_from('<I', raw, position + 42)[0]
        _require(name_size > 0 and name_size <= MAX_ZIP_NAME_BYTES
                 and extra_size <= MAX_ZIP_EXTRA_BYTES
                 and entry_comment_size <= MAX_ZIP_COMMENT_BYTES
                 and disk_start == 0 and local_offset != 0xFFFFFFFF
                 and compressed != 0xFFFFFFFF and expanded != 0xFFFFFFFF
                 and compressed <= MAX_ARCHIVE_BYTES and 0 < expanded <= MAX_DOCUMENT_BYTES
                 and method in (zipfile.ZIP_STORED, zipfile.ZIP_DEFLATED)
                 and not (flags & 1), 'protected-parent-zip-central-fields')
        record_size = 46 + name_size + extra_size + entry_comment_size
        _require(record_size <= end - position, 'protected-parent-zip-central-truncated')
        name_bytes_start = position + 46
        name_bytes = raw[name_bytes_start:name_bytes_start + name_size]
        try:
            name = name_bytes.decode('ascii')
        except UnicodeDecodeError:
            raise ValueError('protected-parent-zip-name') from None
        _safe_zip_name(name)
        extra_start = name_bytes_start + name_size
        extra_end = extra_start + extra_size
        extra_cursor = extra_start
        while extra_cursor < extra_end:
            _require(extra_end - extra_cursor >= 4, 'protected-parent-zip-extra')
            field_id, field_size = struct.unpack_from('<HH', raw, extra_cursor)
            extra_cursor += 4
            _require(field_size <= extra_end - extra_cursor and field_id != 1,
                     'protected-parent-zip-extra')
            extra_cursor += field_size

        _require(local_offset <= central_offset and central_offset - local_offset >= 30
                 and raw[local_offset:local_offset + 4] == b'PK\x03\x04',
                 'protected-parent-zip-local-header')
        local_flags, local_method = struct.unpack_from('<HH', raw, local_offset + 6)
        local_compressed, local_expanded = struct.unpack_from('<II', raw, local_offset + 18)
        local_name_size, local_extra_size = struct.unpack_from('<HH', raw, local_offset + 26)
        local_header_size = 30 + local_name_size + local_extra_size
        _require(local_name_size == name_size and local_extra_size <= MAX_ZIP_EXTRA_BYTES
                 and local_header_size <= central_offset - local_offset
                 and raw[local_offset + 30:local_offset + 30 + local_name_size] == name_bytes
                 and local_flags == flags and local_method == method,
                 'protected-parent-zip-local-fields')
        _require((flags & 8) != 0 or (local_compressed == compressed and local_expanded == expanded),
                 'protected-parent-zip-local-sizes')
        local_extra_start = local_offset + 30 + local_name_size
        local_extra_end = local_offset + local_header_size
        local_extra_cursor = local_extra_start
        while local_extra_cursor < local_extra_end:
            _require(local_extra_end - local_extra_cursor >= 4, 'protected-parent-zip-extra')
            field_id, field_size = struct.unpack_from('<HH', raw, local_extra_cursor)
            local_extra_cursor += 4
            _require(field_size <= local_extra_end - local_extra_cursor and field_id != 1,
                     'protected-parent-zip-extra')
            local_extra_cursor += field_size
        data_start = local_offset + local_header_size
        _require(compressed <= central_offset - data_start,
                 'protected-parent-zip-local-bounds')
        entries.append((name, local_offset, data_start + compressed))
        position += record_size

    _require(position == end and {entry[0] for entry in entries} == set(SELECTED_MEMBERS)
             and len({entry[0].casefold() for entry in entries}) == MAX_ZIP_MEMBERS,
             'protected-parent-zip-central-count')
    ordered_ranges = sorted((entry[1], entry[2]) for entry in entries)
    _require(ordered_ranges[0][1] <= ordered_ranges[1][0],
             'protected-parent-zip-local-overlap')


def _selected_documents(archive_bytes):
    _require(type(archive_bytes) is bytes and 0 < len(archive_bytes) <= MAX_ARCHIVE_BYTES,
             'protected-parent-archive-bytes')
    _precheck_zip_structure(archive_bytes)
    try:
        with zipfile.ZipFile(BytesIO(archive_bytes), 'r') as archive:
            infos = archive.infolist()
            _require(len(infos) == MAX_ZIP_MEMBERS, 'protected-parent-zip-members')
            names = [info.filename for info in infos]
            for name in names:
                _safe_zip_name(name)
            _require(len(set(names)) == len(names)
                     and len({name.casefold() for name in names}) == len(names),
                     'protected-parent-zip-duplicate-or-case-alias')
            _require(set(names) == set(SELECTED_MEMBERS), 'protected-parent-zip-selected-members')
            _require(all(not info.is_dir() and not (info.flag_bits & 1)
                         and info.compress_type in (zipfile.ZIP_STORED, zipfile.ZIP_DEFLATED)
                         for info in infos), 'protected-parent-zip-entry-kind')
            for info in infos:
                mode = (info.external_attr >> 16) & 0xFFFF
                kind = stat.S_IFMT(mode)
                _require(kind == stat.S_IFREG and 0 < info.file_size <= MAX_DOCUMENT_BYTES
                         and 0 <= info.compress_size <= MAX_ARCHIVE_BYTES,
                         'protected-parent-zip-entry-bounds')
            _require(sum(info.file_size for info in infos) <= MAX_EXPANDED_BYTES,
                     'protected-parent-zip-expanded-bounds')
            bodies = {}
            for info in infos:
                with archive.open(info, 'r') as stream:
                    body = stream.read(MAX_DOCUMENT_BYTES + 1)
                    _require(len(body) == info.file_size and len(body) <= MAX_DOCUMENT_BYTES
                             and stream.read(1) == b'', 'protected-parent-zip-stream-bounds')
                bodies[info.filename] = body
            return ProtectedParentDocuments(bodies['plan.json'], bodies['manifest.json'])
    except ValueError:
        raise
    except Exception:
        raise ValueError('protected-parent-zip-invalid') from None


def preflight_protected_parent(expectation_bytes, actual_run_bytes, actual_artifact_bytes, archive_bytes):
    """Compare an independently supplied protected expectation and API projection, then select two bounded ZIP leaves.

    The caller must obtain these inputs through its authenticated protected parent context. This function performs
    no network, token, process, policy, admission, gate, signature or runtime-capability operation.
    """
    expected = _expectation(expectation_bytes)
    run = _actual_run(actual_run_bytes)
    artifact = _actual_artifact(actual_artifact_bytes)
    for name in ('repository', 'run_id', 'run_attempt', 'head_sha', 'workflow_path',
                 'workflow_sha', 'base_sha', 'subject_sha'):
        _require(run[name] == expected[name], 'protected-parent-run-mismatch')
    for name in ('repository', 'run_id', 'artifact_id'):
        expected_name = {'artifact_id': 'artifact_id'}.get(name, name)
        _require(artifact[name] == expected[expected_name], 'protected-parent-artifact-mismatch')
    _require(artifact['artifact_name'] == expected['artifact_name']
             and artifact['size_in_bytes'] == expected['artifact_bytes']
             and artifact['digest'] == 'sha256:' + expected['artifact_sha256'],
             'protected-parent-artifact-mismatch')
    _require(type(archive_bytes) is bytes and len(archive_bytes) == expected['artifact_bytes']
             and len(archive_bytes) == artifact['size_in_bytes'], 'protected-parent-artifact-size')
    actual_sha = hashlib.sha256(archive_bytes).hexdigest()
    _require(actual_sha == expected['artifact_sha256']
             and artifact['digest'] == 'sha256:' + actual_sha, 'protected-parent-artifact-digest')
    # Check the digest before constructing a ZipFile or touching any archive member.
    return _selected_documents(archive_bytes)
