#!/usr/bin/env bash
# Unprivileged build and actual pipe regression only. No sudo, supervisor/worker role, fixture or OS audit.
set -euo pipefail
umask 077
[[ $# == 4 && "$1" == --execute ]] || { echo 'build-invocation-rejected' >&2; exit 1; }
exec python3 -B - "$2" "$3" "$4" <<'PY'
import hashlib
import json
import os
import pathlib
import re
import selectors
import shutil
import signal
import stat
import subprocess
import sys
import tarfile
import time
import xml.etree.ElementTree as ET

SELECTED_VARIANT = 'N11'
BUILD_CONTRACT_SOURCE = '"""Pure build prerequisites shared by the image builder and native case readers.\n\nThis module performs no I/O and issues no native credit. All fourteen images\nmust carry actual passing source-selected core and CLI test results. Commands\nare checked by their operation, project and selector, not an incidental count\nof unrelated Git/package queries. Runtime case acceptance remains separate.\n"""\n\nimport pathlib\nimport re\n\nSCHEMA = "issue779-integrated16-build-v1"\nSELECTORS = ("None", *(f"N{i:02d}" for i in range(4, 17)))\nPIPE_METHOD = "OwnedRawPipeReadWrappingUsesHandleModeAndJoinsBothEofs"\nFRAME_METHOD = "OwnerDeathVariantIsExclusiveAndFrameIsExactBoundedData"\nCLI_METHOD = "CompiledImageOwnsOnlyItsSelectedCheckpoint"\nCORE_PROJECT = "Evidence/ForgeTrust.AppSurface.Evidence.Supervision.Tests/ForgeTrust.AppSurface.Evidence.Supervision.Tests.csproj"\nCLI_PROJECT = "Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj"\nCLI_TEST_PROJECT = "Cli/ForgeTrust.AppSurface.Cli.Tests/ForgeTrust.AppSurface.Cli.Tests.csproj"\nCORE_CLASS = "ForgeTrust.AppSurface.Evidence.Supervision.Tests."\nCLI_CLASS = "ForgeTrust.AppSurface.Cli.Tests.EvidenceNativeQualificationTests"\nSOURCE_PHASES = ("source_before", "source_after_assets", "source_after_build", "source_final")\n\n\nclass BuildContractError(ValueError):\n    """Closed metadata rejection; never include caller values in its message."""\n\n\ndef require(ok, category):\n    if not ok:\n        raise BuildContractError(category)\n\n\ndef is_sha(value):\n    return type(value) is str and re.fullmatch("[0-9a-f]{64}", value) is not None\n\n\ndef expected_regressions(selector):\n    require(selector in SELECTORS, "build-selector")\n    if selector in ("N13", "N14"):\n        return (("owner_death_phase_frame_regression", CORE_CLASS + "LinuxOwnerDeathPhaseFrameTests", FRAME_METHOD),\n                ("owner_death_cli_qualification", CLI_CLASS, CLI_METHOD))\n    return (("linux_pipe_regression", CORE_CLASS + "LinuxOutputPipesTests", PIPE_METHOD),\n            ("compiled_cli_qualification", CLI_CLASS, CLI_METHOD))\n\n\ndef validate_command_records(commands, selector):\n    require(selector in SELECTORS, "build-selector")\n    require(type(commands) is list and 0 < len(commands) <= 128, "build-command-bound")\n    for ordinal, row in enumerate(commands):\n        require(type(row) is dict and type(row.get("ordinal")) is int and row["ordinal"] == ordinal,\n                "build-command-ordinal")\n        args = row.get("argv")\n        require(type(args) is list and args and all(type(arg) is str and arg for arg in args),\n                "build-command-arguments")\n        # MSBuild accepts /p:, -property:, case aliases and multi-property\n        # arguments. A second selector in any such form cannot be ignored.\n        # Response files are also forbidden: they can supply unseen properties.\n        canonical = "-p:EvidencePrivateQualification=" + selector\n        require(not any(arg.startswith("@") for arg in args)\n                and all("evidenceprivatequalification" not in arg.lower()\n                        or (selector != "None" and arg == canonical) for arg in args),\n                "build-selector-alias-or-response-file")\n        require(type(row.get("exit")) is int and row["exit"] == 0 and row.get("failure") is None\n                and row.get("waited") is True and row.get("group_absent") is True\n                and row.get("forced_cleanup") is False and row.get("timed_out") is False\n                and row.get("late_completion") is not True, "build-command-terminal")\n        for name, expected in (("pipe_eofs", [True, True]), ("read_errors", [False, False]),\n                               ("write_errors", [False, False]), ("discarded_bytes", [0, 0])):\n            values = row.get(name)\n            require(type(values) is list and len(values) == 2\n                    and all(type(actual) is type(wanted) and actual == wanted\n                            for actual, wanted in zip(values, expected)), "build-command-output")\n        logs = row.get("logs")\n        require(type(logs) is list and len(logs) == 2\n                and all(type(log) is dict and is_sha(log.get("sha256"))\n                        and type(log.get("bytes")) is int and 0 <= log["bytes"] <= 8 * 1024 * 1024\n                        for log in logs), "build-command-logs")\n    selected = [row for row in commands if pathlib.PurePosixPath(row["argv"][0]).name == "dotnet"\n                and len(row["argv"]) > 1 and row["argv"][1] in ("restore", "publish", "test", "build")]\n    require([row["argv"][1] for row in selected] == ["restore", "publish", "restore", "test", "restore", "test"],\n            "build-operation-sequence")\n    projects = (CLI_PROJECT, CLI_PROJECT, CORE_PROJECT, CORE_PROJECT, CLI_TEST_PROJECT, CLI_TEST_PROJECT)\n    for row, project in zip(selected, projects):\n        args = row["argv"]\n        require(len(args) > 2 and args[2].endswith("/" + project), "build-project")\n        flags = [arg for arg in args if arg.startswith("-p:EvidencePrivateQualification=")]\n        require(flags == ([] if selector == "None" else ["-p:EvidencePrivateQualification=" + selector]),\n                "build-command-selector")\n        require(("--locked-mode" in args) if args[1] == "restore" else ("--no-restore" in args),\n                "build-restore-mode")\n        if args[1] in ("test", "publish"):\n            require("-p:UseSharedCompilation=false" in args, "build-compiler-mode")\n    for row, (_, class_name, method) in zip((selected[3], selected[5]), expected_regressions(selector)):\n        args = row["argv"]\n        require(args.count("--filter") == 1 and args[args.index("--filter") + 1]\n                == "FullyQualifiedName=" + class_name + "." + method, "build-test-filter")\n    require(sum(arg.startswith("-p:EvidencePrivateQualification=") for row in commands for arg in row["argv"])\n            == (0 if selector == "None" else 6), "build-selector-cardinality")\n    return True\n\n\ndef validate_build_receipt(receipt, selector, source):\n    """Validate a measured receipt against the independently pinned source tuple.\n\n    ``source`` has commit, parent, tree, count, capture, projection, map and\n    raw_map fields. It comes from the immutable common capture, never from the\n    receipt being validated. File/archive checks remain with the I/O caller.\n    """\n    require(selector in SELECTORS and type(receipt) is dict and type(source) is dict, "build-receipt-shape")\n    require(set(source) == {"commit", "parent", "tree", "count", "capture", "projection", "map", "raw_map"},\n            "build-source-binding-shape")\n    require(receipt.get("schema") == SCHEMA and type(receipt.get("exit")) is int and receipt["exit"] == 0\n            and receipt.get("failure") is None and receipt.get("diagnostics") == [], "build-receipt-terminal")\n    require(receipt.get("private_qualification") == selector and receipt.get("selected_variant") == selector,\n            "build-receipt-selector")\n    for field, binding in (("source_commit", "commit"), ("source_revision", "commit"), ("source_parent", "parent"),\n                           ("source_count", "count"), ("capture_sha256", "capture"),\n                           ("source_capture_sha256", "capture"), ("capture_input_sha256", "capture"),\n                           ("capture_projection_sha256", "projection"), ("source_map_sha256", "map"),\n                           ("source_capture_map_sha256", "raw_map"), ("harness_parent", "commit"),\n                           ("harness_retry_parent", "commit"), ("harness_retry_parent_parent", "parent")):\n        require(type(receipt.get(field)) is type(source[binding]) and receipt[field] == source[binding],\n                "build-receipt-source")\n    require(receipt.get("build_prerequisite_only") is True and receipt.get("test_execution") is True\n            and all(receipt.get(field) is False for field in ("native_execution", "native_acceptance", "checkpoint_pass",\n                                                            "review_clear", "source_review_clear", "fixture_review_clear",\n                                                            "root_transport_review_clear"))\n            and receipt.get("os_audit") is None, "build-receipt-scope")\n    validate_command_records(receipt.get("commands"), selector)\n    for phase in SOURCE_PHASES:\n        fact = receipt.get(phase)\n        require(type(fact) is dict and fact.get("head") == source["commit"] and fact.get("tree") == source["tree"]\n                and type(fact.get("count")) is int and fact["count"] == source["count"]\n                and fact.get("source_map_sha256") == source["map"]\n                and all(fact.get(key) is True for key in ("index_tree_matches", "physical_git_sha1", "physical_sha256_modes")),\n                "build-source-recheck")\n    for key, class_name, method in expected_regressions(selector):\n        fact = receipt.get(key)\n        require(type(fact) is dict and fact.get("class_name") == class_name and fact.get("method") == method\n                and fact.get("native_acceptance") is False and fact.get("root_factory_exercised") is False\n                and fact.get("unprivileged_library_behavior_only") is True\n                and is_sha(fact.get("trx_sha256")), "build-regression-binding")\n        counters = fact.get("counters")\n        require(type(counters) is dict and all(counters.get(key) == value for key, value in\n                {"total": "1", "executed": "1", "passed": "1", "failed": "0", "error": "0",\n                 "notExecuted": "0", "timeout": "0", "aborted": "0"}.items()), "build-regression-outcome")\n    known = {"owner_death_phase_frame_regression", "owner_death_cli_qualification", "linux_pipe_regression",\n             "compiled_cli_qualification"}\n    expected = {row[0] for row in expected_regressions(selector)}\n    require(not (known - expected) & set(receipt), "build-regression-foreign-selection")\n    return True\n'
BUILD_CONTRACT_SHA = '4e1e774f57d1b878a0985ac218a27490ae4de8da1f21f81cd696dbe6e9c399f8'
_contract_scope = {}
if hashlib.sha256(BUILD_CONTRACT_SOURCE.encode()).hexdigest() != BUILD_CONTRACT_SHA:
    raise ValueError('embedded-build-contract-pin')
exec(compile(BUILD_CONTRACT_SOURCE, '<reviewed-build-contract>', 'exec'), _contract_scope)

HEAD = '71d004d6cf5a791ddbb8e73e3c538248985a8505'
PARENT = '957da1f23ff743778fa39e3e559511ccdb281345'
HARNESS_PARENT = '71d004d6cf5a791ddbb8e73e3c538248985a8505'
TREE = 'd15b1f3a7532947d525e4d74cdc7457f6b8923ea'
CAPTURE_SHA = '8f5822ab0f5da555ef142a28321631f1960e61463fe14a83b33e83d521934046'
CAPTURE_PROJECTION_SHA = 'c2e846a09c6e7561eea1a1531d34a422ba0b7379a05be4e4fcb17bd18134f891'
UBUNTU_PREREQUISITE_SHA = '2962b893b617ff6d2e3e6846df4ea8b170da74ad9b3cd4848b4a0405c4b6daff'
NATIVE_RUNNER_SHA = 'ad1ffed5685c4a9af8de2d9923cad6d4b3476bf7fed5d82607b05460276fdac2'
SDK = '10.0.401'
COUNT = 2872
FILE_CAP = 256 * 1024 * 1024
TREE_CAP = 1024 * 1024 * 1024
NODE_CAP = 8192
LOG_CAP = 8 * 1024 * 1024
ALL_LOG_CAP = 64 * 1024 * 1024
START = time.monotonic()
FINAL = START + 1200
WORK = FINAL - 5
HARN, OUT, CAPTURE = map(pathlib.Path, sys.argv[1:])
RECORDS = []
LOG_BYTES = 0
PHASE = 'input'
RESULT = {'schema': 'issue779-integrated16-build-v1', 'exit': 1,
          'private_qualification': SELECTED_VARIANT, 'build_prerequisite_only': True, 'native_execution': False,
          'checkpoint_pass': False, 'os_audit': None, 'source_commit': HEAD,
          'source_revision': HEAD, 'source_parent': PARENT, 'source_count': COUNT,
          'test_execution': False, 'native_acceptance': False, 'review_clear': False,
          'source_review_clear': False, 'fixture_review_clear': False, 'root_transport_review_clear': False,
          'capture_sha256': CAPTURE_SHA, 'sdk_required': SDK, 'commands': RECORDS,
          'packaging_limits': {'file_bytes': FILE_CAP, 'tree_bytes': TREE_CAP,
                               'tree_nodes': NODE_CAP, 'combined_tool_runtime_bytes': TREE_CAP,
                               'combined_tool_runtime_nodes': NODE_CAP},
          'tree_measurements': {}}

class BuildFailure(ValueError):
    def __init__(self, category):
        super().__init__('build-validation-failed')
        self.category = category

def require(ok, category):
    if not ok:
        raise BuildFailure(category)

def check(final=False):
    if time.monotonic() >= (FINAL if final else WORK):
        raise TimeoutError('original-deadline')

def sha(data):
    return hashlib.sha256(data).hexdigest()

def unique(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, 'duplicate-json-member')
        result[key] = value
    return result

def decode(data):
    return json.loads(data.decode('utf-8', errors='strict'), object_pairs_hook=unique)

def identity(s):
    return (s.st_dev, s.st_ino, s.st_mode, s.st_uid, s.st_gid,
            s.st_nlink, s.st_size, s.st_mtime_ns, s.st_ctime_ns)

def directory(path):
    s = path.lstat()
    require(stat.S_ISDIR(s.st_mode), 'directory-shape')
    return (s.st_dev, s.st_ino, s.st_mode, s.st_uid, s.st_gid)

def regular(path, cap=FILE_CAP, collect=False, destination=None, mode=None, elf=False, final=False):
    check(final)
    before = path.lstat()
    require(stat.S_ISREG(before.st_mode) and before.st_nlink == 1 and before.st_size <= cap, 'regular-file-bound')
    fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK | os.O_CLOEXEC)
    dest = None
    blocks = []
    hasher = hashlib.sha256()
    git_hasher = hashlib.sha1(b'blob ' + str(before.st_size).encode() + b'\0')
    size = 0
    first = b''
    try:
        require(identity(os.fstat(fd)) == identity(before), 'file-open-substitution')
        if destination is not None:
            dest = os.open(destination, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC, 0o600)
        while True:
            check(final)
            block = os.read(fd, 65536)
            if not block:
                break
            if not first:
                first = block[:4]
            size += len(block)
            require(size <= before.st_size and size <= cap, 'file-growth')
            hasher.update(block)
            git_hasher.update(block)
            if collect:
                blocks.append(block)
            if dest is not None:
                pending = memoryview(block)
                while pending:
                    check(final)
                    written = os.write(dest, pending)
                    require(written > 0, 'short-write')
                    pending = pending[written:]
            check(final)
        require(size == before.st_size, 'file-short-read')
        if elf:
            require(first == b'\x7fELF', 'elf-magic')
        require(identity(os.fstat(fd)) == identity(before) == identity(path.lstat()), 'file-changed')
        if dest is not None:
            os.fchmod(dest, mode)
            os.fsync(dest)
            require(os.fstat(dest).st_size == size and os.fstat(dest).st_nlink == 1, 'copied-file-shape')
    finally:
        # Attempt both closes, even if the first reports an error.
        close_error = None
        for opened in (dest, fd):
            if opened is not None:
                try:
                    os.close(opened)
                except OSError as error:
                    close_error = close_error or error
        if close_error is not None:
            raise close_error
    check(final)
    return {'sha256': hasher.hexdigest(), 'git_sha1': git_hasher.hexdigest(),
            'bytes': size, 'mode': f'{stat.S_IMODE(before.st_mode):04o}',
            'data': b''.join(blocks) if collect else None}

def write_json(path, value):
    data = (json.dumps(value, sort_keys=True, separators=(',', ':')) + '\n').encode()
    require(len(data) <= 4 * 1024 * 1024, 'receipt-bound')
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC, 0o600)
    try:
        with os.fdopen(fd, 'wb', closefd=False) as output:
            output.write(data)
            output.flush()
            os.fsync(fd)
    finally:
        os.close(fd)
    return sha(data)


READ_BYTES = 65536
POLL_SECONDS = .02


def bounded_census(argv, cap, deadline):
    """Read the fixed ps helper incrementally and bound its direct child lifetime."""
    process = None
    selector = selectors.DefaultSelector()
    data = bytearray()
    complete = False
    error = None
    started = time.monotonic()
    work_end = deadline - min(.1, max(0, deadline-started)/2)
    try:
        if time.monotonic() >= work_end:
            raise TimeoutError('census-deadline')
        process = subprocess.Popen(argv, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                                   stderr=subprocess.DEVNULL, bufsize=0)
        os.set_blocking(process.stdout.fileno(), False)
        selector.register(process.stdout, selectors.EVENT_READ)
        while time.monotonic() < work_end:
            for key, _ in selector.select(min(POLL_SECONDS, max(0, work_end-time.monotonic()))):
                try:
                    block = os.read(key.fd, min(4096, cap+1-len(data)))
                except BlockingIOError:
                    continue
                if not block:
                    complete = True
                    break
                data.extend(block)
                if len(data) > cap:
                    raise ValueError('census-byte-bound')
            if complete:
                break
        if not complete:
            raise TimeoutError('census-deadline')
    except BaseException as caught:
        error = caught
    finally:
        if process is not None:
            # Its direct child PID stays reserved until the sole wait below. No group is signaled.
            if error is not None:
                try:
                    os.kill(process.pid, signal.SIGKILL)
                except ProcessLookupError:
                    pass
            try:
                process.wait(timeout=max(0, work_end-time.monotonic()))
            except subprocess.TimeoutExpired as caught:
                error = error or caught
                # EOF is not process exit. Its unreaped direct PID is still owned; kill then join
                # within the original reserve. No process-group signal or refreshed deadline.
                try:
                    os.kill(process.pid, signal.SIGKILL)
                except ProcessLookupError:
                    pass
                except OSError as signal_error:
                    error = error or signal_error
                try:
                    process.wait(timeout=max(0, deadline-time.monotonic()))
                except BaseException as join_error:
                    error = error or join_error
            except BaseException as caught:
                error = error or caught
            if process.stdout is not None:
                process.stdout.close()
        selector.close()
    if error is not None:
        raise error
    if process is None or process.returncode != 0 or time.monotonic() >= deadline:
        raise ValueError('census-terminal')
    return bytes(data)


def observe_leader(process, deadline=None):
    """Inspect a child without reaping it, retaining its PID and group identity."""
    if hasattr(os, 'waitid') and hasattr(os, 'WNOWAIT'):
        result = os.waitid(os.P_PID, process.pid, os.WEXITED | os.WNOHANG | os.WNOWAIT)
        return result is not None and result.si_pid == process.pid
    remaining = .5 if deadline is None else min(.5, deadline - time.monotonic())
    if remaining <= 0:
        raise TimeoutError('leader-observation-deadline')
    raw = bounded_census(['/bin/ps', '-p', str(process.pid), '-o', 'stat='], 4096, time.monotonic()+remaining)
    if not raw.strip():
        raise ChildProcessError('leader-observation-failed')
    return raw.decode('ascii', 'strict').strip().startswith('Z')


def descendants_settled(process, deadline):
    """Ignore the retained leader/zombies; require no other live group member."""
    if pathlib.Path('/proc/self/stat').exists():
        with os.scandir('/proc') as entries:
            for entry in entries:
                if time.monotonic() >= deadline:
                    raise TimeoutError('group-observation-deadline')
                if not entry.name.isdecimal() or int(entry.name) == process.pid:
                    continue
                try:
                    fd = os.open('/proc/' + entry.name + '/stat',
                                 os.O_RDONLY | os.O_CLOEXEC | os.O_NOFOLLOW)
                    try:
                        raw = os.read(fd, 4097)
                    finally:
                        os.close(fd)
                except (FileNotFoundError, ProcessLookupError):
                    continue
                if len(raw) > 4096:
                    raise ValueError('process-stat-bound')
                close = raw.rfind(b')')
                fields = raw[close + 2:].split() if close >= 0 else []
                if len(fields) < 3:
                    raise ValueError('process-stat-shape')
                if int(fields[2]) == process.pid and fields[0] not in (b'Z', b'X'):
                    return False
        return True
    remaining = deadline - time.monotonic()
    if remaining <= 0:
        raise TimeoutError('group-observation-deadline')
    raw = bounded_census(['/bin/ps', '-axo', 'pid=,pgid=,stat='], 1024*1024, time.monotonic()+min(.5, remaining))
    for row in raw.decode('ascii', 'strict').splitlines():
        fields = row.split()
        if len(fields) != 3:
            raise ValueError('group-observation-shape')
        if int(fields[0]) != process.pid and int(fields[1]) == process.pid and not fields[2].startswith(('Z', 'X')):
            return False
    return True


def owned_command(argv, *, cwd, env, logs, work_deadline, final_deadline,
                  output_cap, stream_cap=None, child_umask=-1):
    """Drain bounded files, join both EOFs/group, then reap; never signal after wait."""
    state = {'exit': None, 'waited': False, 'group_absent': False,
             'descendants_settled': False, 'leader_exit_observed': False,
             'pipe_eofs': [False, False], 'received_bytes': [0, 0],
             'retained_bytes': [0, 0], 'discarded_bytes': [0, 0],
             'read_errors': [False, False], 'write_errors': [False, False],
             'forced_cleanup': False, 'timed_out': False, 'failure': None}
    process = None
    handles = []
    selector = selectors.DefaultSelector()
    held = True

    def fail(category):
        if state['failure'] is None:
            state['failure'] = category

    def drain(deadline):
        delay = min(POLL_SECONDS, max(0, deadline - time.monotonic()))
        for key, _ in selector.select(delay):
            index = key.data
            try:
                data = os.read(key.fd, READ_BYTES)
            except BlockingIOError:
                continue
            except OSError:
                state['read_errors'][index] = True
                fail('pipe-read-error')
                selector.unregister(key.fileobj)
                continue
            if not data:
                state['pipe_eofs'][index] = True
                selector.unregister(key.fileobj)
                continue
            state['received_bytes'][index] += len(data)
            room = min(max(0, output_cap - sum(state['retained_bytes'])),
                       max(0, (stream_cap if stream_cap is not None else output_cap) - state['retained_bytes'][index]))
            keep = min(room, len(data)) if not state['write_errors'][index] else 0
            written = 0
            while written < keep:
                try:
                    count = os.write(handles[index], data[written:keep])
                    if count <= 0:
                        raise OSError('zero-write')
                except OSError:
                    state['write_errors'][index] = True
                    fail('log-write-error')
                    break
                written += count
                state['retained_bytes'][index] += count
            state['discarded_bytes'][index] += len(data) - written
            if len(data) > room:
                fail('log-bound')

    try:
        if time.monotonic() >= work_deadline:
            raise TimeoutError('pre-spawn-deadline')
        for path in logs:
            handles.append(os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL
                                   | os.O_NOFOLLOW | os.O_CLOEXEC, 0o600))
        if time.monotonic() >= work_deadline:
            raise TimeoutError('pre-spawn-deadline')
        process = subprocess.Popen(argv, cwd=cwd, env=env, stdin=subprocess.DEVNULL,
                                   stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                   start_new_session=True, bufsize=0, umask=child_umask)
        state['pid'] = process.pid
        for index, pipe in enumerate((process.stdout, process.stderr)):
            os.set_blocking(pipe.fileno(), False)
            selector.register(pipe, selectors.EVENT_READ, index)
        while time.monotonic() < work_deadline:
            drain(work_deadline)
            if state['failure'] is not None:
                break
            state['leader_exit_observed'] = observe_leader(process, work_deadline)
            if state['leader_exit_observed']:
                state['descendants_settled'] = descendants_settled(process, work_deadline)
                if state['descendants_settled'] and all(state['pipe_eofs']):
                    break
        else:
            state['timed_out'] = True
            fail('command-deadline')
    except BaseException as error:
        state['timed_out'] |= isinstance(error, TimeoutError)
        fail(type(error).__name__)
    finally:
        if process is not None:
            if state['failure'] is not None:
                try:
                    try:
                        exited = observe_leader(process, final_deadline)
                        settled = descendants_settled(process, final_deadline) if exited else False
                    except BaseException as error:
                        fail(type(error).__name__)
                        exited = settled = False
                    if not exited or not settled:
                        # The original leader remains unreaped, so its numeric PGID cannot be reused.
                        os.killpg(process.pid, signal.SIGKILL)
                        state['forced_cleanup'] = True
                except BaseException as error:
                    fail(type(error).__name__)
            while time.monotonic() < final_deadline:
                try:
                    drain(final_deadline)
                    state['leader_exit_observed'] = observe_leader(process, final_deadline)
                    if state['leader_exit_observed']:
                        state['descendants_settled'] = descendants_settled(process, final_deadline)
                    if state['leader_exit_observed'] and state['descendants_settled'] and all(state['pipe_eofs']):
                        break
                except BaseException as error:
                    fail(type(error).__name__)
                    break
            try:
                process.wait(timeout=max(0, final_deadline - time.monotonic()))
                held = False
                state['waited'] = True
                state['exit'] = process.returncode
            except BaseException as error:
                fail(type(error).__name__)
            # No group signal occurs after the sole wait. A final read-only absence check may reject.
            if not held:
                try:
                    os.killpg(process.pid, 0)
                except ProcessLookupError:
                    state['group_absent'] = True
                except OSError as error:
                    fail(type(error).__name__)
                if not state['group_absent']:
                    fail('group-absence-unproved')
            if state['exit'] != 0:
                fail('command-exit-nonzero')
        for pipe in (getattr(process, 'stdout', None), getattr(process, 'stderr', None)):
            if pipe is not None:
                try:
                    pipe.close()
                except OSError:
                    fail('pipe-close-error')
        try:
            selector.close()
        except OSError:
            fail('selector-close-error')
        for fd in handles:
            try:
                os.fsync(fd)
            except OSError:
                fail('log-flush-error')
            try:
                os.close(fd)
            except OSError:
                fail('log-close-error')
        if not all(state['pipe_eofs']):
            fail('pipe-eof-missing')
        if time.monotonic() >= work_deadline:
            state['timed_out'] = True
            fail('late-command-completion')
        if time.monotonic() >= final_deadline:
            fail('cleanup-deadline')
    return state


def append_event(event):
    path = OUT / 'receipts' / 'command-events.jsonl'
    fd = os.open(path, os.O_WRONLY | os.O_APPEND | os.O_CREAT | os.O_NOFOLLOW | os.O_CLOEXEC, 0o600)
    try:
        with os.fdopen(fd, 'wb', closefd=False) as output:
            output.write((json.dumps(event, sort_keys=True) + '\n').encode())
            output.flush()
            os.fsync(fd)
    finally:
        os.close(fd)

def run(argv, cwd, read_stdout=True, child_umask=-1):
    global LOG_BYTES
    check()
    record = {'ordinal': len(RECORDS), 'phase': PHASE, 'argv': argv,
              'pid': None, 'exit': None, 'timed_out': False, 'forced_cleanup': False,
              'waited': False, 'group_absent': False, 'failure': None,
              'child_umask': child_umask}
    RECORDS.append(record)
    append_event({'before_popen': record.copy()})
    logs = [OUT / 'receipts' / f'{record["ordinal"]:02d}-{kind}.log' for kind in ('stdout', 'stderr')]
    command_start = time.monotonic()
    terminal = owned_command(argv, cwd=cwd, env=ENV, logs=logs,
                             work_deadline=min(WORK, command_start + 180),
                             final_deadline=min(FINAL, command_start + 185),
                             output_cap=max(0, ALL_LOG_CAP - LOG_BYTES), stream_cap=LOG_CAP,
                             child_umask=child_umask)
    record.update(terminal)
    record['logs'] = []
    for path in logs:
        if path.exists():
            try:
                facts = regular(path, LOG_CAP, final=True)
                record['logs'].append({'name': path.name, 'bytes': facts['bytes'], 'sha256': facts['sha256']})
                LOG_BYTES += facts['bytes']
            except BaseException as error:
                record['logs'].append({'name': path.name, 'hash_error': type(error).__name__})
                record['failure'] = record['failure'] or type(error).__name__
    if LOG_BYTES > ALL_LOG_CAP:
        record['failure'] = record['failure'] or 'aggregate-log-bound'
    write_json(OUT / 'receipts' / f'command-{record["ordinal"]:02d}.json', record)
    check()
    require(record['failure'] is None and record['exit'] == 0 and record['waited']
            and record['group_absent'] and not record['forced_cleanup']
            and all(record['pipe_eofs']) and not any(record['read_errors'])
            and not any(record['write_errors']) and not any(record['discarded_bytes']), 'command-failed')
    return regular(logs[0], LOG_CAP, collect=True)['data'] if read_stdout else b''

def relative_name(name):
    path = pathlib.PurePosixPath(name)
    require(isinstance(name, str) and name and not path.is_absolute() and
            all(part not in ('', '.', '..') for part in name.split('/')) and
            not any(c in name for c in '\t\r\n\0') and path.as_posix() == name, 'relative-path')
    return path

def source_check(repo, source):
    hashes, modes = source['sha256'], source['modes']
    require(len(hashes) == COUNT and set(hashes) == set(modes), 'source-count')
    index_raw = run(['git', 'ls-files', '--stage', '-z'], repo)
    tree_raw = run(['git', 'ls-tree', '-rz', HEAD], repo)
    index, tree = {}, {}
    for raw in index_raw.split(b'\0'):
        if not raw:
            continue
        metadata, name = raw.split(b'\t', 1)
        mode, oid, stage = metadata.split()
        name = name.decode('utf-8', errors='strict')
        require(stage == b'0' and name not in index, 'index-row')
        index[name] = (mode.decode(), oid.decode())
    for raw in tree_raw.split(b'\0'):
        if not raw:
            continue
        metadata, name = raw.split(b'\t', 1)
        mode, kind, oid = metadata.split()
        name = name.decode('utf-8', errors='strict')
        require(kind == b'blob' and name not in tree, 'tree-row')
        tree[name] = (mode.decode(), oid.decode())
    require(index == tree and set(index) == set(hashes), 'index-tree-membership')
    parents = {}
    for name in sorted(hashes):
        check()
        relative_name(name)
        require(modes[name] in ('0644', '0755') and re.fullmatch('[0-9a-f]{64}', hashes[name]), 'source-map-value')
        path = repo / name
        parent = path.parent
        while parent != repo.parent:
            parents.setdefault(parent, directory(parent))
            require(directory(parent) == parents[parent], 'source-parent-substitution')
            parent = parent.parent
        facts = regular(path)
        matches = (facts['sha256'] == hashes[name] and facts['mode'] == modes[name] and
                   index[name] == ('100755' if modes[name] == '0755' else '100644', facts['git_sha1']))
        if not matches:
            RESULT['source_mismatch'] = {'path': name, 'expected_sha256': hashes[name],
                                         'actual_sha256': facts['sha256'], 'expected_mode': modes[name],
                                         'actual_mode': facts['mode'], 'index_git_sha1': index[name][1],
                                         'physical_git_sha1': facts['git_sha1']}
        require(matches, 'source-sha-mode-object')
    for parent, before in parents.items():
        require(directory(parent) == before, 'source-parent-changed')
    require(run(['git', 'rev-parse', 'HEAD'], repo).strip().decode() == HEAD and
            run(['git', 'rev-parse', 'HEAD^{tree}'], repo).strip().decode() == TREE and
            run(['git', 'show', '-s', '--format=%P', HEAD], repo).strip().decode() == PARENT, 'source-topology')
    require(not run(['git', 'status', '--porcelain=v1', '--untracked-files=all'], repo).strip(), 'source-not-clean')
    # Only these actual build outputs may be absent from the captured membership.
    projects = {str(pathlib.PurePosixPath(name).parent) for name in hashes if name.endswith('.csproj')}
    excluded = []
    for current, dirs, files in os.walk(repo, followlinks=False):
        check()
        current = pathlib.Path(current)
        for name in list(dirs):
            path = current / name
            rel = path.relative_to(repo).as_posix()
            if rel == '.git' or (name in ('bin', 'obj') and current.relative_to(repo).as_posix() in projects) or (name == 'node_modules' and rel.startswith('Web/')):
                directory(path)
                dirs.remove(name)
                excluded.append(rel)
            else:
                directory(path)
        for name in files:
            rel = (current / name).relative_to(repo).as_posix()
            if rel in hashes:
                continue
            require(rel.startswith('Web/') and (rel.endswith('.gen.css') or name == 'tailwindcss'), 'unexpected-ignored-output')
            regular(current / name)
            excluded.append(rel)
    check()
    return {'count': COUNT, 'physical_sha256_modes': True, 'physical_git_sha1': True,
            'index_tree_matches': True, 'head': HEAD, 'tree': TREE,
            'index_rows_sha256': sha(index_raw), 'tree_rows_sha256': sha(tree_raw),
            'source_map_sha256': sha(json.dumps(source, sort_keys=True, separators=(',', ':')).encode()),
            'expected_ignored_outputs_present': sorted(excluded)}

def scan(root, measurement_name=None):
    files, dirs = {}, {}
    total = 0
    complete = False
    try:
        for current, children, names in os.walk(root, followlinks=False):
            check()
            current = pathlib.Path(current)
            rel_dir = current.relative_to(root).as_posix()
            dirs[rel_dir] = directory(current)
            require(len(files) + len(dirs) + len(children) + len(names) <= NODE_CAP, 'node-count')
            for child in children:
                directory(current / child)
            for name in names:
                path = current / name
                relative = path.relative_to(root).as_posix()
                relative_name(relative)
                facts = regular(path)
                total += facts['bytes']
                files[relative] = {k: facts[k] for k in ('sha256', 'mode', 'bytes')}
                require(total <= TREE_CAP, 'tree-bytes')
        require(directory(root) == dirs['.'], 'tree-root-changed')
        complete = True
        return {'files': files, 'directories': dirs, 'file_count': len(files),
                'node_count': len(files) + len(dirs), 'total_bytes': total}
    finally:
        if measurement_name is not None:
            require(measurement_name == 'published', 'measurement-name')
            # Incomplete observations remain diagnostic data; they never pass a tree guard.
            largest = sorted(files, key=lambda name: (-files[name]['bytes'], name))[:25]
            RESULT['tree_measurements'][measurement_name] = {
                'complete': complete, 'sampled_file_count': len(files),
                'visited_directory_count': len(dirs), 'observed_bytes': total,
                'largest_sampled_files': [{'path': name, 'bytes': files[name]['bytes']}
                                          for name in largest]}

def compare_scan(first, second):
    require(first == second, 'tree-copy-or-substitution')

def copy_tree(source, target, measurement_name=None):
    before = scan(source, measurement_name)
    target.mkdir(mode=0o700)
    for name in sorted(before['directories'], key=lambda x: (len(pathlib.PurePosixPath(x).parts), x)):
        check()
        if name != '.':
            (target / name).mkdir(mode=0o700)
    for name, facts in sorted(before['files'].items()):
        mode = 0o555 if int(facts['mode'], 8) & 0o111 else 0o444
        copied = regular(source / name, destination=target / name, mode=mode,
                         elf=name.endswith('.so'))
        require(copied['sha256'] == facts['sha256'] and copied['bytes'] == facts['bytes'], 'copy-bytes')
    compare_scan(before, scan(source))
    for name in sorted(before['directories'], key=lambda x: len(pathlib.PurePosixPath(x).parts), reverse=True):
        os.chmod(target if name == '.' else target / name, 0o555, follow_symlinks=False)
        check()
    actual = scan(target)
    require(set(actual['files']) == set(before['files']), 'copy-paths')
    for name, facts in before['files'].items():
        require(actual['files'][name]['sha256'] == facts['sha256'] and actual['files'][name]['bytes'] == facts['bytes'], 'copied-sha')
    return actual

def source_export(repo, source):
    archive = OUT / 'source.tar'
    run(['git', 'archive', '--format=tar', '--output=' + str(archive), HEAD], repo)
    require(archive.lstat().st_size <= 128 * 1024 * 1024, 'archive-size')
    root = OUT / 'handoff' / 'source'
    root.mkdir(mode=0o700)
    seen = set()
    entries = 0
    with tarfile.open(archive, mode='r:') as tar:
        for member in tar:
            check()
            entries += 1
            require(entries <= 8192, 'archive-entry-count')
            name = member.name.rstrip('/')
            relative_name(name)
            if member.isdir():
                require(any(path.startswith(name + '/') for path in source['sha256']), 'archive-empty-directory')
                continue
            require(member.isfile() and name in source['sha256'] and name not in seen and member.size <= FILE_CAP, 'archive-file')
            target = root / name
            target.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
            hasher = hashlib.sha256()
            size = 0
            with tar.extractfile(member) as reader, target.open('xb') as writer:
                while True:
                    check()
                    block = reader.read(65536)
                    if not block:
                        break
                    size += len(block)
                    require(size <= member.size, 'archive-growth')
                    writer.write(block)
                    hasher.update(block)
                writer.flush()
                os.fsync(writer.fileno())
                os.fchmod(writer.fileno(), int(source['modes'][name], 8))
            check()
            require(size == member.size and hasher.hexdigest() == source['sha256'][name], 'export-bytes')
            seen.add(name)
    require(seen == set(source['sha256']), 'export-membership')
    actual = scan(root)
    require(actual['file_count'] == COUNT, 'export-count')
    for name, facts in actual['files'].items():
        require(facts['sha256'] == source['sha256'][name] and facts['mode'] == source['modes'][name], 'export-mode')
    return actual

def numeric(value):
    require(isinstance(value, str) and re.fullmatch(r'(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)', value), 'stable-runtime-version')
    return tuple(int(part) for part in value.split('.'))

def versions(root):
    directory(root)
    names = list(root.iterdir())
    require(0 < len(names) <= 32, 'installed-version-count')
    available = {}
    for path in names:
        check()
        version = numeric(path.name)
        directory(path)
        available[version] = path
    return available

def select_framework(requested, available, roll):
    v = numeric(requested)
    require(v[0] == 10, 'framework-major')
    if roll == 'Disable':
        require(v in available, 'exact-runtime-missing')
        return v
    if roll == 'LatestPatch':
        candidates = [x for x in available if x[:2] == v[:2] and x >= v]
    else:  # Default Minor: lowest compatible minor, highest patch in that minor.
        candidates = [x for x in available if x[0] == v[0] and x >= v]
        if candidates:
            minor = min(x[1] for x in candidates)
            candidates = [x for x in candidates if x[1] == minor]
    require(bool(candidates), 'compatible-runtime-missing')
    return max(candidates)

def require_prerequisite_source(receipt):
    # Closed compilation-bound provenance check, after schema/package checks and before host copying.
    # Receipt data supplies no root execution, deployment or acceptance authority. Actual selected
    # runtime files still undergo retained reads, full SHA/ELF auditing and independent containment.
    require(receipt['prerequisite_script_sha256'] == UBUNTU_PREREQUISITE_SHA
            and receipt['runner_sha256'] == NATIVE_RUNNER_SHA,
            'ubuntu-runtime-prerequisite-source')

def ubuntu_runtime_host():
    # Package data conveys provenance only. The complete selected tree is still copied, hashed and ELF-audited.
    prerequisite_root = OUT.parent / 'issue779-csharp-ubuntu-runtime'
    require(not os.path.lexists(prerequisite_root / 'late-publication-failure.json'), 'late-ubuntu-runtime-prerequisite')
    fact = regular(prerequisite_root / 'receipt.json', 1048576, collect=True)
    receipt = decode(fact['data'])
    require(receipt['schema'] == 'issue779-ubuntu-runtime-prerequisite-v1'
            and type(receipt['exit']) is int and receipt['exit'] == 0
            and receipt.get('failure') is None and receipt['native_csharp_dispatched'] is False,
            'ubuntu-runtime-prerequisite-result')
    expected_packages = ('dotnet-host-10.0', 'dotnet-hostfxr-10.0', 'dotnet-runtime-10.0', 'aspnetcore-runtime-10.0')
    version = '10.0.12-0ubuntu1~24.04.1'
    require(receipt['package_version'] == version and set(receipt['packages']) == set(expected_packages)
            and all(v == {'architecture': 'amd64', 'version': version, 'status': 'ii '}
                    for v in receipt['packages'].values()), 'ubuntu-runtime-packages')
    require_prerequisite_source(receipt)
    require(len(receipt['commands']) == 5
            and all(type(c['exit']) is int and c['exit'] == 0 and c['failure'] is None
                    and c['waited'] is True and c['group_absent'] is True
                    and c['forced_cleanup'] is False and c['timed_out'] is False
                    and (not c['root_utility_unit'] or (c['root_start_guard_closed'] is True
                         and c['root_utility_cgroup_empty'] is True)) for c in receipt['commands']),
            'ubuntu-runtime-prerequisite-join')
    host = pathlib.Path('/usr/lib/dotnet/dotnet')
    require(receipt['runtime_host'] == str(host) and receipt['runtime_root'] == str(host.parent)
            and host.resolve(strict=True) == host, 'ubuntu-runtime-host-name')
    actual = regular(host, elf=True)
    require(actual['sha256'] == receipt['runtime_host_sha256'], 'ubuntu-runtime-host-sha')
    RESULT['execution_runtime_prerequisite'] = {'receipt_sha256': fact['sha256'],
        'packages': receipt['packages'], 'host_sha256': actual['sha256']}
    return host

def runtime_copy(tool, dotnet):
    config_path = tool / 'ForgeTrust.AppSurface.Cli.runtimeconfig.json'
    config_fact = regular(config_path, 65536, collect=True)
    config = decode(config_fact['data'])
    options = config['runtimeOptions']
    require(isinstance(options, dict) and options.get('tfm') == 'net10.0' and 'includedFrameworks' not in options,
            'fdd-runtimeconfig')
    require('rollForwardOnNoCandidateFx' not in options and options.get('applyPatches', True) is True,
            'legacy-rollforward-unsupported')
    roll = options.get('rollForward', 'Minor')
    require(roll in ('Disable', 'LatestPatch', 'Minor'), 'rollforward-unsupported')
    frameworks = options.get('frameworks')
    if frameworks is None:
        frameworks = [options['framework']]
    else:
        require('framework' not in options, 'framework-shape-ambiguous')
    require(isinstance(frameworks, list) and 1 <= len(frameworks) <= 2, 'framework-count')
    runtime_root = dotnet.parent
    require(runtime_root == pathlib.Path('/usr/lib/dotnet'), 'ubuntu-runtime-root')
    root_identity = directory(runtime_root)
    for relative in ('host', 'host/fxr', 'shared'):
        directory(runtime_root / relative)
        require((runtime_root / relative).resolve(strict=True) == runtime_root / relative, 'runtime-ancestor-alias')
    selected = {}
    for framework in frameworks:
        require(isinstance(framework, dict) and set(framework) == {'name', 'version'}, 'framework-row')
        name = framework['name']
        require(name in ('Microsoft.NETCore.App', 'Microsoft.AspNetCore.App') and name not in selected, 'framework-name')
        available = versions(runtime_root / 'shared' / name)
        v = select_framework(framework['version'], available, roll)
        require(v == (10, 0, 12), 'ubuntu-execution-framework-version')
        selected[name] = {'requested': framework['version'], 'selected': '.'.join(map(str, v)), 'path': available[v]}
    require('Microsoft.NETCore.App' in selected, 'netcore-framework-required')
    # Fail closed instead of guessing multi-framework version reconciliation.
    require(len({row['selected'] for row in selected.values()}) == 1, 'framework-selection-conflict')
    fxrs = {v: path for v, path in versions(runtime_root / 'host' / 'fxr').items() if v[0] == 10}
    require(bool(fxrs), 'hostfxr-missing')
    fxr_version = max(fxrs)
    require(fxr_version == (10, 0, 12), 'ubuntu-execution-hostfxr-version')
    require(fxr_version[:2] == numeric(selected['Microsoft.NETCore.App']['selected'])[:2], 'fxr-framework-minor-conflict')
    fxr = fxrs[fxr_version] / 'libhostfxr.so'
    require(set(p.name for p in fxrs[fxr_version].iterdir()) == {'libhostfxr.so'}, 'hostfxr-directory-shape')
    root = OUT / 'handoff' / 'runtime'
    root.mkdir(mode=0o700)
    (root / 'host' / 'fxr' / '.'.join(map(str, fxr_version))).mkdir(parents=True, mode=0o700)
    (root / 'shared').mkdir(mode=0o700)
    original_dotnet = regular(dotnet, destination=root / 'dotnet', mode=0o555, elf=True)
    original_fxr = regular(fxr, destination=root / 'host' / 'fxr' / '.'.join(map(str, fxr_version)) / 'libhostfxr.so', mode=0o444, elf=True)
    for name, row in selected.items():
        (root / 'shared' / name).mkdir(mode=0o700)
        copy_tree(row['path'], root / 'shared' / name / row['selected'])
    for current, dirs, files in os.walk(root, topdown=False, followlinks=False):
        check()
        os.chmod(current, 0o555, follow_symlinks=False)
    inventory = scan(root)
    require(directory(runtime_root) == root_identity, 'runtime-root-substitution')
    return inventory, {'runtimeconfig_sha256': config_fact['sha256'], 'roll_forward': roll,
                       'selection_policy': 'stable10-Minor-or-LatestPatch-or-Disable;conflicts-reject',
                       'dotnet_source': str(dotnet), 'dotnet_sha256': original_dotnet['sha256'],
                       'fxr_version': '.'.join(map(str, fxr_version)), 'fxr_sha256': original_fxr['sha256'],
                       'frameworks': {name: {'requested': row['requested'], 'selected': row['selected']} for name, row in selected.items()},
                       'sdk_copied': False, 'os_or_execution_attestation': False}

def publish_inventory(name, inventory):
    path = OUT / 'handoff' / (name + '.tsv')
    # EXACT J order; no header and no byte-count fourth column.
    data = ''.join(f'{row["mode"]}\t{row["sha256"]}\t{relative}\n'
                   for relative, row in sorted(inventory['files'].items())).encode('utf-8')
    require(len(data) <= 1024 * 1024, 'tsv-bound')
    with path.open('xb') as output:
        output.write(data)
        output.flush()
        os.fsync(output.fileno())
    node_map = {'schema': 'issue779-build-node-inventory-v1', 'root_name': name,
                'files': inventory['files'],
                'directories': {relative: {'mode': f'{stat.S_IMODE(row[2]):04o}'} for relative, row in inventory['directories'].items()}}
    node_sha = write_json(OUT / 'handoff' / (name + '-nodes.json'), node_map)
    check()
    return {'file_count': inventory['file_count'], 'node_count': inventory['node_count'],
            'total_bytes': inventory['total_bytes'], 'tsv_sha256': sha(data), 'nodes_sha256': node_sha}


def installation_selection(manifest, source_paths, actual_status):
    """Validate only test-harness additions; never permit product mutation."""
    require(type(manifest) is dict and set(manifest) == {
        'schema', 'source_commit', 'capture_sha256', 'files'}, 'installation-map-shape')
    require(manifest['schema'] == 'issue779-integrated16-installation-map-v1'
            and manifest['source_commit'] == HEAD
            and manifest['capture_sha256'] == CAPTURE_SHA, 'installation-map-binding')
    files = manifest['files']
    require(type(files) is dict and 1 <= len(files) <= 512, 'installation-map-count')
    own = '.github/issue779-csharp-build/installation-map.json'
    for name, row in files.items():
        require(type(name) is str and name not in source_paths and name != own
                and len(name.encode('utf-8')) <= 4096
                and all(part not in ('', '.', '..') for part in name.split('/')),
                'installation-map-path')
        require(name.startswith(('.github/issue779-csharp-build/',
                                 '.github/issue779-csharp-native/',
                                 '.github/issue779-integrated16/'))
                or name == '.github/workflows/issue779-csharp-linux-native.yml',
                'installation-map-scope')
        require(type(row) is dict and set(row) == {'sha256', 'mode'}
                and type(row['sha256']) is str
                and re.fullmatch('[0-9a-f]{64}', row['sha256']) is not None
                and row['mode'] in ('0644', '0755'), 'installation-map-row')
    expected = dict.fromkeys((*files, own), 'A')
    require(actual_status == expected, 'exact-retry-installation-delta')
    return files

def measured_regression(project, key, class_name, method, clone, selected_dotnet):
    qualification = [] if SELECTED_VARIANT == 'None' else ['-p:EvidencePrivateQualification=' + SELECTED_VARIANT]
    results = OUT / 'receipts' / key
    run([str(selected_dotnet), 'restore', project, '--locked-mode', *qualification,
         '-p:UseSharedCompilation=false'], clone)
    run([str(selected_dotnet), 'test', project, '--no-restore', *qualification,
         '-p:UseSharedCompilation=false', '--filter', 'FullyQualifiedName=' + class_name + '.' + method,
         '--logger', 'trx;LogFileName=selected.trx', '--results-directory', str(results)], clone)
    fact = regular(results / 'selected.trx', 256 * 1024, collect=True)
    require(b'<!DOCTYPE' not in fact['data'] and b'<!ENTITY' not in fact['data'], 'regression-trx-declaration')
    document = ET.fromstring(fact['data'])
    ns = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
    counter_rows = document.findall('.//t:Counters', ns)
    definitions = document.findall('.//t:UnitTest', ns)
    outcomes = document.findall('.//t:UnitTestResult', ns)
    require(len(counter_rows) == len(definitions) == len(outcomes) == 1, 'regression-trx-count')
    counters = counter_rows[0].attrib
    require(all(counters.get(k) == v for k, v in
                {'total': '1', 'executed': '1', 'passed': '1', 'failed': '0', 'error': '0',
                 'notExecuted': '0', 'timeout': '0', 'aborted': '0'}.items()), 'regression-trx-outcome')
    test_id = definitions[0].get('id')
    require(type(test_id) is str and re.fullmatch(r'[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}', test_id)
            is not None, 'regression-trx-id')
    test_method = definitions[0].find('t:TestMethod', ns)
    require(test_method is not None and test_method.get('className') == class_name
            and test_method.get('name') == method and outcomes[0].get('outcome') == 'Passed'
            and outcomes[0].get('testId') == test_id, 'regression-trx-method')
    RESULT[key] = {'method': method, 'class_name': class_name, 'counters': counters,
                   'trx_sha256': fact['sha256'], 'unprivileged_library_behavior_only': True,
                   'root_factory_exercised': False, 'native_acceptance': False,
                   'runtime_basis': 'selected SDK dotnet host'}


def main():
    global PHASE, ENV
    require(sys.platform == 'linux' and os.uname().machine == 'x86_64' and os.geteuid() != 0, 'unprivileged-linux-x64')
    require(all(p.is_absolute() and str(p) == str(p.resolve(strict=False)) for p in (HARN, OUT, CAPTURE)), 'canonical-input-path')
    require(not OUT.exists() and not OUT.is_symlink(), 'fresh-output')
    capture_fact = regular(CAPTURE, 4 * 1024 * 1024, collect=True)
    capture = decode(capture_fact['data'])
    require(capture.get('schema') == 'issue779-csharp-common-source-capture-v4', 'capture-schema')
    require(type(capture.get('source')) is dict and set(capture['source']) == {'sha256', 'modes'} and
            type(capture['source']['sha256']) is dict and type(capture['source']['modes']) is dict and
            len(capture['source']['sha256']) == COUNT and len(capture['source']['modes']) == COUNT, 'capture-source-map')
    capture['tree'] = capture.pop('source_tree')
    fields = ('validation_commit', 'previous_validation_commit', 'source_count', 'tree', 'source')
    projection = {key: capture[key] for key in fields}
    projection_sha = sha(json.dumps(projection, sort_keys=True, separators=(',', ':')).encode())
    require(capture_fact['sha256'] == CAPTURE_SHA and projection_sha == CAPTURE_PROJECTION_SHA, 'capture-pin')
    RESULT['capture_input_sha256'] = capture_fact['sha256']
    RESULT['capture_projection_sha256'] = projection_sha
    RESULT['source_capture_sha256'] = CAPTURE_SHA
    RESULT['source_map_sha256'] = sha(json.dumps(capture['source'], sort_keys=True, separators=(',', ':')).encode())
    full_rows = {name: {'sha256': digest, 'mode': capture['source']['modes'][name]}
                 for name, digest in capture['source']['sha256'].items()}
    RESULT['source_capture_map_sha256'] = sha(json.dumps(full_rows, sort_keys=True, separators=(',', ':')).encode())
    require(capture['validation_commit'] == HEAD and capture['previous_validation_commit'] == PARENT and capture['source_count'] == COUNT and capture['tree'] == TREE, 'capture-result')
    OUT.mkdir(mode=0o700)
    (OUT / 'receipts').mkdir(mode=0o700)
    (OUT / 'handoff').mkdir(mode=0o700)
    ENV = dict(os.environ)
    # Caller runtime overrides would make the emitted runtimeconfig insufficient.
    require(not any(k.startswith('DOTNET_ROLL_FORWARD') or k in ('DOTNET_ADDITIONAL_DEPS', 'DOTNET_SHARED_STORE', 'DOTNET_STARTUP_HOOKS') for k in ENV), 'runtime-environment-override')
    ENV.update({'GIT_OPTIONAL_LOCKS': '0', 'DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER': '1',
                'MSBUILDDISABLENODEREUSE': '1', 'DOTNET_NOLOGO': '1', 'DOTNET_CLI_TELEMETRY_OPTOUT': '1'})
    RESULT['environment_names_set'] = sorted(k for k in ENV if k in ('GIT_OPTIONAL_LOCKS', 'DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER', 'MSBUILDDISABLENODEREUSE', 'DOTNET_NOLOGO', 'DOTNET_CLI_TELEMETRY_OPTOUT'))
    PHASE = 'source'
    harness_head = run(['git', 'rev-parse', 'HEAD'], HARN).strip().decode()
    retry_parent = run(['git', 'show', '-s', '--format=%P', 'HEAD'], HARN).strip().decode()
    require(retry_parent == HARNESS_PARENT, 'direct-retry-parent')
    require(run(['git', 'show', '-s', '--format=%P', retry_parent], HARN).strip().decode() == PARENT, 'captured-source-parent-topology')
    installation_path = HARN / '.github/issue779-csharp-build/installation-map.json'
    installation_fact = regular(installation_path, 4 * 1024 * 1024, collect=True)
    installation_git = run(['git', 'show', 'HEAD:.github/issue779-csharp-build/installation-map.json'], HARN)
    require(installation_git == installation_fact['data'], 'installation-map-worktree-binding')
    installation_status = dict((name, status) for status, name in
                              (row.split('\t') for row in run(['git', 'diff', '--name-status', HARNESS_PARENT, 'HEAD'], HARN).decode().splitlines()))
    installation_rows = installation_selection(decode(installation_fact['data']), capture['source']['sha256'], installation_status)
    installed_git = {}
    for node in run(['git', 'ls-tree', '-r', '-z', 'HEAD'], HARN).split(b'\0'):
        if not node:
            continue
        metadata, name = node.split(b'\t', 1)
        mode, kind, blob = metadata.decode('ascii').split(' ')
        relative = name.decode('utf-8')
        require(kind == 'blob' and mode in ('100644', '100755')
                and re.fullmatch('[0-9a-f]{40}', blob) is not None
                and relative not in installed_git, 'installation-git-tree-shape')
        installed_git[relative] = (mode, blob)
    for name, row in installation_rows.items():
        installed = regular(HARN / name, 4 * 1024 * 1024, collect=True, mode=int(row['mode'], 8))
        require(installed['sha256'] == row['sha256'], 'installation-file-sha')
        blob = hashlib.sha1(b'blob ' + str(len(installed['data'])).encode() + b'\0' + installed['data']).hexdigest()
        require(installed_git[name] == ('100755' if row['mode'] == '0755' else '100644', blob), 'installation-file-git-binding')
    RESULT['installation_map_sha256'] = installation_fact['sha256']
    RESULT['harness_retry_parent'] = retry_parent
    RESULT['harness_retry_parent_parent'] = PARENT
    RESULT['harness_retry_installation_delta'] = installation_status
    run(['git', 'merge-base', '--is-ancestor', HEAD, 'HEAD'], HARN)
    RESULT['harness_parent'] = HARNESS_PARENT
    RESULT['harness_commit'] = harness_head
    clone = OUT / 'compile'
    run(['git', 'clone', '--no-hardlinks', '--no-checkout', str(HARN), str(clone)], HARN, child_umask=0o022)
    run(['git', 'checkout', '--detach', HEAD], clone, child_umask=0o022)
    RESULT['source_before'] = source_check(clone, capture['source'])
    source_inventory = source_export(clone, capture['source'])
    PHASE = 'sdk-assets'
    selected_dotnet = (pathlib.Path(os.environ['DOTNET_ROOT']) / 'dotnet').resolve(strict=True)
    require(selected_dotnet.name == 'dotnet', 'dotnet-path')
    dotnet_fact = regular(selected_dotnet, elf=True)
    RESULT['selected_dotnet_sha256'] = dotnet_fact['sha256']
    require(run([str(selected_dotnet), '--version'], clone).strip().decode() == SDK, 'sdk-pin')
    run([str(selected_dotnet), '--info'], clone)
    run([str(selected_dotnet), '--list-runtimes'], clone)
    require(run(['pnpm', '--version'], clone).strip().decode() == '11.1.3', 'pnpm-pin')
    require(run(['node', '--version'], clone).strip().decode().startswith('v24.'), 'node-major')
    run(['pnpm', '--dir', 'Web', 'install', '--frozen-lockfile'], clone, child_umask=0o022)
    run(['pnpm', '--dir', 'Web', 'run', 'assets:build'], clone, child_umask=0o022)
    RESULT['source_after_assets'] = source_check(clone, capture['source'])
    PHASE = 'restore-publish'
    project = str(clone / 'Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj')
    qualification = [] if SELECTED_VARIANT == 'None' else ['-p:EvidencePrivateQualification=' + SELECTED_VARIANT]
    run([str(selected_dotnet), 'restore', project, '--locked-mode', *qualification,
         '-p:ContinuousIntegrationBuild=true', '-p:SelfContained=false', '-p:UseAppHost=false'], clone)
    published = OUT / 'published'
    run([str(selected_dotnet), 'publish', project, '--no-restore', '-c', 'Release', *qualification,
         '-p:ContinuousIntegrationBuild=true', '-p:SelfContained=false', '-p:UseAppHost=false',
         '-p:UseSharedCompilation=false', '-nr:false', '-o', str(published)], clone)
    RESULT['selected_variant'] = RESULT['private_qualification'] = SELECTED_VARIANT
    require(sys.platform == 'linux' and os.getuid() != 0 and os.geteuid() != 0, 'regression-platform-and-user')
    for key, class_name, method in _contract_scope['expected_regressions'](SELECTED_VARIANT):
        selected_project = (_contract_scope['CLI_TEST_PROJECT'] if class_name == _contract_scope['CLI_CLASS']
                            else _contract_scope['CORE_PROJECT'])
        measured_regression(str(clone / selected_project), key, class_name, method, clone, selected_dotnet)
    _contract_scope['validate_command_records'](RECORDS, SELECTED_VARIANT)
    RESULT['test_execution'] = True
    RESULT['source_after_build'] = source_check(clone, capture['source'])
    # Compiler/analyzer warnings are retained; none are suppressed or passed off as clean.
    diagnostics = []
    for record in RECORDS:
        for log in record['logs']:
            data = regular(OUT / 'receipts' / log['name'], LOG_CAP, collect=True)['data']
            for line in data.decode('utf-8', errors='replace').splitlines():
                if re.search(r'\b(?:warning|error)\s+[A-Za-z]+[0-9]+\s*:', line, re.IGNORECASE):
                    diagnostics.append({'command': record['ordinal'], 'kind': 'warning' if re.search(r'\bwarning\b', line, re.IGNORECASE) else 'error'})
                    require(len(diagnostics) <= 1024, 'diagnostics-bound')
    RESULT['diagnostics'] = diagnostics
    require(not diagnostics, 'compiler-or-analyzer-diagnostics')
    PHASE = 'handoff'
    require((published / 'ForgeTrust.AppSurface.Cli.dll').is_file(), 'cli-output-missing')
    tool_inventory = copy_tree(published, OUT / 'handoff' / 'tool', 'published')
    runtime_inventory, runtime_selection = runtime_copy(OUT / 'handoff' / 'tool', ubuntu_runtime_host())
    RESULT['handoff_measurements'] = {name: {'total_bytes': inv['total_bytes'],
                                            'file_count': inv['file_count'],
                                            'node_count': inv['node_count']}
                                     for name, inv in (('tool', tool_inventory), ('runtime', runtime_inventory))}
    require(tool_inventory['total_bytes'] + runtime_inventory['total_bytes'] <= TREE_CAP and
            tool_inventory['node_count'] + runtime_inventory['node_count'] <= NODE_CAP, 'tool-runtime-combined-bound')
    RESULT['runtime_selection'] = runtime_selection
    RESULT['artifacts'] = {name: publish_inventory(name, inv) for name, inv in
                           (('source', source_inventory), ('tool', tool_inventory), ('runtime', runtime_inventory))}
    compare_scan(source_inventory, scan(OUT / 'handoff' / 'source'))
    compare_scan(tool_inventory, scan(OUT / 'handoff' / 'tool'))
    compare_scan(runtime_inventory, scan(OUT / 'handoff' / 'runtime'))
    RESULT['source_final'] = source_check(clone, capture['source'])
    require(regular(selected_dotnet, elf=True)['sha256'] == dotnet_fact['sha256'], 'selected-dotnet-changed')
    check()
    _contract_scope['validate_build_receipt']({**RESULT, 'exit': 0}, SELECTED_VARIANT,
        {'commit': HEAD, 'parent': PARENT, 'tree': TREE, 'count': COUNT, 'capture': CAPTURE_SHA,
         'projection': CAPTURE_PROJECTION_SHA, 'map': RESULT['source_map_sha256'],
         'raw_map': RESULT['source_capture_map_sha256']})

try:
    main()
    RESULT['exit'] = 0
except BaseException as error:
    RESULT['exit'] = 1
    RESULT['failure'] = {'phase': PHASE, 'error_class': type(error).__name__,
                         'category': error.category if isinstance(error, BuildFailure) else None}
RESULT['elapsed_seconds'] = time.monotonic() - START
RESULT['work_deadline_seconds'] = 1195
RESULT['original_deadline_seconds'] = 1200
receipt_sha = None
if (OUT / 'receipts').is_dir():
    try:
        check(final=True)
        receipt_sha = write_json(OUT / 'receipts' / 'build-receipt.json', RESULT)
        check(final=True)
    except BaseException as error:
        RESULT['exit'] = 1
        try:
            write_json(OUT / 'receipts' / 'late-publication-failure.json',
                       {'exit': 1, 'receipt_sha256': receipt_sha, 'error_class': type(error).__name__,
                        'phase': 'final-publication', 'native_execution': False})
        except BaseException:
            pass
if time.monotonic() >= FINAL:
    RESULT['exit'] = 1
print(json.dumps({'exit': RESULT['exit'], 'receipt_sha256': receipt_sha,
                  'build_prerequisite_only': True, 'native_execution': False}, sort_keys=True))
sys.exit(RESULT['exit'])
PY
