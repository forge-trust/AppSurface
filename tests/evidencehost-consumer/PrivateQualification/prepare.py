"""Root-selected, bounded build preparation for the approved private variant.

This generator runs before subjects are evaluated. Its output is source-compiled
metadata, never a runtime enrollment API. The later controller authenticates the
actual worker and collects through the launcher's retained handles.
"""
import base64
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import signal
import stat
import subprocess
import time

from trusted_sdk import SDK_ROOT, metadata as sdk_metadata, seal_trusted_sdk

BASELINE = "5d325bb8c0f857eb37f0a736f39a0342b03e5c38"
APP_ID = "issue779-qualified-native-http"
BUILD_ID = "issue779-private-qualification-1"
PROFILE = "qualification-http"
PRODUCER = "qual-coverage"
ASSERTION = "appsurface/coverage/behavioral-patch@1"
CLI_PROJECT = "Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj"
HOST_SYMBOL = "EVIDENCE_PRIVATE_QUALIFICATION_HOST"


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

    def run(self, argv, cwd, *, input_bytes=None, capture=False, env=None):
        require(time.monotonic() < self.deadline)
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
                output, _ = process.communicate(input_bytes, timeout=min(180, self.deadline-time.monotonic()))
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
                try:
                    process.communicate(timeout=2)
                except Exception:
                    cleanup_failed = True
                    for pipe in (process.stdin, process.stdout, process.stderr):
                        if pipe is not None:
                            try:
                                pipe.close()
                            except Exception:
                                cleanup_failed = True
            except Exception as failure:
                error = failure
                category = "process-start-failed" if process is None else "process-communication-failed"
            finally:
                if process is not None:
                    try:
                        group_empty = join_process_group(process, time.monotonic()+3)
                    except Exception:
                        cleanup_failed, group_empty = True, False
                    if code is None:
                        code = process.returncode
        log_size = log.stat().st_size
        if category is None:
            category = ("output-limit" if log_size > 8*1024*1024 or capture and len(output or b"") > 1024*1024 else
                        "nonzero-exit" if code != 0 else "ownership-unconfirmed" if group_empty is not True else
                        "deadline-expired" if time.monotonic() >= self.deadline else None)
        record = {"argv": argv, "exit_code": code, "timed_out": timed_out,
                  "owned_group_empty": group_empty, "failure_category": category, "log_bytes": log_size,
                  "cleanup_failed": cleanup_failed,
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
        require(code == 0 and group_empty and time.monotonic() < self.deadline)
        if capture:
            require(isinstance(output, bytes) and len(output) <= 1024*1024)
            return output
        return b""


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
        process.wait(timeout=max(.01, deadline-time.monotonic()))
    except (subprocess.TimeoutExpired, OSError, ValueError):
        return False
    while time.monotonic() < deadline:
        try:
            os.killpg(process.pid, 0)
        except ProcessLookupError:
            return True
        except OSError:
            return False
        time.sleep(.02)
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


def bundle_inventory(bundle):
    """Set finite immutable deployment roles/modes before catalogue canonicalization."""
    roles = {"AspireChild.dll": "AppHost", "AspireChild.runtimeconfig.json": "AppHostRuntimeConfiguration",
             "resource/NativeHttpResource.dll": "Resource", "resource/NativeHttpResource.runtimeconfig.json": "ResourceRuntimeConfiguration",
             "dcp/dcp": "Dcp", "proof-input/declared.txt": "DeclaredInput"}
    result = []
    for path in sorted(bundle.rglob("*")):
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
    require(6 <= len(result) <= 256 and sum(row["LengthBytes"] for row in result) <= 512*1024*1024)
    dcp = bundle / "dcp/dcp"
    require(dcp.read_bytes()[:5] == b"\x7fELF\x02")
    for path in sorted(bundle.rglob("*"), reverse=True):
        if path.is_dir():
            os.chmod(path, 0o555)
    os.chmod(bundle, 0o555)
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


def retain_sdk_preflight_binding(workspace, source_commit):
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
    try:
        sdk_initial["host_before"] = sdk_metadata((SDK_ROOT / "dotnet").lstat())
        sdk_initial["host_metadata_state"] = "observed"
    finally:
        # If lstat fails, preserve unavailable/null and propagate that failure.
        binding_path.write_text(json.dumps(incomplete, sort_keys=True) + "\n")


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
    # Retain initial host facts even if the closed bootstrap rejects preparation.
    # This root-private partial record is not a completed build or admission binding.
    retain_sdk_preflight_binding(workspace, source_commit)
    dotnet, sdk_binding = seal_trusted_sdk(min(runner.deadline, time.monotonic() + 120))
    baseline, build = workspace / "baseline", workspace / "build"
    archive(source, BASELINE, baseline, runner)
    archive(source, source_commit, build, runner)
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
    files = bundle_inventory(bundle)
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
    cli_project = build / CLI_PROJECT
    runner.run([str(dotnet), "restore", str(cli_project), "--locked-mode"], build)
    tools = {}
    for entry in ("cli", "host"):
        tool = workspace / ("tool-"+entry)
        argv = [str(dotnet), "publish", str(cli_project), "--no-restore", "--configuration", "Debug", "--output", str(tool), "-p:UseSharedCompilation=false"]
        if entry == "host":
            # A full rebuild prevents reuse of the first entry's compile-time branch.
            runner.run([str(dotnet), "clean", str(cli_project), "--configuration", "Debug"], build)
            argv.append("-p:QualificationHostEntry=true")
        runner.run(argv, build)
        reporter = tool / "reportgenerator/net10.0"
        if not (reporter / "ReportGenerator.dll").is_file():
            cache = Path(os.environ.get("NUGET_PACKAGES", str(Path.home()/".nuget/packages")))
            source_reporter = cache / "reportgenerator/5.5.10/tools/net10.0"
            require((source_reporter / "ReportGenerator.dll").is_file())
            shutil.copytree(source_reporter, reporter)
        deployment = tool / "application-bundles" / APP_ID / BUILD_ID
        shutil.copytree(bundle, deployment)
        shutil.copyfile(tool / "ForgeTrust.AppSurface.Evidence.Contracts.dll", tool / "protected-tool.dll")
        (tool / "qualification-policy.json").write_bytes(base64.b64decode(metadata["policybase64"], validate=True))
        for path in tool.rglob("*"):
            require(not path.is_symlink())
            if path.is_dir() and not path.is_relative_to(deployment):
                os.chmod(path, 0o755)
            elif not path.is_relative_to(deployment):
                os.chmod(path, 0o444)
        os.chmod(tool, 0o755)
        tools[entry] = {"path": str(tool), "sha256": {p.relative_to(tool).as_posix(): sha(p.read_bytes())
            for p in sorted(tool.rglob("*")) if p.is_file()}}
    require(source_inventory(source) == before)
    result = {"source_commit": source_commit, "source_files": before, "run_id": run_id, "workflow_identity": workflow_identity,
        "subject_revision": subject_revision, "subject_sha256": subject_map, "metadata": metadata, "bundle_files": files,
        "generated_sha256": {str(p.relative_to(build)): sha(p.read_bytes()) for p in (generated_contracts, generated_planner, root_module)},
        "preparation_complete": True, "sdk_bootstrap": sdk_binding,
        "tools": tools, "build_commands": runner.results, "dotnet": str(dotnet), "build_root": str(build), "subject_root": str(subject)}
    (workspace / "build-binding.json").write_text(json.dumps(result, sort_keys=True, indent=2)+"\n")
    os.chmod(workspace / "build-binding.json", 0o600)
    return result
