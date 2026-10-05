"""Build the isolated tiny probe against the SHA-pinned public Coverlet tasks."""
import hashlib
import io
import json
import os
import signal
import stat
import subprocess
import sys
import time
import urllib.request
import zipfile
from pathlib import Path

root = Path(sys.argv[1]).resolve(strict=True)
out = Path(sys.argv[2]).resolve()
out.mkdir(mode=0o700)
started = time.monotonic()
deadline = started + 180
records = []
package_sha = 'a7d412ffc03d14f4d884788009f569c7df00346ac5662a3a93ccaa855656bc77'
url = 'https://api.nuget.org/v3-flatcontainer/coverlet.msbuild/10.0.1/coverlet.msbuild.10.0.1.nupkg'
request_started = time.monotonic()
with urllib.request.urlopen(url, timeout=30) as response:
    chunks = bytearray()
    while True:
        assert time.monotonic() < deadline and time.monotonic() - request_started < 60
        block = response.read(65536)
        if not block:
            break
        chunks.extend(block)
        assert len(chunks) <= 8 * 1024 * 1024
assert hashlib.sha256(chunks).hexdigest() == package_sha
package = out / 'package'
package.mkdir(mode=0o700)
selected = []
with zipfile.ZipFile(io.BytesIO(chunks)) as archive:
    names = archive.namelist()
    assert len(names) == len(set(names)) and len(names) <= 256
    for item in archive.infolist():
        if not item.filename.startswith('tasks/net10.0/') or item.is_dir():
            continue
        path = Path(item.filename)
        assert 3 <= len(path.parts) <= 8 and all(part not in ('.', '..') for part in path.parts) and not path.is_absolute()
        assert item.file_size <= 16 * 1024 * 1024 and stat.S_IFMT(item.external_attr >> 16) in (0, stat.S_IFREG)
        content = archive.read(item)
        dest = package / path
        dest.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
        dest.write_bytes(content)
        dest.chmod(0o600)
        selected.append(dict(path=str(path), sha256=hashlib.sha256(content).hexdigest(), bytes=len(content)))
assert sum(x['bytes'] for x in selected) <= 32 * 1024 * 1024
programs = root / 'programs'
build = out / 'probe-build'
build.mkdir(mode=0o700)


def run(args):
    remaining = deadline - time.monotonic()
    assert remaining > 8
    # Inherit the external owner's process group; no descendant can escape its cleanup.
    process = subprocess.Popen(args, cwd=programs,
                               stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                               env=dict(os.environ, MSBUILDDISABLENODEREUSE='1', DOTNET_CLI_USE_MSBUILD_SERVER='0'))
    record = dict(argv=args, pid=process.pid, exit=None, joined=False, group_absent=False, timeout=False)
    records.append(record)
    stdout = stderr = b''
    errors = []
    try:
        stdout, stderr = process.communicate(timeout=min(60, remaining - 7))
    except subprocess.TimeoutExpired:
        record['timeout'] = True
        raise
    finally:
        try:
            for sig in (signal.SIGTERM, signal.SIGKILL):
                if process.poll() is not None:
                    break
                try:
                    process.send_signal(sig)
                except ProcessLookupError:
                    pass
                except BaseException as error:
                    errors.append(type(error).__name__)
                try:
                    stdout, stderr = process.communicate(timeout=2)
                except BaseException as error:
                    errors.append(type(error).__name__)
            try:
                process.wait(timeout=2)
                record['joined'] = True
            except BaseException as error:
                errors.append(type(error).__name__)
        finally:
            record['exit'] = process.returncode
            record['cleanup_errors'] = errors
            record['group_absent'] = None  # Entire inherited group is checked by supervisor.
            for stream in (process.stdout, process.stderr):
                try:
                    if stream is not None:
                        stream.close()
                except BaseException as error:
                    errors.append(type(error).__name__)
        assert len(stdout) + len(stderr) <= 1024 * 1024
        for role, content in (('stdout', stdout), ('stderr', stderr)):
            path = out / f'build-{len(records):02d}.{role}'
            path.write_bytes(content)
            path.chmod(0o600)
            record[role + '_sha256'] = hashlib.sha256(content).hexdigest()
    assert record['exit'] == 0 and record['joined'] and not record['cleanup_errors'] and not record['timeout']
    text = stdout.decode('utf-8') + stderr.decode('utf-8')
    assert not any('warning ' in line.lower() or 'error ' in line.lower() for line in text.splitlines())


numeric_exit = 1
error_category = None
try:
    for project in ('CounterFixture.csproj', 'TaskHost.csproj'):
        run(['dotnet', 'restore', project, '--locked-mode', '-p:CoverletPackageRoot=' + str(package), '--disable-parallel'])
        run(['dotnet', 'build', project, '--no-restore', '-p:UseSharedCompilation=false',
             '-p:CoverletPackageRoot=' + str(package), '--output', str(build)])
    assert time.monotonic() < deadline
    numeric_exit = 0
except BaseException as error:
    error_category = type(error).__name__
finally:
    record = dict(schema='issue779-existing-permissions-probe-build-v1', numeric_exit=numeric_exit,
                  error_category=error_category, elapsed_seconds=time.monotonic()-started,
                  package_sha256=package_sha, package_files=selected, commands=records,
                  native_mechanism_execution=False, production_modified=False)
    path = out / 'build-receipt.json'
    path.write_text(json.dumps(record, indent=2, sort_keys=True) + '\n')
    path.chmod(0o600)
    print(json.dumps(dict(numeric_exit=numeric_exit, elapsed_seconds=record['elapsed_seconds'])))
    raise SystemExit(numeric_exit)
