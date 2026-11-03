"""Real process tests of parent semantic comparison; synthetic JSON grants no runtime authority."""
import argparse
import base64
import copy
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import unittest

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('public_gate_fixture', ROOT / 'tests/evidencehost-consumer/test_protected_gate_consumer.py')
fixture = importlib.util.module_from_spec(spec)
spec.loader.exec_module(fixture)
DOTNET = None
CONSUMER = None
CANARY = 'protected-parent-private-canary-must-not-echo'


def b64(raw):
    return base64.b64encode(raw).decode('ascii')


def envelope():
    """Create a public API-only control with protected inputs separate from uploaded documents."""
    value = fixture.synthetic_envelope()
    plan = value['Plan']
    policy = plan['PolicySnapshot']
    # The current public producer declaration serializes its default retention mode.
    # Keep the synthetic structural fixture on the same canonical contract shape.
    plan['Profile']['Producers'][0]['CoverageArtifactRetention'] = 'managed'
    policy['Profiles'][0]['Producers'][0]['CoverageArtifactRetention'] = 'managed'
    policy['Rules'] = [{'Id': 'source', 'Pattern': 'src/**', 'ProfileId': 'synthetic-api', 'Precedence': 0}]
    plan['MatchedRuleIds'] = ['source']
    plan['PolicyDigest'] = fixture.digest(policy)
    plan['PlanDigest'] = ''
    plan['PlanDigest'] = fixture.digest(plan)
    manifest = value['Manifest']
    manifest['PlanDigest'] = plan['PlanDigest']
    manifest['ManifestDigest'] = ''
    manifest['ManifestDigest'] = fixture.digest(manifest)
    value['Expected']['PolicyDigest'] = plan['PolicyDigest']
    policy_bytes = fixture.canonical(policy)
    diff_bytes = b'diff --git a/src/Feature.cs b/src/Feature.cs\n--- a/src/Feature.cs\n+++ b/src/Feature.cs\n@@ -1 +1 @@\n-old\n+new\n'
    return {'Schema': 'issue779-protected-parent-gate-v1', 'PolicyBytes': b64(policy_bytes),
            'PolicySha256': fixture.hashlib.sha256(policy_bytes).hexdigest(),
            'DiffBytes': b64(diff_bytes), 'DiffSha256': fixture.hashlib.sha256(diff_bytes).hexdigest(),
            'PlanBytes': b64(fixture.canonical(plan)), 'ManifestBytes': b64(fixture.canonical(manifest)),
            'Expected': value['Expected'], 'Artifacts': []}


class ProtectedParentSemanticGateTests(unittest.TestCase):
    def check(self, value, code, message, *, raw=None):
        result = subprocess.run([DOTNET, str(CONSUMER)], input=raw if raw is not None else fixture.canonical(value),
                                capture_output=True, timeout=15, check=False)
        self.assertEqual(code, result.returncode, result.stderr[:256])
        self.assertEqual((message + '\n').encode() if code == 0 else b'', result.stdout)
        self.assertEqual((message + '\n').encode() if code else b'', result.stderr)
        self.assertNotIn(CANARY.encode(), result.stdout + result.stderr)

    def test_real_planner_reconstructs_supported_neighbor_before_public_gate(self):
        self.check(envelope(), 0, 'EVIDENCE_PARENT_GATE_ALLOWED')

    def test_legacy_synthetic_shape_without_public_default_is_denied(self):
        value = envelope()
        plan = json.loads(base64.b64decode(value['PlanBytes']))
        policy = json.loads(base64.b64decode(value['PolicyBytes']))
        del plan['Profile']['Producers'][0]['CoverageArtifactRetention']
        del plan['PolicySnapshot']['Profiles'][0]['Producers'][0]['CoverageArtifactRetention']
        del policy['Profiles'][0]['Producers'][0]['CoverageArtifactRetention']
        policy_bytes = fixture.canonical(policy)
        plan['PolicyDigest'] = fixture.digest(policy)
        plan['PolicySnapshot'] = policy
        plan['PlanDigest'] = ''
        plan['PlanDigest'] = fixture.digest(plan)
        manifest = json.loads(base64.b64decode(value['ManifestBytes']))
        manifest['PlanDigest'] = plan['PlanDigest']
        manifest['ManifestDigest'] = ''
        manifest['ManifestDigest'] = fixture.digest(manifest)
        value['PolicyBytes'] = b64(policy_bytes)
        value['PolicySha256'] = fixture.hashlib.sha256(policy_bytes).hexdigest()
        value['PlanBytes'] = b64(fixture.canonical(plan))
        value['ManifestBytes'] = b64(fixture.canonical(manifest))
        value['Expected']['PolicyDigest'] = plan['PolicyDigest']
        self.check(value, 1, 'EVIDENCE_PARENT_GATE_DENIED')

    def test_coherent_uploaded_plan_cannot_replace_protected_policy(self):
        value = envelope()
        plan = json.loads(base64.b64decode(value['PlanBytes']))
        plan['PolicySnapshot']['Id'] = CANARY
        plan['PolicyId'] = CANARY
        plan['PolicyDigest'] = fixture.digest(plan['PolicySnapshot'])
        plan['PlanDigest'] = ''
        plan['PlanDigest'] = fixture.digest(plan)
        manifest = json.loads(base64.b64decode(value['ManifestBytes']))
        manifest['PlanDigest'] = plan['PlanDigest']
        manifest['ManifestDigest'] = ''
        manifest['ManifestDigest'] = fixture.digest(manifest)
        value['PlanBytes'] = b64(fixture.canonical(plan))
        value['ManifestBytes'] = b64(fixture.canonical(manifest))
        self.check(value, 1, 'EVIDENCE_PARENT_GATE_DENIED')

    def test_current_diff_must_reconstruct_exact_uploaded_plan(self):
        value = envelope()
        changed = b'diff --git a/src/Other.cs b/src/Other.cs\n--- a/src/Other.cs\n+++ b/src/Other.cs\n@@ -1 +1 @@\n-a\n+b\n'
        value['DiffBytes'] = b64(changed)
        value['DiffSha256'] = fixture.hashlib.sha256(changed).hexdigest()
        self.check(value, 1, 'EVIDENCE_PARENT_GATE_DENIED')

    def test_policy_and_diff_bytes_require_independent_raw_hashes(self):
        for key in ('PolicySha256', 'DiffSha256'):
            with self.subTest(key=key):
                value = envelope(); value[key] = 'f' * 64
                self.check(value, 2, 'EVIDENCE_PARENT_GATE_INPUT_REJECTED')

    def test_each_current_protected_expectation_mismatch_denies(self):
        for key in ('RunId', 'BaseRevision', 'SubjectRevision', 'WorkflowIdentity', 'PolicyDigest',
                    'AcceptanceProofDigest', 'OutputIdentity', 'VerifierId', 'VerifierVersion', 'Provider',
                    'CatalogueDigest', 'CapabilitiesDigest', 'AllocationPolicyDigest', 'ToolRootIdentity',
                    'SubjectRootIdentity', 'OutputParentIdentity'):
            with self.subTest(key=key):
                value = envelope(); value['Expected'][key] = 'e' * 64 if key.endswith('Digest') else CANARY
                self.check(value, 1, 'EVIDENCE_PARENT_GATE_DENIED')

    def test_observation_and_failed_cleanup_remain_denied_after_rehash(self):
        for mutation in ('observation', 'cleanup', 'terminal-failure'):
            with self.subTest(mutation=mutation):
                value = envelope(); manifest = json.loads(base64.b64decode(value['ManifestBytes']))
                if mutation == 'observation':
                    manifest.update(Mode='Observation', ClaimKind='ObservationOnly', Eligibility='Informational',
                                    EnvelopeStatus='NotRequired', EnvelopeAssertion=None)
                elif mutation == 'cleanup': manifest['Metrics']['CleanupCompleted'] = False
                else: manifest['Metrics']['TerminalFailureCode'] = 'ASEVD410'
                manifest['ManifestDigest'] = ''; manifest['ManifestDigest'] = fixture.digest(manifest)
                value['ManifestBytes'] = b64(fixture.canonical(manifest))
                self.check(value, 1, 'EVIDENCE_PARENT_GATE_DENIED')

    def test_extra_or_duplicate_collected_bytes_cannot_be_ignored(self):
        for duplicate in (False, True):
            with self.subTest(duplicate=duplicate):
                value = envelope()
                body = {'ProducerId': 'producer', 'RelativePath': 'unexpected', 'Bytes': b64(CANARY.encode())}
                value['Artifacts'] = [body, copy.deepcopy(body)] if duplicate else [body]
                self.check(value, 1, 'EVIDENCE_PARENT_GATE_DENIED')

    def test_matching_artifact_bytes_are_required_for_every_manifest_record(self):
        value = envelope()
        plan = json.loads(base64.b64decode(value['PlanBytes']))
        manifest = json.loads(base64.b64decode(value['ManifestBytes']))
        slot = {'LogicalName': 'report', 'RelativeRoot': 'report', 'MediaType': 'application/json',
                'Required': True, 'MaximumBytes': 1024}
        plan['Profile']['Producers'][0]['ArtifactSlots'] = [slot]
        plan['PolicySnapshot']['Profiles'][0]['Producers'][0]['ArtifactSlots'] = [slot]
        policy_bytes = fixture.canonical(plan['PolicySnapshot'])
        plan['PolicyDigest'] = fixture.digest(plan['PolicySnapshot'])
        plan['PlanDigest'] = ''; plan['PlanDigest'] = fixture.digest(plan)
        value['PolicyBytes'] = b64(policy_bytes)
        value['PolicySha256'] = fixture.hashlib.sha256(policy_bytes).hexdigest()
        value['Expected']['PolicyDigest'] = plan['PolicyDigest']
        value['PlanBytes'] = b64(fixture.canonical(plan))
        for body in (b'', b'{"actual":"collected"}'):
            with self.subTest(bytes=len(body)):
                manifest['PlanDigest'] = plan['PlanDigest']
                manifest['ProducerResults'][0]['Artifacts'] = [
                    {'LogicalName': 'report', 'RelativePath': 'report/result.json', 'MediaType': 'application/json',
                     'LengthBytes': len(body), 'Sha256': fixture.hashlib.sha256(body).hexdigest()}]
                manifest['ManifestDigest'] = ''; manifest['ManifestDigest'] = fixture.digest(manifest)
                value['ManifestBytes'] = b64(fixture.canonical(manifest))
                value['Artifacts'] = [{'ProducerId': 'producer', 'RelativePath': 'report/result.json', 'Bytes': b64(body)}]
                self.check(value, 0, 'EVIDENCE_PARENT_GATE_ALLOWED')
                for fault in ('missing', 'bytes', 'producer', 'path', 'duplicate'):
                    with self.subTest(fault=fault):
                        changed = copy.deepcopy(value)
                        if fault == 'missing': changed['Artifacts'] = []
                        elif fault == 'bytes': changed['Artifacts'][0]['Bytes'] = b64(body + b'x')
                        elif fault == 'producer': changed['Artifacts'][0]['ProducerId'] = 'other-producer'
                        elif fault == 'path': changed['Artifacts'][0]['RelativePath'] = 'other.json'
                        else: changed['Artifacts'].append(copy.deepcopy(changed['Artifacts'][0]))
                        self.check(changed, 1, 'EVIDENCE_PARENT_GATE_DENIED')

    def test_document_exact_limit_neighbor_and_one_byte_over(self):
        for key in ('PolicyBytes', 'DiffBytes', 'PlanBytes', 'ManifestBytes'):
            with self.subTest(key=key):
                value = envelope()
                raw = base64.b64decode(value[key])
                padded = raw + b' ' * (1024 * 1024 - len(raw))
                value[key] = b64(padded)
                hashkey = {'PolicyBytes': 'PolicySha256', 'DiffBytes': 'DiffSha256'}.get(key)
                if hashkey: value[hashkey] = fixture.hashlib.sha256(padded).hexdigest()
                self.check(value, 0, 'EVIDENCE_PARENT_GATE_ALLOWED')
                value[key] = b64(padded + b' ')
                if hashkey: value[hashkey] = fixture.hashlib.sha256(padded + b' ').hexdigest()
                self.check(value, 2, 'EVIDENCE_PARENT_GATE_INPUT_REJECTED')

    def test_empty_null_duplicate_fields_and_missing_required_input_reject(self):
        for raw in (b'', b'null', b'{}', fixture.canonical(envelope()) + b' {}'):
            with self.subTest(length=len(raw)):
                self.check(None, 2, 'EVIDENCE_PARENT_GATE_INPUT_REJECTED', raw=raw)
        for key in envelope():
            with self.subTest(missing=key):
                value = envelope(); del value[key]
                self.check(value, 2, 'EVIDENCE_PARENT_GATE_INPUT_REJECTED')
        value = envelope(); value['PolicyBytes'] = None
        self.check(value, 2, 'EVIDENCE_PARENT_GATE_INPUT_REJECTED')
        raw = fixture.canonical(envelope())
        self.check(None, 2, 'EVIDENCE_PARENT_GATE_INPUT_REJECTED', raw=raw[:-1] + b',"schema":"bad"}')

    def test_malformed_protected_json_diff_encoding_and_base64_never_echo(self):
        for key, raw in (('PolicyBytes', b'{"Id":"' + CANARY.encode()), ('DiffBytes', b'\xff'),
                         ('PlanBytes', CANARY.encode()), ('ManifestBytes', CANARY.encode())):
            with self.subTest(key=key):
                value = envelope(); value[key] = b64(raw)
                hashkey = {'PolicyBytes': 'PolicySha256', 'DiffBytes': 'DiffSha256'}.get(key)
                if hashkey: value[hashkey] = fixture.hashlib.sha256(raw).hexdigest()
                self.check(value, 2, 'EVIDENCE_PARENT_GATE_INPUT_REJECTED')
        value = envelope(); value['ManifestBytes'] = CANARY
        self.check(value, 2, 'EVIDENCE_PARENT_GATE_INPUT_REJECTED')


def main():
    global DOTNET, CONSUMER
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dotnet', required=True)
    parser.add_argument('--consumer', type=Path, required=True)
    args = parser.parse_args()
    DOTNET = args.dotnet
    CONSUMER = args.consumer.resolve(strict=True)
    result = unittest.TextTestRunner(verbosity=2).run(unittest.defaultTestLoader.loadTestsFromTestCase(ProtectedParentSemanticGateTests))
    return 0 if result.wasSuccessful() else 1


if __name__ == '__main__':
    sys.exit(main())
