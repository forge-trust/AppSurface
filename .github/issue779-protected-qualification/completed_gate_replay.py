"""Fixed public CLI replay of a source-bound retained coverage gate report."""
from __future__ import annotations

from dataclasses import dataclass
import hashlib
import math
import os
from pathlib import Path
import stat
import time

import completed_c1
import completed_gate
import completed_native_replay
import completed_run
import completed_validation

MAX_REPLAY_SECONDS = 20 * 60
REPLAY_RESERVE_SECONDS = 90
FINALIZE_RESERVE_SECONDS = 30
DIFF_LIMIT = 20 * 1024 * 1024
MAX_WORKSPACE_BYTES = 2 * 1024 * 1024 * 1024
MAX_WORKSPACE_NODES = 16 * 1024
SOURCE_MOUNT = Path('/home/runner/work/AppSurface/AppSurface')
GATE_OUTPUTS = frozenset(('coverage-gate.json', 'coverage-gate.md',
                          'coverage-patch-targets.json', 'coverage-patch-targets.md'))


def _need(value, code):
    if not value:
        raise ValueError('completed-gate-replay-' + code)


def _linux_supported():
    return completed_native_replay._is_linux() and hasattr(os, 'waitid') and hasattr(os, 'WNOWAIT')


def _remaining(deadline):
    _need(type(deadline) in (int, float) and math.isfinite(deadline)
          and time.monotonic() < deadline, 'deadline')
    return deadline - time.monotonic()


@dataclass(frozen=True)
class GateReplayResult:
    """Mechanism result only. Neither replay nor comparison proves original success or eligibility."""
    run_id: int
    run_attempt: int
    head_sha: str
    source_capture_sha256: str
    base_commit: str
    original_report_sha256: str
    replay_report_sha256: str
    original_coverage_sha256: str
    replay_coverage_sha256: str
    diff_sha256: str
    diff_bytes: int
    argv: tuple[str, ...]
    exit_code: int
    process_records: tuple[tuple[tuple[str, ...], int, str, str], ...]
    stdout_sha256: str
    stderr_sha256: str
    output_files: tuple[tuple[str, str, int], ...]
    original_transcript_sha256: str
    reports_match: bool
    retained_work_root: Path
    cleanup_complete: bool = False
    validator_executed: bool = False
    original_gate_success_proven: bool = False
    test_inventory_success_proven: bool = False
    gate_eligible: bool = False
    admission_created: bool = False
    qualification_complete: bool = False


def _private_directory(path, code):
    path = Path(path)
    _need(path.is_absolute() and path.resolve(strict=True) == path, code + '-path')
    info = path.lstat()
    _need(stat.S_ISDIR(info.st_mode) and not stat.S_ISLNK(info.st_mode)
          and info.st_uid == os.geteuid() and stat.S_IMODE(info.st_mode) == 0o700, code + '-shape')
    return path


def _source_directory(path):
    path = Path(path)
    _need(path.is_absolute() and path.resolve(strict=True) == path, 'source-root-path')
    info = path.lstat()
    _need(stat.S_ISDIR(info.st_mode) and not stat.S_ISLNK(info.st_mode)
          and info.st_uid == os.geteuid(), 'source-root-shape')
    return path


def _workspace_inventory(root, deadline):
    """Bound retained invocation state after each subprocess has exited."""
    root_info = root.lstat()
    _need(stat.S_ISDIR(root_info.st_mode) and not stat.S_ISLNK(root_info.st_mode)
          and root_info.st_uid == os.geteuid() and stat.S_IMODE(root_info.st_mode) == 0o700,
          'workspace-root')
    pending = [root]
    total = nodes = 0
    while pending:
        _remaining(deadline)
        directory = pending.pop()
        for entry in os.scandir(directory):
            nodes += 1
            _need(nodes <= MAX_WORKSPACE_NODES, 'workspace-node-limit')
            info = entry.stat(follow_symlinks=False)
            _need(info.st_uid == os.geteuid(), 'workspace-owner')
            if stat.S_ISDIR(info.st_mode) and not stat.S_ISLNK(info.st_mode):
                pending.append(Path(entry.path))
            else:
                _need(stat.S_ISREG(info.st_mode) and info.st_nlink == 1, 'workspace-file-shape')
                total += info.st_size
                _need(total <= MAX_WORKSPACE_BYTES, 'workspace-byte-limit')
    return nodes, total


def _closed_environment(dotnet, scratch_root):
    env = completed_native_replay._controlled_environment(dotnet, scratch_root)
    env.update({'GIT_CONFIG_NOSYSTEM': '1', 'GIT_CONFIG_GLOBAL': os.devnull,
                'GIT_OPTIONAL_LOCKS': '0'})
    return env


def _git(root, *args, deadline):
    _remaining(deadline)
    result = completed_c1._git(root, *args, deadline=deadline)
    _remaining(deadline)
    return result


def _source_capture(capture_raw, root, *, current_binding, deadline, require_no_ignored=False):
    capture, binding = completed_c1._load_capture_and_binding(capture_raw, root,
        current_binding=current_binding, deadline=deadline)
    _need(capture['source'] == _git(root, 'rev-parse', 'HEAD', deadline=deadline)
          and capture['tree'] == _git(root, 'rev-parse', 'HEAD^{tree}', deadline=deadline),
          'source-head-tree')
    _need(_git(root, 'status', '--porcelain=v1', '--untracked-files=all', deadline=deadline) == '',
          'source-dirty')
    if require_no_ignored:
        _need(_git(root, 'status', '--porcelain=v1', '--untracked-files=all', '--ignored',
                   deadline=deadline) == '', 'source-ignored-files')
    _need(_git(root, 'cat-file', '-e', completed_gate.FIXED_DIFF_BASE + '^{commit}', deadline=deadline)
          == '', 'base-commit')
    _need(_git(root, 'merge-base', '--is-ancestor', completed_gate.FIXED_DIFF_BASE, 'HEAD', deadline=deadline)
          == '', 'base-ancestry')
    return capture, binding


def _read_file(path, *, deadline, maximum, expected_identity=None):
    path = Path(path)
    _need(path.is_absolute() and path.resolve(strict=True) == path, 'input-path')
    fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK | os.O_CLOEXEC)
    try:
        before = os.fstat(fd)
        _need(stat.S_ISREG(before.st_mode) and before.st_uid == os.geteuid() and before.st_nlink == 1
              and before.st_size <= maximum
              and (expected_identity is None or completed_c1._node_identity(before) == expected_identity),
              'input-shape')
        chunks = []
        size = 0
        while True:
            _remaining(deadline)
            part = os.read(fd, min(64 * 1024, maximum + 1 - size))
            if not part:
                break
            chunks.append(part)
            size += len(part)
            _need(size <= maximum, 'input-size')
        after = os.fstat(fd)
        named = os.stat(path, follow_symlinks=False)
        identity = lambda row: (row.st_dev, row.st_ino, row.st_mode, row.st_nlink,
                                row.st_size, row.st_mtime_ns, row.st_ctime_ns)
        _need(size == before.st_size and identity(before) == identity(after) == identity(named),
              'input-changed')
        return b''.join(chunks)
    finally:
        os.close(fd)


def _assert_pinned_file(fd, path, expected_identity):
    """Keep one inode live and prove its name still identifies that owned inode."""
    path = Path(path)
    opened = os.fstat(fd)
    named = os.stat(path, follow_symlinks=False)
    _need(stat.S_ISREG(opened.st_mode) and stat.S_ISREG(named.st_mode)
          and opened.st_uid == named.st_uid == os.geteuid()
          and opened.st_nlink == named.st_nlink == 1
          and stat.S_IMODE(opened.st_mode) == stat.S_IMODE(named.st_mode) == 0o600
          and completed_c1._node_identity(opened) == expected_identity
          and completed_c1._node_identity(named) == expected_identity,
          'coverage-custody')


def _pin_owned_file(path, expected_identity):
    path = Path(path)
    fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK | os.O_CLOEXEC)
    try:
        _assert_pinned_file(fd, path, expected_identity)
        return fd
    except BaseException:
        os.close(fd)
        raise


def _run_public_cli(source_root, coverage_path, output_path, *, deadline, cleanup_deadline=None):
    _remaining(deadline)
    dotnet = completed_native_replay._dotnet_path()
    assembly = source_root / 'Cli/ForgeTrust.AppSurface.Cli/bin/Release/net10.0/ForgeTrust.AppSurface.Cli.dll'
    _need(assembly.is_file() and not assembly.is_symlink() and assembly.resolve(strict=True) == assembly,
          'cli-assembly')
    argv = (
        dotnet, str(assembly), 'coverage', 'gate',
        '--coverage', str(coverage_path), '--repository-root', str(source_root),
        '--diff-base', completed_gate.FIXED_DIFF_BASE, '--min-line', '95', '--min-branch', '85',
        '--min-patch-line', '95', '--min-patch-branch', '85', '--patch-line-mode', 'codecov',
        '--output', str(output_path), '--no-github-summary',
    )
    env = _closed_environment(dotnet, output_path.parent / 'process-scratch')
    execution = completed_native_replay._run_fixed_process(argv, cwd=source_root, env=env,
        deadline=deadline, output_limit=DIFF_LIMIT, cleanup_deadline=cleanup_deadline)
    return argv, execution


def _save_process_logs(name, argv, execution, work, owned, deadline):
    code, stdout, stderr = execution
    log_dir = work / 'process-logs'
    if not os.path.lexists(log_dir):
        completed_c1._mkdir(log_dir, owned=owned, ownership_root=work)
    records = []
    for stream_name, body in (('stdout', stdout), ('stderr', stderr)):
        path = log_dir / f'{name}.{stream_name}'
        completed_c1._write_bytes(path, body, deadline=deadline,
                                  owned=owned, ownership_root=work)
        records.append(hashlib.sha256(body).hexdigest())
    return (tuple(argv), code, records[0], records[1])


def replay_completed_gate(stage, inventory, stage_directory, source_capture, source_root,
                          disposable_parent, *, deadline):
    """Replay the original public coverage gate in an authenticated disposable checkout.

    Input directories must already exist, be private, and be disjoint. The CLI is the sole
    numerical evaluator. This function does not claim original workflow/test success.
    """
    _remaining(deadline)
    _need(type(deadline) in (int, float) and math.isfinite(deadline)
          and 0 < deadline - time.monotonic() <= MAX_REPLAY_SECONDS, 'deadline-bound')
    _need(_linux_supported(), 'linux-required')
    gate_inputs = completed_gate.inspect_completed_gate_archive(
        stage, inventory, stage_directory, source_capture, deadline=deadline)
    _need(gate_inputs.original_report_passed is True, 'original-report-not-passing')
    source = _source_directory(source_root)
    _need(source == SOURCE_MOUNT, 'source-mount')
    parent = _private_directory(disposable_parent, 'disposable-parent')
    stage_root = Path(stage_directory).resolve(strict=True)
    _need(source != parent and source not in parent.parents and parent not in source.parents,
          'root-overlap')
    _need(stage_root != parent and stage_root not in parent.parents and parent not in stage_root.parents
          and stage_root != source and stage_root not in source.parents and source not in stage_root.parents,
          'stage-source-overlap')
    _need(not os.listdir(parent), 'disposable-parent-not-empty')
    _need(not os.path.lexists(source / 'TestResults')
          and not os.path.lexists(source / 'Cli/ForgeTrust.AppSurface.Cli/bin')
          and not os.path.lexists(source / 'Cli/ForgeTrust.AppSurface.Cli/obj'),
          'source-build-output-present')
    capture, binding = _source_capture(source_capture, source,
        current_binding=completed_c1._load_trusted_current_binding(deadline=deadline), deadline=deadline,
        require_no_ignored=True)
    _need(capture['source'] == stage.head_sha and capture['tree'] == stage.source_tree,
          'stage-source-binding')
    base = completed_gate.FIXED_DIFF_BASE
    dotnet = completed_native_replay._dotnet_path()
    git_env = _closed_environment(dotnet, parent)
    diff_argv = ('git', '--no-pager', '--no-optional-locks', '-c', 'core.fsmonitor=false',
         '-c', 'diff.external=', 'diff', '--unified=0', '--no-ext-diff', '--relative',
         base + '...HEAD', '--')
    diff_result = completed_native_replay._run_fixed_process(diff_argv,
        cwd=source, env=git_env, deadline=deadline - FINALIZE_RESERVE_SECONDS,
        output_limit=DIFF_LIMIT, cleanup_deadline=deadline)
    diff_code, diff, diff_error = diff_result
    _need(diff_code == 0 and not diff_error, 'diff-command')
    diff_sha = hashlib.sha256(diff).hexdigest()
    original_report_value = completed_gate._parse(gate_inputs.original_report, deadline)
    original_diff_source = original_report_value['patchDiffSource']
    _need(original_diff_source['bytes'] == len(diff) and original_diff_source['sha256'] == diff_sha
          and original_diff_source['empty'] == (len(diff) == 0), 'original-diff-binding')
    _need(gate_inputs.original_report and gate_inputs.comparison_base == base.encode() + b'\n',
          'original-input-binding')
    # Fixed input digest verifies bytes remain stable while build/replay runs.
    input_hashes = {name: hashlib.sha256(body).hexdigest() for name, body in (
        ('coverage', gate_inputs.coverage_xml), ('report', gate_inputs.original_report),
        ('base', gate_inputs.comparison_base))}
    work = parent / 'coverage-gate-replay'
    output = work / 'reports'
    owned = {}
    work_identity = None
    coverage_pin = None
    primary = None
    result = None
    try:
        _need(not os.path.lexists(work), 'output-exists')
        completed_c1._mkdir(work, owned=owned, ownership_root=work)
        work_identity = owned[()]
        completed_c1._mkdir(output, owned=owned, ownership_root=work)
        scratch = work / 'process-scratch'
        completed_c1._mkdir(scratch, owned=owned, ownership_root=work)
        coverage_path = work / 'coverage.cobertura.xml'
        completed_c1._write_bytes(coverage_path, gate_inputs.coverage_xml,
                                  deadline=deadline, owned=owned, ownership_root=work)
        coverage_identity = owned[('coverage.cobertura.xml',)]
        coverage_pin = _pin_owned_file(coverage_path, coverage_identity)
        process_records = [_save_process_logs('git-diff', diff_argv, diff_result, work, owned, deadline)]
        _remaining(deadline)
        dotnet = completed_native_replay._dotnet_path()
        env = _closed_environment(dotnet, scratch)
        version = (dotnet, '--version')
        version_code, version_out, version_err = completed_native_replay._run_fixed_process(
            version, cwd=source, env=env, deadline=deadline - FINALIZE_RESERVE_SECONDS,
            cleanup_deadline=deadline)
        _need(version_code == 0 and version_out.strip() == b'10.0.401', 'sdk-version')
        process_records.append(_save_process_logs('sdk-version', version,
            (version_code, version_out, version_err), work, owned, deadline))
        _workspace_inventory(work, deadline)
        restore = (dotnet, 'restore', 'Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj', '--locked-mode',
                   '--disable-parallel', '-m:1', '-nr:false', '-p:EvidencePrivateQualification=None')
        build = (dotnet, 'build', 'Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj',
                 '--configuration', 'Release', '--no-restore', '--disable-build-servers', '-m:1',
                 '-nr:false', '-p:UseSharedCompilation=false', '-warnaserror',
                 '-p:EvidencePrivateQualification=None')
        restore_result = completed_native_replay._run_fixed_process(
            restore, cwd=source, env=env, deadline=deadline - REPLAY_RESERVE_SECONDS - FINALIZE_RESERVE_SECONDS,
            cleanup_deadline=deadline)
        restore_code, restore_out, restore_err = restore_result
        process_records.append(_save_process_logs('restore', restore, restore_result,
                                                   work, owned, deadline))
        _workspace_inventory(work, deadline)
        _need(restore_code == 0, 'restore-exit')
        _need(_remaining(deadline) > REPLAY_RESERVE_SECONDS + FINALIZE_RESERVE_SECONDS,
              'build-reserve')
        build_result = completed_native_replay._run_fixed_process(
            build, cwd=source, env=env, deadline=deadline - REPLAY_RESERVE_SECONDS - FINALIZE_RESERVE_SECONDS,
            cleanup_deadline=deadline)
        build_code, build_out, build_err = build_result
        process_records.append(_save_process_logs('build', build, build_result,
                                                  work, owned, deadline))
        _workspace_inventory(work, deadline)
        _need(build_code == 0, 'build-exit')
        _source_capture(source_capture, source,
            current_binding=completed_c1._load_trusted_current_binding(deadline=deadline), deadline=deadline)
        _assert_pinned_file(coverage_pin, coverage_path, coverage_identity)
        _need(_remaining(deadline) > REPLAY_RESERVE_SECONDS, 'replay-reserve')
        _need(_remaining(deadline) > REPLAY_RESERVE_SECONDS + FINALIZE_RESERVE_SECONDS,
              'finalize-reserve')
        argv, execution = _run_public_cli(source, coverage_path, output,
                                          deadline=deadline - FINALIZE_RESERVE_SECONDS,
                                          cleanup_deadline=deadline)
        code, stdout, stderr = execution
        process_records.append(_save_process_logs('coverage-gate', argv, execution,
                                                  work, owned, deadline))
        _need(code == 0, 'cli-exit')
        _workspace_inventory(work, deadline)
        _need(set(os.listdir(output)) == GATE_OUTPUTS, 'output-inventory')
        _assert_pinned_file(coverage_pin, coverage_path, coverage_identity)
        report = _read_file(output / 'coverage-gate.json', deadline=deadline, maximum=completed_gate.MAX_REPORT_BYTES)
        gate_comparison = completed_gate.compare_gate_reports(
            gate_inputs.original_report, report,
            original_role_paths={'coverage': completed_gate.ORIGINAL_COVERAGE_PATH},
            replay_role_paths={'coverage': str(coverage_path)}, deadline=deadline)
        coverage_raw = _read_file(coverage_path, deadline=deadline, maximum=completed_gate.MAX_COBERTURA_BYTES,
                                  expected_identity=coverage_identity)
        _assert_pinned_file(coverage_pin, coverage_path, coverage_identity)
        _need(hashlib.sha256(coverage_raw).hexdigest() == input_hashes['coverage'], 'coverage-mutated')
        _need(hashlib.sha256(gate_inputs.original_report).hexdigest() == input_hashes['report']
              and hashlib.sha256(gate_inputs.comparison_base).hexdigest() == input_hashes['base'],
              'original-input-mutated')
        output_records = []
        for name in sorted(GATE_OUTPUTS):
            body = _read_file(output / name, deadline=deadline, maximum=completed_native_replay.MAX_LOG_BYTES)
            output_records.append((name, hashlib.sha256(body).hexdigest(), len(body)))
        repeated_inputs = completed_gate.inspect_completed_gate_archive(
            stage, inventory, stage_directory, source_capture, deadline=deadline)
        _need(repeated_inputs == gate_inputs, 'staged-inputs-changed')
        _source_capture(source_capture, source,
            current_binding=completed_c1._load_trusted_current_binding(deadline=deadline), deadline=deadline)
        _remaining(deadline)
        result = GateReplayResult(stage.run_id, stage.run_attempt, stage.head_sha,
            stage.source_capture_sha256, base, gate_comparison.original_sha256,
            gate_comparison.replay_sha256, input_hashes['coverage'], hashlib.sha256(coverage_raw).hexdigest(),
            diff_sha, len(diff), argv, code, tuple(process_records),
            hashlib.sha256(stdout).hexdigest(), hashlib.sha256(stderr).hexdigest(),
            tuple(output_records), hashlib.sha256(gate_inputs.transcript).hexdigest(),
            gate_comparison.reports_match, work)
    except BaseException as error:
        primary = error
    finally:
        if coverage_pin is not None:
            try:
                os.close(coverage_pin)
            except OSError as error:
                if primary is None:
                    primary = error
    if primary is not None or result is None:
        if primary is not None:
            note = 'completed-gate-replay-workspace-preserved:' + str(work)
            if hasattr(primary, 'add_note'):
                primary.add_note(note)
            else:
                primary.completed_gate_replay_workspace = str(work)
        raise primary or ValueError('completed-gate-replay-incomplete')
    return result
