from __future__ import annotations

from dataclasses import replace
import base64
import hashlib
import json
from pathlib import Path
import sys
import stat
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / '.github/issue779-protected-qualification'))
import completed_controller
import completed_orchestration
import completed_run
import completed_source
import completed_validation
import completed_wire as wire
import test_completed_validation


class CompletedWireTests(unittest.TestCase):
    def setUp(self):
        self.fixture = test_completed_validation.CompletedValidationTests()
        self.fixture.setUp()
        self.addCleanup(self.fixture.tearDown)
        self.inventory = self.fixture.inspect()
        f = self.fixture
        required = sorted(set(completed_run.REQUIRED_RUNTIME_JOBS)
                          | completed_run.EXPECTED_TERMINAL_JOBS)
        jobs = tuple(completed_run.CompletedJob(name, index + 1, f.head, f.attempt,
                                               'completed', 'success')
                     for index, name in enumerate(required))
        provider = completed_run.ProviderRunConclusion(
            99, completed_run.REPOSITORY, f.run_id, f.attempt, f.head, 99,
            completed_run.WORKFLOW, completed_run.QUALIFICATION_WORKFLOW_NAME,
            'workflow_dispatch', 'main', 'completed', 'success')
        self.stage = replace(f.stage, jobs=jobs, provider_run=provider)
        binding = completed_source.SourceBinding(
            99, f.run_id, f.attempt, f.head, f.tree, f.capture_sha,
            'c' * 40, 'd' * 40, 'e' * 64)
        paths = (Path(completed_source.ROOT), f.stage_root, Path('/tmp/wire-source'),
                 Path('/tmp/wire-native'))
        roots = tuple((path, (1, i + 2, stat.S_IFDIR, 0o700, 1000, 1000))
                      for i, path in enumerate(paths))
        self.preparation = completed_controller.CompletedPreparation(
            self.stage, binding, self.inventory, f.stage_root, Path('/tmp/wire-source'),
            Path('/tmp/wire-native'), roots)

    def _replay(self):
        phases = []
        for phase in completed_orchestration.PHASES:
            raw = json.dumps({'phase': phase, 'run_id': self.stage.run_id},
                             sort_keys=True, separators=(',', ':')).encode()
            phases.append(completed_orchestration.PhaseObservation(
                phase, hashlib.sha256(raw).hexdigest(), raw))
        return completed_orchestration.CompletedRunReplay(
            self.stage.run_id, self.stage.run_attempt, self.stage.head_sha, self.stage.source_tree,
            self.stage.source_capture_sha256, tuple(phases), Path('/tmp/native'),
            Path('/tmp/gate'), 88)

    def test_preparation_roundtrip_preserves_full_37_archive_inventory(self):
        encoded = wire.encode_preparation(self.preparation)
        decoded = wire.decode_preparation(encoded)
        self.assertEqual(self.stage, decoded.stage)
        self.assertEqual(self.inventory, decoded.inventory)
        self.assertEqual(self.preparation.root_identities, decoded.root_identities)
        self.assertEqual(37, len(decoded.inventory.archives))
        self.assertEqual(encoded, wire.encode_preparation(decoded))

    def test_replay_roundtrip_preserves_projection_bytes_and_false_claims(self):
        replay = self._replay()
        raw = wire.encode_replay(replay)
        decoded = wire.decode_replay(raw)
        self.assertEqual(replay.phases, decoded.phases)
        self.assertEqual(replay, decoded)
        self.assertTrue(all(getattr(decoded, name) is False for name in wire._FALSE
                            if hasattr(decoded, name)))
        self.assertEqual(raw, wire.encode_replay(decoded))

    def test_rejects_duplicate_noncanonical_nonfinite_and_oversize(self):
        for raw in (b'{"schema":"x","schema":"y"}', b'{ "schema" : "x" }',
                    b'{"schema":"x","a":NaN}', b' ' * (wire.MAX_WIRE_BYTES + 1)):
            with self.subTest(raw=raw[:32]), self.assertRaises(ValueError):
                wire.decode_preparation(raw)
        with self.assertRaisesRegex(ValueError, 'json'):
            wire.decode_preparation(b'{"schema":"x","SCHEMA":"x"}')
        with self.assertRaisesRegex(ValueError, 'value-type'):
            wire.decode_replay(b'{"schema":"issue779-completed-replay-v1","number":1.25}')

    def test_rejects_claim_mutation_and_tampered_root_identity(self):
        value = json.loads(wire.encode_preparation(self.preparation))
        value['source_binding']['gate_eligible'] = True
        with self.assertRaisesRegex(ValueError, 'authority-claim'):
            wire.decode_preparation(wire._dump(value))
        value = json.loads(wire.encode_preparation(self.preparation))
        value['root_identities'][0][1][1] = True
        with self.assertRaisesRegex(ValueError, 'integer'):
            wire.decode_preparation(wire._dump(value))

    def test_rejects_preparation_identity_and_inventory_drift(self):
        value = json.loads(wire.encode_preparation(self.preparation))
        value['inventory']['source_capture_sha256'] = 'f' * 64
        with self.assertRaisesRegex(ValueError, 'inventory-match'):
            wire.decode_preparation(wire._dump(value))
        value = json.loads(wire.encode_preparation(self.preparation))
        value['stage_directory'] = 'relative/path'
        with self.assertRaisesRegex(ValueError, 'path'):
            wire.decode_preparation(wire._dump(value))

    def test_rejects_provider_drift_legacy_stage_and_boolean_ids(self):
        value = json.loads(wire.encode_preparation(self.preparation))
        value['stage']['provider_run']['run_id'] += 1
        with self.assertRaisesRegex(ValueError, 'provider-conclusion-fields'):
            wire.decode_preparation(wire._dump(value))
        value = json.loads(wire.encode_preparation(self.preparation))
        value['stage']['provider_run'] = None
        with self.assertRaisesRegex(ValueError, 'provider-conclusion-missing'):
            wire.decode_preparation(wire._dump(value))
        value = json.loads(wire.encode_preparation(self.preparation))
        value['stage']['run_id'] = True
        with self.assertRaisesRegex(ValueError, 'integer'):
            wire.decode_preparation(wire._dump(value))

    def test_rejects_digest_grammar_and_complexity_overflow(self):
        value = json.loads(wire.encode_preparation(self.preparation))
        value['stage']['head_sha'] = 'z' * 40
        with self.assertRaisesRegex(ValueError, 'digest'):
            wire.decode_preparation(wire._dump(value))
        nested = '[' * (wire.MAX_DEPTH + 1) + '0' + ']' * (wire.MAX_DEPTH + 1)
        raw = b'{"schema":"issue779-completed-replay-v1","unexpected":' + nested.encode() + b'}'
        with self.assertRaisesRegex(ValueError, 'complexity'):
            wire.decode_replay(raw)
        values = b'0,' * wire.MAX_NODES + b'0'
        raw = b'{"schema":"issue779-completed-replay-v1","unexpected":[' + values + b']}'
        with self.assertRaisesRegex(ValueError, 'complexity'):
            wire.decode_replay(raw)

    def test_rejects_path_controls_and_unsafe_archive_member(self):
        value = json.loads(wire.encode_preparation(self.preparation))
        value['source_root'] = '/tmp/bad\x00path'
        with self.assertRaisesRegex(ValueError, 'path'):
            wire.decode_preparation(wire._dump(value))
        value = json.loads(wire.encode_preparation(self.preparation))
        value['inventory']['archives'][0]['members'][0]['path'] = '../escape'
        with self.assertRaisesRegex(ValueError, 'member-name'):
            wire.decode_preparation(wire._dump(value))

    def test_rejects_replay_phase_mutation_wrong_order_and_claim(self):
        value = json.loads(wire.encode_replay(self._replay()))
        value['phases'][0]['projection_base64'] = base64.b64encode(b'{}').decode()
        with self.assertRaisesRegex(ValueError, 'phase-digest'):
            wire.decode_replay(wire._dump(value))
        value = json.loads(wire.encode_replay(self._replay()))
        value['phases'][0], value['phases'][1] = value['phases'][1], value['phases'][0]
        with self.assertRaisesRegex(ValueError, 'phase-order'):
            wire.decode_replay(wire._dump(value))
        value = json.loads(wire.encode_replay(self._replay()))
        value['native_acceptance'] = True
        with self.assertRaisesRegex(ValueError, 'authority-claim'):
            wire.decode_replay(wire._dump(value))
        for claim in ('trusted_eligibility', 'all_sixteen_claimed'):
            value = json.loads(wire.encode_replay(self._replay()))
            phase = json.dumps({claim: True, 'phase': 'c1'},
                               sort_keys=True, separators=(',', ':')).encode()
            value['phases'][0]['projection_base64'] = base64.b64encode(phase).decode()
            value['phases'][0]['projection_sha256'] = hashlib.sha256(phase).hexdigest()
            with self.subTest(claim=claim), self.assertRaisesRegex(ValueError, 'authority-claim'):
                wire.decode_replay(wire._dump(value))

    def test_rejects_phase_base64_and_noncanonical_projection(self):
        replay = self._replay()
        bad_raw = b'{ "not": "canonical" }'
        phases = list(replay.phases)
        phases[0] = replace(phases[0], projection_sha256=hashlib.sha256(bad_raw).hexdigest(),
                            projection_json=bad_raw)
        with self.assertRaisesRegex(ValueError, 'phase-canonical'):
            wire.encode_replay(replace(replay, phases=tuple(phases)))
        value = json.loads(wire.encode_replay(self._replay()))
        value['phases'][0]['projection_base64'] = '!!!!'
        value['phases'][0]['projection_sha256'] = hashlib.sha256(b'{}').hexdigest()
        with self.assertRaisesRegex(ValueError, 'phase-base64'):
            wire.decode_replay(wire._dump(value))
        value = json.loads(wire.encode_replay(self._replay()))
        encoded = value['phases'][0]['projection_base64']
        alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/'
        alternative = next(candidate for character in alphabet
                           if character != encoded[-2]
                           for candidate in (encoded[:-2] + character + encoded[-1:],)
                           if base64.b64decode(candidate) == base64.b64decode(encoded))
        value['phases'][0]['projection_base64'] = alternative
        with self.assertRaisesRegex(ValueError, 'phase-base64-canonical'):
            wire.decode_replay(wire._dump(value))


if __name__ == '__main__':
    unittest.main()
