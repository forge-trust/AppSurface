"""Bounded, non-authorizing inspection of retained solution-test artifacts."""
from __future__ import annotations

from dataclasses import dataclass, field
import hashlib
import json
import math
import os
from pathlib import Path, PurePosixPath
import re
import stat
import time
import xml.etree.ElementTree as ET

import completed_c1
import completed_gate
import completed_run
import completed_validation

MAX_TIMINGS_BYTES = 1024 * 1024
MAX_JUNIT_BYTES = 8 * 1024 * 1024
MAX_LOG_BYTES = 8 * 1024 * 1024
MAX_SELECTED_BYTES = 256 * 1024 * 1024
MAX_PROJECTS = 256
MAX_JUNIT_CASES_PER_FILE = 250_000
MAX_JUNIT_CASES_TOTAL = 2_000_000
MAX_JUNIT_XML_DEPTH = 64
MAX_JUNIT_XML_NODES = 1_000_000
MAX_SOLUTION_BYTES = 4 * 1024 * 1024
MAX_SOLUTION_XML_NODES = 100_000
SOURCE_MOUNT = Path('/home/runner/work/AppSurface/AppSurface')
TIMINGS_MEMBER = 'AppSurface/AppSurface/TestResults/coverage-merged/timings.json'
COBERTURA_MEMBER = 'AppSurface/AppSurface/TestResults/coverage-merged/coverage.cobertura.xml'
CAPTURE_MEMBER = completed_validation.COVERAGE_SOURCE_MEMBER
PROJECT_LOG_PREFIX = 'AppSurface/AppSurface/TestResults/coverage-merged/projects/'
JUNIT_PREFIX = 'AppSurface/AppSurface/TestResults/coverage-merged/junit-coverage-'
TIMINGS_KEYS = frozenset(('solution', 'configuration', 'coverageDriver', 'buildSolution', 'parallelism',
    'schedule', 'durations', 'merge', 'artifacts', 'diagnostics', 'projects'))
PROJECT_KEYS = frozenset(('project', 'slug', 'originalIndex', 'executionIndex', 'executionStatus',
    'scheduledSeconds', 'durationSource', 'scheduleReason', 'seconds', 'exitCode', 'exclusive', 'log',
    'coverageDriver', 'invocationDirectory', 'hangDiagnostics', 'coverageArtifactStatus',
    'coverageArtifactCause', 'coverageCleanupLog', 'coverageCleanupDiagnostic', 'coverageFile',
    'testResults'))
JUNIT_CHILDREN = {
    'testsuites': {'testsuite'},
    'testsuite': {'testsuite', 'testcase', 'properties', 'system-out', 'system-err'},
    'testcase': {'failure', 'error', 'skipped', 'properties', 'system-out', 'system-err'},
    'properties': {'property'},
    'property': set(),
    'failure': set(), 'error': set(), 'skipped': set(),
    'system-out': set(), 'system-err': set(),
}


def _need(value, reason):
    if not value:
        raise ValueError('completed-test-inventory-' + reason)


def _remaining(deadline):
    _need(type(deadline) in (int, float) and math.isfinite(deadline)
          and time.monotonic() < deadline, 'deadline')
    return deadline - time.monotonic()


def _sha(raw):
    return hashlib.sha256(raw).hexdigest()


def _strict_json(raw, maximum, reason):
    _need(type(raw) is bytes and 0 < len(raw) <= maximum, reason + '-size')
    try:
        value = json.loads(raw.decode('utf-8'), object_pairs_hook=completed_run._unique_pairs,
                           parse_constant=lambda _item: (_ for _ in ()).throw(ValueError(reason)))
    except (UnicodeError, json.JSONDecodeError, RecursionError, ValueError):
        raise ValueError('completed-test-inventory-' + reason + '-json') from None
    _need(type(value) is dict, reason + '-object')
    return value


def _safe_relative(value, reason):
    _need(type(value) is str and value and '\\' not in value and not value.startswith('/'), reason)
    path = PurePosixPath(value)
    _need(path.as_posix() == value and all(part not in ('', '.', '..') for part in path.parts), reason)
    return value


def _parse_xml(raw, maximum, reason):
    _need(type(raw) is bytes and 0 < len(raw) <= maximum, reason + '-size')
    try:
        text = raw.decode('utf-8-sig')
    except UnicodeError:
        raise ValueError('completed-test-inventory-' + reason + '-encoding') from None
    declaration = re.match(r'\s*<\?xml\s+[^?]*encoding\s*=\s*[\'"]([^\'"]+)', text, re.IGNORECASE)
    _need(declaration is None or declaration.group(1).lower().replace('_', '-') in ('utf-8', 'utf8'),
          reason + '-encoding')
    upper = text.upper()
    _need('<!DOCTYPE' not in upper and '<!ENTITY' not in upper, reason + '-dtd')
    try:
        return ET.fromstring(text)
    except (ET.ParseError, ValueError):
        raise ValueError('completed-test-inventory-' + reason + '-xml') from None


def _test_projects(solution_raw, deadline):
    _remaining(deadline)
    _need(type(solution_raw) is bytes and 0 < len(solution_raw) <= MAX_SOLUTION_BYTES,
          'solution-size')
    root = _parse_xml(solution_raw, MAX_SOLUTION_BYTES, 'solution')
    _need(root.tag == 'Solution', 'solution-root')
    stack = [(root, 1)]
    node_count = 0
    while stack:
        element, depth = stack.pop()
        node_count += 1
        _need(node_count <= MAX_SOLUTION_XML_NODES and depth <= MAX_JUNIT_XML_DEPTH,
              'solution-xml-bound')
        stack.extend((child, depth + 1) for child in list(element))
    rows = []
    seen = set()
    for element in root.iter('Project'):
        _remaining(deadline)
        raw_path = element.attrib.get('Path')
        if raw_path is None:
            continue
        path = raw_path.replace('\\', '/')
        _safe_relative(path, 'solution-project-path')
        key = path.casefold()
        _need(key not in seen, 'solution-duplicate-project')
        seen.add(key)
        leaf = path.rsplit('/', 1)[-1]
        if not leaf.lower().endswith('.csproj'):
            continue
        stem = leaf[:-len('.csproj')]
        # Match CoverageRun's IsDiscoveredTestProject after `dotnet sln list`.
        if (stem.endswith('Tests') or stem.endswith('.Tests')
                or stem.endswith('.IntegrationTests')):
            rows.append(path)
    _need(0 < len(rows) <= MAX_PROJECTS and len(rows) == len(set(rows)), 'solution-test-projects')
    return tuple(rows)


def _project_slug(project, source_root):
    """Mirror CoverageRun.AllocateProjectSlugs for the fixed Linux workflow path."""
    _safe_relative(project, 'slug-project-path')
    full_path = os.path.abspath(os.path.join(str(source_root), project)).replace('\\', '/')
    _need(full_path.isascii(), 'slug-path-nonascii')
    leaf = project.rsplit('/', 1)[-1]
    stem = leaf[:-len('.csproj')]
    sanitized = ''.join(character if character.isascii() and character.isalnum()
                        or character in '_.-' else '-' for character in stem)
    if not sanitized or sanitized.isspace():
        sanitized = 'project'
    digest = hashlib.sha256(full_path.upper().encode('utf-8')).hexdigest()[:8]
    return f'{sanitized}-{digest}'


def _optional_counter(element, name, expected):
    raw = element.attrib.get(name)
    if raw is None:
        return
    _need(raw.isascii() and raw.isdecimal() and int(raw) == expected, 'junit-counts')


def _parse_junit(raw, deadline):
    _remaining(deadline)
    _need(type(raw) is bytes and 0 < len(raw) <= MAX_JUNIT_BYTES, 'junit-size')
    root = _parse_xml(raw, MAX_JUNIT_BYTES, 'junit')
    _need(root.tag in ('testsuite', 'testsuites'), 'junit-root')
    stack = [(root, 1)]
    nodes = suites = cases = failures = errors = skipped = 0
    suite_rows = []
    while stack:
        _remaining(deadline)
        element, depth = stack.pop()
        nodes += 1
        _need(nodes <= MAX_JUNIT_XML_NODES and depth <= MAX_JUNIT_XML_DEPTH, 'junit-xml-bound')
        _need(element.tag in JUNIT_CHILDREN, 'junit-xml-element')
        _need(all(child.tag in JUNIT_CHILDREN[element.tag] for child in list(element)),
              'junit-xml-structure')
        if element.tag == 'testsuite':
            suites += 1
            suite_rows.append(element)
        elif element.tag == 'testcase':
            cases += 1
            _need(cases <= MAX_JUNIT_CASES_PER_FILE, 'junit-case-bound')
            statuses = [child.tag for child in list(element)
                        if child.tag in ('failure', 'error', 'skipped')]
            _need(len(statuses) <= 1, 'junit-case-status')
            if statuses == ['failure']:
                failures += 1
            elif statuses == ['error']:
                errors += 1
            elif statuses == ['skipped']:
                skipped += 1
        for child in reversed(list(element)):
            stack.append((child, depth + 1))
    _need(suites > 0 and cases > 0, 'junit-empty')
    for suite in suite_rows:
        nested = list(suite.iter('testcase'))
        local_cases = len(nested)
        local_failures = local_errors = local_skipped = 0
        for case in nested:
            marks = [child.tag for child in list(case)
                     if child.tag in ('failure', 'error', 'skipped')]
            local_failures += marks == ['failure']
            local_errors += marks == ['error']
            local_skipped += marks == ['skipped']
        for name, value in (('tests', local_cases), ('failures', local_failures),
                            ('errors', local_errors), ('skipped', local_skipped)):
            _optional_counter(suite, name, value)
    if root.tag == 'testsuites':
        for name, value in (('tests', cases), ('failures', failures), ('errors', errors),
                            ('skipped', skipped)):
            _optional_counter(root, name, value)
    _remaining(deadline)
    _need(failures == errors == 0, 'junit-failures')
    return cases, skipped


def _validate_timings(value, expected_projects, source_root):
    _need(set(value) == TIMINGS_KEYS, 'timings-schema')
    solution = source_root / 'ForgeTrust.AppSurface.slnx'
    _need(value['solution'] == str(solution) and value['configuration'] == 'Release'
          and value['coverageDriver'] == 'collector' and value['buildSolution'] is True
          and type(value['parallelism']) is int and value['parallelism'] == 2,
          'timings-run-policy')
    schedule = value['schedule']
    _need(type(schedule) is dict and set(schedule) == {'mode', 'timingSource', 'warnings'}
          and schedule['mode'] == 'input-order' and schedule['warnings'] == [], 'timings-schedule')
    timing_source = schedule['timingSource']
    _need(type(timing_source) is dict and set(timing_source) == {'kind', 'path', 'status'}
          and timing_source == {'kind': 'none', 'path': None, 'status': 'not-used'}, 'timings-source')
    durations = value['durations']
    _need(type(durations) is dict and set(durations) == {
        'solutionBuildSeconds', 'coverageMergeSeconds', 'totalSeconds'}
        and all(type(item) is int and 0 <= item <= 2**31 - 1 for item in durations.values()),
        'timings-durations')
    merge = value['merge']
    _need(type(merge) is dict and set(merge) == {'exitCode'}
          and type(merge['exitCode']) is int and merge['exitCode'] == 0, 'timings-merge')
    artifacts = value['artifacts']
    _need(type(artifacts) is dict and set(artifacts) == {
        'coverageFiles', 'cobertura', 'junitFiles', 'testResults', 'diagnostics'}
        and artifacts['cobertura'] == 'coverage.cobertura.xml'
        and type(artifacts['coverageFiles']) is int and artifacts['coverageFiles'] == len(expected_projects)
        and type(artifacts['junitFiles']) is int and artifacts['junitFiles'] == len(expected_projects),
        'timings-artifacts')
    artifact_diagnostics = artifacts['diagnostics']
    _need(type(artifact_diagnostics) is dict
          and set(artifact_diagnostics) == {'schemaVersion', 'markdown', 'json'}
          and artifact_diagnostics['schemaVersion'] == 1
          and type(artifact_diagnostics['markdown']) is str and artifact_diagnostics['markdown']
          and type(artifact_diagnostics['json']) is str and artifact_diagnostics['json'],
          'timings-artifact-diagnostics')
    diagnostics = value['diagnostics']
    _need(type(diagnostics) is dict and set(diagnostics) == {'slowTests'}, 'timings-diagnostics')
    slow = diagnostics['slowTests']
    _need(type(slow) is dict and set(slow) == {'warningCount', 'metadataComplete',
          'aggregationSeconds', 'aggregationPercent', 'markdown', 'json'}
          and type(slow['warningCount']) is int and slow['warningCount'] >= 0
          and type(slow['metadataComplete']) is bool
          and type(slow['aggregationSeconds']) in (int, float)
          and math.isfinite(slow['aggregationSeconds']) and slow['aggregationSeconds'] >= 0
          and type(slow['aggregationPercent']) in (int, float)
          and math.isfinite(slow['aggregationPercent']) and slow['aggregationPercent'] >= 0
          and type(slow['markdown']) is str and slow['markdown']
          and type(slow['json']) is str and slow['json'], 'timings-slow-diagnostics')
    projects = value['projects']
    _need(type(projects) is list and len(projects) == len(expected_projects), 'timings-project-count')
    rows = {}
    original_indexes = set()
    execution_indexes = set()
    for row in projects:
        _need(type(row) is dict and set(row) == PROJECT_KEYS, 'timings-project-schema')
        project = _safe_relative(row['project'], 'timings-project-path')
        _need(project in expected_projects and project not in rows, 'timings-project-inventory')
        slug = row['slug']
        expected_slug = _project_slug(project, source_root)
        _need(type(slug) is str and slug == expected_slug, 'timings-project-slug')
        original = row['originalIndex']
        execution = row['executionIndex']
        _need(type(original) is int and 0 <= original < len(expected_projects)
              and original not in original_indexes and type(execution) is int
              and 0 <= execution < len(expected_projects) and execution not in execution_indexes,
              'timings-project-index')
        original_indexes.add(original)
        execution_indexes.add(execution)
        _need(row['executionStatus'] == 'completed' and type(row['exitCode']) is int
              and row['exitCode'] == 0 and type(row['seconds']) is int and row['seconds'] >= 0
              and row['coverageDriver'] == 'collector'
              and row['coverageArtifactStatus'] == 'produced'
              and row['coverageArtifactCause'] is None
              and row['coverageFile'] == f'projects/{slug}/coverage.cobertura.xml'
              and row['log'] == f'projects/{slug}/dotnet-test.log', 'timings-project-result')
        _need(type(row['exclusive']) is bool and row['durationSource'] in ('none', 'timings')
              and type(row['scheduleReason']) is str and row['scheduleReason'], 'timings-project-schedule')
        test_results = row['testResults']
        junit_path = f'junit-coverage-{original + 1}-{expected_slug}.xml'
        _need(type(test_results) is list and len(test_results) == 1
              and type(test_results[0]) is dict
              and set(test_results[0]) == {'format', 'path', 'parserStatus'}
              and test_results[0]['format'] == 'junit' and test_results[0]['path'] == junit_path
              and test_results[0]['parserStatus'] == 'parsed', 'timings-project-junit')
        rows[project] = (original, execution, slug, row['log'], junit_path)
    _need(set(rows) == set(expected_projects)
          and original_indexes == execution_indexes == set(range(len(expected_projects))),
          'timings-project-set')
    top_results = artifacts['testResults']
    _need(type(top_results) is list and len(top_results) == len(expected_projects),
          'timings-top-junit-count')
    observed = set()
    for item in top_results:
        _need(type(item) is dict and set(item) == {'format', 'project', 'path', 'parserStatus'}
              and item['format'] == 'junit' and item['parserStatus'] == 'parsed', 'timings-top-junit')
        project = item['project']
        _need(project in rows and project not in observed and item['path'] == rows[project][4],
              'timings-top-junit-binding')
        observed.add(project)
    _need(observed == set(rows), 'timings-top-junit-set')
    return rows


@dataclass(frozen=True)
class ProjectTestInventory:
    """Source-bound timing and JUnit data for one solution test project."""
    project: str
    execution_index: int
    junit_path: str
    junit_sha256: str
    log_path: str
    log_sha256: str
    test_cases: int
    skipped_cases: int


@dataclass(frozen=True)
class CompletedTestInventory:
    """Retained test/merge data only; this is not proof of workflow success."""
    run_id: int
    run_attempt: int
    head_sha: str
    source_capture_sha256: str
    solution_sha256: str
    timings_sha256: str
    coverage_sha256: str
    project_count: int
    test_case_count: int
    skipped_case_count: int
    projects: tuple[ProjectTestInventory, ...]
    retained_project_inventory_verified: bool = field(default=True, init=False)
    original_tests_success_proven: bool = field(default=False, init=False)
    original_merge_success_proven: bool = field(default=False, init=False)
    original_workflow_success_proven: bool = field(default=False, init=False)
    gate_eligible: bool = field(default=False, init=False)
    admission_created: bool = field(default=False, init=False)
    qualification_complete: bool = field(default=False, init=False)


def inspect_completed_test_inventory(stage, inventory, stage_directory, source_capture, source_root,
                                     *, deadline):
    """Validate the retained test-project inventory without launching builds or parsing logs.

    Every selected archive body is checksum-bound by the completed-run stage and its full ZIP
    directory is matched against the typed inventory projection before extraction.
    """
    _remaining(deadline)
    _need(isinstance(stage, completed_run.CompletedRunStage)
          and isinstance(inventory, completed_validation.ParsedCompletedRunInventory), 'stage-type')
    bindings, projections = completed_c1._assert_projection(stage, inventory)
    _need(type(source_capture) is bytes and 0 < len(source_capture) <= completed_c1.MAX_SOURCE_BYTES
          and _sha(source_capture) == stage.source_capture_sha256, 'source-capture-binding')
    source = Path(source_root)
    _need(source.is_absolute() and source.resolve(strict=True) == source
          and source == SOURCE_MOUNT and stat.S_ISDIR(source.lstat().st_mode)
          and not stat.S_ISLNK(source.lstat().st_mode), 'source-root')
    current_binding = completed_c1._load_trusted_current_binding(deadline=deadline)
    capture, _source_binding = completed_c1._load_capture_and_binding(source_capture, source,
        current_binding=current_binding, deadline=deadline)
    _need(capture['source'] == stage.head_sha and capture['tree'] == stage.source_tree
          and inventory.source_count == capture['count'], 'source-stage-binding')
    solution_path = source / 'ForgeTrust.AppSurface.slnx'
    solution_row = capture['files'].get('ForgeTrust.AppSurface.slnx')
    _need(type(solution_row) is dict and type(solution_row.get('sha256')) is str
          and completed_run.SHA256.fullmatch(solution_row['sha256']) is not None,
          'solution-capture-row')
    solution_raw = completed_c1._read_trusted_source_file(solution_path, deadline=deadline,
                                                           maximum=MAX_SOLUTION_BYTES)
    _need(_sha(solution_raw) == solution_row['sha256'], 'solution-source-binding')
    expected_projects = _test_projects(solution_raw, deadline)

    stage_root = Path(stage_directory)
    _need(stage_root.is_absolute() and stage_root.resolve(strict=True) == stage_root,
          'stage-path')
    root_info = stage_root.lstat()
    _need(stat.S_ISDIR(root_info.st_mode) and not stat.S_ISLNK(root_info.st_mode)
          and stat.S_IMODE(root_info.st_mode) == 0o700 and root_info.st_uid == os.geteuid(),
          'stage-shape')
    _need(set(os.listdir(stage_root)) == {item.staged_filename for item in stage.artifacts},
          'stage-directory-inventory')
    artifact_name = f'issue779-checkpoint4-coverage-{stage.run_id}-{stage.run_attempt}'
    _need(artifact_name in bindings and artifact_name in projections, 'coverage-artifact-missing')
    binding = bindings[artifact_name]
    projection = projections[artifact_name]
    _need(projection.source_sha256 == stage.source_capture_sha256, 'archive-source-projection')
    raw = completed_c1._open_bound_file(stage_root / binding.staged_filename, binding.size,
        binding.sha256, deadline=deadline, maximum=completed_run._artifact_limit(artifact_name),
        required_mode=0o600)
    rows, expanded = completed_validation._zip_directory(raw)
    actual = tuple(completed_validation.MemberProjection(row[0], row[3], row[4], row[6])
                   for row in rows)
    _need(actual == projection.members and expanded == projection.expanded_bytes,
          'archive-projection')
    files = {row[0]: row for row in rows if not row[0].endswith('/')}
    _need(CAPTURE_MEMBER in files and TIMINGS_MEMBER in files and COBERTURA_MEMBER in files,
          'required-members')
    _need(files[CAPTURE_MEMBER][4] <= completed_c1.MAX_SOURCE_BYTES
          and files[TIMINGS_MEMBER][4] <= MAX_TIMINGS_BYTES
          and files[COBERTURA_MEMBER][4] <= completed_gate.MAX_COBERTURA_BYTES,
          'selected-member-bounds')
    primary = completed_validation._member_bodies(raw, rows,
        {CAPTURE_MEMBER, TIMINGS_MEMBER, COBERTURA_MEMBER}, deadline)
    _need(primary[CAPTURE_MEMBER] == source_capture, 'archive-source-capture')
    timings = _strict_json(primary[TIMINGS_MEMBER], MAX_TIMINGS_BYTES, 'timings')
    project_rows = _validate_timings(timings, expected_projects, source)
    junit_members = {JUNIT_PREFIX + row[4].removeprefix('junit-coverage-')
                     for row in project_rows.values()}
    log_members = {PROJECT_LOG_PREFIX + row[2] + '/dotnet-test.log'
                   for row in project_rows.values()}
    required = junit_members | log_members
    _need(len(junit_members) == len(log_members) == len(expected_projects)
          and required <= set(files), 'required-project-members')
    total = len(primary[CAPTURE_MEMBER]) + len(primary[TIMINGS_MEMBER]) + len(primary[COBERTURA_MEMBER])
    for name in required:
        maximum = MAX_JUNIT_BYTES if name in junit_members else MAX_LOG_BYTES
        _need(0 < files[name][4] <= maximum, 'project-member-size')
        total += files[name][4]
    _need(total <= MAX_SELECTED_BYTES, 'selected-total-bounds')
    _remaining(deadline)
    bodies = completed_validation._member_bodies(raw, rows, required, deadline)
    project_results = []
    test_count = skipped_count = 0
    ordered = sorted(project_rows.items(), key=lambda pair: pair[1][0])
    for project, (original_index, execution_index, slug, log_path, junit_path) in ordered:
        _remaining(deadline)
        junit_member = JUNIT_PREFIX + junit_path.removeprefix('junit-coverage-')
        log_member = PROJECT_LOG_PREFIX + slug + '/dotnet-test.log'
        cases, skipped = _parse_junit(bodies[junit_member], deadline)
        test_count += cases
        skipped_count += skipped
        _need(test_count <= MAX_JUNIT_CASES_TOTAL, 'junit-total-case-bound')
        project_results.append(ProjectTestInventory(project, execution_index, junit_path,
            _sha(bodies[junit_member]), log_path, _sha(bodies[log_member]), cases, skipped))
    _need(len(primary[COBERTURA_MEMBER]) > 0, 'coverage-empty')
    _remaining(deadline)
    return CompletedTestInventory(stage.run_id, stage.run_attempt, stage.head_sha,
        stage.source_capture_sha256, _sha(solution_raw), _sha(primary[TIMINGS_MEMBER]),
        _sha(primary[COBERTURA_MEMBER]), len(project_results), test_count, skipped_count,
        tuple(project_results))
