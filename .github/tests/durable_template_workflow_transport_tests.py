#!/usr/bin/env python3
"""Executes the #806 immutable receipt resolver using owned fake GitHub pages."""
import json
import os
import re
import subprocess
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
WORKFLOW = ROOT / '.github/workflows/durable-template-evidence.yml'

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

if __name__ == '__main__': unittest.main(verbosity=2)
