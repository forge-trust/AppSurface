"""Tests for strict parsing of the fixed public native validator TRX result."""
from __future__ import annotations

from dataclasses import FrozenInstanceError
import os
from pathlib import Path
import sys
import tempfile
import time
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / '.github/issue779-protected-qualification'))
import completed_native_result as subject


def _fixture():
    method = subject.TEST_METHOD
    assembly = '/isolated/source/' + subject.ASSEMBLY
    test_id = '10000000-0000-4000-8000-000000000001'
    execution_id = '20000000-0000-4000-8000-000000000002'
    fixture = f'''<?xml version="1.0" encoding="utf-8"?>
<TestRun id="30000000-0000-4000-8000-000000000003" name="run" xmlns="{subject.NAMESPACE}">
  <Times creation="2026-01-01T00:00:00Z" queuing="2026-01-01T00:00:00Z" start="2026-01-01T00:00:00Z" finish="2026-01-01T00:00:01Z" />
  <TestSettings name="default" id="settings-id"><Deployment runDeploymentRoot="run" /></TestSettings>
  <Results><UnitTestResult executionId="{execution_id}" testId="{test_id}" testName="{method}" computerName="host" duration="00:00:00.1" startTime="2026-01-01T00:00:00Z" endTime="2026-01-01T00:00:01Z" testType="type" outcome="Passed" testListId="list-id" relativeResultsDirectory="results" /></Results>
  <TestDefinitions><UnitTest name="{method}" storage="{assembly.lower()}" id="{test_id}"><Execution id="{execution_id}" /><TestMethod codeBase="{assembly}" adapterTypeName="executor://xunit/VsTestRunner3/netcore/" className="{subject.TEST_CLASS}" name="{subject.TEST_NAME}" /></UnitTest></TestDefinitions>
  <TestEntries><TestEntry testId="{test_id}" executionId="{execution_id}" testListId="list-id" /></TestEntries>
  <TestLists><TestList name="Results Not in a List" id="other-list" /><TestList name="All Loaded Results" id="list-id" /></TestLists>
  <ResultSummary outcome="Completed"><Counters total="1" executed="1" passed="1" failed="0" error="0" timeout="0" aborted="0" inconclusive="0" passedButRunAborted="0" notRunnable="0" notExecuted="0" disconnected="0" warning="0" completed="0" inProgress="0" pending="0" /><Output><StdOut>test output</StdOut></Output></ResultSummary>
</TestRun>'''
    return fixture.encode()


class NativeResultTests(unittest.TestCase):
    def setUp(self):
        self.raw = _fixture()

    def _parse(self, raw=None, deadline=None):
        return subject.parse_native_validator_trx(
            self.raw if raw is None else raw,
            deadline=time.monotonic() + 10 if deadline is None else deadline,
        )

    def test_accepts_exact_single_method_and_sets_fixed_nonclaims(self):
        result = self._parse()
        self.assertEqual(subject.TEST_METHOD, result.test_name)
        self.assertEqual('Passed', result.outcome)
        self.assertFalse(result.validator_executed)
        self.assertFalse(result.native_acceptance)
        self.assertFalse(result.qualification_complete)
        with self.assertRaises(FrozenInstanceError):
            result.native_acceptance = True

    def test_rejects_truncated_oversized_and_expired_inputs(self):
        with self.assertRaisesRegex(ValueError, 'trx-size'):
            self._parse(b'')
        with self.assertRaisesRegex(ValueError, 'trx-size'):
            self._parse(b'x' * (subject.MAX_TRX_BYTES + 1))
        with self.assertRaisesRegex(ValueError, 'deadline'):
            self._parse(deadline=time.monotonic() - 1)
        with self.assertRaisesRegex(ValueError, 'xml-parse'):
            self._parse(self.raw[:-10])

    def test_rejects_entities_namespace_aliases_and_extra_results(self):
        with self.assertRaisesRegex(ValueError, 'unsafe-declaration'):
            self._parse(self.raw.replace(b'<TestRun', b'<!DOCTYPE TestRun [<!ENTITY x "y">]><TestRun', 1))
        with self.assertRaisesRegex(ValueError, 'namespace'):
            self._parse(self.raw.replace(b'<UnitTestResult ',
                b'<x:UnitTestResult xmlns:x="urn:foreign" ', 1))
        extra = self.raw.replace(b'</Results>', b'<UnitTestResult /></Results>')
        with self.assertRaisesRegex(ValueError, 'result-count'):
            self._parse(extra)

    def test_rejects_wrong_method_outcome_counters_and_test_binding(self):
        cases = (
            (self.raw.replace(subject.TEST_NAME.encode(), b'OtherMethod', 1), 'result-method-outcome'),
            (self.raw.replace(b'outcome="Passed"', b'outcome="Skipped"', 1), 'result-method-outcome'),
            (self.raw.replace(b'failed="0"', b'failed="1"'), 'counter-value'),
            (self.raw.replace(b'passed="1"', b'passed="+1"'), 'counter-value'),
            (self.raw.replace(b'passed="1"', b'passed="' + b'9' * 10000 + b'"'), 'counter-value'),
            (self.raw.replace(b'id="10000000-0000-4000-8000-000000000001"',
                              b'id="different-id"', 1), 'definition-identity'),
            (self.raw.replace(b'className="' + subject.TEST_CLASS.encode(), b'className="Foreign.Class'),
             'definition-method'),
            (self.raw.replace(b'codeBase="/isolated/source/Evidence/', b'codeBase="/unrelated/Other/'),
             'definition-method'),
            (self.raw.replace(b'codeBase="/isolated/source/Evidence/',
                              b'codeBase="/evil/../isolated/source/Evidence/'), 'definition-method'),
            (self.raw.replace(b'executionId="20000000-0000-4000-8000-000000000002" testId=',
                              b'executionId="30000000-0000-4000-8000-000000000003" testId=', 1),
             'definition-execution'),
        )
        for raw, expected in cases:
            with self.subTest(expected=expected):
                with self.assertRaisesRegex(ValueError, expected):
                    self._parse(raw)

    def test_command_builder_is_closed_to_fixed_assembly_and_filter(self):
        with tempfile.TemporaryDirectory() as name:
            root = Path(name).resolve()
            source = root / 'source'
            output = root / 'output'
            source.mkdir(mode=0o700)
            output.mkdir(mode=0o700)
            os.chmod(source, 0o700)
            os.chmod(output, 0o700)
            (source / 'ForgeTrust.AppSurface.slnx').write_text('')
            (source / subject.PROJECT).parent.mkdir(parents=True, mode=0o700)
            (source / subject.PROJECT).write_text('')
            assembly = source / subject.ASSEMBLY
            assembly.parent.mkdir(parents=True, mode=0o700)
            assembly.write_bytes(b'fixture path only')
            argv = subject.fixed_validator_argv(source, output)
            self.assertEqual('dotnet', argv[0])
            self.assertEqual('vstest', argv[1])
            self.assertEqual(str(assembly), argv[2])
            self.assertEqual(f'--TestCaseFilter:FullyQualifiedName={subject.TEST_METHOD}', argv[3])
            self.assertEqual(f'--logger:trx;LogFileName={subject.RESULT_FILE}', argv[4])
            self.assertEqual(f'--ResultsDirectory:{output}', argv[5])
            self.assertFalse(any('runsettings' in arg.lower() or 'collector' in arg.lower() for arg in argv))
            (output / subject.RESULT_FILE).write_bytes(b'occupied')
            with self.assertRaisesRegex(ValueError, 'output-root-not-empty'):
                subject.fixed_validator_argv(source, output)

    def test_command_builder_rejects_unsafe_roots_and_symlink_assembly(self):
        with tempfile.TemporaryDirectory() as name:
            base = Path(name).resolve()
            source = base / 'source'
            output = base / 'output'
            source.mkdir(mode=0o700)
            output.mkdir(mode=0o700)
            os.chmod(source, 0o700)
            os.chmod(output, 0o700)
            (source / 'ForgeTrust.AppSurface.slnx').write_text('')
            (source / subject.PROJECT).parent.mkdir(parents=True, mode=0o700)
            (source / subject.PROJECT).write_text('')
            assembly = source / subject.ASSEMBLY
            assembly.parent.mkdir(parents=True, mode=0o700)
            target = base / 'outside.dll'
            target.write_bytes(b'x')
            assembly.symlink_to(target)
            with self.assertRaisesRegex(ValueError, 'assembly-path'):
                subject.fixed_validator_argv(source, output)
            assembly.unlink()
            with self.assertRaisesRegex(ValueError, 'root-overlap'):
                nested = source / 'child'
                nested.mkdir()
                os.chmod(nested, 0o700)
                subject.fixed_validator_argv(source, nested)


if __name__ == '__main__':
    unittest.main()
