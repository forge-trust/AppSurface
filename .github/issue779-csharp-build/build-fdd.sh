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
import shutil
import signal
import stat
import subprocess
import sys
import tarfile
import time
import xml.etree.ElementTree as ET

HEAD = '5b5a2af696741203b1cb5ce200182452b5f1fcb7'
PARENT = '6a766e3471b8a6ea56a15e3e972bf8840289049b'
HARNESS_PARENT = '03b6c18a71601875ba2b361e76ec0406f5c933f9'
TREE = '24f5d5e20b6ffed95645287712a3d81570aa8985'
CAPTURE_SHA = '8bc8f5a31f3952f0858212a5203572b377f58264dbd0c3d1b066739b05bb791b'
CAPTURE_PROJECTION_SHA = 'ec16ecf615fc176bb38d3c7329af2d4889c760f9191ac9525038e9155715d0b6'
UBUNTU_PREREQUISITE_SHA = '59c75ef0c883975b117116237c724d115306d3377ace393f0f012de3bb19db07'
NATIVE_RUNNER_SHA = 'ae76b4ac6d8745fb761f3e5b12ca3868f0497130240ab91bd80d9bb132b74f21'
SDK = '10.0.401'
COUNT = 2827
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
RESULT = {'schema': 'issue779-csharp-fdd-build-v5', 'exit': 1,
          'build_prerequisite_only': True, 'native_execution': False,
          'checkpoint_pass': False, 'os_audit': None, 'source_commit': HEAD,
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

def group_absent(pid):
    try:
        os.killpg(pid, 0)
        return False
    except ProcessLookupError:
        return True

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
    handles = []
    process = None
    failure = None
    try:
        for path in logs:
            handles.append(path.open('xb', buffering=0))
        check()
        command_start = time.monotonic()
        process = subprocess.Popen(argv, cwd=cwd, stdin=subprocess.DEVNULL,
                                   stdout=handles[0], stderr=handles[1], start_new_session=True,
                                   env=ENV, umask=child_umask)
        record['pid'] = process.pid
        # Recompute AFTER Popen; no pre-spawn allowance can extend the deadline.
        command_end = min(WORK, command_start + 180)
        if time.monotonic() >= command_end:
            record['timed_out'] = True
            raise TimeoutError('post-spawn-deadline')
        while True:
            sizes = [path.stat().st_size for path in logs]
            require(all(size <= LOG_CAP for size in sizes) and LOG_BYTES + sum(sizes) <= ALL_LOG_CAP, 'log-bound')
            if time.monotonic() >= command_end:
                record['timed_out'] = True
                raise TimeoutError('command-deadline')
            if process.poll() is not None:
                record['exit'] = process.wait()
                record['waited'] = True
                # Leader exit is not descendant completion. Join the same real
                # process group within the ORIGINAL command/log bounds; never
                # reset its allowance or accept forced cleanup as success.
                if group_absent(process.pid):
                    break
            time.sleep(min(.02, max(0, command_end - time.monotonic())))
    except BaseException as error:
        failure = error
        record['failure'] = type(error).__name__
    finally:
        if process is not None:
            if process.poll() is None or not group_absent(process.pid):
                record['forced_cleanup'] = True
                try:
                    os.killpg(process.pid, signal.SIGKILL)
                except ProcessLookupError:
                    pass
                except OSError as error:
                    failure = failure or error
            try:
                process.wait(timeout=max(.001, FINAL - time.monotonic()))
                record['exit'] = process.returncode
                record['waited'] = True
                while not group_absent(process.pid) and time.monotonic() < FINAL:
                    time.sleep(.01)
                record['group_absent'] = group_absent(process.pid)
                if not record['group_absent']:
                    failure = failure or RuntimeError('group-absence-unproved')
            except BaseException as error:
                failure = failure or error
        for handle in handles:
            try:
                handle.close()
            except OSError as error:
                failure = failure or error
        record['logs'] = []
        for path in logs:
            if path.exists():
                try:
                    facts = regular(path, LOG_CAP, final=True)
                    record['logs'].append({'name': path.name, 'bytes': facts['bytes'], 'sha256': facts['sha256']})
                    LOG_BYTES += facts['bytes']
                except BaseException as error:
                    record['logs'].append({'name': path.name, 'hash_error': type(error).__name__})
                    failure = failure or error
        # Final bytes may arrive between the last poll and group absence.
        # Reconcile the aggregate after final per-file hashing, preserving the
        # first failure and every already-completed cleanup operation.
        if LOG_BYTES > ALL_LOG_CAP:
            failure = failure or BuildFailure('aggregate-log-bound')
        if time.monotonic() >= WORK:
            record['late_completion'] = True
            failure = failure or TimeoutError('late-command-completion')
        record['failure'] = record['failure'] or (type(failure).__name__ if failure else None)
        write_json(OUT / 'receipts' / f'command-{record["ordinal"]:02d}.json', record)
    # Exclusive record persistence is itself part of the original work deadline.
    check()
    if failure is not None:
        raise failure
    require(record['exit'] == 0 and record['waited'] and record['group_absent'] and not record['forced_cleanup'], 'command-failed')
    if read_stdout:
        return regular(logs[0], LOG_CAP, collect=True)['data']
    return b''

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

def main():
    global PHASE, ENV
    require(sys.platform == 'linux' and os.uname().machine == 'x86_64' and os.geteuid() != 0, 'unprivileged-linux-x64')
    require(all(p.is_absolute() and str(p) == str(p.resolve(strict=False)) for p in (HARN, OUT, CAPTURE)), 'canonical-input-path')
    require(not OUT.exists() and not OUT.is_symlink(), 'fresh-output')
    capture_fact = regular(CAPTURE, 4 * 1024 * 1024, collect=True)
    capture = decode(capture_fact['data'])
    fields = ('validation_commit', 'previous_validation_commit', 'primary_head', 'source_count', 'exit', 'source')
    projection = {key: capture[key] for key in fields}
    projection_sha = sha(json.dumps(projection, sort_keys=True, separators=(',', ':')).encode())
    require(projection_sha == CAPTURE_PROJECTION_SHA and
            (capture_fact['sha256'] == CAPTURE_SHA or set(capture) == set(fields)), 'capture-pin')
    RESULT['capture_input_sha256'] = capture_fact['sha256']
    RESULT['capture_projection_sha256'] = projection_sha
    require(capture['exit'] == 0 and capture['validation_commit'] == HEAD and capture['source_count'] == COUNT, 'capture-result')
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
    require(run(['git', 'show', '-s', '--format=%P', retry_parent], HARN).strip().decode() == HEAD, 'published-harness-source-parent-topology')
    installation_delta = {'.github/issue779-csharp-build/build-fdd.sh': 'M', '.github/issue779-csharp-native/README.md': 'M', '.github/issue779-csharp-native/acquire-inbound.sh': 'M', '.github/issue779-csharp-native/checkpoint-n01-n02-v5.sh': 'M', '.github/issue779-csharp-native/prepare-root-inputs-v2.sh': 'M', '.github/issue779-csharp-native/prepare-ubuntu-runtime.py': 'M', '.github/issue779-csharp-native/run-native.py': 'M', '.github/issue779-csharp-native/source-review.json': 'M', '.github/issue779-csharp-native/test_actual_batch_parser.py': 'M', '.github/workflows/issue779-csharp-linux-native.yml': 'M'}
    installation_status = dict((name, status) for status, name in
                              (row.split('\t') for row in run(['git', 'diff', '--name-status', HARNESS_PARENT, 'HEAD'], HARN).decode().splitlines()))
    require(installation_status == installation_delta, 'exact-retry-installation-delta')
    RESULT['harness_retry_parent'] = retry_parent
    RESULT['harness_retry_parent_parent'] = HEAD
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
    run([str(selected_dotnet), 'restore', project, '--locked-mode',
         '-p:ContinuousIntegrationBuild=true', '-p:SelfContained=false', '-p:UseAppHost=false'], clone)
    published = OUT / 'published'
    run([str(selected_dotnet), 'publish', project, '--no-restore', '-c', 'Release',
         '-p:ContinuousIntegrationBuild=true', '-p:SelfContained=false', '-p:UseAppHost=false',
         '-p:UseSharedCompilation=false', '-nr:false', '-o', str(published)], clone)
    # Linux library regression through the same owned raw-handle wrapper; no root factory or admission.
    require(sys.platform == 'linux' and os.getuid() != 0 and os.geteuid() != 0, 'pipe-regression-platform-and-user')
    pipe_project = str(clone / 'Evidence/ForgeTrust.AppSurface.Evidence.Supervision.Tests/ForgeTrust.AppSurface.Evidence.Supervision.Tests.csproj')
    pipe_results = OUT / 'receipts' / 'pipe-tests'
    run([str(selected_dotnet), 'restore', pipe_project, '--locked-mode', '-p:UseSharedCompilation=false'], clone)
    pipe_method = 'OwnedRawPipeReadWrappingUsesHandleModeAndJoinsBothEofs'
    run([str(selected_dotnet), 'test', pipe_project, '--no-restore', '-p:UseSharedCompilation=false',
         '--filter', 'FullyQualifiedName~' + pipe_method, '--logger', 'trx;LogFileName=pipe-handle.trx',
         '--results-directory', str(pipe_results)], clone)
    pipe_fact = regular(pipe_results / 'pipe-handle.trx', 256 * 1024, collect=True)
    require(b'<!DOCTYPE' not in pipe_fact['data'] and b'<!ENTITY' not in pipe_fact['data'], 'pipe-trx-declaration')
    pipe_doc = ET.fromstring(pipe_fact['data'])
    pipe_ns = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
    pipe_counters = pipe_doc.findall('.//t:Counters', pipe_ns)
    pipe_definitions = pipe_doc.findall('.//t:UnitTest', pipe_ns)
    pipe_outcomes = pipe_doc.findall('.//t:UnitTestResult', pipe_ns)
    require(len(pipe_counters) == len(pipe_definitions) == len(pipe_outcomes) == 1, 'pipe-trx-count')
    counters = pipe_counters[0].attrib
    require(all(counters.get(k) == '1' for k in ('total', 'executed', 'passed')) and
            all(counters.get(k) == '0' for k in ('failed', 'error', 'notExecuted', 'timeout', 'aborted')), 'pipe-trx-outcome')
    pipe_id = pipe_definitions[0].get('id')
    require(isinstance(pipe_id, str) and re.fullmatch(r'[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}', pipe_id) is not None, 'pipe-trx-id')
    method = pipe_definitions[0].find('t:TestMethod', pipe_ns)
    require(method is not None and method.get('className') == 'ForgeTrust.AppSurface.Evidence.Supervision.Tests.LinuxOutputPipesTests'
            and method.get('name') == pipe_method and pipe_outcomes[0].get('outcome') == 'Passed'
            and pipe_outcomes[0].get('testId') == pipe_id, 'pipe-trx-method')
    RESULT['linux_pipe_regression'] = {'method': pipe_method, 'counters': counters, 'trx_sha256': pipe_fact['sha256'],
                                       'unprivileged_library_behavior_only': True, 'root_factory_exercised': False,
                                       'native_acceptance': False, 'runtime_basis': 'selected SDK dotnet host'}
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
