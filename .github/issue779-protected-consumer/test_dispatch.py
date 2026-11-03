"""In-memory dispatch controls; resolver and child process are mocked at private seams."""
import base64
import hashlib
import json
import os
from pathlib import Path
import sys
import tempfile
import time
import unittest
from unittest import mock

import dispatch
import github_parent


class DispatchControls(unittest.TestCase):
    def setUp(self):
        self.expectation = b'{"schema":"parent-expectation-fixture"}'
        self.policy = b'{"schema":"policy-fixture"}'
        self.diff = b'--- a/file\n+++ b/file\n'
        self.plan = b'{"schema":"resolved-plan-from-parent-archive"}'
        self.manifest = b'{"schema":"manifest-from-parent-archive"}'
        self.resolution = github_parent.ProtectedParentResolution(
            self.plan, self.manifest, 42, 3, 'a' * 40, 'e' * 40, 'f' * 40,
            987, '1' * 64, 1234)
        self.expected = {
            'RunId': 'github-actions-42-3', 'BaseRevision': 'b' * 40,
            'SubjectRevision': 'a' * 40, 'WorkflowIdentity': 'workflow-fixture',
            'PolicyDigest': '2' * 64, 'AcceptanceProofDigest': '3' * 64,
            'OutputIdentity': 'output-fixture', 'VerifierId': 'verifier-fixture',
            'VerifierVersion': '1', 'Provider': 'github-actions',
            'CatalogueDigest': '4' * 64, 'CapabilitiesDigest': '5' * 64,
            'AllocationPolicyDigest': '6' * 64, 'ToolRootIdentity': 'tool-root-fixture',
            'SubjectRootIdentity': 'subject-root-fixture', 'OutputParentIdentity': 'parent-fixture',
            'AllowReleaseValidatedNotAttested': False,
        }
        self.facts = {
            'workflow_path': '.github/workflows/protected.yml', 'workflow_sha': 'f' * 40,
            'workflow_source_commit': 'e' * 40, 'base_sha': 'b' * 40, 'subject_sha': 'a' * 40,
        }
        self.context = {
            'schema': dispatch.SCHEMA,
            'expectation': self.b64(self.expectation),
            'protected_facts': self.facts,
            'policy_bytes': self.b64(self.policy), 'policy_sha256': hashlib.sha256(self.policy).hexdigest(),
            'diff_bytes': self.b64(self.diff), 'diff_sha256': hashlib.sha256(self.diff).hexdigest(),
            'expected_gate': self.expected,
            'artifacts': [{'ProducerId': 'producer', 'RelativePath': 'reports/empty.json', 'Bytes': ''}],
        }

    @staticmethod
    def b64(raw):
        return base64.b64encode(raw).decode('ascii')

    def encoded_context(self, context=None):
        return json.dumps(self.context if context is None else context, separators=(',', ':')).encode('utf-8')

    def execute(self, child=(0, b'EVIDENCE_PARENT_GATE_ALLOWED\n', b'', False), context=None):
        with mock.patch.object(github_parent, 'resolve_protected_parent', return_value=self.resolution) as resolver, \
             mock.patch.object(dispatch, '_run_child', return_value=child) as runner:
            result = dispatch.dispatch_protected_parent(
                self.encoded_context(context), 'fixture-token', dotnet_host=Path('/opt/parent-dotnet/dotnet'),
                total_timeout=30)
        return result, resolver, runner

    def test_resolver_output_is_base64_enveloped_into_the_fixed_consumer(self):
        result, resolver, runner = self.execute()
        self.assertEqual('semantic-gate-allowed', result.status)
        resolver.assert_called_once()
        argv, payload, _ = runner.call_args.args
        self.assertEqual(['/opt/parent-dotnet/dotnet', str(dispatch.SEMANTIC_GATE_DLL)], argv)
        envelope = json.loads(payload)
        self.assertEqual(self.b64(self.plan), envelope['PlanBytes'])
        self.assertEqual(self.b64(self.manifest), envelope['ManifestBytes'])
        self.assertEqual('', envelope['Artifacts'][0]['Bytes'])
        self.assertNotIn('fixture-token', payload.decode('ascii'))

    def test_context_rejects_unknown_fields_and_duplicate_case_alias_before_resolver(self):
        context = dict(self.context, executable='/tmp/other')
        with mock.patch.object(github_parent, 'resolve_protected_parent') as resolver:
            with self.assertRaises(dispatch.DispatchInputError):
                dispatch.dispatch_protected_parent(self.encoded_context(context), 'token',
                                                   dotnet_host=Path('/opt/dotnet'))
            duplicate = self.encoded_context().replace(b'"schema":', b'"Schema":"alias","schema":', 1)
            with self.assertRaises(dispatch.DispatchInputError):
                dispatch.dispatch_protected_parent(duplicate, 'token', dotnet_host=Path('/opt/dotnet'))
        resolver.assert_not_called()

    def test_policy_and_diff_require_independent_raw_sha256(self):
        for name in ('policy_sha256', 'diff_sha256'):
            with self.subTest(name=name):
                context = dict(self.context, **{name: '0' * 64})
                with mock.patch.object(github_parent, 'resolve_protected_parent') as resolver:
                    with self.assertRaises(dispatch.DispatchInputError):
                        dispatch.dispatch_protected_parent(self.encoded_context(context), 'token',
                                                           dotnet_host=Path('/opt/dotnet'))
                resolver.assert_not_called()

    def test_expected_run_attempt_base_and_subject_must_bind_resolver(self):
        for key, value in (('RunId', 'github-actions-42-2'), ('BaseRevision', 'c' * 40),
                           ('SubjectRevision', 'd' * 40)):
            with self.subTest(key=key):
                context = json.loads(self.encoded_context())
                context['expected_gate'][key] = value
                with mock.patch.object(github_parent, 'resolve_protected_parent', return_value=self.resolution), \
                     mock.patch.object(dispatch, '_run_child') as runner:
                    with self.assertRaises(dispatch.DispatchInputError):
                        dispatch.dispatch_protected_parent(self.encoded_context(context), 'token',
                                                           dotnet_host=Path('/opt/dotnet'))
                runner.assert_not_called()

    def test_artifact_shapes_and_limits_are_checked_before_resolution(self):
        cases = []
        cases.append(dict(self.context, artifacts=[{'ProducerId': 'p', 'RelativePath': 'x', 'Bytes': '***'}]))
        cases.append(dict(self.context, artifacts=[{'ProducerId': 'p', 'RelativePath': 'x', 'Bytes': self.b64(b'x' * (1024 * 1024 + 1))}]))
        cases.append(dict(self.context, artifacts=[{'ProducerId': 'p', 'RelativePath': 'x', 'Bytes': ''}] * 65))
        for context in cases:
            with self.subTest(count=len(context['artifacts'])):
                with mock.patch.object(github_parent, 'resolve_protected_parent') as resolver:
                    with self.assertRaises(dispatch.DispatchInputError):
                        dispatch.dispatch_protected_parent(self.encoded_context(context), 'token',
                                                           dotnet_host=Path('/opt/dotnet'))
                resolver.assert_not_called()

    def test_gate_diagnostics_are_mapped_only_when_exact_and_never_echoed(self):
        cases = (
            ((1, b'', b'EVIDENCE_PARENT_GATE_DENIED\n', False), 'semantic-gate-denied'),
            ((2, b'', b'EVIDENCE_PARENT_GATE_INPUT_REJECTED\n', False), 'semantic-gate-input-rejected'),
            ((3, b'', b'EVIDENCE_PARENT_GATE_FAILED\n', False), 'semantic-gate-failed'),
            ((0, b'raw subject secret', b'', False), 'semantic-gate-output-invalid'),
            ((0, b'EVIDENCE_PARENT_GATE_ALLOWED\n', b'diagnostic', False), 'semantic-gate-output-invalid'),
            ((0, b'EVIDENCE_PARENT_GATE_ALLOWED\n', b'', True), 'semantic-gate-output-invalid'),
        )
        for child, status in cases:
            with self.subTest(status=status, childcode=child[0]):
                result, _, _ = self.execute(child=child)
                self.assertEqual(status, result.status)
                self.assertNotIn('raw subject secret', result.diagnostic)

    def test_parent_owned_runtime_path_is_explicit_and_no_extra_arguments_are_added(self):
        result, _, runner = self.execute()
        self.assertEqual('semantic-gate-allowed', result.status)
        argv = runner.call_args.args[0]
        self.assertEqual(2, len(argv))
        self.assertEqual('dotnet', Path(argv[0]).name)
        self.assertEqual('EvidenceHost.ProtectedParentGate.dll', Path(argv[1]).name)

    def test_invalid_dotnet_path_rejects_before_resolver(self):
        with mock.patch.object(github_parent, 'resolve_protected_parent') as resolver:
            with self.assertRaises(dispatch.DispatchInputError):
                dispatch.dispatch_protected_parent(self.encoded_context(), 'token',
                                                   dotnet_host=Path('dotnet'))
            with self.assertRaises(dispatch.DispatchInputError):
                dispatch.dispatch_protected_parent(self.encoded_context(), 'token',
                                                   dotnet_host=Path('/opt/dotnet/../other'))
        resolver.assert_not_called()

    def test_token_is_not_in_child_environment(self):
        with mock.patch.dict(dispatch.os.environ, {'GH_TOKEN': 'do-not-pass'}, clear=True):
            environment = dispatch._child_environment()
        self.assertNotIn('GH_TOKEN', environment)
        self.assertEqual({'GH_TOKEN'}, {'GH_TOKEN'} - set(environment))

    def test_resolver_deadline_and_child_failures_return_fixed_status(self):
        with mock.patch.object(github_parent, 'resolve_protected_parent', side_effect=TimeoutError):
            with self.assertRaises(TimeoutError):
                dispatch.dispatch_protected_parent(self.encoded_context(), 'token',
                                                   dotnet_host=Path('/opt/dotnet'), total_timeout=1)
        with mock.patch.object(github_parent, 'resolve_protected_parent', return_value=self.resolution), \
             mock.patch.object(dispatch, '_run_child', side_effect=TimeoutError):
            timed = dispatch.dispatch_protected_parent(self.encoded_context(), 'token',
                                                       dotnet_host=Path('/opt/dotnet'), total_timeout=30)
        self.assertEqual('semantic-gate-timeout', timed.status)
        with mock.patch.object(github_parent, 'resolve_protected_parent', return_value=self.resolution), \
             mock.patch.object(dispatch, '_run_child', side_effect=OSError):
            failed = dispatch.dispatch_protected_parent(self.encoded_context(), 'token',
                                                        dotnet_host=Path('/opt/dotnet'), total_timeout=30)
        self.assertEqual('semantic-gate-launch-failed', failed.status)


class DispatchOwnedProcessControls(unittest.TestCase):
    def test_real_child_drains_both_pipes_and_writes_large_payload_within_one_deadline(self):
        payload = b'x' * (256 * 1024)
        code = ('import sys; data=sys.stdin.buffer.read(); '
                'sys.stdout.buffer.write(b"O" * 2048); sys.stderr.buffer.write(b"E" * 2048); '
                'sys.stdout.flush(); sys.stderr.flush(); sys.exit(0 if data == b"x" * (256 * 1024) else 9)')
        deadline = time.monotonic() + 5.0
        result = dispatch._run_child([sys.executable, '-c', code], payload, deadline,
                                     work_deadline=deadline - dispatch.CLEANUP_RESERVE_SECONDS)
        self.assertEqual((0, b'O' * 2048, b'E' * 2048, False), result)

    def test_real_ignored_term_child_and_descendant_are_killed_and_group_reaped(self):
        with tempfile.TemporaryDirectory(prefix='issue779-dispatch-child-') as temp:
            pid_file = Path(temp) / 'descendant.pid'
            descendant = ('import signal,time; signal.signal(signal.SIGTERM, signal.SIG_IGN); '
                          'time.sleep(30)')
            parent = ('import pathlib,signal,subprocess,sys,time; '
                      'child=subprocess.Popen([sys.executable,"-c",sys.argv[1]]); '
                      'pathlib.Path(sys.argv[2]).write_text(f"{__import__(\'os\').getpid()},{child.pid}"); '
                      'signal.signal(signal.SIGTERM,signal.SIG_IGN); time.sleep(30)')
            started = time.monotonic()
            deadline = started + 3.5
            with self.assertRaises(TimeoutError):
                dispatch._run_child([sys.executable, '-c', parent, descendant, str(pid_file)], b'',
                                    deadline, work_deadline=started + 0.35)
            self.assertLess(time.monotonic(), deadline)
            parent_pid, descendant_pid = map(int, pid_file.read_text().split(','))
            self.assertNotEqual(parent_pid, descendant_pid)
            with self.assertRaises(ProcessLookupError):
                os.kill(parent_pid, 0)
            with self.assertRaises(ProcessLookupError):
                os.killpg(parent_pid, 0)
            with self.assertRaises(ProcessLookupError):
                os.kill(descendant_pid, 0)


if __name__ == '__main__':
    unittest.main()
