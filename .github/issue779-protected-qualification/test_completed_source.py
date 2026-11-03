"""Data and filesystem checks for the completed-source preflight; no network or build."""
from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import time
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
import sys
sys.path.insert(0, str(ROOT / '.github/issue779-protected-qualification'))
import completed_run
import completed_source as subject


def _git(root, *args):
    return subprocess.run(['git', *args], cwd=root, stdin=subprocess.DEVNULL,
                          stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                          preexec_fn=lambda: os.umask(0o022), check=True).stdout.decode().strip()


def _capture(root):
    head = _git(root, 'rev-parse', 'HEAD')
    tree = _git(root, 'rev-parse', 'HEAD^{tree}')
    raw = subprocess.run(['git', 'ls-tree', '-rz', '--full-tree', 'HEAD'], cwd=root,
                         stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, check=True).stdout
    files = {}
    for row in raw.split(b'\0'):
        if not row:
            continue
        header, name = row.split(b'\t', 1)
        mode, kind, blob = header.decode('ascii').split()
        path = name.decode('utf-8')
        body = (root / path).read_bytes()
        files[path] = {
            'sha256': hashlib.sha256(body).hexdigest(),
            'git_blob': hashlib.sha1(b'blob ' + str(len(body)).encode('ascii') + b'\0' + body).hexdigest(),
            'mode': '0755' if mode == '100755' else '0644',
        }
    value = {'schema': 'issue779-checkpoint2-source-v1', 'source': head,
             'tree': tree, 'count': len(files), 'files': files}
    return json.dumps(value, sort_keys=True, separators=(',', ':')).encode(), head, tree


class CompletedSourceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name).resolve()
        self.repo = self.root / 'origin'
        self.repo.mkdir()
        _git(self.repo, 'init', '-q', '-b', 'main')
        _git(self.repo, 'config', 'user.name', 'Test Fixture')
        _git(self.repo, 'config', 'user.email', 'fixture@example.invalid')
        for path in subject.REQUIRED_CAPTURE_PATHS:
            file = self.repo / path
            file.parent.mkdir(parents=True, exist_ok=True)
            file.write_text('fixed fixture content for ' + path + '\n')
            os.chmod(file, 0o644)
        (self.repo / '.gitignore').write_text('generated/\n')
        _git(self.repo, 'add', '.')
        _git(self.repo, 'commit', '-q', '-m', 'predecessor')
        self.source_root = self.root / 'completed-source'
        self.native_root = self.root / 'native-source'
        self.validator_root = self.root / 'validator'
        _git(self.root, 'clone', '-q', str(self.repo), str(self.source_root))
        _git(self.root, 'clone', '-q', str(self.repo), str(self.native_root))
        os.chmod(self.native_root, 0o700)
        (self.repo / 'validator-only.txt').write_text('validator commit\n')
        workflow = self.repo / subject.FUTURE_WORKFLOW
        workflow.parent.mkdir(parents=True, exist_ok=True)
        workflow.write_text('# future fixed workflow reference only\n')
        os.chmod(workflow, 0o644)
        _git(self.repo, 'add', 'validator-only.txt')
        _git(self.repo, 'add', subject.FUTURE_WORKFLOW)
        _git(self.repo, 'commit', '-q', '-m', 'validator')
        _git(self.root, 'clone', '-q', str(self.repo), str(self.validator_root))
        self.validator_capture, self.validator_head, self.validator_tree = _capture(self.validator_root)
        self.source_capture, self.source_head, self.source_tree = _capture(self.source_root)
        repo = {'full_name': completed_run.REPOSITORY, 'id': 987654321}
        self.event = json.dumps({'repository': repo, 'workflow_run': {
            'id': 40445212390, 'run_attempt': 2, 'event': 'workflow_dispatch',
            'head_branch': 'main', 'head_sha': self.source_head,
            'status': 'completed', 'conclusion': 'success',
            'name': completed_run.QUALIFICATION_WORKFLOW_NAME,
            'path': completed_run.WORKFLOW, 'workflow_id': 12345,
            'repository': repo, 'head_repository': repo,
        }}, separators=(',', ':')).encode()
        self.environment = {
            'GITHUB_ACTIONS': 'true', 'GITHUB_EVENT_NAME': 'workflow_run',
            'GITHUB_REPOSITORY': completed_run.REPOSITORY,
            'GITHUB_REPOSITORY_ID': '987654321', 'GITHUB_REF': 'refs/heads/main',
            'GITHUB_REF_PROTECTED': 'true',
            'GITHUB_WORKFLOW_REF': completed_run.REPOSITORY + '/' + subject.FUTURE_WORKFLOW
                                   + '@refs/heads/main',
            'GITHUB_SHA': self.validator_head, 'GITHUB_WORKFLOW_SHA': self.validator_head,
        }

    def tearDown(self):
        self.temp.cleanup()

    def _run(self, *, event=None, env=None, validator=None, source=None,
             source_root=None, native_root=None, deadline=None):
        with patch.object(subject, 'ROOT', self.validator_root), \
             patch.object(subject, 'SOURCE_MOUNT', self.source_root):
            return subject.validate_completed_source(
                self.event if event is None else event,
                self.environment if env is None else env,
                self.validator_capture if validator is None else validator,
                self.source_capture if source is None else source,
                self.source_root if source_root is None else source_root,
                self.native_root if native_root is None else native_root,
                deadline=time.monotonic() + 30 if deadline is None else deadline)

    def test_valid_source_pair_returns_only_separate_immutable_identities(self):
        result = self._run()
        self.assertEqual(987654321, result.repository_id)
        self.assertEqual((40445212390, 2), (result.run_id, result.run_attempt))
        self.assertEqual(self.source_head, result.head_sha)
        self.assertEqual(self.source_tree, result.source_tree)
        self.assertEqual(hashlib.sha256(self.source_capture).hexdigest(), result.source_capture_sha256)
        self.assertEqual(self.validator_head, result.validator_head_sha)
        self.assertEqual(self.validator_tree, result.validator_tree)
        self.assertEqual(hashlib.sha256(self.validator_capture).hexdigest(), result.validator_capture_sha256)
        for name in ('admission_created', 'native_acceptance', 'qualification_complete', 'gate_eligible'):
            self.assertIs(getattr(result, name), False)
        with self.assertRaises(AttributeError):
            result.head_sha = '0' * 40

    def test_target_head_must_be_an_ancestor_of_validator_head(self):
        # Rewind the validator clone to an independent commit with no predecessor ancestry.
        unrelated = self.root / 'unrelated'
        unrelated.mkdir()
        _git(unrelated, 'init', '-q', '-b', 'main')
        _git(unrelated, 'config', 'user.name', 'Test Fixture')
        _git(unrelated, 'config', 'user.email', 'fixture@example.invalid')
        for path in subject.REQUIRED_CAPTURE_PATHS:
            file = unrelated / path
            file.parent.mkdir(parents=True, exist_ok=True)
            file.write_text('unrelated ' + path + '\n')
            os.chmod(file, 0o644)
        _git(unrelated, 'add', '.')
        _git(unrelated, 'commit', '-q', '-m', 'unrelated')
        _git(unrelated, 'remote', 'add', 'origin', str(self.repo))
        self.validator_root.rename(self.root / 'old-validator')
        _git(self.root, 'clone', '-q', str(unrelated), str(self.validator_root))
        self.validator_capture, self.validator_head, self.validator_tree = _capture(self.validator_root)
        self.environment['GITHUB_SHA'] = self.validator_head
        self.environment['GITHUB_WORKFLOW_SHA'] = self.validator_head
        with self.assertRaises(ValueError):
            self._run()

    def test_context_mismatch_rejects_before_loading_source_verifier(self):
        env = dict(self.environment, GITHUB_WORKFLOW_REF='forge-trust/AppSurface/feature.yml@refs/heads/main')
        with patch.object(subject.completed_c1, '_load_trusted_current_binding') as load:
            with self.assertRaisesRegex(ValueError, 'completed-source-protected-context'):
                self._run(env=env)
            load.assert_not_called()

    def test_validator_capture_must_match_both_trusted_environment_heads(self):
        env = dict(self.environment, GITHUB_WORKFLOW_SHA='0' * 40)
        with self.assertRaisesRegex(ValueError, 'completed-source-validator-context-head'):
            self._run(env=env)
        value = json.loads(self.validator_capture)
        value['tree'] = 'f' * 40
        with self.assertRaises(ValueError):
            self._run(validator=json.dumps(value, separators=(',', ':')).encode())

    def test_source_capture_must_match_event_head_and_all_required_paths(self):
        value = json.loads(self.source_capture)
        value['source'] = 'f' * 40
        changed = json.dumps(value, separators=(',', ':')).encode()
        with self.assertRaises(ValueError):
            self._run(source=changed)
        value = json.loads(self.source_capture)
        del value['files'][next(iter(subject.REQUIRED_CAPTURE_PATHS))]
        value['count'] = len(value['files'])
        with self.assertRaisesRegex(ValueError, 'completed-source-capture-shape'):
            self._run(source=json.dumps(value, separators=(',', ':')).encode())

        value = json.loads(self.validator_capture)
        del value['files'][subject.FUTURE_WORKFLOW]
        value['count'] = len(value['files'])
        with self.assertRaisesRegex(ValueError, 'completed-source-capture-shape'):
            self._run(validator=json.dumps(value, separators=(',', ':')).encode())

    def test_source_and_native_checkouts_must_be_clean_including_ignored_at_preflight(self):
        ignored = self.source_root / 'generated' / 'build.out'
        ignored.parent.mkdir()
        ignored.write_text('ignored')
        with self.assertRaisesRegex(ValueError, 'completed-source-source-not-fresh'):
            self._run()

    def test_each_preflight_checkout_rejects_ignored_files(self):
        for root, code in ((self.validator_root, 'validator'),
                           (self.source_root, 'source'),
                           (self.native_root, 'native-source')):
            with self.subTest(root=code):
                ignored = root / 'generated' / 'fixture.out'
                ignored.parent.mkdir()
                ignored.write_text('ignored')
                with self.assertRaisesRegex(ValueError, 'source-not-fresh'):
                    self._run()
                ignored.unlink()
                ignored.parent.rmdir()

    def test_postflight_rechecks_same_captures_and_allows_only_ignored_target_outputs(self):
        result = self._run()
        ignored = self.native_root / 'generated' / 'build.out'
        ignored.parent.mkdir()
        ignored.write_text('ignored output')
        with patch.object(subject, 'ROOT', self.validator_root), \
             patch.object(subject, 'SOURCE_MOUNT', self.source_root):
            subject.verify_completed_source(result, self.validator_capture, self.source_capture,
                                            self.source_root, self.native_root,
                                            deadline=time.monotonic() + 30)
        (self.native_root / 'untracked.txt').write_text('not ignored')
        with patch.object(subject, 'ROOT', self.validator_root), \
             patch.object(subject, 'SOURCE_MOUNT', self.source_root):
            with self.assertRaises(ValueError):
                subject.verify_completed_source(result, self.validator_capture, self.source_capture,
                                                self.source_root, self.native_root,
                                                deadline=time.monotonic() + 30)

    def test_expired_deadline_and_overlapping_root_reject(self):
        with self.assertRaisesRegex(ValueError, 'completed-source-deadline'):
            self._run(deadline=time.monotonic() - 1)
        with self.assertRaises(ValueError):
            self._run(native_root=self.source_root)

    def test_fixed_source_mount_is_required(self):
        with patch.object(subject, 'ROOT', self.validator_root), \
             patch.object(subject, 'SOURCE_MOUNT', self.root / 'wrong-source'):
            with self.assertRaisesRegex(ValueError, 'completed-source-source-mount'):
                subject.validate_completed_source(
                    self.event, self.environment, self.validator_capture, self.source_capture,
                    self.source_root, self.native_root, deadline=time.monotonic() + 30)

    def test_direct_git_output_is_capped_before_full_capture(self):
        for index in range(256):
            (self.validator_root / f'untracked-{index:04d}.txt').write_text('x')
        with self.assertRaisesRegex(ValueError, 'completed-source-git'):
            subject._git(self.validator_root, 'status', '--porcelain=v1',
                         '--untracked-files=all', deadline=time.monotonic() + 10)

    def test_direct_git_probe_receives_only_the_closed_environment(self):
        captured = []

        def fake_run(args, *, stdout, **kwargs):
            captured.append((args, kwargs['env']))
            stdout.write(b'c' * 40 + b'\n')
            return subprocess.CompletedProcess(args, 0)

        with patch.object(subject.subprocess, 'run', side_effect=fake_run):
            value = subject._git(self.validator_root, 'rev-parse', 'HEAD',
                                 deadline=time.monotonic() + 10)
        self.assertEqual('c' * 40, value)
        self.assertEqual(1, len(captured))
        self.assertEqual(subject._git_environment(), captured[0][1])
        self.assertEqual('/usr/bin/git', captured[0][0][0])

    def test_trusted_source_verification_uses_controlled_path_and_restores_it(self):
        observed = []

        class Binding:
            def verify_source(_self, _value, _root, *, deadline):
                self.assertGreater(deadline, time.monotonic())
                observed.append(dict(os.environ))

        inherited = {'PATH': '/caller/private/bin:/usr/bin', 'GIT_DIR': '/tmp/untrusted.git',
                     'GIT_WORK_TREE': '/tmp/untrusted-tree', 'GIT_INDEX_FILE': '/tmp/untrusted.index',
                     'GIT_CONFIG_COUNT': '1', 'GIT_CONFIG_KEY_0': 'core.fsmonitor',
                     'GIT_CONFIG_VALUE_0': 'true', 'GIT_TRACE': '/tmp/git.trace',
                     'GH_TOKEN': 'secret-token', 'ACTIONS_RUNTIME_TOKEN': 'secret-runtime-token',
                     'LD_PRELOAD': '/untrusted/loader.so',
                     'DYLD_INSERT_LIBRARIES': '/untrusted/dylib.dylib'}
        fixed = subject._git_environment()
        with patch.dict(os.environ, inherited):
            with patch.object(subject.completed_c1, '_load_trusted_current_binding',
                              return_value=Binding()):
                self._run()
            self.assertEqual([fixed] * 3, observed)
            self.assertEqual(inherited, {key: os.environ[key] for key in inherited})

    def test_trusted_source_verifier_failure_restores_path(self):
        calls = 0

        class Binding:
            def verify_source(_self, _value, _root, *, deadline):
                nonlocal calls
                calls += 1
                self.assertEqual(subject._git_environment(), dict(os.environ))
                if calls == 2:
                    raise RuntimeError('verifier failure')

        inherited = {'PATH': '/caller/private/bin:/usr/bin', 'GIT_DIR': '/tmp/redirect.git',
                     'GIT_WORK_TREE': '/tmp/redirect-tree', 'GH_TOKEN': 'secret-token',
                     'ACTIONS_RUNTIME_TOKEN': 'secret-runtime-token',
                     'LD_PRELOAD': '/untrusted/loader.so',
                     'DYLD_INSERT_LIBRARIES': '/untrusted/dylib.dylib'}
        with patch.dict(os.environ, inherited):
            with patch.object(subject.completed_c1, '_load_trusted_current_binding',
                              return_value=Binding()):
                with self.assertRaisesRegex(RuntimeError, 'verifier failure'):
                    self._run()
            self.assertEqual(2, calls)
            self.assertEqual(inherited, {key: os.environ[key] for key in inherited})

    def test_git_worktree_overrides_cannot_hide_real_untracked_target_file(self):
        source = self.root / 'same-head-source'
        native = self.root / 'same-head-native'
        _git(self.root, 'clone', '-q', str(self.validator_root), str(source))
        _git(self.root, 'clone', '-q', str(self.validator_root), str(native))
        os.chmod(native, 0o700)
        (source / 'Directory.Build.targets').write_text('<Project />\n')
        event_value = json.loads(self.event)
        event_value['workflow_run']['head_sha'] = self.validator_head
        event = json.dumps(event_value, separators=(',', ':')).encode()
        environment = dict(self.environment)
        environment.update({'GIT_DIR': str(native / '.git'), 'GIT_WORK_TREE': str(native),
                            'GIT_INDEX_FILE': str(native / '.git' / 'index')})

        def git_probe(root, *args, deadline):
            if args[0] == 'rev-parse':
                return self.validator_tree if args[1] == 'HEAD^{tree}' else self.validator_head
            if args[0] in ('status', 'merge-base'):
                return ''
            self.fail('unexpected direct git probe: ' + repr(args))

        with patch.object(subject, 'ROOT', self.validator_root), \
             patch.object(subject, 'SOURCE_MOUNT', source), \
             patch.object(subject, '_git', side_effect=git_probe), \
             patch.dict(os.environ, environment):
            with self.assertRaises(ValueError):
                subject.validate_completed_source(
                    event, environment, self.validator_capture, self.validator_capture,
                    source, native, deadline=time.monotonic() + 30)
            self.assertEqual(str(native / '.git'), os.environ['GIT_DIR'])
            self.assertEqual(str(native), os.environ['GIT_WORK_TREE'])
            self.assertTrue((source / 'Directory.Build.targets').exists())

    def test_postflight_rejects_tampered_binding_flags_and_tree_fields(self):
        result = self._run()
        object.__setattr__(result, 'gate_eligible', True)
        with patch.object(subject, 'ROOT', self.validator_root), \
             patch.object(subject, 'SOURCE_MOUNT', self.source_root):
            with self.assertRaisesRegex(ValueError, 'completed-source-binding-shape'):
                subject.verify_completed_source(result, self.validator_capture, self.source_capture,
                                                self.source_root, self.native_root,
                                                deadline=time.monotonic() + 30)


if __name__ == '__main__':
    unittest.main()
