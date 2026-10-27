"""Bounded, non-authorizing staging for the public NativeMeasurementTests validator."""
from __future__ import annotations

from dataclasses import dataclass
import hashlib
import math
import os
from pathlib import Path
import stat
import time

import completed_c1
import completed_c2
import completed_c3
import completed_run
import completed_validation

MAX_INPUT_BYTES = 160 * 1024 * 1024
MAX_SOURCE_BYTES = 1024 * 1024
MAX_REPORT_BYTES = 32 * 1024 * 1024
MAX_TRX_BYTES = 1024 * 1024
MAX_OTHER_BYTES = 8 * 1024 * 1024
OUTPUT_DIRECTORY = 'native-data-inputs'

C3_ARTIFACT = 'issue779-checkpoint3-application-'
ACCOUNT_ARTIFACT = 'issue779-c4-accounts-'
C2_ARTIFACT = completed_c2.ARTIFACT_PREFIX

# (role, archive member path, fixed output path)
MEMBER_MAP = (
    ('c3', 'source.json', 'source.json'),
    ('c3', 'measurement-binding.json', 'measurement-binding.json'),
    ('c3', 'native-supervision.json', 'native-supervision.json'),
    ('c3', 'native-sdk.json', 'native-sdk.json'),
    ('c3', 'native-sdk-failure.json', 'native-sdk-failure.json'),
    ('c3', 'native/sdk-test-failure.json', 'sdk-test-failure.json'),
    ('c3', 'native/native-receipt.json', 'native-receipt.json'),
    ('c3', 'root-file-tests/root-files.trx', 'root-files.trx'),
    ('c3', 'root-file-tests/peer-credentials.trx', 'root-file-tests/peer-credentials.trx'),
    ('c3', 'root-supplement-tests/root-supplement.trx', 'root-supplement.trx'),
    ('c3', 'application-bundle-tests/application-bundle.trx',
     'application-bundle-tests/application-bundle.trx'),
    ('creation', 'source.json', 'accounts-creation/source.json'),
    ('creation', 'measurement-binding.json', 'accounts-creation/measurement-binding.json'),
    ('creation', 'native-accounts.json', 'accounts-creation/native-accounts.json'),
    ('creation', 'native-accounts-receipt.json', 'accounts-creation/native-accounts-receipt.json'),
    ('rollback', 'source.json', 'accounts-rollback/source.json'),
    ('rollback', 'measurement-binding.json', 'accounts-rollback/measurement-binding.json'),
    ('rollback', 'native-accounts.json', 'accounts-rollback/native-accounts.json'),
    ('rollback', 'native-accounts-receipt.json', 'accounts-rollback/native-accounts-receipt.json'),
    ('c2', 'source.json', 'producer-roots/source.json'),
    ('c2', 'producer-roots-binding.json', 'producer-roots/producer-roots-binding.json'),
    ('c2', 'native-producer-roots.json', 'producer-roots/native-producer-roots.json'),
    ('c2', 'native-producer-roots-c2.json', 'producer-roots/native-producer-roots-c2.json'),
    ('c2', 'native/native-receipt.json', 'producer-roots/native-receipt.json'),
)
EXPECTED_OUTPUTS = tuple(row[2] for row in MEMBER_MAP)
ROLE_MEMBERS = {
    role: frozenset(member for selected_role, member, _output in MEMBER_MAP if selected_role == role)
    for role in ('c3', 'creation', 'rollback', 'c2')
}


def _need(condition, reason):
    if not condition:
        raise ValueError('completed-native-inputs-' + reason)


def _remaining(deadline):
    _need(type(deadline) in (int, float) and math.isfinite(deadline)
          and time.monotonic() < deadline, 'deadline')


def _sha(raw):
    return hashlib.sha256(raw).hexdigest()


def _role_names(stage):
    suffix = f'{stage.run_id}-{stage.run_attempt}'
    return {
        'c3': C3_ARTIFACT + suffix,
        'creation': ACCOUNT_ARTIFACT + f'C4Accounts-{suffix}',
        'rollback': ACCOUNT_ARTIFACT + f'C4AccountRollback-{suffix}',
        'c2': C2_ARTIFACT + suffix,
    }


def _file_limit(relative):
    name = Path(relative).name
    if name == 'source.json':
        return MAX_SOURCE_BYTES
    if name in ('native-supervision.json', 'native-sdk.json', 'native-sdk-failure.json',
                'native-accounts.json', 'native-producer-roots.json', 'native-producer-roots-c2.json'):
        return MAX_REPORT_BYTES
    if name.endswith('.trx'):
        return MAX_TRX_BYTES
    return MAX_OTHER_BYTES


def _role_preflight(role, rows, expanded):
    leaves = tuple(row for row in rows if not row[0].endswith('/'))
    _need({row[0] for row in leaves} >= ROLE_MEMBERS[role], role + '-required-members')
    if role == 'c3':
        _need(len(leaves) <= completed_c3.MAX_APPLICATION_FILES
              and expanded <= completed_c3.MAX_APPLICATION_BYTES, 'c3-retention-total')
        def maximum(row):
            basename = Path(row[0]).name
            if basename in ('native-supervision.json', 'native-sdk.json'):
                return completed_c3.MAX_SELECTED_JSON
            if basename == 'native-supervision.cobertura.xml':
                return completed_c3.MAX_COBERTURA_XML
            return MAX_OTHER_BYTES
    elif role in ('creation', 'rollback'):
        allowed = {'source.json', 'driver-controls.log', 'restore.log', 'publish.log',
                   'coverlet-install.log', 'collector.log', 'account-original.json',
                   'account-instrumented.json', 'staging.json', 'owner.stdout', 'owner.stderr',
                   'native-accounts-receipt.json', 'native-accounts.json', 'measurement-binding.json'}
        _need(len(leaves) <= 128 and sum(row[4] for row in leaves) <= 96 * 1024 * 1024
              and {row[0] for row in leaves} <= allowed, role + '-retention-total')
        def maximum(row):
            return (32 if row[0] == 'native-accounts.json' else 8) * 1024 * 1024
    else:
        _need(len(leaves) <= completed_c2.MAX_RETAINED_FILES
              and expanded <= completed_c2.MAX_RETAINED_BYTES
              and len(rows) <= completed_validation.MAX_ARCHIVE_MEMBERS, 'c2-retention-total')
        def maximum(row):
            return (completed_c2.MAX_RETAINED_REPORT
                    if row[0] in ('native-producer-roots.json', 'native-producer-roots-c2.json')
                    else completed_c2.MAX_RETAINED_LEAF)
    _need(all(row[4] <= maximum(row) for row in leaves), role + '-retention-leaf')


def _archive_projection(stage, inventory, stage_root, name, role, deadline):
    binding = next((item for item in stage.artifacts if item.name == name), None)
    projection = next((item for item in inventory.archives if item.artifact_name == name), None)
    _need(binding is not None and projection is not None
          and binding.staged_filename == name + '.zip'
          and binding.size <= completed_run._artifact_limit(name), role + '-artifact')
    raw = completed_c1._open_bound_file(stage_root / binding.staged_filename, binding.size,
        binding.sha256, deadline=deadline, maximum=completed_run._artifact_limit(name), required_mode=0o600)
    rows, expanded = completed_validation._zip_directory(raw)
    actual_members = tuple(completed_validation.MemberProjection(row[0], row[3], row[4], row[6])
                           for row in rows)
    _need(projection.members == actual_members and projection.expanded_bytes == expanded,
          role + '-archive-projection')
    _role_preflight(role, rows, expanded)
    selected_rows = {row[0]: row for row in rows if row[0] in ROLE_MEMBERS[role]}
    _need(set(selected_rows) == set(ROLE_MEMBERS[role]), role + '-selected-members')
    _need(all(row[4] <= _file_limit(member) for member, row in selected_rows.items()),
          role + '-selected-preflight')
    return raw, rows, selected_rows


def _extract_selected(role, raw, rows, selected_rows, deadline):
    bodies = completed_validation._member_bodies(raw, rows, ROLE_MEMBERS[role], deadline)
    _need(set(bodies) == set(ROLE_MEMBERS[role]), role + '-selected-members')
    return bodies


def _owned_identity(info):
    return completed_c1._node_identity(info)


@dataclass(frozen=True)
class NativeInputFile:
    """One copied fixed-name leaf, with its content digest and owned inode identity."""
    relative_path: str
    size: int
    sha256: str
    device: int
    inode: int
    identity: tuple[int, ...]


@dataclass(frozen=True)
class NativeInputs:
    """Immutable staged files and cleanup custody; never a validator or acceptance result."""
    run_id: int
    run_attempt: int
    head_sha: str
    source_capture_sha256: str
    root: Path
    parent: Path
    root_identity: tuple[int, ...]
    files: tuple[NativeInputFile, ...]
    owned_nodes: tuple[tuple[tuple[str, ...], tuple[int, ...]], ...]
    validator_executed: bool = False
    native_acceptance: bool = False
    qualification_complete: bool = False


def stage_native_inputs(stage, inventory, stage_directory, source_capture, private_parent, *, deadline):
    """Stage the exact 24 public-test input leaves without executing any test or archive code.

    The caller supplies an already resolved run stage, its parsed inventory, and the complete
    source capture bytes independently authenticated against the run head. This function binds
    those bytes to the stage but does not authenticate their origin. Successful output remains
    in `native-data-inputs` until `cleanup_native_inputs` is called.
    """
    _remaining(deadline)
    _need(isinstance(stage, completed_run.CompletedRunStage), 'stage-type')
    _need(isinstance(inventory, completed_validation.ParsedCompletedRunInventory), 'inventory-type')
    completed_c1._assert_projection(stage, inventory)
    stage_root = Path(stage_directory)
    parent = Path(private_parent)
    for path, code in ((stage_root, 'stage-root'), (parent, 'private-parent')):
        _need(path.is_absolute() and path.resolve(strict=True) == path, code + '-path')
        info = path.lstat()
        _need(stat.S_ISDIR(info.st_mode) and not stat.S_ISLNK(info.st_mode)
              and info.st_uid == os.geteuid(), code + '-shape')
    stage_info, parent_info = stage_root.lstat(), parent.lstat()
    _need(not (parent == stage_root or stage_root in parent.parents or parent in stage_root.parents),
          'stage-parent-overlap')
    _need(not os.listdir(parent), 'private-parent-not-empty')
    _need(stat.S_IMODE(stage_info.st_mode) == 0o700 and stat.S_IMODE(parent_info.st_mode) == 0o700,
          'private-mode')
    expected_stage_leaves = {item.staged_filename for item in stage.artifacts}
    _need(set(os.listdir(stage_root)) == expected_stage_leaves, 'stage-directory-inventory')
    capture_raw = source_capture
    _need(type(capture_raw) is bytes and 0 < len(capture_raw) <= MAX_SOURCE_BYTES
          and _sha(capture_raw) == stage.source_capture_sha256, 'capture-binding')
    capture = completed_c1._decode(capture_raw, MAX_SOURCE_BYTES, 'capture-json')
    _need(capture.get('schema') == 'issue779-checkpoint2-source-v1'
          and capture.get('source') == stage.head_sha and capture.get('tree') == stage.source_tree
          and type(capture.get('files')) is dict and type(capture.get('count')) is int
          and capture['count'] > 0
          and capture['count'] == len(capture['files']), 'capture-identity')
    _need(inventory.source_count == capture['count'], 'inventory-source-count')
    names = _role_names(stage)
    projections = {}
    for role, archive_name in names.items():
        _remaining(deadline)
        projections[role] = _archive_projection(stage, inventory, stage_root, archive_name, role, deadline)
        raw, rows, selected_rows = projections[role]
        _need(selected_rows['source.json'][4] == len(capture_raw), role + '-capture-size')
        _need(_sha(capture_raw) == stage.source_capture_sha256, role + '-capture-bytes')
    declared_total = sum(row[4] for _raw, _rows, selected_rows in projections.values()
                         for row in selected_rows.values())
    _need(declared_total <= MAX_INPUT_BYTES, 'selected-total-preflight')
    extracted = {}
    for role, (raw, rows, selected_rows) in projections.items():
        _remaining(deadline)
        bodies = _extract_selected(role, raw, rows, selected_rows, deadline)
        _need(bodies['source.json'] == capture_raw, role + '-capture-bytes')
        extracted[role] = bodies
    selected = {}
    for role, member, destination in MEMBER_MAP:
        body = extracted[role][member]
        _need(type(body) is bytes and 0 < len(body) <= _file_limit(destination), 'selected-file-bound')
        _need(destination not in selected, 'destination-duplicate')
        if Path(destination).name == 'source.json':
            _need(body == capture_raw, 'selected-capture-bytes')
        selected[destination] = body
    total = sum(map(len, selected.values()))
    _need(set(selected) == set(EXPECTED_OUTPUTS) and len(selected) == 24
          and total <= MAX_INPUT_BYTES, 'selected-inventory')
    work = parent / OUTPUT_DIRECTORY
    owned = {}
    work_identity = None
    primary = None
    result = None
    try:
        _need(not os.path.lexists(work), 'output-exists')
        completed_c1._mkdir(work, owned=owned, ownership_root=work)
        work_identity = owned.get(())
        for relative, raw in selected.items():
            _remaining(deadline)
            target = work / relative
            cursor = work
            for component in Path(relative).parts[:-1]:
                cursor = cursor / component
                if not os.path.lexists(cursor):
                    completed_c1._mkdir(cursor, owned=owned, ownership_root=work)
            completed_c1._write_bytes(target, raw, deadline=deadline, owned=owned, ownership_root=work)
        files = []
        for relative, raw in sorted(selected.items()):
            path = work / relative
            identity = owned[tuple(Path(relative).parts)]
            info = path.lstat()
            _need(_owned_identity(info) == identity and stat.S_ISREG(info.st_mode)
                  and info.st_nlink == 1 and stat.S_IMODE(info.st_mode) == 0o600
                  and info.st_size == len(raw), 'output-custody')
            files.append(NativeInputFile(relative, len(raw), _sha(raw), info.st_dev, info.st_ino, identity))
        _remaining(deadline)
        result = NativeInputs(stage.run_id, stage.run_attempt, stage.head_sha,
            stage.source_capture_sha256, work, parent, work_identity, tuple(files),
            tuple(sorted(owned.items())))
        verify_native_inputs(result, stage, deadline=deadline)
    except BaseException as error:
        primary = error
    if primary is not None or result is None:
        work_identity = work_identity or owned.get(())
        if work_identity is not None:
            completed_c1._finish_invocation(parent, work, work_identity, owned, primary)
        raise primary or ValueError('completed-native-inputs-incomplete')
    return result


def verify_native_inputs(data, stage, *, deadline):
    """Re-read the fixed input tree and require its original owned inodes and content.

    Call before a controlled copy and after validator execution. This checks only the
    staged bytes and custody, not the source checkout or execution of a validator.
    """
    _remaining(deadline)
    _need(type(data) is NativeInputs and data.validator_executed is False
          and data.native_acceptance is False and data.qualification_complete is False,
          'verification-type')
    _need(type(stage) is completed_run.CompletedRunStage
          and type(data.run_id) is int and data.run_id > 0 and data.run_id == stage.run_id
          and type(data.run_attempt) is int and data.run_attempt > 0 and data.run_attempt == stage.run_attempt
          and type(data.head_sha) is str and completed_run.SHA1.fullmatch(data.head_sha) is not None
          and data.head_sha == stage.head_sha and type(data.source_capture_sha256) is str
          and completed_run.SHA256.fullmatch(data.source_capture_sha256) is not None
          and data.source_capture_sha256 == stage.source_capture_sha256, 'verification-attribution')
    root, parent = data.root, data.parent
    _need(isinstance(root, Path) and isinstance(parent, Path), 'verification-path-type')
    _need(root.is_absolute() and root.resolve(strict=True) == root
          and root.name == OUTPUT_DIRECTORY and root.parent == parent
          and parent.resolve(strict=True) == parent, 'verification-path')
    parent_info = parent.lstat()
    _need(stat.S_ISDIR(parent_info.st_mode) and parent_info.st_uid == os.geteuid()
          and stat.S_IMODE(parent_info.st_mode) == 0o700, 'verification-parent')
    _need(type(data.files) is tuple and len(data.files) == len(EXPECTED_OUTPUTS)
          and all(type(row) is NativeInputFile for row in data.files), 'verification-files')
    files = {row.relative_path: row for row in data.files}
    _need(len(files) == len(EXPECTED_OUTPUTS) and set(files) == set(EXPECTED_OUTPUTS),
          'verification-inventory')
    directories = {()}
    for relative in EXPECTED_OUTPUTS:
        directories.update(tuple(path.parts) for path in Path(relative).parents if path.parts)
    owned = dict(data.owned_nodes)
    _need(len(owned) == len(data.owned_nodes)
          and set(owned) == directories | {tuple(Path(name).parts) for name in EXPECTED_OUTPUTS}
          and owned.get(()) == data.root_identity, 'verification-ownership')
    for parts in sorted(directories, key=lambda item: (len(item), item)):
        _remaining(deadline)
        path = root.joinpath(*parts)
        info = path.lstat()
        _need(stat.S_ISDIR(info.st_mode) and not stat.S_ISLNK(info.st_mode)
              and _owned_identity(info) == owned[parts], 'verification-directory')
        expected_children = {key[len(parts)] for key in owned
                             if len(key) == len(parts) + 1 and key[:len(parts)] == parts}
        _need(set(os.listdir(path)) == expected_children, 'verification-directory-inventory')
    total = 0
    for relative, row in files.items():
        _remaining(deadline)
        path = root / relative
        info = path.lstat()
        _need(type(row.size) is int and 0 < row.size <= _file_limit(relative)
              and row.identity == owned[tuple(Path(relative).parts)]
              and _owned_identity(info) == row.identity
              and (info.st_dev, info.st_ino) == (row.device, row.inode), 'verification-file-custody')
        raw = completed_c1._open_bound_file(path, row.size, row.sha256,
            deadline=deadline, maximum=_file_limit(relative), required_mode=0o600,
            expected_identity=row.identity)
        if relative == 'source.json':
            _need(_sha(raw) == data.source_capture_sha256, 'verification-source')
            capture = completed_c1._decode(raw, MAX_SOURCE_BYTES, 'verification-source-json')
            _need(capture.get('schema') == 'issue779-checkpoint2-source-v1'
                  and capture.get('source') == data.head_sha and capture.get('tree') == stage.source_tree,
                  'verification-source-identity')
        elif Path(relative).name == 'source.json':
            _need(_sha(raw) == data.source_capture_sha256, 'verification-source')
        total += len(raw)
        _need(total <= MAX_INPUT_BYTES, 'verification-total')
    _remaining(deadline)


def cleanup_native_inputs(data):
    """Remove only the invocation-owned `native-data-inputs` tree; never remove its parent."""
    _need(isinstance(data, NativeInputs), 'cleanup-type')
    _need(data.root.name == OUTPUT_DIRECTORY and data.root.parent == data.parent, 'cleanup-path')
    owned = dict(data.owned_nodes)
    _need(len(owned) == len(data.owned_nodes), 'cleanup-ownership')
    completed_c1._finish_invocation(data.parent, data.root, data.root_identity, owned, None)
