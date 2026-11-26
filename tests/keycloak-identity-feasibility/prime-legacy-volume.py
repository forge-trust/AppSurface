"""Prime one exact generated-name fixture volume using the historical realm import."""

import fcntl
import json
import os
from pathlib import Path
import re
import subprocess
import time


RUNTIME_IMAGE = 'mcr.microsoft.com/dotnet/sdk@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b'
KEYCLOAK_IMAGE = 'quay.io/keycloak/keycloak@sha256:831330513f55695572286e521f94fcd3c7e285250ed5b848090265a33192f669'
WORKER = 'ForgeTrust.AppSurface.Auth.Aspire.Keycloak.RealmReadyWorker.dll'


def prime(docker, root, closure, volume, volume_label, import_path, port, admin_password, observe,
          *, historical_uid=False):
    """Return allowlisted readback and stopped evidence. Retain the marker and inputs on uncertain stop."""
    if not re.fullmatch(r'[a-zA-Z0-9][a-zA-Z0-9_.-]{0,127}', volume):
        raise ValueError('unsafe generated volume name')
    if not import_path.is_file() or import_path.is_symlink() or not import_path.resolve().is_relative_to(root):
        raise ValueError('unqualified private legacy import')

    run = root / 'legacy-bootstrap'
    run.mkdir()
    private = run / 'private'
    private.mkdir()
    marker = run / 'active.json'
    deadline = None

    def cli(*args, expected=0, seconds=8):
        if deadline is not None:
            seconds = min(seconds, deadline - time.monotonic())
            if seconds <= 0:
                raise TimeoutError('legacy bootstrap stopped verification exceeded 30 seconds')
        process = subprocess.run([*docker, *args], capture_output=True, text=True, timeout=seconds)
        if process.returncode != expected:
            raise RuntimeError('legacy bootstrap Docker operation failed: ' + args[0])
        if args[0] == 'top' and (process.stdout.strip() or 'is not running' not in process.stderr.lower()):
            raise RuntimeError('legacy bootstrap stopped census unavailable')
        return process.stdout.strip()

    def attest(container, name, image):
        item, = json.loads(cli('inspect', container))
        if item['Id'] != container or item['Name'] != '/' + name or item['Config']['Image'] != image:
            raise IOError('legacy bootstrap container identity changed')
        if item['Config']['Labels'].get('appsurface.issue888.legacy') != volume_label:
            raise IOError('legacy bootstrap container label changed')
        volumes = [mount for mount in item['Mounts'] if mount['Type'] == 'volume']
        if len(volumes) != 1 or volumes[0]['Name'] != volume:
            raise IOError('legacy bootstrap container volume changed')
        return item['State']

    containers = []

    def save():
        temporary = marker.with_suffix('.tmp')
        with temporary.open('w') as file:
            json.dump({'volume': volume, 'fixture': volume_label, 'containers': containers}, file)
            file.flush()
            os.fsync(file.fileno())
        temporary.replace(marker)

    def create(role, image, arguments, command):
        name = 'appsurface-legacy-' + volume_label + '-' + role
        # Durable intent precedes the create. A failed or lost acknowledgement can leave an unknown resource.
        record = {'name': name, 'image': image, 'id': None}
        containers.append(record)
        save()
        record['id'] = cli('create', '--name', name, '--label', 'appsurface.issue888.legacy=' + volume_label,
                           '--restart', 'no', '--cap-drop', 'ALL', '--security-opt', 'no-new-privileges',
                           *arguments, image, *command)
        save()
        attest(record['id'], name, image)
        cli('start', record['id'])
        return record['id']

    with (run / 'owner.lock').open('x+b') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        if volume in cli('volume', 'ls', '--format', '{{.Name}}').splitlines():
            raise ValueError('refusing a preexisting legacy fixture volume')
        save()
        cli('volume', 'create', '--label', 'appsurface.issue888.legacy=' + volume_label, volume)
        initial_volume, = json.loads(cli('volume', 'inspect', volume))
        if initial_volume['Name'] != volume or initial_volume['Driver'] != 'local' or initial_volume['Scope'] != 'local' \
                or initial_volume['Labels'].get('appsurface.issue888.legacy') != volume_label:
            raise IOError('legacy bootstrap volume label changed')
        try:
            identity = []
            if historical_uid:
                if cli('image', 'inspect', KEYCLOAK_IMAGE, '--format', '{{.Config.User}}') != '1000':
                    raise IOError('pinned historical Keycloak image does not declare UID 1000')
            else:
                work = closure / 'host/appsurface-keycloak-realm-ready'
                initializer = create('initialize', RUNTIME_IMAGE,
                    ['--network', 'none', '--read-only', '--user', '0:0', '--cap-add', 'CHOWN', '--cap-add', 'FOWNER',
                     '--mount', f'type=volume,source={volume},target=/volume',
                     '--mount', f'type=bind,source={work},target=/proof/code,readonly',
                     '--entrypoint', '/usr/share/dotnet/dotnet'],
                    ['/proof/code/' + WORKER, '--appsurface-keycloak-initialize-volume', str(os.getuid())])
                if cli('wait', initializer, seconds=30) != '0':
                    raise IOError('legacy volume initialization failed')
                identity = ['--user', f'{os.getuid()}:0']
            cert = private / 'server.crt'
            key = private / 'server.key'
            subprocess.run(['/usr/bin/openssl', 'req', '-x509', '-newkey', 'rsa:2048', '-nodes',
                            '-subj', '/CN=localhost', '-addext', 'subjectAltName=DNS:localhost',
                            '-days', '1', '-keyout', str(key), '-out', str(cert)],
                           check=True, capture_output=True, timeout=12)
            (private / 'bootstrap.conf').write_text(
                f'bootstrap-admin-username=proof-admin\nbootstrap-admin-password={admin_password}\n')
            if historical_uid:
                # These exact fixtures are readable inside the historical UID's read-only mounts.
                # Both mount sources remain behind private host ancestors under `root`.
                for source in (private, import_path.parent):
                    source.chmod(0o555)
                for source in (key, cert, private / 'bootstrap.conf', import_path):
                    source.chmod(0o444)
            server = create('keycloak', KEYCLOAK_IMAGE,
                ['--publish', f'127.0.0.1:{port}:{port}', *identity,
                 '--tmpfs', '/tmp:rw,mode=1777,size=256m', '--env', 'JAVA_OPTS_KC_HEAP=-Xms128m -Xmx512m',
                 '--mount', f'type=volume,source={volume},target=/opt/keycloak/data',
                 '--mount', f'type=bind,source={private},target=/proof/server,readonly',
                 '--mount', f'type=bind,source={import_path.parent},target=/opt/keycloak/data/import,readonly',
                 '--entrypoint', '/opt/keycloak/bin/kc.sh'],
                ['--config-file=/proof/server/bootstrap.conf', 'start-dev', '--import-realm',
                 '--http-enabled=false', f'--https-port={port}', f'--hostname=https://localhost:{port}',
                 '--https-certificate-file=/proof/server/server.crt', '--https-certificate-key-file=/proof/server/server.key'])
            started = time.monotonic()
            while True:
                try:
                    readback = observe(port, cert, admin_password)
                    break
                except (OSError, RuntimeError):
                    if time.monotonic() - started > 120:
                        raise TimeoutError('legacy import did not become readable') from None
                    time.sleep(.2)
            ownership = None
            if historical_uid:
                ownership = observe_data_ownership(cli, server)
                if ownership['serverUid'] != 1000 or ownership['database']['uid'] != 1000:
                    raise IOError('historical Keycloak did not create the database as UID 1000')
        finally:
            stop_started = time.monotonic()
            deadline = stop_started + 30
            stopped = []
            for container in reversed(containers):
                identifier = container['id']
                if identifier is None:
                    raise IOError('legacy create outcome unverified; marker retained')
                state = attest(identifier, container['name'], container['image'])
                if state['Running']:
                    cli('stop', '--time', '5', identifier)
                state = attest(identifier, container['name'], container['image'])
                if state['Running'] or state['Paused'] or state['Restarting'] or state['Pid'] != 0:
                    raise IOError('legacy stop could not be verified')
                if state['Status'] not in ('exited', 'created'):
                    raise IOError('legacy stop could not be verified')
                cli('top', identifier, expected=1)
                stopped.append({'id': identifier, 'state': state['Status'], 'pid': 0})
            if not marker.is_file() or not import_path.is_file() or not private.is_dir():
                raise IOError('legacy inputs or active marker changed before stopped observation')
            for item in stopped:
                cli('rm', item['id'])
            if historical_uid:
                private.chmod(0o700)
                import_path.parent.chmod(0o700)
            for path in private.iterdir():
                path.unlink()
            private.rmdir()
            import_path.unlink()
            if time.monotonic() >= deadline:
                raise TimeoutError('legacy bootstrap cleanup exceeded 30 seconds')
            marker.unlink()
    return {
        'volume': volume, 'label': volume_label, 'createdAt': initial_volume['CreatedAt'],
        'observedBeforeMigration': readback, 'stoppedBeforeClean': stopped,
        'stopSeconds': time.monotonic() - stop_started, 'activeCleared': True, 'ownership': ownership,
    }


def observe_data_ownership(cli, server):
    """Read the running server UID and safe metadata for the actual H2 data paths."""
    uid = int(cli('exec', server, 'id', '-u'))
    root, db = tuple(cli('exec', server, 'stat', '-c', '%u %g %a %i',
                         '/opt/keycloak/data', '/opt/keycloak/data/h2/keycloakdb.mv.db').splitlines())
    return {'serverUid': uid, 'root': ownership_fields(root), 'database': ownership_fields(db)}


def ownership_fields(line):
    uid, gid, mode, inode = line.split()
    return {'uid': int(uid), 'gid': int(gid), 'mode': mode, 'inode': int(inode)}
