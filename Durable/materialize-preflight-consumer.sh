#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 3 ]]; then
  echo "Usage: $0 CONSUMER_PROJECT_TEMPLATE REPOSITORY_ROOT NEW_DESTINATION_DIRECTORY" >&2
  exit 2
fi

consumer_template="$1"
repo_root="$2"
destination_root="$3"

[[ -f "$consumer_template" && ! -L "$consumer_template" ]] || { echo "consumer-template-invalid" >&2; exit 1; }
[[ "$(basename "$consumer_template")" == "PostgreSqlPreflightConsumer.csproj.template" ]] \
  || { echo "consumer-template-name-invalid" >&2; exit 1; }
[[ -f "$repo_root/Directory.Build.props" && ! -L "$repo_root/Directory.Build.props" \
  && -f "$repo_root/Directory.Build.targets" && ! -L "$repo_root/Directory.Build.targets" ]] \
  || { echo "consumer-build-policy-missing" >&2; exit 1; }
[[ ! -e "$destination_root" && ! -L "$destination_root" ]] || { echo "consumer-destination-exists" >&2; exit 1; }

python3 - "$consumer_template" "$repo_root" "$destination_root" <<'PY'
import os
import pathlib
import shutil
import sys

source = pathlib.Path(sys.argv[1])
repo = pathlib.Path(sys.argv[2])
destination = pathlib.Path(sys.argv[3])

try:
    source_real = source.resolve(strict=True)
    source_dir = source_real.parent
    repo_real = repo.resolve(strict=True)
    destination_real = destination.resolve(strict=False)
except OSError:
    raise SystemExit("consumer-template-invalid")

if (destination_real == source_dir or source_dir in destination_real.parents
        or destination_real in source_dir.parents):
    raise SystemExit("consumer-template-destination-overlaps-source")

# Reject symlinks anywhere in authored input before copytree can follow them.
for directory, names, files in os.walk(source_dir, followlinks=False):
    for name in names + files:
        if name in {"bin", "obj"}:
            continue
        path = pathlib.Path(directory) / name
        if path.is_symlink():
            raise SystemExit("consumer-source-invalid")
    names[:] = [name for name in names if name not in {"bin", "obj"}]

flat_sources = list(source_dir.glob("*.cs"))
if not flat_sources or any(not path.is_file() or path.is_symlink() for path in flat_sources):
    raise SystemExit("consumer-sources-missing-or-invalid")

project_name = "PostgreSqlPreflightConsumer.csproj"
if not source.is_file() or source.name != project_name + ".template":
    raise SystemExit("consumer-template-invalid")
if not (repo_real / "Directory.Build.props").is_file() or not (repo_real / "Directory.Build.targets").is_file():
    raise SystemExit("consumer-build-policy-missing")
if destination.exists() or destination.is_symlink():
    raise SystemExit("consumer-destination-exists")

def ignore(directory, names):
    current = pathlib.Path(directory)
    ignored = {"bin", "obj"}
    if current == source_dir:
        ignored.add(project_name)  # Keep the local development project out of proof.
    return [name for name in names if name in ignored]

shutil.copytree(source_dir, destination, ignore=ignore, symlinks=False)
staged_template = destination / source.name
if not staged_template.is_file() or staged_template.is_symlink():
    raise SystemExit("consumer-template-invalid")
staged_project = destination / project_name
staged_template.rename(staged_project)
shutil.copyfile(repo_real / "Directory.Build.props", destination / "Directory.Build.props")
shutil.copyfile(repo_real / "Directory.Build.targets", destination / "Directory.Build.targets")
print(staged_project)
PY
