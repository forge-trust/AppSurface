"""Ordinary retained-file controls. Fixture bytes provide no installation or native proof."""
from pathlib import Path
from unittest import mock
import hashlib
import json
import os
import stat
import tempfile
import unittest

import package_smoke_evidence as subject

SOURCE = 'a' * 40


class PackageSmokeEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.work = self.root / 'work'
        self.work.mkdir(mode=0o700)
        self.output = self.root / 'retained'
        self.bodies = {name: b'' for name in subject.OUTPUTS}
        for name, diagnostic in (
                ('unsupported-owner', b'ASEVD402'), ('omitted-mode', b'ASEVD401'),
                ('evidence-starter-repeat', b'ASEVD202'),
                ('evidence-observation-without-control', b'ASEVD402'),
                ('evidence-legacy-observation-without-control', b'ASEVD402'),
                ('evidence-conflicting-mode', b'ASEVD401')):
            self.bodies[name + '.log'] = diagnostic + b'\n'
        self.bodies['evidence-doctor.log'] = (
            b'unverified trusted-envelope: do not grant admission; issue779-consumer-acceptance.md\n')
        self.bodies['evidence-explain.log'] = (
            b'Evidence plan: targeted-coverage (targeted)\nRequired producers: coverage\n'
            b'Required resources: none\nchanged-behavior-covered\n')
        self.bodies['sdk-load.log'] = b'\n'.join((
            *subject.SDK_LOAD_MARKERS, subject.SDK_LOAD_COMPLETION)) + b'\n'
        self.bodies['starter/evidence.policy.json'] = b'{"generatedBy":"appsurface-evidence-starter-v1"}'
        self.bodies['starter/EvidenceHost.cs'] = b'// actual file fixture\n'
        self.bodies['starter/README.md'] = b'# Starter\n'
        self.bodies['planning/evidence-plan.json'] = b'{"profile":{"id":"targeted-coverage"}}'
        self.bodies['planning/evidence-summary.json'] = json.dumps({
            'kind': 'plan', 'profile': 'targeted-coverage', 'obligations': ['changed-behavior-covered'],
            'next': 'Do not use this plan as gate evidence.'}).replace('Do not', 'do not').encode()
        self.receipt = {
            'schema': 'issue779-packaged-consumer-v1', 'source': SOURCE, 'exit': 0,
            'protected_execution': False, 'trusted_acceptance': False,
            'commands': [{'name': name, 'argv': ['fixture', name],
                          'exit': int(name in subject.REJECTIONS),
                          'log_sha256': self.sha(self.bodies[name + '.log'])} for name in subject.COMMANDS],
            'planning_journey': {
                'entry': 'installed-nuget-tool', 'producer_execution': False, 'trusted_acceptance': False,
                'independent_human_usability': False,
                'starter_sha256': {name: self.sha(self.bodies['starter/' + name]) for name in subject.STARTER},
                'plan_sha256': self.sha(self.bodies['planning/evidence-plan.json']),
                'summary_sha256': self.sha(self.bodies['planning/evidence-summary.json']),
            },
        }
        self.save()

    @staticmethod
    def sha(raw):
        return hashlib.sha256(raw).hexdigest()

    def save(self):
        self.bodies['receipt.json'] = json.dumps(self.receipt).encode()
        for name, original in subject.OUTPUTS.items():
            path = self.work / original
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(self.bodies[name])

    def reject(self):
        with self.assertRaises((ValueError, OSError)):
            subject.retain(self.work, self.output, SOURCE)
        self.assertFalse(self.output.exists())

    def test_real_files_roundtrip_includes_empty_logs_and_only_fixed_outputs(self):
        subject.retain(self.work, self.output, SOURCE)
        self.assertEqual(self.receipt, subject.verify(self.output, SOURCE))
        self.assertEqual(28, len(subject.OUTPUTS))
        for name, expected in self.bodies.items():
            self.assertEqual(expected, (self.output / name).read_bytes())
            self.assertEqual(0o600, stat.S_IMODE((self.output / name).stat().st_mode))
        self.assertEqual(0o700, stat.S_IMODE(self.output.stat().st_mode))

    def test_missing_actual_empty_log_rejects_before_creating_destination(self):
        (self.work / 'sdk-build.log').unlink()
        self.reject()

    def test_changed_log_and_generated_file_reject_without_echo_or_copy(self):
        for name in ('sdk-load.log', 'starter/EvidenceHost.cs', 'planning/evidence-plan.json'):
            with self.subTest(name=name):
                path = self.work / subject.OUTPUTS[name]
                original = path.read_bytes()
                path.write_bytes(b'private-canary')
                try:
                    self.reject()
                finally:
                    path.write_bytes(original)

    def test_missing_duplicate_reordered_or_wrong_command_decisions_reject(self):
        original = json.loads(json.dumps(self.receipt))
        for operation in ('missing', 'duplicate', 'reorder', 'wrong-exit', 'bool-exit', 'wrong-source'):
            with self.subTest(operation=operation):
                self.receipt = json.loads(json.dumps(original))
                commands = self.receipt['commands']
                if operation == 'missing': commands.pop()
                if operation == 'duplicate': commands[-1] = commands[0]
                if operation == 'reorder': commands.reverse()
                if operation == 'wrong-exit': commands[0]['exit'] = 1
                if operation == 'bool-exit': commands[0]['exit'] = False
                if operation == 'wrong-source': self.receipt['source'] = 'b' * 40
                self.save()
                self.reject()

    def test_coherent_log_hash_does_not_bypass_required_diagnostic(self):
        self.bodies['evidence-doctor.log'] = b'private-canary'
        self.receipt['commands'][8]['log_sha256'] = self.sha(self.bodies['evidence-doctor.log'])
        self.save()
        self.reject()

    def test_missing_each_packaged_migration_marker_rejects_before_copy(self):
        original = self.bodies['sdk-load.log'].splitlines()
        command = next(item for item in self.receipt['commands'] if item['name'] == 'sdk-load')
        for marker in subject.SDK_LOAD_MARKERS:
            with self.subTest(marker=marker):
                self.bodies['sdk-load.log'] = b'\n'.join(line for line in original if line != marker) + b'\n'
                command['log_sha256'] = self.sha(self.bodies['sdk-load.log'])
                self.save()
                self.reject()

    def test_substituted_each_packaged_migration_marker_rejects_before_copy(self):
        original = self.bodies['sdk-load.log'].splitlines()
        command = next(item for item in self.receipt['commands'] if item['name'] == 'sdk-load')
        for marker in subject.SDK_LOAD_MARKERS:
            with self.subTest(marker=marker):
                replacement = marker.replace(b'callbacks=0', b'callbacks=1')
                self.bodies['sdk-load.log'] = b'\n'.join(
                    replacement if line == marker else line for line in original) + b'\n'
                command['log_sha256'] = self.sha(self.bodies['sdk-load.log'])
                self.save()
                self.reject()

    def test_claim_flags_and_coherent_execution_summary_reject(self):
        self.receipt['protected_execution'] = True
        self.save()
        self.reject()
        self.receipt['protected_execution'] = False
        self.bodies['planning/evidence-summary.json'] = b'{"kind":"run","profile":"targeted-coverage"}'
        self.receipt['planning_journey']['summary_sha256'] = self.sha(self.bodies['planning/evidence-summary.json'])
        self.save()
        self.reject()

    def test_symlink_hardlink_fifo_and_symlink_parent_reject_without_outside_change(self):
        path = self.work / 'sdk-build.log'
        sentinel = self.root / 'sentinel'
        sentinel.write_bytes(b'outside')
        for kind in ('symlink', 'hardlink', 'fifo'):
            with self.subTest(kind=kind):
                path.unlink()
                if kind == 'symlink': path.symlink_to(sentinel)
                if kind == 'hardlink': os.link(sentinel, path)
                if kind == 'fifo': os.mkfifo(path)
                self.reject()
                self.assertEqual(b'outside', sentinel.read_bytes())
                path.unlink()
                path.write_bytes(b'')
        starter = self.work / 'planning-consumer/.appsurface/evidence'
        moved = starter.with_name('actual')
        starter.rename(moved)
        starter.symlink_to(moved, target_is_directory=True)
        self.reject()

    def test_per_file_and_aggregate_bounds_reject_before_copy(self):
        path = self.work / 'sdk-build.log'
        with path.open('wb') as stream:
            stream.truncate(subject.LEAF_LIMIT + 1)
        self.reject()
        self.save()
        with mock.patch.object(subject, 'TOTAL_LIMIT', sum(map(len, self.bodies.values())) - 1):
            self.reject()

    def test_duplicate_json_and_concurrent_leaf_change_reject(self):
        (self.work / 'receipt.json').write_bytes(b'{"exit":0,"exit":0}')
        self.reject()
        self.save()
        real_read = os.read
        changed = False

        def mutate(fd, size):
            nonlocal changed
            raw = real_read(fd, size)
            if raw and not changed:
                changed = True
                (self.work / 'tool-pack.log').write_bytes(b'changed')
            return raw

        # Ensure the selected first stream has bytes so mutation occurs during its read.
        self.bodies['tool-pack.log'] = b'initial'
        self.receipt['commands'][0]['log_sha256'] = self.sha(b'initial')
        self.save()
        with mock.patch.object(subject.os, 'read', side_effect=mutate):
            self.reject()

    def test_retained_mutation_extra_nodes_private_modes_and_occupied_output_reject(self):
        subject.retain(self.work, self.output, SOURCE)
        with self.assertRaises(FileExistsError):
            subject.retain(self.work, self.output, SOURCE)
        extra = self.output / 'extra'
        extra.mkdir(mode=0o700)
        with self.assertRaises(ValueError): subject.verify(self.output, SOURCE)
        extra.rmdir()
        path = self.output / 'sdk-build.log'
        path.chmod(0o644)
        with self.assertRaises(ValueError): subject.verify(self.output, SOURCE)
        path.chmod(0o600)
        path.write_bytes(b'mutated')
        with self.assertRaises(ValueError): subject.verify(self.output, SOURCE)

    def test_inventory_added_during_validation_rejects_before_success(self):
        subject.retain(self.work, self.output, SOURCE)
        original_validate = subject.validate

        def add_after_reads(bodies, source):
            result = original_validate(bodies, source)
            (self.output / 'late-extra').write_bytes(b'not-in-fixed-inventory')
            return result

        with mock.patch.object(subject, 'validate', side_effect=add_after_reads):
            with self.assertRaises(ValueError):
                subject.verify(self.output, SOURCE)


if __name__ == '__main__':
    unittest.main()
