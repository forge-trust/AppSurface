"""Publish one current C1 image and record actual build terminals, never native proof.

The selected SDK executes only the repository CLI's locked restore and Release
publish. Complete source and image verification surrounds those commands. A
failed command, unfinished process group, changed source or unsafe file prevents
the build receipt from being written. The native adapter independently rechecks
this receipt and still has to execute its genuine protected control.
"""

from pathlib import Path
import importlib.util
import argparse
import json
import os
import re
import selectors
import signal
import stat
import subprocess
import time

import binding


def group_absent(pid):
    """Require actual process-group absence; inability to inspect is failure."""
    try:
        os.killpg(pid, 0)
    except ProcessLookupError:
        return True
    return False


def command(argv, phase, log, source_root, environment, deadline):
    """Bound one original process, its inherited output and remaining group.

    Both EOF and parent settlement are required. A descendant retaining output
    cannot turn a successful parent exit into a successful build observation.
    Every exceptional path kills the selected group and reaps the original.
    """
    process = None
    eof = False
    timed_out = False
    total = 0
    with log.open("xb") as stream:
        log.chmod(0o600)
        try:
            process = subprocess.Popen(argv, cwd=source_root, env=environment,
                stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT, start_new_session=True)
            with process.stdout, selectors.DefaultSelector() as events:
                os.set_blocking(process.stdout.fileno(), False)
                events.register(process.stdout, selectors.EVENT_READ)
                while not eof or process.poll() is None:
                    remaining = deadline - time.monotonic()
                    if remaining <= 0:
                        timed_out = True
                        raise binding.Rejected("build-command-deadline")
                    for key, _ in events.select(min(remaining, 0.1)):
                        data = os.read(key.fd, 65536)
                        if not data:
                            eof = True
                            events.unregister(key.fileobj)
                            continue
                        total += len(data)
                        binding.require(total <= binding.MAX_LOG, "build-log-limit")
                        stream.write(data)
                code = process.wait(timeout=max(0.01, deadline - time.monotonic()))
                binding.require(group_absent(process.pid), "build-command-group")
        finally:
            if process is not None:
                if not group_absent(process.pid):
                    try:
                        os.killpg(process.pid, signal.SIGKILL)
                    except ProcessLookupError:
                        pass
                process.wait(timeout=5)
                binding.require(group_absent(process.pid), "build-final-group")
    raw = binding.read(log, binding.MAX_LOG, deadline=deadline)
    binding.require(eof and not timed_out, "build-command-output")
    return {"phase": phase, "argv": argv, "exit": code,
            "timed_out": False, "joined": True, "log_path": str(log),
            "log_sha256": binding.digest(raw), "log_bytes": len(raw)}


def publish(args):
    """Verify current inputs, execute two build phases, then issue measured data.

    ``output`` is a new receipt file in a private existing evidence directory;
    ``tool`` must not exist. ``runtime`` is the canonical SDK-free FDD image;
    ``build_sdk`` is the distinct canonical setup SDK that executes the builds.
    This operation creates no accounts, units, admission, control socket or
    application/producer ownership.
    """
    started = time.monotonic()
    deadline = started + 600
    source_root = binding.absolute(str(args.source_root))
    runtime = binding.absolute(str(args.runtime))
    sdk = binding.absolute(str(args.build_sdk))
    tool = binding.absolute(str(args.tool))
    output = binding.absolute(str(args.output))
    binding.require(args.selector in binding.SELECTORS and not output.exists()
                    and not output.is_symlink() and not tool.exists()
                    and not tool.is_symlink(), "publish-fresh-output")
    binding.require(source_root.resolve(strict=True) == source_root
                    and runtime.resolve(strict=True) == runtime
                    and sdk.resolve(strict=True) == sdk
                    and tool.resolve(strict=False) == tool
                    and output.resolve(strict=False) == output,
                    "publisher-root-alias")
    binding.require(not any(binding.overlaps(first, second) for first, second in
        ((source_root, tool), (source_root, runtime), (tool, runtime),
         (source_root, output.parent), (runtime, output.parent), (tool, output.parent))),
        "publisher-root-overlap")
    evidence = output.parent.lstat()
    binding.require(stat.S_ISDIR(evidence.st_mode) and evidence.st_uid == os.geteuid()
                    and stat.S_IMODE(evidence.st_mode) == 0o700,
                    "publisher-evidence-directory")
    capture_raw = binding.read(args.source_capture, deadline=deadline)
    capture = binding.validate_capture(binding.decode(capture_raw), args.source)
    binding.verify_source(capture, source_root, deadline=deadline)
    binding.require(not binding.overlaps(sdk, runtime) and not binding.overlaps(sdk, source_root)
                    and not binding.overlaps(sdk, tool) and not binding.overlaps(sdk, output.parent),
                    "publisher-sdk-overlap")
    binding.require(not os.path.lexists(runtime / "sdk"), "publisher-runtime-only")
    dotnet = sdk / "dotnet"
    binding.require(dotnet.resolve(strict=True) == dotnet, "publisher-host-alias")
    environment = os.environ.copy()
    environment.update(DOTNET_ROOT=str(sdk), DOTNET_NOLOGO="1",
        DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_CLI_USE_MSBUILD_SERVER="0",
        MSBUILDDISABLENODEREUSE="1")
    version = command([str(dotnet), "--version"], "sdk-version",
        output.parent / "sdk-version.log", source_root, environment,
        min(deadline, time.monotonic() + 30))
    binding.require(version["exit"] == 0 and
        (output.parent / "sdk-version.log").read_text().strip() == "10.0.401",
        "publisher-sdk-version")
    project = str(source_root / binding.CLI_PROJECT)
    selected = "-p:EvidencePrivateQualification=" + args.selector
    commands = []
    phases = [
        ("locked-restore", [str(dotnet), "restore", project, "--locked-mode",
            "--disable-parallel", "-warnaserror", selected]),
        ("publish", [str(dotnet), "publish", project, "--no-restore", "-c",
            "Release", "--self-contained", "false", "-warnaserror",
            "-p:UseSharedCompilation=false", selected, "-o", str(tool)])]
    for phase, argv in phases:
        row = command(argv, phase, output.parent / (phase + ".log"), source_root,
            environment, min(deadline, time.monotonic() + 180))
        commands.append(row)
        binding.require(row["exit"] == 0, "publisher-command-failed")
        raw = binding.read(Path(row["log_path"]), binding.MAX_LOG, deadline=deadline)
        binding.require(not re.search(rb"\b(?:warning|error) [A-Z]+[0-9]+:", raw),
                        "publisher-compiler-diagnostic")
    binding.verify_source(capture, source_root, deadline=deadline)
    helper_path = Path(__file__).with_name("publish-helpers.py")
    helper_raw = binding.read(helper_path, deadline=deadline)
    helper_rel = helper_path.relative_to(source_root).as_posix()
    binding.require(binding.digest(helper_raw) == capture["files"][helper_rel]["sha256"], "publisher-helper-source")
    spec = importlib.util.spec_from_file_location("c1_helper_publisher", helper_path)
    binding.require(spec is not None and spec.loader is not None, "publisher-helper-loader")
    helper_module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(helper_module)
    helpers = helper_module.publish_helpers(source_root=source_root, capture_raw=capture_raw,
        selector=args.selector, sdk_root=sdk, output=tool.parent / (tool.name + "-helpers"), deadline=deadline)
    for helper_id, helper in helpers.items():
        for row in helper["commands"]:
            raw = binding.read(Path(row["log_path"]), binding.MAX_LOG, deadline=deadline)
            binding.require(binding.digest(raw) == row["log_sha256"] and len(raw) == row["log_bytes"],
                            "publisher-helper-log")
            retained_log = output.parent / (row["phase"] + ".log")
            with retained_log.open("xb") as stream:
                retained_log.chmod(0o600)
                stream.write(raw)
            row["log_path"] = str(retained_log)
    binding.verify_source(capture, source_root, deadline=deadline)
    value = {"schema": "issue779-checkpoint1-build-v1", "source": args.source,
        "selector": args.selector, "configuration": "Release", "sdk_version": "10.0.401",
        "source_root": str(source_root),
        "source_capture_sha256": binding.digest(capture_raw),
        "source_after_capture_sha256": binding.digest(capture_raw), "commands": commands,
        "tool": binding.inventory(tool, "ForgeTrust.AppSurface.Cli.dll", deadline=deadline),
        "runtime": binding.inventory(runtime, "dotnet", deadline=deadline),
        "helpers": helpers, "native_execution": False}
    binding.verify_build(value, capture_raw, args.source, args.selector, deadline=deadline)
    body = binding.canonical(value)
    binding.require(len(body) <= binding.MAX_JSON, "publisher-receipt-limit")
    with output.open("xb") as stream:
        output.chmod(0o600)
        stream.write(body)
    return value


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("source-root", "source-capture", "runtime", "build-sdk", "tool", "output"):
        parser.add_argument("--" + name, required=True, type=Path)
    parser.add_argument("--source", required=True)
    parser.add_argument("--selector", required=True, choices=binding.SELECTORS)
    args = parser.parse_args()
    try:
        publish(args)
    except (OSError, ValueError, subprocess.SubprocessError):
        print("C1 current publication failed; no native result was issued.")
        return 1
    print(json.dumps({"selector": args.selector, "published": True,
                      "native_execution": False}, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
