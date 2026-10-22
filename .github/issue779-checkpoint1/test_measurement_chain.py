"""Detached chain records and ordinary owned-file controls; never native proof."""
import copy
import base64
import io
import tarfile
import types
import hashlib
import os
from pathlib import Path
import stat
import tempfile
import time
import unittest

import binding
import measurement_chain as subject
from test_binding import HEAD, build_model

IDENTITY = 'github-actions-12345-2'


def seed():
    source_raw = binding.canonical({'source': HEAD})
    files = {'lineage/c2/source.json': source_raw, 'lineage/c2/native-producer-roots.json': b'raw-report'}
    predecessor = {'kind': 'producer-roots', 'artifact_name': subject.artifact('C2', IDENTITY),
        'binding_sha256': '1' * 64, 'report_sha256': subject.sha(b'raw-report'),
        'inner_report_sha256': '2' * 64, 'native_receipt_sha256': '3' * 64}
    return source_raw, files, predecessor


class MeasurementChainDataTests(unittest.TestCase):
    def test_valid_initial_and_all_four_prefixes_never_claim_sixteen(self):
        raw, files, predecessor = seed()
        links = []
        previous = predecessor['report_sha256']
        for index in range(5):
            prefix = subject.ORDER[:index]
            value = subject.record(raw, HEAD, IDENTITY, prefix, predecessor, copy.deepcopy(links), previous, files)
            subject.validate_record(value, raw, HEAD, IDENTITY, prefix, files)
            self.assertEqual(predecessor['report_sha256'], value['predecessor']['report_sha256'])
            self.assertFalse(value['all_sixteen_claimed'])
            self.assertEqual(6 if index == 4 else len(subject.flattened(prefix)), len(value['cases']))
            if index < 4:
                selector = subject.ORDER[index]
                new = subject.sha(selector.encode())
                links.append({'selector': selector, 'artifact_name': subject.artifact(selector, IDENTITY),
                    'measurement_binding_sha256': '4' * 64, 'report_sha256': new,
                    'prior_report_sha256': previous, 'prior_binding_sha256': '5' * 64,
                    'native_result_sha256': '6' * 64})
                previous = new

    def test_exact_fields_source_workflow_order_prefix_and_authority_one_change_rejects(self):
        raw, files, predecessor = seed()
        good = subject.record(raw, HEAD, IDENTITY, (), predecessor, [], predecessor['report_sha256'], files)
        changes = {'schema': 'other', 'source': 'a' * 40, 'source_capture_sha256': 'a' * 64,
            'workflow_identity': 'github-actions-12345-1', 'order': list(reversed(subject.ORDER)),
            'completed': ['N04'], 'cases': ['N01'], 'root_only': False, 'worker_imported': True,
            'native_acceptance': True, 'coverage_credit': True, 'all_sixteen_claimed': True,
            'report_sha256': 'a' * 64, 'unknown': 'private-canary'}
        subject.validate_record(good, raw, HEAD, IDENTITY, (), files)
        for key, value in changes.items():
            bad = copy.deepcopy(good); bad[key] = value
            with self.subTest(key=key), self.assertRaises(binding.Rejected):
                subject.validate_record(bad, raw, HEAD, IDENTITY, (), files)

    def test_raw_projection_and_initial_c2_identity_cannot_be_rebound_by_arbitrary_fields(self):
        raw, files, predecessor = seed()
        good = subject.record(raw, HEAD, IDENTITY, (), predecessor, [], predecessor['report_sha256'], files)
        for fault in ('run', 'missing-field', 'extra-field', 'bool-hash', 'changed-bytes', 'extra-leaf'):
            bad = copy.deepcopy(good); current = dict(files)
            if fault == 'run': bad['predecessor']['artifact_name'] = 'issue779-csharp-checkpoint2-12345-1'
            elif fault == 'missing-field': del bad['predecessor']['binding_sha256']
            elif fault == 'extra-field': bad['predecessor']['other'] = 0
            elif fault == 'bool-hash': bad['predecessor']['binding_sha256'] = True
            elif fault == 'changed-bytes': current['lineage/c2/source.json'] += b' '
            else: current['lineage/c2/extra'] = b'x'
            with self.subTest(fault=fault), self.assertRaises(binding.Rejected):
                subject.validate_record(bad, raw, HEAD, IDENTITY, (), current)

    def test_link_must_select_next_fixed_selector_same_run_and_exact_immediate_report(self):
        raw, files, predecessor = seed()
        link = {'selector': 'None', 'artifact_name': subject.artifact('None', IDENTITY),
            'measurement_binding_sha256': '4' * 64, 'report_sha256': '5' * 64,
            'prior_report_sha256': predecessor['report_sha256'], 'prior_binding_sha256': '6' * 64,
            'native_result_sha256': '7' * 64}
        value = subject.record(raw, HEAD, IDENTITY, ('None',), predecessor, [link], link['report_sha256'], files)
        subject.validate_record(value, raw, HEAD, IDENTITY, ('None',), files)
        for key, bad_value in (('selector', 'N04'), ('artifact_name', subject.artifact('None', 'github-actions-12345-1')),
                               ('prior_report_sha256', '0' * 64), ('native_result_sha256', False), ('unknown', 0)):
            bad = copy.deepcopy(value); bad['links'][0][key] = bad_value
            with self.subTest(key=key), self.assertRaises(binding.Rejected):
                subject.validate_record(bad, raw, HEAD, IDENTITY, ('None',), files)

    def test_workflow_and_path_grammar_reject_closed_neighbors(self):
        self.assertEqual(('12345', '2'), subject.workflow(IDENTITY))
        for text in ('github-actions-0-1', 'github-actions-1-0', IDENTITY + '\n', 'github-actions-1-1x'):
            with self.subTest(text=text), self.assertRaises(binding.Rejected): subject.workflow(text)
        self.assertEqual('lineage/a/Assets/item', subject.rel('lineage/a/Assets/item'))
        for text in ('../x', '/x', 'x//y', 'x/./y', 'x/../y', 'x\\y', 'x\ny', ''):
            with self.subTest(text=text), self.assertRaises(binding.Rejected): subject.rel(text)

    def test_real_private_publication_preserves_bytes_and_existing_output_is_never_adopted(self):
        with tempfile.TemporaryDirectory() as temporary:
            output = Path(temporary).resolve() / 'fresh'
            data = {'source.json': b'{"a":1}\n', 'lineage/c2/raw.json': b'  {"x":2}  '}
            old = os.umask(0o077)
            try: subject.fresh(output, data, time.monotonic() + 5)
            finally: os.umask(old)
            self.assertEqual(data, subject.scan(output, time.monotonic() + 5))
            for path in output.rglob('*'):
                self.assertEqual(0o700 if path.is_dir() else 0o600, stat.S_IMODE(path.lstat().st_mode))
            with self.assertRaises(binding.Rejected): subject.fresh(output, data, time.monotonic() + 5)
            self.assertEqual(data, subject.scan(output, time.monotonic() + 5))

    def test_real_symlink_hardlink_fifo_and_expiry_fail_before_publication(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            outside = root / 'outside'; outside.write_bytes(b'unchanged')
            for fault in ('symlink', 'hardlink', 'fifo'):
                tree = root / fault; tree.mkdir()
                leaf = tree / 'leaf'
                if fault == 'symlink': leaf.symlink_to(outside)
                elif fault == 'hardlink': os.link(outside, leaf)
                else: os.mkfifo(leaf)
                with self.subTest(fault=fault), self.assertRaises(binding.Rejected):
                    subject.scan(tree, time.monotonic() + 5)
                self.assertEqual(b'unchanged', outside.read_bytes())
            output = root / 'expired'
            with self.assertRaises(binding.Rejected): subject.fresh(output, {'x': b'x'}, time.monotonic() - 1)
            self.assertFalse(output.exists())

    def test_small_bound_neighbor_keeps_existing_files_and_no_partial_output(self):
        from unittest.mock import patch
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            with patch.object(subject, 'MAX_TOTAL', 3), patch.object(subject, 'MAX_FILES', 2):
                subject.fresh(root / 'valid', {'a': b'a', 'b': b'bc'}, time.monotonic() + 5)
                for data in ({'a': b'abcd'}, {'a': b'a', 'b': b'b', 'c': b'c'}):
                    with self.subTest(data=data), self.assertRaises(binding.Rejected):
                        subject.fresh(root / 'bad', data, time.monotonic() + 5)
                    self.assertFalse((root / 'bad').exists())

    def test_exact_archive_bounds_keep_original_raw_bytes_above_the_log_cap(self):
        self.assertEqual(33_619_968, subject.leaf_limit('lineage/selectors/None/native/native-private.tar'))
        self.assertEqual(33_619_968, subject.leaf_limit('native/native.tar'))
        for name in ('native-private.tar.log', 'prefix-native.tar', 'native-private.TAR', 'other.tar'):
            self.assertEqual(subject.MAX_LEAF, subject.leaf_limit(name))
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            archive = root / 'native-private.tar'
            with archive.open('wb') as stream:
                stream.truncate(subject.MAX_LEAF + 1)
            actual = subject.scan(root, time.monotonic() + 5)
            self.assertEqual(subject.MAX_LEAF + 1, len(actual['native-private.tar']))
            with archive.open('r+b') as stream:
                stream.truncate(subject.MAX_ARCHIVE + 1)
            with self.assertRaises(binding.Rejected): subject.scan(root, time.monotonic() + 5)

    def test_actual_slot_archive_and_raw_leaf_custody_rejects_post_wrapper_changes(self):
        bridge_path = subject.ROOT / '.github/issue779-checkpoint1/procedures/slot/bridge.py'
        bridge = types.ModuleType('chain_test_actual_slot_bridge')
        bridge.__file__ = str(bridge_path)
        exec(compile(bridge_path.read_bytes(), str(bridge_path), 'exec'), bridge.__dict__)
        leaves = {'payload': b'actual retained bytes', 'retention-selection.json': b'{}'}
        stream = io.BytesIO()
        with tarfile.open(fileobj=stream, mode='w', format=tarfile.USTAR_FORMAT) as archive:
            for name, raw in sorted(leaves.items()):
                item = tarfile.TarInfo(name); item.mode = 0o600; item.size = len(raw)
                archive.addfile(item, io.BytesIO(raw))
        archive_raw = stream.getvalue()
        commands = []
        raws = {'native/native-private.tar': archive_raw,
                **{'native/retained/' + n: b for n, b in leaves.items()}}
        for ordinal in range(2):
            logs = []
            for channel in ('stdout', 'stderr'):
                name = format(ordinal, '02d') + '-' + channel + '.log'
                body = b'' if channel == 'stderr' else b'actual finite utility output'
                raws['native/commands/' + name] = body
                logs.append({'name': name, 'bytes': len(body), 'sha256': subject.sha(body)})
            command = {'ordinal': ordinal, 'exit': 0, 'waited': True, 'group_absent': True,
                'forced_cleanup': False, 'timed_out': False, 'failure': None,
                'root_utility_cgroup_empty': True, 'logs': logs}
            commands.append(command)
            raws['native/commands/command-' + format(ordinal, '02d') + '.json'] = binding.canonical(command)
        packet = {'archive_sha256': subject.sha(archive_raw),
                  'files': {n: {'bytes': len(b), 'sha256': subject.sha(b)} for n, b in leaves.items()},
                  'commands': commands}
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve() / 'packet'
            old = os.umask(0o077)
            try: subject.fresh(root, raws, time.monotonic() + 5)
            finally: os.umask(old)
            actual = subject.scan(root, time.monotonic() + 5)
            subject.slot_custody(actual, packet, bridge, set(leaves))
            for fault in ('missing-archive', 'changed-archive', 'missing-leaf', 'changed-leaf',
                          'extra-leaf', 'missing-empty-stream', 'changed-command', 'rebound-projection'):
                changed = dict(actual); changed_packet = copy.deepcopy(packet)
                if fault == 'missing-archive': del changed['native/native-private.tar']
                elif fault == 'changed-archive': changed['native/native-private.tar'] += b'x'
                elif fault == 'missing-leaf': del changed['native/retained/payload']
                elif fault == 'changed-leaf': changed['native/retained/payload'] = b'substitute'
                elif fault == 'extra-leaf': changed['native/retained/extra'] = b'extra'
                elif fault == 'missing-empty-stream': del changed['native/commands/00-stderr.log']
                elif fault == 'changed-command': changed['native/commands/command-00.json'] = b'{}'
                else:
                    changed['native/retained/payload'] = b'substitute'
                    changed_packet['files']['payload'] = {'bytes': 10, 'sha256': subject.sha(b'substitute')}
                with self.subTest(fault=fault), self.assertRaises((binding.Rejected, ValueError)):
                    subject.slot_custody(changed, changed_packet, bridge, set(leaves))

    def test_n16_embedded_bytes_require_every_actual_leaf_and_both_streams(self):
        values = {'data': b'actual data', '00-stdout.log': b'actual stdout', '00-stderr.log': b''}
        command = {'ordinal': 0, 'exit': 0, 'phase': 'actual-csharp-n16', 'waited': True,
            'group_absent': True, 'root_start_guard_created': True, 'root_start_guard_closed': True,
            'root_utility_cgroup_empty': True, 'forced_cleanup': False, 'timed_out': False, 'failure': None,
            'logs': [{'name': n, 'bytes': len(values[n]), 'sha256': subject.sha(values[n])}
                     for n in ('00-stdout.log', '00-stderr.log')]}
        values['command-00.json'] = binding.canonical(command)
        leaves = {n: 4096 for n in values}
        packet = {'files': {n: {'bytes': len(b), 'sha256': subject.sha(b),
                               'base64': base64.b64encode(b).decode()} for n, b in values.items()}}
        raws = {'native/' + (n if n in ('command-00.json', '00-stdout.log', '00-stderr.log') else 'N16-' + n): b
                for n, b in values.items()}
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve() / 'packet'
            old = os.umask(0o077)
            try: subject.fresh(root, raws, time.monotonic() + 5)
            finally: os.umask(old)
            actual = subject.scan(root, time.monotonic() + 5)
            subject.n16_custody(actual, packet, leaves)
            for name in actual:
                for fault in ('delete', 'change'):
                    changed = dict(actual)
                    if fault == 'delete': del changed[name]
                    else: changed[name] += b'x'
                    with self.subTest(name=name, fault=fault), self.assertRaises(binding.Rejected):
                        subject.n16_custody(changed, packet, leaves)
        bad = copy.deepcopy(packet); bad['files']['data']['base64'] = base64.b64encode(b'substitute').decode()
        with self.assertRaises(binding.Rejected): subject.n16_custody(raws, bad, leaves)

    def test_capture_bound_data_import_checks_actual_source_bytes_before_import(self):
        required = ('.github/issue779-checkpoint1/binding.py', '.github/issue779-checkpoint1/measurement.py',
            '.github/issue779-checkpoint1/measurement_chain.py', '.github/issue779-checkpoint4/producer_roots.py',
            '.github/issue779-checkpoint4/measurement_arguments.py',
            'Evidence/ForgeTrust.AppSurface.Evidence.Supervision/LinuxRetainedReportSet.cs',
            'Evidence/ForgeTrust.AppSurface.Evidence.Supervision/SupervisionRunOutputBudget.cs')
        files = {}
        for name in required:
            path = subject.ROOT / name
            files[name] = {'sha256': subject.sha(path.read_bytes()), 'mode': format(stat.S_IMODE(path.stat().st_mode), '04o'),
                           'git_blob': '1' * 40}
        value = {'schema': 'issue779-checkpoint2-source-v1', 'source': HEAD, 'tree': '2' * 40,
                 'count': len(files), 'files': files}
        _, bodies = subject.source(binding.canonical(value), HEAD, time.monotonic() + 5)
        producer, measured = subject.validators(bodies)
        self.assertEqual(7, len(producer.CASES))
        modules = {name + '.dll': {} for name in binding.MEASUREMENT_MODULES}
        measured.report_json(binding.canonical(modules))
        first = required[0]
        value['files'][first]['sha256'] = '0' * 64
        with self.assertRaises(binding.Rejected):
            subject.source(binding.canonical(value), HEAD, time.monotonic() + 5)


if __name__ == '__main__': unittest.main()
