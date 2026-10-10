"""Private CI prerequisite only: install the fixed Ubuntu runtime using the existing bounded root runner."""
import argparse
import hashlib
import types
import json
import os
from pathlib import Path
import stat
import time

VERSION = '10.0.12-0ubuntu1~24.04.1'
PACKAGES = ('dotnet-host-10.0', 'dotnet-hostfxr-10.0', 'dotnet-runtime-10.0', 'aspnetcore-runtime-10.0')
ROOT = Path('/usr/lib/dotnet')
RUNNER_SHA = '977d615c1084b7c11c63f4b984415779f97a714f679bb67b5388b2f90f91e8fc'


def package_rows(data):
    """Reject any missing, extra, duplicate, non-Ubuntu-version or non-amd64 installed package."""
    if not isinstance(data, bytes) or not 0 < len(data) <= 4096:
        raise ValueError('package-data-bound')
    result = {}
    for line in data.decode('ascii').splitlines():
        fields = line.split('\t')
        if (len(fields) != 4 or fields[0] not in PACKAGES or fields[0] in result
                or fields[1:] != ['amd64', VERSION, 'ii ']):
            raise ValueError('package-installed-shape')
        result[fields[0]] = {'architecture': fields[1], 'version': fields[2], 'status': fields[3]}
    if set(result) != set(PACKAGES):
        raise ValueError('package-installed-set')
    return result


def host_candidates(data):
    """Package file names are data. Only a single canonical physical host under Ubuntu's root is eligible."""
    if not isinstance(data, bytes) or not 0 < len(data) <= 65536:
        raise ValueError('host-file-list-bound')
    result = []
    for line in data.decode('ascii').splitlines():
        path = Path(line)
        if path.name != 'dotnet' or not line.startswith(str(ROOT) + '/'):
            continue
        if any(p in ('', '.', '..') for p in line[1:].split('/')) or str(path) != line:
            raise ValueError('host-file-name')
        result.append(line)
    if len(result) != 1:
        raise ValueError('host-file-count')
    return result[0]



def publish_result(module, output, result, deadline):
    """Late stdout completion is failure; a success receipt cannot authorize a later build after invalidation."""
    module.save(output / 'receipt.json', result, deadline - 5)
    try:
        module.require(time.monotonic() < deadline - 5, 'late-runtime-prerequisite')
        print('UBUNTU_RUNTIME_TERMINAL:' + str(result['exit']), flush=True)
        module.require(time.monotonic() < deadline - 5, 'late-runtime-terminal-output')
    except BaseException:
        if time.monotonic() < deadline:
            try:
                module.save(output / 'late-publication-failure.json',
                            {'exit': 1, 'category': 'late-or-failed-runtime-terminal'}, deadline)
            except BaseException:
                pass
        raise
    return result['exit']



def load_reviewed_runner(path, expected_sha256, deadline):
    """Retain and execute the exact digest-checked runner bytes; never reopen its path."""
    if (type(expected_sha256) is not str or len(expected_sha256) != 64
            or any(c not in '0123456789abcdef' for c in expected_sha256)
            or time.monotonic() >= deadline):
        raise ValueError('reviewed-source-pin')
    before = path.lstat()
    if not stat.S_ISREG(before.st_mode) or before.st_nlink != 1 or not 0 < before.st_size <= 131072:
        raise ValueError('reviewed-source-pin')
    def identity(value):
        return (value.st_dev,value.st_ino,value.st_mode,value.st_uid,value.st_gid,value.st_nlink,
                value.st_size,value.st_mtime_ns,value.st_ctime_ns)
    fd = os.open(path, os.O_RDONLY|os.O_NOFOLLOW|os.O_NONBLOCK|os.O_CLOEXEC)
    try:
        if identity(before) != identity(os.fstat(fd)):
            raise ValueError('reviewed-source-pin')
        parts, size = [], 0
        while True:
            if time.monotonic() >= deadline:
                raise ValueError('reviewed-source-pin')
            part = os.read(fd, min(65536,131073-size))
            if not part:
                break
            parts.append(part); size += len(part)
            if size > 131072 or size > before.st_size:
                raise ValueError('reviewed-source-pin')
        if (size != before.st_size or identity(before) != identity(os.fstat(fd))
                or identity(before) != identity(path.lstat())):
            raise ValueError('reviewed-source-pin')
        data = b''.join(parts)
    finally:
        os.close(fd)
    if hashlib.sha256(data).hexdigest() != expected_sha256 or time.monotonic() >= deadline:
        raise ValueError('reviewed-source-pin')
    module = types.ModuleType('reviewed_native_runner')
    module.__file__ = str(path)
    exec(compile(data, '<issue779-reviewed-native-runner>', 'exec', dont_inherit=True), module.__dict__)
    if time.monotonic() >= deadline:
        raise ValueError('reviewed-source-pin')
    return module


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--execute', action='store_true')
    parser.add_argument('--reviewed-script-sha256', required=True)
    parser.add_argument('--runner-sha256', required=True)
    args = parser.parse_args()
    here = Path(__file__)
    start = time.monotonic()
    job_end = int(os.environ['JOB_DEADLINE_MONOTONIC_NS']) / 1_000_000_000
    end = min(start + 300, job_end)
    if (not args.execute or args.runner_sha256 != RUNNER_SHA
            or hashlib.sha256(here.read_bytes()).hexdigest() != args.reviewed_script_sha256):
        raise ValueError('reviewed-source-pin')
    module = load_reviewed_runner(here.with_name('run-native.py'), args.runner_sha256, end)
    module.require(end - start > 295, 'runtime-prerequisite-budget')
    output = Path(os.environ['RUNNER_TEMP']) / 'issue779-csharp-ubuntu-runtime'
    module.require(not os.path.lexists(output), 'fresh-runtime-prerequisite')
    output.mkdir(mode=0o700)
    runner = module.Runner(output, end)
    result = {'schema': 'issue779-ubuntu-runtime-prerequisite-v1', 'exit': 1,
              'commands': runner.records, 'package_version': VERSION, 'native_csharp_dispatched': False,
              'prerequisite_script_sha256': args.reviewed_script_sha256, 'runner_sha256': args.runner_sha256}
    error = None
    try:
        runner.phase = 'ubuntu-signed-package-index'
        runner.run(module.ROOT_PREFIX + ['-c',
            'set -euo pipefail; exec /usr/bin/apt-get -o Acquire::Retries=0 '
            '-o Acquire::http::Timeout=15 -o Acquire::https::Timeout=15 '
            '-o Acquire::AllowInsecureRepositories=false -o Acquire::AllowDowngradeToInsecureRepositories=false update'],
            min(end - 5, time.monotonic() + 90))
        runner.phase = 'ubuntu-runtime-candidate-metadata'
        runner.run(['/usr/bin/apt-cache', 'policy', *PACKAGES], min(end - 5, time.monotonic() + 15))
        runner.phase = 'fixed-ubuntu-runtime-install'
        install = 'set -euo pipefail; export DEBIAN_FRONTEND=noninteractive; exec /usr/bin/apt-get '
        install += '-y --no-install-recommends -o DPkg::Lock::Timeout=5 -o Acquire::Retries=0 '
        install += '-o Acquire::http::Timeout=15 -o Acquire::https::Timeout=15 '
        install += '-o APT::Get::AllowUnauthenticated=false -o Acquire::AllowInsecureRepositories=false '
        install += '-o Acquire::AllowDowngradeToInsecureRepositories=false install '
        install += ' '.join(p + '=' + VERSION for p in PACKAGES)
        runner.run(module.ROOT_PREFIX + ['-c', install], min(end - 5, time.monotonic() + 165))
        runner.phase = 'installed-package-data'
        _, data = runner.run(['/usr/bin/dpkg-query', '-W',
            '-f=${Package}\t${Architecture}\t${Version}\t${db:Status-Abbrev}\n', *PACKAGES], min(end - 5, time.monotonic() + 10))
        result['packages'] = package_rows(data)
        _, data = runner.run(['/usr/bin/dpkg-query', '-L', PACKAGES[0]], min(end - 5, time.monotonic() + 10))
        host = Path(host_candidates(data))
        module.require(host.resolve(strict=True) == host, 'ubuntu-host-alias')
        for path in [Path('/'), *reversed(list(host.parents)[:-1]), host]:
            row = path.lstat()
            module.require(row.st_uid == 0 and row.st_gid == 0 and not row.st_mode & 0o022
                           and not stat.S_ISLNK(row.st_mode), 'ubuntu-host-owner-or-ancestor')
        row = host.lstat()
        module.require(stat.S_ISREG(row.st_mode) and row.st_nlink == 1, 'ubuntu-host-shape')
        data = module.read(host, 4 * 1024 * 1024, end - 5)
        module.require(data[:7] == b'\x7fELF\x02\x01\x01', 'ubuntu-host-elf')
        result.update(exit=0, runtime_host=str(host), runtime_root=str(host.parent),
                      runtime_host_sha256=module.sha(data))
    except BaseException as caught:
        error = caught
        result['failure'] = {'category': getattr(caught, 'category', 'ubuntu-runtime-prerequisite'),
                             'error_class': type(caught).__name__}
    result['elapsed_seconds'] = time.monotonic() - start
    return publish_result(module, output, result, end)


if __name__ == '__main__':
    raise SystemExit(main())
