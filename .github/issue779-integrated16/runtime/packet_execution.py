#!/usr/bin/env python3
"""Closed plan and native-dispatch gate for one integrated16 selector.

Planning is data-only.  Execution is available only after the packet review,
the staged build image, and bounded runtime/helper preparation receipts verify.
The case runner remains the original pinned runner from the selected packet.
"""
from __future__ import annotations

import argparse
import base64
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import stat
import sys
import time

SCHEMA = 'issue779-integrated16-case-plan-v1'
REVIEW_SCHEMA = 'issue779-integrated16-native-execution-review-v1'
PACKET_SHA256 = 'a6b2dadbd332f4174a9a1c81487bbc9d19324559cc092929d9af083347fbbcd8'
SOURCE = '957da1f23ff743778fa39e3e559511ccdb281345'
CAPTURE = '6d723d1d944485b785e400849db941c3e869065371e2978806abc6b3dec7f0c4'
SOURCE_COUNT = 2872
SOURCE_TREE = '51a3f946d42f4141af1cc8d297161dd362b79fd0'
ARTIFACT_METADATA_SHA256 = '2837ae66440f45a36adc5865781eb3355c934fd80cb89d2c75e6b367c5502f07'
BUILD_RUN = 38511845118
RUNNER_BUDGET_SECONDS = 900
RUNNER_CLEANUP_RESERVE_SECONDS = 5
HELPER_RESERVE_MS = (RUNNER_BUDGET_SECONDS + RUNNER_CLEANUP_RESERVE_SECONDS) * 1000
HELPER_END_SLACK_MS = 20
MAX_FILE = 4 * 1024 * 1024
MAX_RUNNER = 256 * 1024
MAX_DOTNET = 256 * 1024 * 1024
HEX = re.compile(r'[0-9a-f]{64}\Z')
SELECTORS = ('None', *(f'N{i:02d}' for i in range(4, 17)))
CASE_FOR_SELECTOR = {'None': 'N03', **{f'N{i:02d}': f'N{i:02d}' for i in range(4, 17)}}
SHARED_HELPERS = {'None', 'N04', 'N05', 'N06', 'N07', 'N08', 'N11', 'N12'}
EXPECTED_FLAGS = {
    'None': ('--build-root','--execute','--helper-build-root','--output','--reviewed-script-sha256','--workspace'),
    'N04': ('--build-root','--execute','--helper-build-root','--n04-helper-build-root','--output','--reviewed-script-sha256','--workspace'),
    'N05': ('--build-root','--execute','--helper-build-root','--output','--reviewed-script-sha256','--workspace'),
    'N06': ('--build-root','--execute','--helper-build-root','--output','--reviewed-script-sha256','--workspace'),
    'N07': ('--build-root','--execute','--helper-build-root','--n07-helper-build-root','--output','--reviewed-script-sha256','--workspace'),
    'N08': ('--build-root','--execute','--helper-build-root','--output','--reviewed-script-sha256','--workspace'),
    'N09': ('--build-root','--execute','--output','--reviewed-script-sha256','--workspace'),
    'N10': ('--build-root','--execute','--output','--reviewed-script-sha256','--workspace'),
    'N11': ('--build-root','--execute','--helper-build-root','--output','--reviewed-script-sha256','--workspace'),
    'N12': ('--build-root','--execute','--helper-build-root','--output','--reviewed-script-sha256','--workspace'),
    'N13': ('--build-root','--case','--execute','--output','--reviewed-root','--reviewed-script-sha256','--workspace'),
    'N14': ('--build-root','--case','--execute','--output','--reviewed-root','--reviewed-script-sha256','--workspace'),
    'N15': ('--build-root','--execute','--output','--reviewed-script-sha256','--workspace'),
    'N16': ('--build-root','--execute','--output','--reviewed-script-sha256','--workspace'),
}


class Rejected(ValueError):
    def __init__(self, category: str):
        super().__init__(category)
        self.category = category


def require(ok: bool, category: str) -> None:
    if not ok:
        raise Rejected(category)


def _ident(st: os.stat_result) -> tuple[int, ...]:
    return (st.st_dev, st.st_ino, st.st_mode, st.st_uid, st.st_gid,
            st.st_nlink, st.st_size, st.st_mtime_ns, st.st_ctime_ns)


def _read(path: Path, cap: int = MAX_FILE) -> bytes:
    before = path.lstat()
    require(stat.S_ISREG(before.st_mode) and before.st_nlink == 1
            and 0 < before.st_size <= cap, 'input-file-shape')
    fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC | os.O_NONBLOCK)
    try:
        require(_ident(before) == _ident(os.fstat(fd)), 'input-file-substitution')
        chunks: list[bytes] = []
        size = 0
        while True:
            block = os.read(fd, min(65536, cap + 1 - size))
            if not block:
                break
            size += len(block)
            require(size <= cap and size <= before.st_size, 'input-file-growth')
            chunks.append(block)
        require(size == before.st_size and _ident(before) == _ident(os.fstat(fd))
                == _ident(path.lstat()), 'input-file-changed')
        return b''.join(chunks)
    finally:
        os.close(fd)


def _unique(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, 'duplicate-json-key')
        result[key] = value
    return result


def _json(raw: bytes) -> dict:
    value = json.loads(raw, object_pairs_hook=_unique)
    require(type(value) is dict, 'json-object-required')
    return value


def _sha(raw: bytes) -> str:
    return hashlib.sha256(raw).hexdigest()


def _safe_rel(text: str) -> str:
    require(type(text) is str and bool(text) and not text.startswith('/')
            and '\\' not in text and all(p not in ('', '.', '..') for p in text.split('/')),
            'relative-path')
    return text


def _path_under(root: Path, rel: str) -> Path:
    rel = _safe_rel(rel)
    path = root / rel
    current = root
    for part in Path(rel).parts:
        current = current / part
        try:
            st = current.lstat()
        except FileNotFoundError:
            raise Rejected('input-path-missing') from None
        require(not stat.S_ISLNK(st.st_mode), 'input-symlink')
    return path


def _private_root(root: Path, *, must_exist: bool = True) -> Path:
    require(root.is_absolute() and root == Path(os.path.normpath(str(root))), 'root-not-absolute-normalized')
    if must_exist:
        st = root.lstat()
        require(stat.S_ISDIR(st.st_mode) and not stat.S_ISLNK(st.st_mode)
                and st.st_uid == os.geteuid() and stat.S_IMODE(st.st_mode) & 0o022 == 0,
                'root-not-private')
    return root


def _packet(packet_root: Path) -> tuple[dict, dict[str, dict]]:
    packet_root = _private_root(packet_root)
    raw = _read(packet_root / 'runtime-packet.json', MAX_FILE)
    require(_sha(raw) == PACKET_SHA256, 'packet-manifest-sha')
    manifest = _json(raw)
    require(manifest.get('schema') == 'issue779-integrated16-runtime-packet-v1'
            and manifest.get('source_commit') == SOURCE
            and manifest.get('capture_sha256') == CAPTURE
            and type(manifest.get('source_count')) is int
            and manifest['source_count'] == SOURCE_COUNT, 'packet-source-binding')
    cases = manifest.get('cases')
    require(type(cases) is dict and set(cases) == {f'N{i:02d}' for i in range(1, 17)},
            'packet-case-set')
    flat: dict[str, dict] = {}
    for case in sorted(cases):
        row = cases[case]
        files = row.get('files') if type(row) is dict else None
        require(type(files) is dict and files, 'packet-case-files')
        for name, pin in files.items():
            rel = _safe_rel(f'cases/{case}/tooling/{name}')
            require(type(pin) is dict and set(pin) == {'bytes', 'mode', 'sha256'}
                    and type(pin.get('bytes')) is int and 0 < pin['bytes'] <= MAX_FILE
                    and type(pin.get('mode')) is str
                    and re.fullmatch(r'0[0-7]{3}', pin['mode'])
                    and type(pin.get('sha256')) is str and HEX.fullmatch(pin['sha256']),
                    'packet-file-row')
            path = _path_under(packet_root, rel)
            data = _read(path, pin['bytes'])
            st = path.lstat()
            require(len(data) == pin['bytes'] and _sha(data) == pin['sha256']
                    and format(stat.S_IMODE(st.st_mode), '04o') == pin['mode'],
                    'packet-file-binding')
            flat[rel] = {'sha256': pin['sha256'], 'mode': pin['mode'], 'bytes': pin['bytes']}
    require(len(flat) == 358, 'packet-file-count')
    actual = set()
    for directory, names, files in os.walk(packet_root, followlinks=False):
        base = Path(directory)
        for name in names:
            require(not (base / name).is_symlink(), 'packet-directory-symlink')
        for name in files:
            path = base / name
            require(not path.is_symlink() and stat.S_ISREG(path.lstat().st_mode), 'packet-extra-file-shape')
            rel = path.relative_to(packet_root).as_posix()
            if rel != 'runtime-packet.json':
                actual.add(rel)
    require(actual == set(flat), 'packet-file-set')
    return manifest, flat


def _review(path: Path, expected_sha: str, flat: dict[str, dict], packet_root: Path) -> dict:
    require(type(expected_sha) is str and HEX.fullmatch(expected_sha), 'review-sha-format')
    raw = _read(path)
    require(_sha(raw) == expected_sha, 'review-sha-mismatch')
    review = _json(raw)
    require(set(review) == {'schema', 'source_commit', 'capture_sha256', 'packet_sha256',
                            'source_review_clear', 'whole_harness_review_clear',
                            'native_execution_clear', 'installation_map_sha256', 'files'},
            'review-field-set')
    require(review.get('schema') == REVIEW_SCHEMA and review.get('source_commit') == SOURCE
            and review.get('capture_sha256') == CAPTURE
            and review.get('packet_sha256') == PACKET_SHA256
            and all(review.get(k) is True for k in ('source_review_clear',
                    'whole_harness_review_clear', 'native_execution_clear')),
            'review-binding-or-gates')
    require(type(review.get('installation_map_sha256')) is str
            and HEX.fullmatch(review['installation_map_sha256']), 'installation-map-sha-format')
    files = review.get('files')
    require(type(files) is dict and len(files) >= len(flat) + 1, 'review-installation-map-incomplete')
    for name, row in files.items():
        _safe_rel(name)
        require(type(row) is dict and set(row) == {'sha256', 'mode', 'bytes'}
                and type(row.get('sha256')) is str and HEX.fullmatch(row['sha256'])
                and type(row.get('mode')) is str and re.fullmatch(r'0[0-7]{3}', row['mode'])
                and type(row.get('bytes')) is int and 0 <= row['bytes'] <= MAX_FILE,
                'review-installation-row')
    for rel, row in flat.items():
        require(files.get(f'packet/{rel}') == row, 'review-packet-file-map-mismatch')
    manifest_raw = _read(packet_root / 'runtime-packet.json', MAX_FILE)
    manifest_path_row = files.get('packet/runtime-packet.json')
    manifest_mode = format(stat.S_IMODE((packet_root / 'runtime-packet.json').lstat().st_mode), '04o')
    require(manifest_path_row == {'sha256': _sha(manifest_raw), 'mode': manifest_mode,
                                  'bytes': len(manifest_raw)}, 'review-packet-manifest-map-mismatch')
    install_map_raw = _read(packet_root.parent / 'installation-map.json', 2 * 1024 * 1024)
    require(_sha(install_map_raw) == review['installation_map_sha256'], 'installation-map-pin')
    install_map = _json(install_map_raw)
    require(set(install_map) == {'schema', 'source_commit', 'capture_sha256', 'packet_sha256', 'files'}
            and install_map.get('schema') == 'issue779-integrated16-native-installation-v1'
            and install_map.get('source_commit') == SOURCE
            and install_map.get('capture_sha256') == CAPTURE
            and install_map.get('packet_sha256') == PACKET_SHA256
            and install_map.get('files') == files, 'installation-map-binding')
    return review


def _pins(workspace: Path, native: Path) -> tuple[Path, dict[str, str]]:
    runner = _path_under(workspace, '.github/issue779-csharp-native/run-native.py')
    raw = _read(runner, MAX_RUNNER)
    import ast
    tree = ast.parse(raw.decode('utf-8'))
    pins = None
    for node in tree.body:
        if isinstance(node, ast.Assign) and any(isinstance(t, ast.Name) and t.id in ('PINS', 'REVIEWED_FILES') for t in node.targets):
            try:
                value = ast.literal_eval(node.value)
            except (ValueError, TypeError):
                continue
            if type(value) is dict and all(type(k) is str and type(v) is str for k, v in value.items()):
                pins = value
                break
    return runner, pins if pins is not None else {}


def _embedded_review_gates(runner: Path) -> dict[str, bool]:
    import ast
    tree = ast.parse(_read(runner, MAX_RUNNER).decode('utf-8'))
    gates = {}
    for node in tree.body:
        if isinstance(node, ast.Assign):
            for target in node.targets:
                if isinstance(target, ast.Name) and (target.id.endswith('_REVIEW_CLEAR')
                                                      or target.id.endswith('_PENDING')):
                    value = ast.literal_eval(node.value)
                    require(type(value) is bool, 'runner-review-gate-shape')
                    gates[target.id] = value
    return dict(sorted(gates.items()))


def _embedded_gates_clear(gates: dict[str, bool]) -> bool:
    return bool(gates) and all((value is False if name.endswith('_PENDING') else value is True)
                               for name, value in gates.items())


def _command_successful(row: dict) -> bool:
    return (type(row) is dict and type(row.get('exit')) is int and row['exit'] == 0
            and all(row.get(k) is True for k in ('waited', 'leader_exit_observed',
                    'descendants_settled', 'pipe_eof', 'deadline_ok'))
            and all(row.get(k) is False for k in ('forced_cleanup', 'overflow',
                    'read_error', 'write_error', 'error'))
            and row.get('discarded_bytes') == 0 and row.get('failure') is None)


def _runner_terminal_exit(row: dict) -> int:
    return 0 if _command_successful(row) else 1


def _runner_window(deadline_ns: int, now_ns: int) -> tuple[int, int]:
    """Keep the full runner cap and a positive settlement interval in one deadline."""
    cleanup_ns = RUNNER_CLEANUP_RESERVE_SECONDS * 1_000_000_000
    remaining_ns = deadline_ns - now_ns
    budget_ns = min(RUNNER_BUDGET_SECONDS * 1_000_000_000,
                    remaining_ns - cleanup_ns)
    require(budget_ns > 0, 'runner-deadline-reserve')
    end_ns = min(now_ns + budget_ns, deadline_ns - cleanup_ns)
    cleanup_remaining_ns = min(cleanup_ns, max(0, deadline_ns - end_ns))
    require(cleanup_remaining_ns == cleanup_ns, 'runner-cleanup-reserve-unavailable')
    return end_ns, cleanup_remaining_ns


def _runner_receipt_successful(receipt: dict) -> bool:
    if type(receipt) is not dict or receipt.get('exit') != 0:
        return False
    command = receipt.get('command')
    return (_command_successful(command)
            and receipt.get('child_exit') == command['exit'])


def _require_runner_success(receipt: dict) -> None:
    """Keep a failed/incomplete native child from becoming a successful CLI exit."""
    require(_runner_receipt_successful(receipt),
            'native-runner-unsuccessful')


def _uptime_ms() -> int:
    try:
        fd = os.open('/proc/uptime', os.O_RDONLY | os.O_CLOEXEC | os.O_NOFOLLOW | os.O_NONBLOCK)
    except FileNotFoundError:
        # Data-only planning controls can run on macOS; execute mode is Linux-only.
        return time.monotonic_ns() // 1_000_000
    try:
        raw = os.read(fd, 129)
        require(len(raw) <= 128 and os.read(fd, 1) == b'', 'uptime-cap')
    finally:
        os.close(fd)
    require(re.fullmatch(rb'[0-9]+\.[0-9]+ [0-9]+\.[0-9]+\n?', raw) is not None, 'uptime-shape')
    token = raw.split(b' ', 1)[0]
    whole, dot, frac = token.partition(b'.')
    require(bool(dot) and whole.isdigit() and frac.isdigit(), 'uptime-shape')
    return int(whole) * 1000 + int((frac + b'000')[:3])


def _hash_regular(path: Path, cap: int) -> str:
    return _sha(_read(path, cap))


def _selector(selector: str) -> str:
    require(type(selector) is str and selector in SELECTORS, 'selector-not-closed')
    return CASE_FOR_SELECTOR[selector]


def _expected(selector: str, packet_root: Path, job_root: Path, deadline_ns: int,
              sdk_path: Path | None = None) -> dict:
    remaining_ms = (deadline_ns - time.monotonic_ns()) // 1_000_000
    require(remaining_ms > (RUNNER_BUDGET_SECONDS + RUNNER_CLEANUP_RESERVE_SECONDS) * 1000,
            'runner-original-deadline-reserve-unavailable')
    case = _selector(selector)
    tooling = packet_root / 'cases' / case / 'tooling'
    native = tooling / '.github/issue779-csharp-native'
    runner, pins = _pins(tooling, native)
    gates = _embedded_review_gates(runner)
    runner_sha = _hash_regular(runner, MAX_RUNNER)
    runtime_script = _path_under(tooling, '.github/issue779-csharp-native/prepare-ubuntu-runtime.py')
    runtime_sha = _hash_regular(runtime_script, MAX_FILE)
    runtime = {
        'operation': 'native-prerequisite',
        'argv': ['/usr/bin/python3', '-B', str(runtime_script), '--execute',
                 '--reviewed-script-sha256', runtime_sha, '--runner-sha256', runner_sha],
        'cwd': str(tooling), 'timeout_seconds': 300,
        'purpose': 'case-runtime-prerequisite',
    }
    helper_rows = []
    if selector in SHARED_HELPERS or selector in ('N04', 'N07'):
        require(sdk_path is not None, 'dotnet-root-required-for-helper')
        sdk = sdk_path
        sdk_sha = _hash_regular(sdk, MAX_DOTNET)
        recipes = []
        if selector in SHARED_HELPERS:
            recipes.append(('build-helper.sh', 'issue779-n03-helper-build', native))
        if selector == 'N04':
            recipes.append(('build-n04-helper.sh', 'issue779-n04-helper-build', native / 'n04-helper-source'))
        if selector == 'N07':
            recipes.append(('build-n07-helper.sh', 'issue779-n07-helper-build', native / 'n07-helper-source'))
        # Sample uptime before remaining time, so clock/read latency reduces
        # the helper interval instead of extending the original job budget.
        now_uptime = _uptime_ms()
        remaining_ms = (deadline_ns - time.monotonic_ns()) // 1_000_000
        require(remaining_ms > HELPER_RESERVE_MS + HELPER_END_SLACK_MS, 'native-reserve-unavailable')
        end_ms = now_uptime + remaining_ms - HELPER_RESERVE_MS - HELPER_END_SLACK_MS
        timeout = (remaining_ms - HELPER_RESERVE_MS - HELPER_END_SLACK_MS) / 1000
        for name, outname, source in recipes:
            recipe = _path_under(tooling, f'.github/issue779-csharp-native/{name}')
            recipe_sha = _hash_regular(recipe, MAX_FILE)
            require(pins.get(name) == recipe_sha, 'helper-recipe-pin')
            source_rel = source.relative_to(tooling).as_posix()
            source_files = {}
            for path in sorted(source.rglob('*')):
                if path.is_symlink():
                    raise Rejected('helper-source-symlink')
                if path.is_file():
                    rel = path.relative_to(tooling).as_posix()
                    data = _read(path, MAX_FILE)
                    source_files[rel] = {'sha256': _sha(data), 'bytes': len(data),
                                         'mode': format(stat.S_IMODE(path.lstat().st_mode), '04o')}
            require(source_files, 'helper-source-empty')
            if name == 'build-helper.sh' and b'n07_helper_process.py' in _read(recipe, MAX_FILE):
                module = _path_under(tooling, '.github/issue779-csharp-native/n07_helper_process.py')
                module_sha = _hash_regular(module, MAX_FILE)
                if 'n07_helper_process.py' in pins:
                    require(pins['n07_helper_process.py'] == module_sha, 'helper-process-module-pin')
                source_files['.github/issue779-csharp-native/n07_helper_process.py'] = {
                    'sha256': module_sha, 'bytes': module.stat().st_size,
                    'mode': format(stat.S_IMODE(module.lstat().st_mode), '04o')}
            argv = ['/usr/bin/bash', '--noprofile', '--norc', str(recipe), '--execute',
                    str(source), str(sdk), sdk_sha, str(job_root / outname), str(end_ms)]
            row = {'operation': 'peer-helper' if name == 'build-helper.sh' else 'coordinator-helper',
                   'argv': argv, 'cwd': str(tooling), 'timeout_seconds': timeout,
                   'recipe': str(recipe), 'recipe_sha256': recipe_sha,
                   'source_dir': str(source), 'source_files': source_files,
                   'sdk_sha256': sdk_sha, 'output_root': str(job_root / outname),
                   'end_monotonic_ms': end_ms, 'recorded_uptime_ms': now_uptime,
                   'job_remaining_ms_at_plan': remaining_ms,
                   'deadline_reserve_ms': HELPER_RESERVE_MS + HELPER_END_SLACK_MS}
            helper_rows.append(row)
    output = job_root / 'issue779-csharp-native-run'
    runner_argv = ['/usr/bin/python3', '-B', str(runner), '--execute', '--workspace', str(tooling),
                   '--build-root', str(job_root / 'image'), '--output', str(output),
                   '--reviewed-script-sha256', runner_sha]
    if selector in SHARED_HELPERS:
        runner_argv += ['--helper-build-root', str(job_root / 'issue779-n03-helper-build')]
    if selector == 'N04':
        runner_argv += ['--n04-helper-build-root', str(job_root / 'issue779-n04-helper-build')]
    if selector == 'N07':
        runner_argv += ['--n07-helper-build-root', str(job_root / 'issue779-n07-helper-build')]
    if selector in ('N13', 'N14'):
        runner_argv += ['--case', selector, '--reviewed-root', str(job_root / 'owner-death-reviewed')]
    flags = tuple(sorted({a for a in runner_argv if a.startswith('--')}))
    require(flags == tuple(sorted(EXPECTED_FLAGS[selector])), 'runner-argv-flags')
    return {'schema': SCHEMA, 'selector': selector, 'case': case,
            'source_commit': SOURCE, 'capture_sha256': CAPTURE, 'source_count': SOURCE_COUNT,
            'packet_sha256': PACKET_SHA256, 'job_root': str(job_root),
            'job_deadline_ns': deadline_ns, 'review_sha256': None,
            'embedded_review_gates': gates,
            'stages': {'runtime': [runtime], 'helpers': helper_rows},
            'runner': {'operation': 'native-prerequisite', 'argv': runner_argv,
                       'cwd': str(tooling), 'timeout_seconds': RUNNER_BUDGET_SECONDS,
                       'expected_output_absent': str(output)},
            'execution_attempted': False, 'native_credit': False}


def derive_plan(selector: str, packet_root: Path, job_root: Path, review_path: Path,
                review_sha256: str, deadline_ns: int, *, sdk_path: Path | None = None) -> dict:
    packet_root = _private_root(Path(packet_root))
    job_root = _private_root(Path(job_root))
    manifest, flat = _packet(packet_root)
    review = _review(Path(review_path), review_sha256, flat, packet_root)
    case = _selector(selector)
    if sdk_path is None and (selector in SHARED_HELPERS or selector in ('N04', 'N07')):
        dotnet_root = os.environ.get('DOTNET_ROOT')
        require(bool(dotnet_root), 'dotnet-root-required-for-helper')
        sdk_path = Path(dotnet_root) / 'dotnet'
    if sdk_path is not None:
        sdk_path = Path(sdk_path)
        require(sdk_path.is_absolute(), 'dotnet-path-not-absolute')
    plan = _expected(selector, packet_root, job_root, deadline_ns, sdk_path)
    plan['review_sha256'] = review_sha256
    plan['installation_map_sha256'] = review['installation_map_sha256']
    return plan


def validate_plan(plan: dict, packet_root: Path, job_root: Path, review_path: Path,
                  review_sha: str, deadline_ns: int) -> None:
    """Recheck all reviewed bytes and command fields without resetting deadlines."""
    require(type(plan) is dict and set(plan) == {'schema', 'selector', 'case', 'source_commit',
            'capture_sha256', 'source_count', 'packet_sha256', 'job_root', 'job_deadline_ns',
            'review_sha256', 'installation_map_sha256', 'embedded_review_gates', 'stages', 'runner',
            'execution_attempted', 'native_credit'} and plan.get('schema') == SCHEMA, 'plan-schema')
    packet_root, job_root = Path(packet_root), Path(job_root)
    _private_root(packet_root)
    _private_root(job_root)
    manifest, flat = _packet(packet_root)
    review = _review(Path(review_path), review_sha, flat, packet_root)
    require(plan.get('review_sha256') == review_sha
            and plan.get('installation_map_sha256') == review['installation_map_sha256'], 'plan-review-pin')
    require(plan.get('source_commit') == SOURCE and plan.get('capture_sha256') == CAPTURE
            and plan.get('source_count') == SOURCE_COUNT and plan.get('packet_sha256') == PACKET_SHA256
            and plan.get('job_root') == str(job_root) and plan.get('job_deadline_ns') == deadline_ns,
            'plan-source-or-deadline-binding')
    case = _selector(plan.get('selector'))
    require(plan.get('case') == case and plan.get('execution_attempted') is False
            and plan.get('native_credit') is False, 'plan-case-or-status')
    stages = plan.get('stages')
    require(type(stages) is dict and set(stages) == {'runtime', 'helpers'}, 'plan-stage-set')
    sdk_path = None
    helper_rows = stages['helpers']
    if helper_rows:
        first = helper_rows[0]
        sdk_path = Path(first['argv'][6]) if type(first) is dict and type(first.get('argv')) is list and len(first['argv']) > 6 else None
        require(sdk_path is not None and sdk_path.is_absolute(), 'plan-sdk-path')
    expected = _expected(plan['selector'], packet_root, job_root, deadline_ns, sdk_path)
    require(plan.get('embedded_review_gates') == expected['embedded_review_gates'],
            'plan-embedded-review-gates')
    for stage in ('runtime', 'helpers'):
        got, want = stages[stage], expected['stages'][stage]
        require(type(got) is list and len(got) == len(want), f'plan-{stage}-count')
        for actual, fixed in zip(got, want):
            require(type(actual) is dict, f'plan-{stage}-row')
            if stage == 'runtime':
                require(set(actual) == {'operation', 'argv', 'cwd', 'timeout_seconds', 'purpose'},
                        'plan-runtime-field-set')
                for key in ('operation', 'argv', 'cwd', 'purpose'):
                    require(actual.get(key) == fixed[key], f'plan-runtime-{key}')
                require(actual.get('timeout_seconds') == fixed['timeout_seconds'], 'plan-runtime-timeout')
            else:
                require(set(actual) == {'operation', 'argv', 'cwd', 'timeout_seconds',
                        'recipe', 'recipe_sha256', 'source_dir', 'source_files', 'sdk_sha256',
                        'output_root', 'end_monotonic_ms', 'recorded_uptime_ms',
                        'job_remaining_ms_at_plan', 'deadline_reserve_ms'}, 'plan-helper-field-set')
                for key in ('operation', 'cwd', 'recipe', 'recipe_sha256', 'source_dir',
                            'source_files', 'sdk_sha256', 'output_root', 'deadline_reserve_ms'):
                    require(actual.get(key) == fixed[key], f'plan-helper-{key}')
                recorded_end = actual.get('end_monotonic_ms')
                require(type(recorded_end) is int, 'plan-helper-end-shape')
                require(type(actual.get('argv')) is list and len(actual['argv']) == len(fixed['argv'])
                        and actual['argv'][:-1] == fixed['argv'][:-1]
                        and actual['argv'][-1] == str(recorded_end),
                        'plan-helper-argv-end')
                end_ms = recorded_end
                recorded = actual.get('recorded_uptime_ms')
                planned_remaining = actual.get('job_remaining_ms_at_plan')
                # Use the opposite sampling order for the later upper bound.
                # Linux /proc/uptime rounds to centiseconds; the original
                # twenty-millisecond subtraction covers that quantization.
                remaining_ms = (deadline_ns - time.monotonic_ns()) // 1_000_000
                now_ms = _uptime_ms()
                require(type(end_ms) is int and type(recorded) is int and type(planned_remaining) is int
                        and planned_remaining > HELPER_RESERVE_MS + HELPER_END_SLACK_MS
                        and end_ms == recorded + planned_remaining - HELPER_RESERVE_MS - HELPER_END_SLACK_MS
                        and recorded <= now_ms < end_ms
                        and end_ms <= now_ms + remaining_ms - HELPER_RESERVE_MS,
                        'plan-helper-original-end')
                require(type(actual.get('timeout_seconds')) in (int, float)
                        and actual['timeout_seconds'] == (planned_remaining - HELPER_RESERVE_MS - HELPER_END_SLACK_MS) / 1000,
                        'plan-helper-timeout')
    runner = plan.get('runner')
    fixed_runner = expected['runner']
    require(type(runner) is dict and all(runner.get(k) == fixed_runner[k]
            for k in ('operation', 'argv', 'cwd', 'timeout_seconds', 'expected_output_absent')),
            'plan-runner-binding')


def _artifact(selector: str) -> dict:
    metadata_path = Path(__file__).resolve().parent / 'image-artifact-metadata.json'
    raw = _read(metadata_path, 1024 * 1024)
    require(_sha(raw) == ARTIFACT_METADATA_SHA256, 'image-metadata-pin')
    meta = _json(raw)
    require(meta.get('run_id') == BUILD_RUN and meta.get('total_count') == 14, 'image-metadata-run')
    name = f'issue779-integrated-image-{selector}-{BUILD_RUN}-1'
    matches = [x for x in meta.get('artifacts', []) if type(x) is dict and x.get('name') == name]
    require(len(matches) == 1, 'image-artifact-selection')
    row = matches[0]
    require(type(row.get('id')) is int and type(row.get('size_in_bytes')) is int
            and row.get('digest', '').startswith('sha256:') and HEX.fullmatch(row['digest'][7:]),
            'image-artifact-row')
    return row


def verify_staged_image(selector: str, job_root: Path) -> dict:
    row = _artifact(selector)
    image = job_root / 'image'
    image_st = image.lstat()
    require(stat.S_ISDIR(image_st.st_mode) and not stat.S_ISLNK(image_st.st_mode), 'image-root-shape')
    marker = _json(_read(_path_under(job_root, 'image/stage-image-receipt.json')))
    require(marker.get('schema') == 'issue779-integrated16-staged-image-v1'
            and marker.get('status') == 'verified-and-staged'
            and marker.get('selector') == selector and marker.get('artifact_id') == row['id']
            and marker.get('artifact_sha256') == row['digest'][7:]
            and marker.get('source_commit') == SOURCE and marker.get('source_tree') == SOURCE_TREE
            and marker.get('source_count') == SOURCE_COUNT and marker.get('capture_sha256') == CAPTURE
            and marker.get('native_execution') is False and marker.get('native_credit') is False,
            'staged-image-binding')
    download = _json(_read(job_root / 'image-download.json'))
    measured = download.get('download')
    require(download.get('schema') == 'issue779-integrated-image-download-v1'
            and download.get('build_run') == BUILD_RUN and download.get('selector') == selector
            and download.get('artifact_id') == row['id'] and type(measured) is dict
            and measured.get('bytes') == row['size_in_bytes']
            and measured.get('sha256') == row['digest'][7:]
            and download.get('native_execution') is False and download.get('native_credit') is False,
            'image-download-binding')
    build_raw = _read(_path_under(job_root, 'image/receipts/build-receipt.json'))
    image_raw = _read(_path_under(job_root, 'image/receipts/image-receipt.json'))
    require(marker.get('build_receipt_sha256') == _sha(build_raw)
            and marker.get('image_receipt_sha256') == _sha(image_raw), 'staged-image-receipt-hash')
    return marker


def _successful_receipt(root: Path, stage: str, plan: dict) -> None:
    receipt = _json(_read(_path_under(root, f'preparation-{stage}.json'), 256 * 1024))
    rows = receipt.get('commands')
    expected = plan['stages'][stage]
    require(receipt.get('schema') == 'issue779-integrated16-preparation-stage-v1'
            and receipt.get('stage') == stage and receipt.get('selector') == plan['selector']
            and receipt.get('exit') == 0 and receipt.get('native_execution') is False
            and receipt.get('native_credit') is False and type(rows) is list and len(rows) == len(expected),
            f'{stage}-preparation-receipt')
    fields = {'operation','exit','waited','leader_exit_observed','descendants_settled',
              'forced_cleanup','log','log_bytes','overflow','discarded_bytes','read_error',
              'write_error','pipe_eof','deadline_ok','failure','error'}
    for index, (row, command) in enumerate(zip(rows, expected)):
        require(type(row) is dict and row.get('operation') == command['operation']
                and set(row) == fields and type(row.get('exit')) is int
                and row['exit'] == 0 and row.get('failure') is None
                and all(row.get(k) is True for k in ('waited','leader_exit_observed','descendants_settled','pipe_eof','deadline_ok'))
                and all(row.get(k) is False for k in ('forced_cleanup','overflow','read_error','write_error','error'))
                and row.get('discarded_bytes') == 0 and row.get('log') == f'preparation-{stage}-{index}.log'
                and type(row.get('log_bytes')) is int and 0 <= row['log_bytes'] <= 8 * 1024 * 1024,
                f'{stage}-child-not-settled')


def stage_owner_death(plan: dict, packet_root: Path, job_root: Path) -> Path:
    """Materialize the runner's fixed N13/N14 reviewed input directory."""
    require(plan.get('selector') in ('N13', 'N14'), 'owner-death-selector')
    tooling = packet_root / 'cases' / plan['case'] / 'tooling'
    native = tooling / '.github/issue779-csharp-native'
    target = job_root / 'owner-death-reviewed'
    target.mkdir(mode=0o700)
    for source in sorted(native.iterdir()):
        if (source.name not in ('source-capture.json', 'preparation.json')
                and source.is_file()
                and (source.suffix in ('.sh', '.py', '.json') or source.name == 'README.md')):
            raw = _read(source, MAX_FILE)
            fd = os.open(target / source.name, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC, 0o600)
            with os.fdopen(fd, 'wb') as out:
                out.write(raw); out.flush(); os.fsync(out.fileno())
    capture = _read(tooling / '.github/issue779-csharp-build/capture-receipt.json')
    require(_sha(capture) == CAPTURE, 'owner-death-capture-pin')
    _write_exclusive(target / 'source-capture.json', capture)
    review = _json(_read(target / 'source-review.json'))
    encoded, expected = review.get('fixture_preparation_base64'), review.get('fixture_preparation_sha256')
    require(type(encoded) is str and type(expected) is str and HEX.fullmatch(expected), 'owner-death-preparation-fields')
    try:
        prep = base64.b64decode(encoded, validate=True)
    except (ValueError, TypeError):
        raise Rejected('owner-death-preparation-base64') from None
    require(prep and _sha(prep) == expected, 'owner-death-preparation-sha')
    _write_exclusive(target / 'preparation.json', prep)
    return target


def _write_exclusive(path: Path, raw: bytes) -> None:
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC, 0o600)
    with os.fdopen(fd, 'wb') as out:
        out.write(raw); out.flush(); os.fsync(out.fileno())


def execute_runner(plan: dict, packet_root: Path, job_root: Path, review_path: Path,
                   review_sha: str, deadline_ns: int) -> dict:
    validate_plan(plan, packet_root, job_root, review_path, review_sha, deadline_ns)
    require(_embedded_gates_clear(plan['embedded_review_gates']), 'runner-internal-review-gates-pending')
    require(sys.platform == 'linux' and os.uname().machine == 'x86_64', 'native-host-not-supported')
    _successful_receipt(job_root, 'runtime', plan)
    _successful_receipt(job_root, 'helpers', plan)
    marker = verify_staged_image(plan['selector'], job_root)
    output = job_root / 'issue779-csharp-native-run'
    require(not output.exists() and not output.is_symlink(), 'runner-output-reused')
    reviewed = stage_owner_death(plan, packet_root, job_root) if plan['selector'] in ('N13','N14') else None
    # The original fixture runner retains its own fixed sudo calls for root
    # operations. Its entrypoint stays unprivileged for owner and group checks.
    module_path = Path(__file__).resolve().parent / 'preparation_process.py'
    require(_hash_regular(module_path, MAX_FILE) == '2a35612025354f11e98105e62dbaa0d1c08ce3beafac0ed962ac07705a9f8691',
            'bounded-process-module-pin')
    spec = importlib.util.spec_from_file_location('_issue779_preparation_process', module_path)
    require(spec is not None and spec.loader is not None, 'bounded-process-import')
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    end_ns, cleanup_ns = _runner_window(deadline_ns, time.monotonic_ns())
    end = end_ns / 1_000_000_000
    env = os.environ.copy()
    env['RUNNER_TEMP'] = str(job_root)
    env['JOB_DEADLINE_MONOTONIC_NS'] = str(deadline_ns)
    argv = list(plan['runner']['argv'])
    # No sudo wrapper: the case's own ROOT_PREFIX applies privilege only to
    # the fixed root-owned fixture operations.
    if reviewed is not None:
        argv[-1] = str(reviewed)
    receipt = {'schema':'issue779-integrated16-native-runner-receipt-v1',
               'selector':plan['selector'], 'case':plan['case'], 'source_commit':SOURCE,
               'capture_sha256':CAPTURE, 'packet_sha256':PACKET_SHA256,
               'staged_image_marker_sha256':_sha(_read(job_root/'image'/'stage-image-receipt.json')),
               'runner_argv':argv, 'native_attempted':True, 'native_credit':False, 'exit':1}
    log_path = job_root / 'native-runner.log'
    try:
        public, _private = module.run_command(
            'native-prerequisite', argv, cwd=Path(plan['runner']['cwd']), log_path=log_path,
            env=env, check=lambda: (_ for _ in ()).throw(TimeoutError('original-job-deadline'))
            if time.monotonic_ns() >= end_ns else None,
            cleanup_remaining=lambda: max(0.0, min(cleanup_ns / 1_000_000_000,
                                                    (deadline_ns - time.monotonic_ns()) / 1_000_000_000)))
        receipt['command'] = public
        receipt['child_exit'] = public.get('exit')
        receipt['exit'] = _runner_terminal_exit(public)
        receipt['native_attempted'] = bool(public.get('waited') or public.get('leader_exit_observed'))
        receipt['terminal'] = 'runner-successfully-settled' if receipt['exit'] == 0 else 'runner-failed-or-incomplete'
        _write_exclusive(job_root / 'native-runner-receipt.json',
                         (json.dumps(receipt, sort_keys=True, indent=2)+'\n').encode())
        return receipt
    except BaseException as exc:
        receipt['failure'] = getattr(exc, 'category', type(exc).__name__)
        receipt['terminal'] = 'runner-failed-or-incomplete'
        _write_exclusive(job_root / 'native-runner-receipt.json',
                         (json.dumps(receipt, sort_keys=True, indent=2)+'\n').encode())
        raise


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--selector', choices=SELECTORS, required=True)
    parser.add_argument('--packet-root', type=Path, required=True)
    parser.add_argument('--job-root', type=Path, required=True)
    parser.add_argument('--review', type=Path, required=True)
    parser.add_argument('--review-sha256', required=True)
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument('--plan', action='store_true')
    mode.add_argument('--execute', action='store_true')
    parser.add_argument('--plan-file', type=Path)
    args = parser.parse_args()
    deadline = int(os.environ['JOB_DEADLINE_MONOTONIC_NS'])
    if args.plan:
        sdk = Path(os.environ['DOTNET_ROOT']) / 'dotnet' if os.environ.get('DOTNET_ROOT') else None
        plan = derive_plan(args.selector, args.packet_root, args.job_root, args.review,
                           args.review_sha256, deadline, sdk_path=sdk)
        raw = (json.dumps(plan, sort_keys=True, indent=2)+'\n').encode()
        if args.plan_file:
            _write_exclusive(args.plan_file, raw)
        else:
            sys.stdout.buffer.write(raw)
        return
    require(args.plan_file is not None and args.plan_file == args.job_root / 'case-plan.json',
            'execute-requires-frozen-plan-file')
    frozen = _json(_read(args.plan_file, 1024 * 1024))
    require(frozen.get('selector') == args.selector, 'execute-selector-plan-mismatch')
    validate_plan(frozen, args.packet_root, args.job_root, args.review, args.review_sha256, deadline)
    result = execute_runner(frozen, args.packet_root, args.job_root, args.review,
                            args.review_sha256, deadline)
    _require_runner_success(result)


if __name__ == '__main__':
    try:
        main()
    except (Rejected, OSError, ValueError, KeyError, TypeError) as exc:
        category = getattr(exc, 'category', type(exc).__name__)
        print(f'PACKET_EXECUTION_REJECTED:{category}', file=sys.stderr)
        raise SystemExit(1)
