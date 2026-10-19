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
        self.assertEqual(6, sum(item.startswith('[ForgeTrust.') for item in inner))

    def test_instrumentation_requires_all_six_dlls_and_keeps_all_twelve_hashes(self):
        args = SimpleNamespace(tool=Path('/none'), mutation_tool=Path('/c2'))
        original = {'original': {v: {n: 'a' * 64 for n in subject.NAMES} for v in ('None', 'C2')}}
        changed = {n: ('b' * 64 if n.endswith('.dll') else 'a' * 64) for n in subject.NAMES}
        with patch.object(subject, 'image', return_value=changed):
            self.assertEqual(12, len(subject.instrumented(args, original)['None']))
        changed[subject.NAMES[0]] = 'a' * 64
        with patch.object(subject, 'image', return_value=changed), self.assertRaises(ValueError):
            subject.instrumented(args, original)


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


if __name__ == '__main__':
    unittest.main()
