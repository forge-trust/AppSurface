"""Download one source-selected build image with an absolute network deadline.

The API bearer is sent only to the fixed GitHub artifact endpoint. A bounded
private control pipe passes request data to an unprivileged single-purpose child;
the parent kills its process group at the monotonic work deadline, reaps the child,
and requires stdout EOF before accepting any file. Storage redirects are handled
manually and each hop must remain HTTPS on port 443 without URL userinfo.
"""
import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import selectors
import signal
import stat
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

METADATA_SHA = '338d59c0a386e3b28ecae0d5af534a3e006f8b6339c950096279d7910ab0b39b'
BUILD_RUN = 38441424522
SELECTORS = ('None', *(f'N{i:02d}' for i in range(4, 17)))
ZIP_LIMIT = 256 * 1024 * 1024
CONTROL_LIMIT = 16 * 1024
WORKER_OUTPUT_LIMIT = 8192
CLEANUP_GRACE = 3.0
CLEANUP_FALLBACK = 2.0
MAX_STORAGE_REDIRECTS = 5
REDIRECT_CODES = frozenset((301, 302, 303, 307, 308))
WORKER_SCHEMA = 'issue779-integrated16-download-worker-v1'


class DownloadError(ValueError):
    def __init__(self, category, process=None):
        super().__init__(category)
        self.category = category
        self.process = process or {}


def require(ok, category):
    if not ok:
        raise ValueError(category)


def selected_artifact(raw, selector):
    require(selector in SELECTORS and hashlib.sha256(raw).hexdigest() == METADATA_SHA,
            'artifact-metadata-binding')
    metadata = json.loads(raw)
    require(metadata['run_id'] == BUILD_RUN and metadata['total_count'] == 14,
            'artifact-run-binding')
    name = f'issue779-integrated-image-{selector}-{BUILD_RUN}-1'
    selected = [row for row in metadata['artifacts'] if row['name'] == name]
    require(len(selected) == 1, 'artifact-selection')
    row = selected[0]
    require(type(row['id']) is int and row['id'] > 0 and row['expired'] is False
            and type(row['size_in_bytes']) is int and 0 < row['size_in_bytes'] <= ZIP_LIMIT
            and row['digest'].startswith('sha256:') and len(row['digest']) == 71,
            'artifact-metadata-shape')
    return row


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def validate_https_location(location, category='artifact-storage-location'):
    require(type(location) is str and location and not any(ord(ch) < 0x20 for ch in location),
            category)
    try:
        parsed = urllib.parse.urlsplit(location)
        port = parsed.port
    except ValueError:
        raise ValueError(category) from None
    require(parsed.scheme.lower() == 'https' and bool(parsed.hostname)
            and parsed.username is None and parsed.password is None
            and port in (None, 443) and not parsed.fragment, category)
    return location


def signed_location(artifact_id, token, deadline):
    """Ask only the fixed GitHub API; never forward its bearer token to storage."""
    require(type(artifact_id) is int and artifact_id > 0
            and type(token) is str and bool(token) and '\r' not in token and '\n' not in token
            and time.monotonic() < deadline, 'artifact-download-prerequisite')
    request = urllib.request.Request(
        f'https://api.github.com/repos/forge-trust/AppSurface/actions/artifacts/{artifact_id}/zip',
        headers={'Authorization': 'Bearer ' + token, 'Accept': 'application/vnd.github+json',
                 'X-GitHub-Api-Version': '2022-11-28'})
    try:
        with urllib.request.build_opener(NoRedirect).open(
                request, timeout=min(30, deadline - time.monotonic())):
            raise ValueError('artifact-api-no-redirect')
    except urllib.error.HTTPError as error:
        try:
            require(error.code == 302, 'artifact-api-status')
            location = error.headers.get('Location', '')
        finally:
            error.close()
    return validate_https_location(location, 'artifact-storage-location')


def _open_storage(location, deadline, opener_factory=urllib.request.build_opener):
    """Open a storage response with redirects disabled and checked one hop at a time."""
    current = validate_https_location(location)
    opener = opener_factory(NoRedirect())
    redirects = 0
    while True:
        remaining = deadline - time.monotonic()
        require(remaining > 0, 'artifact-download-deadline')
        request = urllib.request.Request(current, headers={'Accept': 'application/zip'})
        try:
            response = opener.open(request, timeout=min(30, remaining))
        except urllib.error.HTTPError as error:
            try:
                status = error.code
                location_header = error.headers.get('Location') if error.headers else None
            finally:
                error.close()
            require(status in REDIRECT_CODES, 'artifact-storage-status')
            require(redirects < MAX_STORAGE_REDIRECTS, 'artifact-storage-redirect-limit')
            require(type(location_header) is str and bool(location_header),
                    'artifact-storage-redirect-location')
            next_location = urllib.parse.urljoin(current, location_header)
            current = validate_https_location(next_location)
            redirects += 1
            continue
        status = getattr(response, 'status', None)
        if status is None:
            status = response.getcode()
        if status != 200:
            response.close()
            raise ValueError('artifact-storage-status')
        return response, redirects


def _identity(fact):
    return (fact.st_dev, fact.st_ino, fact.st_mode, fact.st_uid, fact.st_gid,
            fact.st_nlink, fact.st_size, fact.st_mtime_ns, fact.st_ctime_ns)


def _download_direct(location, path, expected_size, expected_digest, deadline,
                     open_storage=_open_storage):
    """Worker-only streaming implementation; the parent supervises its whole lifetime."""
    require(path.is_absolute() and not os.path.lexists(path)
            and time.monotonic() < deadline, 'artifact-download-freshness')
    require(type(expected_size) is int and 0 < expected_size <= ZIP_LIMIT,
            'artifact-download-byte-limit')
    require(type(expected_digest) is str and expected_digest.startswith('sha256:')
            and len(expected_digest) == 71, 'artifact-download-digest-shape')
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC, 0o600)
    count = 0
    digest = hashlib.sha256()
    redirects = 0
    try:
        response, redirects = open_storage(location, deadline)
        with response:
            require(getattr(response, 'status', 200) == 200, 'artifact-storage-status')
            while True:
                require(time.monotonic() < deadline, 'artifact-download-deadline')
                block = response.read(min(65536, expected_size + 1 - count))
                if not block:
                    break
                count += len(block)
                require(count <= expected_size, 'artifact-download-growth')
                digest.update(block)
                pending = memoryview(block)
                while pending:
                    require(time.monotonic() < deadline, 'artifact-download-deadline')
                    written = os.write(fd, pending)
                    require(written > 0, 'artifact-download-short-write')
                    pending = pending[written:]
        os.fsync(fd)
        require(time.monotonic() < deadline and count == expected_size
                and 'sha256:' + digest.hexdigest() == expected_digest, 'artifact-download-digest')
        fact = os.fstat(fd)
        require(stat.S_ISREG(fact.st_mode) and fact.st_nlink == 1
                and fact.st_size == count and stat.S_IMODE(fact.st_mode) == 0o600,
                'artifact-download-file')
        return {'bytes': count, 'sha256': digest.hexdigest(), 'redirects': redirects}
    finally:
        os.close(fd)


def _worker_environment():
    # The bearer is intentionally absent from the environment and argv; it crosses
    # only the bounded private control pipe and is used for the fixed GitHub API.
    names = ('PATH', 'LANG', 'LC_ALL', 'TMPDIR', 'HTTPS_PROXY', 'https_proxy',
             'NO_PROXY', 'no_proxy', 'SSL_CERT_FILE', 'SSL_CERT_DIR')
    env = {name: os.environ[name] for name in names if name in os.environ}
    env['PYTHONNOUSERSITE'] = '1'
    return env


def _run_owned_child(argv, cwd, deadline, input_payload=None):
    """Run the single-process network worker and always join it before returning.

    On deadline or protocol failure, signal the still-unreaped worker process
    group, reap the child within a fixed cleanup allowance, and require pipe EOF.
    The worker itself never launches subprocesses; stdout contains only a small
    token-free result record, not image bytes.
    """
    require(time.monotonic() < deadline, 'artifact-download-deadline')
    if input_payload is not None:
        require(type(input_payload) is bytes and 0 < len(input_payload) <= CONTROL_LIMIT,
                'artifact-worker-input-size')
    proc = subprocess.Popen(argv, cwd=cwd, env=_worker_environment(),
                            stdin=subprocess.PIPE if input_payload is not None else subprocess.DEVNULL,
                            stdout=subprocess.PIPE,
                            stderr=subprocess.DEVNULL, start_new_session=True,
                            close_fds=True, bufsize=0)
    assert proc.stdout is not None
    selector = selectors.DefaultSelector()
    os.set_blocking(proc.stdout.fileno(), False)
    selector.register(proc.stdout, selectors.EVENT_READ, 'stdout')
    input_offset = 0
    if proc.stdin is not None:
        os.set_blocking(proc.stdin.fileno(), False)
        selector.register(proc.stdin, selectors.EVENT_WRITE, 'stdin')
    output = bytearray()
    pipe_eof = False
    waited = False
    returncode = None
    timed_out = False
    forced_cleanup = False
    overflow = False
    failure = None
    try:
        while not pipe_eof or (proc.stdin is not None and not proc.stdin.closed
                               and proc.stdin.fileno() in selector.get_map()):
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                timed_out = True
                break
            events = selector.select(remaining)
            if not events:
                timed_out = time.monotonic() >= deadline
                if timed_out:
                    break
                continue
            for key, _ in events:
                if key.data == 'stdin':
                    try:
                        count = os.write(key.fd, input_payload[input_offset:input_offset + 4096])
                    except BlockingIOError:
                        continue
                    except BrokenPipeError:
                        failure = 'artifact-worker-input-closed'
                        break
                    require(count > 0, 'artifact-worker-input-write')
                    input_offset += count
                    if input_offset == len(input_payload):
                        selector.unregister(key.fileobj)
                        proc.stdin.close()
                    continue
                try:
                    block = os.read(key.fd, min(4096, WORKER_OUTPUT_LIMIT + 1 - len(output)))
                except BlockingIOError:
                    continue
                if not block:
                    selector.unregister(key.fileobj)
                    pipe_eof = True
                    break
                output.extend(block)
                if len(output) > WORKER_OUTPUT_LIMIT:
                    overflow = True
                    failure = 'artifact-worker-output-limit'
                    break
            if failure is not None:
                break
        if (proc.stdin is not None and not proc.stdin.closed
                and proc.stdin.fileno() in selector.get_map()):
            failure = failure or 'artifact-worker-input-incomplete'
        if pipe_eof and failure is None and not timed_out:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                timed_out = True
            else:
                try:
                    returncode = proc.wait(timeout=remaining)
                    waited = True
                    if time.monotonic() >= deadline:
                        timed_out = True
                except subprocess.TimeoutExpired:
                    timed_out = True
    except BaseException:
        failure = 'artifact-worker-supervision'
        raise
    finally:
        if not waited:
            forced_cleanup = True
            # The process is still unreaped here, so its PID/PGID identity is
            # reserved while the whole worker group is stopped.
            try:
                os.killpg(proc.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
            except OSError:
                try:
                    proc.kill()
                except (ProcessLookupError, OSError):
                    pass
            try:
                returncode = proc.wait(timeout=CLEANUP_GRACE)
                waited = True
            except subprocess.TimeoutExpired:
                try:
                    proc.kill()
                except (ProcessLookupError, OSError):
                    pass
                try:
                    returncode = proc.wait(timeout=CLEANUP_FALLBACK)
                    waited = True
                except (subprocess.TimeoutExpired, OSError):
                    failure = failure or 'artifact-worker-reap-timeout'
            except OSError:
                failure = failure or 'artifact-worker-reap-error'
        if proc.stdin is not None and not proc.stdin.closed:
            try:
                selector.unregister(proc.stdin)
            except (KeyError, ValueError):
                pass
            proc.stdin.close()
        if not pipe_eof:
            cleanup_end = time.monotonic() + CLEANUP_GRACE
            while time.monotonic() < cleanup_end:
                try:
                    events = selector.select(min(0.05, cleanup_end - time.monotonic()))
                    if not events:
                        continue
                    for key, _ in events:
                        try:
                            block = os.read(key.fd, 4096)
                        except BlockingIOError:
                            continue
                        if not block:
                            selector.unregister(key.fileobj)
                            pipe_eof = True
                            break
                        room = max(0, WORKER_OUTPUT_LIMIT - len(output))
                        if room:
                            output.extend(block[:room])
                        if len(block) > room:
                            overflow = True
                            failure = failure or 'artifact-worker-output-limit'
                    if pipe_eof:
                        break
                except (OSError, ValueError):
                    failure = failure or 'artifact-worker-pipe-error'
                    break
        try:
            selector.close()
        finally:
            proc.stdout.close()
    result = {
        'returncode': returncode,
        'waited': waited,
        'pipe_eof': pipe_eof,
        'process_settled': waited and pipe_eof,
        'deadline_ok': not timed_out and time.monotonic() < deadline,
        'timed_out': timed_out,
        'forced_cleanup': forced_cleanup,
        'overflow': overflow,
        'failure': failure,
    }
    if not result['process_settled']:
        raise DownloadError('artifact-download-worker-unsettled', result)
    if timed_out or not result['deadline_ok']:
        return result, bytes(output)
    if failure is not None or overflow:
        raise DownloadError(failure or 'artifact-download-worker-output-limit', result)
    return result, bytes(output)


def _verify_download(path, expected_size, expected_digest, deadline):
    before = path.lstat()
    require(stat.S_ISREG(before.st_mode) and before.st_nlink == 1
            and stat.S_IMODE(before.st_mode) == 0o600 and before.st_size == expected_size,
            'artifact-download-file')
    fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC)
    count = 0
    digest = hashlib.sha256()
    try:
        opened = os.fstat(fd)
        require(_identity(before) == _identity(opened), 'artifact-download-substitution')
        while True:
            require(time.monotonic() < deadline, 'artifact-download-deadline')
            block = os.read(fd, min(65536, expected_size + 1 - count))
            if not block:
                break
            count += len(block)
            require(count <= expected_size, 'artifact-download-growth')
            digest.update(block)
        require(count == expected_size and _identity(before) == _identity(os.fstat(fd))
                == _identity(path.lstat()), 'artifact-download-file-changed')
        actual = digest.hexdigest()
        require('sha256:' + actual == expected_digest, 'artifact-download-digest')
        return {'bytes': count, 'sha256': actual}
    finally:
        os.close(fd)


def _supervised_download(control, path, expected_size, expected_digest, deadline):
    require(path.is_absolute() and not os.path.lexists(path)
            and time.monotonic() < deadline, 'artifact-download-freshness')
    require(type(expected_size) is int and 0 < expected_size <= ZIP_LIMIT,
            'artifact-download-byte-limit')
    require(type(expected_digest) is str and expected_digest.startswith('sha256:')
            and len(expected_digest) == 71, 'artifact-download-digest-shape')
    path.parent.mkdir(parents=True, exist_ok=True)
    value = dict(control)
    value.update({'path': str(path), 'expected_size': expected_size,
                  'expected_digest': expected_digest, 'deadline': deadline})
    payload = (json.dumps(value, sort_keys=True, separators=(',', ':')) + '\n').encode()
    require(0 < len(payload) <= CONTROL_LIMIT, 'artifact-worker-control-size')
    secret = value.get('token')
    argv = [sys.executable, '-B', str(Path(__file__).resolve()), '--download-worker']
    process, output = _run_owned_child(argv, path.parent, deadline, payload)
    if process['timed_out'] or not process['deadline_ok']:
        raise DownloadError('artifact-download-deadline', process)
    require(process['returncode'] == 0 and process['failure'] is None
            and not process['forced_cleanup'] and not process['overflow'],
            'artifact-download-worker-status')
    if secret and secret.encode() in output:
        raise DownloadError('artifact-worker-secret-echo', process)
    try:
        message = json.loads(output)
    except (json.JSONDecodeError, UnicodeDecodeError):
        raise DownloadError('artifact-download-worker-protocol', process) from None
    if type(message) is not dict or message.get('schema') != WORKER_SCHEMA:
        raise DownloadError('artifact-download-worker-protocol', process)
    if 'error' in message:
        category = message['error']
        if (set(message) == {'schema', 'error'} and type(category) is str
                and category.startswith('artifact-')):
            raise DownloadError(category, process)
        raise DownloadError('artifact-download-worker-failed', process)
    require(set(message) == {'schema', 'bytes', 'sha256', 'redirects'}
            and type(message['bytes']) is int and type(message['redirects']) is int
            and 0 <= message['redirects'] <= MAX_STORAGE_REDIRECTS
            and type(message['sha256']) is str
            and len(message['sha256']) == 64, 'artifact-download-worker-protocol')
    measured = _verify_download(path, expected_size, expected_digest, deadline)
    require(message['bytes'] == measured['bytes']
            and message['sha256'] == measured['sha256'], 'artifact-download-worker-binding')
    return {**measured, 'redirects': message['redirects'], 'process': process}


def download(location, path, expected_size, expected_digest, deadline):
    """Download a pre-signed storage URL under a hard child-process deadline."""
    return _supervised_download({'kind': 'storage', 'location': location},
                                path, expected_size, expected_digest, deadline)


def download_artifact(artifact_id, token, path, expected_size, expected_digest, deadline):
    """Fetch GitHub's signed location and its storage bytes inside one owned worker."""
    require(type(artifact_id) is int and artifact_id > 0 and type(token) is str and bool(token),
            'artifact-download-prerequisite')
    return _supervised_download({'kind': 'artifact', 'artifact_id': artifact_id, 'token': token},
                                path, expected_size, expected_digest, deadline)


def _worker_main():
    try:
        raw_control = sys.stdin.buffer.read(CONTROL_LIMIT + 1)
        require(0 < len(raw_control) <= CONTROL_LIMIT, 'artifact-worker-control-size')
        control = json.loads(raw_control)
        require(type(control) is dict, 'artifact-worker-control-json')
        required = {'kind', 'path', 'expected_size', 'expected_digest', 'deadline'}
        kind = control.get('kind')
        if kind == 'storage':
            required |= {'location'}
        elif kind == 'artifact':
            required |= {'artifact_id', 'token'}
        else:
            raise ValueError('artifact-worker-control-kind')
        require(set(control) == required, 'artifact-worker-control-fields')
        deadline = control['deadline']
        require(type(deadline) in (int, float) and math.isfinite(deadline),
                'artifact-worker-control-deadline')
        path = Path(control['path'])
        require(path.is_absolute(), 'artifact-download-path')
        location = (control['location'] if kind == 'storage' else
                    signed_location(control['artifact_id'], control['token'], deadline))
        result = _download_direct(location, path, control['expected_size'],
                                  control['expected_digest'], deadline)
        message = {'schema': WORKER_SCHEMA, **result}
        sys.stdout.write(json.dumps(message, sort_keys=True, separators=(',', ':')) + '\n')
        sys.stdout.flush()
        return 0
    except BaseException as error:
        category = str(error) if isinstance(error, ValueError) else ''
        if not category.startswith('artifact-') or len(category) > 96:
            category = 'artifact-download-worker-failed'
        message = {'schema': WORKER_SCHEMA, 'error': category}
        try:
            sys.stdout.write(json.dumps(message, sort_keys=True, separators=(',', ':')) + '\n')
            sys.stdout.flush()
        except BaseException:
            pass
        return 2


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--selector', choices=SELECTORS, required=True)
    parser.add_argument('--job-root', type=Path, required=True)
    args = parser.parse_args()
    root = Path(__file__).resolve().parent
    row = selected_artifact((root / 'image-artifact-metadata.json').read_bytes(), args.selector)
    job_end = int(os.environ['JOB_DEADLINE_MONOTONIC_NS']) / 1_000_000_000
    download_end = min(time.monotonic() + 180, job_end - 905)
    path = args.job_root / 'image.zip'
    measured = download_artifact(row['id'], os.environ['GH_TOKEN'], path,
                                 row['size_in_bytes'], row['digest'], download_end)
    # The installed workflow binds this module together with the stager and
    # its pure contract. Receipt verification is independent of the downloader.
    from image_artifact import stage_image
    result = stage_image(path, row['size_in_bytes'], row['digest'], args.selector,
                         args.job_root / 'image', min(time.monotonic() + 300, job_end - 905))
    receipt = {'schema': 'issue779-integrated-image-download-v1', 'build_run': BUILD_RUN,
               'artifact_id': row['id'], 'selector': args.selector, 'download': measured,
               'stage': result, 'native_execution': False, 'native_credit': False}
    target = args.job_root / 'image-download.json'
    with target.open('x') as stream:
        json.dump(receipt, stream, sort_keys=True)
        stream.write('\n')
    target.chmod(0o600)
    path.unlink()
    print('IMAGE_PREREQUISITE_STAGED')


if __name__ == '__main__':
    if len(sys.argv) == 2 and sys.argv[1] == '--download-worker':
        raise SystemExit(_worker_main())
    main()
