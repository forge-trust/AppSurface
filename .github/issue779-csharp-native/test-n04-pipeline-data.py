"""Unexecuted detached DATA controls. No Runner/root/subprocess or admission."""
import ast
import copy
import hashlib
import io
import json
import os
from pathlib import Path
import re
import stat
import tarfile
import tempfile
import time
import unittest

DONOR = Path(__file__).resolve().parent
HISTORICAL_RUNNER_SHA = '9b9ac43cf8b4564fbe0de8c6d86617a377ba888b048d1366e652759a109b7620'
FILTER_SHA = 'b0c31c1fa931b7de996a635f4cc1a636c2e5a8f1b0744eeac1a3b56526514bf4'
RETAINER_SHA = '096b1f8672839560ded52dd18b0faa19123164b854c55aacec43352618ba6346'
N04_NAMES = frozenset(('logs/n04-helper.stderr', 'logs/n04-helper.stdout',
    'n04-account-ids.tsv', 'n04-consistency.json', 'n04-descriptor.json',
    'n04-helper-live.json', 'n04-helper-result.json', 'n04-kernel-observation.json',
    'n04-root-failure.json', 'n04-root-terminal.txt'))

def digest(raw):
    return hashlib.sha256(raw).hexdigest()

def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(',', ':')).encode() + b'\n'

def load_data_functions():
    """Future suite executes only exact approved data defs, never top-level candidate."""
    path = Path(os.environ.get('N04_PIPELINE_RUNNER', str(DONOR / 'run-native.py')))
    expected = os.environ.get('N04_PIPELINE_RUNNER_SHA256')
    if expected is None or re.fullmatch(r'[0-9a-f]{64}', expected) is None:
        raise ValueError('explicit-candidate-pin-required')
    raw = path.read_bytes()
    if digest(raw) != expected:
        raise ValueError('candidate-data-source-pin')
    tree = ast.parse(raw)
    names = {'Rejected', 'require', 'unique', 'ident', 'read', 'decode', 'sha',
             'validate_n04_helper_handoff', 'read_n04_helper_handoff',
             'require_data', 'unique_pairs', 'decode_data', 'exact_object',
             'inspect_retention', 'inspect_archive'}
    assignments = {'PINS', 'ARCHIVE_NAMES', 'NEGATIVE_CAPS'}
    selected = []
    seen = set()
    for node in tree.body:
        if isinstance(node, (ast.FunctionDef, ast.ClassDef)) and node.name in names:
            selected.append(node); seen.add(node.name)
        elif isinstance(node, ast.Assign) and all(isinstance(t, ast.Name) and t.id in assignments for t in node.targets):
            selected.append(node)
    if seen != names:
        raise ValueError('data-definition-set')
    scope = dict(os=os, stat=stat, time=time, json=json, re=re,
                 hashlib=hashlib, io=io, tarfile=tarfile)
    exec(compile(ast.fix_missing_locations(ast.Module(body=selected, type_ignores=[])),
                 '<pinned-data-only-definitions>', 'exec'), scope)
    if 'Runner' in scope or 'main' in scope:
        raise ValueError('operational-definition-forbidden')
    return scope

class PipelineDataControls(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.data = load_data_functions()

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)
        helper = self.root / 'helper'; helper.mkdir()
        contents = {'NativeRootCoordinator.dll': b'detached-FDD-shaped-data-not-an-assembly',
                    'NativeRootCoordinator.deps.json': b'{}\n',
                    'NativeRootCoordinator.runtimeconfig.json': b'{}\n',
                    'NativeRootCoordinator.pdb': b'detached-symbol-data'}
        files = {}
        for name, raw in contents.items():
            p = helper / name; p.write_bytes(raw); p.chmod(0o444)
            files[name] = {'mode':'0444', 'bytes':len(raw), 'sha256':digest(raw)}
        self.nodes = {'schema':'issue779-build-node-inventory-v1', 'root_name':'tool',
                      'directories':{'.':{'mode':'0555'}}, 'files':files}
        pins = ast.literal_eval(next(n.value for n in ast.parse((DONOR / 'build-n04-helper.sh').read_text().split("<<'PY'\n", 1)[1].rsplit('\nPY', 1)[0]).body
                                     if isinstance(n, ast.Assign) and any(isinstance(t, ast.Name) and t.id=='PINS' for t in n.targets)))
        self.value = {'schema':'issue779-n04-helper-build-handoff-v1', 'exit':0,
            'authority':False, 'native_execution':False, 'commands':[], 'source_pins':pins,
            'sdk_required':'10.0.401', 'runtime_required':'10.0.12',
            'recipe_sha256':self.data['PINS']['build-n04-helper.sh'],
            'sdk_sha256':'1'*64, 'helper_root':str(helper), 'helper_files':4,
            'helper_directories':1, 'helper_bytes':sum(map(len, contents.values())),
            'helper_entry_sha256':files['NativeRootCoordinator.dll']['sha256']}
        for i in range(3):
            name='build-%02d.log'%i; raw=b'detached command data\n'; (self.root/name).write_bytes(raw)
            self.value['commands'].append(dict(exit=0, waited=True, group_absent=True,
                forced_cleanup=False, error=False, log=name, log_bytes=len(raw)))
        helper.chmod(0o555)
        self.save_maps()

    def tearDown(self):
        (self.root/'helper').chmod(0o700)
        self.tmp.cleanup()

    def save_maps(self):
        raw=canonical(self.nodes)
        rows=''.join('%s\t%s\t%s\n'%(v['mode'],v['sha256'],n)
                     for n,v in sorted(self.nodes['files'].items())).encode()
        (self.root/'helper-nodes.json').write_bytes(raw); (self.root/'helper.tsv').write_bytes(rows)
        self.value['helper_nodes_sha256']=digest(raw); self.value['helper_tsv_sha256']=digest(rows)
        self.save_receipt()

    def save_receipt(self):
        (self.root/'helper-build-receipt.json').write_bytes(canonical(self.value))

    def read_handoff(self):
        return self.data['read_n04_helper_handoff'](self.root, time.monotonic()+2)

    def test_valid_four_file_handoff(self):
        raw,value=self.read_handoff()
        self.assertEqual(value,self.value); self.assertEqual(raw,canonical(value))

    def test_closed_schema(self):
        self.value['extra']='private-canary'; self.save_receipt()
        with self.assertRaises(ValueError): self.read_handoff()

    def test_bool_not_integer(self):
        self.value['helper_files']=True; self.save_receipt()
        with self.assertRaises(ValueError): self.read_handoff()

    def test_command_boolean_exit(self):
        self.value['commands'][0]['exit']=False; self.save_receipt()
        with self.assertRaises(ValueError): self.read_handoff()

    def test_recipe_and_source_pins(self):
        for key in ('recipe_sha256','source_pins'):
            value=copy.deepcopy(self.value)
            if key=='source_pins': value[key]['Program.cs']='2'*64
            else: value[key]='2'*64
            with self.subTest(key=key), self.assertRaises(ValueError):
                self.data['validate_n04_helper_handoff'](value,self.data['PINS']['build-n04-helper.sh'])

    def test_map_digest(self):
        (self.root/'helper.tsv').write_bytes(b'changed\n')
        with self.assertRaises(ValueError): self.read_handoff()

    def test_missing_deps_membership(self):
        del self.nodes['files']['NativeRootCoordinator.deps.json']
        self.value['helper_files']=3
        self.value['helper_bytes']=sum(v['bytes'] for v in self.nodes['files'].values())
        self.save_maps()
        with self.assertRaises(ValueError): self.read_handoff()

    def test_log_diagnostic(self):
        raw=b'error CS0001: detached-canary\n'; (self.root/'build-00.log').write_bytes(raw)
        self.value['commands'][0]['log_bytes']=len(raw);self.save_receipt()
        with self.assertRaises(ValueError): self.read_handoff()

    def test_log_length(self):
        self.value['commands'][1]['log_bytes']+=1;self.save_receipt()
        with self.assertRaises(ValueError): self.read_handoff()

    def test_pending_and_late_markers(self):
        for name in ('helper-build-receipt.pending.json','late-helper-publication-failure.json'):
            p=self.root/name;p.write_bytes(b'{}\n')
            with self.subTest(name=name),self.assertRaises(ValueError):self.read_handoff()
            p.unlink()

    def test_symlink_receipt_rejected(self):
        p=self.root/'helper-build-receipt.json';p.rename(self.root/'original.json');p.symlink_to('original.json')
        with self.assertRaises(ValueError):self.read_handoff()

    def test_duplicate_receipt_member(self):
        raw=canonical(self.value).rstrip(b'\n');raw=raw[:-1]+b',"exit":0}\n'
        (self.root/'helper-build-receipt.json').write_bytes(raw)
        with self.assertRaises(ValueError):self.read_handoff()

    def test_expired_original_read_deadline(self):
        with self.assertRaises(ValueError):
            self.data['read_n04_helper_handoff'](self.root,time.monotonic()-1)

    def archive(self, extra=None):
        entries={name:b'' for name in N04_NAMES}
        if extra:entries[extra]=b''
        selection={'schema':'issue779-native-retention-selection-v1','selection_status':'one-namespace',
            'data_file_count':len(entries),'data_bytes':0,'missing_fixed_file_count':0}
        entries['retention-selection.json']=canonical(selection)
        output=io.BytesIO()
        with tarfile.open(fileobj=output,mode='w:',format=tarfile.USTAR_FORMAT) as archive:
            for name,raw in sorted(entries.items()):
                info=tarfile.TarInfo(name);info.size=len(raw);info.mode=0o600
                info.uid=info.gid=info.mtime=0;archive.addfile(info,io.BytesIO(raw))
        return output.getvalue()

    def test_exact_ten_retainer_names(self):
        raw=(DONOR/'retain-native.sh').read_bytes()
        self.assertEqual(digest(raw),RETAINER_SHA)
        matched=re.search(r'^inner\+=\((logs/n04-helper\.stderr[^\n]+)\)$',raw.decode(),re.M)
        self.assertIsNotNone(matched)
        self.assertEqual(set(matched.group(1).split()),N04_NAMES)
        self.assertTrue(N04_NAMES<=self.data['ARCHIVE_NAMES'])

    def test_whitelist_positive_actual_retention_validator(self):
        value=self.data['inspect_retention'](self.archive(),self.data['ARCHIVE_NAMES'])
        self.assertEqual(set(value),set(N04_NAMES)|{'retention-selection.json'})

    def test_unknown_archive_name(self):
        with self.assertRaises(ValueError):
            self.data['inspect_retention'](self.archive('n04-unknown.json'),self.data['ARCHIVE_NAMES'])

    def test_filter_is_actual_shared_source(self):
        raw=(DONOR/'n04-helper-receipt-filter.jq').read_bytes()
        self.assertEqual(digest(raw),FILTER_SHA)
        for name in ('acquire-inbound.sh','prepare-root-inputs-v2.sh'):
            self.assertIn(raw.decode().strip(),(DONOR/name).read_text())

    def test_five_tree_and_overlap_static_interfaces(self):
        # Static source inclusion only: does not execute or emulate Bash guards.
        for name in ('acquire-inbound.sh','prepare-root-inputs-v2.sh'):
            body=(DONOR/name).read_text()
            self.assertIn('source tool runtime helper n04helper',body)
            self.assertIn('helper-overlap' if name=='acquire-inbound.sh' else 'overlapping-inputs',body)
            self.assertIn('${roots[$a]} != "${roots[$b]}"',body)
            self.assertIn('${roots[$a]} != "${roots[$b]}/"*',body)
        transport=(DONOR/'prepare-root-inputs-v2.sh').read_text()
        for suffix in ('root','map','map-sha256','nodes','nodes-sha256','build-receipt','build-receipt-sha256','entry-sha256'):
            self.assertIn('n04helper-'+suffix,transport)
        self.assertIn('/var/lib/appsurface-evidence-n04-helpers',transport)

if __name__=='__main__':
    unittest.main()
