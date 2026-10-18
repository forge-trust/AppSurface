"""Finite real-object negative controls; C# retains all runtime, custody and account authority."""
from pathlib import Path
import hashlib
import importlib.util
import json
import os
import signal
import stat
import subprocess
import time
import uuid

spec = importlib.util.spec_from_file_location('workspace_guard_driver', Path(__file__).with_name('run-native.py'))
r = importlib.util.module_from_spec(spec)
spec.loader.exec_module(r)
APPLICATION_CASES = ('bundle-extra-directory', 'bundle-input-bytes', 'bundle-input-mode')
PRODUCER_CASES = ('scratch-fifo', 'scratch-hardlink', 'scratch-oversize')


def identity(path):
    """Snapshot this actual owned name, excluding read-dependent access time; no execution authority."""
    value = path.lstat()
    return (value.st_dev, value.st_ino, value.st_uid, value.st_gid, value.st_mode,
            value.st_nlink, value.st_size, value.st_mtime_ns, value.st_ctime_ns)


def mutate_bundle(tool, case):
    """Change one freshly copied fixture input before auditing; never alter the compiled selection."""
    r.require(case in APPLICATION_CASES, 'workspace-guard-case')
    root = tool / 'application'
    if case == 'bundle-extra-directory':
        selected = root / 'fixture-extra'
        selected.mkdir(mode=0o555)
        selected.chmod(0o555)
    else:
        selected = root / 'inputs/declared-input.txt'
        r.require(selected.read_bytes() == b'declared-native-input\n', 'original-declared-input')
        if case == 'bundle-input-bytes':
            selected.chmod(0o600)
            selected.write_bytes(b'Declared-native-input\n')
            selected.chmod(0o444)
        else:
            selected.chmod(0o555)
    value = selected.lstat()
    r.require(not stat.S_ISLNK(value.st_mode) and value.st_nlink >= 1, 'actual-mutated-node')
    return {'relative': str(selected.relative_to(tool)), 'mode': stat.S_IMODE(value.st_mode),
            'bytes': value.st_size, 'uid': value.st_uid, 'gid': value.st_gid,
            'sha256': hashlib.sha256(selected.read_bytes()).hexdigest() if selected.is_file() else None}


def seal_fixture(root):
    """Seal only a fresh root-created prelaunch copy, before any untrusted identity can access it."""
    for path in (root, *root.rglob('*')):
        value = path.lstat()
        r.require(stat.S_ISDIR(value.st_mode) or stat.S_ISREG(value.st_mode), 'guard-fixture-node')
        os.chown(path, 0, 0, follow_symlinks=False)
        path.chmod(0o555 if path.is_dir() or value.st_mode & 0o111 else 0o444)


def failure(path, phase, code):
    """Retain the original single closed root fault; raw messages never select an accepted outcome."""
    rows = r.events(path, 'evidence-native-observation-failure-v4')
    r.require(len(rows) == 1, 'workspace-original-failure')
    row = rows[0]
    r.require(row.get('phase') == phase and row.get('diagnostic_code') == code
              and row.get('error_kind') == 'Admission', 'workspace-original-failure-kind')
    return row


def require_producer_rejection(case, generation, descriptor, stderr):
    """Require actual unsealed unsafe nodes after an otherwise passing producer procedure."""
    r.require(case in PRODUCER_CASES, 'producer-guard-case')
    first = failure(stderr, 'ProducerSeal', 'ASEVD402')
    seals = r.events(stderr, 'evidence-producer-seal-failure-v1')
    r.require(seals and seals[0].get('phase') == 'collect'
              and seals[0].get('error_kind') == 'Admission', 'actual-collect-rejection')
    root = Path('/run') / ('appsurface-evidence-' + generation)
    manifest = json.loads((root / 'output/evidence/evidence-manifest.json').read_bytes())
    r.require(manifest['ExecutionVerdict'] == 'Passed'
              and len(manifest['ProducerResults']) == 1
              and manifest['ProducerResults'][0]['Outcome'] == 'Passed', 'guard-after-passing-producer')
    selected = Path('/run') / ('appsurface-evidence-producer-data-' + generation) / 'scratch/tmp' / ('checkpoint3-' + case)
    value = selected.lstat()
    r.require(value.st_uid == descriptor['subject_uid'] and value.st_gid == descriptor['subject_gid']
              and stat.S_IMODE(value.st_mode) == 0o600 and value.st_ino > 0, 'original-subject-owned-unsafe-node')
    guards = [event['fields'] for event in r.events(stderr, 'evidence-producer-seal-guard-v1')]
    if case == 'scratch-fifo':
        r.require(stat.S_ISFIFO(value.st_mode) and any(item.get('reason') == 'node-type'
                  and item.get('mode') == value.st_mode for item in guards), 'fifo-before-open-rejected')
    else:
        r.require(stat.S_ISREG(value.st_mode), 'actual-regular-unsafe-node')
        r.require(any(item.get('reason') == 'node-metadata' and item.get('inode') == value.st_ino
                  and item.get('actual_uid') == value.st_uid and item.get('actual_gid') == value.st_gid
                  and item.get('links') == value.st_nlink and item.get('bytes') == value.st_size
                  for item in guards), 'actual-unsafe-metadata-rejected')
        if case == 'scratch-hardlink':
            linked = Path(str(selected) + '-linked').lstat()
            r.require(value.st_nlink == 2 and (value.st_dev, value.st_ino) == (linked.st_dev, linked.st_ino), 'actual-hardlinked-node')
        else:
            r.require(value.st_size == 256 * 1024 * 1024 + 1 and value.st_nlink == 1, 'actual-oversized-node')
    return {'first_failure': first, 'mode': value.st_mode, 'uid': value.st_uid, 'gid': value.st_gid,
            'links': value.st_nlink, 'bytes': value.st_size, 'inode': value.st_ino,
            'quarantined': True, 'passing_procedure_before_failed_seal': True}


CONTAINMENT_RESERVE_SECONDS = 60


def observation_deadline(end, maximum):
    """Reserve actual containment inside the existing matrix clock, refusing a launch without it."""
    now = time.monotonic()
    r.require(end - now > CONTAINMENT_RESERVE_SECONDS, 'workspace-guard-containment-reserve')
    return min(end - CONTAINMENT_RESERVE_SECONDS, now + maximum)


def contain_units(units, process, end):
    """Try both manager stop and KILL for every selected unit before reaping the original client."""
    for unit in units:
        for command, seconds in ((['/usr/bin/systemctl', 'stop', unit], 5),
                                 (['/usr/bin/systemctl', 'kill', '--kill-whom=all', '--signal=KILL', unit], 2)):
            try:
                r.command(command, min(end, time.monotonic() + seconds), allowed=(0, 1, 5))
            except Exception:
                pass # A failed stop never skips the independent whole-unit KILL attempt.
    if process.poll() is None:
        try:
            os.killpg(process.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
        process.wait(timeout=max(.01, min(5, end - time.monotonic())))


def application_case(args, case, tools, runtime, end):
    """Run one genuine rejected root request and observe original containment/quarantine before repair."""
    r.require(time.monotonic() < end, 'workspace-guard-matrix-deadline')
    generation = uuid.uuid4().hex
    root = Path('/run') / ('issue779-c3-guard-' + generation)
    r.c2.create_traversable_root(root)
    normal, definition, digest = tools['normal']
    tool = root / 'tool'
    r.c2.copy_tree(normal, tool)
    subject = root / 'subject'
    r.c2.copy_tree(args.subject, subject)
    seal_fixture(tool)
    seal_fixture(subject)
    r.require(time.monotonic() < end, 'workspace-guard-matrix-deadline')
    mutation = mutate_bundle(tool, case)
    r.require(mutation['uid'] == mutation['gid'] == 0, 'actual-root-mutation')
    sentinel = root / 'outside-sentinel'
    r.write(sentinel, b'outside-owned-workspace-guard-sentinel\n')
    original = identity(sentinel)
    expected_bytes = sentinel.read_bytes()
    owner = 'appsurface-evidence-owner-' + generation + '.service'
    units = [owner, 'appsurface-evidence-worker-' + generation + '.service',
             'appsurface-evidence-producer-' + generation + '.service',
             'appsurface-evidence-application-' + generation + '.service']
    request = r.request_data(args, tool, runtime, definition, digest, subject, 'normal')
    request_path = root / 'request.json'
    r.write(request_path, r.canonical(request))
    guard = Path('/run/appsurface-evidence-owners')
    r.require(guard.is_dir() and guard.stat().st_uid == 0 and stat.S_IMODE(guard.stat().st_mode) == 0o700, 'original-owner-guard-root')
    marker = guard / generation
    marker.mkdir(mode=0o700)
    r.write(marker / 'armed', (generation + '\n').encode())
    stdout, stderr = root / 'owner.stdout', root / 'owner.stderr'
    result = {'case': case, 'generation': generation, 'passed': False, 'native_acceptance': False,
              'selected_entry_digest': digest, 'mutation': mutation}
    process = None
    deadline = observation_deadline(end, 180)
    try:
        with stdout.open('xb') as output, stderr.open('xb') as error:
            process = subprocess.Popen(r.owner_command(runtime, tool, owner, marker / 'armed', request_path),
                                       stdin=subprocess.DEVNULL, stdout=output, stderr=error, start_new_session=True)
            while process.poll() is None and time.monotonic() < deadline:
                r.require(stdout.stat().st_size + stderr.stat().st_size <= r.MAX_LOG, 'workspace-guard-output-bound')
                time.sleep(.1)
            r.require(process.poll() is not None, 'workspace-guard-terminal')
            result['root_exit'] = process.wait(timeout=5)
        r.require(result['root_exit'] != 0 and stdout.read_bytes() == b'', 'workspace-guard-no-success')
        result['first_failure'] = failure(stderr, 'WorkspaceCreate', 'ASEVD410')
        descriptor = Path('/run') / ('appsurface-evidence-' + generation) / 'worker/worker-control.json'
        r.require(not descriptor.exists(), 'workspace-rejection-before-descriptor-publication')
        r.require(r.c2.groups_empty(units), 'workspace-guard-original-groups-empty')
        result['groups_empty'] = True
        result['accounts'] = r.named_accounts(generation, True, deadline)
        r.require(identity(sentinel) == original and sentinel.read_bytes() == expected_bytes, 'outside-sentinel-unchanged')
        result['outside_sentinel_unchanged'] = True
        result['quarantined'] = True
        result['passed'] = True
    except BaseException as error:
        result['failure_category'] = str(error) if isinstance(error, RuntimeError) else type(error).__name__
    finally:
        # Containment cannot upgrade the original failure; account quarantine is never repaired by this fixture.
        if process is not None and (process.poll() is None or not result['passed']):
            contain_units(units, process, end)
        result['final_groups_empty'] = r.c2.groups_empty(units)
        if not result['final_groups_empty']:
            result['passed'] = False
            result.setdefault('failure_category', 'workspace-guard-final-groups-not-empty')
        for source, suffix in ((stdout, 'owner.stdout'), (stderr, 'owner.stderr')):
            if source.is_file() and source.stat().st_size <= r.MAX_LOG:
                destination = args.output / (case + '-' + suffix)
                with destination.open('xb') as target:
                    target.write(source.read_bytes())
                destination.chmod(0o600)
        r.write(args.output / (case + '.json'), r.canonical(result))
    return result


def run(args, tools, runtime, end):
    """Run the fixed six controls sequentially on the original matrix clock; stop on first failure."""
    results = []
    for case in APPLICATION_CASES:
        result = application_case(args, case, tools, runtime, end)
        results.append(result)
        if not result['passed']:
            return results
    for case in PRODUCER_CASES:
        result = r.execute(args, case, tools, runtime, end)
        if not result.get('final_groups_empty'):
            result['passed'] = False
            result.setdefault('failure_category', 'workspace-guard-final-groups-not-empty')
        results.append(result)
        if not result['passed']:
            break
    return results
