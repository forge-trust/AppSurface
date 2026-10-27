"""Strict report data comparison tests; no gate evaluator is executed here."""
from __future__ import annotations

import json
from pathlib import Path
import sys
import time
import unittest
from dataclasses import replace
from io import BytesIO
import hashlib
import os
import tempfile
import zipfile

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / '.github/issue779-protected-qualification'))
import completed_gate as subject
import completed_run
import completed_validation
BASE = subject.FIXED_DIFF_BASE


def _report(coverage):
    return {
        'passed': True,
        'coverage': coverage,
        'tolerancePercent': 0.5,
        'thresholds': {'line': 95, 'branch': 85, 'patchLine': 95, 'patchBranch': 85},
        'effectiveThresholds': {'line': 94.5, 'branch': 84.5, 'patchLine': 94.5, 'patchBranch': 84.5},
        'patchLineMode': 'codecov',
        'patchDiffSource': {'kind': 'git-base', 'label': BASE, 'strictness': 'local-git',
            'diffBase': BASE, 'path': None, 'bytes': 128, 'sha256': 'b' * 64, 'empty': False},
        'line': {'covered': 95, 'valid': 100, 'percent': 95.0},
        'branch': {'covered': 85, 'valid': 100, 'percent': 85.0},
        'patchLine': {'diffBase': BASE, 'changed': 10, 'measurable': 10, 'covered': 10, 'percent': 100.0},
        'patchBranch': {'diffBase': BASE, 'changed': 10, 'measurable': 10, 'covered': 9, 'percent': 90.0},
    }


class CompletedGateTests(unittest.TestCase):
    def compare(self, left, right, original_path='/original/TestResults/coverage-merged/coverage.cobertura.xml',
                replay_path='/replay/TestResults/coverage-merged/coverage.cobertura.xml'):
        return subject.compare_gate_reports(json.dumps(left, allow_nan=False).encode(),
            json.dumps(right, allow_nan=False).encode(),
            original_role_paths={'coverage': original_path}, replay_role_paths={'coverage': replay_path},
            deadline=time.monotonic() + 5)

    def test_machine_coverage_paths_normalize_only_when_explicitly_bound(self):
        comparison = self.compare(_report('/original/TestResults/coverage-merged/coverage.cobertura.xml'),
                                  _report('/replay/TestResults/coverage-merged/coverage.cobertura.xml'))
        self.assertTrue(comparison.reports_match)
        self.assertFalse(comparison.validator_executed)
        self.assertFalse(comparison.original_gate_success_proven)
        self.assertFalse(comparison.gate_eligible)
        self.assertFalse(comparison.admission_created)

    def test_fixed_git_base_and_no_diff_path_are_required(self):
        left = _report('/original/cov.xml')
        right = _report('/replay/cov.xml')
        paths_original = {'coverage': '/original/cov.xml'}
        paths_replay = {'coverage': '/replay/cov.xml'}
        result = subject.compare_gate_reports(json.dumps(left).encode(), json.dumps(right).encode(),
            original_role_paths=paths_original, replay_role_paths=paths_replay, deadline=time.monotonic() + 5)
        self.assertTrue(result.reports_match)
        for changed in ('label', 'diffBase'):
            left = _report('/original/cov.xml')
            left['patchDiffSource'][changed] = 'a' * 40
            with self.assertRaisesRegex(ValueError, 'source-value'):
                self.compare(left, right, original_path='/original/cov.xml', replay_path='/replay/cov.xml')
        left = _report('/original/cov.xml')
        left['patchDiffSource']['path'] = '/unexpected.diff'
        with self.assertRaisesRegex(ValueError, 'source-value'):
            self.compare(left, right, original_path='/original/cov.xml', replay_path='/replay/cov.xml')

    def test_metric_threshold_path_schema_and_pass_changes_reject(self):
        baseline = _report('/original/cov.xml')
        for label, mutate in (
            ('metric', lambda value: value['line'].__setitem__('percent', 96.0)),
            ('threshold', lambda value: value['thresholds'].__setitem__('line', 94)),
            ('schema', lambda value: value.__setitem__('unknown', True)),
            ('pass', lambda value: value.__setitem__('passed', False)),
            ('coverage-path', lambda value: value.__setitem__('coverage', '/wrong/cov.xml')),
        ):
            candidate = json.loads(json.dumps(baseline))
            if label != 'coverage-path':
                candidate['coverage'] = '/replay/cov.xml'
            mutate(candidate)
            with self.subTest(kind=label), self.assertRaises(ValueError):
                self.compare(baseline, candidate, original_path='/original/cov.xml', replay_path='/replay/cov.xml')

    def test_duplicate_nonfinite_oversized_and_deadline_inputs_reject(self):
        valid = json.dumps(_report('/same/cov.xml')).encode()
        duplicate = valid[:-1] + b',"passed":true}'
        with self.assertRaisesRegex(ValueError, 'report-json'):
            subject.compare_gate_reports(duplicate, valid, original_role_paths={'coverage': '/same/cov.xml'},
                replay_role_paths={'coverage': '/same/cov.xml'}, deadline=time.monotonic() + 5)
        nonfinite = valid.replace(b'"percent": 95.0', b'"percent": NaN', 1)
        with self.assertRaisesRegex(ValueError, 'report-json'):
            subject.compare_gate_reports(nonfinite, valid, original_role_paths={'coverage': '/same/cov.xml'},
                replay_role_paths={'coverage': '/same/cov.xml'}, deadline=time.monotonic() + 5)
        with self.assertRaisesRegex(ValueError, 'report-size'):
            subject.compare_gate_reports(b' ' * (subject.MAX_REPORT_BYTES + 1), valid,
                original_role_paths={'coverage': '/same/cov.xml'}, replay_role_paths={'coverage': '/same/cov.xml'},
                deadline=time.monotonic() + 5)
        with self.assertRaisesRegex(ValueError, 'deadline'):
            subject.compare_gate_reports(valid, valid, original_role_paths={'coverage': '/same/cov.xml'},
                replay_role_paths={'coverage': '/same/cov.xml'}, deadline=time.monotonic() - 1)

    def test_exact_decimal_differences_and_valid_int_decimal_equivalence(self):
        baseline = json.dumps(_report('/same/cov.xml')).encode()
        fractional_change = baseline.replace(b'"percent": 95.0',
            b'"percent": 95.00000000000000000000000001', 1)
        with self.assertRaisesRegex(ValueError, 'report-mismatch'):
            subject.compare_gate_reports(baseline, fractional_change,
                original_role_paths={'coverage': '/same/cov.xml'}, replay_role_paths={'coverage': '/same/cov.xml'},
                deadline=time.monotonic() + 5)
        integer_form = baseline.replace(b'"percent": 95.0', b'"percent": 95', 1)
        result = subject.compare_gate_reports(baseline, integer_form,
            original_role_paths={'coverage': '/same/cov.xml'}, replay_role_paths={'coverage': '/same/cov.xml'},
            deadline=time.monotonic() + 5)
        self.assertTrue(result.reports_match)

    def test_nested_duplicates_huge_numbers_and_invalid_metric_domains_reject(self):
        valid = json.dumps(_report('/same/cov.xml')).encode()
        samples = (
            valid.replace(b'"line": 95,', b'"line": 95, "line": 95,', 1),
            valid.replace(b'"percent": 95.0', b'"percent": 1e9999', 1),
            valid.replace(b'"covered": 95', b'"covered": 999999999999999999999999999999999999999', 1),
            valid.replace(b'"percent": 95.0', b'"percent": -0.01', 1),
            valid.replace(b'"percent": 95.0', b'"percent": 100.01', 1),
            valid.replace(b'"covered": 95', b'"covered": null', 1),
            valid.replace(b'"valid": 100', b'"valid": null', 1),
            valid.replace(b'"covered": 85', b'"covered": null', 1),
            valid.replace(b'"valid": 100', b'"valid": null', 2),
            valid.replace(b'"percent": 95.0', b'"percent": 1e99999999999999999999999999999999999999999999999', 1),
            valid.replace(b'"patchLine": {', b'"patchLine": null', 1),
            valid.replace(BASE.encode(), b'1' * 40, 1),
            b'{malformed',
        )
        for raw in samples:
            with self.subTest(raw=raw[:120]), self.assertRaises(ValueError):
                subject.compare_gate_reports(valid, raw, original_role_paths={'coverage': '/same/cov.xml'},
                    replay_role_paths={'coverage': '/same/cov.xml'}, deadline=time.monotonic() + 5)

    def test_path_role_shape_and_report_subobject_mutations_reject(self):
        baseline = _report('/same/cov.xml')
        changed_reports = []
        for section, key, replacement in (
            ('effectiveThresholds', 'line', 95),
            ('patchDiffSource', 'strictness', 'fail-closed'),
            ('line', 'valid', -1),
            ('branch', 'percent', 101),
            ('patchLine', 'covered', 11),
            ('patchBranch', 'measurable', True),
        ):
            candidate = json.loads(json.dumps(baseline))
            candidate[section][key] = replacement
            changed_reports.append(candidate)
        for candidate in changed_reports:
            with self.subTest(candidate=candidate), self.assertRaises(ValueError):
                self.compare(baseline, candidate, original_path='/same/cov.xml', replay_path='/same/cov.xml')
        with self.assertRaisesRegex(ValueError, 'path-roles'):
            subject.compare_gate_reports(json.dumps(baseline).encode(), json.dumps(baseline).encode(),
                original_role_paths={'coverage': '/same/cov.xml', 'extra': 'x'},
                replay_role_paths={'coverage': '/same/cov.xml'}, deadline=time.monotonic() + 5)

    def test_retained_archive_preflight_returns_bound_inputs_without_authority(self):
        with _ArchiveFixture() as fixture:
            result = subject.inspect_completed_gate_archive(
                fixture.stage, fixture.inventory, fixture.stage_root, fixture.capture,
                deadline=time.monotonic() + 10)
            self.assertEqual(fixture.report, result.original_report)
            self.assertEqual(fixture.capture, result.source_capture)
            self.assertFalse(result.validator_executed)
            self.assertFalse(result.original_gate_success_proven)
            self.assertFalse(result.gate_eligible)
            self.assertFalse(result.admission_created)

    def test_missing_original_gate_report_fails_before_member_extraction(self):
        from unittest.mock import patch
        with _ArchiveFixture(include_report=False) as fixture:
            with patch.object(subject.completed_validation, '_member_bodies') as extract:
                with self.assertRaisesRegex(ValueError, 'required-members'):
                    subject.inspect_completed_gate_archive(
                        fixture.stage, fixture.inventory, fixture.stage_root, fixture.capture,
                        deadline=time.monotonic() + 10)
                extract.assert_not_called()

    def test_archive_projection_raw_binding_source_base_and_report_are_bound(self):
        from unittest.mock import patch
        with _ArchiveFixture() as fixture:
            # Find the coverage archive regardless of inventory ordering.
            name = fixture.artifact_name
            projections = tuple(replace(row, members=()) if row.artifact_name == name else row
                                for row in fixture.inventory.archives)
            bad_inventory = replace(fixture.inventory, archives=projections)
            with self.assertRaisesRegex(ValueError, 'archive-projection'):
                subject.inspect_completed_gate_archive(fixture.stage, bad_inventory, fixture.stage_root,
                    fixture.capture, deadline=time.monotonic() + 10)
            path = fixture.stage_root / (name + '.zip')
            raw = path.read_bytes()
            path.write_bytes(bytes((raw[0] ^ 1,)) + raw[1:])
            with self.assertRaisesRegex(ValueError, 'file-binding'):
                subject.inspect_completed_gate_archive(fixture.stage, fixture.inventory, fixture.stage_root,
                    fixture.capture, deadline=time.monotonic() + 10)
            path.write_bytes(raw)
            with self.assertRaisesRegex(ValueError, 'source-capture-binding'):
                subject.inspect_completed_gate_archive(fixture.stage, fixture.inventory, fixture.stage_root,
                    b'{}', deadline=time.monotonic() + 10)

    def test_selected_member_oversize_and_wrong_base_or_report_reject(self):
        from unittest.mock import patch
        with _ArchiveFixture() as fixture:
            with patch.object(subject.completed_validation, '_member_bodies') as extract:
                with patch.object(subject, 'MAX_GATE_BYTES', 1):
                    with self.assertRaisesRegex(ValueError, 'selected-member-bounds'):
                        subject.inspect_completed_gate_archive(fixture.stage, fixture.inventory,
                            fixture.stage_root, fixture.capture, deadline=time.monotonic() + 10)
                extract.assert_not_called()
        for field, wrong, reason in (('base', b'0' * 40 + b'\n', 'comparison-base'),
                                     ('report-base', None, 'source-value'),
                                     ('report-path', None, 'original-report-coverage-path')):
            with self.subTest(field=field), _ArchiveFixture(**({'comparison_base': wrong}
                    if field == 'base' else {'report_mutation': (
                        ('patchDiffSource', 'diffBase', '0' * 40) if field == 'report-base'
                        else ('top', 'coverage', '/unexpected/coverage.cobertura.xml'))})) as fixture:
                with self.assertRaisesRegex(ValueError, reason):
                    subject.inspect_completed_gate_archive(fixture.stage, fixture.inventory,
                        fixture.stage_root, fixture.capture, deadline=time.monotonic() + 10)

    def test_expired_deadline_rejects_before_archive_read(self):
        with _ArchiveFixture() as fixture:
            with self.assertRaisesRegex(ValueError, 'deadline'):
                subject.inspect_completed_gate_archive(fixture.stage, fixture.inventory,
                    fixture.stage_root, fixture.capture, deadline=time.monotonic() - 1)

    def test_inventory_source_count_must_bind_capture_count(self):
        with _ArchiveFixture() as fixture:
            wrong = replace(fixture.inventory, source_count=2)
            with self.assertRaisesRegex(ValueError, 'source-capture-binding'):
                subject.inspect_completed_gate_archive(fixture.stage, wrong, fixture.stage_root,
                    fixture.capture, deadline=time.monotonic() + 5)

    def test_coverage_archive_transcript_capture_and_report_content_fail_closed(self):
        for overrides, error in (
            ({subject.TRANSCRIPT_MEMBER: b''}, 'selected-member-bounds'),
            ({subject.COVERAGE_MEMBER: b''}, 'selected-member-bounds'),
            ({subject.CAPTURE_MEMBER: b'{}'}, 'coverage-source-capture'),
        ):
            with self.subTest(error=error), _ArchiveFixture(member_overrides=overrides) as fixture:
                with self.assertRaisesRegex(ValueError, error):
                    subject.inspect_completed_gate_archive(fixture.stage, fixture.inventory,
                        fixture.stage_root, fixture.capture, deadline=time.monotonic() + 5)
        with _ArchiveFixture(report_mutation=('top', 'passed', False)) as fixture:
            with self.assertRaisesRegex(ValueError, 'report-not-passing'):
                subject.inspect_completed_gate_archive(fixture.stage, fixture.inventory,
                    fixture.stage_root, fixture.capture, deadline=time.monotonic() + 5)

    def test_coverage_source_projection_and_stage_directory_are_exact(self):
        with _ArchiveFixture() as fixture:
            projections = tuple(replace(row, source_sha256='0' * 64)
                                if row.artifact_name == fixture.artifact_name else row
                                for row in fixture.inventory.archives)
            bad_inventory = replace(fixture.inventory, archives=projections)
            with self.assertRaisesRegex(ValueError, 'archive-source-projection'):
                subject.inspect_completed_gate_archive(fixture.stage, bad_inventory,
                    fixture.stage_root, fixture.capture, deadline=time.monotonic() + 5)
            extra = fixture.stage_root / 'unexpected'
            extra.write_bytes(b'x')
            with self.assertRaisesRegex(ValueError, 'stage-directory-inventory'):
                subject.inspect_completed_gate_archive(fixture.stage, fixture.inventory,
                    fixture.stage_root, fixture.capture, deadline=time.monotonic() + 5)


class _ArchiveFixture:
    """Synthetic complete typed-stage shell with an authentic-shaped coverage archive."""
    def __init__(self, include_report=True, comparison_base=None, report_mutation=None,
                 member_overrides=None):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name).resolve()
        self.stage_root = self.root / 'stage'
        self.stage_root.mkdir(mode=0o700)
        os.chmod(self.stage_root, 0o700)
        self.run_id, attempt, head, tree = 700, 1, 'a' * 40, 'b' * 40
        self.capture = json.dumps({'schema': 'issue779-checkpoint2-source-v1', 'source': head,
            'tree': tree, 'files': {'README.md': {'sha256': 'f' * 64}}, 'count': 1},
            sort_keys=True, separators=(',', ':')).encode()
        capture_sha = hashlib.sha256(self.capture).hexdigest()
        self.artifact_name = f'issue779-checkpoint4-coverage-{self.run_id}-{attempt}'
        report = _report(subject.ORIGINAL_COVERAGE_PATH)
        if report_mutation:
            section, field, value = report_mutation
            (report if section == 'top' else report[section])[field] = value
        self.report = json.dumps(report, separators=(',', ':')).encode() if include_report else None
        selected = {
            subject.CAPTURE_MEMBER: self.capture,
            subject.BASE_MEMBER: comparison_base or (BASE.encode() + b'\n'),
            subject.TRANSCRIPT_MEMBER: b'fixture transcript, no success assertion',
            subject.COVERAGE_MEMBER: b'<coverage />',
        }
        selected.update(member_overrides or {})
        if include_report:
            selected[subject.GATE_MEMBER] = self.report
        stream = BytesIO()
        with zipfile.ZipFile(stream, 'w', compression=zipfile.ZIP_STORED) as archive:
            for name, body in selected.items():
                archive.writestr(name, body)
        raw = stream.getvalue()
        bindings, projections = [], []
        expected_names = completed_run.expected_artifact_names(self.run_id, attempt)
        for index, name in enumerate(expected_names, 1):
            content = raw if name == self.artifact_name else b'x'
            filename = name + '.zip'
            (self.stage_root / filename).write_bytes(content)
            (self.stage_root / filename).chmod(0o600)
            digest = hashlib.sha256(content).hexdigest()
            bindings.append(completed_run.ArtifactBinding(index, name, len(content), digest, filename))
            rows, expanded = (completed_validation._zip_directory(content) if name == self.artifact_name
                              else ((), 0))
            members = tuple(completed_validation.MemberProjection(row[0], row[3], row[4], row[6])
                            for row in rows)
            source_sha = (capture_sha if name.startswith('issue779-checkpoint1-selector-')
                          or name == self.artifact_name else None)
            projections.append(completed_validation.ArchiveProjection(index, name, digest, len(content),
                expanded, members, source_sha))
        self.stage = completed_run.CompletedRunStage(321, self.run_id, attempt, head,
            completed_run.WORKFLOW, (), tree, capture_sha, tuple(range(1, 38)), tuple(bindings),
            sum(row.size for row in bindings))
        aggregate = completed_validation.AggregateProjection('schema', head, capture_sha, (), (), 0, 0, 0, 'c' * 64)
        partial = completed_validation.PartialReceiptProjection(self.run_id, attempt, capture_sha,
            1, 'd' * 64, tuple(range(1, 37)), (), 'e' * 64)
        self.inventory = completed_validation.ParsedCompletedRunInventory(321, self.run_id, attempt,
            head, tree, capture_sha, 1, tuple(projections), aggregate, partial)

    def __enter__(self):
        return self

    def __exit__(self, *_args):
        self.temp.cleanup()


if __name__ == '__main__':
    unittest.main()
