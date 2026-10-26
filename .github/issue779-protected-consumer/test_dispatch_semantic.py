"""Actual dispatch-to-.NET child controls; the resolver returns synthetic public data only."""
from __future__ import annotations

import base64
import copy
import hashlib
import json
import os
from pathlib import Path
import unittest
from unittest import mock

import dispatch
import github_parent
import test_semantic_gate


class DispatchSemanticProcessControls(unittest.TestCase):
    def setUp(self):
        self.host = Path(os.environ['ISSUE779_PARENT_TEST_DOTNET']).resolve(strict=True)
        self.value = test_semantic_gate.envelope()
        self.value['Expected'].update(RunId='github-actions-42-1', BaseRevision='b' * 40, SubjectRevision='a' * 40)
        manifest = json.loads(base64.b64decode(self.value['ManifestBytes']))
        manifest['EnvelopeAssertion'].update(RunId='github-actions-42-1', BaseRevision='b' * 40,
                                             SubjectRevision='a' * 40)
        manifest['ManifestDigest'] = ''
        manifest['ManifestDigest'] = test_semantic_gate.fixture.digest(manifest)
        self.value['ManifestBytes'] = test_semantic_gate.b64(test_semantic_gate.fixture.canonical(manifest))

    def run_child(self, mutate=None):
        value = copy.deepcopy(self.value)
        if mutate is not None:
            mutate(value)
        context = {
            'schema': dispatch.SCHEMA, 'expectation': test_semantic_gate.b64(b'{"public":"data only"}'),
            'protected_facts': {'workflow_path': '.github/workflows/producer.yml', 'workflow_sha': 'f' * 40,
                                'workflow_source_commit': 'e' * 40, 'base_sha': 'b' * 40, 'subject_sha': 'a' * 40},
            'policy_bytes': value['PolicyBytes'], 'policy_sha256': value['PolicySha256'],
            'diff_bytes': value['DiffBytes'], 'diff_sha256': value['DiffSha256'],
            'expected_gate': value['Expected'], 'artifacts': value['Artifacts'],
        }
        plan = base64.b64decode(value['PlanBytes'])
        manifest = base64.b64decode(value['ManifestBytes'])
        resolution = github_parent.ProtectedParentResolution(plan, manifest, 42, 1, 'a' * 40,
                                                              'e' * 40, 'f' * 40, 99, 'c' * 64, 128)
        with mock.patch.object(github_parent, 'resolve_protected_parent', return_value=resolution), \
             mock.patch.dict(os.environ, {'GH_TOKEN': 'private-child-token-must-not-echo'}):
            result = dispatch.dispatch_protected_parent(json.dumps(context).encode(),
                                                        'private-child-token-must-not-echo',
                                                        dotnet_host=self.host, total_timeout=12)
        self.assertNotIn('private-child-token', result.diagnostic)
        return result

    def test_real_fixed_consumer_accepts_only_the_coherent_public_semantic_neighbor(self):
        result = self.run_child()
        self.assertEqual('semantic-gate-allowed', result.status)
        self.assertEqual(0, result.exit_code)

    def test_real_fixed_consumer_denies_a_forged_expected_proof_and_changed_policy(self):
        def wrong_proof(value):
            value['Expected']['AcceptanceProofDigest'] = 'f' * 64

        def wrong_policy(value):
            policy = json.loads(base64.b64decode(value['PolicyBytes']))
            policy['Id'] = 'changed-policy-canary'
            raw = test_semantic_gate.fixture.canonical(policy)
            value['PolicyBytes'] = test_semantic_gate.b64(raw)
            value['PolicySha256'] = hashlib.sha256(raw).hexdigest()

        for change in (wrong_proof, wrong_policy):
            with self.subTest(change=change.__name__):
                result = self.run_child(change)
                self.assertEqual('semantic-gate-denied', result.status)
                self.assertEqual(1, result.exit_code)


if __name__ == '__main__':
    unittest.main()
