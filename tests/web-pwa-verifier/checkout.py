"""Copy versioned sources and pending Git additions into an independent fixture."""

import os
from pathlib import Path
import shutil
import subprocess

ROOT = Path(__file__).resolve().parents[2]


def source_checkout(destination: Path) -> Path:
    destination = destination.resolve()
    # Copy versioned inputs only, including staged new files when running locally.
    files = subprocess.check_output(
        ["git", "ls-files", "--cached", "--others", "--exclude-standard", "-z"], cwd=ROOT
    ).split(b"\0")
    for entry in filter(None, files):
        source = ROOT / os.fsdecode(entry)
        if source.is_file():
            target = destination / source.relative_to(ROOT)
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(source, target)
    return destination
