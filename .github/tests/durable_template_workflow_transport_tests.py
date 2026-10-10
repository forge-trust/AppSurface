#!/usr/bin/env python3
"""Executes #806 receipt transport and native acquisition with owned command fixtures."""
import json
import os
import subprocess
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
WORKFLOW = ROOT / '.github/workflows/durable-template-evidence.yml'
MAC_ACQUISITION = ROOT / '.github/scripts/resolve-macos-native-postgresql.sh'

class DurableTemplateTransportTests(unittest.TestCase):
    def test_all_five_receipts_are_required_and_prior_attempts_cannot_fill_a_gap(self):
        source = WORKFLOW.read_text()
        start = source.index('      - name: Resolve exact receipt IDs')
        script = '\n'.join(line[10:] for line in source[start:].split('        run: |\n', 1)[1].splitlines() if line.startswith('          '))
        with tempfile.TemporaryDirectory(prefix='issue806-transport-') as temporary:
            root = Path(temporary)
            gh = root / 'gh'
            gh.write_text('#!/usr/bin/env python3\nimport os,subprocess\nraise SystemExit(subprocess.run(["jq","-r",__import__("sys").argv[-1],os.environ["PAGES"]]).returncode)\n')
            gh.chmod(0o755)
            suffixes = ['linux-x64', 'osx-arm64', 'win-x64', 'timing-primed', 'timing-cold']
            entries = [dict(id=index+1, name='durable-template-99-2-'+suffix, expired=False) for index, suffix in enumerate(suffixes)]
            pages = root/'pages.json'
            output = root/'output'
            environment = dict(os.environ, PATH=str(root)+os.pathsep+os.environ['PATH'], GITHUB_RUN_ID='99',GITHUB_RUN_ATTEMPT='2',GITHUB_REPOSITORY='fixture/repo',GITHUB_OUTPUT=str(output),PAGES=str(pages))
            for case in ['all','missing','expired','prior-attempt','duplicate','duplicate-masks-missing']:
                current = [dict(entry) for entry in entries]
                if case == 'missing': current.pop()
                if case == 'expired': current[-1]['expired'] = True
                if case == 'prior-attempt': current[-1]['name'] = 'durable-template-99-1-timing-cold'
                if case == 'duplicate': current.append(dict(current[-1],id=6))
                if case == 'duplicate-masks-missing': current[-1] = dict(current[-2],id=6)
                pages.write_text(json.dumps(dict(artifacts=current)))
                output.write_text('')
                result = subprocess.run(['bash','-c',script],env=environment,capture_output=True,text=True,timeout=10)
                with self.subTest(case=case):
                    self.assertEqual(result.returncode == 0, case == 'all')
                    self.assertEqual(output.read_text(), 'ids=1,2,3,4,5\n' if case == 'all' else '')

    def test_candidate_bytes_and_exact_receipt_ids_survive_every_publisher_boundary(self):
        source=WORKFLOW.read_text()
        self.assertIn('needs: [native-template, template-timing]',source)
        self.assertIn('core.autocrlf false',source)
        self.assertIn('artifact-ids: ${{ inputs.producer_artifact_id }}',source)
        self.assertIn('clock: [primed, cold]',source)
        self.assertIn('docker rm --force --volumes "$id"',source)
        for name in ['nuget-prerelease-publish.yml','nuget-stable-publish.yml']:
            publisher=(WORKFLOW.parent/name).read_text()
            self.assertIn('artifact-ids: ${{ needs.durable-template-evidence.outputs.receipt_artifact_ids }}',publisher)
            self.assertLess(publisher.index('verify-durable-template-evidence'),publisher.index('publish-'+('prerelease' if 'prerelease' in name else 'stable')+' --durable-template-evidence'))

class DurableTemplateMacAcquisitionTests(unittest.TestCase):
    def test_installed_formula_acquisition_and_failures_never_return_partial_tools(self):
        with tempfile.TemporaryDirectory(prefix='issue806-mac-acquisition-') as temporary:
            root = Path(temporary)
            brew = root / 'brew'
            brew.write_text('''#!/usr/bin/env python3
import json, os, sys
from pathlib import Path

root = Path(os.environ['BREW_FIXTURE'])
prefix = root / 'installed formula'
case = os.environ['BREW_CASE']
args = sys.argv[1:]
with (root / 'calls.jsonl').open('a') as log:
    log.write(json.dumps(args) + '\\n')
if args == ['--prefix', 'postgresql@16']:
    # The real Homebrew command can succeed without an installed keg.
    print(prefix)
    raise SystemExit(0)
if args == ['--prefix', '--installed', 'postgresql@16']:
    if not (root / 'installed').exists():
        raise SystemExit(1)
    if case == 'post-install-prefix-failure':
        raise SystemExit(4)
    print({'blank-prefix': '', 'relative-prefix': 'relative',
           'multiline-prefix': str(prefix) + '\\nsecond-prefix'}.get(case, str(prefix)))
    raise SystemExit(0)
if args == ['install', 'postgresql@16']:
    print('installation progress on stdout')
    print('installation progress on stderr', file=sys.stderr)
    if case == 'install-failure':
        raise SystemExit(3)
    (prefix / 'bin').mkdir(parents=True)
    for tool in ['initdb', 'postgres', 'pg_ctl', 'psql']:
        if case == 'incomplete-install' and tool == 'psql':
            continue
        (prefix / 'bin' / tool).write_text('fixture tool')
    (root / 'installed').write_text('installed')
    raise SystemExit(0)
raise SystemExit('unexpected brew arguments')
''')
            brew.chmod(0o755)
            installed_cases = {'installed', 'symlink-prefix', 'incomplete-installed', 'directory-tool',
                               'blank-prefix', 'relative-prefix', 'multiline-prefix'}
            cases = ['missing', 'installed', 'symlink-prefix', 'install-failure', 'post-install-prefix-failure',
                     'incomplete-install', 'incomplete-installed', 'directory-tool',
                     'blank-prefix', 'relative-prefix', 'multiline-prefix']
            for case in cases:
                with self.subTest(case=case):
                    fixture = root / case
                    fixture.mkdir()
                    prefix = fixture / 'installed formula'
                    if case in installed_cases:
                        (fixture / 'installed').write_text('installed')
                        if case == 'symlink-prefix':
                            cellar = fixture / 'physical keg'
                            cellar.mkdir()
                            prefix.symlink_to(cellar, target_is_directory=True)
                        (prefix / 'bin').mkdir(parents=True)
                        for tool in ['initdb', 'postgres', 'pg_ctl', 'psql']:
                            if case == 'incomplete-installed' and tool == 'psql':
                                continue
                            target = prefix / 'bin' / tool
                            if case == 'directory-tool' and tool == 'pg_ctl':
                                target.mkdir()
                            else:
                                target.write_text('fixture tool')
                    environment = dict(os.environ, PATH=str(root)+os.pathsep+os.environ['PATH'],
                                       BREW_FIXTURE=str(fixture), BREW_CASE=case)
                    if case == 'missing':
                        bare_prefix = subprocess.run(['brew', '--prefix', 'postgresql@16'],
                                                     env=environment, capture_output=True, text=True, timeout=10)
                        self.assertEqual(bare_prefix.returncode, 0)
                        self.assertFalse(prefix.exists())
                        (fixture / 'calls.jsonl').unlink()
                    result = subprocess.run(['bash', str(MAC_ACQUISITION)], env=environment,
                                            stdin=subprocess.DEVNULL, capture_output=True, text=True, timeout=10)
                    succeeded = case in {'missing', 'installed', 'symlink-prefix'}
                    self.assertEqual(result.returncode == 0, succeeded, result.stderr)
                    self.assertEqual(result.stdout, str((prefix / 'bin').resolve())+'\n' if succeeded else '')
                    calls = [json.loads(line) for line in (fixture / 'calls.jsonl').read_text().splitlines()]
                    expected = [['--prefix', '--installed', 'postgresql@16']]
                    if case not in installed_cases:
                        expected.append(['install', 'postgresql@16'])
                        if case != 'install-failure':
                            expected.append(['--prefix', '--installed', 'postgresql@16'])
                    self.assertEqual(calls, expected)
                    if case in {'blank-prefix', 'relative-prefix', 'multiline-prefix'}:
                        self.assertIn('installed prefix is missing or invalid', result.stderr)
                    if case in {'incomplete-install', 'incomplete-installed', 'directory-tool'}:
                        self.assertIn('tool set is incomplete', result.stderr)
                    if case == 'missing':
                        self.assertIn('installation progress on stdout', result.stderr)
                        self.assertIn('installation progress on stderr', result.stderr)

    def test_workflow_keeps_complete_tool_validation_after_mac_acquisition(self):
        source = WORKFLOW.read_text()
        step = source.split('      - name: Resolve private native PostgreSQL tools\n', 1)[1].split('      - name:', 1)[0]
        self.assertIn('$bin = & bash .github/scripts/resolve-macos-native-postgresql.sh', step)
        self.assertIn("if ($LASTEXITCODE -ne 0) { throw 'Native PostgreSQL acquisition failed.' }", step)
        self.assertLess(step.index('resolve-macos-native-postgresql.sh'), step.index("@('initdb', 'postgres', 'pg_ctl', 'psql')"))
        self.assertIn('-PathType Leaf', step)
        self.assertLess(step.index('-PathType Leaf'), step.index('NATIVE_PG_BIN=$bin'))
        self.assertIn('timeout-minutes: 30', source.split('  template-timing:', 1)[0])

if __name__ == '__main__': unittest.main(verbosity=2)
