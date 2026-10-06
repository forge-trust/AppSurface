"""Root-selected, bounded build preparation for the approved private variant.

This generator runs before subjects are evaluated. Its output is source-compiled
metadata, never a runtime enrollment API. The later controller authenticates the
actual worker and collects through the launcher's retained handles.
"""
import base64
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import selectors
import shutil
import signal
import stat
import subprocess
import sys
import time

from trusted_sdk import SDK_ROOT, SDK_PATH, SdkDiagnostic, identity as sdk_identity, metadata as sdk_metadata, protect_sdk_ancestors, seal_trusted_sdk
from trusted_sdk_distribution import ARCHIVE_SHA512, ARCHIVE_URL, MAX_ARCHIVE_BYTES, MAX_NODES, MAX_TOTAL_BYTES, SDK_VERSION

BASELINE = "5d325bb8c0f857eb37f0a736f39a0342b03e5c38"
APP_ID = "issue779-qualified-native-http"
BUILD_ID = "issue779-private-qualification-1"
PROFILE = "qualification-http"
PRODUCER = "qual-coverage"
ASSERTION = "appsurface/coverage/behavioral-patch@1"
CLI_PROJECT = "Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj"
HOST_SYMBOL = "EVIDENCE_PRIVATE_QUALIFICATION_HOST"
PRIVATE_PUBLISH_RID = "linux-x64"
PRIVATE_PUBLISH_TARGET = "net10.0/linux-x64"
PRIVATE_PUBLISH_PROPERTIES = ("-p:RuntimeIdentifier=linux-x64", "-p:SelfContained=false", "-p:NuGetAudit=true")
PRIVATE_LOCK_COUNT = 256
PRIVATE_LOCK_BYTES = 256 * 1024
PRIVATE_LOCK_TOTAL = 8 * 1024 * 1024
PRIVATE_LOCK_SCAN_ENTRIES = 32768
PRIVATE_ASSETS_BYTES = 8 * 1024 * 1024


class PreparationFailure(Exception):
    """Fixed failure category; raw build logs remain in the root-private directory."""


def require(condition):
    if not condition:
        raise PreparationFailure("qualification-preparation-rejected")


def sha(data):
    return hashlib.sha256(data).hexdigest()


def unique_json(data):
    def pairs(rows):
        result = {}
        folded = set()
        for key, value in rows:
            require(key.casefold() not in folded)
            folded.add(key.casefold())
            result[key] = value
        return result
    return json.loads(data, object_pairs_hook=pairs)


def source_inventory(root):
    """Bind every tracked file's Git blob and executable mode to physical bytes."""
    raw = subprocess.check_output(["git", "ls-files", "--stage", "-z"], cwd=root)
    result = {}
    for row in raw.split(b"\0"):
        if not row:
            continue
        metadata, name = row.split(b"\t", 1)
        mode, blob, stage = metadata.split()
        relative = name.decode("utf-8")
        path = root / relative
        info = path.lstat()
        require(stage == b"0" and mode in (b"100644", b"100755")
                and stat.S_ISREG(info.st_mode) and not path.is_symlink())
        data = path.read_bytes()
        git_hash = hashlib.sha1(b"blob " + str(len(data)).encode() + b"\0" + data).hexdigest()
        require(git_hash == blob.decode() and bool(info.st_mode & 0o111) == (mode == b"100755"))
        result[relative] = {"sha256": sha(data), "git_mode": mode.decode()}
    return result


class Runner:
    """Owns each trusted build process group and a single cumulative deadline."""
    def __init__(self, logs, deadline):
        self.logs, self.deadline = logs, deadline
        self.results = []
        self._state = "ready"

    def run(self, argv, cwd, *, input_bytes=None, capture=False, capture_limit=1024*1024, env=None, maximum_seconds=180):
        """Return bounded captured bytes only after EOF, exit and owned-group cleanup.

        Captured stdout defaults to 1 MiB; a root-selected exact integer may raise
        that limit to 32 MiB plus one overflow-detection byte. Input is None or
        at most 1 MiB of bytes. Capture pumps stdin/stdout in nonblocking 64 KiB
        chunks. I/O reserves up to three seconds inside the original command end
        for cleanup; leader reap, group absence and publication use that same end.
        A real stream-close error rejects success even when the command exited zero.
        Accepted arguments latch pending before any log open or spawn. Reentry
        rejects; every later failure latches failed permanently, even if cleanup
        leaves cumulative time available. Only fully validated durable receipt,
        capture and final deadline checks restore ready. Invalid argument guards
        reject before consuming the ready state.
        Root-selected maximum_seconds defaults to 180 and may only shorten a
        command to an integer from 1 through 180; it cannot extend the original
        cumulative deadline. Ordinary command logs and receipts remain private.
        """
        require(type(maximum_seconds) is int and 1 <= maximum_seconds <= 180
                and type(capture_limit) is int and 0 < capture_limit <= 32*1024*1024+1
                and (input_bytes is None or type(input_bytes) is bytes and len(input_bytes) <= 1024*1024))
        require(self._state == "ready")
        self._state = "pending"
        try:
            result = self._run_owned(argv, cwd, input_bytes=input_bytes, capture=capture,
                capture_limit=capture_limit, env=env, maximum_seconds=maximum_seconds)
        except BaseException:
            self._state = "failed"
            raise
        self._state = "ready"
        return result

    def _run_owned(self, argv, cwd, *, input_bytes, capture, capture_limit, env, maximum_seconds):
        """Pending owner; only complete valid publication permits ready state."""
        require(time.monotonic() < self.deadline)
        started = time.monotonic()
        command_deadline = min(self.deadline, started + maximum_seconds)
        io_deadline = command_deadline-min(3, (command_deadline-started)/2)
        require(started < io_deadline < command_deadline)
        log = self.logs / f"build-{len(self.results)+1:02d}.log"
        process, output, code, error = None, None, None, None
        group_empty, timed_out, cleanup_failed = None, False, False
        category = None
        with log.open("xb") as stream:
            os.chmod(log, 0o600)
            environment = dict(os.environ if env is None else env, MSBUILDDISABLENODEREUSE="1",
                               DOTNET_CLI_USE_MSBUILD_SERVER="false", DOTNET_CLI_TELEMETRY_OPTOUT="1")
            try:
                process = subprocess.Popen(argv, cwd=cwd, env=environment, stdin=subprocess.PIPE if input_bytes is not None else subprocess.DEVNULL,
                                           stdout=subprocess.PIPE if capture else stream, stderr=stream, start_new_session=True)
                if capture:
                    output = self._capture(process, input_bytes, io_deadline, capture_limit)
                else:
                    remaining = io_deadline-time.monotonic()
                    if remaining <= 0:
                        raise subprocess.TimeoutExpired(argv, 0)
                    output, _ = process.communicate(input_bytes, timeout=remaining)
                code = process.returncode
            except subprocess.TimeoutExpired:
                timed_out = True
                category = "process-timeout"
                code, output = 124, None
                try:
                    os.killpg(process.pid, signal.SIGKILL)
                except ProcessLookupError:
                    pass
                except Exception:
                    cleanup_failed = True
            except Exception as failure:
                error = failure
                category = "process-start-failed" if process is None else "process-communication-failed"
            finally:
                if process is not None:
                    try:
                        group_empty = join_process_group(process, command_deadline)
                    except Exception:
                        cleanup_failed, group_empty = True, False
                    if code is None:
                        code = process.returncode
                    for pipe in (process.stdin, process.stdout, process.stderr):
                        if pipe is not None:
                            try:
                                pipe.close()
                            except Exception:
                                cleanup_failed = True
        log_size = log.stat().st_size
        if category is None:
            category = ("output-limit" if log_size > 8*1024*1024 or capture and len(output or b"") > capture_limit else
                        "cleanup-failed" if cleanup_failed else
                        "nonzero-exit" if code != 0 else "ownership-unconfirmed" if group_empty is not True else
                        "deadline-expired" if time.monotonic() >= command_deadline else None)
        record = {"argv": argv, "exit_code": code, "timed_out": timed_out,
                  "command_deadline": command_deadline, "io_deadline": io_deadline,
                  "owned_group_empty": group_empty, "failure_category": category, "log_bytes": log_size,
                  "cleanup_failed": cleanup_failed, "capture_limit": capture_limit if capture else None,
                  "captured_bytes": len(output) if isinstance(output, bytes) else None,
                  "log_sha256": sha(log.read_bytes()) if log_size <= 8*1024*1024 else None}
        self.results.append(record)
        receipt_path = self.logs / f"command-{len(self.results):02d}.json"
        with receipt_path.open("x") as receipt_stream:
            os.chmod(receipt_path, 0o600)
            json.dump(record, receipt_stream, sort_keys=True)
            receipt_stream.write("\n")
        if error is not None:
            raise error
        require(category is None)
        require(code == 0 and group_empty and not cleanup_failed and time.monotonic() < command_deadline)
        if capture:
            require(isinstance(output, bytes) and len(output) <= capture_limit)
            return output
        return b""

    @staticmethod
    def _capture(process, input_bytes, deadline, cap):
        """Bound stdout before append; nonblocking stdin, EOF and reap share one deadline."""
        output, written = bytearray(), 0
        with selectors.DefaultSelector() as selected:
            os.set_blocking(process.stdout.fileno(), False)
            selected.register(process.stdout, selectors.EVENT_READ, "stdout")
            if process.stdin is not None:
                os.set_blocking(process.stdin.fileno(), False)
                if input_bytes:
                    selected.register(process.stdin, selectors.EVENT_WRITE, "stdin")
                else:
                    process.stdin.close()
            while selected.get_map():
                remaining = deadline-time.monotonic()
                if remaining <= 0:
                    raise subprocess.TimeoutExpired(process.args, 0)
                for key, _ in selected.select(min(.1, remaining)):
                    if key.data == "stdin":
                        try:
                            count = os.write(key.fileobj.fileno(), input_bytes[written:written+65536])
                        except BlockingIOError:
                            continue
                        require(count > 0)
                        written += count
                        if written == len(input_bytes):
                            selected.unregister(key.fileobj)
                            key.fileobj.close()
                    else:
                        try:
                            block = os.read(key.fileobj.fileno(), 65536)
                        except BlockingIOError:
                            continue
                        if not block:
                            selected.unregister(key.fileobj)
                        else:
                            require(len(output)+len(block) <= cap)
                            output.extend(block)
            remaining = deadline-time.monotonic()
            if remaining <= 0:
                raise subprocess.TimeoutExpired(process.args, 0)
            process.wait(timeout=remaining)
        return bytes(output)


def join_process_group(process, deadline):
    """Boundedly reap the leader and terminate any retained build-group descendants.

    Shared compiler/MSBuild servers are disabled before spawn. A missing leader is
    insufficient: the selected process group must be absent before build output is
    accepted. Failure never becomes a later successful preparation receipt.
    """
    try:
        os.killpg(process.pid, signal.SIGKILL)
    except ProcessLookupError:
        pass
    except OSError:
        # A redundant kill can fail while the dead leader is still unreaped.
        # Always attempt its bounded reap; only the later kernel group-absence
        # check can establish cleanup after this error.
        pass
    try:
        remaining = deadline-time.monotonic()
        if remaining <= 0:
            return False
        process.wait(timeout=remaining)
    except (subprocess.TimeoutExpired, OSError, ValueError):
        return False
    while time.monotonic() < deadline:
        try:
            os.killpg(process.pid, 0)
        except ProcessLookupError:
            return True
        except OSError:
            return False
        remaining = deadline-time.monotonic()
        if remaining <= 0:
            return False
        time.sleep(min(.02, remaining))
    return False


def policy():
    """Closed actual coverage procedure, unchanged numeric gate and resource topology."""
    producer = {"Id": PRODUCER, "Kind": "coverage", "Version": "1.0.0", "RequiredResources": ["native-http"],
                "AssertionIds": [ASSERTION], "ArtifactSlots": [{"LogicalName": "coverage-report", "RelativeRoot": "merged",
                "MediaType": "application/xml", "MaximumBytes": 16*1024*1024, "Required": True}], "TimeoutSeconds": 180,
                "CoverageGate": {"MinLinePercent": 95, "MinBranchPercent": 85, "MinPatchLinePercent": None,
                "MinPatchBranchPercent": None, "PatchLineMode": "measurable", "TolerancePercent": 0.5}}
    profile = {"Id": PROFILE, "Scope": "Targeted", "Resources": [{"Id": "native-http", "Readiness": "aspire_health",
               "DeadlineSeconds": 45, "Requires": []}], "Producers": [producer], "Obligations": [{"Id": "qualified-risk",
               "RiskClass": "behavioral", "Rationale": "Exercise the actual restricted coverage procedure after kernel-authenticated readiness.",
               "RequiredProducerIds": [PRODUCER], "RequiredAssertionId": ASSERTION}]}
    return {"Id": "issue779-private-qualification", "Version": "1.0.0", "ConservativeProfileId": PROFILE,
            "Profiles": [profile], "Rules": [{"Id": "qualification-source", "Pattern": "tests/**", "ProfileId": PROFILE, "Precedence": 0}]}


def bundle_inventory(bundle, *, deadline=None):
    """Set finite immutable deployment roles/modes before catalogue canonicalization."""
    roles = {"AspireChild.dll": "AppHost", "AspireChild.runtimeconfig.json": "AppHostRuntimeConfiguration",
             "resource/NativeHttpResource.dll": "Resource", "resource/NativeHttpResource.runtimeconfig.json": "ResourceRuntimeConfiguration",
             "dcp/dcp": "Dcp", "proof-input/declared.txt": "DeclaredInput"}
    result = []
    for path in sorted(bundle.rglob("*")):
        require(deadline is None or time.monotonic() < deadline)
        require(not path.is_symlink())
        if path.is_dir():
            os.chmod(path, 0o755)
            continue
        info = path.lstat()
        require(stat.S_ISREG(info.st_mode) and info.st_nlink == 1 and 0 < info.st_size <= 128*1024*1024)
        relative = path.relative_to(bundle).as_posix()
        role = roles.get(relative, "DcpExtension" if relative.startswith("dcp/ext/") else
                         "DependencyManifest" if relative.endswith(".deps.json") else "Dependency")
        mode = 0o555 if role in ("Dcp", "DcpExtension") else 0o444
        os.chmod(path, mode)
        result.append({"RelativePath": relative, "Role": role, "LengthBytes": info.st_size, "Sha256": sha(path.read_bytes()), "Mode": mode})
        require(deadline is None or time.monotonic() < deadline)
    require(6 <= len(result) <= 256 and sum(row["LengthBytes"] for row in result) <= 512*1024*1024)
    dcp = bundle / "dcp/dcp"
    require(dcp.read_bytes()[:5] == b"\x7fELF\x02")
    for path in sorted(bundle.rglob("*"), reverse=True):
        require(deadline is None or time.monotonic() < deadline)
        if path.is_dir():
            os.chmod(path, 0o555)
    os.chmod(bundle, 0o555)
    require(deadline is None or time.monotonic() < deadline)
    return result


def seal_subject_tree(subject):
    """Seal the trusted copied snapshot before its protected preflight.

    Git tar entries can carry group-write bits even though the checked-out
    sources are read-only to untrusted accounts. Remove those bits explicitly;
    the later root preflight still verifies ownership and every frozen byte.
    This helper evaluates no project, package, test, or subject command.
    """
    require(subject.is_dir() and not subject.is_symlink())
    paths = list(subject.rglob("*"))
    for path in paths:
        info = path.lstat()
        require(not stat.S_ISLNK(info.st_mode)
                and (stat.S_ISDIR(info.st_mode) or stat.S_ISREG(info.st_mode)))
        if stat.S_ISREG(info.st_mode):
            require(info.st_nlink == 1)
    for path in paths:
        if path.is_file():
            os.chmod(path, 0o444)
    for path in reversed(paths):
        if path.is_dir():
            os.chmod(path, 0o555)
    os.chmod(subject, 0o555)


def expand(template, replacements):
    value = template.read_text()
    for marker, replacement in replacements.items():
        require(value.count(marker) == 1 and isinstance(replacement, str)
                and re.fullmatch(r"[A-Za-z0-9_./:@+\-=]+", replacement))
        value = value.replace(marker, replacement)
    require("__QUALIFICATION_" not in value and "{{" not in value)
    return value


def archive(root, revision, destination, runner):
    destination.mkdir(mode=0o700)
    tar_path = destination.parent / (destination.name + ".tar")
    runner.run(["git", "archive", "--format=tar", "--output="+str(tar_path), revision], root)
    runner.run(["tar", "-xf", str(tar_path), "-C", str(destination)], root)
    tar_path.unlink()


def retain_sdk_preflight_binding(workspace, source_commit, *, observe_host=True):
    """Persist data before reading host metadata; never build, seal or admit.

    ``prepare`` supplies its fresh root workspace. Portable controls select an
    owned metadata file only, and cannot exercise the root SDK sealing API.
    """
    sdk_initial = {"schema": "issue779-trusted-sdk-bootstrap-preflight-v1",
                   "root": str(SDK_ROOT), "host_before": None, "host_metadata_state": "unavailable"}
    binding_path = workspace / "build-binding.json"
    incomplete = {"source_commit": source_commit, "sdk_bootstrap": sdk_initial,
                  "preparation_complete": False}
    with binding_path.open("x") as stream:
        json.dump(incomplete, stream, sort_keys=True)
        stream.write("\n")
    os.chmod(binding_path, 0o600)
    if not observe_host:
        return
    try:
        sdk_initial["host_before"] = sdk_metadata((SDK_ROOT / "dotnet").lstat())
        sdk_initial["host_metadata_state"] = "observed"
    finally:
        # If lstat fails, preserve unavailable/null and propagate that failure.
        binding_path.write_text(json.dumps(incomplete, sort_keys=True) + "\n")


def validate_distribution_provenance(raw):
    """Parse only the fixed child installer's bounded complete provenance.

    Exact URL, SHA-512, SDK/RID/root and closed fields bind the installer result.
    This metadata is not the SDK audit, process ownership or qualification proof.
    Runner.run must already have confirmed actual zero exit and group absence.
    """
    require(type(raw) is bytes and 0 < len(raw) <= 4096)
    try:
        value = unique_json(raw)
    except (ValueError, UnicodeError):
        raise PreparationFailure('qualification-preparation-rejected') from None
    expected = {'schema', 'sdk_version', 'rid', 'root', 'archive_url', 'archive_sha512',
                'compressed_bytes', 'expanded_bytes', 'node_count', 'explicit_member_count',
                'tree_sha256', 'gnu_longname_headers', 'complete', 'sdk_audit_completed', 'qualification_claim'}
    require(type(value) is dict and set(value) == expected)
    require(value['schema'] == 'issue779-pinned-sdk-distribution-v1'
            and value['sdk_version'] == SDK_VERSION and value['rid'] == 'linux-x64'
            and value['root'] == str(SDK_ROOT) and value['archive_url'] == ARCHIVE_URL
            and value['archive_sha512'] == ARCHIVE_SHA512
            and value['complete'] is True and value['sdk_audit_completed'] is False
            and value['qualification_claim'] is False)
    for key, bound in (('compressed_bytes', MAX_ARCHIVE_BYTES), ('expanded_bytes', MAX_TOTAL_BYTES),
                       ('node_count', MAX_NODES), ('explicit_member_count', MAX_NODES)):
        require(type(value[key]) is int and 0 < value[key] <= bound)
    require(value['explicit_member_count'] <= value['node_count']
            and type(value['gnu_longname_headers']) is int
            and 0 <= value['gnu_longname_headers'] <= value['explicit_member_count']
            and type(value['tree_sha256']) is str
            and re.fullmatch(r'[0-9a-f]{64}', value['tree_sha256']))
    return value


def record_installed_sdk_preflight(workspace, distribution, ancestors):
    """Extend the fresh incomplete private record after the child actually joins.

    Preserve publisher provenance and ancestor observations before the separate
    full audit starts. Host lstat failure retains unavailable/null and propagates.
    This file is owned by the root preparation workspace; no subject has run.
    """
    binding_path = workspace / 'build-binding.json'
    binding = unique_json(binding_path.read_bytes())
    require(binding.get('preparation_complete') is False)
    initial = binding['sdk_bootstrap']
    initial['distribution'] = distribution
    initial['preinstallation_ancestors'] = ancestors
    try:
        initial['host_before'] = sdk_metadata((SDK_ROOT / 'dotnet').lstat())
        initial['host_metadata_state'] = 'observed'
    finally:
        encoded = (json.dumps(binding, sort_keys=True) + '\n').encode()
        require(len(encoded) <= 4096)
        binding_path.write_bytes(encoded)


def bootstrap_sdk(source, workspace, source_commit, runner):
    """Own fixed installation, then audit the entire SDK before any build.

    Retain incomplete metadata before ancestor checks. Install with the fixed
    Python child argv under Runner's absolute 180-second group owner. Never call
    install in this process: its DNS/TLS/read operations require external kill
    and reap. Only accepted provenance and joined zero exit precede the unchanged
    full 120-second SDK audit. All later .NET commands use its canonical host.
    """
    retain_sdk_preflight_binding(workspace, source_commit, observe_host=False)
    diagnostic = SdkDiagnostic()
    try:
        ancestors = protect_sdk_ancestors(min(runner.deadline, time.monotonic() + 5), diagnostic=diagnostic)
        installer = source / 'tests/evidencehost-consumer/PrivateQualification/trusted_sdk_distribution.py'
        raw = runner.run(['/usr/bin/python3', '-B', str(installer), '--install'], source,
                         capture=True, env=dict(os.environ, PATH=SDK_PATH))
        distribution = validate_distribution_provenance(raw)
        record_installed_sdk_preflight(workspace, distribution, ancestors)
        diagnostic = SdkDiagnostic()
        dotnet, binding = seal_trusted_sdk(min(runner.deadline, time.monotonic() + 120), diagnostic=diagnostic)
        binding['distribution'] = distribution
        binding['preinstallation_ancestors'] = ancestors
        return dotnet, binding
    except BaseException:
        retain_sdk_failure_diagnostic(workspace, diagnostic)
        raise


def retain_sdk_failure_diagnostic(workspace, diagnostic):
    """Best-effort atomic update of the existing incomplete binding, <=4096 bytes.

    Only the owning preparation procedure calls this after bootstrap failure.
    The already retained host-before record is preserved. Capture failure returns
    false and cannot replace the original exception or mark preparation complete.
    No SDK paths or arbitrary exception material enter the closed diagnostic.
    """
    parent = source = target = None
    temporary = '.build-binding-sdk-diagnostic.tmp'
    created = False
    try:
        require(type(diagnostic) is SdkDiagnostic and diagnostic.failure is not None)
        parent = os.open(workspace, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
        parent_info = os.fstat(parent)
        require(parent_info.st_uid == 0 and not parent_info.st_mode & 0o022)
        source = os.open('build-binding.json', os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK | os.O_CLOEXEC,
                         dir_fd=parent)
        info = os.fstat(source)
        require(stat.S_ISREG(info.st_mode) and info.st_uid == 0 and info.st_nlink == 1
                and stat.S_IMODE(info.st_mode) == 0o600 and 0 <= info.st_size <= 4096)
        raw = os.read(source, 4097)
        require(len(raw) == info.st_size)
        binding = unique_json(raw)
        require(binding.get('preparation_complete') is False and type(binding.get('sdk_bootstrap')) is dict)
        binding['sdk_bootstrap']['diagnostic'] = diagnostic.snapshot()
        encoded = (json.dumps(binding, sort_keys=True) + '\n').encode()
        require(len(encoded) <= 4096)
        require(sdk_identity(os.fstat(source)) == sdk_identity(info)
                and sdk_identity(os.stat('build-binding.json', dir_fd=parent, follow_symlinks=False)) == sdk_identity(info))
        target = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC,
                         0o600, dir_fd=parent)
        created = True
        require(os.write(target, encoded) == len(encoded))
        after = os.fstat(target)
        require(after.st_uid == 0 and after.st_nlink == 1 and stat.S_IMODE(after.st_mode) == 0o600)
        require(sdk_identity(os.stat('build-binding.json', dir_fd=parent, follow_symlinks=False)) == sdk_identity(info))
        os.replace(temporary, 'build-binding.json', src_dir_fd=parent, dst_dir_fd=parent)
        created = False
        return True
    except BaseException:
        return False
    finally:
        for fd in (target, source):
            if fd is not None:
                try:
                    os.close(fd)
                except BaseException:
                    pass
        if created and parent is not None:
            try:
                os.unlink(temporary, dir_fd=parent)
            except BaseException:
                pass
        if parent is not None:
            try:
                os.close(parent)
            except BaseException:
                pass


def write_complete_build_binding(workspace, result, deadline):
    """Atomically replace incomplete private build data; reject expiry after close.

    This data writer has no runtime completion or admission authority. It verifies
    the existing root-procedure preflight record before replacing it. Late close
    retains diagnostic bytes but propagates failure to the owning preparation;
    filesystem blocking still requires the controller's external process owner.
    """
    require(time.monotonic() < deadline)
    raw = (json.dumps(result, sort_keys=True, indent=2)+"\n").encode()
    require(len(raw) <= 1024*1024)
    target = workspace / "build-binding.json"
    selected = target.lstat()
    require(stat.S_ISREG(selected.st_mode) and selected.st_uid == os.geteuid()
            and selected.st_nlink == 1 and stat.S_IMODE(selected.st_mode) == 0o600
            and selected.st_size <= 4096)
    fd = os.open(target, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK | os.O_CLOEXEC)
    try:
        require(sdk_identity(os.fstat(fd)) == sdk_identity(selected))
        before = os.read(fd, 4097)
        require(len(before) == selected.st_size and not os.read(fd, 1)
                and unique_json(before).get("preparation_complete") is False
                and sdk_identity(os.fstat(fd)) == sdk_identity(selected)
                == sdk_identity(target.stat(follow_symlinks=False)))
    finally:
        os.close(fd)
    require(time.monotonic() < deadline)
    temporary = workspace / ".build-binding-complete.tmp"
    with temporary.open("xb") as stream:
        os.fchmod(stream.fileno(), 0o600)
        stream.write(raw)
    require(time.monotonic() < deadline)
    require(sdk_identity(target.stat(follow_symlinks=False)) == sdk_identity(selected))
    os.replace(temporary, target)
    require(time.monotonic() < deadline)


def prepare_product_tool_directory(workspace, entry, deadline, *, expected_owner_uid=0):
    """Create one fresh private publish directory under the retained workspace.

    Production selects UID0 and the original Runner deadline. Explicit fd chmod
    makes mode 0700 independent of umask before any publish can create output.
    Existing names, links and unsafe workspace metadata are rejected, never fixed.
    The optional UID is file-data testing only; it supplies no worker authority.
    Final mode 0755 remains the later sealing step after replacement and auditing.
    All retained descriptors close before return, within the original deadline.
    """
    require(type(expected_owner_uid) is int and expected_owner_uid >= 0
            and type(entry) is str and entry in ("cli", "host")
            and type(deadline) in (int, float) and 0 < deadline < float("inf"))
    workspace = Path(workspace)
    require(workspace.is_absolute() and ".." not in workspace.parts
            and time.monotonic() < deadline)
    selected = workspace.lstat()
    require(stat.S_ISDIR(selected.st_mode) and selected.st_uid == expected_owner_uid
            and not selected.st_mode & 0o022)
    directory_identity = lambda row: (row.st_dev, row.st_ino, row.st_mode, row.st_uid, row.st_gid)
    owned, error, result = [], None, None
    try:
        parent_fd = os.open(workspace, os.O_RDONLY | os.O_DIRECTORY | os.O_NONBLOCK | os.O_NOFOLLOW | os.O_CLOEXEC)
        owned.append(parent_fd)
        require(directory_identity(selected) == directory_identity(os.fstat(parent_fd))
                and time.monotonic() < deadline)
        name = "tool-"+entry
        os.mkdir(name, mode=0o700, dir_fd=parent_fd)
        created = os.stat(name, dir_fd=parent_fd, follow_symlinks=False)
        require(stat.S_ISDIR(created.st_mode) and created.st_uid == expected_owner_uid
                and time.monotonic() < deadline)
        # A restrictive umask can remove even owner read/search. Set the mode
        # through the retained protected parent before opening the new child;
        # no existing or followed link is eligible for this chmod.
        os.chmod(name, 0o700, dir_fd=parent_fd, follow_symlinks=False)
        named = os.stat(name, dir_fd=parent_fd, follow_symlinks=False)
        require(stat.S_ISDIR(named.st_mode) and named.st_uid == expected_owner_uid
                and named.st_dev == created.st_dev and named.st_ino == created.st_ino
                and stat.S_IMODE(named.st_mode) == 0o700 and time.monotonic() < deadline)
        tool_fd = os.open(name, os.O_RDONLY | os.O_DIRECTORY | os.O_NONBLOCK | os.O_NOFOLLOW | os.O_CLOEXEC,
                          dir_fd=parent_fd)
        owned.append(tool_fd)
        require(directory_identity(named) == directory_identity(os.fstat(tool_fd))
                and time.monotonic() < deadline)
        os.fchmod(tool_fd, 0o700)
        final = os.fstat(tool_fd)
        require(stat.S_ISDIR(final.st_mode) and final.st_uid == expected_owner_uid
                and final.st_dev == created.st_dev and final.st_ino == created.st_ino
                and stat.S_IMODE(final.st_mode) == 0o700
                and directory_identity(final) == directory_identity(os.stat(name, dir_fd=parent_fd, follow_symlinks=False))
                and directory_identity(selected) == directory_identity(os.fstat(parent_fd))
                == directory_identity(workspace.lstat()) and time.monotonic() < deadline)
        result = workspace / name
    except BaseException as failure:
        error = failure
    finally:
        for fd in reversed(owned):
            try:
                os.close(fd)
            except BaseException as failure:
                if error is None:
                    error = failure
    if error is not None:
        raise error
    require(time.monotonic() < deadline)
    return result


def prepare_application_bundle_input(workspace, deadline, *, expected_owner_uid=0, expected_owner_gid=0):
    """Relocate the fresh build into one protected sibling before canonicalization.

    Returns (bundle_path, provenance). Existing names are rejected; no payload is
    copied into a tool. The outer container remains 0700, its application parent
    becomes 0555, and bundle_inventory later seals the build tree to 0555/0444.
    Owner overrides test file data only. Every FD closes on the original deadline;
    partial failure remains private for diagnosis and is never repaired/fallback.
    """
    workspace = Path(workspace)
    require(workspace.is_absolute() and ".." not in workspace.parts
            and type(expected_owner_uid) is int and expected_owner_uid >= 0
            and type(expected_owner_gid) is int and expected_owner_gid >= 0
            and type(deadline) in (int, float) and 0 < deadline < float("inf")
            and time.monotonic() < deadline)
    identity = lambda row: (row.st_dev, row.st_ino, row.st_uid, row.st_gid, stat.S_IMODE(row.st_mode))
    owned, error, result = [], None, None
    try:
        named_workspace = workspace.lstat()
        require(stat.S_ISDIR(named_workspace.st_mode) and named_workspace.st_uid == expected_owner_uid
                and named_workspace.st_gid == expected_owner_gid and not named_workspace.st_mode & 0o022)
        parent = os.open(workspace, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
        owned.append(parent)
        require(identity(named_workspace) == identity(os.fstat(parent)))
        pins = []
        container = parent
        for name, mode in (("application-bundle-input", 0o700), (APP_ID, 0o755)):
            require(time.monotonic() < deadline)
            os.mkdir(name, mode=0o700, dir_fd=parent)
            created = os.stat(name, dir_fd=parent, follow_symlinks=False)
            require(stat.S_ISDIR(created.st_mode) and created.st_uid == expected_owner_uid)
            # Only the fresh name in a retained protected parent is eligible.
            os.chmod(name, 0o700, dir_fd=parent, follow_symlinks=False)
            named = os.stat(name, dir_fd=parent, follow_symlinks=False)
            require(identity(named)[:2] == identity(created)[:2] and stat.S_ISDIR(named.st_mode)
                    and named.st_uid == expected_owner_uid and stat.S_IMODE(named.st_mode) == 0o700)
            child = os.open(name, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=parent)
            owned.append(child)
            require(identity(named) == identity(os.fstat(child)))
            os.fchown(child, expected_owner_uid, expected_owner_gid)
            os.fchmod(child, mode)
            final = os.fstat(child)
            require(identity(final) == identity(os.stat(name, dir_fd=parent, follow_symlinks=False))
                    and final.st_uid == expected_owner_uid and final.st_gid == expected_owner_gid
                    and stat.S_IMODE(final.st_mode) == mode and final.st_dev == named_workspace.st_dev)
            pins.append((parent, child, name, identity(final)))
            parent = child
            if name == "application-bundle-input":
                container = child
        require(time.monotonic() < deadline)
        named_bundle = os.stat("bundle", dir_fd=owned[0], follow_symlinks=False)
        require(stat.S_ISDIR(named_bundle.st_mode) and named_bundle.st_uid == expected_owner_uid
                and named_bundle.st_gid == expected_owner_gid)
        bundle_fd = os.open("bundle", os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=owned[0])
        owned.append(bundle_fd)
        require(identity(named_bundle) == identity(os.fstat(bundle_fd)))
        # This is the root's fresh copied build, still before canonical sealing.
        # A publisher's inherited group-write mode cannot escape the 0700 outer.
        os.fchmod(bundle_fd, 0o755)
        named_bundle = os.fstat(bundle_fd)
        require(identity(named_bundle) == identity(os.stat("bundle", dir_fd=owned[0], follow_symlinks=False)))
        # The application directory was created exclusively above and is empty.
        require(os.listdir(parent) == [] and time.monotonic() < deadline)
        os.rename("bundle", BUILD_ID, src_dir_fd=owned[0], dst_dir_fd=parent)
        require(identity(named_bundle) == identity(os.stat(BUILD_ID, dir_fd=parent, follow_symlinks=False))
                == identity(os.fstat(bundle_fd)))
        os.fchmod(parent, 0o555)
        pins[-1] = (pins[-1][0], parent, APP_ID, identity(os.fstat(parent)))
        for ancestor, child, name, expected in pins:
            require(expected == identity(os.fstat(child))
                    == identity(os.stat(name, dir_fd=ancestor, follow_symlinks=False)))
        require(identity(named_workspace) == identity(os.fstat(owned[0])) == identity(workspace.lstat()))
        info = os.fstat(container)
        outer = workspace / "application-bundle-input"
        result = (outer / APP_ID / BUILD_ID, {"container_path": str(outer), "path": str(outer / APP_ID / BUILD_ID),
            "container_metadata": {"uid": info.st_uid, "gid": info.st_gid, "mode": format(stat.S_IMODE(info.st_mode), "04o"),
                                   "device": info.st_dev, "inode": info.st_ino, "nlink": info.st_nlink}})
    except BaseException as failure:
        error = failure
    finally:
        for fd in reversed(owned):
            try:
                os.close(fd)
            except BaseException as failure:
                if error is None:
                    error = failure
    if error is not None:
        raise error
    require(time.monotonic() < deadline)
    return result


def measure_product_tool_inventory(tool, coverage_module, deadline, *, expected_owner_uid=0, expected_owner_gid=0):
    """Return (complete_sha_map, observed_summary) using the existing tool bounds.

    No files are excluded and no bundle override is supplied. All files retain
    32 MiB, the tree 256 MiB/2048 entries/depth eight. Measurements are real pinned
    file data, not a fit prediction or runtime authority. The original deadline
    includes both snapshots, streaming verification and descriptor closure.
    """
    files, directories = coverage_module.snapshot_tree(tool, deadline, uid=expected_owner_uid, gid=expected_owner_gid)
    total, maximum = 0, 0
    for relative, row in files.items():
        path = tool / relative
        parent = coverage_module.open_directory(path.parent, deadline, uid=expected_owner_uid, gid=expected_owner_gid)
        try:
            digest, info = coverage_module.hash_published_file(parent, path.name, deadline,
                uid=expected_owner_uid, gid=expected_owner_gid, mode=int(row["mode"], 8),
                cap=min(coverage_module.FILE_LIMIT, coverage_module.TREE_LIMIT-total))
            require(digest == row["sha256"])
            total += info[6]
            maximum = max(maximum, info[6])
        finally:
            os.close(parent)
        require(time.monotonic() < deadline)
    require((files, directories) == coverage_module.snapshot_tree(tool, deadline, uid=expected_owner_uid, gid=expected_owner_gid)
            and time.monotonic() < deadline)
    summary = {"file_count": len(files), "directory_count": len(directories), "total_bytes": total,
               "maximum_file_bytes": maximum,
               "maximum_depth": max((len(Path(name).parts) for name in (*files, *directories) if name), default=0),
               "file_limit_bytes": coverage_module.FILE_LIMIT, "tree_limit_bytes": coverage_module.TREE_LIMIT,
               "maximum_entries": coverage_module.MAX_FILES}
    return {name: row["sha256"] for name, row in files.items()}, summary


PRIVATE_PUBLISH_CHECKPOINTS = frozenset(("post-refresh-snapshot", "lock-comparison", "cli-rid-target", "protected-lock-check"))
PRIVATE_PUBLISH_CATEGORIES = frozenset(("JSON", "schema", "version", "original-group", "row", "newRIDgroup", "unknownRIDnode", "RIDrow", "pathset", "metadata", "bounds", "deadline", "unknown"))
PRIVATE_PUBLISH_FIELDS = frozenset(("type", "requested", "resolved", "contentHash", "dependencies", "row-set", "other"))
PRIVATE_PUBLISH_PACKAGE_TYPES = frozenset(("Direct", "Transitive", "CentralTransitive", "Project"))


class PrivatePublishDiagnostic:
    """Closed failure observations only; no command, filesystem or acceptance API."""

    def __init__(self):
        self.value = {"schema": "issue779-private-linux-publish-failure-v1", "checkpoint": None,
            "category": "unknown", "lock_ordinal": None, "expected_lock_count": None,
            "before_sha256": None, "after_sha256": None, "row_sha256": None,
            "before_version": None, "after_version": None, "before_groups": None, "after_groups": None,
            "original_group_identical": None, "fields": [], "package_type": None,
            "resolved_equal": None, "content_hash_equal": None, "dependencies_equal": None}

    def snapshot(self):
        value = dict(self.value)
        require(set(value) == set(PrivatePublishDiagnostic().value)
                and value["schema"] == "issue779-private-linux-publish-failure-v1"
                and value["checkpoint"] in PRIVATE_PUBLISH_CHECKPOINTS
                and value["category"] in PRIVATE_PUBLISH_CATEGORIES)
        for name, maximum in (("lock_ordinal", PRIVATE_LOCK_COUNT-1), ("expected_lock_count", PRIVATE_LOCK_COUNT),
                              ("before_version", 3), ("after_version", 3), ("before_groups", 64), ("after_groups", 64)):
            require(value[name] is None or type(value[name]) is int and 0 <= value[name] <= maximum)
        for name in ("before_sha256", "after_sha256", "row_sha256"):
            require(value[name] is None or type(value[name]) is str and re.fullmatch(r"[0-9a-f]{64}", value[name]))
        for name in ("original_group_identical", "resolved_equal", "content_hash_equal", "dependencies_equal"):
            require(value[name] is None or type(value[name]) is bool)
        require(type(value["fields"]) is list and len(value["fields"]) <= len(PRIVATE_PUBLISH_FIELDS)
                and all(type(name) is str and name in PRIVATE_PUBLISH_FIELDS for name in value["fields"])
                and len(set(value["fields"])) == len(value["fields"])
                and (value["package_type"] is None or value["package_type"] in PRIVATE_PUBLISH_PACKAGE_TYPES))
        value["fields"] = list(value["fields"])
        require(len(json.dumps(value, sort_keys=True, separators=(",", ":"), allow_nan=False).encode()) <= 1024)
        return value


def _publish_note(diagnostic, category, **fields):
    """Best effort bookkeeping must never replace an existing rejection."""
    try:
        if type(diagnostic) is PrivatePublishDiagnostic:
            diagnostic.value.update(fields, category=category)
    except BaseException:
        pass


def _publish_row_difference(diagnostic, before, after, category):
    """Describe the first differing row using hashes and closed field names only."""
    try:
        for name in sorted(set(before) | set(after)):
            old, new = before.get(name), after.get(name)
            canonical = lambda row: json.dumps(row, sort_keys=True, separators=(",", ":"), allow_nan=False)
            if canonical(old) == canonical(new):
                continue
            fields = ["row-set"] if old is None or new is None else sorted(
                field if field in PRIVATE_PUBLISH_FIELDS else "other"
                for field in set(old) | set(new) if canonical(old.get(field)) != canonical(new.get(field)))
            row = new if type(new) is dict else {}
            _publish_note(diagnostic, category, row_sha256=sha(name.encode()), fields=sorted(set(fields)),
                package_type=row.get("type") if type(row.get("type")) is str and row["type"] in PRIVATE_PUBLISH_PACKAGE_TYPES else None,
                **{key: (canonical(old.get(field)) == canonical(new.get(field))
                    if type(old) is dict and type(new) is dict else None)
                    for key, field in (("resolved_equal", "resolved"), ("content_hash_equal", "contentHash"),
                                       ("dependencies_equal", "dependencies"))})
            break
    except BaseException:
        pass


def retain_private_publish_failure(workspace, source_commit, diagnostic, deadline, *, expected_owner_uid=0):
    """Append one <=1 KiB observation to the existing SDK partial binding.

    This ordinary-file procedure returns false on every capture failure. The
    default UID is root; the override is a portable metadata seam, not authority.
    No new clock is created. Only the exact incomplete SDK binding is replaced.
    """
    parent = source = target = None
    made = None
    temporary = ".build-binding-linux-publish-diagnostic.tmp"
    published = close_failed = False
    try:
        require(type(diagnostic) is PrivatePublishDiagnostic and type(expected_owner_uid) is int
                and expected_owner_uid >= 0 and type(source_commit) is str
                and re.fullmatch(r"[0-9a-f]{40}", source_commit) and time.monotonic() < deadline)
        observation = diagnostic.snapshot()
        parent = _open_publish_metadata_directory(workspace, deadline, expected_owner_uid)
        parent_info = os.fstat(parent)
        source = os.open("build-binding.json", os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK | os.O_CLOEXEC, dir_fd=parent)
        before = os.fstat(source)
        require(stat.S_ISREG(before.st_mode) and before.st_uid == expected_owner_uid and before.st_nlink == 1
                and stat.S_IMODE(before.st_mode) == 0o600 and 0 < before.st_size <= 4096
                and before.st_dev == parent_info.st_dev and time.monotonic() < deadline)
        raw = os.read(source, 4097)
        require(len(raw) == before.st_size and os.read(source, 1) == b""
                and _publish_metadata_identity(before) == _publish_metadata_identity(os.fstat(source))
                == _publish_metadata_identity(os.stat("build-binding.json", dir_fd=parent, follow_symlinks=False)))
        binding = unique_json(raw)
        require(type(binding) is dict and set(binding) == {"source_commit", "preparation_complete", "sdk_bootstrap"}
                and binding["source_commit"] == source_commit and binding["preparation_complete"] is False)
        sdk = binding["sdk_bootstrap"]
        require(type(sdk) is dict and set(sdk) == {"schema", "root", "host_before", "host_metadata_state", "distribution", "preinstallation_ancestors"}
                and sdk["schema"] == "issue779-trusted-sdk-bootstrap-preflight-v1" and sdk["root"] == str(SDK_ROOT)
                and sdk["host_metadata_state"] == "observed" and type(sdk["host_before"]) is dict
                and type(sdk["preinstallation_ancestors"]) is dict)
        validate_distribution_provenance(json.dumps(sdk["distribution"], allow_nan=False).encode())
        binding["private_linux_publish_failure"] = observation
        encoded = (json.dumps(binding, sort_keys=True, separators=(",", ":"), allow_nan=False)+"\n").encode()
        require(len(encoded) <= 4096 and time.monotonic() < deadline
                and (os.fstat(parent).st_dev, os.fstat(parent).st_ino, os.fstat(parent).st_mode, os.fstat(parent).st_uid, os.fstat(parent).st_gid)
                == (parent_info.st_dev, parent_info.st_ino, parent_info.st_mode, parent_info.st_uid, parent_info.st_gid)
                and _publish_metadata_identity(os.fstat(parent)) == _publish_metadata_identity(Path(workspace).lstat())
                and _publish_metadata_identity(before) == _publish_metadata_identity(os.fstat(source))
                == _publish_metadata_identity(os.stat("build-binding.json", dir_fd=parent, follow_symlinks=False)))
        target = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC, 0o600, dir_fd=parent)
        made = os.fstat(target)
        require(stat.S_ISREG(made.st_mode) and made.st_uid == expected_owner_uid and made.st_gid == before.st_gid and made.st_nlink == 1
                and stat.S_IMODE(made.st_mode) == 0o600 and time.monotonic() < deadline)
        require(os.write(target, encoded) == len(encoded))
        os.fsync(target)
        after = os.fstat(target)
        require(after.st_size == len(encoded) and after.st_uid == expected_owner_uid and after.st_gid == before.st_gid and after.st_nlink == 1
                and stat.S_IMODE(after.st_mode) == 0o600
                and (after.st_dev, after.st_ino) == (made.st_dev, made.st_ino)
                and _publish_metadata_identity(after) == _publish_metadata_identity(os.stat(temporary, dir_fd=parent, follow_symlinks=False))
                and _publish_metadata_identity(before) == _publish_metadata_identity(os.fstat(source))
                == _publish_metadata_identity(os.stat("build-binding.json", dir_fd=parent, follow_symlinks=False))
                and (os.fstat(parent).st_dev, os.fstat(parent).st_ino, os.fstat(parent).st_mode, os.fstat(parent).st_uid, os.fstat(parent).st_gid)
                == (parent_info.st_dev, parent_info.st_ino, parent_info.st_mode, parent_info.st_uid, parent_info.st_gid)
                and _publish_metadata_identity(os.fstat(parent)) == _publish_metadata_identity(Path(workspace).lstat())
                and time.monotonic() < deadline)
        os.replace(temporary, "build-binding.json", src_dir_fd=parent, dst_dir_fd=parent)
        made = None
        os.fsync(parent)
        require(_publish_metadata_identity(os.fstat(target)) == _publish_metadata_identity(
                os.stat("build-binding.json", dir_fd=parent, follow_symlinks=False)) and time.monotonic() < deadline)
        published = True
    except BaseException:
        pass
    finally:
        # Delete only the sibling still proven to be our held file, never a replacement.
        if made is not None and parent is not None and target is not None:
            try:
                held = os.fstat(target)
                named = os.stat(temporary, dir_fd=parent, follow_symlinks=False)
                if (held.st_dev, held.st_ino) == (made.st_dev, made.st_ino) == (named.st_dev, named.st_ino):
                    os.unlink(temporary, dir_fd=parent)
            except BaseException:
                pass
        for fd in (target, source, parent):
            if fd is not None:
                try:
                    os.close(fd)
                except BaseException:
                    close_failed = True
    try:
        return published and not close_failed and time.monotonic() < deadline
    except BaseException:
        return False


def _publish_metadata_identity(info):
    return (info.st_dev, info.st_ino, info.st_mode, info.st_uid, info.st_gid,
            info.st_nlink, info.st_size, info.st_mtime_ns, info.st_ctime_ns)


def _close_publish_metadata_fd(fd):
    """Close a borrowed metadata procedure's owned FD without replacing failure."""
    pending = sys.exc_info()[0] is not None
    try:
        os.close(fd)
    except BaseException:
        if not pending:
            raise PreparationFailure("qualification-preparation-rejected") from None


def _open_publish_metadata_directory(path, deadline, expected_owner_uid):
    """Pin every ancestor without following links; return one owned leaf FD."""
    path = Path(path)
    require(path.is_absolute() and ".." not in path.parts and time.monotonic() < deadline)
    fd = os.open("/", os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
    try:
        for name in path.parts[1:]:
            require(time.monotonic() < deadline)
            child = os.open(name, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=fd)
            try:
                _close_publish_metadata_fd(fd)
            except BaseException:
                _close_publish_metadata_fd(child)
                raise
            fd = child
        info = os.fstat(fd)
        require(info.st_uid == expected_owner_uid and not info.st_mode & 0o022
                and _publish_metadata_identity(info) == _publish_metadata_identity(path.lstat()))
        result, fd = fd, None
        return result
    finally:
        if fd is not None:
            _close_publish_metadata_fd(fd)


def _read_publish_metadata(parent, name, deadline, cap, expected_owner_uid):
    """Read only a pinned owned single-link file, with the cap checked first."""
    require(time.monotonic() < deadline and type(cap) is int and cap >= 0)
    fd = os.open(name, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK | os.O_CLOEXEC, dir_fd=parent)
    try:
        before = os.fstat(fd)
        require(stat.S_ISREG(before.st_mode) and before.st_nlink == 1
                and before.st_uid == expected_owner_uid and before.st_dev == os.fstat(parent).st_dev
                and 0 < before.st_size <= cap)
        data = bytearray()
        while len(data) < before.st_size:
            require(time.monotonic() < deadline)
            block = os.read(fd, min(65536, before.st_size-len(data)))
            require(bool(block))
            data.extend(block)
        require(os.read(fd, 1) == b"" and _publish_metadata_identity(before) == _publish_metadata_identity(os.fstat(fd))
                == _publish_metadata_identity(os.stat(name, dir_fd=parent, follow_symlinks=False)))
    finally:
        _close_publish_metadata_fd(fd)
    require(time.monotonic() < deadline)
    return bytes(data)


def snapshot_private_publish_locks(root, deadline, expected_paths=None, *, expected_owner_uid=0, diagnostic=None):
    """Read every *.lock.json through retained FDs; reject changed path sets.

    Metadata only: 256 locks, 256 KiB each, 8 MiB combined; traversal charges
    32768 entries before sorting and permits depth sixteen. UID override is an
    ordinary-file data seam. Source archive modes below the protected root are
    not changed. No links or nonregular entries are accepted anywhere scanned.
    """
    require(type(expected_owner_uid) is int and expected_owner_uid >= 0)
    if expected_paths is not None:
        require(type(expected_paths) is tuple and 0 < len(expected_paths) <= PRIVATE_LOCK_COUNT
                and all(type(p) is str and p.endswith(".lock.json") and not p.startswith("/")
                        and all(s not in ("", ".", "..") for s in p.split("/")) for p in expected_paths)
                and len(set(expected_paths)) == len(expected_paths))
    locks, charged, total = {}, 0, 0
    _publish_note(diagnostic, "deadline" if time.monotonic() >= deadline else "metadata")
    root_fd = _open_publish_metadata_directory(root, deadline, expected_owner_uid)
    def walk(fd, prefix, depth):
        nonlocal charged, total
        _publish_note(diagnostic, "bounds" if depth > 16 else "deadline" if time.monotonic() >= deadline else "metadata")
        require(depth <= 16 and time.monotonic() < deadline)
        directory = os.fstat(fd)
        require(directory.st_uid == expected_owner_uid)
        names = []
        with os.scandir(fd) as entries:
            for entry in entries:
                _publish_note(diagnostic, "bounds" if charged >= PRIVATE_LOCK_SCAN_ENTRIES else "deadline" if time.monotonic() >= deadline else "metadata")
                require(time.monotonic() < deadline and charged < PRIVATE_LOCK_SCAN_ENTRIES)
                charged += 1
                names.append(entry.name)
        for name in sorted(names):
            require(time.monotonic() < deadline)
            relative = name if not prefix else prefix+"/"+name
            info = os.stat(name, dir_fd=fd, follow_symlinks=False)
            require(info.st_uid == expected_owner_uid)
            if stat.S_ISDIR(info.st_mode):
                child = os.open(name, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=fd)
                try:
                    require(_publish_metadata_identity(info) == _publish_metadata_identity(os.fstat(child)))
                    walk(child, relative, depth+1)
                    require(_publish_metadata_identity(info) == _publish_metadata_identity(os.stat(name, dir_fd=fd, follow_symlinks=False)))
                finally:
                    _close_publish_metadata_fd(child)
            else:
                require(stat.S_ISREG(info.st_mode))
                if name.endswith(".lock.json"):
                    _publish_note(diagnostic, "bounds" if len(locks) >= PRIVATE_LOCK_COUNT or info.st_size > min(PRIVATE_LOCK_BYTES, PRIVATE_LOCK_TOTAL-total) else "metadata")
                    require(len(locks) < PRIVATE_LOCK_COUNT)
                    data = _read_publish_metadata(fd, name, deadline,
                        min(PRIVATE_LOCK_BYTES, PRIVATE_LOCK_TOTAL-total), expected_owner_uid)
                    total += len(data)
                    locks[relative] = data
        require(_publish_metadata_identity(directory) == _publish_metadata_identity(os.fstat(fd)))
    try:
        walk(root_fd, "", 0)
        require(_publish_metadata_identity(os.fstat(root_fd)) == _publish_metadata_identity(Path(root).lstat()))
    finally:
        _close_publish_metadata_fd(root_fd)
    _publish_note(diagnostic, "deadline" if time.monotonic() >= deadline else "pathset" if expected_paths is not None and set(locks) != set(expected_paths) else "bounds")
    require(0 < len(locks) <= PRIVATE_LOCK_COUNT and time.monotonic() < deadline
            and (expected_paths is None or set(locks) == set(expected_paths)))
    return locks


def _private_publish_json(raw, maximum):
    require(type(raw) is bytes and 0 < len(raw) <= maximum)
    try:
        result = unique_json(raw)
        require(type(result) is dict)
        # Round-trip rejects non-JSON numbers and preserves scalar types for comparisons.
        json.dumps(result, allow_nan=False)
        return result
    except (ValueError, TypeError, RecursionError):
        raise PreparationFailure("qualification-preparation-rejected") from None


def validate_private_publish_lock(before_raw, after_raw, *, diagnostic=None):
    """Allow fixed Linux RID subsets of their exact inherited framework groups.

    Added keys must be nonempty RID-free original keys plus /linux-x64, without
    framework aliasing or normalization. Every original group and row remains
    identical; each added group uses only identical rows from its own framework.
    The return value still reports the exact CLI net10.0/linux-x64 target only.
    Unknown SDK/RID nodes fail closed; this is metadata, not binary or fit proof.
    """
    _publish_note(diagnostic, "JSON", before_sha256=sha(before_raw) if type(before_raw) is bytes and 0 < len(before_raw) <= PRIVATE_LOCK_BYTES else None,
                  after_sha256=sha(after_raw) if type(after_raw) is bytes and 0 < len(after_raw) <= PRIVATE_LOCK_BYTES else None, row_sha256=None, fields=[],
                  package_type=None, resolved_equal=None, content_hash_equal=None, dependencies_equal=None,
                  original_group_identical=None)
    if any(type(raw) is bytes and not 0 < len(raw) <= PRIVATE_LOCK_BYTES for raw in (before_raw, after_raw)):
        _publish_note(diagnostic, "bounds")
    before = _private_publish_json(before_raw, PRIVATE_LOCK_BYTES)
    after = _private_publish_json(after_raw, PRIVATE_LOCK_BYTES)
    _publish_note(diagnostic, "schema",
        **{prefix+"_version": document.get("version") if type(document.get("version")) is int and 0 <= document["version"] <= 3 else None
           for prefix, document in (("before", before), ("after", after))},
        **{prefix+"_groups": len(document["dependencies"]) if type(document.get("dependencies")) is dict and len(document["dependencies"]) <= 64 else None
           for prefix, document in (("before", before), ("after", after))})
    for document in (before, after):
        require(set(document) == {"version", "dependencies"} and type(document["version"]) is int
                and document["version"] in (1, 2, 3) and type(document["dependencies"]) is dict
                and len(document["dependencies"]) <= 64)
        for target, rows in document["dependencies"].items():
            require(type(target) is str and type(rows) is dict and len(rows) <= 8192
                    and all(type(name) is str and type(row) is dict for name, row in rows.items()))
    canonical = lambda value: json.dumps(value, sort_keys=True, separators=(",", ":"), allow_nan=False)
    _publish_note(diagnostic, "version")
    require(before["version"] == after["version"])
    old, new = before["dependencies"], after["dependencies"]
    # NuGet lock targets are per framework: derive only the one fixed RID from
    # exact inherited framework keys, never from a normalized alias or RID key.
    linux_groups = {framework+"/"+PRIVATE_PUBLISH_RID: framework for framework in old
                    if framework and "/" not in framework}
    added_groups = set(new)-set(old)
    _publish_note(diagnostic, "original-group" if not set(old) <= set(new) else "newRIDgroup")
    require(set(old) <= set(new) and added_groups <= set(linux_groups))
    for target, rows in old.items():
        _publish_note(diagnostic, "original-group", original_group_identical=canonical(rows) == canonical(new[target]))
        if canonical(rows) != canonical(new[target]):
            _publish_row_difference(diagnostic, rows, new[target], "row")
        require(canonical(rows) == canonical(new[target]))
    for target in sorted(added_groups):
        inherited = old[linux_groups[target]]
        for name, row in new[target].items():
            if name not in inherited:
                _publish_row_difference(diagnostic, {}, {name: row}, "unknownRIDnode")
            elif canonical(row) != canonical(inherited[name]):
                _publish_row_difference(diagnostic, {name: inherited[name]}, {name: row}, "RIDrow")
            require(name in inherited and canonical(row) == canonical(inherited[name]))
    try:
        if type(diagnostic) is PrivatePublishDiagnostic and diagnostic.value["checkpoint"] == "cli-rid-target" and PRIVATE_PUBLISH_TARGET not in new:
            _publish_note(diagnostic, "schema")
    except BaseException:
        pass
    return PRIVATE_PUBLISH_TARGET in new


def private_publish_assets(build, deadline, *, expected_owner_uid=0):
    """Require the actual fixed CLI assets RID target; return its hash binding."""
    owned = [_open_publish_metadata_directory(build, deadline, expected_owner_uid)]
    try:
        for name in (*Path(CLI_PROJECT).parent.parts, "obj"):
            parent = owned[-1]
            before = os.stat(name, dir_fd=parent, follow_symlinks=False)
            child = os.open(name, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=parent)
            owned.append(child)
            require(time.monotonic() < deadline and before.st_uid == expected_owner_uid
                    and _publish_metadata_identity(before) == _publish_metadata_identity(os.fstat(child))
                    == _publish_metadata_identity(os.stat(name, dir_fd=parent, follow_symlinks=False)))
        raw = _read_publish_metadata(owned[-1], "project.assets.json", deadline, PRIVATE_ASSETS_BYTES, expected_owner_uid)
        for index, name in enumerate((*Path(CLI_PROJECT).parent.parts, "obj")):
            require(_publish_metadata_identity(os.fstat(owned[index+1]))
                    == _publish_metadata_identity(os.stat(name, dir_fd=owned[index], follow_symlinks=False)))
        require(_publish_metadata_identity(os.fstat(owned[0])) == _publish_metadata_identity(Path(build).lstat()))
    finally:
        first = None
        pending = sys.exc_info()[0] is not None
        for fd in reversed(owned):
            try:
                os.close(fd)
            except BaseException as error:
                first = first or error
        if first is not None and not pending:
            raise PreparationFailure("qualification-preparation-rejected") from None
    document = _private_publish_json(raw, PRIVATE_ASSETS_BYTES)
    require(type(document.get("targets")) is dict and type(document["targets"].get(PRIVATE_PUBLISH_TARGET)) is dict
            and type(document.get("project")) is dict and type(document["project"].get("restore")) is dict
            and document["project"]["restore"].get("projectPath") == str(Path(build) / CLI_PROJECT)
            and time.monotonic() < deadline)
    return {"relative_path": str(Path(CLI_PROJECT).parent / "obj/project.assets.json"),
            "target": PRIVATE_PUBLISH_TARGET, "sha256": sha(raw), "bytes": len(raw)}


def prepare_private_linux_publish(build, source_files, protected_roots, runner, dotnet, *, diagnostic_workspace=None, source_commit=None):
    """Refresh only private build-copy RID locks, validate, then restore locked.

    protected_roots is the parent's fixed role->Path mapping for source, baseline,
    pristine product checkout and pristine product build. Their lock bytes/path
    sets must remain unchanged. All snapshots and commands use runner.deadline.
    """
    require(type(protected_roots) is dict and set(protected_roots) == {"source", "baseline", "product_source", "product_build"}
            and all(Path(build) != Path(root) and Path(build) not in Path(root).parents
                    and Path(root) not in Path(build).parents for root in protected_roots.values()))
    paths = tuple(sorted(name for name in source_files if name.endswith(".lock.json")))
    before = snapshot_private_publish_locks(build, runner.deadline, paths)
    require(all(sha(data) == source_files[name]["sha256"] for name, data in before.items()))
    for data in before.values():
        validate_private_publish_lock(data, data)
    def fingerprints(root):
        return {name: sha(data) for name, data in snapshot_private_publish_locks(root, runner.deadline).items()}
    protected = {role: fingerprints(root) for role, root in protected_roots.items()}
    cli = str(Path(build) / CLI_PROJECT)
    properties = list(PRIVATE_PUBLISH_PROPERTIES)
    runner.run([str(dotnet), "restore", cli, "--force-evaluate", "--use-lock-file",
                "-p:RestoreLockedMode=false"] + properties, build)
    diagnostic = PrivatePublishDiagnostic()
    try:
        _publish_note(diagnostic, "unknown", checkpoint="post-refresh-snapshot", expected_lock_count=len(paths))
        after = snapshot_private_publish_locks(build, runner.deadline, paths, diagnostic=diagnostic)
        _publish_note(diagnostic, "unknown", checkpoint="lock-comparison")
        for ordinal, name in enumerate(paths):
            _publish_note(diagnostic, "unknown", lock_ordinal=ordinal)
            validate_private_publish_lock(before[name], after[name], diagnostic=diagnostic)
        _publish_note(diagnostic, "unknown", checkpoint="cli-rid-target",
            lock_ordinal=paths.index(str(Path(CLI_PROJECT).parent / "packages.lock.json")))
        require(validate_private_publish_lock(before[str(Path(CLI_PROJECT).parent / "packages.lock.json")],
                                             after[str(Path(CLI_PROJECT).parent / "packages.lock.json")], diagnostic=diagnostic))
        _publish_note(diagnostic, "metadata", checkpoint="protected-lock-check", lock_ordinal=None,
            before_sha256=None, after_sha256=None, row_sha256=None, before_version=None, after_version=None,
            before_groups=None, after_groups=None, original_group_identical=None, fields=[], package_type=None,
            resolved_equal=None, content_hash_equal=None, dependencies_equal=None)
        require(protected == {role: fingerprints(root) for role, root in protected_roots.items()})
    except BaseException:
        # Every observation, including its clock read, is subordinate to the original error.
        try:
            if time.monotonic() >= runner.deadline:
                _publish_note(diagnostic, "deadline")
            if diagnostic_workspace is not None:
                retain_private_publish_failure(diagnostic_workspace, source_commit, diagnostic, runner.deadline)
        except BaseException:
            pass
        raise
    runner.run([str(dotnet), "restore", cli, "--locked-mode"] + properties, build)
    require(snapshot_private_publish_locks(build, runner.deadline, paths) == after
            and protected == {role: fingerprints(root) for role, root in protected_roots.items()})
    assets = private_publish_assets(build, runner.deadline)
    require(time.monotonic() < runner.deadline)
    return {"schema": "issue779-private-linux-publish-metadata-v1", "runtime_identifier": PRIVATE_PUBLISH_RID,
            "self_contained": False, "nuget_audit_enabled": True, "assets": assets,
            "locks": {name: {"before_sha256": sha(before[name]), "after_sha256": sha(after[name]),
                             "before_bytes": len(before[name]), "after_bytes": len(after[name])} for name in paths},
            "protected_locks": {role: {"count": len(rows), "sha256": sha(json.dumps(rows, sort_keys=True,
                separators=(",", ":")).encode())} for role, rows in protected.items()}}


def verify_private_linux_publish_metadata(build, metadata, protected_roots, deadline):
    """Reject publication-time lock/assets drift before the final build binding."""
    final_locks = snapshot_private_publish_locks(build, deadline, tuple(metadata["locks"]))
    require(all(sha(data) == metadata["locks"][name]["after_sha256"] for name, data in final_locks.items()))
    require(private_publish_assets(build, deadline) == metadata["assets"])
    for role, root in protected_roots.items():
        rows = {name: sha(data) for name, data in snapshot_private_publish_locks(root, deadline).items()}
        require(metadata["protected_locks"][role] == {"count": len(rows),
            "sha256": sha(json.dumps(rows, sort_keys=True, separators=(",", ":")).encode())})
    require(time.monotonic() < deadline)


def prepare(source, workspace, source_commit, run_id, workflow_identity):
    """Build both actual entries from the same frozen variant and exact compiled bindings."""
    require(os.geteuid() == 0 and re.fullmatch(r"[0-9a-f]{40}", source_commit)
            and re.fullmatch(r"[0-9]+/[0-9]+", run_id) and len(workflow_identity) <= 256)
    require(subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=source, text=True).strip() == source_commit)
    require(subprocess.check_output(["git", "status", "--porcelain"], cwd=source) == b"")
    before = source_inventory(source)
    logs = workspace / "build-logs"
    logs.mkdir(mode=0o700)
    runner = Runner(logs, time.monotonic()+1200)
    dotnet, sdk_binding = bootstrap_sdk(source, workspace, source_commit, runner)
    baseline, build = workspace / "baseline", workspace / "build"
    archive(source, BASELINE, baseline, runner)
    archive(source, source_commit, build, runner)
    # Root selects one fixed physical helper; no uploaded source, callback, or
    # path participates in product preparation or runtime authority.
    helper_path = build / "tests/evidencehost-consumer/PrivateQualification/prepare-product.py"
    helper_spec = importlib.util.spec_from_file_location("qualification_product_preparation", helper_path)
    product_helper = importlib.util.module_from_spec(helper_spec)
    helper_spec.loader.exec_module(product_helper)
    product = product_helper.build_product_inputs(source, workspace, runner, dotnet,
        source.parent / "issue779-product-source",
        source / "tests/evidencehost-consumer/PrivateQualification/product-source-manifest.json")
    inspector_project = build / "tests/evidencehost-consumer/ProductAbiInspector/ProductAbiInspector.csproj"
    runner.run([str(dotnet), "restore", str(inspector_project), "--locked-mode"], build)
    runner.run([str(dotnet), "build", str(inspector_project), "--no-restore", "-p:UseSharedCompilation=false"], build)
    inspector = inspector_project.parent / "bin/Debug/net10.0/ProductAbiInspector.dll"
    inspector_sha256 = product_helper.sha(product_helper.read_regular(inspector,
        product_helper.MAXIMUM_FILE, deadline=runner.deadline))
    package_root, package_binding = product_helper.prepare_coverlet_package(workspace, runner)
    taskhost_binding = product_helper.build_product_taskhost(build, workspace, runner, dotnet, package_root)
    metadata_project = build / "tests/evidencehost-consumer/PrivateQualificationMetadata/PrivateQualificationMetadata.csproj"
    prop = "-p:QualificationBaselineRoot="+str(baseline)
    runner.run([str(dotnet), "restore", str(metadata_project), "--locked-mode", prop], build)
    runner.run([str(dotnet), "build", str(metadata_project), "--no-restore", "-p:UseSharedCompilation=false", prop], build)
    app_project = build / "tests/evidencehost-consumer/AspireChild/AspireChild.csproj"
    runner.run([str(dotnet), "restore", str(app_project), "--locked-mode"], build)
    runner.run([str(dotnet), "build", str(app_project), "--no-restore", "-p:UseSharedCompilation=false"], build)
    bundle = workspace / "bundle"
    shutil.copytree(app_project.parent / "bin/Debug/net10.0", bundle)
    # Build output debug metadata is unnecessary for the closed application deployment.
    for path in bundle.rglob("*.pdb"):
        path.unlink()
    declared = bundle / "proof-input"
    declared.mkdir(mode=0o755)
    (declared / "declared.txt").write_bytes(b"declared-native-input\n")
    bundle, bundle_input = prepare_application_bundle_input(workspace, runner.deadline)
    files = bundle_inventory(bundle, deadline=runner.deadline)
    bundle_input["bundle_files_sha256"] = sha(json.dumps(files, sort_keys=True, separators=(",", ":")).encode())
    formatter = metadata_project.parent / "bin/Debug/net10.0/ForgeTrust.AppSurface.Cli.Tests.dll"
    metadata_raw = runner.run([str(dotnet), str(formatter)], build,
        input_bytes=json.dumps({"policy": policy(), "bundle_files": files}, separators=(",", ":")).encode(), capture=True)
    metadata = unique_json(metadata_raw)
    require(set(metadata) == {"entrybase64", "entry_digest", "catalogue_digest", "policybase64", "policy_sha256", "planbase64", "plan_digest"})
    subject = workspace / "subject"
    shutil.copytree(build / "tests/evidencehost-consumer/PrivateQualificationSubject", subject)
    for name in ("Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "NuGet.Config"):
        path = build / name
        if path.exists():
            shutil.copy2(path, subject / name)
    seal_subject_tree(subject)
    subject_map = {p.relative_to(subject).as_posix(): sha(p.read_bytes()) for p in sorted(subject.rglob("*")) if p.is_file()}
    subject_revision = sha(json.dumps(subject_map, sort_keys=True, separators=(",", ":")).encode())
    replacements = {"__QUALIFICATION_RUN_ID__": run_id, "__QUALIFICATION_WORKFLOW_IDENTITY__": workflow_identity,
        "__QUALIFICATION_SOURCE_COMMIT__": source_commit, "__QUALIFICATION_SUBJECT_REVISION__": subject_revision,
        "__QUALIFICATION_POLICY_SHA256__": metadata["policy_sha256"], "__QUALIFICATION_PLAN_DIGEST__": metadata["plan_digest"],
        "__QUALIFICATION_CANONICAL_PLAN_BASE64__": metadata["planbase64"], "__QUALIFICATION_ENTRY_DIGEST__": metadata["entry_digest"],
        "__QUALIFICATION_CATALOGUE_DIGEST__": metadata["catalogue_digest"]}
    templates = build / "tests/evidencehost-consumer/PrivateQualification"
    generated_contracts = build / "Evidence/ForgeTrust.AppSurface.Evidence.Contracts/EvidencePrivateQualificationBinding.cs"
    generated_contracts.write_text(expand(templates / "ContractsBinding.cs.in", replacements))
    generated_planner = build / "Evidence/ForgeTrust.AppSurface.Evidence.Planner/EvidenceClosedApplicationQualificationRegistration.cs"
    generated_planner.write_text(expand(build / "Evidence/ForgeTrust.AppSurface.Evidence.Planner/EvidenceClosedApplicationQualificationRegistration.cs.in",
        {"__QUALIFICATION_CANONICAL_ENTRY_BASE64__": metadata["entrybase64"]}))
    root_module = build / "scripts/evidencehost_linux_application.py"
    registration = expand(templates / "root-registration.py.in", {"{{canonicalEntryB64}}": metadata["entrybase64"],
        "{{entryDigest}}": metadata["entry_digest"], "{{catalogueDigest}}": metadata["catalogue_digest"], "{{policyByteSHA}}": metadata["policy_sha256"]})
    original = root_module.read_text()
    marker = "_COMPILED_REGISTRATIONS: tuple[_CompiledRegistration, ...] = ()"
    require(original.count(marker) == 1)
    root_module.write_text(original.replace(marker, registration))
    launcher_module = build / "scripts/evidencehost-linux-launcher.py"
    launcher_source = launcher_module.read_text()
    bundle_marker = "_PRIVATE_QUALIFICATION_BUNDLE_SOURCE = False"
    require(launcher_source.count(bundle_marker) == 1 and time.monotonic() < runner.deadline)
    launcher_module.write_text(launcher_source.replace(bundle_marker, "_PRIVATE_QUALIFICATION_BUNDLE_SOURCE = True"))
    coverage_spec = importlib.util.spec_from_file_location("qualification_product_inventory",
        build / "tests/evidencehost-consumer/PrivateQualification/product-coverage.py")
    coverage_module = importlib.util.module_from_spec(coverage_spec)
    coverage_spec.loader.exec_module(coverage_module)
    cli_project = build / CLI_PROJECT
    protected_lock_roots = {"source": source, "baseline": baseline,
        "product_source": source.parent / "issue779-product-source", "product_build": Path(product["build_root"])}
    linux_publish = prepare_private_linux_publish(build, before, protected_lock_roots, runner, dotnet,
        diagnostic_workspace=workspace, source_commit=source_commit)
    tools, product_binary_bindings = {}, {}
    for entry in ("cli", "host"):
        tool = prepare_product_tool_directory(workspace, entry, runner.deadline)
        argv = [str(dotnet), "publish", str(cli_project), "--no-restore", "--configuration", "Debug", "--output", str(tool), "-p:UseSharedCompilation=false"] + list(PRIVATE_PUBLISH_PROPERTIES)
        if entry == "host":
            # A full rebuild prevents reuse of the first entry's compile-time branch.
            runner.run([str(dotnet), "clean", str(cli_project), "--configuration", "Debug"] + list(PRIVATE_PUBLISH_PROPERTIES), build)
            argv.append("-p:QualificationHostEntry=true")
        runner.run(argv, build)
        reporter = tool / "reportgenerator/net10.0"
        if not (reporter / "ReportGenerator.dll").is_file():
            cache = Path(os.environ.get("NUGET_PACKAGES", str(Path.home()/".nuget/packages")))
            source_reporter = cache / "reportgenerator/5.5.10/tools/net10.0"
            require((source_reporter / "ReportGenerator.dll").is_file())
            shutil.copytree(source_reporter, reporter)
        shutil.copyfile(tool / "ForgeTrust.AppSurface.Evidence.Contracts.dll", tool / "protected-tool.dll")
        (tool / "qualification-policy.json").write_bytes(base64.b64decode(metadata["policybase64"], validate=True))
        product_binary_bindings[entry] = product_helper.replace_and_inspect(tool, product,
            runner, dotnet, inspector, workspace / ("product-binary-"+entry+".json"),
            inspector_sha256=inspector_sha256, inspector_contract_confirmed=True)
        checkpoint = "permission-sealing"
        try:
            require(time.monotonic() < runner.deadline)
            for path in tool.rglob("*"):
                require(time.monotonic() < runner.deadline)
                require(not path.is_symlink())
                if path.is_dir():
                    os.chmod(path, 0o755)
                else:
                    os.chmod(path, 0o444)
            os.chmod(tool, 0o755)
            checkpoint = "inventory"
            tool_map, published_inventory = measure_product_tool_inventory(tool, coverage_module, runner.deadline)
            tools[entry] = {"path": str(tool), "sha256": tool_map, "published_inventory": published_inventory}
        except BaseException as error:
            try:
                product_helper.capture_product_preparation_failure(tool, workspace / ("product-binary-"+entry+".json"),
                    "tool-sealing", error, deadline=runner.deadline, checkpoint=checkpoint)
            except BaseException:
                pass
            raise
    require(time.monotonic() < runner.deadline)
    verify_private_linux_publish_metadata(build, linux_publish, protected_lock_roots, runner.deadline)
    require(source_inventory(source) == before)
    require(time.monotonic() < runner.deadline)
    result = {"source_commit": source_commit, "source_files": before, "run_id": run_id, "workflow_identity": workflow_identity,
        "subject_revision": subject_revision, "subject_sha256": subject_map, "metadata": metadata, "bundle_files": files,
        "generated_sha256": {str(p.relative_to(build)): sha(p.read_bytes()) for p in (generated_contracts, generated_planner, root_module, launcher_module)},
        "application_bundle_input": bundle_input,
        "private_linux_publish": linux_publish,
        "preparation_complete": True, "sdk_bootstrap": sdk_binding,
        "product_coverage_inputs": {"source_commit": product["source_commit"],
            "source_manifest_sha256": sha((source / "tests/evidencehost-consumer/PrivateQualification/product-source-manifest.json").read_bytes()),
            "candidates": product["candidates"], "inspector_sha256": inspector_sha256,
            "coverlet_package": package_binding, "taskhost": taskhost_binding,
            "binary_bindings": product_binary_bindings, "runtime_compatibility_claim": False},
        "tools": tools, "build_commands": runner.results, "dotnet": str(dotnet), "build_root": str(build), "subject_root": str(subject)}
    write_complete_build_binding(workspace, result, runner.deadline)
    require(time.monotonic() < runner.deadline)
    return result
