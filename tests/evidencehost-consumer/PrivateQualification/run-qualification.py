#!/usr/bin/env python3
"""Execute the reviewed private CLI and public Host consumers through real root supervision.

The source commit is chosen by the immutable workflow. Compilation is the only
place the private tuple/binding is filled. No output grants Trusted authority.
"""
import argparse
import base64
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import re
import shutil
import stat
import subprocess
import sys
import tarfile
import time
import uuid
from xml.etree import ElementTree

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
from prepare import APP_ID, BUILD_ID, PROFILE, PRODUCER, ASSERTION, PreparationFailure, prepare, require, sha, source_inventory, unique_json, Runner


def load(path, name):
    """Load only the selected trusted physical source; never an uploaded handler."""
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


def immutable_tree(root, expected):
    """Rehash every prepared immutable file and reject links, extras or missing bytes."""
    observed = {}
    for path in sorted(root.rglob("*")):
        info = path.lstat()
        require(not stat.S_ISLNK(info.st_mode) and info.st_uid == 0)
        if stat.S_ISDIR(info.st_mode):
            require(not info.st_mode & 0o022)
        else:
            require(stat.S_ISREG(info.st_mode) and info.st_nlink == 1 and not info.st_mode & 0o022)
            observed[path.relative_to(root).as_posix()] = sha(path.read_bytes())
    require(observed == expected)


def validate_result(artifacts, expected_plan, entry, metadata):
    """Validate collected data; this function creates no admission or proof."""
    manifest_name = "evidence-manifest.json" if entry == "cli" else "manifest.json"
    manifest = unique_json(artifacts[manifest_name])
    require(manifest["Mode"] == "Observation" and manifest["ExecutionVerdict"] == "Passed"
            and manifest["ClaimKind"] == "None" and manifest["Eligibility"] == "None"
            and manifest.get("EnvelopeAssertion") is None and manifest["Metrics"]["CleanupCompleted"] is True
            and manifest["Metrics"].get("TerminalFailureCode") is None and manifest["PlanDigest"] == metadata["plan_digest"])
    require(len(manifest["ResourceResults"]) == 1 and manifest["ResourceResults"][0]["ResourceId"] == "native-http"
            and manifest["ResourceResults"][0]["Outcome"] == "Ready")
    require(len(manifest["ProducerResults"]) == 1)
    producer = manifest["ProducerResults"][0]
    require(producer["ProducerId"] == PRODUCER and producer["Outcome"] == "Passed"
            and producer["SatisfiedAssertionIds"] == [ASSERTION] and len(producer["Artifacts"]) == 1)
    artifact = producer["Artifacts"][0]
    report_name = PRODUCER+"/merged/coverage.cobertura.xml"
    report = artifacts[report_name]
    require(artifact["LogicalName"] == "coverage-report" and artifact["RelativePath"] == "merged/coverage.cobertura.xml"
            and artifact["MediaType"] == "application/xml" and artifact["LengthBytes"] == len(report)
            and artifact["Sha256"] == sha(report))
    require(manifest["ClosedObligationIds"] == ["qualified-risk"] and manifest["UnmediatedObligationIds"] == [])
    if entry == "cli":
        require(artifacts["evidence-plan.json"] == expected_plan)
        summary = unique_json(artifacts["evidence-summary.json"])
        require(all(summary[key] == manifest[key] for key in ("ExecutionVerdict", "ClaimKind", "Eligibility")))
    xml = ElementTree.fromstring(report)
    require(xml.tag == "coverage" and int(xml.attrib["lines-valid"]) > 0 and int(xml.attrib["branches-valid"]) > 0)
    return manifest


def persist_and_validate(workspace, entry, artifacts, plan, metadata):
    """Save only bounded closed collected files before evaluating their data.

    The actual caller must already have closed completion/accounts. This data-only
    helper cannot establish origin or ownership and creates no acceptance receipt.
    Invalid data is kept privately, and the original validation exception propagates.
    """
    require(entry in ("cli", "host"))
    expected = {"manifest.json", PRODUCER+"/merged/coverage.cobertura.xml"} if entry == "host" else {
        "evidence-plan.json", "evidence-manifest.json", "evidence-summary.json", PRODUCER+"/merged/coverage.cobertura.xml"}
    require(set(artifacts) == expected and all(type(data) is bytes for data in artifacts.values())
            and sum(len(data) for data in artifacts.values()) <= 20*1024*1024)
    copies = workspace / ("collected-"+entry)
    copies.mkdir(mode=0o700)
    for name, data in artifacts.items():
        path = copies / name
        parent = copies
        for component in Path(name).parts[:-1]:
            parent = parent / component
            parent.mkdir(exist_ok=True, mode=0o700)
            os.chmod(parent, 0o700)
        with path.open("xb") as stream:
            stream.write(data)
        os.chmod(path, 0o600)
    return copies, validate_result(artifacts, plan, entry, metadata)


def entry_preflight(binding, workspace, entry):
    """Load selected code and verify immutable snapshots before any launch.

    A fixed private stage records early rejection without an exception message,
    input path, partial success or execution authority. Capture failure preserves
    the original exception. Actual launch remains in the owning root procedure.
    """
    require(entry in ("cli", "host"))
    stage = "launcher-import"
    try:
        build = Path(binding["build_root"])
        launcher = load(build / "scripts/evidencehost-linux-launcher.py", "qualification_launcher")
        stage = "collector-import"
        collector = load(build / "tests/evidencehost-consumer/PrivateQualification/retained-output.py", "qualification_collector")
        stage = "tool-preflight"
        tool = Path(binding["tools"][entry]["path"])
        immutable_tree(tool, binding["tools"][entry]["sha256"])
        stage = "subject-preflight"
        subject = Path(binding["subject_root"])
        immutable_tree(subject, binding["subject_sha256"])
        return launcher, collector, tool, subject
    except Exception as error:
        try:
            failure_class = type(error).__name__
            if failure_class not in {"PreparationFailure", "ImportError", "ModuleNotFoundError", "OSError", "PermissionError", "FileNotFoundError"}:
                failure_class = "unknown"
            path = workspace / ("preflight-" + entry + ".json")
            with path.open("x") as stream:
                os.chmod(path, 0o600)
                json.dump({"entry": entry, "stage": stage, "error_class": failure_class}, stream, sort_keys=True)
                stream.write("\n")
        except Exception:
            pass
        raise


def retain_diagnostics(workspace, output, *, expected_owner_uid=0):
    """Retain a bounded private archive from closed root-owned diagnostic names only.

    Complete small collected files and bindings remain complete. Build logs retain
    at most their last 16 KiB, with original length/retained-byte SHA in the private index. Missing
    diagnostics never change an execution failure or grant a positive receipt.
    launcher-owned-exit.json stays the same fixed 4096-byte opaque private file;
    its v2 startup fields are not parsed here or promoted to authority. Historical
    v1 bytes are never converted or used as a schema-v2 success substitute.
    """
    selected = [("build-binding.json", 1024*1024, False)]
    selected.extend((f"build-logs/build-{index:02d}.log", 16*1024, True) for index in range(1, 33))
    selected.extend((f"build-logs/command-{index:02d}.json", 16*1024, False) for index in range(1, 33))
    for entry in ("cli", "host"):
        selected.append((f"preflight-{entry}.json", 4096, False))
        if entry == "cli":
            selected.extend((f"failure-cli/"+name, maximum, False) for name, maximum in (
                ("evidence-manifest.json", 256*1024), ("evidence-summary.json", 64*1024)))
        selected.extend((f"failure-{entry}/"+name, maximum, False) for name, maximum in (
            ("launcher-failure.json", 4096), ("launcher-owned-exit.json", 4096), ("launcher-worker-journal.log", 16*1024),
            ("subject-collector-startup.json", 64*1024),
            ("subject-failure-output/stdout.prefix", 519168), ("subject-failure-output/stderr.prefix", 519168)))
        selected.extend((f"failure-{entry}/vstest-diagnostics/"+name, maximum, False)
                        for name, maximum in (("runner.log", 128*1024), ("collector.log", 128*1024),
                                              ("host.log", 128*1024), ("index.json", 4096)))
        names = ("evidence-plan.json", "evidence-manifest.json", "evidence-summary.json") if entry == "cli" else ("manifest.json",)
        selected.extend((f"collected-{entry}/"+name, 128*1024, False) for name in (*names, PRODUCER+"/merged/coverage.cobertura.xml"))
        selected.extend(((f"collected-{entry}/build-01.log", 16*1024, True),
                         (f"collected-{entry}/command-01.json", 16*1024, False)))
    index, contents, total = [], [], 0
    deadline = time.monotonic()+5
    root_fd = os.open(workspace, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
    try:
        root_info = os.fstat(root_fd)
        require(root_info.st_uid == expected_owner_uid and not root_info.st_mode & 0o022)
        for relative, maximum, tail in selected:
            require(time.monotonic() < deadline)
            fd, directories = -1, []
            try:
                parent = root_fd
                for component in Path(relative).parts[:-1]:
                    child = os.open(component, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=parent)
                    directories.append(child)
                    info = os.fstat(child)
                    require(info.st_uid == expected_owner_uid and stat.S_IMODE(info.st_mode) == 0o700)
                    parent = child
                fd = os.open(Path(relative).name, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK | os.O_CLOEXEC, dir_fd=parent)
                before = os.fstat(fd)
                require(stat.S_ISREG(before.st_mode) and before.st_uid == expected_owner_uid and before.st_nlink == 1
                        and stat.S_IMODE(before.st_mode) == 0o600)
                if before.st_size > maximum and not tail:
                    index.append({"name": relative, "state": "oversize", "length": before.st_size})
                    continue
                start = max(0, before.st_size-maximum) if tail else 0
                data = os.pread(fd, before.st_size-start, start)
                after, named = os.fstat(fd), os.stat(Path(relative).name, dir_fd=parent, follow_symlinks=False)
                identity = lambda value: (value.st_dev, value.st_ino, value.st_mode, value.st_uid, value.st_gid,
                    value.st_nlink, value.st_size, value.st_mtime_ns, value.st_ctime_ns)
                require(len(data) == before.st_size-start and identity(before) == identity(after) == identity(named))
                total += len(data)
                require(total <= 3*1024*1024 and time.monotonic() < deadline)
                contents.append((relative, data))
                index.append({"name": relative, "state": "tail" if start else "complete", "length": before.st_size,
                              "retained_length": len(data), "retained_sha256": sha(data)})
            except FileNotFoundError:
                continue
            finally:
                if fd >= 0: os.close(fd)
                for directory_fd in reversed(directories): os.close(directory_fd)
    finally:
        os.close(root_fd)
    contents.append(("index.json", json.dumps(index, sort_keys=True, separators=(",", ":")).encode()))
    path = output/"private-diagnostics.tar"
    with path.open("xb") as stream:
        with tarfile.open(fileobj=stream, mode="w", format=tarfile.USTAR_FORMAT) as archive:
            for name, data in sorted(contents):
                info = tarfile.TarInfo(name); info.size = len(data); info.mode = 0o600
                info.uid = info.gid = info.mtime = 0; info.uname = info.gname = ""
                archive.addfile(info, io.BytesIO(data))
    os.chmod(path, 0o600)
    require(path.stat().st_size <= 4*1024*1024)
    return sha(path.read_bytes())


def prepare_entry_output_parent(workspace, entry):
    """Create a fresh cli/host outer output parent with search-only access.

    The controller's root-only main owns this directory. Mode 0711 permits the
    worker to traverse it to the launcher's worker-owned 0700 run anchor, without
    granting outer-directory listing or writes. Explicit chmod defeats umask;
    an existing path, including a symlink, is never reused. This helper does not
    create an artifact slot, change private children, or establish admission.
    """
    require(type(entry) is str and entry in ("cli", "host"))
    output = workspace / ("output-"+entry)
    output.mkdir(mode=0o711)
    os.chmod(output, 0o711)
    return output


def one_entry(binding, workspace, entry):
    """Launch, pin output, collect bytes, close accounts, then evaluate structural data."""
    build = Path(binding["build_root"])
    launcher, collector, tool, subject = entry_preflight(binding, workspace, entry)
    output = prepare_entry_output_parent(workspace, entry)
    diagnostics = workspace / ("failure-"+entry)
    diagnostics.mkdir(mode=0o700)
    diag_fd = launcher.open_diagnostic_directory(diagnostics)
    metadata = binding["metadata"]
    plan = base64.b64decode(metadata["planbase64"], validate=True)
    args = launcher.parser().parse_args([
        "--tool-root", str(tool), "--subject-root", str(subject), "--policy-file", str(tool/"qualification-policy.json"),
        "--job-seconds", "900", "--mode", "observation", "--output-parent", str(output), "--output-slot", "qualification",
        "--base-revision", binding["source_commit"], "--subject-revision", binding["subject_revision"],
        "--workflow-identity", binding["workflow_identity"], "--run-id", binding["run_id"],
        "--solution", "QualificationSubject.csproj", "--path", "tests/QualificationSubjectTests.cs",
        "--application-id", APP_ID, "--application-entry-digest", metadata["entry_digest"], "--application-profile", PROFILE,
        "--admission-seconds", "30", "--start-seconds", "120", "--collection-seconds", "60",
        "--cleanup-seconds", "60", "--stopping-seconds", "5"])
    started = time.monotonic()
    completion = None
    try:
        completion = launcher.launch_with_completion(args, diagnostic_directory_fd=diag_fd)
        require(type(completion) is launcher._LaunchCompletion)
        descriptor = completion.descriptor
        require(descriptor["run_id"] == binding["run_id"] and descriptor["base_revision"] == binding["source_commit"]
                and descriptor["entry_sha256"] == binding["tools"][entry]["sha256"]["ForgeTrust.AppSurface.Cli.dll"]
                and descriptor["policy_sha256"] == metadata["policy_sha256"] and completion.application_output_receipt is not None
                and len(completion.subject_output_receipts) == 1)
        artifacts = collector.collect(completion, plan, deadline=min(started+900, time.monotonic()+60), entry=entry)
        kernel_output = {"application": completion.application_output_receipt, "producer": completion.subject_output_receipts}
        # Account cleanup must finish before even the private structural data evaluation.
        completion.close()
        completion = None
        copies, manifest = persist_and_validate(workspace, entry, artifacts, plan, metadata)
        plan_path = copies / "protected-expected-plan.json"
        plan_path.write_bytes(plan)
        os.chmod(plan_path, 0o600)
        manifest_path = copies / ("evidence-manifest.json" if entry == "cli" else "manifest.json")
        verifier = Runner(copies, time.monotonic()+30)
        verifier.run([binding["dotnet"], str(tool/"ForgeTrust.AppSurface.Cli.dll"), "evidence", "verify",
                      str(manifest_path), "--plan", str(plan_path)], build)
        immutable_tree(tool, binding["tools"][entry]["sha256"])
        immutable_tree(subject, binding["subject_sha256"])
        return {"entry": entry, "exit_code": 0, "elapsed_seconds": time.monotonic()-started,
                "claim": "None", "eligibility": "None", "execution_verdict": manifest["ExecutionVerdict"],
                "manifest_digest": manifest["ManifestDigest"], "plan_digest": manifest["PlanDigest"],
                "output_receipts": kernel_output, "artifacts_sha256": {name: sha(data) for name, data in artifacts.items()},
                "structural_commands": verifier.results, "cleanup_complete": True}
    except Exception as error:
        try:
            launcher.write_failure_diagnostic(diag_fd, error)
        except Exception:
            pass
        raise
    finally:
        os.close(diag_fd)
        if completion is not None:
            completion.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source-commit", required=True)
    parser.add_argument("--workflow-head", required=True)
    parser.add_argument("--run-id", required=True)
    parser.add_argument("--workflow-file", required=True, choices=[".github/workflows/issue779-private-qualification.yml"])
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    require(sys.platform == "linux" and os.geteuid() == 0 and Path("/proc/1/comm").read_text().strip() == "systemd"
            and re.fullmatch(r"[0-9a-f]{40}", args.workflow_head))
    source = HERE.parents[2]
    require(subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=source, text=True).strip() == args.source_commit)
    workflow_identity = "forge-trust/AppSurface/"+args.workflow_file+"@"+args.workflow_head
    output = Path(args.output)
    require(output.is_absolute() and not output.exists() and not output.is_symlink())
    workspace = Path("/opt/issue779-qualification-"+uuid.uuid4().hex)
    workspace.mkdir(mode=0o711)
    os.chmod(workspace, 0o711)
    receipt = {"schema": "issue779-private-qualification-v1", "source_commit": args.source_commit,
               "workflow_head": args.workflow_head, "run_id": args.run_id, "claim": "None", "eligibility": "None",
               "trusted_enrollment": False, "entries": [], "exit_code": 1, "failure_category": "preparation-failed"}
    try:
        binding = prepare(source, workspace, args.source_commit, args.run_id, workflow_identity)
        receipt["build_binding_sha256"] = sha((workspace/"build-binding.json").read_bytes())
        for entry in ("cli", "host"):
            receipt["failure_category"] = entry+"-execution-failed"
            receipt["entries"].append(one_entry(binding, workspace, entry))
        require(source_inventory(source) == binding["source_files"])
        receipt.update(exit_code=0, failure_category=None)
    except Exception:
        # Raw exception, subject output, host paths and logs are never printed publicly.
        pass
    finally:
        output.mkdir(mode=0o700)
        os.chmod(output, 0o700)
        try:
            receipt["private_diagnostics_sha256"] = retain_diagnostics(workspace, output)
        except Exception:
            receipt["private_diagnostics_sha256"] = None
        (output/"qualification-receipt.json").write_text(json.dumps(receipt, sort_keys=True, indent=2)+"\n")
        os.chmod(output/"qualification-receipt.json", 0o600)
        # The private retained build/diagnostic directory is recorded only for root inspection.
        (output/"private-workspace.txt").write_text(str(workspace)+"\n")
        os.chmod(output/"private-workspace.txt", 0o600)
    print("PRIVATE_QUALIFICATION_PASSED" if receipt["exit_code"] == 0 else "PRIVATE_QUALIFICATION_FAILED", flush=True)
    return receipt["exit_code"]


if __name__ == "__main__":
    raise SystemExit(main())
