#!/usr/bin/env bash
set -euo pipefail

# Exact-artifact entry point for the PostgreSQL preflight consumer. This script
# never packs source. Its input is the frozen package bundle produced by the
# release pack-and-verify job (package-artifact-manifest.json plus .nupkg files).

usage() {
  echo "Usage: $0 --artifact-dir DIR --artifact-manifest FILE --source-commit SHA --run-id ID --artifact-id ID --consumer-project FILE --receipt FILE [--prepare-only]" >&2
}

fail() {
  echo "PostgreSQL preflight artifact proof failed [$1]." >&2
  exit 1
}

artifact_dir=""
artifact_manifest=""
source_commit=""
run_id=""
artifact_id=""
consumer_project=""
receipt_path=""
prepare_only=false

while [[ $# -gt 0 ]]; do
  case "$1" in
    --artifact-dir) [[ $# -ge 2 ]] || { usage; exit 2; }; artifact_dir="$2"; shift 2 ;;
    --artifact-manifest) [[ $# -ge 2 ]] || { usage; exit 2; }; artifact_manifest="$2"; shift 2 ;;
    --source-commit) [[ $# -ge 2 ]] || { usage; exit 2; }; source_commit="$2"; shift 2 ;;
    --run-id) [[ $# -ge 2 ]] || { usage; exit 2; }; run_id="$2"; shift 2 ;;
    --artifact-id) [[ $# -ge 2 ]] || { usage; exit 2; }; artifact_id="$2"; shift 2 ;;
    --consumer-project) [[ $# -ge 2 ]] || { usage; exit 2; }; consumer_project="$2"; shift 2 ;;
    --receipt) [[ $# -ge 2 ]] || { usage; exit 2; }; receipt_path="$2"; shift 2 ;;
    --prepare-only) prepare_only=true; shift ;;
    -h|--help) usage; exit 0 ;;
    *) usage; fail "unknown-option" ;;
  esac
done

[[ -n "$artifact_dir" && -n "$artifact_manifest" && -n "$source_commit" \
  && -n "$run_id" && -n "$artifact_id" && -n "$consumer_project" && -n "$receipt_path" ]] \
  || { usage; fail "missing-input"; }
[[ -d "$artifact_dir" && -f "$artifact_manifest" && -f "$consumer_project" ]] \
  || fail "input-not-found"
[[ ! -e "$receipt_path" && ! -L "$receipt_path" ]] || fail "receipt-already-exists"
[[ "$source_commit" =~ ^([0-9a-fA-F]{40}|[0-9a-fA-F]{64})$ ]] || fail "invalid-source-commit"
[[ "$run_id" =~ ^[A-Za-z0-9._-]{1,100}$ ]] || fail "invalid-run-id"
[[ "$artifact_id" =~ ^[A-Za-z0-9._-]{1,160}$ ]] || fail "invalid-artifact-id"

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
artifact_dir="$(cd "$artifact_dir" && pwd -P)"
artifact_manifest="$(cd "$(dirname "$artifact_manifest")" && pwd -P)/$(basename "$artifact_manifest")"
consumer_project="$(cd "$(dirname "$consumer_project")" && pwd -P)/$(basename "$consumer_project")"
receipt_parent="$(cd "$(dirname "$receipt_path")" && pwd -P)"
receipt_path="$receipt_parent/$(basename "$receipt_path")"
tmp_root="$(cd "${TMPDIR:-/tmp}" && pwd -P)"
work_dir="$(mktemp -d "$tmp_root/appsurface-preflight-artifacts.XXXXXX")"
staged_receipt=""
export NUGET_PACKAGES="$work_dir/nuget-packages"
export DOTNET_CLI_HOME="$work_dir/dotnet-home"
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
mkdir -p "$NUGET_PACKAGES" "$DOTNET_CLI_HOME" "$work_dir/tool" "$work_dir/extracted"

cleanup() {
  local status=$?
  trap - EXIT
  if [[ -n "${staged_receipt:-}" && -e "$staged_receipt" ]]; then
    rm -f -- "$staged_receipt" || status=1
  fi
  if [[ -n "${work_dir:-}" && -d "$work_dir" \
    && "$work_dir" == "$tmp_root"/appsurface-preflight-artifacts.* ]]; then
    rm -rf -- "$work_dir" || status=1
    [[ ! -e "$work_dir" ]] || status=1
  fi
  exit "$status"
}
trap cleanup EXIT

verified_json="$work_dir/verified-artifacts.json"
python3 - "$artifact_dir" "$artifact_manifest" "$verified_json" <<'PY'
import hashlib
import json
import pathlib
import re
import sys
import zipfile
import xml.etree.ElementTree as ET

root = pathlib.Path(sys.argv[1]).resolve()
manifest_path = pathlib.Path(sys.argv[2]).resolve()
output_path = pathlib.Path(sys.argv[3])
try:
    raw = manifest_path.read_bytes()
    manifest = json.loads(raw)
except (OSError, UnicodeError, json.JSONDecodeError):
    raise SystemExit("artifact-manifest-invalid")
if manifest.get("schema_version") != 1 or not isinstance(manifest.get("package_version"), str):
    raise SystemExit("artifact-manifest-identity-invalid")
version = manifest["package_version"]
entries = manifest.get("entries")
if not isinstance(entries, list) or not entries:
    raise SystemExit("artifact-manifest-empty")
selected = {}
for entry in entries:
    if not isinstance(entry, dict):
        raise SystemExit("artifact-entry-invalid")
    package_id = entry.get("package_id")
    filename = entry.get("artifact_file_name")
    digest512 = entry.get("sha512")
    if not isinstance(package_id, str) or not isinstance(filename, str) or not isinstance(digest512, str):
        raise SystemExit("artifact-entry-fields-invalid")
    if pathlib.PurePath(filename).name != filename or not filename.endswith(".nupkg"):
        raise SystemExit("artifact-entry-path-invalid")
    if not re.fullmatch(r"[0-9a-fA-F]{128}", digest512):
        raise SystemExit("artifact-entry-hash-invalid")
    path = root / filename
    if path.is_symlink() or not path.is_file():
        raise SystemExit("artifact-file-missing-or-not-regular")
    sha512 = hashlib.sha512(path.read_bytes()).hexdigest()
    if sha512.lower() != digest512.lower():
        raise SystemExit("artifact-sha512-mismatch")
    with zipfile.ZipFile(path) as archive:
        nuspecs = [name for name in archive.namelist() if name.lower().endswith(".nuspec")]
        if len(nuspecs) != 1:
            raise SystemExit("artifact-nuspec-invalid")
        package = ET.fromstring(archive.read(nuspecs[0]))
    metadata = next((element for element in package.iter() if element.tag.endswith("metadata")), None)
    if metadata is None:
        raise SystemExit("artifact-metadata-missing")
    values = {child.tag.rsplit("}", 1)[-1]: (child.text or "") for child in metadata}
    if values.get("id", "").casefold() != package_id.casefold() or values.get("version") != version:
        raise SystemExit("artifact-package-identity-mismatch")
    key = package_id.casefold()
    if key in selected:
        raise SystemExit("artifact-package-duplicate")
    selected[key] = {
        "packageId": package_id,
        "version": version,
        "file": filename,
        "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
        "sha512": sha512.lower(),
        "isTool": entry.get("is_tool") is True,
        "toolCommandName": entry.get("tool_command_name", ""),
        "sourceProject": entry.get("project_path", ""),
    }
cli = selected.get("forgetrust.appsurface.cli")
provider = selected.get("forgetrust.appsurface.durable.postgresql")
if cli is None or cli["isTool"] is not True or cli["toolCommandName"] != "appsurface":
    raise SystemExit("matching-cli-tool-not-in-bundle")
if provider is None:
    raise SystemExit("matching-postgresql-provider-not-in-bundle")
output_path.write_text(json.dumps({
    "manifestSha256": hashlib.sha256(raw).hexdigest(),
    "packageVersion": version,
    "packages": selected,
}, sort_keys=True) + "\n", encoding="utf-8")
PY
[[ -s "$verified_json" ]] || fail "artifact-validation"

python3 - "$verified_json" "$artifact_dir" "$work_dir" <<'PY'
import json, pathlib, shutil, sys
record = json.loads(pathlib.Path(sys.argv[1]).read_text(encoding="utf-8"))
source = pathlib.Path(sys.argv[2])
work = pathlib.Path(sys.argv[3])
feed = work / "feed"
feed.mkdir()
for package in record["packages"].values():
    shutil.copyfile(source / package["file"], feed / package["file"])
(work / "NuGet.config").write_text(
    "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"
    "<configuration><packageSources><clear />"
    f"<add key=\"frozen-artifacts\" value=\"{feed}\" />"
    "<add key=\"nuget.org\" value=\"https://api.nuget.org/v3/index.json\" />"
    "</packageSources><packageSourceMapping>"
    "<packageSource key=\"frozen-artifacts\"><package pattern=\"ForgeTrust.*\" /></packageSource>"
    "<packageSource key=\"nuget.org\"><package pattern=\"*\" /></packageSource>"
    "</packageSourceMapping></configuration>\n", encoding="utf-8")
PY

cli_tool="$work_dir/tool/appsurface"
package_version="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["packageVersion"])' "$verified_json")"
dotnet tool install ForgeTrust.AppSurface.Cli \
  --version "$package_version" \
  --tool-path "$work_dir/tool" \
  --configfile "$work_dir/NuGet.config" \
  --ignore-failed-sources \
  --verbosity quiet >/dev/null || fail "cli-tool-install"
[[ -x "$cli_tool" ]] || fail "cli-tool-command-missing"

# The tool install above consumes the exact SHA-512-verified CLI archive from the
# frozen-only ForgeTrust feed. Restore the provider as a package reference to
# materialize its complete ForgeTrust dependency closure, then compare every
# restored ForgeTrust archive byte-for-byte with the manifest entry.
python3 - "$work_dir/closure-probe.csproj" "$package_version" <<'PY'
import pathlib, sys
path = pathlib.Path(sys.argv[1])
version = sys.argv[2]
path.write_text(
    '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework>'
    '<RestorePackagesWithLockFile>false</RestorePackagesWithLockFile></PropertyGroup><ItemGroup>'
    f'<PackageReference Include="ForgeTrust.AppSurface.Durable.PostgreSql" Version="{version}" />'
    '</ItemGroup></Project>\n', encoding="utf-8")
PY
dotnet restore "$work_dir/closure-probe.csproj" --configfile "$work_dir/NuGet.config" \
  --packages "$NUGET_PACKAGES" --verbosity quiet || fail "cli-dependency-closure-restore"
python3 - "$verified_json" "$artifact_dir" "$NUGET_PACKAGES" <<'PY'
import json, pathlib, sys
record = json.loads(pathlib.Path(sys.argv[1]).read_text(encoding="utf-8"))
artifacts = pathlib.Path(sys.argv[2])
packages = pathlib.Path(sys.argv[3])
restored = []
for package_dir in packages.iterdir():
    if not package_dir.is_dir() or not package_dir.name.casefold().startswith("forgetrust."):
        continue
    for version_dir in package_dir.iterdir():
        if not version_dir.is_dir():
            continue
        archive = version_dir / f"{package_dir.name.lower()}.{version_dir.name.lower()}.nupkg"
        if archive.is_file():
            restored.append((package_dir.name, version_dir.name, archive))
manifest = {key.casefold(): item for key, item in record["packages"].items()}
if not restored:
    raise SystemExit("restored-cli-provider-forgetrust-closure-empty")
for package_id, version, archive in restored:
    entry = manifest.get(package_id.casefold())
    if entry is None or entry["version"].casefold() != version.casefold():
        raise SystemExit("restored-cli-provider-forgetrust-dependency-not-in-frozen-manifest")
    if archive.read_bytes() != (artifacts / entry["file"]).read_bytes():
        raise SystemExit("restored-cli-provider-forgetrust-closure-archive-mismatch")
PY

provider_package="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["packages"]["forgetrust.appsurface.durable.postgresql"]["file"])' "$verified_json")"
recipe_path="$work_dir/extracted/configure-postgresql-roles.sql"
python3 - "$artifact_dir/$provider_package" "$recipe_path" <<'PY'
import pathlib, sys, zipfile
with zipfile.ZipFile(sys.argv[1]) as package:
    name = "contentFiles/any/any/configure-postgresql-roles.sql"
    if package.namelist().count(name) != 1:
        raise SystemExit("packaged-role-recipe-missing-or-ambiguous")
    pathlib.Path(sys.argv[2]).write_bytes(package.read(name))
PY

tool_help="$work_dir/preflight-help.txt"
"$cli_tool" durable schema preflight --help >"$tool_help" 2>&1 || fail "preflight-help"
grep -Fq -- "--role-pairs-file" "$tool_help" || fail "preflight-manifest-option-missing"
grep -Fq -- "--migration-owner-role" "$tool_help" || fail "preflight-owner-option-missing"

if [[ "$prepare_only" != true ]]; then
  role_pairs_file="${PREFLIGHT_ROLE_PAIRS_FILE:-$repo_root/examples/durable-postgresql/role-pairs-full-and-work-only.example.json}"
  migration_owner_role="${PREFLIGHT_MIGRATION_OWNER_ROLE:-appsurface_durable_owner}"
  [[ -f "$role_pairs_file" ]] || fail "proof-manifest-not-found"
  # The consumer receives the installed tool path and all identity values via
  # environment/arguments only; it remains source-independent of product projects.
  consumer_artifacts="$work_dir/consumer-artifacts"
  dotnet restore "$consumer_project" \
    --configfile "$work_dir/NuGet.config" --packages "$NUGET_PACKAGES" --artifacts-path "$consumer_artifacts" \
    -p:AppSurfacePackageVersion="$package_version" -m:1 -p:UseSharedCompilation=false \
    || fail "provider-consumer-restore"
python3 - "$verified_json" "$artifact_dir" "$NUGET_PACKAGES" "$consumer_artifacts/obj/PostgreSqlPreflightConsumer/project.assets.json" <<'PY'
import json, pathlib, sys
record = json.loads(pathlib.Path(sys.argv[1]).read_text(encoding="utf-8"))
artifacts = pathlib.Path(sys.argv[2])
packages = pathlib.Path(sys.argv[3])
assets = json.loads(pathlib.Path(sys.argv[4]).read_text(encoding="utf-8"))
manifest = {key.casefold(): item for key, item in record["packages"].items()}
closure = []
for key, library in assets.get("libraries", {}).items():
    package_id, version = key.rsplit("/", 1)
    if package_id.casefold().startswith("forgetrust.") and library.get("type") == "package":
        closure.append((package_id, version))
if not closure:
    raise SystemExit("restored-provider-forgetrust-closure-empty")
for package_id, version in closure:
    entry = manifest.get(package_id.casefold())
    if entry is None or entry["version"].casefold() != version.casefold():
        raise SystemExit("restored-provider-forgetrust-dependency-not-in-frozen-manifest")
    archive = packages / package_id.lower() / version.lower() / f"{package_id.lower()}.{version.lower()}.nupkg"
    if not archive.is_file() or archive.read_bytes() != (artifacts / entry["file"]).read_bytes():
        raise SystemExit("restored-provider-forgetrust-closure-archive-mismatch")
PY
  dotnet run --project "$consumer_project" --configuration Release --no-restore \
    --artifacts-path "$consumer_artifacts" -p:AppSurfacePackageVersion="$package_version" -- --self-test \
    || fail "receipt-helper-self-test"
  internal_receipt="$work_dir/consumer-receipt.json"
  consumer_args=(
    --verified-artifacts "$verified_json"
    --recipe "$recipe_path"
    --receipt "$internal_receipt"
    --source-commit "$source_commit" --run-id "$run_id" --artifact-id "$artifact_id"
    --role-pairs-file "$role_pairs_file"
    --migration-owner-role "$migration_owner_role"
    --schema10-fixture "$repo_root/Durable/consumers/PostgreSqlPreflightConsumer/schema10-two-pair.sql"
  )
  [[ -n "${PREFLIGHT_STORE_ID:-}" ]] && consumer_args+=(--store-id "$PREFLIGHT_STORE_ID")
  [[ -n "${PREFLIGHT_ACTIVE_EPOCH:-}" ]] && consumer_args+=(--active-epoch "$PREFLIGHT_ACTIVE_EPOCH")
  APP_SURFACE_PREFLIGHT_TOOL="$cli_tool" \
    DOTNET_CLI_HOME="$DOTNET_CLI_HOME" NUGET_PACKAGES="$NUGET_PACKAGES" \
    dotnet run --project "$consumer_project" --configuration Release --no-restore \
      --artifacts-path "$consumer_artifacts" -p:AppSurfacePackageVersion="$package_version" -- \
      "${consumer_args[@]}" \
      >"$work_dir/consumer.stdout" 2>"$work_dir/consumer.stderr" \
      || { tail -80 "$work_dir/consumer.stderr" >&2; tail -40 "$work_dir/consumer.stdout" >&2; fail "consumer-proof"; }
  [[ -s "$internal_receipt" ]] || fail "consumer-receipt-missing"
  staged_receipt="$receipt_parent/.$(basename "$receipt_path").${BASHPID:-$$}.stage"
  python3 - "$internal_receipt" "$staged_receipt" <<'PY'
import os, pathlib, sys
source = pathlib.Path(sys.argv[1])
destination = pathlib.Path(sys.argv[2])
with source.open("rb") as input_stream, destination.open("xb") as output_stream:
    data = input_stream.read(2 * 1024 * 1024 + 1)
    if not data or len(data) > 2 * 1024 * 1024:
        raise SystemExit("consumer-receipt-size-invalid")
    output_stream.write(data)
    output_stream.flush()
    os.fsync(output_stream.fileno())
PY
fi

if [[ -n "$work_dir" ]]; then
  rm -rf -- "$work_dir" || fail "isolated-workdir-cleanup"
  [[ ! -e "$work_dir" ]] || fail "isolated-workdir-remains"
  work_dir=""
fi

if [[ -n "$staged_receipt" ]]; then
  python3 - "$staged_receipt" "$receipt_path" <<'PY'
import os, pathlib, sys
staged = pathlib.Path(sys.argv[1])
destination = pathlib.Path(sys.argv[2])
if destination.exists() or destination.is_symlink():
    raise SystemExit("receipt-destination-exists")
os.link(staged, destination, follow_symlinks=False)
os.unlink(staged)
directory = os.open(destination.parent, os.O_RDONLY)
try:
    os.fsync(directory)
finally:
    os.close(directory)
PY
  staged_receipt=""
  echo "issue-845-proof=passed"
fi

if [[ "$prepare_only" == true ]]; then
  echo "Exact package bundle validated and CLI tool installed; no PostgreSQL proof was run."
fi
