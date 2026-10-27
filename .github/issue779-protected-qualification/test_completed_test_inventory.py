"""Portable tests for retained test-inventory inspection; no runtime is launched."""
from __future__ import annotations

from pathlib import Path
import sys
import hashlib
import io
import json
import os
import stat
import time
import unittest
import zipfile
from copy import deepcopy
from dataclasses import replace
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / '.github/issue779-protected-qualification'))
import completed_test_inventory as subject
import completed_run
import completed_validation
import test_completed_validation as validation_fixture


def _zip(members):
    output = io.BytesIO()
    with zipfile.ZipFile(output, 'w', compression=zipfile.ZIP_STORED) as archive:
        for name, body in members.items():
            info = zipfile.ZipInfo(name)
            info.create_system = 3
            info.external_attr = (stat.S_IFREG | 0o600) << 16
            archive.writestr(info, body)
    return output.getvalue()


def _compact_json(value):
    return json.dumps(value, sort_keys=True, separators=(',', ':')).encode()


class _PublicFixture:
    """Typed-stage fixture; source bootstrap is mocked, archive custody is exercised."""
    project = 'Unit/Alpha.Tests.csproj'
    slug = None
    junit_name = None
    junit_body = b'<testsuite tests="1" failures="0" errors="0" skipped="0"><testcase name="ok" /></testsuite>'
    log_body = b'opaque retained project log\n'
    solution = b'<Solution><Project Path="Unit/Alpha.Tests.csproj" /></Solution>'
    coverage = b'<coverage line-rate="1" branch-rate="1" />\n'

    def __init__(self):
        self.base = validation_fixture.CompletedValidationTests()
        self.base.setUp()
        self.source_root = Path(self.base.temp.name).resolve() / 'source'
        self.source_root.mkdir()
        (self.source_root / 'ForgeTrust.AppSurface.slnx').write_bytes(self.solution)
        self.slug = subject._project_slug(self.project, self.source_root)
        self.junit_name = f'junit-coverage-1-{self.slug}.xml'
        self.capture_value = json.loads(self.base.capture)
        self.capture_value['files']['ForgeTrust.AppSurface.slnx'] = {
            'sha256': hashlib.sha256(self.solution).hexdigest(), 'git_blob': 'c' * 40, 'mode': '0644'}
        self.capture_value['count'] += 1
        self.capture = _compact_json(self.capture_value)
        self.capture_sha = hashlib.sha256(self.capture).hexdigest()
        self.timings = self._timings()
        coverage_name = f'issue779-checkpoint4-coverage-{self.base.run_id}-{self.base.attempt}'
        old_inventory = self.base.inspect()
        old_projections = {item.artifact_name: item for item in old_inventory.archives}
        new_bindings = []
        new_projections = []
        self.coverage_name = coverage_name
        for old in self.base.stage.artifacts:
            path = self.base.stage_root / old.staged_filename
            with zipfile.ZipFile(io.BytesIO(path.read_bytes()), 'r') as archive:
                members = {item.filename: archive.read(item) for item in archive.infolist()}
            if old.name.startswith('issue779-checkpoint4-coverage-'):
                members = {name: body for name, body in members.items()
                           if name != 'source.json' and name != subject.CAPTURE_MEMBER}
                members.update({
                    subject.CAPTURE_MEMBER: self.capture,
                    subject.TIMINGS_MEMBER: _compact_json(self.timings),
                    subject.COBERTURA_MEMBER: self.coverage,
                    subject.JUNIT_PREFIX + self.junit_name.removeprefix('junit-coverage-'): self.junit_body,
                    subject.PROJECT_LOG_PREFIX + self.slug + '/dotnet-test.log': self.log_body,
                })
            elif 'source.json' in members:
                members['source.json'] = self.capture
            raw = _zip(members)
            path.write_bytes(raw)
            os.chmod(path, 0o600)
            binding = replace(old, size=len(raw), sha256=hashlib.sha256(raw).hexdigest())
            new_bindings.append(binding)
            rows, expanded = completed_validation._zip_directory(raw)
            has_capture = subject.CAPTURE_MEMBER in members or 'source.json' in members
            projection = completed_validation.ArchiveProjection(binding.artifact_id, binding.name,
                binding.sha256, len(raw), expanded,
                tuple(completed_validation.MemberProjection(row[0], row[3], row[4], row[6])
                      for row in rows), self.capture_sha if has_capture else None)
            new_projections.append(projection)
        self.stage = replace(self.base.stage, source_capture_sha256=self.capture_sha,
            artifacts=tuple(new_bindings), total_archive_bytes=sum(item.size for item in new_bindings))
        self.inventory = replace(old_inventory, source_capture_sha256=self.capture_sha,
            source_count=self.capture_value['count'], archives=tuple(new_projections))

    def _timings(self):
        project = self.project
        junit_name = self.junit_name
        slug = self.slug
        source = self.source_root
        return {
            'solution': str(source / 'ForgeTrust.AppSurface.slnx'),
            'configuration': 'Release', 'coverageDriver': 'collector', 'buildSolution': True,
            'parallelism': 2,
            'schedule': {'mode': 'input-order',
                'timingSource': {'kind': 'none', 'path': None, 'status': 'not-used'}, 'warnings': []},
            'durations': {'solutionBuildSeconds': 1, 'coverageMergeSeconds': 1, 'totalSeconds': 2},
            'merge': {'exitCode': 0},
            'artifacts': {'coverageFiles': 1, 'cobertura': 'coverage.cobertura.xml', 'junitFiles': 1,
                'testResults': [{'format': 'junit', 'project': project, 'path': junit_name,
                                 'parserStatus': 'parsed'}],
                'diagnostics': {'schemaVersion': 1, 'markdown': '/tmp/slow.md', 'json': '/tmp/slow.json'}},
            'diagnostics': {'slowTests': {'warningCount': 0, 'metadataComplete': True,
                'aggregationSeconds': 0, 'aggregationPercent': 0, 'markdown': '/tmp/slow.md',
                'json': '/tmp/slow.json'}},
            'projects': [{'project': project, 'slug': slug, 'originalIndex': 0, 'executionIndex': 0,
                'executionStatus': 'completed', 'scheduledSeconds': 1, 'durationSource': 'none',
                'scheduleReason': 'input-order', 'seconds': 1, 'exitCode': 0, 'exclusive': False,
                'log': f'projects/{slug}/dotnet-test.log', 'coverageDriver': 'collector',
                'invocationDirectory': f'projects/{slug}/results', 'hangDiagnostics': None,
                'coverageArtifactStatus': 'produced', 'coverageArtifactCause': None,
                'coverageCleanupLog': None, 'coverageCleanupDiagnostic': None,
                'coverageFile': f'projects/{slug}/coverage.cobertura.xml',
                'testResults': [{'format': 'junit', 'path': junit_name, 'parserStatus': 'parsed'}]}],
        }

    def inspect(self, capture=None):
        source_raw = self.capture if capture is None else capture
        with patch.object(subject, 'SOURCE_MOUNT', self.source_root), \
             patch.object(subject.completed_c1, '_load_trusted_current_binding', return_value=object()), \
             patch.object(subject.completed_c1, '_load_capture_and_binding',
                          return_value=(self.capture_value, object())), \
             patch.object(subject.completed_c1, '_read_trusted_source_file', return_value=self.solution):
            return subject.inspect_completed_test_inventory(self.stage, self.inventory,
                self.base.stage_root, source_raw, self.source_root, deadline=time.monotonic() + 20)

    def close(self):
        self.base.tearDown()


class CompletedTestInventoryTests(unittest.TestCase):
    def test_solution_selection_matches_current_project_name_rule(self):
        raw = b'''<Solution>
          <Folder Name="/tests/">
            <Project Path="A/Alpha.Tests.csproj" />
            <Project Path="B/Beta.IntegrationTests.csproj" />
            <Project Path="C/EndsTests.csproj" />
            <Project Path="D/NotATest.csproj" />
            <Project Path="E/Lower.tests.csproj" />
          </Folder>
        </Solution>'''
        projects = subject._test_projects(raw, time.monotonic() + 3)
        self.assertEqual(('A/Alpha.Tests.csproj', 'B/Beta.IntegrationTests.csproj',
                          'C/EndsTests.csproj'), projects)

    def test_checked_in_solution_discovers_the_current_59_projects(self):
        raw = (ROOT / 'ForgeTrust.AppSurface.slnx').read_bytes()
        projects = subject._test_projects(raw, time.monotonic() + 5)
        self.assertEqual(59, len(projects))
        self.assertEqual('ForgeTrust.AppSurface.Auth.Tests-5279b90a',
            subject._project_slug('Auth/ForgeTrust.AppSurface.Auth.Tests.csproj',
                                  Path('/home/runner/work/AppSurface/AppSurface')))

    def test_solution_rejects_duplicate_unsafe_and_active_xml_constructs(self):
        deadline = time.monotonic() + 3
        for raw in (
            b'<Solution><Project Path="a/A.Tests.csproj"/><Project Path="a/A.Tests.csproj"/></Solution>',
            b'<Solution><Project Path="../A.Tests.csproj"/></Solution>',
            b'<!DOCTYPE Solution [<!ENTITY x "boom">]><Solution><Project Path="&x;.csproj"/></Solution>',
            b'<NotSolution><Project Path="a/A.Tests.csproj"/></NotSolution>',
        ):
            with self.subTest(raw=raw), self.assertRaises(ValueError):
                subject._test_projects(raw, deadline)

    def test_junit_counts_and_skips_are_structurally_parsed(self):
        raw = b'''<testsuites tests="3" failures="0" errors="0" skipped="1">
          <testsuite name="one" tests="2" failures="0" errors="0" skipped="1">
            <testcase classname="A" name="works" />
            <testcase classname="A" name="ignored"><skipped /></testcase>
          </testsuite>
          <testsuite name="two" tests="1" failures="0" errors="0" skipped="0">
            <testcase classname="B" name="also-works" />
          </testsuite>
        </testsuites>'''
        self.assertEqual((3, 1), subject._parse_junit(raw, time.monotonic() + 3))

    def test_junit_rejects_failure_error_count_mismatch_duplicates_and_empty(self):
        deadline = time.monotonic() + 3
        cases = (
            b'<testsuite tests="1" failures="0"><testcase><failure/></testcase></testsuite>',
            b'<testsuite tests="1"><testcase><error/></testcase></testsuite>',
            b'<testsuite tests="1"><testcase><skipped/><failure/></testcase></testsuite>',
            b'<testsuite/>',
            b'<!DOCTYPE x [<!ENTITY e "x">]><testsuite><testcase/></testsuite>',
            b'<testsuite><vendor><testcase name="hidden"><failure /></testcase></vendor></testsuite>',
            '<?xml version="1.0" encoding="UTF-16"?><testsuite><testcase /></testsuite>'.encode('utf-16'),
        )
        for raw in cases:
            with self.subTest(raw=raw), self.assertRaises(ValueError):
                subject._parse_junit(raw, deadline)

    def test_timing_contract_requires_every_discovered_project_to_pass(self):
        source = subject.SOURCE_MOUNT
        project = 'Unit/Alpha.Tests.csproj'
        slug = subject._project_slug(project, source)
        junit = f'junit-coverage-1-{slug}.xml'
        data = {
            'solution': str(source / 'ForgeTrust.AppSurface.slnx'),
            'configuration': 'Release', 'coverageDriver': 'collector', 'buildSolution': True,
            'parallelism': 2,
            'schedule': {'mode': 'input-order',
                         'timingSource': {'kind': 'none', 'path': None, 'status': 'not-used'},
                         'warnings': []},
            'durations': {'solutionBuildSeconds': 1, 'coverageMergeSeconds': 1, 'totalSeconds': 2},
            'merge': {'exitCode': 0},
            'artifacts': {'coverageFiles': 1, 'cobertura': 'coverage.cobertura.xml', 'junitFiles': 1,
                          'testResults': [{'format': 'junit', 'project': project, 'path': junit,
                                           'parserStatus': 'parsed'}],
                          'diagnostics': {'schemaVersion': 1, 'markdown': '/tmp/a.md', 'json': '/tmp/a.json'}},
            'diagnostics': {'slowTests': {'warningCount': 0, 'metadataComplete': True,
                'aggregationSeconds': 0, 'aggregationPercent': 0, 'markdown': '/tmp/a.md', 'json': '/tmp/a.json'}},
            'projects': [{'project': project, 'slug': slug, 'originalIndex': 0, 'executionIndex': 0,
                'executionStatus': 'completed', 'scheduledSeconds': 1, 'durationSource': 'none',
                'scheduleReason': 'input-order', 'seconds': 1, 'exitCode': 0, 'exclusive': False,
                'log': f'projects/{slug}/dotnet-test.log', 'coverageDriver': 'collector',
                'invocationDirectory': 'projects/{}/results'.format(slug), 'hangDiagnostics': None,
                'coverageArtifactStatus': 'produced', 'coverageArtifactCause': None,
                'coverageCleanupLog': None, 'coverageCleanupDiagnostic': None,
                'coverageFile': f'projects/{slug}/coverage.cobertura.xml',
                'testResults': [{'format': 'junit', 'path': junit, 'parserStatus': 'parsed'}]}],
        }
        rows = subject._validate_timings(data, (project,), source)
        self.assertEqual((0, 0, slug, f'projects/{slug}/dotnet-test.log', junit), rows[project])
        wrong_slug = deepcopy(data)
        wrong_slug['projects'][0]['slug'] = slug[:-1] + ('0' if slug[-1] != '0' else '1')
        with self.assertRaisesRegex(ValueError, 'timings-project-slug'):
            subject._validate_timings(wrong_slug, (project,), source)
        reordered = deepcopy(data)
        beta_slug = subject._project_slug('Unit/Beta.Tests.csproj', source)
        second = deepcopy(reordered['projects'][0])
        second.update({'project': 'Unit/Beta.Tests.csproj', 'slug': beta_slug,
                       'originalIndex': 1, 'executionIndex': 0,
                       'log': f'projects/{beta_slug}/dotnet-test.log',
                       'invocationDirectory': f'projects/{beta_slug}/results',
                       'coverageFile': f'projects/{beta_slug}/coverage.cobertura.xml',
                       'testResults': [{'format': 'junit', 'path': f'junit-coverage-2-{beta_slug}.xml',
                                        'parserStatus': 'parsed'}]})
        reordered['projects'][0]['executionIndex'] = 1
        reordered['projects'].append(second)
        reordered['artifacts']['coverageFiles'] = reordered['artifacts']['junitFiles'] = 2
        reordered['artifacts']['testResults'].append({'format': 'junit',
            'project': 'Unit/Beta.Tests.csproj', 'path': f'junit-coverage-2-{beta_slug}.xml',
            'parserStatus': 'parsed'})
        rows = subject._validate_timings(reordered,
            (project, 'Unit/Beta.Tests.csproj'), source)
        self.assertEqual((0, 1, slug, f'projects/{slug}/dotnet-test.log', junit), rows[project])
        reordered['projects'][0]['exitCode'] = 1
        with self.assertRaises(ValueError):
            subject._validate_timings(reordered, (project, 'Unit/Beta.Tests.csproj'), source)

    def test_public_entry_rejects_untyped_stage_without_source_or_filesystem(self):
        with self.assertRaisesRegex(ValueError, 'stage-type'):
            subject.inspect_completed_test_inventory(object(), object(), Path('/nonexistent'), b'{}',
                subject.SOURCE_MOUNT, deadline=time.monotonic() + 2)

    def test_junit_count_bounds_reject_before_unbounded_tree_walk(self):
        with self.assertRaisesRegex(ValueError, 'junit-case-bound'):
            subject._parse_junit(b'<testsuite>' + b'<testcase/>' *
                (subject.MAX_JUNIT_CASES_PER_FILE + 1) + b'</testsuite>', time.monotonic() + 10)
        with self.assertRaisesRegex(ValueError, 'junit-size'):
            subject._parse_junit(b' ' * (subject.MAX_JUNIT_BYTES + 1), time.monotonic() + 3)

    def test_result_defaults_do_not_claim_run_or_workflow_success(self):
        self.assertFalse(subject.CompletedTestInventory.__dataclass_fields__[
            'original_tests_success_proven'].default)
        self.assertFalse(subject.CompletedTestInventory.__dataclass_fields__[
            'original_workflow_success_proven'].default)

    def test_public_api_accepts_coherent_synthetic_archive_without_authority(self):
        fixture = _PublicFixture()
        try:
            result = fixture.inspect()
            self.assertEqual(1, result.project_count)
            self.assertEqual(1, result.test_case_count)
            self.assertEqual(0, result.skipped_case_count)
            self.assertEqual(fixture.junit_name, result.projects[0].junit_path)
            self.assertTrue(result.retained_project_inventory_verified)
            self.assertFalse(result.original_tests_success_proven)
            self.assertFalse(result.original_merge_success_proven)
            self.assertFalse(result.original_workflow_success_proven)
            self.assertFalse(result.gate_eligible)
            self.assertFalse(result.admission_created)
            self.assertFalse(result.qualification_complete)
        finally:
            fixture.close()

    def test_public_api_rejects_missing_junit_and_nonzero_project_exit(self):
        fixture = _PublicFixture()
        try:
            coverage_binding = next(row for row in fixture.stage.artifacts
                if row.name == fixture.coverage_name)
            path = fixture.base.stage_root / coverage_binding.staged_filename
            with zipfile.ZipFile(io.BytesIO(path.read_bytes()), 'r') as archive:
                members = {item.filename: archive.read(item) for item in archive.infolist()}
            members.pop(subject.JUNIT_PREFIX + fixture.junit_name.removeprefix('junit-coverage-'))
            raw = _zip(members)
            path.write_bytes(raw)
            os.chmod(path, 0o600)
            replacement = replace(coverage_binding, size=len(raw), sha256=hashlib.sha256(raw).hexdigest())
            fixture.stage = replace(fixture.stage, artifacts=tuple(replacement if row.name == replacement.name
                else row for row in fixture.stage.artifacts), total_archive_bytes=sum(
                replacement.size if row.name == replacement.name else row.size for row in fixture.stage.artifacts))
            rows, expanded = completed_validation._zip_directory(raw)
            projections = tuple(replace(row, archive_sha256=replacement.sha256,
                archive_bytes=replacement.size, expanded_bytes=expanded,
                members=tuple(completed_validation.MemberProjection(item[0], item[3], item[4], item[6])
                    for item in rows)) if row.artifact_name == replacement.name else row
                for row in fixture.inventory.archives)
            fixture.inventory = replace(fixture.inventory, archives=projections)
            with self.assertRaisesRegex(ValueError, 'required-project-members'):
                fixture.inspect()
        finally:
            fixture.close()

    def test_public_api_rejects_nonzero_exit_and_oversized_junit_declaration(self):
        fixture = _PublicFixture()
        try:
            fixture.timings['projects'][0]['exitCode'] = 1
            self._replace_coverage_members(fixture, {subject.TIMINGS_MEMBER: _compact_json(fixture.timings)})
            with self.assertRaisesRegex(ValueError, 'timings-project-result'):
                fixture.inspect()
        finally:
            fixture.close()

    def test_coherent_project_and_junit_rename_still_rejects_wrong_source_slug(self):
        fixture = _PublicFixture()
        try:
            original_slug = fixture.slug
            changed_slug = original_slug[:-1] + ('0' if original_slug[-1] != '0' else '1')
            fixture.timings['projects'][0]['slug'] = changed_slug
            fixture.timings['projects'][0]['log'] = f'projects/{changed_slug}/dotnet-test.log'
            fixture.timings['projects'][0]['coverageFile'] = f'projects/{changed_slug}/coverage.cobertura.xml'
            fixture.timings['projects'][0]['invocationDirectory'] = f'projects/{changed_slug}/results'
            renamed_junit = f'junit-coverage-1-{changed_slug}.xml'
            fixture.timings['projects'][0]['testResults'][0]['path'] = renamed_junit
            fixture.timings['artifacts']['testResults'][0]['path'] = renamed_junit
            self._replace_coverage_members(fixture, {
                subject.TIMINGS_MEMBER: _compact_json(fixture.timings),
                subject.JUNIT_PREFIX + renamed_junit.removeprefix('junit-coverage-'): fixture.junit_body,
                subject.PROJECT_LOG_PREFIX + changed_slug + '/dotnet-test.log': fixture.log_body,
            })
            with self.assertRaisesRegex(ValueError, 'timings-project-slug'):
                fixture.inspect()
        finally:
            fixture.close()

        fixture = _PublicFixture()
        try:
            with patch.object(subject, 'MAX_JUNIT_BYTES', 8):
                with self.assertRaisesRegex(ValueError, 'project-member-size'):
                    fixture.inspect()
        finally:
            fixture.close()

    @staticmethod
    def _replace_coverage_members(fixture, replacements):
        binding = next(row for row in fixture.stage.artifacts if row.name == fixture.coverage_name)
        path = fixture.base.stage_root / binding.staged_filename
        with zipfile.ZipFile(io.BytesIO(path.read_bytes()), 'r') as archive:
            members = {item.filename: archive.read(item) for item in archive.infolist()}
        members.update(replacements)
        raw = _zip(members)
        path.write_bytes(raw)
        os.chmod(path, 0o600)
        updated = replace(binding, size=len(raw), sha256=hashlib.sha256(raw).hexdigest())
        fixture.stage = replace(fixture.stage, artifacts=tuple(updated if row.name == updated.name else row
            for row in fixture.stage.artifacts), total_archive_bytes=sum(
            updated.size if row.name == updated.name else row.size for row in fixture.stage.artifacts))
        rows, expanded = completed_validation._zip_directory(raw)
        projection = completed_validation.ArchiveProjection(updated.artifact_id, updated.name, updated.sha256,
            len(raw), expanded, tuple(completed_validation.MemberProjection(row[0], row[3], row[4], row[6])
                for row in rows), fixture.capture_sha)
        fixture.inventory = replace(fixture.inventory, archives=tuple(projection if row.artifact_name == updated.name
            else row for row in fixture.inventory.archives))

    def test_public_api_binds_current_archive_projection_and_capture_bytes(self):
        fixture = _PublicFixture()
        try:
            original_projection = next(row for row in fixture.inventory.archives
                                       if row.artifact_name == fixture.coverage_name)
            changed = replace(fixture.inventory, archives=tuple(
                replace(row, members=()) if row.artifact_name == fixture.coverage_name else row
                for row in fixture.inventory.archives))
            fixture.inventory = changed
            with self.assertRaisesRegex(ValueError, 'archive-projection'):
                fixture.inspect()
            fixture.inventory = replace(fixture.inventory, archives=tuple(
                original_projection if row.artifact_name == fixture.coverage_name else row
                for row in fixture.inventory.archives))
            with self.assertRaisesRegex(ValueError, 'source-capture-binding'):
                fixture.inspect(capture=fixture.capture + b' ')
        finally:
            fixture.close()

    def test_authority_fields_cannot_be_passed_to_result_constructor(self):
        self.assertTrue(all(not subject.CompletedTestInventory.__dataclass_fields__[name].init
            for name in ('retained_project_inventory_verified', 'original_tests_success_proven',
                         'original_merge_success_proven', 'original_workflow_success_proven',
                         'gate_eligible', 'admission_created', 'qualification_complete')))


if __name__ == '__main__':
    unittest.main()
