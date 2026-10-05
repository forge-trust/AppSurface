"""Own download/build descendants through a hard external group deadline."""
import hashlib
import json
import os
import signal
import subprocess
import sys
import time
from pathlib import Path

source = Path(sys.argv[1]).resolve(strict=True)
out = Path(sys.argv[2]).resolve()
started = time.monotonic()
deadline = started + 180
process = None
stdout = stderr = b''
record = dict(numeric_exit=1, timed_out=False, joined=False, group_absent=False, cleanup_errors=[],
              operation_guard_seconds=180, cleanup_guard_seconds=5, native_mechanism_execution=False)
try:
    process = subprocess.Popen([sys.executable, '-B', str(source / 'build-native-probe.py'), str(source), str(out)],
                               start_new_session=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    record['pid'] = process.pid
    stdout, stderr = process.communicate(timeout=max(0.001, deadline-time.monotonic()))
    record['child_exit'] = process.returncode
    if process.returncode == 0 and time.monotonic() < deadline:
        record['numeric_exit'] = 0
except subprocess.TimeoutExpired:
    record['timed_out'] = True
except BaseException as error:
    record['error_category'] = type(error).__name__
finally:
    cleanup_deadline = time.monotonic() + 5
    if process is not None:
        for sig in (signal.SIGTERM, signal.SIGKILL):
            try:
                os.killpg(process.pid, 0)
            except ProcessLookupError:
                break
            except BaseException as error:
                record['cleanup_errors'].append(type(error).__name__)
            try:
                os.killpg(process.pid, sig)
            except ProcessLookupError:
                pass
            except BaseException as error:
                record['cleanup_errors'].append(type(error).__name__)
            try:
                stdout, stderr = process.communicate(timeout=max(0.001, min(2, cleanup_deadline-time.monotonic())))
            except BaseException as error:
                record['cleanup_errors'].append(type(error).__name__)
        try:
            process.wait(timeout=max(0.001, cleanup_deadline-time.monotonic()))
            record['joined'] = True
        except BaseException as error:
            record['cleanup_errors'].append(type(error).__name__)
        try:
            os.killpg(process.pid, 0)
        except ProcessLookupError:
            record['group_absent'] = True
        except BaseException as error:
            record['cleanup_errors'].append(type(error).__name__)
        for stream in (process.stdout, process.stderr):
            try:
                if stream is not None:
                    stream.close()
            except BaseException as error:
                record['cleanup_errors'].append(type(error).__name__)
    if not record['joined'] or not record['group_absent'] or record['cleanup_errors'] or record['timed_out']:
        record['numeric_exit'] = 1
    record['elapsed_seconds'] = time.monotonic()-started
    if len(stdout) + len(stderr) > 1024 * 1024:
        record['numeric_exit'] = 1
        stdout, stderr = stdout[:512*1024], stderr[:512*1024]
        record['logs_truncated'] = True
    out.mkdir(mode=0o700, exist_ok=True)
    for role, data in (('stdout', stdout), ('stderr', stderr)):
        path = out / ('supervisor.' + role)
        with path.open('xb') as stream:
            os.chmod(path, 0o600)
            stream.write(data)
        record[role + '_sha256'] = hashlib.sha256(data).hexdigest()
    path = out / 'supervision-receipt.json'
    with path.open('x') as stream:
        os.chmod(path, 0o600)
        json.dump(record, stream, indent=2, sort_keys=True)
        stream.write('\n')
    print(json.dumps(dict(numeric_exit=record['numeric_exit'], elapsed_seconds=record['elapsed_seconds'],
                          timed_out=record['timed_out'], group_absent=record['group_absent'])))
    raise SystemExit(record['numeric_exit'])
