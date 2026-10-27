"""Detached data and real small-file controls; no driver, subprocess, owner or native admission."""
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch
import copy
import json
import os
import tempfile
import unittest

import producer_roots as subject


class ProducerRootsDataTests(unittest.TestCase):
    def receipt(self):
        rows = []
        for number, case in enumerate(subject.CASES, 1):
            cleanup = case == 'cleanup-failure'
            row = {'case': case, 'generation': format(number, '032x'), 'passed': True,
                   'native_acceptance': False, 'groups_empty': True, 'final_groups_empty': True,
                   'accounts_absent': not cleanup, 'accounts_quarantined': cleanup,
                   'owner_terminal': {'wrapper_exit': 0 if case == 'normal' else 1}}
            if cleanup:
                row['quarantined'] = True
            if case in ('cancel', 'ignored-cancel'):
                row['cancel_sent'] = True
            if case == 'ignored-cancel':
                row['descendant_term_observed'] = True
            if case == 'quota':
                row['output_observation'] = {'quota_exceeded': True, 'received_bytes': 16777217,
                                             'received_byte_limit': 16777216}
            if case == 'artifact-mutation':
                row['artifact_mutation'] = [
                    {'schema': 'evidence-c2-artifact-mutation-v1', 'phase': 'mutated',
                     'original_sha256': 'a' * 64, 'changed_sha256': 'b' * 64, 'length': 1,
                     'device_major': 0, 'device_minor': 2, 'inode': 3},
                    {'schema': 'evidence-c2-artifact-mutation-v1', 'phase': 'read-rejected', 'returned_bytes': 0}]
            rows.append(row)
        return {'schema': 'issue779-csharp-checkpoint2-native-v1', 'source': 'a' * 40,
                'base': subject.BASE, 'exit': 0, 'cases': rows,
                'native_acceptance': False, 'trusted_eligibility': False}

    def check(self, value):
        return subject.native(value, 'a' * 40, subject.BASE)

    def test_exact_seven_detached_controls_and_expected_quarantine_neighbor(self):
        self.assertEqual(7, len(self.check(self.receipt())))

    def test_cleanup_and_custody_booleans_cannot_upgrade_failed_or_missing_data(self):
        for index in range(7):
            for key in ('passed', 'groups_empty', 'final_groups_empty', 'accounts_absent',
                        'accounts_quarantined', 'native_acceptance'):
                bad = self.receipt(); bad['cases'][index][key] = not bad['cases'][index][key]
                with self.subTest(index=index, key=key), self.assertRaises(ValueError): self.check(bad)

    def test_order_duplicate_generation_head_and_missing_case(self):
        for change in ('order', 'duplicate', 'head', 'missing', 'exit-bool'):
            bad = self.receipt()
            if change == 'order': bad['cases'].reverse()
            if change == 'duplicate': bad['cases'][1]['generation'] = bad['cases'][0]['generation']
            if change == 'head': bad['source'] = 'b' * 40
            if change == 'missing': bad['cases'].pop()
            if change == 'exit-bool': bad['exit'] = False
            with self.subTest(change=change), self.assertRaises(ValueError): self.check(bad)

    def test_physical_control_flags_and_mutation_digest_neighbor(self):
        changes = ((2, 'cancel_sent', False), (4, 'descendant_term_observed', False), (6, 'quarantined', False))
        for index, key, value in changes:
            bad = self.receipt(); bad['cases'][index][key] = value
            with self.subTest(key=key), self.assertRaises(ValueError): self.check(bad)
        bad = self.receipt(); bad['cases'][3]['output_observation']['received_bytes'] = 16777216
        with self.assertRaises(ValueError): self.check(bad)
        bad = self.receipt(); bad['cases'][5]['artifact_mutation'][0]['changed_sha256'] = 'a' * 64
        with self.assertRaises(ValueError): self.check(bad)

    def standard_report(self):
        return {module + '.dll': {} for module in subject.MODULES}

    def test_standard_counter_shape_does_not_accept_boolean_or_negative_hits(self):
        good = self.standard_report()
        good[subject.SUP + '.dll'] = {'/src/a.cs': {'T': {'M': {'Lines': {'1': 1}, 'Branches': [
            {'Line': 1, 'Offset': 0, 'EndOffset': 1, 'Path': 0, 'Ordinal': 0, 'Hits': 0}]}}}}
        subject.report(subject.canonical(good))
        for hits in (True, -1, 1.2):
            bad = copy.deepcopy(good)
            bad[subject.SUP + '.dll']['/src/a.cs']['T']['M']['Lines']['1'] = hits
            with self.subTest(hits=hits), self.assertRaises(ValueError): subject.report(subject.canonical(bad))
        bad = copy.deepcopy(good); bad.pop(subject.MODULES[0] + '.dll')
        with self.assertRaises(ValueError): subject.report(subject.canonical(bad))

    def test_duplicate_json_members_rejected(self):
        for body in (b'{"a":0,"a":1}', b'{"a":0,"A":1}', b'{"x":NaN}'):
            with self.assertRaises(ValueError): subject.decode(body)

    def test_witness_is_exact_current_source_arm_not_prior_positive_method_neighbor(self):
        doc = subject.QUOTA_DOC
        body = (subject.ROOT / doc).read_bytes()
        capture = {'files': {doc: {'sha256': subject.digest(body)}}}
        line = next(n for n, text in enumerate(body.decode().splitlines(), 1)
                    if text.strip() == 'Fail(SupervisionRunOutputFailure.QuotaExceeded);')
        typ = subject.SUP + '.SupervisionRunOutputBudget'
        method = 'System.Boolean ' + typ + '::Charge(System.Int32)'
        value = self.standard_report()
        value[subject.SUP + '.dll'] = {'/src/' + doc: {typ: {method: {'Lines': {str(line): 1, str(line + 2): 255}, 'Branches': []}}}}
        self.assertEqual(line, subject.witness(value, capture, False)['start'])
        value[subject.SUP + '.dll']['/src/' + doc][typ][method]['Lines'][str(line)] = 0
        with self.assertRaises(ValueError): subject.witness(value, capture, False)
        capture['files'][doc]['sha256'] = '0' * 64
        with self.assertRaises(ValueError): subject.witness(value, capture, False)

    def test_c2_witness_requires_exact_private_method_not_other_positive_method(self):
        doc = subject.MUTATION_DOC; body = (subject.ROOT / doc).read_bytes()
        line = next(n for n, text in enumerate(body.decode().splitlines(), 1)
                    if text.strip() == 'internal byte[] MutateRetainedReportForC2(CancellationToken token)')
        typ = subject.SUP + '.LinuxRetainedReportSet'
        method = 'System.Byte[] ' + typ + '::MutateRetainedReportForC2(System.Threading.CancellationToken)'
        value = self.standard_report()
        value[subject.SUP + '.dll'] = {'/src/' + doc: {typ: {method: {'Lines': {str(line + 3): 1}, 'Branches': []}}}}
        capture = {'files': {doc: {'sha256': subject.digest(body)}}}
        subject.witness(value, capture, True)
        value[subject.SUP + '.dll']['/src/' + doc][typ]['Other'] = value[subject.SUP + '.dll']['/src/' + doc][typ].pop(method)
        with self.assertRaises(ValueError): subject.witness(value, capture, True)

    def test_nested_arguments_have_one_merge_only_and_fixed_driver_stages(self):
        args = SimpleNamespace(tool=Path('/tmp/none image'), mutation_tool=Path('/tmp/c2 image'),
            runtime=Path('/tmp/runtime'), subject=Path('/tmp/subject'), evidence=Path('/tmp/evidence'),
            source='a' * 40, base=subject.BASE)
        inner, outer = subject.collector(args, True), subject.collector(args, False)
        self.assertNotIn('--merge-with', inner)
        self.assertEqual(1, outer.count('--merge-with'))
        self.assertEqual(str(args.evidence / subject.FILES[4]), outer[outer.index('--merge-with') + 1])
        self.assertIn('--stage driver', inner[inner.index('--targetargs') + 1])
        self.assertIn('--stage inner', outer[outer.index('--targetargs') + 1])
        self.assertEqual(7, sum(item.startswith('[ForgeTrust.') for item in inner))
        self.assertIn('[ForgeTrust.AppSurface.Evidence.Cli]*', inner)
        self.assertIn('[ForgeTrust.AppSurface.Evidence.Cli]*', outer)

    def test_instrumentation_requires_all_seven_dlls_and_keeps_all_fourteen_hashes(self):
        args = SimpleNamespace(tool=Path('/none'), mutation_tool=Path('/c2'))
        original = {'original': {v: {n: 'a' * 64 for n in subject.NAMES} for v in ('None', 'C2')}}
        changed = {n: ('b' * 64 if n.endswith('.dll') else 'a' * 64) for n in subject.NAMES}
        with patch.object(subject, 'image', return_value=changed):
            self.assertEqual(14, len(subject.instrumented(args, original)['None']))
        changed[subject.NAMES[0]] = 'a' * 64
        with patch.object(subject, 'image', return_value=changed), self.assertRaises(ValueError):
            subject.instrumented(args, original)

    def test_shared_cli_is_required_in_each_closed_image_and_standard_report(self):
        original = {'original': {v: {n: 'a' * 64 for n in subject.NAMES} for v in ('None', 'C2')}}
        maps = {v: {n: ('b' if n.endswith('.dll') else 'a') * 64 for n in subject.NAMES}
                for v in ('None', 'C2')}
        subject.validate_instrumented(maps, original)
        for variant in ('None', 'C2'):
            for mutation in ('missing-dll', 'missing-pdb', 'alias', 'unchanged'):
                changed = copy.deepcopy(maps)
                name = 'ForgeTrust.AppSurface.Evidence.Cli.' + ('pdb' if mutation == 'missing-pdb' else 'dll')
                if mutation.startswith('missing'):
                    del changed[variant][name]
                elif mutation == 'alias':
                    changed[variant]['other.dll'] = changed[variant].pop(name)
                else:
                    changed[variant][name] = original['original'][variant][name]
                with self.subTest(variant=variant, mutation=mutation), self.assertRaises(ValueError):
                    subject.validate_instrumented(changed, original)
        for mutation in ('missing', 'duplicate-basename', 'alias'):
            report = self.standard_report()
            del report['ForgeTrust.AppSurface.Evidence.Cli.dll']
            if mutation == 'duplicate-basename':
                report['/other/ForgeTrust.AppSurface.Cli.dll'] = {}
            elif mutation == 'alias':
                report['ForgeTrust.AppSurface.Evidence.Cli'] = {}
            with self.subTest(report=mutation), self.assertRaisesRegex(ValueError, 'report-modules'):
                subject.report(subject.canonical(report))


class ProducerRootsSmallFileTests(unittest.TestCase):
    def test_real_regular_empty_neighbor_and_bound(self):
        with tempfile.TemporaryDirectory() as root:
            path = Path(root) / 'leaf'; path.write_bytes(b'abc')
            self.assertEqual(b'abc', subject.read(path, 3))
            with self.assertRaises(ValueError): subject.read(path, 2)
            path.write_bytes(b''); self.assertEqual(b'', subject.read(path))

    def test_real_symlink_hardlink_fifo_rejected_without_blocking(self):
        with tempfile.TemporaryDirectory() as root:
            root = Path(root); target = root / 'target'; target.write_bytes(b'abc')
            link = root / 'link'; link.symlink_to(target)
            with self.assertRaises(OSError): subject.read(link)
            link.unlink(); os.link(target, link)
            with self.assertRaises(ValueError): subject.read(link)
            link.unlink(); os.mkfifo(link)
            with self.assertRaises(ValueError): subject.read(link)

    def test_exclusive_receipt_publication_does_not_replace(self):
        with tempfile.TemporaryDirectory() as root:
            path = Path(root) / 'leaf'; subject.write(path, {'observed': True})
            self.assertEqual({'observed': True}, json.loads(subject.read(path)))
            with self.assertRaises(FileExistsError): subject.write(path, {'observed': False})
            self.assertEqual(0o600, path.stat().st_mode & 0o777)

    def test_existing_retention_limits_include_future_binding_and_reject_links(self):
        with tempfile.TemporaryDirectory() as root:
            root = Path(root); path = root / 'leaf'; path.write_bytes(b'a')
            subject.retention(root, 1)
            with patch.object(subject, 'TOTAL', 1), self.assertRaises(ValueError): subject.retention(root, 1)
            (root / 'link').symlink_to(path)
            with self.assertRaises(ValueError): subject.retention(root)

    def test_full_detached_finalize_joins_bytes_maps_receipts_and_witnesses(self):
        # A synthetic metadata packet exercises the reader only; it is never a native proof.
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            evidence = root / 'evidence'; evidence.mkdir()
            args = SimpleNamespace(tool=root / 'none', mutation_tool=root / 'c2', runtime=root / 'runtime',
                subject=root / 'subject', evidence=evidence, source='a' * 40, base=subject.BASE, collector_exit=0)
            for path in (args.tool, args.mutation_tool):
                path.mkdir()
                for name in subject.NAMES: (path / name).write_bytes((str(path) + name).encode())
            console = subject.console(args); console.parent.mkdir(parents=True); console.write_bytes(b'console')
            capture = {'files': {doc: {'sha256': subject.digest((subject.ROOT / doc).read_bytes())}
                       for doc in (subject.MUTATION_DOC, subject.QUOTA_DOC)}}
            context = {'source': args.source, 'source_capture_sha256': 'b' * 64}
            with patch.object(subject, 'context', return_value=context), patch.object(subject, 'source', return_value=(b'capture', capture)):
                subject.bind(args)
                original = subject.decode(subject.read(evidence / subject.FILES[0]))
                for path in (args.tool, args.mutation_tool):
                    for name in subject.NAMES:
                        if name.endswith('.dll'): (path / name).write_bytes(b'instrumented-' + name.encode())
                armed = {'original_sha256': subject.digest(subject.read(evidence / subject.FILES[0])),
                         'instrumented': subject.instrumented(args, original)}
                subject.write(evidence / subject.FILES[1], armed)
                subject.write(evidence / subject.FILES[2], {'inner_collector_exit': 0, 'original_sha256': armed['original_sha256']})
                subject.write(evidence / subject.FILES[3], {'driver_exit': 0,
                    'instrumented_sha256': subject.digest(subject.read(evidence / subject.FILES[1]))})
                native_dir = evidence / 'native'; native_dir.mkdir()
                inventory = {key: {name: {'sha256': armed['instrumented'][variant][name]} for name in subject.NAMES}
                             for key, variant in (('tool', 'None'), ('mutation_tool', 'C2'))}
                subject.write(native_dir / 'input-inventory.json', inventory)
                packet = ProducerRootsDataTests().receipt()
                for row in packet['cases']:
                    row['input_inventory_sha256'] = subject.digest(subject.read(native_dir / 'input-inventory.json'))
                    for channel in ('stdout', 'stderr'):
                        body = (row['case'] + channel).encode()
                        (native_dir / (row['case'] + '-owner.' + channel)).write_bytes(body)
                        row[channel + '_sha256'] = subject.digest(body)
                    subject.write(native_dir / (row['case'] + '.json'), row)
                subject.write(native_dir / 'native-receipt.json', packet)
                report = {module + '.dll': {} for module in subject.MODULES}
                docs = report[subject.SUP + '.dll']
                for doc, typ, method, marker in (
                    (subject.MUTATION_DOC, subject.SUP + '.LinuxRetainedReportSet',
                     'System.Byte[] ' + subject.SUP + '.LinuxRetainedReportSet::MutateRetainedReportForC2(System.Threading.CancellationToken)',
                     'internal byte[] MutateRetainedReportForC2(CancellationToken token)'),
                    (subject.QUOTA_DOC, subject.SUP + '.SupervisionRunOutputBudget',
                     'System.Boolean ' + subject.SUP + '.SupervisionRunOutputBudget::Charge(System.Int32)',
                     'Fail(SupervisionRunOutputFailure.QuotaExceeded);')):
                    line = next(n for n, text in enumerate((subject.ROOT / doc).read_text().splitlines(), 1) if text.strip() == marker)
                    docs['/src/' + doc] = {typ: {method: {'Lines': {str(line): 1}, 'Branches': []}}}
                subject.write(evidence / subject.FILES[4], report); subject.write(evidence / subject.FILES[5], report)
                for path in (args.tool, args.mutation_tool):
                    for name in subject.NAMES: (path / name).write_bytes((str(path) + name).encode())
                # A nonempty original/restored mismatch cannot publish a final binding.
                corrupted = args.tool / subject.NAMES[0]; actual = corrupted.read_bytes(); corrupted.write_bytes(b'changed')
                with self.assertRaises(ValueError): subject.finalize(args)
                self.assertFalse((evidence / subject.FILES[6]).exists())
                corrupted.write_bytes(actual)
                subject.finalize(args)
                result = subject.decode(subject.read(evidence / subject.FILES[6]))
                self.assertEqual(original['original'], result['restored'])
                self.assertIs(False, result['ready_for_gate'])
                self.assertIs(False, result['worker_imported'])


class ProducerRootsPredecessorTests(unittest.TestCase):
    """Metadata packets and real small files only; no native execution or authority."""

    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        evidence = self.root / 'evidence'; evidence.mkdir()
        incoming = self.root / 'incoming'; incoming.mkdir()
        self.args = SimpleNamespace(tool=self.root / 'none', mutation_tool=self.root / 'c2',
            runtime=self.root / 'runtime', subject=self.root / 'subject', evidence=evidence,
            source='a' * 40, base=subject.BASE, collector_exit=0,
            prior_report=incoming / 'native-accounts.json', prior_binding=incoming / 'measurement-binding.json',
            prior_source=incoming / 'source.json', prior_native=incoming / 'native-accounts-receipt.json',
            prior_artifact_name='issue779-c4-accounts-C4AccountRollback-39200000000-1')
        self.helpers = subject.account_data()
        names = (subject.ACCOUNT_DRIVER, self.helpers.WITNESS_SOURCE, subject.MUTATION_DOC,
                 subject.QUOTA_DOC, 'Evidence/' + subject.SUP + '/LinuxRunAccounts.cs')
        self.capture = {'schema': 'issue779-checkpoint2-source-v1', 'source': self.args.source,
            'count': len(names), 'files': {name: {'sha256': subject.digest((subject.ROOT / name).read_bytes()),
                'mode': format((subject.ROOT / name).stat().st_mode & 0o777, '04o')} for name in names}}
        self.source_body = subject.canonical(self.capture)
        self.args.prior_source.write_bytes(self.source_body)
        self.modules = ProducerRootsDataTests().standard_report()
        declaring, signature, _ = self.helpers.CURRENT_WITNESSES['C4AccountRollback']
        self.witness_line = next(n for n, line in enumerate(
            (subject.ROOT / self.helpers.WITNESS_SOURCE).read_text().splitlines(), 1) if line.strip() == signature)
        self.modules[subject.SUP + '.dll']['/src/' + self.helpers.WITNESS_SOURCE] = {
            declaring: {'System.Void ' + declaring + '::MoveNext()': {
                'Lines': {str(self.witness_line): 1}, 'Branches': []}}}
        self.native = {'schema': 'issue779-c4-account-driver-v1', 'source': self.args.source,
            'variant': 'C4AccountRollback', 'generation': 'c' * 32, 'passed': True,
            'groups_empty': True, 'final_groups_empty': True, 'native_acceptance': False, 'wrapper_exit': 0,
            'receipt': {'schema': 'issue779-c4-accounts-v1', 'variant': 'C4AccountRollback',
                'generation': 'c' * 32, 'managed_entry_sha256': 'b' * 64,
                'native_acceptance': False, 'consumer_started': False, 'creation_succeeded': False,
                'shared_close_task': None, 'closed_holder_rejected': None,
                'cleanup_identity_guards_checked': None,
                'names_absent': True, 'numeric_ids_absent': True, 'utilities': 7, 'settled_utilities': 7,
                'failed_utilities': 1, 'close_completed': True, 'quarantined': False,
                'reservation_failed': True, 'identities_published': False, 'original_handles_closed': True,
                'ids': {'worker_uid': 101, 'subject_uid': 102, 'worker_gid': 101, 'subject_gid': 102, 'results_gid': 103},
                'operation_failure': {'account_failure': 'OperationFailed', 'preparation_stage': 'UtilityExecute',
                    'utility_stage': 'TerminalCheck', 'operation': 'CreateResultsGroup', 'error_kind': 'Admission',
                    'diagnostic_code': 'ASEVD410', 'exec_main_code': 1, 'exec_main_status': 9, 'dbus_category': None}}}
        self.binding = {'schema': 'issue779-c4-account-measurement-v2', 'source': self.args.source,
            'variant': 'C4AccountRollback', 'collector': 'coverlet.console/10.0.1', 'native_acceptance': False,
            'original': {name: 'a' * 64 for name in subject.NAMES},
            'instrumented': {name: 'b' * 64 if name.endswith('.dll') else 'a' * 64 for name in subject.NAMES},
            'source_capture_sha256': subject.digest(self.source_body),
            'current_witness': self.helpers.require_current_witness(self.modules, self.capture, 'C4AccountRollback'),
            'prior_kind': 'accounts', 'prior_variant': 'C4Accounts',
            'prior_artifact_name': 'issue779-c4-accounts-C4Accounts-39200000000-1',
            'prior_coverage_sha256': 'd' * 64, 'prior_input_binding_sha256': 'e' * 64,
            'prior_source_capture_sha256': subject.digest(self.source_body),
            'prior_native_receipt_sha256': 'f' * 64, 'prior_root_trx_sha256': None}
        self.publish_prior()

    def publish_prior(self):
        self.args.prior_report.write_bytes(subject.canonical(self.modules))
        self.args.prior_native.write_bytes(subject.canonical(self.native))
        self.binding['coverage_sha256'] = subject.digest(self.args.prior_report.read_bytes())
        self.binding['native_receipt_sha256'] = subject.digest(self.args.prior_native.read_bytes())
        self.args.prior_binding.write_bytes(subject.canonical(self.binding))

    def check(self):
        return subject.predecessor(self.args, self.source_body, self.capture)

    def test_current_rollback_shape_requires_explicit_null_cleanup_identity_guard(self):
        self.publish_prior()
        self.check()
        for kind in ('missing', 'wrong-positive', 'wrong-negative'):
            with self.subTest(kind=kind):
                original = self.native['receipt'].copy()
                try:
                    if kind == 'missing':
                        self.native['receipt'].pop('cleanup_identity_guards_checked')
                    else:
                        self.native['receipt']['cleanup_identity_guards_checked'] = kind == 'wrong-positive'
                    self.publish_prior()
                    with self.assertRaisesRegex(RuntimeError, '^account-control-receipt-'):
                        self.check()
                finally:
                    self.native['receipt'] = original
        self.publish_prior()
        self.check()

    def reject(self):
        with self.assertRaises((ValueError, RuntimeError, KeyError)):
            self.check()

    def quota(self, hits):
        typ = subject.SUP + '.SupervisionRunOutputBudget'
        method = 'System.Boolean ' + typ + '::Charge(System.Int32)'
        line = next(n for n, text in enumerate((subject.ROOT / subject.QUOTA_DOC).read_text().splitlines(), 1)
                    if text.strip() == 'Fail(SupervisionRunOutputFailure.QuotaExceeded);')
        data = {'Lines': {str(line + 2): 255}, 'Branches': []}
        if hits is not None: data['Lines'][str(line)] = hits
        self.modules[subject.SUP + '.dll']['/src/' + subject.QUOTA_DOC] = {typ: {method: data}}
        return data, line

    def test_all_or_none_options_and_v1_absence(self):
        self.assertTrue(subject.prior_enabled(self.args))
        for name in subject.PRIOR_OPTIONS:
            args = copy.copy(self.args); setattr(args, name, None)
            with self.subTest(name=name), self.assertRaises(ValueError): subject.prior_enabled(args)
        args = copy.copy(self.args)
        for name in subject.PRIOR_OPTIONS: delattr(args, name)
        self.assertFalse(subject.prior_enabled(args))
        self.assertIsNone(subject.predecessor(args, b'', {}))

    def test_fixed_predecessor_object_and_exact_hashes(self):
        value = self.check()
        self.assertEqual({'kind', 'variant', 'artifact_name', 'merge_order', 'source_capture_sha256',
            'input_binding_sha256', 'coverage_sha256', 'native_receipt_sha256'}, set(value))
        self.assertEqual(['C3', 'C4Accounts', 'C4AccountRollback', 'C2', 'None'], value['merge_order'])
        for key, path in (('coverage_sha256', self.args.prior_report), ('input_binding_sha256', self.args.prior_binding),
                          ('native_receipt_sha256', self.args.prior_native), ('source_capture_sha256', self.args.prior_source)):
            self.assertEqual(subject.digest(path.read_bytes()), value[key])

    def test_nested_collectors_each_merge_once_and_forward_all_prior_inputs(self):
        for inner, expected in ((True, self.args.prior_report), (False, self.args.evidence / subject.FILES[4])):
            values = subject.collector(self.args, inner)
            self.assertEqual(1, values.count('--merge-with'))
            self.assertEqual(str(expected), values[values.index('--merge-with') + 1])
            target = values[values.index('--targetargs') + 1]
            for name in subject.PRIOR_OPTIONS: self.assertIn('--' + name.replace('_', '-'), target)

    def test_source_requires_exact_bytes_not_semantic_json_equality(self):
        self.args.prior_source.write_bytes(json.dumps(self.capture, indent=2).encode())
        self.reject()

    def test_each_actual_input_hash_and_current_head_are_bound(self):
        for key in ('coverage_sha256', 'native_receipt_sha256', 'source_capture_sha256', 'source'):
            good = self.binding[key]; self.binding[key] = '0' * len(good)
            self.args.prior_binding.write_bytes(subject.canonical(self.binding))
            with self.subTest(key=key): self.reject()
            self.binding[key] = good
        self.publish_prior(); self.check()

    def test_variant_order_run_attempt_and_ancestor_hashes_are_closed(self):
        for key, bad in (('schema', 'issue779-c4-account-measurement-v1'), ('variant', 'C4Accounts'),
            ('collector', 'coverlet.console/9.0.0'), ('prior_kind', 'c3'), ('prior_variant', 'C3'),
            ('prior_artifact_name', 'issue779-c4-accounts-C4Accounts-39200000000-2'),
            ('prior_coverage_sha256', 'not-a-hash'), ('prior_source_capture_sha256', '0' * 64),
            ('prior_root_trx_sha256', 'd' * 64)):
            good = self.binding[key]; self.binding[key] = bad; self.publish_prior()
            with self.subTest(key=key): self.reject()
            self.binding[key] = good
        self.publish_prior(); self.check()

    def test_receipt_requires_real_rollback_result_and_domain_identity_rules(self):
        # Cross-domain UID == GID is valid; within-domain aliasing is not.
        self.check()
        changes = (('wrapper_exit', False), ('passed', False), ('groups_empty', False), ('final_groups_empty', False),
                   ('native_acceptance', True), ('variant', 'C4Accounts'))
        for key, bad in changes:
            good = self.native[key]; self.native[key] = bad; self.publish_prior()
            with self.subTest(key=key): self.reject()
            self.native[key] = good
        for key, bad in (('numeric_ids_absent', False), ('reservation_failed', False), ('failed_utilities', 0),
                         ('shared_close_task', True), ('original_handles_closed', False)):
            good = self.native['receipt'][key]; self.native['receipt'][key] = bad; self.publish_prior()
            with self.subTest(key=key): self.reject()
            self.native['receipt'][key] = good
        ids = self.native['receipt']['ids']; ids['subject_uid'] = ids['worker_uid']; self.publish_prior(); self.reject()
        ids['subject_uid'] = 102
        self.native['receipt']['operation_failure']['exec_main_code'] = 2; self.publish_prior(); self.reject()

    def test_image_maps_bind_all_fourteen_files_and_each_changed_dll(self):
        name = subject.NAMES[0]
        self.binding['instrumented'][name] = self.binding['original'][name]; self.publish_prior(); self.reject()
        self.binding['instrumented'][name] = 'b' * 64
        del self.binding['original'][subject.NAMES[-1]]; self.publish_prior(); self.reject()

    def test_unique_rollback_witness_must_hit_current_method_even_with_other_positive_lines(self):
        docs = self.modules[subject.SUP + '.dll']['/src/' + self.helpers.WITNESS_SOURCE]
        declaring = self.helpers.CURRENT_WITNESSES['C4AccountRollback'][0]
        details = docs[declaring]['System.Void ' + declaring + '::MoveNext()']
        details['Lines'][str(self.witness_line)] = 0
        docs['PriorPositive'] = {'M': {'Lines': {'1': 100}, 'Branches': []}}
        self.publish_prior(); self.reject()
        details['Lines'][str(self.witness_line)] = 1
        for key in ('source_sha256', 'partial_source_sha256', 'declaring_type', 'method'):
            good = self.binding['current_witness'][key]; self.binding['current_witness'][key] = 'wrong'
            self.publish_prior()
            with self.subTest(key=key): self.reject()
            self.binding['current_witness'][key] = good
        self.publish_prior(); self.check()

    def test_prior_c2_declaration_rejected_even_zero_or_wrong_document(self):
        typ = subject.SUP + '.LinuxRetainedReportSet'
        method = 'System.Byte[] ' + typ + '::MutateRetainedReportForC2(System.Threading.CancellationToken)'
        for hits in (0, 1):
            self.modules[subject.SUP + '.dll']['/other/doc.cs'] = {typ: {method: {'Lines': {'1': hits}, 'Branches': []}}}
            self.publish_prior()
            with self.subTest(hits=hits): self.reject()

    def test_quota_absent_point_zero_and_positive_are_distinct_without_synthetic_zero(self):
        self.check()  # No method at all.
        data, line = self.quota(None); self.publish_prior(); self.check()  # Method, no exact point.
        self.assertNotIn(str(line), data['Lines'])
        data['Lines'][str(line)] = 0; self.publish_prior(); self.check()
        data['Lines'][str(line)] = 1; self.publish_prior(); self.reject()
        data['Lines'].pop(str(line)); data['Lines']['0' + str(line)] = 1
        self.publish_prior(); self.reject()

    def test_external_inputs_must_be_distinct_absolute_and_outside_outgoing_inventory(self):
        for name, bad in (('prior_report', Path('relative.json')),
                          ('prior_report', self.args.evidence / 'incoming.json'),
                          ('prior_report', self.args.prior_native),
                          ('prior_artifact_name', 'issue779-c4-accounts-C4AccountRollback-0-1')):
            args = copy.copy(self.args); setattr(args, name, bad)
            with self.subTest(name=name, bad=bad), self.assertRaises(ValueError): subject.prior_enabled(args)

    def test_actual_incoming_report_link_and_changed_bytes_reject_before_consumption(self):
        self.check()
        link = self.args.prior_report.with_name('linked.json')
        os.link(self.args.prior_report, link)
        self.reject()
        link.unlink()
        self.args.prior_report.write_bytes(self.args.prior_report.read_bytes() + b' ')
        self.reject()

    def prepare_completed(self):
        """Build detached finalization data; never invoke a driver or collector."""
        args = self.args
        for path in (args.tool, args.mutation_tool):
            path.mkdir()
            for name in subject.NAMES: (path / name).write_bytes((str(path) + name).encode())
        console = subject.console(args); console.parent.mkdir(parents=True); console.write_bytes(b'console')
        subject.write(args.evidence / 'package-smoke.json', {'schema': 'issue779-packaged-consumer-v1',
            'source': args.source, 'exit': 0, 'tool_image': str(args.tool)})
        subject.bind(args)
        original = subject.decode(subject.read(args.evidence / subject.FILES[0]))
        for path in (args.tool, args.mutation_tool):
            for name in subject.NAMES:
                if name.endswith('.dll'): (path / name).write_bytes(b'instrumented-' + name.encode())
        armed = {'original_sha256': subject.digest(subject.read(args.evidence / subject.FILES[0])),
                 'instrumented': subject.instrumented(args, original)}
        subject.write(args.evidence / subject.FILES[1], armed)
        subject.write(args.evidence / subject.FILES[2], {'inner_collector_exit': 0, 'original_sha256': armed['original_sha256']})
        subject.write(args.evidence / subject.FILES[3], {'driver_exit': 0,
            'instrumented_sha256': subject.digest(subject.read(args.evidence / subject.FILES[1]))})
        native_dir = args.evidence / 'native'; native_dir.mkdir()
        inventory = {key: {name: {'sha256': armed['instrumented'][variant][name]} for name in subject.NAMES}
            for key, variant in (('tool', 'None'), ('mutation_tool', 'C2'))}
        subject.write(native_dir / 'input-inventory.json', inventory)
        packet = ProducerRootsDataTests().receipt()
        for row in packet['cases']:
            row['input_inventory_sha256'] = subject.digest(subject.read(native_dir / 'input-inventory.json'))
            for channel in ('stdout', 'stderr'):
                body = (row['case'] + channel).encode(); (native_dir / (row['case'] + '-owner.' + channel)).write_bytes(body)
                row[channel + '_sha256'] = subject.digest(body)
            subject.write(native_dir / (row['case'] + '.json'), row)
        subject.write(native_dir / 'native-receipt.json', packet)
        combined = copy.deepcopy(self.modules)
        for doc, typ, method, marker in (
            (subject.MUTATION_DOC, subject.SUP + '.LinuxRetainedReportSet',
             'System.Byte[] ' + subject.SUP + '.LinuxRetainedReportSet::MutateRetainedReportForC2(System.Threading.CancellationToken)',
             'internal byte[] MutateRetainedReportForC2(CancellationToken token)'),
            (subject.QUOTA_DOC, subject.SUP + '.SupervisionRunOutputBudget',
             'System.Boolean ' + subject.SUP + '.SupervisionRunOutputBudget::Charge(System.Int32)',
             'Fail(SupervisionRunOutputFailure.QuotaExceeded);')):
            line = next(n for n, text in enumerate((subject.ROOT / doc).read_text().splitlines(), 1) if text.strip() == marker)
            combined[subject.SUP + '.dll']['/src/' + doc] = {typ: {method: {'Lines': {str(line): 1}, 'Branches': []}}}
        for name in subject.FILES[4:6]: subject.write(args.evidence / name, combined)
        for path in (args.tool, args.mutation_tool):
            for name in subject.NAMES: (path / name).write_bytes((str(path) + name).encode())
        return original

    def test_self_consistent_changed_prior_cannot_replay_after_bind(self):
        with patch.object(subject, 'source', return_value=(self.source_body, self.capture)):
            self.prepare_completed()
            self.modules[subject.SUP + '.dll']['Prior'] = {'T': {'M': {'Lines': {'1': 1}, 'Branches': []}}}
            self.publish_prior(); self.check()  # The new packet is self-consistent, but not the bound packet.
            with self.assertRaises(ValueError): subject.bound(self.args)
            with self.assertRaises(ValueError): subject.finalize(self.args)
            self.assertFalse((self.args.evidence / subject.FILES[6]).exists())

    def test_completed_v2_requires_current_witness_before_ready_publication(self):
        with patch.object(subject, 'source', return_value=(self.source_body, self.capture)):
            self.prepare_completed()
            path = self.args.evidence / subject.FILES[5]
            valid = path.read_bytes(); value = subject.decode(valid)
            typ = subject.SUP + '.SupervisionRunOutputBudget'
            value[subject.SUP + '.dll']['/src/' + subject.QUOTA_DOC][typ]['System.Boolean ' + typ + '::Charge(System.Int32)']['Lines'] = {}
            path.write_bytes(subject.canonical(value))
            with self.assertRaises(ValueError): subject.finalize(self.args)
            self.assertFalse((self.args.evidence / subject.FILES[6]).exists())
            path.write_bytes(valid); subject.finalize(self.args)
            result = subject.decode(subject.read(self.args.evidence / subject.FILES[6]))
            self.assertEqual('issue779-producer-roots-measurement-v2', result['schema'])
            self.assertIs(True, result['ready_for_gate'])
            self.assertIs(False, result['native_acceptance'])
            self.assertIs(False, result['worker_imported'])
            self.assertEqual(self.check(), result['external_predecessor'])
            self.assertEqual(result['external_predecessor'], result['context']['external_predecessor'])


if __name__ == '__main__':
    unittest.main()
