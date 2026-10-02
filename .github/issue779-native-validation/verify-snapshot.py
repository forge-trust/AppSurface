#!/usr/bin/env python3
"""Verify a captured checkout; importing this module performs no Git or file writes."""
from pathlib import Path, PurePosixPath
import hashlib
import json
import os
import stat
import subprocess
import sys


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def checked_paths(paths):
    require(isinstance(paths, list) and paths == sorted(set(paths)), "Source paths must be sorted and unique.")
    for name in paths:
        require(isinstance(name, str) and name and not any(c in name for c in "\\\n\r\0"), "Invalid source name.")
        path = PurePosixPath(name)
        require(not path.is_absolute() and str(path) == name and ".." not in path.parts
                and ".git" not in path.parts, "Noncanonical source name.")
    return paths


def file_digest(path):
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def source_snapshot(root, paths):
    hashes, modes = {}, {}
    for name in checked_paths(paths):
        path = root / name
        before = path.lstat()
        require(stat.S_ISREG(before.st_mode), "Source is not a regular file: " + name)
        # Check every parent as well; a regular leaf cannot hide a linked directory.
        for parent in path.parents:
            if parent == root:
                break
            require(not parent.is_symlink(), "Linked source parent: " + name)
        hashes[name] = file_digest(path)
        after = path.lstat()
        require((before.st_dev, before.st_ino, before.st_size, before.st_mode, before.st_mtime_ns)
                == (after.st_dev, after.st_ino, after.st_size, after.st_mode, after.st_mtime_ns),
                "Source changed while reading: " + name)
        modes[name] = format(stat.S_IMODE(after.st_mode), "04o")
    return {"sha256": hashes, "modes": modes}


def git(root, *arguments, timeout=30):
    environment = os.environ.copy()
    environment["GIT_OPTIONAL_LOCKS"] = "0"
    return subprocess.check_output(["git", "-c", "core.hooksPath=/dev/null", "-c", "core.autocrlf=false",
                                    *arguments], cwd=root, env=environment, timeout=timeout)


def git_text(root, *arguments, timeout=30):
    return git(root, *arguments, timeout=timeout).decode().strip()


def git_names(root, *arguments, timeout=30):
    return sorted(name.decode() for name in git(root, "ls-files", "-z", *arguments, timeout=timeout).split(b"\0") if name)


def index_and_tree(root):
    index = {}
    for entry in git(root, "ls-files", "--stage", "-z").split(b"\0"):
        if not entry:
            continue
        metadata, name = entry.split(b"\t", 1)
        mode, oid, stage = metadata.decode().split()
        require(stage == "0" and name.decode() not in index, "Unmerged or duplicate private index entry.")
        index[name.decode()] = (mode, oid)
    tree = {}
    for entry in git(root, "ls-tree", "-r", "-z", "HEAD").split(b"\0"):
        if not entry:
            continue
        metadata, name = entry.split(b"\t", 1)
        mode, kind, oid = metadata.decode().split()
        require(kind == "blob" and name.decode() not in tree, "Invalid private commit tree entry.")
        tree[name.decode()] = (mode, oid)
    return index, tree


def verify_checkout(repo, record):
    paths = checked_paths(sorted(record["source_sha256"]))
    require(source_snapshot(repo, paths) == {"sha256": record["source_sha256"], "modes": record["source_modes"]},
            "Private source bytes or modes differ.")
    for name in record["intentional_removed_source_files"]:
        require(not os.path.lexists(repo / name), "Removed source exists in private checkout.")
    require(git_text(repo, "rev-parse", "HEAD") == record["validation_commit"], "Private HEAD differs.")
    require(git_text(repo, "rev-parse", "HEAD^") == record["previous_validation_commit"], "Private parent differs.")
    require(git_text(repo, "rev-parse", "origin/main") == record["comparison_base"], "Private historical base differs.")
    require(git(repo, "status", "--porcelain=v1", "--untracked-files=all") == b"", "Private checkout is dirty.")
    index, tree = index_and_tree(repo)
    require(set(index) == set(paths) == set(tree) and index == tree, "Private index/tree path or object binding differs.")
    require(git_text(repo, "rev-parse", "--show-object-format") == "sha1", "Unsupported Git object format.")
    for name in paths:
        expected_mode = "100755" if int(record["source_modes"][name], 8) & 0o111 else "100644"
        contents = (repo / name).read_bytes()
        oid = hashlib.sha1(b"blob " + str(len(contents)).encode() + b"\0" + contents).hexdigest()
        require(index[name] == (expected_mode, oid), "Private index bytes or executable mode differs: " + name)
    return {"source_files": len(paths), "clean_head_index_tree": True, "bytes_modes": True}


if __name__ == "__main__":
    root = Path(__file__).resolve().parent
    record = json.loads((root / "snapshot.json").read_text())
    print(json.dumps(verify_checkout(root / "repo", record), sort_keys=True))
