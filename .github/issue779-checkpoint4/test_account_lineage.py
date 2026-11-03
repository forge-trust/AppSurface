"""Detached source/report lineage and real-file retention controls.

Temporary parser neighbors are not native execution, admission or counter authority.
"""
from pathlib import Path
from types import SimpleNamespace
import copy
import hashlib
import importlib.util
import json
import os
import sys
import tempfile
import unittest

REPO = Path(__file__).resolve().parents[2]
SCRIPTS = REPO / '.github/issue779-checkpoint4'
sys.path.insert(0, str(SCRIPTS))
def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    value = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(value)
    return value
subject = load('lineage_subject', SCRIPTS / 'run-accounts.py')
retention = load('lineage_retention', SCRIPTS / 'check-account-retention.py')
old_controls = load('existing_account_controls', SCRIPTS / 'test_run_accounts.py')
sha = lambda data: hashlib.sha256(data).hexdigest()

def captured_witness_sources(capture):
    for relative in (subject.WITNESS_SOURCE, 'Evidence/ForgeTrust.AppSurface.Evidence.Supervision/LinuxRunAccounts.cs'):
        body = (REPO / relative).read_bytes()
        capture['files'][relative] = {'sha256': sha(body), 'mode': '0644',
            'git_blob': hashlib.sha1(b'blob ' + str(len(body)).encode() + b'\0' + body).hexdigest()}
    capture['count'] = len(capture['files'])
    return capture

def detached_witness_report(variant):
    declaring, signature, ending = subject.CURRENT_WITNESSES[variant]
    text = (REPO / subject.WITNESS_SOURCE).read_text()
    line = str(text[:text.index(signature)].count('\n') + 2)
    method = 'System.Void ' + declaring + '::MoveNext()'
    report = {name + '.dll': {} for name in subject.MODULES}
    documents = report['ForgeTrust.AppSurface.Evidence.Supervision.dll']
    # Positive predecessor-only rows cannot substitute for this variant's exact witness.
    for path in ('LinuxRunAccounts.cs', 'LinuxOwnerActivation.cs'):
        documents['/captured/Evidence/ForgeTrust.AppSurface.Evidence.Supervision/' + path] = {
            'PriorType': {'PriorMethod': {'Lines': {'10': 100}}}}
    documents['/captured/' + subject.WITNESS_SOURCE] = {declaring: {method: {'Lines': {line: 1}}}}
    if variant == 'C4AccountRollback':
        prior_declaring = subject.CURRENT_WITNESSES['C4Accounts'][0]
        documents['/captured/' + subject.WITNESS_SOURCE][prior_declaring] = {
            'System.Void ' + prior_declaring + '::MoveNext()': {'Lines': {'29': 100}}}
    return report

class LineageDataTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.evidence = self.root / 'current'; self.evidence.mkdir()
        self.prior = self.root / 'prior'; self.prior.mkdir()
        self.source = 'c' * 40
        capture = {'schema': 'issue779-checkpoint2-source-v1', 'source': self.source,
                   'tree': 'd' * 40, 'count': 1, 'files': {'detached.cs': {'mode': '0644', 'sha256': 'a' * 64, 'git_blob': 'b' * 40}}}
        body = subject.canonical(capture)
        (self.evidence / 'source.json').write_bytes(body)
        (self.prior / 'source.json').write_bytes(body)
        report = subject.canonical({name + '.dll': {} for name in subject.MODULES})
        (self.prior / 'coverage.json').write_bytes(report)
        native = {'schema': 'issue779-csharp-checkpoint3-native-v1', 'source': self.source,
                  'base': subject.BASE, 'exit': 0, 'trusted_eligibility': False}
        for key, cases in (('cases', subject.C3_CASES), ('workspace_guards', subject.GUARD_CASES)):
            native[key] = [{'case': case, 'passed': True, 'groups_empty': True,
                            'final_groups_empty': True, 'native_acceptance': False} for case in cases]
        self.native = native
        native_body = subject.canonical(native)
        (self.prior / 'native-receipt.json').write_bytes(native_body)
        ns = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'
        root_count = subject.C3_ROOT_FILE_TEST_COUNT
        trx = (
            f'<TestRun xmlns="{ns}"><ResultSummary><Counters total="{root_count}" '
            f'executed="{root_count}" passed="{root_count}" failed="0" />'
            '</ResultSummary><Results>'
            + '<UnitTestResult outcome="Passed" />' * root_count
            + '</Results></TestRun>'
        ).encode()
        (self.prior / 'root-files.trx').write_bytes(trx)
        original = {name: 'a' * 64 for name in subject.NAMES}
        instrumented = {name: ('b' if name.endswith('.dll') else 'a') * 64 for name in subject.NAMES}
        self.binding = {
            'schema': 'issue779-native-instrumentation-v1', 'source': self.source,
            'collector': 'coverlet.console/10.0.1', 'source_capture_sha256': sha(body),
            'coverage_sha256': sha(report), 'native_receipt_sha256': sha(native_body),
            'original': original, 'instrumented': instrumented,
            'root_file_tests': {
                'trx_sha256': sha(trx), 'executed': root_count, 'passed': root_count,
                'native_acceptance': False,
            },
        }
        self.save_binding()
        self.args = SimpleNamespace(variant='C4Accounts', prior_kind='c3', source=self.source,
                    prior_artifact_name='issue779-checkpoint3-application-123-1', evidence=self.evidence,
                    merge_source=self.prior / 'source.json', merge_report=self.prior / 'coverage.json',
                    merge_binding=self.prior / 'measurement-binding.json', merge_native=self.prior / 'native-receipt.json',
                    merge_root_trx=self.prior / 'root-files.trx')

    def save_binding(self):
        (self.prior / 'measurement-binding.json').write_bytes(subject.canonical(self.binding))

    def test_obsolete_or_other_consistent_root_counts_reject(self):
        for count in (39, 40, 41, 42, subject.C3_ROOT_FILE_TEST_COUNT + 1):
            ns = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'
            body = (f'<TestRun xmlns="{ns}"><ResultSummary><Counters total="{count}" executed="{count}" passed="{count}" failed="0" /></ResultSummary><Results>'
                    + '<UnitTestResult outcome="Passed" />' * count + '</Results></TestRun>').encode()
            self.args.merge_root_trx.write_bytes(body)
            self.binding['root_file_tests'].update(trx_sha256=sha(body), executed=count, passed=count)
            self.save_binding()
            with self.subTest(count=count), self.assertRaisesRegex(RuntimeError, 'account-control-prior-root'):
                subject.prior_binding(self.args)

    def test_valid_c3_neighbor_records_all_exact_input_digests(self):
        value = subject.prior_binding(self.args)
        self.assertEqual('C3', value['prior_variant'])
        self.assertEqual(sha(self.args.merge_report.read_bytes()), value['prior_coverage_sha256'])
        self.assertEqual(sha(self.args.merge_binding.read_bytes()), value['prior_input_binding_sha256'])
        self.assertEqual(sha(self.args.merge_root_trx.read_bytes()), value['prior_root_trx_sha256'])

    def assert_rebound_shared_cli_report_rejects(self):
        raw = self.args.merge_report.read_bytes()
        for mutation in ('missing', 'duplicate-basename', 'alias'):
            report = json.loads(raw)
            del report['ForgeTrust.AppSurface.Evidence.Cli.dll']
            if mutation == 'duplicate-basename':
                report['/other/ForgeTrust.AppSurface.Cli.dll'] = {}
            elif mutation == 'alias':
                report['ForgeTrust.AppSurface.Evidence.Cli'] = {}
            body = subject.canonical(report)
            self.args.merge_report.write_bytes(body)
            self.binding['coverage_sha256'] = sha(body)
            self.save_binding()
            with self.subTest(prior=self.args.prior_kind, shared_cli=mutation):
                with self.assertRaisesRegex(RuntimeError, 'prior-modules'):
                    subject.prior_binding(self.args)
        self.args.merge_report.write_bytes(raw)
        self.binding['coverage_sha256'] = sha(raw)
        self.save_binding()
        subject.prior_binding(self.args)

    def test_rebound_c3_report_cannot_omit_or_alias_the_shared_cli(self):
        self.assert_rebound_shared_cli_report_rejects()

    def test_semantically_equal_but_different_source_bytes_reject(self):
        self.args.merge_source.write_bytes(self.args.merge_source.read_bytes() + b' ')
        with self.assertRaises(RuntimeError): subject.prior_binding(self.args)

    def test_report_binding_and_receipt_mutation_each_reject(self):
        for path in (self.args.merge_report, self.args.merge_native):
            old = path.read_bytes(); path.write_bytes(old + b' ')
            with self.subTest(path=path.name), self.assertRaises(RuntimeError): subject.prior_binding(self.args)
            path.write_bytes(old)
        self.binding['coverage_sha256'] = 'f' * 64; self.save_binding()
        with self.assertRaises(RuntimeError): subject.prior_binding(self.args)

    def test_changed_prior_binding_bytes_cannot_replace_the_captured_lineage(self):
        first = subject.prior_binding(self.args)
        self.args.merge_binding.write_bytes(self.args.merge_binding.read_bytes() + b' ')
        second = subject.prior_binding(self.args)
        original = {'source': self.source, 'variant': 'C4Accounts', 'collector': 'coverlet.console/10.0.1',
                    'source_capture_sha256': 'd' * 64, 'files': {name: 'a' * 64 for name in subject.NAMES}, **first}
        instrumented = copy.deepcopy(original); instrumented.update(second)
        for name in subject.NAMES:
            if name.endswith('.dll'): instrumented['files'][name] = 'b' * 64
        with self.assertRaises(RuntimeError): subject.validate_instrumentation(original, instrumented, self.source, 'C4Accounts')

    def test_order_artifact_schema_and_module_image_mismatch_reject(self):
        for key, bad in (('prior_kind', 'accounts'), ('variant', 'C4AccountRollback'), ('prior_artifact_name', 'other')):
            old = getattr(self.args, key); setattr(self.args, key, bad)
            with self.subTest(key=key), self.assertRaises(RuntimeError): subject.prior_binding(self.args)
            setattr(self.args, key, old)
        for key, bad in (('schema', 'v0'), ('source', 'd' * 40), ('collector', 'other')):
            old = self.binding[key]; self.binding[key] = bad; self.save_binding()
            with self.subTest(key=key), self.assertRaises(RuntimeError): subject.prior_binding(self.args)
            self.binding[key] = old; self.save_binding()
        self.binding['instrumented'] = copy.deepcopy(self.binding['original']); self.save_binding()
        with self.assertRaises(RuntimeError): subject.prior_binding(self.args)

    def test_hash_bound_nonpassing_or_nonempty_native_neighbor_rejects(self):
        for key in ('passed', 'groups_empty', 'final_groups_empty'):
            value = copy.deepcopy(self.native); value['cases'][0][key] = False
            body = subject.canonical(value); self.args.merge_native.write_bytes(body)
            self.binding['native_receipt_sha256'] = sha(body); self.save_binding()
            with self.subTest(key=key), self.assertRaises(RuntimeError): subject.prior_binding(self.args)

    def test_root_receipt_requires_actual_hash_and_all_results_passing(self):
        old = self.args.merge_root_trx.read_bytes()
        for body in (old.replace(b'outcome="Passed"', b'outcome="Failed"', 1), old + b' '):
            self.args.merge_root_trx.write_bytes(body)
            if b'Failed' in body:
                self.binding['root_file_tests']['trx_sha256'] = sha(body); self.save_binding()
            with self.assertRaises(RuntimeError): subject.prior_binding(self.args)

    def test_valid_account_neighbor_then_failed_cleanup_rejects(self):
        capture = captured_witness_sources(json.loads(self.args.merge_source.read_bytes()))
        source_body = subject.canonical(capture)
        self.args.merge_source.write_bytes(source_body)
        (self.evidence / 'source.json').write_bytes(source_body)
        self.binding['source_capture_sha256'] = sha(source_body); self.save_binding()
        first = subject.prior_binding(self.args)
        receipt = old_controls.AccountDriverDataTests().valid()
        receipt['managed_entry_sha256'] = self.binding['instrumented']['ForgeTrust.AppSurface.Cli.dll']
        native = {'schema': 'issue779-c4-account-driver-v1', 'source': self.source, 'variant': 'C4Accounts',
                  'generation': receipt['generation'], 'passed': True, 'groups_empty': True, 'final_groups_empty': True,
                  'native_acceptance': False, 'wrapper_exit': 0, 'receipt': receipt}
        body = subject.canonical(native); self.args.merge_native.write_bytes(body)
        self.binding.update(first)
        self.binding.update(schema=subject.LINEAGE_SCHEMA, variant='C4Accounts', native_acceptance=False, native_receipt_sha256=sha(body))
        report = detached_witness_report('C4Accounts')
        report_body = subject.canonical(report); self.args.merge_report.write_bytes(report_body)
        self.binding['coverage_sha256'] = sha(report_body)
        self.binding['current_witness'] = subject.require_current_witness(report, capture, 'C4Accounts')
        self.save_binding()
        self.args.variant = 'C4AccountRollback'; self.args.prior_kind = 'accounts'
        self.args.prior_artifact_name = 'issue779-c4-accounts-C4Accounts-123-1'; self.args.merge_root_trx = None
        self.assertEqual('C4Accounts', subject.prior_binding(self.args)['prior_variant'])
        self.assert_rebound_shared_cli_report_rejects()
        native['receipt']['numeric_ids_absent'] = False
        body = subject.canonical(native); self.args.merge_native.write_bytes(body)
        self.binding['native_receipt_sha256'] = sha(body); self.save_binding()
        with self.assertRaises(RuntimeError): subject.prior_binding(self.args)

class CurrentWitnessDataTests(unittest.TestCase):
    def capture(self):
        return captured_witness_sources({'files': {}})
    def test_both_exact_observed_async_mappings_have_valid_detached_neighbors(self):
        for variant in subject.VARIANTS:
            value = subject.require_current_witness(detached_witness_report(variant), self.capture(), variant)
            self.assertEqual(subject.WITNESS_SOURCE, value['document'])
            self.assertEqual(subject.CURRENT_WITNESSES[variant][0], value['declaring_type'])
    def test_positive_prior_rows_cannot_shadow_zero_current_unique_witness(self):
        for variant in subject.VARIANTS:
            report = detached_witness_report(variant); capture = self.capture()
            subject.require_current_witness(report, capture, variant)
            declaring = subject.CURRENT_WITNESSES[variant][0]
            lines = report['ForgeTrust.AppSurface.Evidence.Supervision.dll']['/captured/' + subject.WITNESS_SOURCE][declaring]['System.Void ' + declaring + '::MoveNext()']['Lines']
            for line in lines: lines[line] = 0
            with self.subTest(variant=variant), self.assertRaisesRegex(RuntimeError, 'current-witness-hit'):
                subject.require_current_witness(report, capture, variant)
    def test_wrong_document_declaring_type_or_canonical_method_rejects(self):
        for variant in subject.VARIANTS:
            good = detached_witness_report(variant); capture = self.capture()
            subject.require_current_witness(good, capture, variant)
            declaring = subject.CURRENT_WITNESSES[variant][0]
            for mutation in ('document', 'type', 'method'):
                report = copy.deepcopy(good); docs = report['ForgeTrust.AppSurface.Evidence.Supervision.dll']
                path = '/captured/' + subject.WITNESS_SOURCE
                if mutation == 'document': docs['/foreign/LinuxAccountMeasurement.cs'] = docs.pop(path)
                elif mutation == 'type': docs[path][declaring + 'Foreign'] = docs[path].pop(declaring)
                else:
                    methods = docs[path][declaring]
                    methods['System.Int32 ' + declaring + '::MoveNext()'] = methods.pop('System.Void ' + declaring + '::MoveNext()')
                with self.subTest(variant=variant, mutation=mutation), self.assertRaises(RuntimeError):
                    subject.require_current_witness(report, capture, variant)
    def test_both_actual_physical_source_hashes_are_required(self):
        for variant in subject.VARIANTS:
            report = detached_witness_report(variant); good = self.capture()
            subject.require_current_witness(report, good, variant)
            for path in good['files']:
                capture = copy.deepcopy(good); capture['files'][path]['sha256'] = '0' * 64
                with self.subTest(variant=variant, path=path), self.assertRaisesRegex(RuntimeError, 'current-witness-source'):
                    subject.require_current_witness(report, capture, variant)
    def test_positive_lines_outside_the_source_method_span_do_not_count(self):
        for variant in subject.VARIANTS:
            good = detached_witness_report(variant); capture = self.capture()
            subject.require_current_witness(good, capture, variant)
            declaring = subject.CURRENT_WITNESSES[variant][0]
            for line in ('1', '999999'):
                report = copy.deepcopy(good)
                report['ForgeTrust.AppSurface.Evidence.Supervision.dll']['/captured/' + subject.WITNESS_SOURCE][declaring]['System.Void ' + declaring + '::MoveNext()']['Lines'] = {line: 100}
                with self.subTest(variant=variant, line=line), self.assertRaisesRegex(RuntimeError, 'current-witness-hit'):
                    subject.require_current_witness(report, capture, variant)

class RetentionDataTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(); self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
    def sparse(self, name, size):
        path = self.root / name
        with path.open('xb') as file: file.truncate(size)
        return path
    def test_only_exact_report_name_accepts_32_mib(self):
        self.sparse('native-accounts.json', 32 * retention.MIB); retention.check(self.root)
        self.sparse('collector.log', 8 * retention.MIB + 1)
        with self.assertRaises(ValueError): retention.check(self.root)
    def test_report_over_32_mib_and_unknown_directory_reject(self):
        path = self.sparse('native-accounts.json', 32 * retention.MIB + 1)
        with self.assertRaises(ValueError): retention.check(self.root)
        path.unlink(); (self.root / 'unknown').mkdir()
        with self.assertRaises(ValueError): retention.check(self.root)
    def test_hardlink_symlink_and_fifo_reject(self):
        path = self.sparse('owner.stdout', 1)
        other = self.root / 'owner.stderr'; os.link(path, other)
        with self.assertRaises(ValueError): retention.check(self.root)
        other.unlink(); path.unlink(); other.symlink_to('/dev/null')
        with self.assertRaises(OSError): retention.check(self.root)
        other.unlink(); os.mkfifo(other)
        with self.assertRaises(ValueError): retention.check(self.root)
    def test_aggregate_96_mib_remains_enforced(self):
        self.sparse('native-accounts.json', 32 * retention.MIB)
        for name in sorted(retention.NAMES - {'native-accounts.json'})[:9]: self.sparse(name, 8 * retention.MIB)
        with self.assertRaises(ValueError): retention.check(self.root)

if __name__ == '__main__':
    unittest.main()
