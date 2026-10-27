"""Unit coverage for the fixed account data replay boundary."""
from __future__ import annotations

import importlib.util
import hashlib
import json
import os
from dataclasses import replace
from pathlib import Path
import tempfile
import sys
import subprocess
from unittest.mock import patch
import time
import unittest
import zipfile

ROOT = Path(__file__).resolve().parents[2]
SOURCE_ROOT = Path(os.environ.get('ISSUE779_ACCOUNT_SOURCE_ROOT', '/nonexistent'))
sys.path.insert(0, str(ROOT / '.github/issue779-checkpoint4'))
sys.path.insert(0, str(ROOT / '.github/issue779-protected-qualification'))
import completed_accounts as subject


def _load_original():
    path = ROOT / '.github/issue779-checkpoint4/run-accounts.py'
    spec = importlib.util.spec_from_file_location('accounts_test_original', path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class CompletedAccountsTests(unittest.TestCase):
    def test_original_pure_validator_accepts_both_receipt_variants(self):
        original = _load_original()
        for rollback, variant in ((False, 'C4Accounts'), (True, 'C4AccountRollback')):
            receipt = {'schema': 'issue779-c4-accounts-v1', 'variant': variant, 'generation': 'a' * 32,
                       'managed_entry_sha256': 'b' * 64, 'native_acceptance': False, 'consumer_started': False,
                       'creation_succeeded': not rollback,
                       'cleanup_identity_guards_checked': None if rollback else True,
                       'shared_close_task': None if rollback else True,
                       'closed_holder_rejected': None if rollback else True,
                       'names_absent': True, 'numeric_ids_absent': True, 'utilities': 7 if rollback else 6,
                       'settled_utilities': 7 if rollback else 6, 'failed_utilities': int(rollback),
                       'close_completed': True, 'quarantined': False, 'reservation_failed': rollback,
                       'identities_published': not rollback, 'original_handles_closed': True,
                       'ids': {'worker_uid': 1, 'subject_uid': 2, 'worker_gid': 3, 'subject_gid': 4, 'results_gid': 5},
                       'operation_failure': ({'account_failure': 'OperationFailed',
                           'preparation_stage': 'UtilityExecute', 'utility_stage': 'TerminalCheck',
                           'operation': 'CreateResultsGroup', 'error_kind': 'Admission',
                           'diagnostic_code': 'ASEVD410', 'exec_main_code': 1, 'exec_main_status': 1,
                           'dbus_category': None} if rollback else None)}
            original.validate_receipt(receipt, variant, 'a' * 32, 'b' * 64)

    def test_original_validator_rejects_false_cleanup_claim(self):
        original = _load_original()
        receipt = {'schema': 'issue779-c4-accounts-v1'}
        with self.assertRaises(RuntimeError):
            original.validate_receipt(receipt, 'C4Accounts', 'a' * 32, 'b' * 64)

    def test_captured_validator_hash_mismatch_restores_present_none_module_cache(self):
        args_path = ROOT / '.github/issue779-checkpoint4/measurement_arguments.py'
        validator_path = ROOT / '.github/issue779-checkpoint4/run-accounts.py'
        capture = {'files': {
            '.github/issue779-checkpoint4/measurement_arguments.py': {
                'sha256': hashlib.sha256(args_path.read_bytes()).hexdigest()},
            '.github/issue779-checkpoint4/run-accounts.py': {'sha256': 'f' * 64}}}
        before = 'measurement_arguments' in sys.modules
        old = sys.modules.get('measurement_arguments')
        sys.modules['measurement_arguments'] = None
        try:
            with self.assertRaisesRegex(ValueError, 'completed-accounts-validator-load'):
                subject._load_original_validator(ROOT, capture, time.monotonic() + 5)
            self.assertIsNone(sys.modules['measurement_arguments'])
        finally:
            if before:
                sys.modules['measurement_arguments'] = old
            else:
                sys.modules.pop('measurement_arguments', None)

    def test_wrapper_rejects_wrong_public_stage_type(self):
        with self.assertRaisesRegex(ValueError, 'completed-accounts-stage-type'):
            subject.replay_completed_accounts(object(), object(), Path('/tmp'), Path('/tmp'), b'{}',
                                              deadline=time.monotonic() + 1)
        stage = subject.completed_run.CompletedRunStage(1, 1, 1, 'a' * 40, 'workflow.yml', (), 'b' * 40,
            'c' * 64, tuple(range(1, 38)), (), 0)
        with self.assertRaisesRegex(ValueError, 'completed-accounts-inventory-type'):
            subject.replay_completed_accounts(stage, object(), Path('/tmp'), Path('/tmp'), b'{}',
                                              deadline=time.monotonic() + 1)

    def test_owner_stream_receipt_binding_rejects_hash_or_body_mismatch(self):
        receipt = {'schema': 'receipt'}
        body = json.dumps(receipt).encode() + b'\n'
        outer = {'owner_stdout_sha256': hashlib.sha256(body).hexdigest()}
        subject._validate_owner_streams(outer, receipt, body, b'')
        for changed_outer, changed_receipt, stdout, stderr in (
                ({'owner_stdout_sha256': '0' * 64}, receipt, body, b''),
                (outer, {'schema': 'other'}, body, b''),
                (outer, receipt, body, b'diagnostic'),
                (outer, receipt, body[:-1], b'')):
            with self.subTest(stderr=stderr), self.assertRaises(ValueError):
                subject._validate_owner_streams(changed_outer, changed_receipt, stdout, stderr)

    @unittest.skipUnless(os.environ.get('ISSUE779_ACCOUNT_REPLAY_FIXTURE') == '1'
                         and SOURCE_ROOT.exists() and os.environ.get('ISSUE779_ACCOUNT_CAPTURE')
                         and Path(os.environ['ISSUE779_ACCOUNT_CAPTURE']).is_file(),
                         'requires explicit local source-capture fixture inputs')
    def test_synthetic_wrapper_replay_uses_captured_original_validators(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = SOURCE_ROOT
            relative = '.github/issue779-checkpoint4/run-accounts.py'
            head = subprocess.check_output(('git', '-C', str(SOURCE_ROOT), 'rev-parse', 'HEAD'), text=True).strip()
            tree = subprocess.check_output(
                ('git', '-C', str(SOURCE_ROOT), 'rev-parse', 'HEAD^{tree}'), text=True).strip()
            capture_path = Path(os.environ['ISSUE779_ACCOUNT_CAPTURE'])
            capture_raw = capture_path.read_bytes()
            capture = json.loads(capture_raw)
            self.assertEqual(head, capture['source'])
            self.assertEqual(tree, capture['tree'])
            capture_hash = hashlib.sha256(capture_raw).hexdigest()
            driver = _load_original()
            args_module = __import__('measurement_arguments')
            source_code = (source / '.github/issue779-checkpoint4/measurement/LinuxAccountMeasurement.cs').read_text()
            stage_dir = root / 'stage'
            stage_dir.mkdir(mode=0o700)
            stage_dir = stage_dir.resolve()
            source = source.resolve()
            bindings = []
            archive_details = {}
            for rollback, variant in ((False, 'C4Accounts'), (True, 'C4AccountRollback')):
                entry = 'b' * 64
                generation = 'a' * 32
                inner = {'schema': 'issue779-c4-accounts-v1', 'variant': variant, 'generation': generation,
                         'managed_entry_sha256': entry, 'native_acceptance': False, 'consumer_started': False,
                         'creation_succeeded': not rollback,
                         'cleanup_identity_guards_checked': None if rollback else True,
                         'shared_close_task': None if rollback else True,
                         'closed_holder_rejected': None if rollback else True,
                         'names_absent': True, 'numeric_ids_absent': True, 'utilities': 7 if rollback else 6,
                         'settled_utilities': 7 if rollback else 6, 'failed_utilities': int(rollback),
                         'close_completed': True, 'quarantined': False, 'reservation_failed': rollback,
                         'identities_published': not rollback, 'original_handles_closed': True,
                         'ids': {'worker_uid': 1, 'subject_uid': 2, 'worker_gid': 3,
                                 'subject_gid': 4, 'results_gid': 5},
                         'operation_failure': ({'account_failure': 'OperationFailed',
                             'preparation_stage': 'UtilityExecute', 'utility_stage': 'TerminalCheck',
                             'operation': 'CreateResultsGroup', 'error_kind': 'Admission',
                             'diagnostic_code': 'ASEVD410', 'exec_main_code': 1, 'exec_main_status': 1,
                             'dbus_category': None} if rollback else None)}
                owner_stdout = json.dumps(inner).encode() + b'\n'
                outer = {'schema': 'issue779-c4-account-driver-v1', 'source': head, 'variant': variant,
                         'generation': generation, 'passed': True, 'native_acceptance': False,
                         'groups_empty': True, 'final_groups_empty': True, 'wrapper_exit': 0,
                         'owner_stdout_sha256': hashlib.sha256(owner_stdout).hexdigest(), 'receipt': inner}
                names = tuple(module + suffix for module in args_module.MODULES for suffix in ('.dll', '.pdb'))
                originals = {name: ('a' if name.endswith('.dll') else 'c') * 64 for name in names}
                instrumented_files = {name: ('b' if name.endswith('.dll') else 'd') * 64 for name in names}
                prior_result = {'prior_kind': 'c3' if not rollback else 'accounts',
                                'prior_variant': 'C3' if not rollback else 'C4Accounts',
                                'prior_artifact_name': ('issue779-checkpoint3-application-7-1' if not rollback
                                                        else 'issue779-c4-accounts-C4Accounts-7-1'),
                                'prior_coverage_sha256': 'a' * 64, 'prior_input_binding_sha256': 'b' * 64,
                                'prior_source_capture_sha256': 'c' * 64, 'prior_native_receipt_sha256': 'd' * 64,
                                'prior_root_trx_sha256': 'e' * 64}
                lineage = prior_result
                original_binding = {'source': head, 'variant': variant, 'collector': 'coverlet.console/10.0.1',
                                    'source_capture_sha256': capture_hash, 'files': originals, **lineage}
                instrumented_binding = {'source': head, 'variant': variant, 'collector': 'coverlet.console/10.0.1',
                                        'source_capture_sha256': capture_hash, 'files': instrumented_files, **lineage}
                declaring, signature, _ending = driver.CURRENT_WITNESSES[variant]
                low = source_code[:source_code.index(signature)].count('\n') + 1
                method = 'System.Void ' + declaring + '::MoveNext()'
                report = {name: {} for name in names}
                report['ForgeTrust.AppSurface.Evidence.Supervision.dll'] = {
                    '.github/issue779-checkpoint4/measurement/LinuxAccountMeasurement.cs': {
                        declaring: {method: {'Lines': {str(low): 1}}}}}
                witness = driver.require_current_witness(report, capture, variant)
                receipt_raw = json.dumps(outer).encode()
                report_raw = json.dumps(report).encode()
                measurement = {'schema': driver.LINEAGE_SCHEMA, 'source': head, 'variant': variant,
                    'collector': 'coverlet.console/10.0.1', 'original': originals,
                    'instrumented': instrumented_files, 'native_acceptance': False,
                    'source_capture_sha256': capture_hash,
                    'coverage_sha256': hashlib.sha256(report_raw).hexdigest(),
                    'native_receipt_sha256': hashlib.sha256(receipt_raw).hexdigest(),
                    'current_witness': witness, **prior_result}
                archive_name = f'issue779-c4-accounts-{variant}-7-1'
                archive_path = stage_dir / (archive_name + '.zip')
                with zipfile.ZipFile(archive_path, 'w', compression=zipfile.ZIP_STORED) as archive:
                    archive.writestr('source.json', capture_raw)
                    archive.writestr('native-accounts-receipt.json', receipt_raw)
                    archive.writestr('account-original.json', json.dumps(original_binding).encode())
                    archive.writestr('account-instrumented.json', json.dumps(instrumented_binding).encode())
                    archive.writestr('native-accounts.json', report_raw)
                    archive.writestr('measurement-binding.json', json.dumps(measurement).encode())
                    archive.writestr('owner.stdout', owner_stdout)
                    archive.writestr('owner.stderr', b'')
                raw = archive_path.read_bytes()
                archive_path.chmod(0o600)
                zip_rows, expanded = subject.completed_validation._zip_directory(raw)
                archive_details[archive_name] = (expanded, tuple(
                    subject.completed_validation.MemberProjection(item[0], item[3], item[4], item[6])
                    for item in zip_rows))
                bindings.append(subject.completed_run.ArtifactBinding(10 + len(bindings), archive_name, len(raw),
                                    hashlib.sha256(raw).hexdigest(), archive_name + '.zip'))
            all_names = subject.completed_run.expected_artifact_names(7, 1)
            binding_by_name = {row.name: row for row in bindings}
            complete_bindings = []
            for index, name in enumerate(all_names, 1):
                complete_bindings.append(binding_by_name.get(name) or subject.completed_run.ArtifactBinding(
                    1000 + index, name, 1, '0' * 64, name + '.zip'))
            stage = subject.completed_run.CompletedRunStage(1, 7, 1, head, 'workflow.yml', (), tree,
                                                           capture_hash, tuple(range(1, 38)),
                                                           tuple(complete_bindings),
                                                           sum(row.size for row in complete_bindings))
            for row in complete_bindings:
                if row.name not in {item.name for item in bindings}:
                    (stage_dir / row.staged_filename).write_bytes(b'x')
                    (stage_dir / row.staged_filename).chmod(0o600)
            archives = tuple(subject.completed_validation.ArchiveProjection(row.artifact_id, row.name,
                row.sha256, row.size, *archive_details.get(row.name, (0, ())),
                capture_hash if 'selector' in row.name else None) for row in complete_bindings)
            aggregate = subject.completed_validation.AggregateProjection('', head, capture_hash, (), (), 0, 0, 0, '')
            partial = subject.completed_validation.PartialReceiptProjection(7, 1, capture_hash, 1, 'a' * 64,
                tuple(range(1, 37)), (), 'b' * 64)
            inventory = subject.completed_validation.ParsedCompletedRunInventory(1, 7, 1, head, tree,
                capture_hash, capture['count'], archives, aggregate, partial)
            with patch.object(subject.completed_c1, '_load_trusted_current_binding', return_value=object()), \
                 patch.object(subject.completed_c1, '_load_capture_and_binding',
                              side_effect=ValueError('capture-invalid')), \
                 patch.object(subject, '_load_original_validator') as loader:
                with self.assertRaisesRegex(ValueError, 'capture-invalid'):
                    subject.replay_completed_accounts(stage, inventory, stage_dir, source, capture_raw,
                                                      deadline=time.monotonic() + 5)
                loader.assert_not_called()
            bad_archives = tuple(replace(row, members=())
                                 if row.artifact_name.startswith('issue779-c4-accounts-') else row
                                 for row in inventory.archives)
            bad_inventory = replace(inventory, archives=bad_archives)
            with self.assertRaisesRegex(ValueError, 'account-archive-projection'):
                subject.replay_completed_accounts(stage, bad_inventory, stage_dir, source, capture_raw,
                                                  deadline=time.monotonic() + 5)
            def prior_fixture(_module, _stage, _inventory, _directory, _variant,
                              original, _capture_raw, _deadline):
                keys = ('prior_kind', 'prior_variant', 'prior_artifact_name', 'prior_coverage_sha256',
                        'prior_input_binding_sha256', 'prior_source_capture_sha256',
                        'prior_native_receipt_sha256', 'prior_root_trx_sha256')
                return {key: original[key] for key in keys}
            with patch.object(subject, '_validate_original_prior', side_effect=prior_fixture):
                result = subject.replay_completed_accounts(stage, inventory, stage_dir, source, capture_raw,
                                                           deadline=time.monotonic() + 5)
            self.assertEqual(('C4Accounts', 'C4AccountRollback'), tuple(row.variant for row in result.accounts))
            self.assertFalse(result.qualification_complete)
            self.assertFalse(result.admission_created)

            # Expire on the post-callback check for the second variant. The first
            # four calls are entry, first iteration start/end, second iteration start.
            expired_deadline = time.monotonic() + 5
            original_remaining = subject._remaining
            calls = {'count': 0}
            def expire_after_final_callback(deadline):
                calls['count'] += 1
                if calls['count'] == 5:
                    raise ValueError('completed-accounts-deadline')
                return original_remaining(deadline)
            with patch.object(subject, '_validate_original_prior', side_effect=prior_fixture), \
                 patch.object(subject, '_remaining', side_effect=expire_after_final_callback):
                with self.assertRaisesRegex(ValueError, '^completed-accounts-deadline$'):
                    subject.replay_completed_accounts(stage, inventory, stage_dir, source, capture_raw,
                                                      deadline=expired_deadline)

    def test_projection_defaults_are_non_authorizing(self):
        projection = subject.AccountReplay('fixed-name', 'C4Accounts', 'a' * 64, 'b' * 64,
                                           'c' * 64, 'd' * 64, 'e' * 64, 'f' * 64, 'g' * 64)
        self.assertFalse(projection.native_acceptance)
        self.assertFalse(projection.qualification_complete)
        with self.assertRaises(AttributeError):
            projection.native_acceptance = True

    def test_completed_result_cannot_return_after_absolute_deadline(self):
        stage = subject.completed_run.CompletedRunStage(1, 17, 2, 'a' * 40, 'workflow.yml', (), 'b' * 40,
            'c' * 64, tuple(range(1, 38)), (), 0)
        rows = tuple(subject.AccountReplay(name, variant, 'c' * 64, 'd' * 64, 'e' * 64,
                                           'f' * 64, 'a' * 64, 'b' * 64, 'c' * 64)
                     for name, variant in zip(('fixed-a', 'fixed-b'), subject.VARIANTS))
        with self.assertRaisesRegex(ValueError, '^completed-accounts-deadline$'):
            subject._finish_completed_accounts(stage, rows, time.monotonic() - 1)
        result = subject._finish_completed_accounts(stage, rows, time.monotonic() + 5)
        self.assertEqual((17, 2, 'a' * 40), (result.run_id, result.run_attempt, result.head_sha))
        self.assertFalse(result.qualification_complete)
        self.assertFalse(result.admission_created)

    def test_receipt_json_rejects_duplicate_keys(self):
        with self.assertRaisesRegex(ValueError, 'completed-accounts-receipt-json'):
            subject._json(b'{"x":1,"x":2}', 1024, 'receipt-json')

    def test_original_prior_binding_callback_rejects_changed_source_and_linked_binding(self):
        driver = _load_original()
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            evidence, prior = root / 'current', root / 'prior'
            evidence.mkdir(); prior.mkdir()
            source = 'c' * 40
            capture = {'schema': 'issue779-checkpoint2-source-v1', 'source': source,
                       'tree': 'd' * 40, 'count': 1, 'files': {'detached.cs':
                       {'mode': '0644', 'sha256': 'a' * 64, 'git_blob': 'b' * 40}}}
            capture_raw = json.dumps(capture, sort_keys=True, separators=(',', ':')).encode()
            report = json.dumps({name + '.dll': {} for name in driver.MODULES}, sort_keys=True,
                                separators=(',', ':')).encode()
            native = {'schema': 'issue779-csharp-checkpoint3-native-v1', 'source': source,
                      'base': driver.BASE, 'exit': 0, 'trusted_eligibility': False}
            for key, cases in (('cases', driver.C3_CASES), ('workspace_guards', driver.GUARD_CASES)):
                native[key] = [{'case': case, 'passed': True, 'groups_empty': True,
                                'final_groups_empty': True, 'native_acceptance': False} for case in cases]
            native_raw = json.dumps(native, sort_keys=True, separators=(',', ':')).encode()
            count = driver.C3_ROOT_FILE_TEST_COUNT
            ns = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'
            trx = (f'<TestRun xmlns="{ns}"><ResultSummary><Counters total="{count}" executed="{count}" '
                   f'passed="{count}" failed="0" error="0" notExecuted="0" warning="0" />'
                   '</ResultSummary><Results>' + '<UnitTestResult outcome="Passed" />' * count
                   + '</Results></TestRun>').encode()
            images = {name: 'a' * 64 for name in driver.NAMES}
            binding = {'schema': 'issue779-native-instrumentation-v1', 'source': source,
                       'collector': 'coverlet.console/10.0.1',
                       'source_capture_sha256': hashlib.sha256(capture_raw).hexdigest(),
                       'coverage_sha256': hashlib.sha256(report).hexdigest(),
                       'native_receipt_sha256': hashlib.sha256(native_raw).hexdigest(),
                       'original': images, 'instrumented': {name: ('b' if name.endswith('.dll') else 'a') * 64
                                                            for name in driver.NAMES},
                       'root_file_tests': {'trx_sha256': hashlib.sha256(trx).hexdigest(), 'executed': count,
                                           'passed': count, 'native_acceptance': False}}
            binding_raw = json.dumps(binding, sort_keys=True, separators=(',', ':')).encode()
            stage_dir = root / 'stage'; stage_dir.mkdir(mode=0o700); stage_dir = stage_dir.resolve()
            name = 'issue779-checkpoint3-application-9-1'
            members = {'source': capture_raw, 'report': report, 'binding': binding_raw,
                       'native': native_raw, 'root': trx}
            expected = driver.prior_binding
            required = {'prior_kind', 'prior_variant', 'prior_artifact_name', 'prior_coverage_sha256',
                        'prior_input_binding_sha256', 'prior_source_capture_sha256', 'prior_native_receipt_sha256',
                        'prior_root_trx_sha256'}
            expected_result = {'prior_kind': 'c3', 'prior_variant': 'C3', 'prior_artifact_name': name,
                'prior_coverage_sha256': hashlib.sha256(report).hexdigest(),
                'prior_input_binding_sha256': hashlib.sha256(binding_raw).hexdigest(),
                'prior_source_capture_sha256': hashlib.sha256(capture_raw).hexdigest(),
                'prior_native_receipt_sha256': hashlib.sha256(native_raw).hexdigest(),
                'prior_root_trx_sha256': hashlib.sha256(trx).hexdigest()}
            original = dict(expected_result)
            original.update({'prior_kind': 'c3', 'prior_variant': 'C3'})
            module = driver
            original_read = module.read_file
            args_source = Path('/synthetic/current/source.json')
            args_prior = {Path('/synthetic/prior/source.json'): capture_raw,
                Path('/synthetic/prior/report.json'): report,
                Path('/synthetic/prior/binding.json'): binding_raw,
                Path('/synthetic/prior/native.json'): native_raw,
                Path('/synthetic/prior/root.trx'): trx,
                args_source: capture_raw}
            module.read_file = lambda path, _limit: args_prior[Path(path)]
            args = __import__('types').SimpleNamespace(variant='C4Accounts', prior_kind='c3', source=source,
                prior_artifact_name=name, evidence=Path('/synthetic/current'),
                merge_source=Path('/synthetic/prior/source.json'), merge_report=Path('/synthetic/prior/report.json'),
                merge_binding=Path('/synthetic/prior/binding.json'), merge_native=Path('/synthetic/prior/native.json'),
                merge_root_trx=Path('/synthetic/prior/root.trx'))
            try:
                result = module.prior_binding(args)
            finally:
                module.read_file = original_read
            self.assertEqual(expected_result, result)
            original_link = args_prior[Path('/synthetic/prior/binding.json')]
            rebound = json.loads(original_link)
            rebound['coverage_sha256'] = 'f' * 64
            args_prior[Path('/synthetic/prior/binding.json')] = json.dumps(
                rebound, sort_keys=True, separators=(',', ':')).encode()
            module.read_file = lambda path, _limit: args_prior[Path(path)]
            try:
                with self.assertRaises(RuntimeError): module.prior_binding(args)
            finally:
                module.read_file = original_read
            args_prior[Path('/synthetic/prior/binding.json')] = original_link
            args_prior[Path('/synthetic/prior/source.json')] += b' '
            module.read_file = lambda path, _limit: args_prior[Path(path)]
            try:
                with self.assertRaises(RuntimeError): module.prior_binding(args)
            finally:
                module.read_file = original_read

    def test_original_rollback_prior_binding_callback_uses_the_successful_creation_predecessor(self):
        spec = importlib.util.spec_from_file_location('accounts_lineage_fixture',
            ROOT / '.github/issue779-checkpoint4/test_account_lineage.py')
        fixture_module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(fixture_module)
        fixture = fixture_module.LineageDataTests()
        fixture.setUp()
        self.addCleanup(fixture.temporary.cleanup)
        driver = _load_original()
        capture = fixture_module.captured_witness_sources(json.loads(fixture.args.merge_source.read_bytes()))
        source_raw = driver.canonical(capture)
        fixture.args.merge_source.write_bytes(source_raw)
        (fixture.evidence / 'source.json').write_bytes(source_raw)
        fixture.binding['source_capture_sha256'] = fixture_module.sha(source_raw)
        fixture.save_binding()
        first = driver.prior_binding(fixture.args)
        creation = fixture_module.old_controls.AccountDriverDataTests().valid()
        creation['managed_entry_sha256'] = fixture.binding['instrumented']['ForgeTrust.AppSurface.Cli.dll']
        native = {'schema': 'issue779-c4-account-driver-v1', 'source': fixture.source,
                  'variant': 'C4Accounts', 'generation': creation['generation'], 'passed': True,
                  'groups_empty': True, 'final_groups_empty': True, 'native_acceptance': False,
                  'wrapper_exit': 0, 'receipt': creation}
        native_raw = driver.canonical(native)
        fixture.args.merge_native.write_bytes(native_raw)
        fixture.binding.update(first)
        fixture.binding.update(schema=driver.LINEAGE_SCHEMA, variant='C4Accounts', native_acceptance=False,
                               native_receipt_sha256=fixture_module.sha(native_raw))
        report_raw = driver.canonical(fixture_module.detached_witness_report('C4Accounts'))
        fixture.args.merge_report.write_bytes(report_raw)
        fixture.binding['coverage_sha256'] = fixture_module.sha(report_raw)
        fixture.binding['current_witness'] = driver.require_current_witness(
            json.loads(report_raw), capture, 'C4Accounts')
        fixture.save_binding()
        rollback_args = __import__('types').SimpleNamespace(variant='C4AccountRollback', prior_kind='accounts',
            source=fixture.source, prior_artifact_name='issue779-c4-accounts-C4Accounts-123-1',
            evidence=fixture.evidence, merge_source=fixture.args.merge_source,
            merge_report=fixture.args.merge_report, merge_binding=fixture.args.merge_binding,
            merge_native=fixture.args.merge_native, merge_root_trx=None)
        rollback_result = driver.prior_binding(rollback_args)
        predecessor = {'prior_kind': 'accounts', 'prior_variant': 'C4Accounts',
                       'prior_artifact_name': rollback_args.prior_artifact_name,
                       'prior_coverage_sha256': fixture_module.sha(report_raw),
                       'prior_input_binding_sha256': fixture_module.sha(fixture.args.merge_binding.read_bytes()),
                       'prior_source_capture_sha256': fixture_module.sha(source_raw),
                       'prior_native_receipt_sha256': fixture_module.sha(native_raw),
                       'prior_root_trx_sha256': None}
        self.assertEqual(predecessor, rollback_result)
        original = dict(predecessor)
        file_map = {'source': source_raw, 'report': report_raw,
                    'binding': fixture.args.merge_binding.read_bytes(), 'native': native_raw}
        replayed = subject._invoke_original_prior(driver, variant='C4AccountRollback',
            artifact_name=rollback_args.prior_artifact_name, run_id=123, run_attempt=1,
            head_sha=fixture.source, original=original, source_capture=source_raw,
            file_map=file_map, deadline=time.monotonic() + 5)
        self.assertEqual(rollback_result, replayed)

    def test_prior_adapter_binds_fixed_paths_and_restores_callback_on_failure(self):
        calls = []
        observed = []
        class FakeOriginal:
            __file__ = str(ROOT / '.github/issue779-checkpoint4/run-accounts.py')
            WITNESS_SOURCE = 'unused.cs'
            read_file = staticmethod(lambda _path, _limit: b'prior')

            def prior_binding(args):
                calls.append(args)
                for path in (args.evidence / 'source.json', args.merge_source, args.merge_report,
                             args.merge_binding, args.merge_native, args.merge_root_trx):
                    observed.append((Path(path), FakeOriginal.read_file(path, 1024)))
                raise RuntimeError('expected failure')

        original = {'prior_kind': 'c3', 'prior_variant': 'C3', 'prior_artifact_name': 'artifact',
                    'prior_coverage_sha256': 'a' * 64, 'prior_input_binding_sha256': 'b' * 64,
                    'prior_source_capture_sha256': 'c' * 64, 'prior_native_receipt_sha256': 'd' * 64,
                    'prior_root_trx_sha256': 'e' * 64}
        file_map = {'source': b'{}', 'report': b'{}', 'binding': b'{}', 'native': b'{}', 'root': b'<x/>'}
        callback = FakeOriginal.read_file
        with patch.object(subject.completed_c1, '_read_trusted_source_file', return_value=b'captured'):
            with self.assertRaisesRegex(RuntimeError, 'expected failure'):
                subject._invoke_original_prior(FakeOriginal, variant='C4Accounts', artifact_name='fixed',
                    run_id=1, run_attempt=1, head_sha='a' * 40, original=original,
                    source_capture=b'{}', file_map=file_map, deadline=time.monotonic() + 5)
        self.assertIs(FakeOriginal.read_file, callback)
        self.assertEqual(1, len(calls))
        self.assertEqual(Path('/issue779-account-replay/prior/report.json'), calls[0].merge_report)
        self.assertEqual([b'{}', b'{}', b'{}', b'{}', b'{}', b'<x/>'],
                         [body for _, body in observed])


if __name__ == '__main__':
    unittest.main()
