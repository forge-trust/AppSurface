"""Copy a small, measured framework-dependent .NET 10 runtime closure.

This is a filesystem preparation step only. It copies the selected host, hostfxr,
and matching .NET/ASP.NET shared frameworks from the actual setup SDK root. It
does not execute the runtime, qualify an OS, or establish native acceptance.
The caller must pass the separately verified SDK root and retain this output
outside the source capture and artifact directory.
"""

from pathlib import Path
import argparse
import hashlib
import json
import os
import re
import stat
import tempfile
import time

import binding


FILE_LIMIT = 256 * 1024 * 1024
TOTAL_LIMIT = 1024 * 1024 * 1024
NODE_LIMIT = 8192
JSON_LIMIT = 8 * 1024 * 1024
VERSION = re.compile(r"10\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\Z")
FRAMEWORK_VERSION = re.compile(r"10\.0\.(?:0|[1-9][0-9]*)\Z")
DIR_FLAGS = os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC
FILE_FLAGS = os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK | os.O_CLOEXEC
FAILURE_CATEGORIES = frozenset((
    "runtime-copy-inventory", "runtime-copy-write", "runtime-deadline",
    "runtime-dotnet-host", "runtime-framework-version-mismatch",
    "runtime-image-bound", "runtime-node-limit", "runtime-output-fresh",
    "runtime-output-parent", "runtime-receipt-fresh", "runtime-receipt-limit",
    "runtime-receipt-write", "runtime-root-alias", "runtime-sdk-content",
    "runtime-sdk-root", "runtime-source-changed", "runtime-source-directory",
    "runtime-source-file", "runtime-source-growth", "runtime-source-link-or-special",
    "runtime-source-substitution", "runtime-total-limit", "runtime-version-directory",
    "runtime-version-missing",
))


class Rejected(ValueError):
    """A closed category for unsafe or incomplete runtime input."""


def require(condition, category):
    if not condition:
        raise Rejected(category)


def identity(value):
    return (value.st_dev, value.st_ino, value.st_mode, value.st_uid, value.st_gid,
            value.st_nlink, value.st_size, value.st_mtime_ns, value.st_ctime_ns)


def check_deadline(deadline):
    require(time.monotonic() < deadline, "runtime-deadline")


def open_dir(root_fd, parts):
    """Open every component separately so no intermediate symlink is followed."""
    fd = os.dup(root_fd)
    try:
        for part in parts:
            child = os.open(part, DIR_FLAGS, dir_fd=fd)
            os.close(fd)
            fd = child
        return fd
    except BaseException:
        os.close(fd)
        raise


def open_file(root_fd, parts):
    parent = open_dir(root_fd, parts[:-1])
    try:
        return os.open(parts[-1], FILE_FLAGS, dir_fd=parent)
    finally:
        os.close(parent)


def stat_at(root_fd, parts):
    parent = open_dir(root_fd, parts[:-1])
    try:
        return os.stat(parts[-1], dir_fd=parent, follow_symlinks=False)
    finally:
        os.close(parent)


def checked_dir(root_fd, parts, dirs):
    rel = "/".join(parts)
    before = stat_at(root_fd, parts)
    require(stat.S_ISDIR(before.st_mode) and stat.S_IMODE(before.st_mode) == 0o755,
            "runtime-source-directory")
    fd = open_dir(root_fd, parts)
    try:
        require(identity(before) == identity(os.fstat(fd)), "runtime-source-substitution")
    finally:
        os.close(fd)
    previous = dirs.get(rel)
    require(previous is None or previous["identity"] == identity(before),
            "runtime-source-substitution")
    dirs[rel] = {"mode": "0755", "identity": identity(before)}


def read_regular(root_fd, parts, expected=None, deadline=None):
    if deadline is not None:
        check_deadline(deadline)
    before = stat_at(root_fd, parts)
    require(stat.S_ISREG(before.st_mode) and before.st_nlink == 1
            and stat.S_IMODE(before.st_mode) in (0o644, 0o755)
            and 0 <= before.st_size <= FILE_LIMIT, "runtime-source-file")
    if expected is not None:
        require(identity(before) == expected, "runtime-source-substitution")
    fd = open_file(root_fd, parts)
    try:
        require(identity(before) == identity(os.fstat(fd)), "runtime-source-substitution")
        digest = hashlib.sha256()
        total = 0
        while True:
            if deadline is not None:
                check_deadline(deadline)
            chunk = os.read(fd, min(1024 * 1024, FILE_LIMIT + 1 - total))
            if not chunk:
                break
            total += len(chunk)
            require(total <= before.st_size and total <= FILE_LIMIT, "runtime-source-growth")
            digest.update(chunk)
        after_fd = os.fstat(fd)
        after_name = stat_at(root_fd, parts)
        require(total == before.st_size and identity(before) == identity(after_fd)
                == identity(after_name), "runtime-source-substitution")
        return {
            "sha256": digest.hexdigest(), "bytes": total,
            "mode": format(stat.S_IMODE(before.st_mode), "04o"),
            "identity": identity(before),
        }
    finally:
        os.close(fd)


def candidates(root_fd, relative, pattern, deadline):
    parts = tuple(relative.split("/"))
    container = stat_at(root_fd, parts)
    require(stat.S_ISDIR(container.st_mode) and stat.S_IMODE(container.st_mode) == 0o755,
            "runtime-source-directory")
    fd = open_dir(root_fd, parts)
    rows = []
    try:
        with os.scandir(fd) as entries:
            for entry in entries:
                check_deadline(deadline)
                if not pattern.fullmatch(entry.name):
                    continue
                fact = entry.stat(follow_symlinks=False)
                require(stat.S_ISDIR(fact.st_mode) and stat.S_IMODE(fact.st_mode) == 0o755,
                        "runtime-version-directory")
                child = os.open(entry.name, DIR_FLAGS, dir_fd=fd)
                try:
                    require(identity(fact) == identity(os.fstat(child)),
                            "runtime-source-substitution")
                finally:
                    os.close(child)
                rows.append((entry.name, identity(fact)))
    finally:
        os.close(fd)
    return tuple(sorted(rows, key=lambda row: row[0]))


def snapshot_tree(root_fd, selected_dirs, deadline):
    directories = {}
    files = {}
    pending = []
    total = 0
    for rel in selected_dirs:
        parts = tuple(rel.split("/"))
        for index in range(1, len(parts) + 1):
            checked_dir(root_fd, parts[:index], directories)
        pending.append(parts)
    while pending:
        check_deadline(deadline)
        parts = pending.pop()
        fd = open_dir(root_fd, parts)
        try:
            with os.scandir(fd) as entries:
                for entry in entries:
                    check_deadline(deadline)
                    child_parts = parts + (entry.name,)
                    rel = "/".join(child_parts)
                    binding.relative(rel)
                    fact = entry.stat(follow_symlinks=False)
                    if stat.S_ISDIR(fact.st_mode):
                        require(stat.S_IMODE(fact.st_mode) == 0o755,
                                "runtime-source-directory")
                        child = os.open(entry.name, DIR_FLAGS, dir_fd=fd)
                        try:
                            require(identity(fact) == identity(os.fstat(child)),
                                    "runtime-source-substitution")
                        finally:
                            os.close(child)
                        directories[rel] = {"mode": "0755", "identity": identity(fact)}
                        pending.append(child_parts)
                    else:
                        require(stat.S_ISREG(fact.st_mode) and fact.st_nlink == 1,
                                "runtime-source-link-or-special")
                        total += fact.st_size
                        require(total <= TOTAL_LIMIT, "runtime-total-limit")
                        row = read_regular(root_fd, child_parts, identity(fact), deadline)
                        files[rel] = row
                    require(len(files) + len(directories) <= NODE_LIMIT,
                            "runtime-node-limit")
        finally:
            os.close(fd)
    for rel, row in files.items():
        fact = stat_at(root_fd, tuple(rel.split("/")))
        require(identity(fact) == row["identity"], "runtime-source-substitution")
    return {"files": files, "directories": directories}


def source_candidates(root_fd, deadline):
    return {
        "host": candidates(root_fd, "host/fxr", VERSION, deadline),
        "core": candidates(root_fd, "shared/Microsoft.NETCore.App", FRAMEWORK_VERSION, deadline),
        "aspnet": candidates(root_fd, "shared/Microsoft.AspNetCore.App", FRAMEWORK_VERSION, deadline),
    }


def choose_versions(candidates_by_kind):
    host = candidates_by_kind["host"]
    core = candidates_by_kind["core"]
    aspnet = candidates_by_kind["aspnet"]
    require(host and core and aspnet, "runtime-version-missing")
    common_frameworks = {name for name, _ in core} & {name for name, _ in aspnet}
    require(common_frameworks, "runtime-framework-version-mismatch")
    host_version = max((name for name, _ in host), key=lambda value: tuple(map(int, value.split("."))))
    framework_version = max(common_frameworks,
        key=lambda value: tuple(map(int, value.split("."))))
    return host_version, framework_version


def selected_source_snapshot(sdk_root, deadline):
    root_fd = os.open(sdk_root, DIR_FLAGS)
    try:
        candidates_before = source_candidates(root_fd, deadline)
        host_version, framework_version = choose_versions(candidates_before)
        # Keep the actual SDK marker separate; it is verified but never copied.
        sdk_fact = stat_at(root_fd, ("sdk",))
        require(stat.S_ISDIR(sdk_fact.st_mode), "runtime-sdk-root")
        dotnet_stat = stat_at(root_fd, ("dotnet",))
        require(stat.S_ISREG(dotnet_stat.st_mode) and stat.S_IMODE(dotnet_stat.st_mode) == 0o755
                and dotnet_stat.st_nlink == 1, "runtime-dotnet-host")
        dotnet_row = read_regular(root_fd, ("dotnet",), identity(dotnet_stat), deadline)
        require(dotnet_row["bytes"] <= TOTAL_LIMIT, "runtime-total-limit")
        selected = ["host/fxr/" + host_version,
            "shared/Microsoft.NETCore.App/" + framework_version,
            "shared/Microsoft.AspNetCore.App/" + framework_version]
        tree = snapshot_tree(root_fd, selected, deadline)
        tree["files"]["dotnet"] = dotnet_row
        require(len(tree["files"]) + len(tree["directories"]) <= NODE_LIMIT,
                "runtime-node-limit")
        total = sum(row["bytes"] for row in tree["files"].values())
        require(total <= TOTAL_LIMIT, "runtime-total-limit")
        return candidates_before, host_version, framework_version, tree
    finally:
        os.close(root_fd)


def public_snapshot(tree):
    return {
        "files": {name: {key: row[key] for key in ("sha256", "bytes", "mode")}
                  for name, row in sorted(tree["files"].items())},
        "directories": dict(sorted((name, row["mode"])
                                    for name, row in tree["directories"].items())),
    }


def copy_file(root_fd, temp_root, rel, expected, deadline):
    parts = tuple(rel.split("/"))
    before = stat_at(root_fd, parts)
    require(identity(before) == expected["identity"], "runtime-source-substitution")
    source_fd = open_file(root_fd, parts)
    destination = temp_root.joinpath(*parts)
    target_fd = None
    try:
        require(identity(before) == identity(os.fstat(source_fd)), "runtime-source-substitution")
        target_fd = os.open(destination, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW
                            | os.O_CLOEXEC, int(expected["mode"], 8))
        digest = hashlib.sha256()
        copied = 0
        while True:
            check_deadline(deadline)
            chunk = os.read(source_fd, min(1024 * 1024, FILE_LIMIT + 1 - copied))
            if not chunk:
                break
            copied += len(chunk)
            require(copied <= expected["bytes"] and copied <= FILE_LIMIT,
                    "runtime-source-growth")
            digest.update(chunk)
            view = memoryview(chunk)
            while view:
                written = os.write(target_fd, view)
                require(written > 0, "runtime-copy-write")
                view = view[written:]
        os.fchmod(target_fd, int(expected["mode"], 8))
        require(copied == expected["bytes"] and digest.hexdigest() == expected["sha256"]
                and identity(before) == identity(os.fstat(source_fd))
                == identity(stat_at(root_fd, parts)), "runtime-source-substitution")
        os.fsync(target_fd)
    finally:
        os.close(source_fd)
        if target_fd is not None:
            os.close(target_fd)


def _write_private(path, raw):
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW
                 | os.O_CLOEXEC, 0o600)
    try:
        os.fchmod(fd, 0o600)
        view = memoryview(raw)
        while view:
            written = os.write(fd, view)
            require(written > 0, "runtime-receipt-write")
            view = view[written:]
        os.fsync(fd)
    finally:
        os.close(fd)


def prepare_runtime(sdk_root, output, receipt=None, *, deadline=None):
    """Create a fresh compact FDD runtime and optionally its private receipt.

    ``sdk_root`` is the canonical full setup SDK. ``output`` is an absent path
    under an owner-private parent. If supplied, ``receipt`` is a fresh private
    file outside both the SDK and compact runtime. The returned ``image`` is an
    inventory of the bytes actually copied; it is not an OS qualification.
    """
    deadline = time.monotonic() + 300 if deadline is None else deadline
    sdk_root = binding.absolute(str(sdk_root))
    output = binding.absolute(str(output))
    receipt = None if receipt is None else binding.absolute(str(receipt))
    check_deadline(deadline)
    require(sdk_root.resolve(strict=True) == sdk_root and output.resolve(strict=False) == output,
            "runtime-root-alias")
    sdk_stat = sdk_root.lstat()
    require(stat.S_ISDIR(sdk_stat.st_mode), "runtime-sdk-root")
    parent = output.parent
    parent_stat = parent.lstat()
    require(stat.S_ISDIR(parent_stat.st_mode) and parent_stat.st_uid == os.geteuid()
            and not (stat.S_IMODE(parent_stat.st_mode) & 0o022), "runtime-output-parent")
    require(not output.exists() and not output.is_symlink()
            and not binding.overlaps(sdk_root, output), "runtime-output-fresh")
    if receipt is not None:
        receipt_parent = receipt.parent.lstat()
        require(stat.S_ISDIR(receipt_parent.st_mode) and receipt_parent.st_uid == os.geteuid()
                and stat.S_IMODE(receipt_parent.st_mode) == 0o700
                and not receipt.exists() and not receipt.is_symlink()
                and not binding.overlaps(sdk_root, receipt)
                and not binding.overlaps(output, receipt), "runtime-receipt-fresh")
    before_candidates, host_version, framework_version, before = selected_source_snapshot(
        sdk_root, deadline)
    before_public = public_snapshot(before)

    temporary = tempfile.TemporaryDirectory(prefix=".c1-runtime-", dir=parent)
    temp_root = Path(temporary.name)
    try:
        for rel in sorted(before["directories"], key=lambda value: (value.count("/"), value)):
            target = temp_root / rel
            target.mkdir(mode=0o755, parents=True, exist_ok=True)
            os.chmod(target, 0o755, follow_symlinks=False)
        os.chmod(temp_root, 0o755, follow_symlinks=False)
        source_fd = os.open(sdk_root, DIR_FLAGS)
        try:
            for rel, row in sorted(before["files"].items()):
                copy_file(source_fd, temp_root, rel, row, deadline)
        finally:
            os.close(source_fd)

        after_candidates, after_host, after_framework, after = selected_source_snapshot(
            sdk_root, deadline)
        require(after_candidates == before_candidates and after_host == host_version
                and after_framework == framework_version
                and public_snapshot(after) == before_public
                and all(after["files"][name]["identity"] == row["identity"]
                        for name, row in before["files"].items())
                and all(after["directories"][name]["identity"] == row["identity"]
                        for name, row in before["directories"].items()),
                "runtime-source-changed")
        require(not (temp_root / "sdk").exists() and not (temp_root / "packs").exists()
                and not (temp_root / "templates").exists(), "runtime-sdk-content")
        check_deadline(deadline)
        os.rename(temp_root, output)
    except BaseException:
        temporary.cleanup()
        raise
    else:
        temporary.cleanup()

    image = binding.inventory(output, "dotnet", deadline=deadline)
    require(sum(row["bytes"] for row in image["files"].values()) <= TOTAL_LIMIT
            and len(image["files"]) + len(image["directories"]) <= NODE_LIMIT,
            "runtime-image-bound")
    require(image["files"] == before_public["files"]
            and image["directories"] == before_public["directories"],
            "runtime-copy-inventory")
    value = {
        "schema": "issue779-checkpoint1-runtime-copy-v1",
        "sdk_root": str(sdk_root),
        "host_fxr_version": host_version,
        "shared_framework_version": framework_version,
        "source_inventory_sha256": binding.digest(binding.canonical(before_public)),
        "image": image,
        "runtime_only_copy": True,
        "os_qualification": False,
    }
    raw = binding.canonical(value)
    require(len(raw) <= JSON_LIMIT, "runtime-receipt-limit")
    if receipt is not None:
        _write_private(receipt, raw)
    return value


def failure_diagnostic(error):
    """Project a failed preparation to closed data without paths or raw errors.

    Known custody categories remain distinguishable. Other input errors use
    ``runtime-input-invalid``; OS errors use ``runtime-os-error`` and retain only
    an integer errno in 1..4095. This data cannot qualify a runtime or turn a
    rejected preparation into a published image.
    """
    category = "runtime-os-error" if isinstance(error, OSError) else "runtime-input-invalid"
    if (type(error) is Rejected and len(error.args) == 1
            and type(error.args[0]) is str and error.args[0] in FAILURE_CATEGORIES):
        category = error.args[0]
    number = error.errno if isinstance(error, OSError) else None
    return {
        "schema": "issue779-checkpoint1-runtime-failure-v1",
        "category": category,
        "error_kind": "os" if isinstance(error, OSError) else "input",
        "errno": number if type(number) is int and 1 <= number <= 4095 else None,
    }


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--sdk-root", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--receipt", type=Path)
    args = parser.parse_args(argv)
    try:
        value = prepare_runtime(args.sdk_root, args.output, args.receipt)
    except (OSError, ValueError) as error:
        print(json.dumps(failure_diagnostic(error), sort_keys=True))
        return 1
    print(json.dumps({"runtime": str(value["image"]["root"]),
                      "runtime_only_copy": True, "os_qualification": False}, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
