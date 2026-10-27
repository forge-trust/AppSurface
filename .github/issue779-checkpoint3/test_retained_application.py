"""Portable data-only tests for the fixed C3 retained application callback."""
import importlib.util
import json
from pathlib import Path
import time
import unittest

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location('retained_application', HERE / 'retained_application.py')
subject = importlib.util.module_from_spec(spec)
spec.loader.exec_module(subject)
runner_spec = importlib.util.spec_from_file_location('retained_c3_runner', HERE / 'run-native.py')
runner = importlib.util.module_from_spec(runner_spec)
runner_spec.loader.exec_module(runner)


def encode(value):
    return json.dumps(value, sort_keys=True, separators=(',', ':')).encode()


def fixture():
    source = 'a' * 40
    generation = '1' * 32
    guard_event = {'schema': 'evidence-native-owner-reference-guards-v1', 'generation': generation,
                   'rejected': list(runner.OWNER_REFERENCE_GUARDS), 'native_acceptance': False}
    output = {'schema': 'evidence-coverage-output-v1', 'registered_pairs': 2,
              'received_bytes': 1, 'received_byte_limit': 16 * 1024 * 1024,
              'failure': 'None', 'quota_exceeded': False, 'stop_signal_failed': False,
              'pairs': [{'received_bytes': 1, 'stdout_bytes': 0, 'stderr_bytes': 1,
                         'stdout_eof': True, 'stderr_eof': True, 'failure': 'None', 'quota_exceeded': False},
                        {'received_bytes': 0, 'stdout_bytes': 0, 'stderr_bytes': 0,
                         'stdout_eof': True, 'stderr_eof': True, 'failure': 'None', 'quota_exceeded': False}]}
    rows, files = [], {}
    for case in subject.CASES:
        positive = case in ('normal', 'sdk-normal')
        disposal = case == 'disposal-failure'
        observation = {'schema': 'evidence-application-native-v1', 'phase': 'settled',
                       'lease_id': generation, 'unit': 'appsurface-evidence-application-' + generation + '.service',
                       'main_code': 1 if positive else None, 'main_status': 0 if positive else None,
                       'group_empty_twice': True, 'pumps_joined': True, 'pending_start_joined': True,
                       'received_bytes': 1 if positive else 0, 'ledger_failure': 'None', 'monitor_failed': False}
        row = {'case': case, 'generation': generation, 'passed': True, 'native_acceptance': False,
               'groups_empty': True, 'final_groups_empty': True, 'application_prefixes_retained': True,
               'root_exit': 0 if positive else 1,
               'application_observations': ([{'phase': 'ready', 'dcp_checked': True}, observation]
                                            if positive else [observation]),
               'owner_reference_guards': guard_event,
               'accounts': [{'name': prefix + generation[:28], 'table': table,
                             'present': case == 'disposal-failure'}
                            for prefix, table in (('evw', 'passwd'), ('evs', 'passwd'), ('eva', 'passwd'),
                                                  ('evw', 'group'), ('evs', 'group'), ('eva', 'group'),
                                                  ('evr', 'group'), ('evg', 'group'))]}
        if not disposal:
            row['output'] = output
        stderr_events = [guard_event]
        if not disposal:
            stderr_events.append(output)
            files['native/' + case + '-owner.stderr'] = b'\n'.join(map(encode, stderr_events))
        else:
            storage = {'schema': 'evidence-application-storage-v1', 'phase': 'unmount-rejected', 'errno': 16}
            files['native/' + case + '-owner.stderr'] = b'\n'.join((encode(guard_event), encode(storage)))
        if positive:
            row.update({'producer': {'pid': 2}, 'dcp': {'pid': 3}, 'coverage': {'line-rate': '1'},
                        'factory_entered': True})
            files['native/' + case + '-owner.stdout'] = encode(
                {'ExecutionVerdict': 'Passed', 'CleanupCompleted': True, 'ClaimKind': 'ObservationOnly'})
            files['native/' + case + '-manifest.json'] = encode({
                'ExecutionVerdict': 'Passed', 'Metrics': {'CleanupCompleted': True},
                'ClaimKind': 'ObservationOnly', 'ProducerResults': [{'Outcome': 'Passed',
                    'Artifacts': [{'RelativePath': 'merged/coverage.cobertura.xml'}]}]})
        else:
            files['native/' + case + '-owner.stdout'] = b''
        if case == 'readiness':
            row.update({'http_negative': {'status': 503, 'received_bytes': 12},
                        'dcp': {'pid': 3}, 'resource': {'status': 503}})
        if case == 'factory-stall':
            row.update({'factory_entered': True, 'dcp': None})
        if case in ('cancel', 'stuck-descendant'):
            row.update({'cancel_sent': True, 'producer': {'pid': 4}})
        if case == 'stuck-descendant':
            row.update({'descendant_term_seen': True, 'descendant': {'pid': 5}})
        if disposal:
            row['scratch_hold'] = {'inode': 1}
        files['native/' + case + '.json'] = encode(row)
        rows.append(row)
    guards = []
    for case in subject.GUARDS:
        producer_case = case in subject.GUARDS[3:]
        failure = {'schema': 'evidence-native-observation-failure-v4',
                   'phase': 'ProducerSeal' if producer_case else 'WorkspaceCreate',
                   'diagnostic_code': 'ASEVD402' if producer_case else 'ASEVD410',
                   'error_kind': 'Admission', 'account_failure': None,
                   'control_failure': None, 'custody_failure': None}
        row = {'case': case, 'generation': generation, 'passed': True, 'native_acceptance': False,
               'groups_empty': True, 'final_groups_empty': True, 'outside_sentinel_unchanged': True,
               'quarantined': True, 'root_exit': 1,
               'accounts': [{'name': prefix + generation[:28], 'table': table, 'present': True}
                            for prefix, table in (('evw', 'passwd'), ('evs', 'passwd'), ('eva', 'passwd'),
                                                  ('evw', 'group'), ('evs', 'group'), ('eva', 'group'),
                                                  ('evr', 'group'), ('evg', 'group'))]}
        if producer_case:
            row['workspace_guard'] = {'first_failure': failure}
        else:
            row['first_failure'] = failure
        guards.append(row)
        files['native/' + case + '.json'] = encode(row)
        files['native/' + case + '-owner.stdout'] = b''
        files['native/' + case + '-owner.stderr'] = encode(failure)
    receipt = {'schema': 'issue779-csharp-checkpoint3-native-v1', 'source': source,
               'base': subject.BASE, 'exit': 0, 'trusted_eligibility': False,
               'cases': rows, 'workspace_guards': guards}
    return receipt, files, source


class RetainedApplicationTests(unittest.TestCase):
    def validate(self, receipt, files, source):
        return subject.validate(receipt, source=source, base=subject.BASE, files=files,
                                events=runner.events,
                                output_accounting=runner.output_accounting,
                                owner_reference_guards=runner.owner_reference_guards,
                                deadline=time.monotonic() + 10)

    def test_fixed_seven_case_projection_has_no_authority(self):
        receipt, files, source = fixture()
        result = self.validate(receipt, files, source)
        self.assertEqual(tuple(subject.CASES), result['cases'])
        self.assertIs(result['native_acceptance'], False)
        self.assertIs(result['trusted_eligibility'], False)

    def test_rebound_non_eof_event_still_fails_the_fixed_stream_parser(self):
        receipt, files, source = fixture()
        events = [json.loads(row) for row in files['native/normal-owner.stderr'].splitlines()]
        events[-1]['pairs'][0]['stderr_eof'] = False
        files['native/normal-owner.stderr'] = b'\n'.join(map(encode, events))
        receipt['cases'][0]['output']['pairs'][0]['stderr_eof'] = False
        files['native/normal.json'] = encode(receipt['cases'][0])
        with self.assertRaisesRegex(RuntimeError, 'actual-all-eof'):
            self.validate(receipt, files, source)

    def test_duplicate_case_and_fixed_false_claims_reject(self):
        for mutation in ('duplicate', 'native', 'trusted'):
            receipt, files, source = fixture()
            if mutation == 'duplicate':
                receipt['cases'][1] = dict(receipt['cases'][0], case='normal')
            elif mutation == 'native':
                receipt['cases'][0]['native_acceptance'] = True
            else:
                receipt['trusted_eligibility'] = True
            with self.subTest(mutation=mutation), self.assertRaises(ValueError):
                self.validate(receipt, files, source)


if __name__ == '__main__':
    unittest.main()
