"""Real file/process procedure controls; never construct a root coverage capability."""
import atexit
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import selectors
import signal
import stat
import sys
import tempfile
import time
import unittest
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location('private_product_coverage', Path(__file__).with_name('product-coverage.py'))
M = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(M)


class FileControls(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix='product-owner-', dir='/tmp')
        self.root = Path(self.temporary.name).resolve()
        self.addCleanup(self.temporary.cleanup)
        self.fd = os.open(self.root, os.O_RDONLY | os.O_DIRECTORY)
        self.addCleanup(os.close, self.fd)

    def read(self, name, **options):
        return M.read_file(self.fd, name, time.monotonic()+2, uid=os.getuid(), **options)

    def test_exact_file_bound_and_oversize_before_read(self):
        (self.root/'small').write_bytes(b'abc')
        self.assertEqual(b'abc', self.read('small', cap=3)[0])
        with patch.object(M.os, 'read', side_effect=AssertionError('read before bound')):
            with self.assertRaises(M.ProductCoverageError): self.read('small', cap=2)

    def test_hardlink_symlink_and_fifo_do_not_read(self):
        (self.root/'original').write_bytes(b'sentinel')
        os.link(self.root/'original', self.root/'linked')
        (self.root/'alias').symlink_to('original')
        os.mkfifo(self.root/'pipe', 0o600)
        with patch.object(M.os, 'read', side_effect=AssertionError('unsafe read')):
            for name in ('linked', 'alias', 'pipe'):
                with self.subTest(name=name), self.assertRaises((M.ProductCoverageError, OSError)):
                    self.read(name)
        self.assertEqual(b'sentinel', (self.root/'original').read_bytes())

    def test_actual_substitution_during_read_rejects(self):
        (self.root/'original').write_bytes(b'abc')
        actual = os.read
        called = False
        def substitute(fd, count):
            nonlocal called
            value = actual(fd, count)
            if not called:
                called = True
                os.rename(self.root/'original', self.root/'retired')
                (self.root/'original').write_bytes(b'abc')
            return value
        with patch.object(M.os, 'read', side_effect=substitute):
            with self.assertRaises(M.ProductCoverageError): self.read('original')

    def test_read_failure_closes_actual_fd(self):
        (self.root/'original').write_bytes(b'abc')
        closed = []
        actual = os.close
        def record(fd):
            actual(fd)
            closed.append(fd)
        with patch.object(M.os, 'read', side_effect=OSError('private canary')), patch.object(M.os, 'close', side_effect=record):
            with self.assertRaises(OSError): self.read('original')
        self.assertEqual(1, len(closed))
        with self.assertRaises(OSError): os.fstat(closed[0])

    def test_late_close_never_publishes_file(self):
        (self.root/'original').write_bytes(b'abc')
        deadline = time.monotonic()+2
        actual = os.close
        expired = False
        def close(fd):
            nonlocal expired
            actual(fd)
            expired = True
        clock = lambda: deadline+1 if expired else deadline-1
        with patch.object(M.os, 'close', side_effect=close), patch.object(M.time, 'monotonic', side_effect=clock):
            with self.assertRaises(M.ProductCoverageError):
                M.read_file(self.fd, 'original', deadline, uid=os.getuid())

    def test_snapshot_real_nested_tree_and_modes(self):
        (self.root/'nested').mkdir(mode=0o700)
        (self.root/'nested'/'data').write_bytes(b'abc')
        os.chmod(self.root/'nested'/'data', 0o600)
        files, directories = M.snapshot_tree(self.root, time.monotonic()+2, uid=os.getuid())
        self.assertEqual({'nested/data': {'sha256': hashlib.sha256(b'abc').hexdigest(), 'mode': '0600'}}, files)
        self.assertEqual({'': '0700', 'nested': '0700'}, directories)

    def test_restore_all_hashes_before_any_metadata_write(self):
        (self.root/'first').write_bytes(b'abc')
        (self.root/'second').write_bytes(b'wrong')
        os.chmod(self.root/'first', 0o600)
        original = ({name: {'sha256': hashlib.sha256(b'abc').hexdigest(), 'mode': '0400'}
                     for name in ('first', 'second')}, {'': '0700'})
        with patch.object(M.os, 'fchmod', side_effect=AssertionError('early chmod')) as chmod, patch.object(M.os, 'fchown', side_effect=AssertionError('early chown')) as chown:
            with self.assertRaises(M.ProductCoverageError):
                M.restore_metadata(self.root, original, time.monotonic()+2, uid=os.getuid(), gid=os.getgid())
            chmod.assert_not_called()
            chown.assert_not_called()
        self.assertEqual(0o600, (self.root/'first').stat().st_mode & 0o777)

    def test_restore_named_substitution_between_hash_and_reopen_never_writes(self):
        (self.root/'first').write_bytes(b'abc')
        original = ({'first': {'sha256': hashlib.sha256(b'abc').hexdigest(), 'mode': '0400'}}, {'': '0700'})
        actual = M.read_file
        def substitute(*args, **options):
            result = actual(*args, **options)
            os.rename(self.root/'first', self.root/'old')
            (self.root/'first').write_bytes(b'abc')
            return result
        with patch.object(M, 'read_file', side_effect=substitute), patch.object(M.os, 'fchmod') as chmod, patch.object(M.os, 'fchown') as chown:
            with self.assertRaises(M.ProductCoverageError):
                M.restore_metadata(self.root, original, time.monotonic()+2, uid=os.getuid(), gid=os.getgid())
            chmod.assert_not_called()
            chown.assert_not_called()

    def test_restore_real_verified_files_and_full_mode_map(self):
        (self.root/'first').write_bytes(b'abc')
        os.chmod(self.root/'first', 0o600)
        original = ({'first': {'sha256': hashlib.sha256(b'abc').hexdigest(), 'mode': '0400'}}, {'': '0700'})
        M.restore_metadata(self.root, original, time.monotonic()+2, uid=os.getuid(), gid=os.getgid())
        self.assertEqual(0o400, (self.root/'first').stat().st_mode & 0o777)
        self.assertEqual(b'abc', (self.root/'first').read_bytes())
        os.chmod(self.root/'first', 0o600)


class PublishedTreeInventoryControls(unittest.TestCase):
    """Ordinary owned bytes exercise inventory bounds, never a root owner or lease."""
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix='published-tree-', dir='/tmp')
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name).resolve()
        os.chmod(self.root, 0o700)

    def write(self, relative, data=b'x'):
        path = self.root / relative
        path.write_bytes(data)
        os.chmod(path, 0o600)

    def snapshot(self):
        return M.snapshot_tree(self.root, time.monotonic()+10, uid=os.getuid())

    def test_authenticated_published_shape_972_files_and_84_directories_is_bounded(self):
        for index in range(83):
            (self.root / f'd{index:03d}').mkdir(mode=0o700)
        for index in range(972):
            self.write(f'd{index % 83:03d}/f{index:04d}')
        files, directories = self.snapshot()
        self.assertEqual(972, len(files))
        self.assertEqual(84, len(directories))
        self.assertEqual(1056, len(files)+len(directories))
        self.assertEqual({'sha256': hashlib.sha256(b'x').hexdigest(), 'mode': '0600'},
                         files['d000/f0000'])

    def test_exact_2048_rows_include_root_and_next_row_is_rejected(self):
        for index in range(2047):
            self.write(f'f{index:04d}')
        files, directories = self.snapshot()
        self.assertEqual(2048, len(files)+len(directories))
        self.assertEqual({'': '0700'}, directories)
        self.write('f2047')
        with self.assertRaises(M.ProductCoverageError) as caught:
            self.snapshot()
        self.assertEqual('tree-bound', str(caught.exception))

    def test_byte_limit_remains_independent_of_entry_capacity(self):
        self.write('first', b'1234')
        self.write('second', b'5678')
        self.assertEqual(32 << 20, M.FILE_LIMIT)
        self.assertEqual(256 << 20, M.TREE_LIMIT)
        with patch.object(M, 'TREE_LIMIT', 8):
            self.assertEqual(2, len(self.snapshot()[0]))
            self.write('third', b'9')
            with self.assertRaises(M.ProductCoverageError) as caught:
                self.snapshot()
        self.assertEqual('tree-byte-bound', str(caught.exception))

    def test_depth_eight_still_accepts_and_ninth_directory_is_rejected(self):
        current = self.root
        for index in range(8):
            current = current / f'd{index}'
            current.mkdir(mode=0o700)
        self.assertEqual(9, len(self.snapshot()[1]))
        (current / 'ninth').mkdir(mode=0o700)
        with self.assertRaises(M.ProductCoverageError) as caught:
            self.snapshot()
        self.assertEqual('tree-bound', str(caught.exception))


class DataControls(unittest.TestCase):
    def test_fixed_packets_reject_duplicates_and_wrong_shape(self):
        good = {'stage': 'collected', 'passed': True, 'errors': 0, 'warnings': 0}
        self.assertEqual(good, M.packet(json.dumps(good).encode(), 'collected'))
        for data in (b'{"stage":"collected","stage":"collected","passed":true,"errors":0,"warnings":0}',
                     b'{"stage":"collected","passed":true,"errors":false,"warnings":0}',
                     b'{"stage":"collected","passed":true,"errors":0,"warnings":1}',
                     b'{"stage":"collected","passed":true,"errors":0,"warnings":0,"canary":1}'):
            with self.subTest(data=data), self.assertRaises(M.ProductCoverageError): M.packet(data, 'collected')

    def test_unit_selection_fixed_generated_shapes(self):
        worker = 'evidencehost-123456abcdef-worker.service'
        self.assertEqual('worker', M.unit_kind(worker, worker))
        self.assertEqual('producer', M.unit_kind('evidencehost-123456abcdef-s-127.service', worker))
        self.assertEqual('application', M.unit_kind('issue779-app-'+'a'*32+'.service', worker))
        for unit in ('evidencehost-123456abcdef-s-128.service', 'evidencehost-aaaaaaaaaaaa-s-0.service', 'foreign.service'):
            with self.subTest(unit=unit), self.assertRaises(M.ProductCoverageError): M.unit_kind(unit, worker)

    def test_actual_opened_mount_id_selects_namespace_record(self):
        text = '11 1 0:9 / /run/selected rw,nosuid,nodev,noexec - tmpfs tmpfs rw\n12 1 0:9 / /run/selected rw - tmpfs tmpfs rw\n'
        result = M.validate_mount(text, Path('/run/selected'), os.makedev(0, 9), 11)
        self.assertEqual(11, result['opened_mount_id'])
        with self.assertRaises(M.ProductCoverageError): M.validate_mount(text, Path('/run/selected'), os.makedev(0, 9), 12)
        with self.assertRaises(M.ProductCoverageError): M.validate_mount(text, Path('/run/selected'), os.makedev(0, 8), 11)

    def test_permission_phase_preserves_owner_bits_and_original_map(self):
        files = {'library': {'sha256': 'a'*64, 'mode': '0444'},
                 'executable': {'sha256': 'b'*64, 'mode': '0555'},
                 'owner_writable': {'sha256': 'c'*64, 'mode': '0644'}}
        directories = {'': '0755', 'readonly': '0555', 'private': '0700'}
        original = json.dumps([files, directories], sort_keys=True)
        pinned_files, pinned_directories = M.tool_permission_map(files, directories)
        self.assertEqual(['0440', '0550', '0640'], [pinned_files[name]['mode'] for name in files])
        self.assertEqual({'': '0750', 'readonly': '0550', 'private': '0750'}, pinned_directories)
        self.assertEqual(original, json.dumps([files, directories], sort_keys=True))
        wrong = dict(files, library={'sha256': 'a'*64, 'mode': '0999'})
        with self.assertRaises(M.ProductCoverageError): M.tool_permission_map(wrong, directories)

    def test_close_projection_cannot_hide_retained_mount_or_failure(self):
        good = M.close_receipt(None, True, False, True)
        self.assertTrue(good['account_cleanup_allowed'])
        for arguments in (('close-failed', True, False, True), (None, False, False, True),
                          (None, True, True, True), (None, True, False, False)):
            with self.subTest(arguments=arguments):
                value = M.close_receipt(*arguments)
                self.assertFalse(value['account_cleanup_allowed'])
                self.assertTrue(value['workspace_quarantined'])
        with self.assertRaises(M.ProductCoverageError): M.close_receipt('raw canary', True, False, True)


class ProcessControls(unittest.TestCase):
    def test_root_utility_real_output_and_overbound(self):
        deadline = time.monotonic()+3
        self.assertEqual(b'abc', M.command([sys.executable, '-c', "import sys;sys.stdout.buffer.write(b'abc')"], deadline))
        with self.assertRaises(M.ProductCoverageError):
            M.command([sys.executable, '-c', "import sys;sys.stdout.buffer.write(b'x'*65537)"], time.monotonic()+3)

    def test_post_spawn_constructor_failure_kills_and_closes_actual_child(self):
        actual_popen = M.subprocess.Popen
        processes = []
        def spawn(*args, **options):
            process = actual_popen(*args, **options)
            processes.append(process)
            return process
        with patch.object(M.subprocess, 'Popen', side_effect=spawn), patch.object(M.selectors, 'DefaultSelector', side_effect=OSError('selector unavailable')):
            with self.assertRaises(OSError):
                M.Child([sys.executable, '-c', 'import sys;sys.stdin.buffer.read()'], dict(os.environ))
        self.assertEqual(1, len(processes))
        process = processes[0]
        self.assertEqual(-signal.SIGKILL, process.returncode)
        self.assertTrue(all(stream.closed for stream in (process.stdin, process.stdout, process.stderr)))
        with self.assertRaises(ProcessLookupError): os.killpg(process.pid, 0)

    def test_utility_post_spawn_setup_failure_closes_both_pipes_and_preserves_error(self):
        actual = M.subprocess.Popen
        processes = []
        first = OSError('selector setup failed')
        def spawn(*args, **options):
            process = actual(*args, **options)
            processes.append(process)
            return process
        with patch.object(M.subprocess, 'Popen', side_effect=spawn), patch.object(M.selectors, 'DefaultSelector', side_effect=first):
            with self.assertRaises(OSError) as caught:
                M.command([sys.executable, '-c', 'import time;time.sleep(30)'], time.monotonic()+3)
        self.assertIs(first, caught.exception)
        self.assertEqual(1, len(processes))
        self.assertEqual(-signal.SIGKILL, processes[0].returncode)
        self.assertTrue(processes[0].stdout.closed and processes[0].stderr.closed)
        with self.assertRaises(ProcessLookupError): os.killpg(processes[0].pid, 0)

    def test_provider_actual_same_process_collect_and_eof(self):
        code = "import sys,json;print(json.dumps({'stage':'prepared','state':'/tmp/state','temp':'/tmp/'}),flush=True);assert sys.stdin.buffer.read(8)==b'collect\\n';print('official coverage table',flush=True);print(json.dumps({'stage':'collected','passed':True,'errors':0,'warnings':0}),flush=True)"
        child = M.Child([sys.executable, '-c', code], dict(os.environ))
        self.addCleanup(child.close)
        deadline = time.monotonic()+3
        prepared = None
        while prepared is None:
            child.pump(deadline)
            prepared = child.next_packet('prepared')
        self.assertEqual('/tmp/state', prepared['state'])
        child.collect(deadline)
        collected = None
        while collected is None or not child.joined(deadline):
            child.pump(deadline)
            if collected is None: collected = child.next_packet('collected')
        self.assertTrue(collected['passed'])
        self.assertEqual(0, child.process.returncode)
        self.assertFalse(child.selector.get_map())

    def test_uncertain_provider_is_killed_without_managed_exit(self):
        with tempfile.TemporaryDirectory(dir='/tmp') as root:
            marker = str(Path(root)/'managed-exit')
            code = "import atexit,json,sys,pathlib;atexit.register(lambda:pathlib.Path(sys.argv[1]).write_text('restored'));print(json.dumps({'stage':'prepared','state':'/tmp/state','temp':'/tmp/'}),flush=True);sys.stdin.buffer.read(8)"
            child = M.Child([sys.executable, '-c', code, marker], dict(os.environ))
            try:
                deadline = time.monotonic()+3
                ready = None
                while ready is None:
                    child.pump(deadline)
                    ready = child.next_packet('prepared')
                child.kill_without_restore(deadline)
                self.assertEqual(-signal.SIGKILL, child.process.returncode)
                self.assertFalse(Path(marker).exists())
                self.assertTrue(child.absent())
            finally: child.close()


if __name__ == '__main__':
    unittest.main()
