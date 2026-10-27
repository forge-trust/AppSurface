"""Fixed, bounded execution of the public native measurement data test.

This module reports a local procedure observation only. It never establishes
protected admission, native acceptance, gate eligibility, or qualification.
"""
from __future__ import annotations

from dataclasses import dataclass, field
import hashlib
import math
import os
from pathlib import Path
import signal
import stat
import subprocess
import threading
import time

import completed_c1
import completed_native_inputs
import completed_native_result

MAX_DEADLINE_SECONDS = 20 * 60
CLEANUP_RESERVE_SECONDS = 30
MAX_PROCESS_OUTPUT = 8 * 1024 * 1024
MAX_PROCESS_OUTPUT_HARD = 20 * 1024 * 1024
MAX_LOG_BYTES = 16 * 1024 * 1024
SDK_VERSION = '10.0.401'
PROJECT = completed_native_result.PROJECT
NATIVE_INPUT_ROOT = 'TestResults/issue779-native-measurement'
OUTPUT_NAME = 'native-replay-output'
LOG_NAME = 'native-replay-logs'


def _need(value, code):
    if not value:
        raise ValueError('completed-native-replay-' + code)


def _sha(raw):
    return hashlib.sha256(raw).hexdigest()


def _identity(info):
    return completed_c1._node_identity(info)


def _remaining(deadline):
    _need(type(deadline) in (int, float) and math.isfinite(deadline)
          and time.monotonic() < deadline, 'deadline')
    return deadline - time.monotonic()


def _deadline(deadline):
    _need(type(deadline) in (int, float) and math.isfinite(deadline), 'deadline')
    remaining = deadline - time.monotonic()
    _need(0 < remaining <= MAX_DEADLINE_SECONDS, 'deadline-bound')
    return deadline


def _is_linux():
    return os.name == 'posix' and __import__('sys').platform.startswith('linux')


class NativeReplayError(RuntimeError):
    """Fixed diagnostic category; subprocess text is never placed in the message."""


class _OutputCapture:
    def __init__(self, stream, maximum):
        self.stream = stream
        self.maximum = maximum
        self.data = bytearray()
        self.overflow = threading.Event()
        self.error = None
        self.thread = threading.Thread(target=self._read, daemon=False)

    def _read(self):
        try:
            while True:
                chunk = self.stream.read(64 * 1024)
                if not chunk:
                    return
                room = self.maximum - len(self.data)
                if len(chunk) > room:
                    if room > 0:
                        self.data.extend(chunk[:room])
                    self.overflow.set()
                    # Continue draining until the parent kills the process group.
                elif not self.overflow.is_set():
                    self.data.extend(chunk)
        except BaseException as error:
            self.error = error

    def start(self):
        self.thread.start()

    def join(self, timeout=5):
        if self.thread.ident is not None:
            self.thread.join(timeout=timeout)
        _need(not self.thread.is_alive(), 'process-reader-join')
        self.stream.close()
        if self.error is not None:
            raise NativeReplayError('completed-native-replay-process-read') from None


def _kill_and_join(process, captures, cleanup_deadline):
    first_error = None
    try:
        os.killpg(process.pid, signal.SIGKILL)
    except ProcessLookupError:
        pass
    except OSError:
        try:
            process.kill()
        except OSError:
            first_error = NativeReplayError('completed-native-replay-process-kill')
    try:
        _wait_group_quiescent(process.pid, cleanup_deadline)
    except BaseException as error:
        if first_error is None:
            first_error = error
    try:
        process.wait(timeout=max(0.01, min(5, cleanup_deadline - time.monotonic())))
    except subprocess.TimeoutExpired:
        try:
            process.kill()
        except OSError:
            pass
        try:
            process.wait(timeout=max(0.01, min(5, cleanup_deadline - time.monotonic())))
        except BaseException as error:
            if first_error is None:
                first_error = error
    except BaseException as error:
        if first_error is None:
            first_error = error
    for capture in captures:
        try:
            capture.join(max(0.01, min(2.5, cleanup_deadline - time.monotonic())))
        except BaseException as error:
            if first_error is None:
                first_error = error
    if first_error is not None:
        raise first_error


def _wait_group_quiescent(process_group, deadline):
    """Wait until no live process remains in this pinned invocation group."""
    while True:
        _remaining(deadline)
        active = False
        try:
            names = os.listdir('/proc')
        except OSError:
            raise NativeReplayError('completed-native-replay-procfs') from None
        for name in names:
            if not name.isascii() or not name.isdecimal():
                continue
            _remaining(deadline)
            try:
                with open(Path('/proc', name, 'stat'), 'rb') as stream:
                    raw_bytes = stream.read(64 * 1024 + 1)
                _need(len(raw_bytes) <= 64 * 1024, 'procfs-stat-size')
                close = raw_bytes.rfind(b')')
                fields = raw_bytes[close + 2:].split()
                # fields: state, ppid, pgrp, ...
                _need(close > 0 and len(fields) >= 3, 'procfs-stat-shape')
                pid = int(name)
                if int(fields[2]) == process_group and pid != process_group \
                        and fields[0] not in (b'Z', b'X'):
                    active = True
                    break
            except FileNotFoundError:
                continue
            except (OSError, ValueError, IndexError):
                raise NativeReplayError('completed-native-replay-procfs-stat') from None
        if not active:
            return
        time.sleep(min(0.01, max(0.001, deadline - time.monotonic())))


def _run_fixed_process(argv, *, cwd, env, deadline, output_limit=MAX_PROCESS_OUTPUT,
                       cleanup_deadline=None):
    """Run one already-fixed argv under a hard deadline and per-stream byte caps.

    The caller must close argv/environment choices. This function provides no
    shell, executable discovery, or callback surface.
    """
    _remaining(deadline)
    if cleanup_deadline is None:
        cleanup_deadline = deadline + 5
    _need(type(cleanup_deadline) in (int, float) and math.isfinite(cleanup_deadline)
          and cleanup_deadline >= deadline, 'process-cleanup-deadline')
    _need(type(argv) is tuple and argv and all(type(item) is str and item for item in argv),
          'process-argv')
    _need(type(env) is dict and all(type(k) is str and type(v) is str for k, v in env.items()),
          'process-environment')
    _need(type(output_limit) is int and 0 < output_limit <= MAX_PROCESS_OUTPUT_HARD,
          'process-output-bound')
    _need(hasattr(os, 'waitid') and hasattr(os, 'WNOWAIT'), 'process-waitid-required')
    work = Path(cwd)
    _need(work.is_absolute() and work.resolve(strict=True) == work and work.is_dir()
          and not work.is_symlink(), 'process-cwd')
    try:
        process = subprocess.Popen(argv, cwd=work, env=env, stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, shell=False, close_fds=True,
            start_new_session=True)
    except BaseException:
        raise NativeReplayError('completed-native-replay-process-start') from None
    stdout = _OutputCapture(process.stdout, output_limit)
    stderr = _OutputCapture(process.stderr, output_limit)
    captures = (stdout, stderr)
    failure = None
    pinned_exit = False
    try:
        stdout.start()
        stderr.start()
        linux_waitid = hasattr(os, 'waitid') and hasattr(os, 'WNOWAIT')
        while True:
            if stdout.overflow.is_set() or stderr.overflow.is_set():
                failure = 'process-output-limit'
                break
            if time.monotonic() >= deadline:
                failure = 'process-deadline'
                break
            if linux_waitid:
                observed = os.waitid(os.P_PID, process.pid, os.WEXITED | os.WNOHANG | os.WNOWAIT)
                if observed is not None and observed.si_pid != 0:
                    pinned_exit = True
                    break
            elif process.poll() is not None:
                break
            time.sleep(min(0.05, max(0.001, deadline - time.monotonic())))
        if failure is not None:
            _kill_and_join(process, captures, cleanup_deadline)
        elif linux_waitid and pinned_exit:
            # Keep the exited leader unreaped while killing any helper that
            # detached its stdio but stayed in this process group. Its PID pins
            # the group identity until the group is settled and readers join.
            try:
                os.killpg(process.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
            _wait_group_quiescent(process.pid, cleanup_deadline)
            process.wait(timeout=max(0.01, min(5, cleanup_deadline - time.monotonic())))
            for capture in captures:
                capture.join(max(0.01, min(2.5, cleanup_deadline - time.monotonic())))
        if stdout.overflow.is_set() or stderr.overflow.is_set():
            failure = 'process-output-limit'
    except BaseException as primary_error:
        if process.returncode is None:
            try:
                _kill_and_join(process, captures, cleanup_deadline)
            except BaseException as cleanup_error:
                if hasattr(primary_error, 'add_note'):
                    primary_error.add_note('completed-native-replay-process-cleanup-failed')
        else:
            first_error = None
            for capture in captures:
                if capture.thread.is_alive():
                    try:
                        capture.join(max(0.01, min(2.5, cleanup_deadline - time.monotonic())))
                    except BaseException as error:
                        if first_error is None:
                            first_error = error
            if first_error is not None:
                if hasattr(primary_error, 'add_note'):
                    primary_error.add_note('completed-native-replay-process-reader-cleanup-failed')
        raise
    if failure:
        raise NativeReplayError('completed-native-replay-' + failure)
    return process.returncode, bytes(stdout.data), bytes(stderr.data)


def _controlled_environment(dotnet, scratch_root, native=False):
    root = str(Path(dotnet).parent)
    scratch = Path(scratch_root)
    _need(scratch.is_absolute() and scratch.resolve(strict=True) == scratch and scratch.is_dir()
          and not scratch.is_symlink(), 'environment-scratch')
    env = {
        'PATH': root + ':/usr/bin:/bin',
        'HOME': str(scratch / 'home'),
        'LANG': 'C.UTF-8',
        'DOTNET_ROOT': root,
        'DOTNET_CLI_HOME': str(scratch / 'dotnet-home'),
        'DOTNET_CLI_TELEMETRY_OPTOUT': '1',
        'DOTNET_NOLOGO': '1',
        'DOTNET_SKIP_FIRST_TIME_EXPERIENCE': '1',
        'DOTNET_PROCESSOR_COUNT': '2',
        'DOTNET_CLI_USE_MSBUILD_SERVER': '0',
        'MSBUILDDISABLENODEREUSE': '1',
        'NUGET_PACKAGES': str(scratch / 'nuget'),
    }
    if native:
        env['EVIDENCEHOST_NATIVE_MEASUREMENT'] = '1'
    return env


def _dotnet_path():
    path = Path('/usr/share/dotnet/dotnet').resolve(strict=True)
    info = path.stat()
    _need(path.is_absolute() and stat.S_ISREG(info.st_mode) and os.access(path, os.X_OK), 'sdk-path')
    return str(path)


def _git(source, *args, deadline, maximum=4096):
    _remaining(deadline)
    try:
        completed = subprocess.run(('/usr/bin/git', '--no-optional-locks', '-c', 'core.fsmonitor=false',
            '-c', 'safe.directory=' + str(source), *args), cwd=source,
            stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
            timeout=min(15.0, _remaining(deadline)), check=False, close_fds=True)
    except (OSError, subprocess.TimeoutExpired):
        raise NativeReplayError('completed-native-replay-git') from None
    _need(completed.returncode == 0 and len(completed.stdout) <= maximum, 'git')
    return completed.stdout


def _capture_and_binding(source_capture, source, deadline):
    old_path = os.environ.get('PATH')
    os.environ['PATH'] = '/usr/bin:/bin'
    try:
        current = completed_c1._load_trusted_current_binding(deadline=deadline)
        return completed_c1._load_capture_and_binding(
            source_capture, source, current_binding=current, deadline=deadline)
    finally:
        if old_path is None:
            os.environ.pop('PATH', None)
        else:
            os.environ['PATH'] = old_path


def _run_logged(argv, *, cwd, env, deadline, cleanup_deadline, log_path, owned, ownership_root):
    code, out, err = _run_fixed_process(argv, cwd=cwd, env=env, deadline=deadline,
                                        cleanup_deadline=cleanup_deadline)
    body = b'[stdout]\n' + out + b'\n[stderr]\n' + err
    _need(len(body) <= MAX_LOG_BYTES, 'log-bound')
    completed_c1._write_bytes(log_path, body, deadline=deadline, owned=owned,
                              ownership_root=ownership_root)
    return code, _sha(body), len(body)


def _cleanup_root(parent, root, owned, primary, label):
    if not owned:
        return primary
    identity = owned.get(())
    if identity is None:
        return primary
    try:
        completed_c1._finish_invocation(parent, root, identity, owned, None)
    except BaseException as error:
        if primary is None:
            return error
        if hasattr(primary, 'add_note'):
            primary.add_note('completed-native-replay-' + label + '-cleanup-failed')
    return primary


def run_native_replay(stage, inventory, stage_directory, source_capture, source_root,
                      private_output_parent, *, deadline):
    """Stage authentic inputs, build the fixed test, run one public validator, and clean up.

    `source_root` must be a disposable exact-source checkout. The invocation removes
    only its owned input stage and TestResults tree; bounded logs, TRX, caches and
    build bin/obj artifacts remain in the caller's disposable workspace.
    """
    deadline = _deadline(deadline)
    _need(_is_linux(), 'linux-required')
    work_deadline = deadline - CLEANUP_RESERVE_SECONDS
    _need(time.monotonic() < work_deadline, 'cleanup-reserve')
    source = Path(source_root)
    parent = Path(private_output_parent)
    _need(source.is_absolute() and source.resolve(strict=True) == source and source.is_dir()
          and not source.is_symlink(), 'source-root')
    _need(parent.is_absolute() and parent.resolve(strict=True) == parent and parent.is_dir()
          and not parent.is_symlink(), 'output-parent')
    _need(source != parent and source not in parent.parents and parent not in source.parents,
          'root-overlap')
    _need(stat.S_IMODE(source.lstat().st_mode) == 0o700 and source.lstat().st_uid == os.geteuid(),
          'source-custody')
    _need(stat.S_IMODE(parent.lstat().st_mode) == 0o700 and parent.lstat().st_uid == os.geteuid()
          and not os.listdir(parent), 'output-parent-custody')

    capture, source_binding = _capture_and_binding(source_capture, source, work_deadline)
    _need(capture['source'] == stage.head_sha and capture['tree'] == stage.source_tree
          and _sha(source_capture) == stage.source_capture_sha256, 'source-attribution')
    _need(source != Path(stage_directory) and source not in Path(stage_directory).parents
          and Path(stage_directory) not in source.parents, 'source-stage-overlap')
    _need(_git(source, 'ls-files', '--others', '--ignored', '--exclude-standard',
               deadline=work_deadline, maximum=4096) == b'', 'source-ignored-or-untracked')
    tracked_paths = _git(source, 'ls-files', '-z', deadline=work_deadline, maximum=8 * 1024 * 1024)
    _need(not any(part in (b'bin', b'obj') for path in tracked_paths.split(b'\0') if path
                  for part in path.split(b'/')), 'source-build-cache')

    private_dirs = {}
    output_owned = {}
    owned_parent = parent
    output = parent / OUTPUT_NAME
    logs = parent / LOG_NAME
    state = parent / 'native-replay-state'
    result = None
    primary = None
    native_inputs = None
    test_root = source / 'TestResults'
    test_root_owned = {}
    test_root_identity = None
    copied = {}
    input_parent = parent / 'native-input-stage'
    input_owned = {}
    state_owned = {}
    try:
        _need(not os.path.lexists(output) and not os.path.lexists(logs)
              and not os.path.lexists(state), 'output-exists')
        completed_c1._mkdir(output, owned=private_dirs, ownership_root=parent)
        output_owned[()] = private_dirs[(OUTPUT_NAME,)]
        completed_c1._mkdir(logs, owned=private_dirs, ownership_root=parent)
        completed_c1._mkdir(state, owned=state_owned, ownership_root=state)
        for name in ('home', 'dotnet-home', 'nuget'):
            completed_c1._mkdir(state / name, owned=state_owned, ownership_root=state)
        completed_c1._mkdir(input_parent, owned=input_owned, ownership_root=input_parent)
        native_inputs = completed_native_inputs.stage_native_inputs(
            stage, inventory, stage_directory, source_capture, input_parent, deadline=work_deadline)
        completed_native_inputs.verify_native_inputs(native_inputs, stage, deadline=work_deadline)
        _need(not os.path.lexists(test_root), 'testresults-exists')
        completed_c1._mkdir(test_root, owned=test_root_owned, ownership_root=test_root)
        test_root_identity = test_root_owned.get(())
        target_root = source / NATIVE_INPUT_ROOT
        # Create each directory and file using exclusive, inode-recorded operations.
        dirs_owned = dict(test_root_owned)
        test_root_owned = dirs_owned
        rel_root = Path('issue779-native-measurement')
        completed_c1._mkdir(target_root, owned=dirs_owned, ownership_root=test_root)
        for row in native_inputs.files:
            _remaining(deadline)
            relative = Path(row.relative_path)
            cursor = target_root
            rel_cursor = rel_root
            for component in relative.parts[:-1]:
                cursor /= component
                rel_cursor /= component
                if not os.path.lexists(cursor):
                    completed_c1._mkdir(cursor, owned=dirs_owned, ownership_root=test_root)
            raw = completed_c1._open_bound_file(native_inputs.root / row.relative_path, row.size,
                row.sha256, deadline=deadline, maximum=completed_native_inputs.MAX_INPUT_BYTES,
                required_mode=0o600, expected_identity=row.identity)
            destination = target_root / relative
            completed_c1._write_bytes(destination, raw, deadline=deadline, owned=dirs_owned,
                                      ownership_root=test_root)
            copied[tuple((rel_root / relative).parts)] = (len(raw), row.sha256,
                dirs_owned[tuple((rel_root / relative).parts)])
        for key, (size, digest, identity) in copied.items():
            path = test_root.joinpath(*key)
            raw = completed_c1._open_bound_file(path, size, digest, deadline=deadline,
                maximum=completed_native_inputs.MAX_INPUT_BYTES, required_mode=0o600,
                expected_identity=identity)
            _need(len(raw) == size and _sha(raw) == digest, 'copied-input-binding')
        completed_native_inputs.verify_native_inputs(native_inputs, stage, deadline=work_deadline)

        dotnet = _dotnet_path()
        version_argv = (dotnet, '--version')
        version_code, version_out, _version_err = _run_fixed_process(
            version_argv, cwd=source, env=_controlled_environment(dotnet, state), deadline=work_deadline,
            cleanup_deadline=deadline)
        _need(version_code == 0 and version_out.strip() == SDK_VERSION.encode('ascii'), 'sdk-version')
        project = str(source / PROJECT)
        build_commands = (
            (dotnet, 'restore', project, '--locked-mode', '--disable-parallel', '-m:1',
             '-nr:false', '-p:EvidencePrivateQualification=None'),
            (dotnet, 'build', project, '--no-restore', '--configuration', 'Release',
             '--disable-build-servers', '-m:1', '-nr:false', '-p:UseSharedCompilation=false', '-warnaserror',
             '-p:EvidencePrivateQualification=None'),
        )
        command_hashes = []
        for index, argv in enumerate(build_commands):
            code, log_sha, log_size = _run_logged(argv, cwd=source,
                env=_controlled_environment(dotnet, state), deadline=work_deadline,
                cleanup_deadline=deadline,
                log_path=logs / f'build-{index}.log', owned=private_dirs, ownership_root=parent)
            _need(code == 0, f'build-{index}-failed')
            command_hashes.append((tuple(argv), log_sha, log_size))
        _capture_and_binding(source_capture, source, work_deadline)
        assembly = source / completed_native_result.ASSEMBLY
        assembly_info = assembly.lstat()
        _need(stat.S_ISREG(assembly_info.st_mode) and not stat.S_ISLNK(assembly_info.st_mode)
              and assembly_info.st_uid == os.geteuid() and assembly_info.st_size > 0, 'assembly-shape')
        test_argv = (dotnet, 'vstest', str(assembly),
            f'--TestCaseFilter:FullyQualifiedName={completed_native_result.TEST_METHOD}',
            f'--logger:trx;LogFileName={completed_native_result.RESULT_FILE}',
            f'--ResultsDirectory:{output}')
        test_code, test_log_sha, test_log_size = _run_logged(test_argv, cwd=source,
            env=_controlled_environment(dotnet, state, native=True), deadline=work_deadline,
            cleanup_deadline=deadline,
            log_path=logs / 'test.log', owned=private_dirs, ownership_root=parent)
        _need(test_code == 0, 'validator-exit')
        trx_path = output / completed_native_result.RESULT_FILE
        trx_info = trx_path.lstat()
        _need(stat.S_ISREG(trx_info.st_mode) and trx_info.st_nlink == 1
              and trx_info.st_uid == os.geteuid()
              and trx_info.st_size <= completed_native_result.MAX_TRX_BYTES, 'trx-shape')
        completed_c1._remember_owned(trx_path, output, output_owned)
        _need(set(os.listdir(output)) == {completed_native_result.RESULT_FILE}, 'trx-inventory')
        trx = completed_c1._read_private_file(trx_path, deadline=work_deadline,
            maximum=completed_native_result.MAX_TRX_BYTES, required_mode=stat.S_IMODE(trx_info.st_mode))
        trx = completed_c1._open_bound_file(trx_path, trx_info.st_size, _sha(trx), deadline=work_deadline,
            maximum=completed_native_result.MAX_TRX_BYTES, expected_identity=output_owned[(completed_native_result.RESULT_FILE,)])
        parsed = completed_native_result.parse_native_validator_trx(trx, deadline=work_deadline)
        _need(set(os.listdir(logs)) == {'build-0.log', 'build-1.log', 'test.log'}, 'log-inventory')
        for name, expected_sha in (
            ('build-0.log', command_hashes[0][1]),
            ('build-1.log', command_hashes[1][1]),
            ('test.log', test_log_sha),
        ):
            row = private_dirs[(LOG_NAME, name)]
            log_path = logs / name
            log_info = log_path.lstat()
            raw_log = completed_c1._open_bound_file(log_path, log_info.st_size, expected_sha,
                deadline=work_deadline, maximum=MAX_LOG_BYTES, required_mode=0o600,
                expected_identity=row)
            _need(len(raw_log) == log_info.st_size, 'log-content')
        completed_native_inputs.verify_native_inputs(native_inputs, stage, deadline=work_deadline)
        _capture_and_binding(source_capture, source, work_deadline)
        for key, (size, digest, identity) in copied.items():
            path = test_root.joinpath(*key)
            completed_c1._open_bound_file(path, size, digest, deadline=work_deadline,
                maximum=completed_native_inputs.MAX_INPUT_BYTES, required_mode=0o600,
                expected_identity=identity)
        result = NativeReplayResult(
            'issue779-native-replay-observation-v1', stage.run_id, stage.run_attempt,
            stage.head_sha, stage.source_capture_sha256, SDK_VERSION,
            tuple(command_hashes), tuple(test_argv), test_log_sha, test_log_size,
            parsed.trx_sha256, parsed.trx_bytes, parsed.test_name, parsed.outcome,
            str(parent), str(output), str(logs), str(state),
        )
    except BaseException as error:
        primary = error
    finally:
        # Remove only the exact invocation-owned native input stage.
        if native_inputs is not None:
            try:
                completed_native_inputs.cleanup_native_inputs(native_inputs)
            except BaseException as error:
                if primary is None:
                    primary = error
                elif hasattr(primary, 'add_note'):
                    primary.add_note('completed-native-replay-input-cleanup-failed')
        if input_owned:
            primary = _cleanup_root(parent, input_parent, input_owned, primary, 'input-parent')
        # dotnet/NuGet may create cache nodes that were not created by this
        # controller. Keep the fresh private scratch tree under caller custody.
        # The TestResults directory did not exist before this call. Cleanup is
        # inode-owned and refuses any unknown file or replaced node.
        if test_root_identity is not None:
            try:
                completed_c1._finish_invocation(source, test_root, test_root_identity,
                                                test_root_owned, None)
            except BaseException as error:
                if primary is None:
                    primary = error
                elif hasattr(primary, 'add_note'):
                    primary.add_note('completed-native-replay-testresults-cleanup-failed')
        # Bounded command logs and the parsed TRX remain under the private parent
        # with exact inode records in this process. The caller disposes that root.
    if primary is not None:
        raise primary
    _remaining(deadline)
    _need(result is not None, 'incomplete')
    return result


@dataclass(frozen=True)
class NativeReplayResult:
    """Non-authorizing observation of one exact public test invocation."""

    schema: str
    run_id: int
    run_attempt: int
    head_sha: str
    source_capture_sha256: str
    sdk_version: str
    build_commands: tuple[tuple[tuple[str, ...], str, int], ...]
    test_command: tuple[str, ...]
    test_log_sha256: str
    test_log_bytes: int
    trx_sha256: str
    trx_bytes: int
    test_name: str
    outcome: str
    retained_output_parent: str
    retained_test_output: str
    retained_logs: str
    retained_work_root: str
    cleanup_complete: bool = field(default=False, init=False)
    validator_executed: bool = False
    native_acceptance: bool = field(default=False, init=False)
    qualification_complete: bool = field(default=False, init=False)
    gate_eligible: bool = field(default=False, init=False)
