"""Actual fixed capture-body controls with labelled data and mocked privileged I/O."""
import importlib.util
import pathlib
import sys
import types
import unittest
from unittest import mock
import test_slot_archive_validator as data

ROOT = pathlib.Path(__file__).parent

def load(case):
    spec = importlib.util.spec_from_file_location('capture_' + case, ROOT / ('capture-' + case.lower() + '.py'))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module

class CaptureContractTests(unittest.TestCase):
    def run_body(self, case, mutation=None):
        capture = load(case)
        values = data.archive(case)
        root = '/run/appsurface-evidence-' + data.GENERATION
        private = '/var/lib/appsurface-evidence-input-' + data.GENERATION + '/control'
        files = {
            private + '/logs/n01.stderr': b''.join(values[n] for n in capture.SLOT_FRAME_MEMBERS),
            private + '/logs/n01.stdout': b'',
            root + '/worker/worker-control.json': values['negative-descriptor.json'],
            root + ('/output/evidence' if case == 'N05' else '/raw-results') + '/allocation-sentinel': capture.SLOT_SENTINEL_BYTES,
        }
        if mutation:
            mutation(files, values, capture, root, private)
        def stat(mode, uid=0, gid=0):
            return types.SimpleNamespace(st_mode=mode, st_uid=uid, st_gid=gid, st_dev=1, st_ino=2,
                st_nlink=1, st_size=14, st_mtime_ns=1, st_ctime_ns=1)
        link = stat(0o120777)
        stats = {private: stat(0o40700), root + '/worker/worker-control.json': stat(0o100440, 0, data.WORKER_GID),
                 root + '/raw-results': stat(0o40710, data.SUBJECT_UID, data.RESULTS_GID), root + '/output/evidence': link}
        names = {root + '/output': ['evidence'], root + '/output/evidence': ['allocation-sentinel'],
                 root + '/raw-results': [] if case == 'N05' else ['allocation-sentinel']}
        saved = {}
        with mock.patch.object(capture.sys, 'argv', ['capture', private, root, data.GENERATION, 'c' * 64]), \
             mock.patch.object(capture.os, 'geteuid', return_value=0), \
             mock.patch.object(capture.os, 'lstat', side_effect=lambda path: stats[path]), \
             mock.patch.object(capture.os, 'listdir', side_effect=lambda path: names[path]), \
             mock.patch.object(capture.os, 'readlink', return_value='../raw-results'), \
             mock.patch.object(capture, 'read_private', side_effect=lambda path, *args: files[path]), \
             mock.patch.object(capture, 'ident', return_value=link), \
             mock.patch.object(capture, 'save', side_effect=lambda name, raw: saved.setdefault(name, raw)):
            try:
                capture.body()
            except (ValueError, KeyError):
                return saved, False
        return saved, True

    def test_actual_fixed_capture_body_preserves_all_framed_bytes_for_both_cases(self):
        for case in ('N05', 'N06'):
            with self.subTest(case=case):
                saved, ok = self.run_body(case)
                self.assertTrue(ok)
                self.assertEqual(saved, data.archive(case))
                self.assertTrue(all(saved[n].endswith(b'\n') and saved[n].count(b'\n') == 1
                    for n in data.NATIVE.SLOT_FRAME_MEMBERS))

    def test_sentinel_failure_cannot_write_any_archive_member(self):
        def change(files, values, capture, root, private):
            files[root + '/output/evidence/allocation-sentinel'] = b'changed'
        saved, ok = self.run_body('N05', change)
        self.assertFalse(ok)
        self.assertEqual(saved, {})

    def test_descriptor_bound_policy_failure_cannot_write_any_member(self):
        def change(files, values, capture, root, private):
            name = root + '/worker/worker-control.json'
            files[name] = files[name].replace(b'c' * 64, b'f' * 64)
        saved, ok = self.run_body('N06', change)
        self.assertFalse(ok)
        self.assertEqual(saved, {})

    def test_changed_raw_frame_cannot_write_any_member(self):
        def change(files, values, capture, root, private):
            name = private + '/logs/n01.stderr'
            files[name] = files[name].replace(b'"pending_stop":true', b'"pending_stop":false')
        saved, ok = self.run_body('N05', change)
        self.assertFalse(ok)
        self.assertEqual(saved, {})

    def test_missing_or_double_frame_lf_rejects(self):
        for case in ('N05', 'N06'):
            for ending in (b'', b'\n\n'):
                with self.subTest(case=case, ending=ending):
                    values = data.archive(case)
                    values['negative-post-join.json'] = values['negative-post-join.json'].rstrip(b'\n') + ending
                    data.reject(self, values, case)

    def test_actual_one_row_tsv_rejects_labelled_or_noncanonical_alias(self):
        for raw in (b'worker_uid\t65010\nworker_gid\t65011\n', b'065010\t65011\t65012\t65013\t65014\n',
                    b'65010\t65011\t65012\t65013\t65014\n\n'):
            values = data.archive('N05')
            values['negative-account-ids.tsv'] = raw
            data.reject(self, values)

    def test_fixed_capture_owns_case_selection_without_argument_or_env_selector(self):
        for case in ('N05', 'N06'):
            capture = load(case)
            self.assertEqual(capture.CASE, case)
            self.assertNotIn('getenv(', (ROOT / ('capture-' + case.lower() + '.py')).read_text())

if __name__ == '__main__':
    unittest.main()
