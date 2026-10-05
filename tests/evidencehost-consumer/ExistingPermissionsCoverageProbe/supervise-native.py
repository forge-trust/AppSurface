"""Root owner of a fixed generated unit and the controller's inherited group."""
import argparse
import json
import os
import signal
import subprocess
import sys
import time
import uuid
import importlib.util
from pathlib import Path

p = argparse.ArgumentParser()
for name in ('source-root', 'probe-build', 'output', 'dotnet'):
    p.add_argument('--' + name, type=Path, required=True)
a = p.parse_args()
assert os.geteuid() == 0
tag = uuid.uuid4().hex
unit = 'issue779-existing-permissions-' + tag + '.service'
cgroup = Path('/sys/fs/cgroup/system.slice') / unit
started = time.monotonic()
deadline = started + 90
process = None
stdout = stderr = b''
record = dict(schema='issue779-existing-permissions-native-owner-v1', numeric_exit=1,
              operation_guard_seconds=90, cleanup_guard_seconds=10, timed_out=False,
              joined=False, group_absent=False, selected_unit_stopped=False,
              cleanup_errors=[], new_path_grant=False, trusted_claim=False)
try:
    argv = [sys.executable, '-B', str(Path(__file__).with_name('run-native-probe.py'))]
    for name in ('source-root', 'probe-build', 'output', 'dotnet'):
        argv.extend(['--' + name, str(getattr(a, name.replace('-', '_')))])
    argv.extend(['--tag', tag])
    process = subprocess.Popen(argv, start_new_session=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    record['controller_pid'] = process.pid
    stdout, stderr = process.communicate(timeout=max(0.001, deadline-time.monotonic()))
    record['controller_exit'] = process.returncode
    if process.returncode == 0 and time.monotonic() < deadline:
        record['numeric_exit'] = 0
except subprocess.TimeoutExpired:
    record['timed_out'] = True
except BaseException as error:
    record['error_category'] = type(error).__name__
finally:
    cleanup_deadline = time.monotonic() + 10
    if process is not None:
        # Seal all possible dispatch before stopping the generated unit.
        for sig in (signal.SIGTERM, signal.SIGKILL):
            try:
                os.killpg(process.pid, sig)
            except ProcessLookupError:
                break
            except BaseException as error:
                record['cleanup_errors'].append(type(error).__name__)
            try:
                stdout, stderr = process.communicate(timeout=max(0.001, min(2, cleanup_deadline-time.monotonic())))
            except BaseException as error:
                record['cleanup_errors'].append(type(error).__name__)
        try:
            process.wait(timeout=max(0.001, min(2, cleanup_deadline-time.monotonic())))
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
                stream.close()
            except BaseException as error:
                record['cleanup_errors'].append(type(error).__name__)
    try:
        outcomes = []
        for command in (['/usr/bin/systemctl', 'kill', '--kill-whom=all', '--signal=SIGKILL', unit],
                        ['/usr/bin/systemctl', 'stop', unit]):
            completed = subprocess.run(command, capture_output=True, timeout=max(0.001, min(2, cleanup_deadline-time.monotonic())))
            # Missing unit after successful controller stop is expected. Physical facts follow.
            if len(completed.stdout) + len(completed.stderr) > 65536:
                raise RuntimeError('closed cleanup command output bound')
            outcomes.append(completed.returncode)
        record['kill_command_exit'], record['stop_command_exit'] = outcomes
        query = subprocess.run(['/usr/bin/systemctl', 'show', unit, '--property=LoadState,ActiveState,SubState,MainPID'],
                               capture_output=True, timeout=max(0.001, min(2, cleanup_deadline-time.monotonic())))
        assert len(query.stdout) + len(query.stderr) <= 65536
        facts = {}
        for line in query.stdout.decode('ascii').splitlines():
            key, sep, value = line.partition('=')
            assert sep and key not in facts
            facts[key] = value
        helper_path = Path(__file__).with_name('run-native-probe.py')
        spec = importlib.util.spec_from_file_location('probe_stop_data', helper_path)
        helper = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(helper)
        record['stop_confirmation'] = helper.confirmed_unit_stop(outcomes[1], query.returncode, facts)
        record['unit_query_exit'] = query.returncode
        if cgroup.exists():
            assert cgroup.is_dir() and not cgroup.is_symlink()
            assert not any(p.is_dir() for p in cgroup.iterdir())
            assert not (cgroup / 'cgroup.procs').read_bytes().strip()
        record['selected_unit_stopped'] = True
    except BaseException as error:
        record['cleanup_errors'].append(type(error).__name__)
    if not record['joined'] or not record['group_absent'] or not record['selected_unit_stopped'] or record['timed_out'] or record['cleanup_errors']:
        record['numeric_exit'] = 1
    if len(stdout) + len(stderr) > 65536:
        record['numeric_exit'] = 1
        record['logs_truncated'] = True
        stdout, stderr = stdout[:32768], stderr[:32768]
    a.output.mkdir(mode=0o700, exist_ok=True)
    for role, data in (('stdout', stdout), ('stderr', stderr)):
        path = a.output / ('supervisor.' + role)
        with path.open('xb') as stream:
            os.chmod(path, 0o600)
            stream.write(data)
    record['elapsed_seconds'] = time.monotonic()-started
    path = a.output / 'supervision-receipt.json'
    with path.open('x') as stream:
        os.chmod(path, 0o600)
        json.dump(record, stream, indent=2, sort_keys=True)
        stream.write('\n')
    print(json.dumps(dict(numeric_exit=record['numeric_exit'], elapsed_seconds=record['elapsed_seconds'],
                          timed_out=record['timed_out'], group_absent=record['group_absent'])))
    raise SystemExit(record['numeric_exit'])
