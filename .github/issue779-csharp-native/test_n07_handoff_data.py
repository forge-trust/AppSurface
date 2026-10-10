"""Defined-only detached receipt controls. Never run the helper, Runner, or a unit.

Future authorized execution extracts the actual candidate data validator by AST;
it neither imports the orchestrator nor mirrors an unused schema implementation.
These records are synthetic DATA and never native or ownership evidence.
"""
import ast
import copy
from pathlib import Path
import re
import unittest


class HandoffData(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        source = (Path(__file__).parent / 'run-native.py').read_text()
        tree = ast.parse(source)
        selected = [n for n in tree.body if isinstance(n, ast.FunctionDef)
                    and n.name in ('require', 'validate_n07_helper_handoff')]
        if len(selected) != 2:
            raise ValueError('actual-validator-definition-count')
        class Rejected(ValueError):
            pass
        namespace = {'re': re, 'Rejected': Rejected}
        exec(compile(ast.Module(body=selected, type_ignores=[]), '<actual-n07-data-validator>', 'exec'), namespace)
        cls.validate = staticmethod(namespace['validate_n07_helper_handoff'])
        assertion = next(n for n in ast.walk(selected[-1]) if isinstance(n, ast.Compare)
                         and isinstance(n.left, ast.Subscript)
                         and isinstance(n.left.slice, ast.Constant) and n.left.slice.value == 'source_pins')
        cls.pins = ast.literal_eval(assertion.comparators[0])
        cls.recipe = 'c' * 64

    def value(self):
        return dict(schema='issue779-n07-helper-build-handoff-v1', exit=0, authority=False,
                    native_execution=False, recipe_sha256=self.recipe, sdk_required='10.0.401',
                    runtime_required='10.0.12', sdk_sha256='a' * 64, source_pins=copy.deepcopy(self.pins),
                    helper_files=4, helper_directories=1, helper_bytes=100,
                    helper_tsv_sha256='b' * 64, helper_nodes_sha256='d' * 64,
                    helper_entry_sha256='e' * 64, helper_root='/detached/data-only/helper',
                    commands=[dict(error=False, exit=0, forced_cleanup=False, group_absent=True,
                                   log='build-%02d.log' % i, log_bytes=0, waited=True) for i in range(3)])

    def rejects(self, value):
        with self.assertRaises(ValueError):
            self.validate(value, self.recipe)

    def test_closed_valid_data_neighbor(self):
        self.validate(self.value(), self.recipe)

    def test_missing_source_input(self):
        value = self.value(); value['source_pins'].pop('N07CoordinatorData.cs'); self.rejects(value)

    def test_wrong_recipe(self):
        value = self.value(); value['recipe_sha256'] = 'f' * 64; self.rejects(value)

    def test_bool_not_exit_integer(self):
        value = self.value(); value['exit'] = False; self.rejects(value)

    def test_pending_or_wrong_schema(self):
        value = self.value(); value['schema'] = 'issue779-n04-helper-build-handoff-v1'; self.rejects(value)

    def test_nonzero_child(self):
        value = self.value(); value['commands'][1]['exit'] = 1; self.rejects(value)

    def test_unjoined_group(self):
        value = self.value(); value['commands'][1]['group_absent'] = False; self.rejects(value)

    def test_forced_cleanup(self):
        value = self.value(); value['commands'][1]['forced_cleanup'] = True; self.rejects(value)

    def test_missing_reap(self):
        value = self.value(); value['commands'][1]['waited'] = False; self.rejects(value)

    def test_log_bound(self):
        value = self.value(); value['commands'][1]['log_bytes'] = 8388609; self.rejects(value)

    def test_file_count_bool_and_node_bound(self):
        value = self.value(); value['helper_files'] = True; self.rejects(value)
        value = self.value(); value['helper_files'] = 8192; self.rejects(value)

    def test_no_authority_or_unknown_key(self):
        value = self.value(); value['authority'] = True; self.rejects(value)
        value = self.value(); value['native_acceptance'] = True; self.rejects(value)


if __name__ == '__main__':
    unittest.main()
