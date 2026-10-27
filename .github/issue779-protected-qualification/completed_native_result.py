"""Strict, portable projection of the one fixed native validator TRX result."""
from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime
import hashlib
import math
import os
from pathlib import Path
import re
import stat
import time
import xml.etree.ElementTree as ET

MAX_TRX_BYTES = 1024 * 1024
NAMESPACE = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'
TEST_METHOD = (
    'ForgeTrust.AppSurface.Evidence.Supervision.Native.Tests.NativeMeasurementTests.'
    'LinuxNativeMeasurementsMatchTheCurrentSourceAndAllSevenPhysicalControls'
)
TEST_CLASS = 'ForgeTrust.AppSurface.Evidence.Supervision.Native.Tests.NativeMeasurementTests'
TEST_NAME = 'LinuxNativeMeasurementsMatchTheCurrentSourceAndAllSevenPhysicalControls'
PROJECT = 'Evidence/ForgeTrust.AppSurface.Evidence.Supervision.Native.Tests/ForgeTrust.AppSurface.Evidence.Supervision.Native.Tests.csproj'
ASSEMBLY = 'Evidence/ForgeTrust.AppSurface.Evidence.Supervision.Native.Tests/bin/Release/net10.0/ForgeTrust.AppSurface.Evidence.Supervision.Native.Tests.dll'
RESULT_FILE = 'native-validator.trx'
_COUNTERS = frozenset({
    'total', 'executed', 'passed', 'failed', 'error', 'timeout', 'aborted', 'inconclusive',
    'passedButRunAborted', 'notRunnable', 'notExecuted', 'disconnected', 'warning',
    'completed', 'inProgress', 'pending',
})
_ROOT_CHILDREN = ('Times', 'TestSettings', 'Results', 'TestDefinitions', 'TestEntries', 'TestLists', 'ResultSummary')
_TAG = lambda name: f'{{{NAMESPACE}}}{name}'


def _need(condition: bool, code: str) -> None:
    if not condition:
        raise ValueError('completed-native-result-' + code)


def _deadline(deadline: float) -> None:
    _need(type(deadline) in (int, float) and math.isfinite(deadline)
          and time.monotonic() < deadline, 'deadline')


def _children(element: ET.Element, name: str) -> list[ET.Element]:
    return [child for child in list(element) if child.tag == _TAG(name)]


def _one(element: ET.Element, name: str) -> ET.Element:
    children = _children(element, name)
    _need(len(children) == 1, 'xml-' + name.lower() + '-count')
    return children[0]


def _assembly_suffix(value: str) -> bool:
    if type(value) is not str or '\x00' in value or '\\' in value:
        return False
    path = value.split('/')
    expected = ASSEMBLY.split('/')
    return (value.startswith('/') and all(item not in ('', '.', '..') for item in path[1:])
            and len(path) >= len(expected) and [item.casefold() for item in path[-len(expected):]] == [
                item.casefold() for item in expected
            ])


def _timestamp(value: str) -> datetime:
    if type(value) is not str or re.fullmatch(
            r'\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})', value) is None:
        raise ValueError('timestamp')
    # DateTimeOffset accepts seven fractional digits; Python datetime accepts six.
    normalized = re.sub(r'\.(\d{6})\d(?=Z|[+-])', r'.\1', value)
    return datetime.fromisoformat(normalized.replace('Z', '+00:00'))


@dataclass(frozen=True)
class NativeValidatorResult:
    """TRX outcome projection only; it cannot prove a trusted execution or qualification."""

    schema: str
    test_name: str
    outcome: str
    trx_bytes: int
    trx_sha256: str
    validator_executed: bool = False
    native_acceptance: bool = False
    qualification_complete: bool = False


def parse_native_validator_trx(raw: bytes, *, deadline: float) -> NativeValidatorResult:
    """Accept only one passed result for the fixed public method and its fixed test assembly."""
    _deadline(deadline)
    _need(type(raw) is bytes and 0 < len(raw) <= MAX_TRX_BYTES, 'trx-size')
    folded = raw.upper()
    _need(b'\x00' not in raw and b'<!DOCTYPE' not in folded and b'<!ENTITY' not in folded,
          'xml-unsafe-declaration')
    try:
        root = ET.fromstring(raw)
    except (ET.ParseError, ValueError, RecursionError):
        raise ValueError('completed-native-result-xml-parse') from None
    _deadline(deadline)
    _need(root.tag == _TAG('TestRun'), 'xml-root')
    _need(set(root.attrib) == {'id', 'name'} and root.get('id'), 'xml-root-attributes')
    _need(all(child.tag.startswith('{' + NAMESPACE + '}') for child in root.iter()), 'xml-namespace')
    _need(tuple(child.tag for child in root) == tuple(_TAG(name) for name in _ROOT_CHILDREN),
          'xml-root-children')

    results = _one(root, 'Results')
    _need(not results.attrib, 'results-attributes')
    result_rows = list(results)
    _need(len(result_rows) == 1 and result_rows[0].tag == _TAG('UnitTestResult'), 'result-count')
    result = result_rows[0]
    _need(len(result) == 0, 'result-children')
    _need(result.attrib.keys() == {
        'executionId', 'testId', 'testName', 'computerName', 'duration', 'startTime', 'endTime',
        'testType', 'outcome', 'testListId', 'relativeResultsDirectory',
    }, 'result-attributes')
    _need(result.get('testName') == TEST_METHOD and result.get('outcome') == 'Passed', 'result-method-outcome')
    _need(result.get('executionId') and result.get('testId') and result.get('testListId'), 'result-identities')
    uuid_pattern = r'[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}'
    _need(re.fullmatch(uuid_pattern, result.get('executionId', '')) is not None
          and re.fullmatch(uuid_pattern, result.get('testId', '')) is not None,
          'result-identities')
    _need(re.fullmatch(r'\d{2}:\d{2}:\d{2}(?:\.\d+)?', result.get('duration', '')) is not None,
          'result-duration')
    try:
        start = _timestamp(result.get('startTime', ''))
        end = _timestamp(result.get('endTime', ''))
        _need(start.tzinfo is not None and end.tzinfo is not None and end >= start, 'result-time')
    except (TypeError, ValueError):
        raise ValueError('completed-native-result-result-time') from None

    definitions = _one(root, 'TestDefinitions')
    _need(not definitions.attrib, 'definitions-attributes')
    definition_rows = list(definitions)
    _need(len(definition_rows) == 1 and definition_rows[0].tag == _TAG('UnitTest'), 'definition-count')
    definition = definition_rows[0]
    _need(definition.get('name') == TEST_METHOD and definition.get('id') == result.get('testId'),
          'definition-identity')
    _need(set(definition.attrib) == {'name', 'storage', 'id'} and _assembly_suffix(definition.get('storage', '')),
          'definition-storage')
    _need(tuple(child.tag for child in definition) == (_TAG('Execution'), _TAG('TestMethod')),
          'definition-children')
    execution = _one(definition, 'Execution')
    _need(set(execution.attrib) == {'id'} and execution.get('id') == result.get('executionId')
          and len(execution) == 0, 'definition-execution')
    method = _one(definition, 'TestMethod')
    _need(set(method.attrib) == {'codeBase', 'adapterTypeName', 'className', 'name'}
          and method.get('className') == TEST_CLASS and method.get('name') == TEST_NAME
          and method.get('adapterTypeName') == 'executor://xunit/VsTestRunner3/netcore/'
          and _assembly_suffix(method.get('codeBase', ''))
          and definition.get('storage', '').casefold() == method.get('codeBase', '').casefold()
          and len(method) == 0,
          'definition-method')

    entries = _one(root, 'TestEntries')
    _need(not entries.attrib, 'entries-attributes')
    entry_rows = list(entries)
    _need(len(entry_rows) == 1 and entry_rows[0].tag == _TAG('TestEntry'), 'entry-count')
    entry = entry_rows[0]
    _need(set(entry.attrib) == {'testId', 'executionId', 'testListId'}
          and entry.get('testId') == result.get('testId')
          and entry.get('executionId') == result.get('executionId')
          and entry.get('testListId') == result.get('testListId') and len(entry) == 0,
          'entry-binding')

    test_lists = _one(root, 'TestLists')
    _need(not test_lists.attrib, 'test-lists-attributes')
    list_rows = list(test_lists)
    _need(bool(list_rows) and all(row.tag == _TAG('TestList') and set(row.attrib) == {'name', 'id'}
          and row.get('id') and len(row) == 0 for row in list_rows), 'test-lists')
    list_ids = [row.get('id') for row in list_rows]
    list_names = [row.get('name') for row in list_rows]
    _need(len(list_ids) == len(set(list_ids)) and len(list_names) == len(set(list_names))
          and set(list_names) == {'Results Not in a List', 'All Loaded Results'}
          and result.get('testListId') in list_ids, 'test-list-binding')

    summary = _one(root, 'ResultSummary')
    _need(summary.get('outcome') == 'Completed' and set(summary.attrib) == {'outcome'}, 'summary-outcome')
    _need(tuple(child.tag for child in summary) == (_TAG('Counters'), _TAG('Output')), 'summary-children')
    counters = _one(summary, 'Counters')
    _need(set(counters.attrib) == _COUNTERS and len(counters) == 0, 'counter-shape')
    _need(all(value == ('1' if name in ('total', 'executed', 'passed') else '0')
              for name, value in counters.attrib.items()),
          'counter-value')
    output = _one(summary, 'Output')
    _need(not output.attrib and tuple(child.tag for child in output) in ((), (_TAG('StdOut'),), (_TAG('StdErr'),),
          (_TAG('StdOut'), _TAG('StdErr'))) and all(len(child) == 0 for child in output), 'summary-output')
    _need(all(not child.attrib for child in output), 'summary-output-attributes')
    times = _one(root, 'Times')
    _need(set(times.attrib) == {'creation', 'queuing', 'start', 'finish'} and len(times) == 0,
          'times-content')
    settings = _one(root, 'TestSettings')
    _need(tuple(child.tag for child in settings) == (_TAG('Deployment'),)
          and set(settings.attrib) == {'name', 'id'}, 'settings-shape')
    deployment = _one(settings, 'Deployment')
    _need(set(deployment.attrib) == {'runDeploymentRoot'} and len(deployment) == 0,
          'deployment-shape')
    _deadline(deadline)
    return NativeValidatorResult('issue779-native-validator-trx-v1', TEST_METHOD, 'Passed',
                                 len(raw), hashlib.sha256(raw).hexdigest())


def fixed_validator_argv(source_root: str | os.PathLike[str],
                         private_output_root: str | os.PathLike[str]) -> tuple[str, ...]:
    """Build argv for only the fixed public test; caller must first authenticate the source/build."""
    source = Path(source_root)
    output = Path(private_output_root)
    try:
        source_canonical = source.resolve(strict=True)
        output_canonical = output.resolve(strict=True)
    except OSError:
        raise ValueError('completed-native-result-root') from None
    _need(source.is_absolute() and source_canonical == source
          and source.is_dir() and not source.is_symlink(), 'source-root')
    _need(output.is_absolute() and output_canonical == output
          and output.is_dir() and not output.is_symlink(), 'output-root')
    slnx = source / 'ForgeTrust.AppSurface.slnx'
    project = source / PROJECT
    _need(slnx.is_file() and not slnx.is_symlink() and slnx.resolve(strict=True) == slnx
          and project.is_file() and not project.is_symlink() and project.resolve(strict=True) == project,
          'source-project')
    output_info = output.lstat()
    _need(stat.S_ISDIR(output_info.st_mode) and output_info.st_uid == os.geteuid()
          and stat.S_IMODE(output_info.st_mode) == 0o700, 'output-root-custody')
    _need(not (source == output or source in output.parents or output in source.parents), 'root-overlap')
    assembly = source / ASSEMBLY
    _need(assembly.is_file() and not assembly.is_symlink() and assembly.resolve(strict=True) == assembly,
          'assembly-path')
    result_path = output / RESULT_FILE
    _need(not os.listdir(output), 'output-root-not-empty')
    _need(not os.path.lexists(result_path), 'result-path-exists')
    return (
        'dotnet', 'vstest', str(assembly), f'--TestCaseFilter:FullyQualifiedName={TEST_METHOD}',
        f'--logger:trx;LogFileName={RESULT_FILE}', f'--ResultsDirectory:{output}',
    )
