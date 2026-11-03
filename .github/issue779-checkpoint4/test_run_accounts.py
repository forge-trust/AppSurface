"""Detached receipt and input controls only; none creates owners, users, groups or a native result."""
from pathlib import Path
import copy
import importlib.util
import json
import os
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('account_driver', Path(__file__).with_name('run-accounts.py'))
subject = importlib.util.module_from_spec(spec)
spec.loader.exec_module(subject)


class AccountDriverDataTests(unittest.TestCase):
    generation = 'a' * 32
    entry = 'b' * 64

    def valid(self, rollback=False):
        return {'schema': 'issue779-c4-accounts-v1', 'variant': subject.VARIANTS[int(rollback)],
                'generation': self.generation, 'managed_entry_sha256': self.entry,
                'native_acceptance': False, 'consumer_started': False, 'creation_succeeded': not rollback,
                'cleanup_identity_guards_checked': None if rollback else True,
                'shared_close_task': None if rollback else True, 'closed_holder_rejected': None if rollback else True,
                'names_absent': True, 'numeric_ids_absent': True, 'utilities': 7 if rollback else 6,
                'settled_utilities': 7 if rollback else 6, 'failed_utilities': int(rollback),
                'close_completed': True, 'quarantined': False, 'reservation_failed': rollback,
                'identities_published': not rollback, 'original_handles_closed': True,
                'ids': {'worker_uid': 101, 'subject_uid': 102, 'worker_gid': 101, 'subject_gid': 102, 'results_gid': 103},
                'operation_failure': {'account_failure': 'OperationFailed', 'preparation_stage': 'UtilityExecute',
                    'utility_stage': 'TerminalCheck', 'operation': 'CreateResultsGroup', 'error_kind': 'Admission',
                    'diagnostic_code': 'ASEVD410', 'exec_main_code': 1, 'exec_main_status': 9,
                    'dbus_category': None} if rollback else None}

    def check(self, value):
        subject.validate_receipt(value, value['variant'], self.generation, self.entry)

    def test_both_valid_detached_neighbors_and_actual_utility_ranges(self):
        for rollback, bounds in ((False, (6, 8)), (True, (7, 9))):
            for count in bounds:
                value = self.valid(rollback); value['utilities'] = value['settled_utilities'] = count
                self.check(value)
            for count in (bounds[0] - 1, bounds[1] + 1, True):
                value = self.valid(rollback); value['utilities'] = value['settled_utilities'] = count
                with self.assertRaises(RuntimeError): self.check(value)

    def test_every_cleanup_and_negative_flag_requires_the_actual_boolean(self):
        for rollback in (False, True):
            good = self.valid(rollback); self.check(good)
            for key in ('names_absent', 'numeric_ids_absent', 'close_completed', 'original_handles_closed',
                        'native_acceptance', 'consumer_started', 'cleanup_identity_guards_checked',
                        'quarantined', 'creation_succeeded',
                        'identities_published', 'reservation_failed'):
                value = copy.deepcopy(good); value[key] = not good[key]
                with self.subTest(key=key), self.assertRaises(RuntimeError): self.check(value)

    def test_numeric_ids_are_five_exact_distinct_per_domain_without_forbidding_uid_gid_equality(self):
        good = self.valid(); self.check(good)
        for key in good['ids']:
            for invalid in (0, 4294967295, True, -1):
                value = copy.deepcopy(good); value['ids'][key] = invalid
                with self.subTest(key=key, invalid=invalid), self.assertRaises(RuntimeError): self.check(value)
        for key, other in (('subject_uid', 'worker_uid'), ('results_gid', 'worker_gid')):
            value = copy.deepcopy(good); value['ids'][key] = value['ids'][other]
            with self.assertRaises(RuntimeError): self.check(value)
        value = copy.deepcopy(good); value['ids']['private-canary'] = 999
        with self.assertRaises(RuntimeError): self.check(value)

    def test_rollback_needs_original_closed_terminal_failure_not_a_generic_failed_case(self):
        good = self.valid(True); self.check(good)
        for key, invalid in [('operation', 'CreateUser'), ('preparation_stage', 'IdentityRead'),
                             ('utility_stage', 'Unknown'), ('exec_main_code', 2), ('exec_main_status', 0),
                             ('exec_main_status', True), ('diagnostic_code', 'private-canary'), ('dbus_category', 'other')]:
            value = copy.deepcopy(good); value['operation_failure'][key] = invalid
            with self.subTest(key=key), self.assertRaises(RuntimeError): self.check(value)

    def test_source_generation_image_and_closed_members_bind_each_receipt(self):
        good = self.valid(); self.check(good)
        for key in ('generation', 'managed_entry_sha256', 'schema'):
            value = copy.deepcopy(good); value[key] = 'private-canary'
            with self.assertRaises(RuntimeError): self.check(value)
        value = copy.deepcopy(good); value['private-canary'] = True
        with self.assertRaises(RuntimeError): self.check(value)
        for raw in (b'{"x":1,"x":2}', b'{"x":1,"X":2}'):
            with self.assertRaises(RuntimeError): subject.strict_json(raw)

    def test_request_is_empty_targeted_and_retains_fixed_existing_allowances(self):
        value = subject.request_data(Path('/var/lib/a/tool'), Path('/var/lib/a/runtime'), Path('/var/lib/a/subject'), 'c' * 40, 1190)
        self.assertEqual([], value['observation_producer_ids'])
        self.assertEqual(['accounts-empty'], value['observation_profile_ids'])
        self.assertNotIn('solution', value); self.assertNotIn('application_id', value)
        self.assertEqual([30, 120, 60, 120, 10], [value[key + '_seconds'] for key in ('admission', 'start', 'collection', 'cleanup', 'stopping')])
        with self.assertRaises(RuntimeError): subject.request_data(Path('/a'), Path('/b'), Path('/c'), 'c' * 40, 1110)
        policy = subject.policy()
        self.assertEqual('Targeted', policy['Profiles'][0]['Scope'])
        self.assertTrue(all(not policy['Profiles'][0][key] for key in ('Resources', 'Producers', 'Obligations')))
        self.assertTrue(policy['Profiles'][1]['Resources'])

    def test_instrumentation_binds_every_module_source_and_compiled_variant(self):
        original = {'source': 'c' * 40, 'variant': 'C4Accounts', 'collector': 'coverlet.console/10.0.1',
                    'source_capture_sha256': 'd' * 64, 'files': {name: 'a' * 64 for name in subject.NAMES}}
        instrumented = copy.deepcopy(original)
        for name in subject.NAMES:
            if name.endswith('.dll'): instrumented['files'][name] = 'b' * 64
        subject.validate_instrumentation(original, instrumented, 'c' * 40, 'C4Accounts')
        for key, invalid in [('source', 'e' * 40), ('variant', 'C4AccountRollback'),
                             ('source_capture_sha256', 'e' * 64), ('collector', 'other')]:
            value = copy.deepcopy(instrumented); value[key] = invalid
            with self.subTest(key=key), self.assertRaises(RuntimeError):
                subject.validate_instrumentation(original, value, 'c' * 40, 'C4Accounts')
        value = copy.deepcopy(instrumented); value['files'][subject.NAMES[0]] = original['files'][subject.NAMES[0]]
        with self.assertRaises(RuntimeError): subject.validate_instrumentation(original, value, 'c' * 40, 'C4Accounts')
        self.assertEqual(14, len(original['files']))
        for mutation in ('missing-dll', 'missing-pdb', 'alias', 'unchanged'):
            value = copy.deepcopy(instrumented)
            name = 'ForgeTrust.AppSurface.Evidence.Cli.' + ('pdb' if mutation == 'missing-pdb' else 'dll')
            if mutation.startswith('missing'):
                del value['files'][name]
            elif mutation == 'alias':
                value['files']['other.dll'] = value['files'].pop(name)
            else:
                value['files'][name] = original['files'][name]
            with self.subTest(shared_cli=mutation), self.assertRaises(RuntimeError):
                subject.validate_instrumentation(original, value, 'c' * 40, 'C4Accounts')

    def test_reader_rejects_links_and_oversize_after_real_regular_neighbor(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary); path = root / 'record'; path.write_bytes(b'closed')
            self.assertEqual(b'closed', subject.read_file(path, 6))
            with self.assertRaises(RuntimeError): subject.read_file(path, 5)
            link = root / 'link'; link.symlink_to(path)
            with self.assertRaises(OSError): subject.read_file(link, 6)
            link.unlink(); os.link(path, link)
            with self.assertRaises(RuntimeError): subject.read_file(path, 6)
            fifo = root / 'fifo'; os.mkfifo(fifo)
            with self.assertRaises(RuntimeError): subject.read_file(fifo, 6)


if __name__ == '__main__':
    unittest.main()
