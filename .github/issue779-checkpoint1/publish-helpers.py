"""Build only the fixed framework-dependent C1 helper images for one selector.

Each helper is restored in locked mode and published in Release from the
source-bound repository using the actual setup SDK. This function records
actual argv, joined process terminals, bounded log hashes, and complete output
inventories. It neither creates native results nor grants production authority.

``output`` must be a fresh directory path outside the captured source and the
artifact/evidence directory. The function creates it privately; helper images
are its child directories and command logs are siblings of those images.
"""

from pathlib import Path
import os
import re
import stat
import subprocess
import time

import binding


HELPERS = {
    "peer": (".github/issue779-checkpoint1/procedures/helpers/peer/NativePeerBroker.csproj",
             "NativePeerBroker.dll"),
    "n04": (".github/issue779-checkpoint1/procedures/helpers/n04/NativeRootCoordinator.csproj",
            "NativeRootCoordinator.dll"),
    "n07": (".github/issue779-checkpoint1/procedures/helpers/n07/NativeRootCoordinator.csproj",
            "NativeRootCoordinator.dll"),
}
PEER_SELECTORS = frozenset(("None", "N04", "N05", "N06", "N07", "N08", "N11", "N12"))
DIAGNOSTIC = re.compile(rb"\b(?:warning|error) [A-Z]+[0-9]+:")


def _require(condition, category):
    binding.require(condition, category)


def _helper_ids(selector):
    _require(selector in binding.SELECTORS, "helper-selector")
    selected = []
    if selector in PEER_SELECTORS:
        selected.append("peer")
    if selector == "N04":
        selected.append("n04")
    if selector == "N07":
        selected.append("n07")
    return tuple(selected)


def _private_parent(path):
    fact = path.lstat()
    _require(stat.S_ISDIR(fact.st_mode) and fact.st_uid == os.geteuid()
             and stat.S_IMODE(fact.st_mode) == 0o700, "helper-private-parent")


def _verify_command(row, expected_phase, expected_argv, deadline):
    _require(type(row) is dict and row.get("phase") == expected_phase
             and row.get("argv") == expected_argv and row.get("exit") == 0
             and row.get("timed_out") is False and row.get("joined") is True,
             "helper-command-terminal")
    raw = binding.read(Path(row["log_path"]), binding.MAX_LOG, deadline=deadline)
    _require(row.get("log_bytes") == len(raw)
             and row.get("log_sha256") == binding.digest(raw)
             and DIAGNOSTIC.search(raw) is None, "helper-command-log")


def publish_helpers(*, source_root, capture_raw, selector, sdk_root, output, deadline):
    """Publish the selector's actual helper images and return their inventories.

    ``deadline`` is the caller's absolute monotonic deadline. The helper
    selection is fixed in source: None, N04-N08, N11, and N12 use peer; N04
    and N07 also use their matching coordinator; all other selectors use none.
    No command text, project path, target framework, or output path comes from a
    receipt. A caller still binds and verifies the returned map in its build
    receipt and independently qualifies the compact FDD runtime.
    """
    helper_ids = _helper_ids(selector)
    source_root = binding.absolute(str(source_root))
    _require(source_root.resolve(strict=True) == source_root, "helper-root-alias")
    decoded_capture = binding.decode(capture_raw)
    capture = binding.validate_capture(decoded_capture, decoded_capture.get("source"))
    end = deadline
    _require(type(end) in (int, float) and end > time.monotonic(), "helper-deadline")
    binding.verify_source(capture, source_root, deadline=end)
    if not helper_ids:
        return {}

    sdk_root = binding.absolute(str(sdk_root))
    output = binding.absolute(str(output))
    _require(sdk_root.resolve(strict=True) == sdk_root
             and output.resolve(strict=False) == output, "helper-root-alias")
    _require(not binding.overlaps(source_root, sdk_root)
             and not binding.overlaps(source_root, output)
             and not binding.overlaps(sdk_root, output), "helper-root-overlap")
    parent = output.parent
    _private_parent(parent)
    _require(not output.exists() and not output.is_symlink(), "helper-output-fresh")
    sdk_stat = sdk_root.lstat()
    dotnet = sdk_root / "dotnet"
    dotnet_stat = dotnet.lstat()
    _require(stat.S_ISDIR(sdk_stat.st_mode) and stat.S_ISREG(dotnet_stat.st_mode)
             and stat.S_IMODE(dotnet_stat.st_mode) == 0o755 and dotnet_stat.st_nlink == 1,
             "helper-sdk-root")

    # Import late so the publisher owns the one shared process-group/EOF logger,
    # while preserving this module's single-direction dependency in tests.
    from publish import command

    os.mkdir(output, 0o700)
    os.chmod(output, 0o700)
    environment = os.environ.copy()
    environment.update(DOTNET_ROOT=str(sdk_root), DOTNET_NOLOGO="1",
        DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_CLI_USE_MSBUILD_SERVER="0",
        MSBUILDDISABLENODEREUSE="1")
    results = {}
    for helper_id in helper_ids:
        _require(time.monotonic() < end, "helper-deadline")
        project_rel, entry = HELPERS[helper_id]
        project = source_root / project_rel
        fact = project.lstat()
        _require(stat.S_ISREG(fact.st_mode) and fact.st_nlink == 1,
                 "helper-project-file")
        bundle = output / helper_id
        restore_log = output / (helper_id + "-locked-restore.log")
        publish_log = output / (helper_id + "-publish.log")
        _require(not bundle.exists() and not bundle.is_symlink()
                 and not restore_log.exists() and not publish_log.exists(), "helper-output-fresh")
        restore_argv = [str(dotnet), "restore", str(project), "--locked-mode",
                        "--disable-parallel", "-p:Configuration=Release", "-warnaserror",
                        "-p:UseSharedCompilation=false", "--nologo"]
        publish_argv = [str(dotnet), "publish", str(project), "--no-restore", "-c",
                        "Release", "--self-contained", "false", "-warnaserror",
                        "-p:UseSharedCompilation=false", "--nologo", "-o", str(bundle)]
        command_rows = []
        for phase, argv, log in (("locked-restore", restore_argv, restore_log),
                                 ("publish", publish_argv, publish_log)):
            row = command(argv, "helper-" + helper_id + "-" + phase, log,
                source_root, environment, min(end, time.monotonic() + 180))
            expected_phase = "helper-" + helper_id + "-" + phase
            _verify_command(row, expected_phase, argv, end)
            command_rows.append(row)
            binding.require(time.monotonic() < end, "helper-deadline")
        binding.prepare_publish_image(bundle, deadline=end)
        image = binding.inventory(bundle, entry, deadline=end)
        prefix = entry[:-4]
        _require(prefix + ".deps.json" in image["files"]
                 and prefix + ".runtimeconfig.json" in image["files"]
                 and "sdk" not in image["directories"], "helper-fdd-output")
        results[helper_id] = {"image": image, "commands": command_rows}
        binding.verify_source(capture, source_root, deadline=end)
    binding.verify_source(capture, source_root, deadline=end)
    return results
