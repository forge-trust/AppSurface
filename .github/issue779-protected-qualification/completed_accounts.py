"""Fixed, non-authorizing replay of the two original C4 account receipt validators."""
from __future__ import annotations

from dataclasses import dataclass
import hashlib
from types import SimpleNamespace
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
sys.path.insert(0, str(ROOT / '.github/issue779-checkpoint4'))
import completed_run
import completed_validation
import completed_c1
import completed_c3

VARIANTS = ('C4Accounts', 'C4AccountRollback')
MAX_SOURCE_BYTES = 1024 * 1024
MAX_RECEIPT_BYTES = 64 * 1024
MAX_REPORT_BYTES = 32 * 1024 * 1024
MAX_ARCHIVE_BYTES = 12 * 1024 * 1024
MAX_TOTAL_ACCOUNT_ARCHIVES = 512 * 1024 * 1024
REQUIRED_MEMBERS = ('source.json', 'native-accounts-receipt.json', 'account-original.json',
                    'account-instrumented.json', 'native-accounts.json', 'measurement-binding.json',
                    'owner.stdout', 'owner.stderr')
ACCOUNT_ARTIFACT_PREFIX = 'issue779-c4-accounts-'
RETAINED_NAMES = frozenset(('source.json driver-controls.log restore.log publish.log coverlet-install.log '
                            'collector.log account-original.json account-instrumented.json staging.json '
                            'owner.stdout owner.stderr native-accounts-receipt.json native-accounts.json '
                            'measurement-binding.json').split())


def _need(value, code):
    if not value:
        raise ValueError('completed-accounts-' + code)


def _remaining(deadline):
    _need(type(deadline) in (int, float) and math.isfinite(deadline)
          and time.monotonic() < deadline, 'deadline')


def _sha(raw):
    return hashlib.sha256(raw).hexdigest()


def _json(raw, maximum, code):
    _need(type(raw) is bytes and 0 < len(raw) <= maximum, code)
    try:
        value = json.loads(raw.decode('utf-8'), object_pairs_hook=completed_run._unique_pairs,
                           parse_constant=lambda _x: (_ for _ in ()).throw(ValueError(code)))
    except (UnicodeError, json.JSONDecodeError, RecursionError, ValueError):
        raise ValueError('completed-accounts-' + code) from None
    return value


def _validate_owner_streams(outer, receipt, stdout, stderr):
    _need(type(stdout) is bytes and 1 < len(stdout) <= 4097 and stdout.endswith(b'\n')
          and stderr == b'' and outer.get('owner_stdout_sha256') == _sha(stdout)
          and _json(stdout[:-1], 4096, 'owner-receipt-json') == receipt, 'owner-stream-binding')


def _load_original_validator(source_root, capture, deadline):
    rel = '.github/issue779-checkpoint4/run-accounts.py'
    args_rel = '.github/issue779-checkpoint4/measurement_arguments.py'
    row = capture['files'].get(rel)
    args_row = capture['files'].get(args_rel)
    _need(type(row) is dict and type(row.get('sha256')) is str
          and type(args_row) is dict and type(args_row.get('sha256')) is str, 'captured-validator')
    args_path = Path(source_root) / args_rel
    args_raw = completed_c1._read_trusted_source_file(args_path, deadline=deadline, maximum=256 * 1024)
    _need(_sha(args_raw) == args_row['sha256'], 'captured-helper-hash')
    args_name = 'issue779_original_measurement_arguments'
    args_module = types.ModuleType(args_name)
    args_module.__file__ = str(args_path)
    try:
        exec(compile(args_raw, args_module.__file__, 'exec', dont_inherit=True), args_module.__dict__)
    except Exception:
        raise ValueError('completed-accounts-captured-helper-load') from None
    missing_args = object()
    old_args = sys.modules.get('measurement_arguments', missing_args)
    sys.modules['measurement_arguments'] = args_module
    path = Path(source_root) / rel
    try:
        raw = completed_c1._read_trusted_source_file(path, deadline=deadline, maximum=2 * 1024 * 1024)
        _need(_sha(raw) == row['sha256'], 'captured-validator-hash')
        module = types.ModuleType('issue779_original_accounts_validator')
        module.__file__ = str(path)
        exec(compile(raw, str(path), 'exec', dont_inherit=True), module.__dict__)
    except Exception:
        raise ValueError('completed-accounts-validator-load') from None
    finally:
        if old_args is missing_args:
            sys.modules.pop('measurement_arguments', None)
        else:
            sys.modules['measurement_arguments'] = old_args
    _need(callable(getattr(module, 'validate_receipt', None)), 'validator-missing')
    return module, _sha(raw), _sha(args_raw)


def _archive_members(stage, inventory, stage_directory, artifact_name, members, deadline):
    binding = next((item for item in stage.artifacts if item.name == artifact_name), None)
    _need(binding is not None and binding.staged_filename == artifact_name + '.zip', 'prior-artifact-binding')
    raw = completed_validation._read_stage_file(Path(stage_directory), binding, deadline)
    rows, expanded = completed_validation._zip_directory(raw)
    projection = next((item for item in inventory.archives if item.artifact_name == artifact_name), None)
    actual_members = tuple(completed_validation.MemberProjection(row[0], row[3], row[4], row[6])
                           for row in rows)
    _need(projection is not None and projection.expanded_bytes == expanded
          and projection.members == actual_members, 'prior-archive-projection')
    files = tuple(row for row in rows if not row[0].endswith('/'))
    total = sum(row[4] for row in files)
    if artifact_name.startswith('issue779-checkpoint3-application-'):
        _need(len(files) <= completed_c3.MAX_APPLICATION_FILES
              and total <= completed_c3.MAX_APPLICATION_BYTES
              and all(row[4] <= (completed_c3.MAX_COBERTURA_XML
                                  if Path(row[0]).name == 'native-supervision.cobertura.xml'
                                  else completed_c3.MAX_SELECTED_JSON
                                  if Path(row[0]).name in ('native-supervision.json', 'native-sdk.json')
                                  else 8 * 1024 * 1024) for row in files),
              'prior-retention-bounds')
        _need(expanded <= completed_c3.MAX_APPLICATION_BYTES, 'prior-expanded-bounds')
    else:
        _need(len(files) <= 128 and total <= 96 * 1024 * 1024
              and all(row[4] <= (32 if row[0] == 'native-accounts.json' else 8) * 1024 * 1024
                      for row in files), 'prior-retention-bounds')
        _need(expanded <= 96 * 1024 * 1024, 'prior-expanded-bounds')
    return completed_validation._member_bodies(raw, rows, tuple(members), deadline)


def _invoke_original_prior(module, *, variant, artifact_name, run_id, run_attempt, head_sha,
                           original, source_capture, file_map, deadline):
    prior_kind = 'c3' if variant == 'C4Accounts' else 'accounts'
    native_name = 'native/native-receipt.json' if prior_kind == 'c3' else 'native-accounts-receipt.json'
    evidence = Path('/issue779-account-replay/current')
    merge_source = Path('/issue779-account-replay/prior/source.json')
    merge_report = Path('/issue779-account-replay/prior/report.json')
    merge_binding = Path('/issue779-account-replay/prior/measurement-binding.json')
    merge_native = Path('/issue779-account-replay/prior/' + native_name)
    merge_root = Path('/issue779-account-replay/prior/root-files.trx') if prior_kind == 'c3' else None
    reads = {evidence / 'source.json': None,
             merge_source: file_map['source'], merge_report: file_map['report'],
             merge_binding: file_map['binding'], merge_native: file_map['native']}
    capture = completed_run._json(source_capture, MAX_SOURCE_BYTES, 'account-prior-source')
    source_root = Path(module.__file__).resolve().parents[2]
    witness_paths = ((module.WITNESS_SOURCE,
                      'Evidence/ForgeTrust.AppSurface.Evidence.Supervision/LinuxRunAccounts.cs')
                     if prior_kind == 'accounts' else ())
    for relative in witness_paths:
        row = capture['files'].get(relative)
        _need(type(row) is dict and type(row.get('sha256')) is str, 'prior-witness-source-row')
        body = completed_c1._read_trusted_source_file(source_root / relative, deadline=deadline,
                                                      maximum=2 * 1024 * 1024)
        _need(_sha(body) == row['sha256'], 'prior-witness-source-hash')
        reads[source_root / relative] = body
    if merge_root is not None:
        reads[merge_root] = file_map['root']
    args = SimpleNamespace(variant=variant, prior_kind=prior_kind, prior_artifact_name=artifact_name,
                           source=head_sha, evidence=evidence, merge_source=merge_source,
                           merge_report=merge_report, merge_binding=merge_binding, merge_native=merge_native,
                           merge_root_trx=merge_root)
    old_reader = module.read_file
    try:
        def read_bound(path, maximum):
            path = Path(path)
            value = reads.get(path)
            if path == evidence / 'source.json':
                value = source_capture
            _need(type(value) is bytes and 0 < len(value) <= maximum, 'prior-read-bound')
            return value
        module.read_file = read_bound
        result = module.prior_binding(args)
    finally:
        module.read_file = old_reader
    keys = ('prior_kind', 'prior_variant', 'prior_artifact_name', 'prior_coverage_sha256',
            'prior_input_binding_sha256', 'prior_source_capture_sha256', 'prior_native_receipt_sha256',
            'prior_root_trx_sha256')
    _need({key: original.get(key) for key in keys} == result, 'prior-binding-consensus')
    return result


def _validate_original_prior(module, stage, inventory, stage_directory, variant, original, source_capture, deadline):
    prior_kind = 'c3' if variant == 'C4Accounts' else 'accounts'
    artifact_name = (f'issue779-checkpoint3-application-{stage.run_id}-{stage.run_attempt}'
                     if prior_kind == 'c3' else
                     f'issue779-c4-accounts-C4Accounts-{stage.run_id}-{stage.run_attempt}')
    if prior_kind == 'c3':
        member_map = _archive_members(stage, inventory, stage_directory, artifact_name,
            ('source.json', 'native-supervision.json', 'measurement-binding.json',
             'native/native-receipt.json', 'root-file-tests/root-files.trx'), deadline)
        file_map = {'source': member_map['source.json'], 'report': member_map['native-supervision.json'],
                    'binding': member_map['measurement-binding.json'],
                    'native': member_map['native/native-receipt.json'],
                    'root': member_map['root-file-tests/root-files.trx']}
    else:
        member_map = _archive_members(stage, inventory, stage_directory, artifact_name,
            ('source.json', 'native-accounts.json', 'measurement-binding.json',
             'native-accounts-receipt.json'), deadline)
        file_map = {'source': member_map['source.json'], 'report': member_map['native-accounts.json'],
                    'binding': member_map['measurement-binding.json'],
                    'native': member_map['native-accounts-receipt.json']}
    return _invoke_original_prior(module, variant=variant, artifact_name=artifact_name,
        run_id=stage.run_id, run_attempt=stage.run_attempt, head_sha=stage.head_sha,
        original=original, source_capture=source_capture, file_map=file_map, deadline=deadline)


@dataclass(frozen=True)
class AccountReplay:
    """Evidence projection from original pure receipt callbacks; never an admission token."""
    artifact_name: str
    variant: str
    source_sha256: str
    archive_sha256: str
    receipt_sha256: str
    instrumentation_sha256: str
    witness_sha256: str
    validator_sha256: str
    helper_sha256: str
    native_acceptance: bool = False
    qualification_complete: bool = False


@dataclass(frozen=True)
class CompletedAccounts:
    """Two fixed account projections, explicitly non-authorizing."""
    run_id: int
    run_attempt: int
    head_sha: str
    accounts: tuple[AccountReplay, AccountReplay]
    qualification_complete: bool = False
    admission_created: bool = False


def replay_completed_accounts(stage, inventory, stage_directory, source_root, source_capture, *, deadline):
    """Read the two fixed account ZIPs and invoke only the captured pure data validator.

    `stage` must be the completed-run artifact inventory. The archive members are
    inspected in memory through the existing bounded ZIP parser; no archive path,
    member name, validator callback, or expected variant comes from uploaded data.
    """
    _need(isinstance(stage, completed_run.CompletedRunStage), 'stage-type')
    _need(isinstance(inventory, completed_validation.ParsedCompletedRunInventory), 'inventory-type')
    completed_c1._assert_projection(stage, inventory)
    projection_by_name = {item.artifact_name: item for item in inventory.archives}
    _remaining(deadline)
    projection_by_name = {item.artifact_name: item for item in inventory.archives}
    stage_directory = Path(stage_directory)
    _need(stage_directory.is_absolute() and stage_directory.resolve(strict=True) == stage_directory,
          'stage-directory-path')
    stage_info = stage_directory.lstat()
    _need(stat.S_ISDIR(stage_info.st_mode) and not stat.S_ISLNK(stage_info.st_mode)
          and stat.S_IMODE(stage_info.st_mode) == 0o700 and stage_info.st_uid == os.geteuid(),
          'stage-directory-shape')
    expected_leaf_names = {item.staged_filename for item in stage.artifacts}
    _need(set(os.listdir(stage_directory)) == expected_leaf_names, 'stage-directory-inventory')
    _need(type(source_capture) is bytes and 0 < len(source_capture) <= MAX_SOURCE_BYTES
          and _sha(source_capture) == stage.source_capture_sha256, 'source-capture')
    current_binding = completed_c1._load_trusted_current_binding(deadline=deadline)
    capture, _captured_binding = completed_c1._load_capture_and_binding(
        source_capture, source_root, current_binding=current_binding, deadline=deadline)
    _need(capture.get('source') == stage.head_sha and capture.get('tree') == stage.source_tree,
          'source-identity')
    expected_names = {f'{ACCOUNT_ARTIFACT_PREFIX}{variant}-{stage.run_id}-{stage.run_attempt}'
                      for variant in VARIANTS}
    accounts = tuple(item for item in stage.artifacts if item.name.startswith(ACCOUNT_ARTIFACT_PREFIX))
    _need({item.name for item in accounts} == expected_names and len(accounts) == 2
          and all(item.staged_filename == item.name + '.zip' and 0 < item.size <= MAX_ARCHIVE_BYTES
                  for item in accounts)
          and type(stage.total_archive_bytes) is int and stage.total_archive_bytes <= MAX_TOTAL_ACCOUNT_ARCHIVES,
          'account-inventory')
    source_root = Path(source_root).resolve(strict=True)
    original_module, validator_sha, helper_sha = _load_original_validator(source_root, capture, deadline)
    validator = original_module.validate_receipt
    projections = []
    for variant in VARIANTS:
        _remaining(deadline)
        name = f'issue779-c4-accounts-{variant}-{stage.run_id}-{stage.run_attempt}'
        binding = next((item for item in stage.artifacts if item.name == name), None)
        _need(binding is not None and binding.staged_filename == name + '.zip'
              and binding.size <= MAX_ARCHIVE_BYTES, 'artifact-binding')
        raw = completed_validation._read_stage_file(Path(stage_directory), binding, deadline)
        rows, expanded = completed_validation._zip_directory(raw)
        projection = projection_by_name[name]
        actual_members = tuple(completed_validation.MemberProjection(row[0], row[3], row[4], row[6])
                               for row in rows)
        _need(projection.members == actual_members and projection.expanded_bytes == expanded,
              'account-archive-projection')
        files = tuple(row for row in rows if not row[0].endswith('/'))
        declared_total = sum(row[4] for row in files)
        _need(len(files) <= 128 and {row[0] for row in files} <= RETAINED_NAMES
              and declared_total <= 96 * 1024 * 1024 and expanded == declared_total
              and all(row[4] <= (32 if row[0] == 'native-accounts.json' else 8) * 1024 * 1024
                      for row in files), 'retention-bounds')
        bodies = completed_validation._member_bodies(raw, rows, REQUIRED_MEMBERS, deadline)
        _need(set(bodies) == set(REQUIRED_MEMBERS), 'required-members')
        embedded_capture = completed_run._json(bodies['source.json'], MAX_SOURCE_BYTES,
                                               'completed-accounts-embedded-source')
        _need(bodies['source.json'] == source_capture and embedded_capture == capture, 'embedded-source')
        receipt_raw = bodies['native-accounts-receipt.json']
        receipt = _json(receipt_raw, MAX_RECEIPT_BYTES, 'receipt-json')
        original = _json(bodies['account-original.json'], MAX_RECEIPT_BYTES, 'original-binding')
        instrumented = _json(bodies['account-instrumented.json'], MAX_RECEIPT_BYTES, 'instrumented-binding')
        measurement = _json(bodies['measurement-binding.json'], MAX_RECEIPT_BYTES, 'measurement-binding')
        report_raw = bodies['native-accounts.json']
        _need(type(report_raw) is bytes and 0 < len(report_raw) <= MAX_REPORT_BYTES, 'report-size')
        report = _json(report_raw, MAX_REPORT_BYTES, 'report-json')
        owner_stdout = bodies['owner.stdout']
        owner_stderr = bodies['owner.stderr']
        generation = receipt.get('generation') if type(receipt) is dict else None
        inner_receipt = receipt.get('receipt') if type(receipt) is dict else None
        _need(type(generation) is str and type(inner_receipt) is dict
              and inner_receipt.get('generation') == generation, 'generation')
        try:
            _need(receipt.get('schema') == 'issue779-c4-account-driver-v1'
                  and receipt.get('source') == stage.head_sha and receipt.get('variant') == variant
                  and receipt.get('passed') is True and receipt.get('native_acceptance') is False
                  and receipt.get('groups_empty') is True and receipt.get('final_groups_empty') is True
                  and type(receipt.get('wrapper_exit')) is int and receipt['wrapper_exit'] == 0,
                  'native-summary')
            _validate_owner_streams(receipt, inner_receipt, owner_stdout, owner_stderr)
            _need(type(instrumented.get('files')) is dict, 'instrumented-files')
            entry = instrumented['files'].get('ForgeTrust.AppSurface.Cli.dll')
            validator(inner_receipt, variant, generation, entry)
            original_module.validate_instrumentation(original, instrumented, stage.head_sha, variant)
            prior = _validate_original_prior(original_module, stage, inventory, stage_directory, variant,
                                             original, source_capture, deadline)
            _need(type(instrumented) is dict
                  and {key: instrumented.get(key) for key in prior} == prior, 'instrumented-prior-binding')
            _need(type(report) is dict, 'report-shape')
            witness = original_module.require_current_witness(report, capture, variant)
            expected_measurement = {'schema': original_module.LINEAGE_SCHEMA, 'source': stage.head_sha,
                'variant': variant, 'collector': 'coverlet.console/10.0.1',
                'original': original['files'], 'instrumented': instrumented['files'],
                'native_acceptance': False, 'source_capture_sha256': original['source_capture_sha256'],
                'coverage_sha256': _sha(report_raw), 'native_receipt_sha256': _sha(receipt_raw),
                'current_witness': witness, **prior}
            _need(measurement == expected_measurement, 'measurement-binding')
        except Exception as error:
            raise ValueError('completed-accounts-original-validator') from error
        projections.append(AccountReplay(name, variant, stage.source_capture_sha256,
                                         binding.sha256, _sha(receipt_raw),
                                         _sha(bodies['account-original.json'] + bodies['account-instrumented.json']),
                                         _sha(json.dumps(witness, ensure_ascii=False, sort_keys=True,
                                                         separators=(',', ':')).encode()),
                                         validator_sha, helper_sha))
    _need(len(projections) == 2 and tuple(item.variant for item in projections) == VARIANTS,
          'variant-order')
    return CompletedAccounts(stage.run_id, stage.run_attempt, stage.head_sha, tuple(projections))
