"""Retain genuine current helper publications for the fixed Linux procedures.

These test inputs grant no worker, admission or native success. The publication
receipt must already pass the shared complete-source, real-command and image
verification. Every helper is copied into its original protected namespace and
rehashed through the retained-root copy implementation before use.
"""

import hashlib
import importlib.util
import os
from pathlib import Path
import re
import stat
import time


HERE = Path(__file__).resolve().parent
HELPERS = {"peer": ("n03", "NativePeerBroker.dll"),
           "n04": ("n04", "NativeRootCoordinator.dll"),
           "n07": ("n07", "NativeRootCoordinator.dll")}


def require(value, category):
    if not value:
        raise ValueError("helper-" + category)


def load_current(path, capture, source_root, binding, *, deadline):
    """Load one fixed captured module after checking its exact current bytes."""
    relative = path.relative_to(source_root).as_posix()
    raw = binding.read(path, deadline=deadline)
    require(binding.digest(raw) == capture["files"][relative]["sha256"], "module-binding")
    spec = importlib.util.spec_from_file_location("c1_" + path.stem, path)
    require(spec is not None and spec.loader is not None, "module-loader")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def build_helper_inputs(context, prepared, binding, *, helper_id, deadline):
    """Copy exactly peer/n04/n07; never execute receipt-defined command text.

    The copied helper's file modes are 0444/0555 and every directory is 0555.
    The caller owns the original deadline and subsequent actual OS/ELF audit.
    A failed or partial generation is retained and never reused.
    """
    require(helper_id in HELPERS and os.geteuid() == 0, "fixed-root-input")
    require(time.monotonic() < deadline and re.fullmatch(r"[0-9a-f]{32}", context["generation"]),
            "generation-deadline")
    build, capture = prepared["build"], prepared["capture"]
    binding.verify_build(build, prepared["capture_raw"], context["source"], context["selector"], deadline=deadline)
    source_root = Path(build["source_root"])
    owner = load_current(HERE.parent / "owner-death/adapter.py", capture, source_root, binding, deadline=deadline)
    workspace = load_current(HERE.parent / "owner-death/workspace.py", capture, source_root, binding, deadline=deadline)
    image = build["helpers"][helper_id]["image"]
    namespace, entry = HELPERS[helper_id]
    binding.validate_image(image, entry)
    binding.verify_image(image, deadline=deadline)
    parent = Path("/var/lib/appsurface-evidence-" + namespace + "-helpers")
    workspace.safe_ancestors(parent.parent)
    if not os.path.lexists(parent):
        parent.mkdir(mode=0o755)
        parent.chmod(0o755)
    metadata = parent.lstat()
    require(stat.S_ISDIR(metadata.st_mode) and metadata.st_uid == metadata.st_gid == 0
            and stat.S_IMODE(metadata.st_mode) == 0o755 and not parent.is_symlink(), "parent-custody")
    generation = parent / context["generation"]
    require(not os.path.lexists(generation), "fresh-generation")
    generation.mkdir(mode=0o755)
    generation.chmod(0o755)
    root = generation / "bundle"
    nodes = owner._copy_tree(Path(image["root"]), root, image["files"], set(image["directories"]),
                             workspace, deadline=deadline, seal=True)
    node = {"schema": "issue779-build-node-inventory-v1", "root_name": "tool", **nodes}
    raw = owner.canonical(node)
    tsv = "".join(f"{row['mode']}\t{row['sha256']}\t{name}\n"
                  for name, row in sorted(nodes["files"].items())).encode()
    control = prepared["control"]
    map_path, nodes_path = control / (helper_id + ".tsv"), control / (helper_id + "-nodes.json")
    owner.save(map_path, tsv)
    owner.save(nodes_path, raw)
    os.chmod(map_path, 0o444)
    os.chmod(nodes_path, 0o444)
    require(time.monotonic() < deadline, "late-helper-copy")
    binding.verify_image(image, deadline=deadline)
    return {"root": root, "map": map_path, "map_sha256": hashlib.sha256(tsv).hexdigest(),
            "nodes": nodes_path, "nodes_sha256": hashlib.sha256(raw).hexdigest(),
            "entry_sha256": nodes["files"][entry]["sha256"], "generation": context["generation"]}


def build_peer_inputs(context, prepared, binding, *, deadline):
    """Fixed NativePeerBroker publication for real N02/N03 prerequisites."""
    return build_helper_inputs(context, prepared, binding, helper_id="peer", deadline=deadline)
