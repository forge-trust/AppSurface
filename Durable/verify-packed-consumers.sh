#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PACKAGE_VERSION="${APP_SURFACE_PACKAGE_VERSION:-0.1.0}"
TMP_ROOT="$(cd "${TMPDIR:-/tmp}" && pwd -P)"
WORK_DIR="$(mktemp -d "$TMP_ROOT/appsurface-durable-consumers.XXXXXX")"
FEED_DIR="$WORK_DIR/feed"
CONFIG_FILE="$WORK_DIR/NuGet.config"
LEGACY_ABI_CONFIG_FILE="$WORK_DIR/LegacyAbi.NuGet.config"
ARTIFACTS_DIR="$WORK_DIR/artifacts"
export NUGET_PACKAGES="$WORK_DIR/packages"
export DOTNET_CLI_HOME="$WORK_DIR/dotnet-home"

cleanup() {
  if [[ -n "${WORK_DIR:-}" \
    && -d "$WORK_DIR" \
    && "$WORK_DIR" == "$TMP_ROOT"/appsurface-durable-consumers.* ]]; then
    rm -rf -- "$WORK_DIR"
  fi
}

trap cleanup EXIT

mkdir -p "$FEED_DIR" "$ARTIFACTS_DIR" "$NUGET_PACKAGES" "$DOTNET_CLI_HOME"

projects=(
  "ForgeTrust.AppSurface.Core/ForgeTrust.AppSurface.Core.csproj"
  "Flow/ForgeTrust.AppSurface.Flow/ForgeTrust.AppSurface.Flow.csproj"
  "Workers/ForgeTrust.AppSurface.Workers/ForgeTrust.AppSurface.Workers.csproj"
  "Durable/ForgeTrust.AppSurface.Durable/ForgeTrust.AppSurface.Durable.csproj"
  "Durable/ForgeTrust.AppSurface.Durable.Provider/ForgeTrust.AppSurface.Durable.Provider.csproj"
  "Durable/ForgeTrust.AppSurface.Durable.PostgreSql/ForgeTrust.AppSurface.Durable.PostgreSql.csproj"
  "Durable/ForgeTrust.AppSurface.Durable.Testing/ForgeTrust.AppSurface.Durable.Testing.csproj"
  "Observability/ForgeTrust.AppSurface.Observability/ForgeTrust.AppSurface.Observability.csproj"
)

packed_packages=(
  "ForgeTrust.AppSurface.Core"
  "ForgeTrust.AppSurface.Flow"
  "ForgeTrust.AppSurface.Workers"
  "ForgeTrust.AppSurface.Durable"
  "ForgeTrust.AppSurface.Durable.Provider"
  "ForgeTrust.AppSurface.Durable.PostgreSql"
  "ForgeTrust.AppSurface.Durable.Testing"
  "ForgeTrust.AppSurface.Observability"
)

fail() {
  echo "Packed Durable consumer verification failed: $1" >&2
  exit 1
}

lowercase() {
  printf '%s' "$1" | tr '[:upper:]' '[:lower:]'
}

verify_restored_package() {
  local package_id="$1"
  local package_file="$FEED_DIR/$package_id.$PACKAGE_VERSION.nupkg"
  local package_id_lower
  local package_version_lower
  local package_cache_directory
  local restored_package_file
  package_id_lower="$(lowercase "$package_id")"
  package_version_lower="$(lowercase "$PACKAGE_VERSION")"
  package_cache_directory="$NUGET_PACKAGES/$package_id_lower/$package_version_lower"
  restored_package_file="$package_cache_directory/$package_id_lower.$package_version_lower.nupkg"
  local package_metadata_file="$package_cache_directory/.nupkg.metadata"

  [[ -f "$package_file" ]] || fail "the freshly packed package is missing: $package_file"
  [[ -f "$restored_package_file" ]] || fail "the restored package is missing: $restored_package_file"
  [[ -f "$package_metadata_file" ]] || fail "NuGet restore metadata is missing: $package_metadata_file"
  grep -Fq "\"source\": \"$FEED_DIR\"" "$package_metadata_file" \
    || fail "$package_id/$PACKAGE_VERSION was not restored from the generated local feed"

  # Byte identity is stronger than package ID/version or source mapping alone and
  # avoids non-portable sha512sum/shasum command differences between CI hosts.
  cmp -s "$package_file" "$restored_package_file" \
    || fail "$package_id/$PACKAGE_VERSION differs from the freshly packed local artifact"
}

verify_assets_package() {
  local assets_file="$1"
  local package_id="$2"

  grep -Fq "\"$package_id/$PACKAGE_VERSION\":" "$assets_file" \
    || fail "$assets_file does not contain the expected $package_id/$PACKAGE_VERSION package"
}

verify_appsurface_assets_closure() {
  local assets_file="$1"
  local consumer_name="$2"
  local package_ids_file="${assets_file}.appsurface-packages"

  [[ -f "$assets_file" ]] || fail "$consumer_name restore did not produce $assets_file"
  if ! python3 - "$assets_file" "$PACKAGE_VERSION" > "$package_ids_file" <<'PY'
import json
import sys

assets_path, expected_version = sys.argv[1:]
with open(assets_path, encoding="utf-8") as stream:
    assets = json.load(stream)

packages = {}
libraries = assets.get("libraries", {})
for framework in assets.get("targets", {}).values():
    for key in framework:
        package_id, separator, version = key.rpartition("/")
        if not separator or not package_id.lower().startswith("forgetrust.appsurface."):
            continue
        if libraries.get(key, {}).get("type") != "package":
            raise SystemExit(
                f"{assets_path} resolved {package_id} as a project reference, not a NuGet package"
            )
        if version.lower() != expected_version.lower():
            raise SystemExit(
                f"{assets_path} resolved {package_id}/{version}; expected coordinated version {expected_version}"
            )
        packages[package_id.lower()] = package_id

if not packages:
    raise SystemExit(f"{assets_path} has no restored ForgeTrust.AppSurface package closure")

for package_id in sorted(packages.values(), key=str.lower):
    print(package_id)
PY
  then
    fail "$consumer_name has an AppSurface project-reference substitution or mixed package version"
  fi
  while IFS= read -r package_id; do
    [[ -n "$package_id" ]] || continue
    verify_restored_package "$package_id"
  done < "$package_ids_file"
}

# BEGIN strict PostgreSQL test process environment helper
run_strict_postgres_test_process() {
  local parent_ci="${CI:-}"
  local parent_ci_lower
  parent_ci_lower="$(printf '%s' "$parent_ci" | tr '[:upper:]' '[:lower:]')"
  if [[ "$parent_ci_lower" == "true" ]]; then
    printf '%s\n' "PostgreSQL proof mode: pinned Testcontainers image (strict CI)."
    env -u APPSURFACE_POSTGRES_TEST_CONNECTION \
      -u APPSURFACE_POSTGRES_TEST_ALLOW_SKIP \
      CI=true "$@"
  elif [[ "${APPSURFACE_POSTGRES_TEST_CONNECTION:-}" =~ [^[:space:]] ]]; then
    printf '%s\n' "PostgreSQL proof mode: explicit local PostgreSQL 16+ override (strict)."
    env -u APPSURFACE_POSTGRES_TEST_ALLOW_SKIP CI=true "$@"
  else
    printf '%s\n' "PostgreSQL proof mode: pinned Testcontainers image (strict local)."
    env -u APPSURFACE_POSTGRES_TEST_CONNECTION \
      -u APPSURFACE_POSTGRES_TEST_ALLOW_SKIP \
      CI=true "$@"
  fi
}
# END strict PostgreSQL test process environment helper

# BEGIN activation test output sanitizer
print_sanitized_activation_test_log() {
  local log_file="$1"
  python3 - "$log_file" <<'PY'
import os
import pathlib
import re
import sys

text = pathlib.Path(sys.argv[1]).read_text(encoding="utf-8", errors="replace")
connection = os.environ.get("APPSURFACE_POSTGRES_TEST_CONNECTION", "")
if connection:
    text = text.replace(connection, "<redacted-postgres-connection>")
text = re.sub(
    # The greedy password span consumes embedded @ through the authority's final delimiter.
    r"(?i)(postgres(?:ql)?://)[^:/?#\s@]+:[^/?#\s]+@",
    r"\1<redacted>@",
    text,
)
lines = text.splitlines()
password_field = re.search(r"(?i)\b(password|pwd)\s*=", text)
if password_field:
    # Omit ambiguous diagnostics rather than guessing quoted, escaped, or
    # multiline field boundaries and retaining a credential fragment.
    lines = [password_field.group(1) + "=<redacted> (diagnostic output omitted)"]
for line in lines[-120:]:
    print(line[:1200])
PY
}
# END activation test output sanitizer

# BEGIN strict PostgreSQL TRX proof helper
validate_strict_postgres_trx() {
  local trx_file="$1"
  python3 - "$trx_file" <<'PY'
import pathlib
import sys
import xml.etree.ElementTree as ET

trx_path = pathlib.Path(sys.argv[1])
if not trx_path.is_file():
    raise SystemExit("the strict PostgreSQL proof did not produce its required TRX report")
try:
    root = ET.parse(trx_path).getroot()
except ET.ParseError:
    raise SystemExit("the strict PostgreSQL proof produced an invalid TRX report")

def local_name(tag):
    return tag.rsplit("}", 1)[-1]

summary = next((item for item in root.iter() if local_name(item.tag) == "ResultSummary"), None)
counters = next(
    (item for item in summary.iter() if local_name(item.tag) == "Counters"),
    None,
) if summary is not None else None
if counters is None:
    raise SystemExit("the strict PostgreSQL proof TRX report has no test counters")

total = int(counters.attrib.get("total", "0"))
passed = int(counters.attrib.get("passed", "0"))
failed = int(counters.attrib.get("failed", "0"))
error = int(counters.attrib.get("error", "0"))
timeout = int(counters.attrib.get("timeout", "0"))
aborted = int(counters.attrib.get("aborted", "0"))
inconclusive = int(counters.attrib.get("inconclusive", "0"))
passed_but_run_aborted = int(counters.attrib.get("passedButRunAborted", "0"))
not_executable = int(counters.attrib.get("notRunnable", "0"))
disconnected = int(counters.attrib.get("disconnected", "0"))
skipped = int(counters.attrib.get("notExecuted", "0"))
unit_test_results = [item for item in root.iter() if local_name(item.tag) == "UnitTestResult"]
skipped = max(skipped, sum(item.attrib.get("outcome", "").lower() in {"notexecuted", "skipped"} for item in unit_test_results))
nonpassing_results = [item for item in unit_test_results if item.attrib.get("outcome", "").lower() != "passed"]
print(f"TRX proof counts: total={total} passed={passed} skipped={skipped} failed={failed}")
if (
    total <= 0
    or passed <= 0
    or skipped != 0
    or failed != 0
    or error != 0
    or timeout != 0
    or aborted != 0
    or inconclusive != 0
    or passed_but_run_aborted != 0
    or not_executable != 0
    or disconnected != 0
    or not unit_test_results
    or nonpassing_results
):
    raise SystemExit("the strict PostgreSQL proof requires passing tests, zero skips, and at least one test")
PY
}
# END strict PostgreSQL TRX proof helper

# BEGIN external activation test runner
run_external_activation_test_project() {
  local project="$1"
  local log_file="$2"
  local label="$3"
  local results_dir="${log_file%.log}-results"
  local trx_file="$results_dir/activation-proof.trx"
  shift 3
  local test_exit_code=0

  mkdir -p "$results_dir"
  if run_strict_postgres_test_process dotnet test "$project" "$@" \
      --results-directory "$results_dir" \
      --logger "trx;LogFileName=activation-proof.trx" > "$log_file" 2>&1; then
    if ! validate_strict_postgres_trx "$trx_file"; then
      echo "$label produced no passing, skip-free TRX proof; sanitized diagnostics follow." >&2
      print_sanitized_activation_test_log "$log_file" >&2
      fail "$label did not produce a strict passing test report"
    fi
    print_sanitized_activation_test_log "$log_file" | rg -i "Passed!|Test Run Successful|Total tests|Passed:" || true
  else
    test_exit_code=$?
  fi

  if [[ "$test_exit_code" -ne 0 ]]; then
    echo "$label failed with exit code $test_exit_code; sanitized diagnostics follow." >&2
    print_sanitized_activation_test_log "$log_file" >&2
    fail "$label did not pass"
  fi
}
# END external activation test runner

verify_external_activation_package_consumer() {
  local host_source_dir="$ROOT_DIR/examples/durable-external-activation"
  local tests_source_dir="$ROOT_DIR/examples/durable-external-activation.tests"
  local host_project="$host_source_dir/DurableExternalActivationExample.csproj"
  local tests_project="$tests_source_dir/ForgeTrust.AppSurface.DurableExternalActivationExample.Tests.csproj"
  local consumer_root="$WORK_DIR/external-activation-package-consumer"
  local consumer_examples_dir="$consumer_root/examples"
  local consumer_host_dir="$consumer_examples_dir/durable-external-activation"
  local consumer_tests_dir="$consumer_examples_dir/durable-external-activation.tests"
  local consumer_host_project="$consumer_host_dir/DurableExternalActivationExample.csproj"
  local consumer_tests_project="$consumer_tests_dir/ForgeTrust.AppSurface.DurableExternalActivationExample.Tests.csproj"
  local activation_log="$WORK_DIR/external-activation-packed-tests.log"

  [[ -f "$host_project" ]] || fail "the external-activation host project is missing: $host_project"
  [[ -f "$tests_project" ]] || fail "the external-activation test project is missing: $tests_project"

  run_external_activation_test_project \
    "$tests_project" \
    "$WORK_DIR/external-activation-source-tests.log" \
    "Source-reference external-activation host/test PostgreSQL proof" \
    --configuration Release \
    --artifacts-path "$ARTIFACTS_DIR" \
    -m:1 \
    -p:RestoreLockedMode=true \
    -p:UseSharedCompilation=false \
    --nologo

  mkdir -p "$consumer_host_dir" "$consumer_tests_dir"
python3 - "$ROOT_DIR" "$host_source_dir" "$tests_source_dir" "$consumer_root" "$consumer_host_project" "$consumer_tests_project" "${packed_packages[@]}" <<'PY'
import os
import pathlib
import shutil
import sys
import xml.etree.ElementTree as ET

repo_root = pathlib.Path(sys.argv[1]).resolve()
host_source = pathlib.Path(sys.argv[2]).resolve()
tests_source = pathlib.Path(sys.argv[3]).resolve()
consumer_root = pathlib.Path(sys.argv[4]).resolve()
host_project = pathlib.Path(sys.argv[5]).resolve()
tests_project = pathlib.Path(sys.argv[6]).resolve()
packed_packages = {item.lower(): item for item in sys.argv[7:]}

host_directory = host_project.parent
tests_directory = tests_project.parent
host_project_name = "DurableExternalActivationExample.csproj"

def copy_source_tree(source, target):
    def ignore(directory, names):
        ignored = {"bin", "obj", ".git", ".vs", "packages.lock.json", ".DS_Store"}
        return [name for name in names if name in ignored]

    shutil.copytree(source, target, dirs_exist_ok=True, ignore=ignore)

copy_source_tree(host_source, host_directory)
copy_source_tree(tests_source, tests_directory)

def local_name(tag):
    return tag.rsplit("}", 1)[-1]

def add_package_reference(group, package_id):
    element = ET.SubElement(group, "PackageReference")
    element.set("Include", package_id)

def transform_project(project_path, source_project, is_tests):
    tree = ET.parse(project_path)
    root = tree.getroot()
    groups = [element for element in root if local_name(element.tag) == "ItemGroup"]
    if not groups:
        groups = [ET.SubElement(root, "ItemGroup")]
    package_group = groups[-1]

    for group in groups:
        for child in list(group):
            kind = local_name(child.tag)
            if kind == "ProjectReference":
                include = child.get("Include", "")
                referenced_project = (source_project.parent / include).resolve()
                referenced_name = referenced_project.name
                if is_tests and referenced_name == host_project_name:
                    child.set(
                        "Include",
                        os.path.relpath(host_project, project_path.parent).replace(os.sep, "/"),
                    )
                    continue
                package_id = pathlib.Path(referenced_name).stem
                if not package_id.lower().startswith("forgetrust.appsurface."):
                    raise SystemExit(
                        f"{source_project} has an unsupported non-package project reference: {referenced_name}"
                    )
                packaged_id = packed_packages.get(package_id.lower())
                if packaged_id is None:
                    raise SystemExit(
                        f"{source_project} references {package_id}, which is not packed into the coordinated feed"
                    )
                parent = group
                parent.remove(child)
                add_package_reference(parent, packaged_id)
            elif kind == "PackageReference":
                package_id = child.get("Include", "")
                if package_id.lower().startswith("forgetrust.appsurface."):
                    packaged_id = packed_packages.get(package_id.lower())
                    if packaged_id is None:
                        raise SystemExit(
                            f"{source_project} references {package_id}, which is not packed into the coordinated feed"
                        )
                    child.set("Include", packaged_id)
                    child.attrib.pop("Version", None)
            elif kind == "Content":
                include = child.get("Include", "")
                content_source = (source_project.parent / include).resolve()
                canonical_role_recipe = (repo_root / "Durable/configure-postgresql-roles.sql").resolve()
                if content_source == canonical_role_recipe:
                    child.set("Include", str(canonical_role_recipe))

    if is_tests:
        package_names = {
            child.get("Include", "").lower()
            for group in root.iter()
            if local_name(group.tag) == "ItemGroup"
            for child in group
            if local_name(child.tag) == "PackageReference"
        }
        for dependency in (
            "Microsoft.NET.Test.Sdk",
            "Npgsql",
            "Testcontainers.PostgreSql",
            "xunit",
            "xunit.runner.visualstudio",
        ):
            if dependency.lower() not in package_names:
                add_package_reference(package_group, dependency)

        compile_items = [
            child
            for group in root.iter()
            if local_name(group.tag) == "ItemGroup"
            for child in list(group)
            if local_name(child.tag) == "Compile"
        ]
        support_files = (
            repo_root / "Durable/ForgeTrust.AppSurface.Durable.PostgreSql.Tests/PostgreSqlIntegrationTestDatabase.cs",
            repo_root / "Durable/ForgeTrust.AppSurface.Durable.PostgreSql.Tests/PostgreSqlTestContainerImage.cs",
        )
        parent_by_child = {
            child: parent
            for parent in root.iter()
            for child in list(parent)
        }
        for support_file in support_files:
            existing = [
                item
                for item in compile_items
                if pathlib.Path(item.get("Include", "").replace("\\", "/")).name == support_file.name
            ]
            if existing:
                compile_item = existing[0]
                for duplicate in existing[1:]:
                    parent_by_child[duplicate].remove(duplicate)
            else:
                compile_item = ET.SubElement(package_group, "Compile")
            compile_item.set("Include", str(support_file))
            compile_item.set("Link", f"PackedSupport/{support_file.name}")
            compile_item.attrib.pop("Update", None)

    for project_reference in (
        child
        for group in root.iter()
        if local_name(group.tag) == "ItemGroup"
        for child in group
        if local_name(child.tag) == "ProjectReference"
    ):
        referenced_name = pathlib.Path(project_reference.get("Include", "")).name
        if referenced_name.startswith("ForgeTrust.AppSurface."):
            raise SystemExit(f"{project_path} retains an AppSurface ProjectReference to {referenced_name}")

    tree.write(project_path, encoding="utf-8", xml_declaration=True)

transform_project(host_project, host_source / host_project_name, is_tests=False)
transform_project(
    tests_project,
    tests_source / "ForgeTrust.AppSurface.DurableExternalActivationExample.Tests.csproj",
    is_tests=True,
)

central_source = repo_root / "Directory.Packages.props"
central_target = consumer_root / "Directory.Packages.props"
tree = ET.parse(central_source)
root = tree.getroot()
group = next((item for item in root if local_name(item.tag) == "ItemGroup"), None)
if group is None:
    group = ET.SubElement(root, "ItemGroup")
existing_versions = {
    child.get("Include", "").lower()
    for child in group
    if local_name(child.tag) == "PackageVersion"
}
for package_id in packed_packages.values():
    if package_id.lower() in existing_versions:
        continue
    version = ET.SubElement(group, "PackageVersion")
    version.set("Include", package_id)
    version.set("Version", "$(AppSurfacePackageVersion)")
tree.write(central_target, encoding="utf-8", xml_declaration=True)
shutil.copy2(repo_root / "Directory.Build.props", consumer_root / "Directory.Build.props")
shutil.copy2(repo_root / "Directory.Build.targets", consumer_root / "Directory.Build.targets")
PY
  [[ -f "$consumer_host_project" ]] || fail "packed external-activation host project generation failed"
  [[ -f "$consumer_tests_project" ]] || fail "packed external-activation test project generation failed"

  dotnet restore "$consumer_tests_project" \
    --configfile "$CONFIG_FILE" \
    -m:1 \
    -p:AppSurfacePackageVersion="$PACKAGE_VERSION" \
    -p:RestoreLockedMode=false \
    -p:UseSharedCompilation=false

  verify_appsurface_assets_closure "$consumer_host_dir/obj/project.assets.json" "packed external-activation host"
  verify_appsurface_assets_closure "$consumer_tests_dir/obj/project.assets.json" "packed external-activation tests"

  run_external_activation_test_project \
    "$consumer_tests_project" \
    "$activation_log" \
    "Packed external-activation host/test PostgreSQL proof" \
    --configuration Release \
    --no-restore \
    -m:1 \
    -p:AppSurfacePackageVersion="$PACKAGE_VERSION" \
    -p:RestoreLockedMode=false \
    -p:UseSharedCompilation=false \
    --nologo
}

verify_external_activation_source_projects() {
  local host_project="$ROOT_DIR/examples/durable-external-activation/DurableExternalActivationExample.csproj"
  local tests_project="$ROOT_DIR/examples/durable-external-activation.tests/ForgeTrust.AppSurface.DurableExternalActivationExample.Tests.csproj"

  [[ -f "$host_project" ]] || fail "the external-activation host project is missing: $host_project"
  [[ -f "$tests_project" ]] || fail "the external-activation test project is missing: $tests_project"
}

verify_packaged_role_recipe() {
  local package_file="$FEED_DIR/ForgeTrust.AppSurface.Durable.PostgreSql.$PACKAGE_VERSION.nupkg"
  local source_recipe="$ROOT_DIR/Durable/configure-postgresql-roles.sql"
  local packaged_recipe="$WORK_DIR/configure-postgresql-roles.packaged.sql"
  local recipe_path="contentFiles/any/any/configure-postgresql-roles.sql"

  [[ -f "$source_recipe" ]] || fail "the canonical PostgreSQL role recipe is missing"
  python3 - "$package_file" "$recipe_path" "$packaged_recipe" <<'PY'
import pathlib
import sys
import zipfile

package_path, recipe_path, output_path = sys.argv[1:]
with zipfile.ZipFile(package_path) as package:
    matches = [name for name in package.namelist() if name == recipe_path]
    if len(matches) != 1:
        raise SystemExit(f"expected exactly one packaged role recipe at {recipe_path}; found {len(matches)}")
    pathlib.Path(output_path).write_bytes(package.read(matches[0]))
PY
  cmp -s "$source_recipe" "$packaged_recipe" \
    || fail "the packaged PostgreSQL role recipe differs from the canonical source bytes"
}

verify_legacy_core_provider_abi_consumer() {
  local legacy_dir="$WORK_DIR/legacy-core-provider-abi"
  local legacy_project="$legacy_dir/LegacyCoreProviderAbiConsumer.csproj"
  local legacy_probe="$legacy_dir/bin/Release/net10.0/ForgeTrust.AppSurface.Durable.LegacyCoreProviderAbiConsumer.dll"
  local legacy_packages="$WORK_DIR/legacy-abi-packages"
  local host_dir="$WORK_DIR/CoreProviderAbiHost"
  local host_project="$host_dir/CoreProviderAbiHost.csproj"
  local host_assets="$host_dir/obj/project.assets.json"

  # This locked historical compile is separate from V2WorkHarness's cached
  # preview.8 PostgreSQL runtime/artifact proof; it checks Core/Provider ABI only.
  cp -R "$ROOT_DIR/Durable/compatibility/LegacyCoreProviderAbiConsumer" "$legacy_dir"
  cp "$ROOT_DIR/Directory.Packages.props" "$legacy_dir/Directory.Packages.props"
  mkdir -p "$legacy_packages"
  NUGET_PACKAGES="$legacy_packages" dotnet restore "$legacy_project" \
    --configfile "$LEGACY_ABI_CONFIG_FILE" \
    --locked-mode \
    -m:1 \
    -p:UseSharedCompilation=false
  NUGET_PACKAGES="$legacy_packages" dotnet build "$legacy_project" \
    --configuration Release \
    --no-restore \
    -m:1 \
    -p:UseSharedCompilation=false
  [[ -f "$legacy_probe" ]] || fail "the independently compiled historical Core/Provider ABI consumer is missing"

  cp -R "$ROOT_DIR/Durable/packed-consumers/CoreProviderAbiHost" "$host_dir"
  mv "$host_dir/CoreProviderAbiHost.csproj.template" "$host_project"
  dotnet restore "$host_project" \
    --configfile "$CONFIG_FILE" \
    -m:1 \
    -p:AppSurfacePackageVersion="$PACKAGE_VERSION" \
    -p:UseSharedCompilation=false
  verify_appsurface_assets_closure "$host_assets" "current-package Core/Provider ABI host"
  dotnet build "$host_project" \
    --configuration Release \
    --no-restore \
    -m:1 \
    -p:AppSurfacePackageVersion="$PACKAGE_VERSION" \
    -p:UseSharedCompilation=false
  cp "$legacy_probe" "$host_dir/bin/Release/net10.0/ForgeTrust.AppSurface.Durable.LegacyCoreProviderAbiConsumer.dll"
  dotnet run --project "$host_project" \
    --configuration Release \
    --no-build \
    --no-restore \
    -p:AppSurfacePackageVersion="$PACKAGE_VERSION"
}

verify_packed_postgresql_execution_policy_consumer() {
  local consumer_dir="$WORK_DIR/PostgreSqlPolicyConsumer"
  local consumer_project="$consumer_dir/PostgreSqlPolicyConsumer.Tests.csproj"
  local assets_file="$consumer_dir/obj/project.assets.json"

  cp -R "$ROOT_DIR/Durable/packed-consumers/PostgreSqlPolicyConsumer" "$consumer_dir"
  mv "$consumer_dir/PostgreSqlPolicyConsumer.Tests.csproj.template" "$consumer_project"
  cp "$ROOT_DIR/Directory.Packages.props" "$consumer_dir/Directory.Packages.props"
  cp "$ROOT_DIR/Directory.Build.props" "$consumer_dir/Directory.Build.props"
  cp "$ROOT_DIR/Directory.Build.targets" "$consumer_dir/Directory.Build.targets"
  python3 - "$consumer_dir/Directory.Packages.props" <<'PY'
import sys
import xml.etree.ElementTree as ET

path = sys.argv[1]
tree = ET.parse(path)
root = tree.getroot()
local_name = lambda tag: tag.rsplit("}", 1)[-1]
group = next((item for item in root if local_name(item.tag) == "ItemGroup"), None)
if group is None:
    group = ET.SubElement(root, "ItemGroup")
package_id = "ForgeTrust.AppSurface.Durable.PostgreSql"
if not any(
    local_name(item.tag) == "PackageVersion"
    and item.get("Include", "").lower() == package_id.lower()
    for item in group
):
    version = ET.SubElement(group, "PackageVersion")
    version.set("Include", package_id)
    version.set("Version", "$(AppSurfacePackageVersion)")
tree.write(path, encoding="utf-8", xml_declaration=True)
PY
  dotnet restore "$consumer_project" \
    --configfile "$CONFIG_FILE" \
    -m:1 \
    -p:AppSurfacePackageVersion="$PACKAGE_VERSION" \
    -p:RepositoryRoot="$ROOT_DIR" \
    -p:RestoreLockedMode=false \
    -p:UseSharedCompilation=false
  verify_appsurface_assets_closure "$assets_file" "packed PostgreSQL execution-policy consumer"
  run_external_activation_test_project \
    "$consumer_project" \
    "$WORK_DIR/postgresql-execution-policy-packed-tests.log" \
    "Packed PostgreSQL execution-policy consumer proof" \
    --configuration Release \
    --no-restore \
    -m:1 \
    -p:AppSurfacePackageVersion="$PACKAGE_VERSION" \
    -p:RepositoryRoot="$ROOT_DIR" \
    -p:RestoreLockedMode=false \
    -p:UseSharedCompilation=false \
    --nologo
}

verify_external_activation_source_projects

for project in "${projects[@]}"; do
  dotnet restore "$ROOT_DIR/$project" \
    --locked-mode \
    --artifacts-path "$ARTIFACTS_DIR" \
    -m:1 \
    -p:UseSharedCompilation=false
  dotnet pack "$ROOT_DIR/$project" \
    --configuration Release \
    --no-restore \
    --artifacts-path "$ARTIFACTS_DIR" \
    --output "$FEED_DIR" \
    -m:1 \
    -p:PackageVersion="$PACKAGE_VERSION" \
    -p:UseSharedCompilation=false
  package_id="${project##*/}"
  package_id="${package_id%.csproj}"
  [[ -f "$FEED_DIR/$package_id.$PACKAGE_VERSION.nupkg" ]] \
    || fail "packing did not produce the expected $package_id/$PACKAGE_VERSION artifact"
done

verify_packaged_role_recipe

sed "s|__LOCAL_FEED__|$FEED_DIR|g" > "$CONFIG_FILE" <<'EOF'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="durable-local" value="__LOCAL_FEED__" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="durable-local">
      <package pattern="ForgeTrust.*" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
EOF

cat > "$LEGACY_ABI_CONFIG_FILE" <<'EOF'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
EOF

verify_legacy_core_provider_abi_consumer

testing_consumer_dir="$WORK_DIR/TestingConsumer"
testing_adoption_started_at="$(date +%s)"
cp -R "$ROOT_DIR/Durable/consumers/TestingConsumer" "$testing_consumer_dir"
mv "$testing_consumer_dir/TestingConsumer.csproj.template" "$testing_consumer_dir/TestingConsumer.csproj"
dotnet restore "$testing_consumer_dir/TestingConsumer.csproj" \
  --configfile "$CONFIG_FILE" \
  -m:1 \
  -p:AppSurfacePackageVersion="$PACKAGE_VERSION" \
  -p:UseSharedCompilation=false

testing_assets_file="$testing_consumer_dir/obj/project.assets.json"
[[ -f "$testing_assets_file" ]] || fail "restore did not produce $testing_assets_file"
verify_assets_package "$testing_assets_file" "ForgeTrust.AppSurface.Durable.Testing"
python3 - "$testing_assets_file" "$PACKAGE_VERSION" <<'PY'
import json
import sys

assets_path, version = sys.argv[1:]
with open(assets_path, encoding="utf-8") as stream:
    targets = next(iter(json.load(stream)["targets"].values()))

root = f"ForgeTrust.AppSurface.Durable.Testing/{version}"
if root not in targets:
    raise SystemExit(f"packed Testing graph is missing {root}")

by_name = {}
for key in targets:
    by_name.setdefault(key.rsplit("/", 1)[0].lower(), []).append(key)

seen = set()
pending = [root]
while pending:
    key = pending.pop()
    if key in seen:
        continue
    seen.add(key)
    for name in targets[key].get("dependencies", {}):
        pending.extend(by_name.get(name.lower(), ()))

for key in sorted(seen):
    name = key.rsplit("/", 1)[0].lower()
    if (
        name.startswith(("microsoft.aspnetcore.", "testcontainers", "xunit", "nunit", "mstest"))
        or name in {"microsoft.aspnetcore.app.ref", "npgsql", "microsoft.net.test.sdk", "fluentassertions", "shouldly"}
        or name.startswith("coverlet")
    ):
        raise SystemExit(f"packed Testing dependency closure contains forbidden package {key}")
print(f"Packed Testing dependency closure: {len(seen)} allowed packages")
PY
dotnet test "$testing_consumer_dir/TestingConsumer.csproj" \
  --configuration Release \
  --no-restore \
  -m:1 \
  -p:AppSurfacePackageVersion="$PACKAGE_VERSION" \
  -p:UseSharedCompilation=false
testing_adoption_seconds="$(( $(date +%s) - testing_adoption_started_at ))"
echo "Packed Testing consumer restore-to-completed-assertions: ${testing_adoption_seconds}s (upper bound on first assertion; five-minute target: 300s)"

for consumer in Adopter Provider PostgreSqlProvider; do
  consumer_dir="$WORK_DIR/$consumer"
  cp -R "$ROOT_DIR/Durable/packed-consumers/$consumer" "$consumer_dir"
  mv "$consumer_dir/$consumer.csproj.template" "$consumer_dir/$consumer.csproj"
  dotnet restore "$consumer_dir/$consumer.csproj" \
    --configfile "$CONFIG_FILE" \
    -m:1 \
    -p:AppSurfacePackageVersion="$PACKAGE_VERSION" \
    -p:UseSharedCompilation=false

  assets_file="$consumer_dir/obj/project.assets.json"
  [[ -f "$assets_file" ]] || fail "restore did not produce $assets_file"
  case "$consumer" in
    Adopter)
      verify_assets_package "$assets_file" "ForgeTrust.AppSurface.Durable"
      ;;
    Provider)
      verify_assets_package "$assets_file" "ForgeTrust.AppSurface.Durable.Provider"
      ;;
    PostgreSqlProvider)
      verify_assets_package "$assets_file" "ForgeTrust.AppSurface.Durable.PostgreSql"
      ;;
    *)
      fail "unknown packed consumer: $consumer"
      ;;
  esac

  dotnet build "$consumer_dir/$consumer.csproj" \
    --configuration Release \
    --no-restore \
    -m:1 \
    -p:AppSurfacePackageVersion="$PACKAGE_VERSION" \
    -p:UseSharedCompilation=false
  dotnet run --project "$consumer_dir/$consumer.csproj" \
    --configuration Release \
    --no-build \
    --no-restore
done

verify_external_activation_package_consumer
verify_packed_postgresql_execution_policy_consumer

for package_id in "${packed_packages[@]}"; do
  verify_restored_package "$package_id"
done

negative_root="$ROOT_DIR/Durable/packed-consumers/Negative"
python3 "$negative_root/check_sarif.py" --self-test

negative_count=0
for fixture_dir in "$negative_root"/*/; do
  [[ -f "$fixture_dir/expected.json" ]] || fail "Missing expected.json in $fixture_dir"
  fixture_name="$(basename "$fixture_dir")"
  positive_dir="$WORK_DIR/positive-$fixture_name"
  fixture_work_dir="$WORK_DIR/negative-$fixture_name"
  mkdir -p "$positive_dir" "$fixture_work_dir"
  cp "$fixture_dir/$fixture_name.csproj.template" "$positive_dir/$fixture_name.csproj"
  cp "$fixture_dir/$fixture_name.csproj.template" "$fixture_work_dir/$fixture_name.csproj"

  expected_file="$fixture_dir/expected.json"
  invalid_source="$fixture_dir/$(jq -r '.invalid.file' "$expected_file")"
  positive_source="$fixture_dir/$(jq -r '.positive.file' "$expected_file")"
  python3 "$negative_root/check_sarif.py" --manifest "$expected_file" "$invalid_source" "$positive_source"

  cp "$positive_source" "$positive_dir/Program.cs"
  dotnet restore "$positive_dir/$fixture_name.csproj" \
    --configfile "$CONFIG_FILE" \
    -p:AppSurfacePackageVersion="$PACKAGE_VERSION"
  dotnet build "$positive_dir/$fixture_name.csproj" \
    --configuration Release \
    --no-restore \
    -p:UseSharedCompilation=false \
    -p:AppSurfacePackageVersion="$PACKAGE_VERSION" \
    --nologo

  cp "$invalid_source" "$fixture_work_dir/Program.cs"
  dotnet restore "$fixture_work_dir/$fixture_name.csproj" \
    --configfile "$CONFIG_FILE" \
    -p:AppSurfacePackageVersion="$PACKAGE_VERSION"
  sarif_file="$fixture_work_dir/compiler.sarif"
  set +e
  dotnet build "$fixture_work_dir/$fixture_name.csproj" \
    --configuration Release \
    --no-restore \
    -p:UseSharedCompilation=false \
    -p:AppSurfacePackageVersion="$PACKAGE_VERSION" \
    -p:ErrorLog="$sarif_file,version=2.1" \
    --nologo
  invalid_exit=$?
  set -e
  if [[ "$invalid_exit" -eq 0 ]]; then
    echo "Negative fixture unexpectedly compiled: $fixture_name" >&2
    exit 1
  fi
  python3 "$negative_root/check_sarif.py" "$fixture_work_dir/Program.cs" "$sarif_file"
  negative_count=$((negative_count + 1))
done

[[ "$negative_count" -eq 8 ]] || {
  echo "Expected 8 negative fixtures, found $negative_count." >&2
  exit 1
}

echo "Packed Durable adopter and provider consumers compiled and ran successfully, including the four-kind admission contract; verified $negative_count exact negative fixtures."
