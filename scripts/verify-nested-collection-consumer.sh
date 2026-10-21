#!/usr/bin/env bash
set -Eeuo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(git -C "$SCRIPT_DIR" rev-parse --show-toplevel)"
PROOFS_ROOT="$REPO_ROOT/.gstack/issue-868/packproof"
PACKAGE_VERSION="0.1.0-issue868"
PINNED_SKOOLIT_COMMIT="a589f164ef3346d4ecf8bf39b8a73ec71ea1cb1e"
PINNED_VIEW="src/Skoolit.Web/Views/ForwardingProof/EvidenceCandidate.cshtml"
PINNED_SCRIPT="src/Skoolit.Web/wwwroot/js/alpha-evidence-wizard.js"
COMMAND="${1:-prepare}"
RUN_DIR=""
STAGE="initializing"
START_LOCK_DIR=""
START_LOCK_ACQUIRED=false
START_OWNED_PID=""
START_OWNED_DLL=""
START_OWNED_URL=""

fail() {
    printf 'ERROR: %s\n' "$*" >&2
    if [[ -n "$RUN_DIR" && -d "$RUN_DIR" ]]; then
        printf 'Failed at stage: %s (exit 1): %s\n' "$STAGE" "$*" > "$RUN_DIR/FAILURE.txt"
    fi
    exit 1
}

on_error() {
    local status=$?
    trap - ERR
    if [[ -n "$RUN_DIR" && -d "$RUN_DIR" ]]; then
        printf 'Failed at stage: %s (exit %s)\n' "$STAGE" "$status" > "$RUN_DIR/FAILURE.txt"
    fi
    exit "$status"
}
trap on_error ERR

cleanup_start() {
    local status=$?
    trap - ERR
    if [[ -n "$START_OWNED_PID" && $status -ne 0 ]]; then
        local args recorded_pid recorded_url stopped=false
        args="$(ps -p "$START_OWNED_PID" -o args= 2>/dev/null || true)"
        if [[ "$args" == *"$START_OWNED_DLL"* ]]; then
            kill "$START_OWNED_PID" 2>/dev/null || true
            for _ in $(seq 1 20); do
                if ! kill -0 "$START_OWNED_PID" 2>/dev/null; then
                    stopped=true
                    break
                fi
                sleep 1
            done
            if ! kill -0 "$START_OWNED_PID" 2>/dev/null; then
                stopped=true
            fi
            if [[ "$stopped" == true ]]; then
                wait "$START_OWNED_PID" 2>/dev/null || true
            else
                printf 'Cleanup: owned host PID %s is still running after 20 seconds; retaining host.pid and host.url for a later stop.\n' \
                    "$START_OWNED_PID" >&2
            fi
        fi
        recorded_pid="$(cat "$RUN_DIR/host.pid" 2>/dev/null || true)"
        recorded_url="$(cat "$RUN_DIR/host.url" 2>/dev/null || true)"
        if [[ "$stopped" == true && "$recorded_pid" == "$START_OWNED_PID" ]]; then
            rm -f "$RUN_DIR/host.pid"
            if [[ -n "$recorded_url" && "$recorded_url" == "$START_OWNED_URL" ]]; then
                rm -f "$RUN_DIR/host.url"
            fi
        fi
    fi
    if [[ "$START_LOCK_ACQUIRED" == true ]]; then
        rmdir "$START_LOCK_DIR" 2>/dev/null || true
        START_LOCK_ACQUIRED=false
    fi
    return "$status"
}
trap cleanup_start EXIT

resolve_run_dir() {
    local run_id="${2:-}"
    if [[ -z "$run_id" ]]; then
        [[ -f "$PROOFS_ROOT/LATEST" ]] || fail "No prepared run. Execute $0 prepare first."
        run_id="$(cat "$PROOFS_ROOT/LATEST")"
    fi
    [[ "$run_id" != */* && "$run_id" != *..* ]] || fail "Run id must be a direct child name under $PROOFS_ROOT."
    RUN_DIR="$PROOFS_ROOT/$run_id"
    [[ -d "$RUN_DIR" ]] || fail "Packproof run does not exist: $RUN_DIR"
}

start_host() {
    local host_dir="$RUN_DIR/consumer-host"
    local dll="$host_dir/bin/Release/net10.0/RazorWireWebExample.dll"
    local port="" base_url="" pid=""
    START_LOCK_DIR="$RUN_DIR/start.lock"
    mkdir "$START_LOCK_DIR" 2>/dev/null || fail "Another start operation is already active for this run."
    START_LOCK_ACQUIRED=true
    [[ -f "$RUN_DIR/PREPARED" && -s "$RUN_DIR/receipt.md" ]] \
        || fail "Run is not completely prepared; execute $0 prepare again."
    [[ -f "$dll" ]] || fail "Copied PackageReference host has not been built: $dll"
    export NUGET_PACKAGES="$RUN_DIR/nuget-packages"

    if [[ -f "$RUN_DIR/host.pid" ]]; then
        local existing_pid existing_args
        existing_pid="$(cat "$RUN_DIR/host.pid")"
        if kill -0 "$existing_pid" 2>/dev/null; then
            existing_args="$(ps -p "$existing_pid" -o args= 2>/dev/null || true)"
            if [[ "$existing_args" == *"$dll"* ]]; then
                printf 'Validating the existing isolated PackageReference host PID %s.\n' "$existing_pid"
                pid="$existing_pid"
                base_url="$(cat "$RUN_DIR/host.url")"
                port="${base_url##*:}"
            else
                fail "Recorded PID $existing_pid is running a different process; refusing to signal it."
            fi
        else
            rm -f "$RUN_DIR/host.pid"
        fi
    fi

    if [[ -z "${pid:-}" ]]; then
        STAGE="launching isolated PackageReference host"
        port="$(python3 -c 'import socket; s=socket.socket(); s.bind(("127.0.0.1", 0)); print(s.getsockname()[1]); s.close()')"
        base_url="http://127.0.0.1:$port"
        nohup bash -c 'cd "$1" && exec env ASPNETCORE_ENVIRONMENT=Development DOTNET_ENVIRONMENT=Development ASPNETCORE_URLS="$2" dotnet "$3" --urls "$2"' \
            _ "$host_dir" "$base_url" "$dll" > "$RUN_DIR/host.log" 2>&1 < /dev/null &
        pid=$!
        START_OWNED_PID="$pid"
        START_OWNED_DLL="$dll"
        START_OWNED_URL="$base_url"
        printf '%s\n' "$pid" > "$RUN_DIR/host.pid"
        printf '%s\n' "$base_url" > "$RUN_DIR/host.url"
    fi

    local ready=false
    for _ in $(seq 1 60); do
        if ! kill -0 "$pid" 2>/dev/null; then
            cat "$RUN_DIR/host.log" >&2
            fail "Copied host exited before it became ready."
        fi
        if python3 - "$base_url/Reactivity/SkoolitV4Collections?mode=physical" <<'PY' >/dev/null 2>&1
import sys
import urllib.request
with urllib.request.urlopen(sys.argv[1], timeout=2) as response:
    if response.status != 200:
        raise SystemExit(1)
PY
        then
            ready=true
            break
        fi
        sleep 1
    done
    [[ "$ready" == true ]] || { cat "$RUN_DIR/host.log" >&2; fail "Copied host did not become ready at $base_url."; }

    python3 - "$base_url/_content/ForgeTrust.RazorWire/razorwire/form-interactions.js" "$RUN_DIR/served-form-interactions.js" <<'PY'
import pathlib
import sys
import urllib.request

with urllib.request.urlopen(sys.argv[1], timeout=10) as response:
    if response.status != 200:
        raise SystemExit(f"Expected the packed runtime asset to return 200; got {response.status}.")
    pathlib.Path(sys.argv[2]).write_bytes(response.read())
PY
    local served_sha package_sha
    served_sha="$(shasum -a 256 "$RUN_DIR/served-form-interactions.js" | awk '{print $1}')"
    package_sha="$(shasum -a 256 "$RUN_DIR/packaged-form-interactions.js" | awk '{print $1}')"
    [[ "$served_sha" == "$package_sha" ]] || fail "Served JavaScript hash differs from the exact packed asset."

    python3 - "$RUN_DIR/receipt.md" "$base_url" "$port" "$pid" "$served_sha" <<'PY'
import pathlib
import sys

receipt, base_url, port, pid, served_sha = sys.argv[1:]
with pathlib.Path(receipt).open("a") as output:
    output.write(f'''\n## Isolated host

- Host URL: {base_url}/Reactivity/SkoolitV4Collections?mode=physical
- Listener: 127.0.0.1:{port} (loopback only)
- PID: {pid} (stored in host.pid; stop only through this script's stop command)
- Content root: consumer-host/ (process starts in the copied host directory)
- GET readiness: HTTP 200
- Served URL: {base_url}/_content/ForgeTrust.RazorWire/razorwire/form-interactions.js
- Served runtime asset SHA-256: {served_sha}
- Served asset matches the embedded package asset: yes

The isolated host remains running for browser QA. Stop it with the script's `stop` command and this run ID.
''')
PY

    printf 'Packed consumer ready: %s/Reactivity/SkoolitV4Collections?mode=physical\n' "$base_url"
    printf 'Packproof receipt: %s/receipt.md\n' "$RUN_DIR"
    printf 'Host log: %s/host.log\n' "$RUN_DIR"
}

write_nuget_config() {
    python3 - "$1" "$2" <<'PY_NUGET'
import pathlib
import sys
import xml.etree.ElementTree as ET

destination, local_feed = sys.argv[1:]
configuration = ET.Element("configuration")
sources = ET.SubElement(configuration, "packageSources")
ET.SubElement(sources, "clear")
ET.SubElement(sources, "add", {"key": "issue-868-local", "value": local_feed})
ET.SubElement(sources, "add", {"key": "nuget.org", "value": "https://api.nuget.org/v3/index.json"})
mapping = ET.SubElement(configuration, "packageSourceMapping")
local = ET.SubElement(mapping, "packageSource", {"key": "issue-868-local"})
ET.SubElement(local, "package", {"pattern": "ForgeTrust.*"})
public = ET.SubElement(mapping, "packageSource", {"key": "nuget.org"})
ET.SubElement(public, "package", {"pattern": "*"})
ET.ElementTree(configuration).write(destination, encoding="utf-8", xml_declaration=True)
PY_NUGET
}

publish_prepared_run() {
    local run_id="$1"
    [[ -s "$RUN_DIR/receipt.md" ]] || fail "Prepared run receipt is missing or empty."
    touch "$RUN_DIR/PREPARED"
    printf '%s\n' "$run_id" > "$PROOFS_ROOT/LATEST.$run_id"
    mv "$PROOFS_ROOT/LATEST.$run_id" "$PROOFS_ROOT/LATEST"
}

prepare() {
    mkdir -p "$PROOFS_ROOT"
    local run_id
    run_id="$(date -u +%Y%m%dT%H%M%SZ)-$$"
    RUN_DIR="$PROOFS_ROOT/$run_id"
    mkdir -p "$RUN_DIR/local-feed" "$RUN_DIR/nuget-packages"
    export NUGET_PACKAGES="$RUN_DIR/nuget-packages"

    STAGE="recording source identity"
    git -C "$REPO_ROOT" rev-parse HEAD > "$RUN_DIR/source-commit.txt"
    git -C "$REPO_ROOT" status --short > "$RUN_DIR/source-status.txt"
    git -C "$REPO_ROOT" diff --binary HEAD > "$RUN_DIR/source-diff.patch"
    python3 - "$REPO_ROOT" "$RUN_DIR/source-inputs.json" <<'PY_SOURCE'
import hashlib
import json
import pathlib
import subprocess
import sys

root = pathlib.Path(sys.argv[1])
paths = subprocess.check_output(["git", "-C", str(root), "ls-files", "-z", "--cached", "--others", "--exclude-standard"]).split(b"\0")
inputs = {}
for raw in paths:
    if not raw:
        continue
    path = root / raw.decode()
    if path.is_file() and not path.is_symlink():
        content = path.read_bytes()
        inputs[path.relative_to(root).as_posix()] = {"sha256": hashlib.sha256(content).hexdigest(), "bytes": len(content)}
pathlib.Path(sys.argv[2]).write_text(json.dumps(inputs, indent=2, sort_keys=True) + "\n")
PY_SOURCE
    local source_asset="$REPO_ROOT/Web/ForgeTrust.RazorWire/wwwroot/razorwire/form-interactions.js"
    local pinned_view_file="$RUN_DIR/skoolit-source/$PINNED_VIEW"
    local pinned_script_file="$RUN_DIR/skoolit-source/$PINNED_SCRIPT"
    local consumer_policy="$REPO_ROOT/examples/razorwire-mvc/wwwroot/js/SkoolitV4CollectionPolicy.js"
    local consumer_model="$REPO_ROOT/examples/razorwire-mvc/Models/SkoolitV4CollectionModels.cs"
    local consumer_controller="$REPO_ROOT/examples/razorwire-mvc/Controllers/SkoolitV4CollectionController.cs"
    local consumer_view="$REPO_ROOT/examples/razorwire-mvc/Views/Reactivity/SkoolitV4CollectionEditor.cshtml"
    [[ -f "$source_asset" && -f "$consumer_policy" && -f "$consumer_model" \
        && -f "$consumer_controller" && -f "$consumer_view" ]] \
        || fail "Expected runtime or V4 consumer fixture source is missing."

    STAGE="fetching pinned Skoolit source evidence from the exact commit"
    mkdir -p "$(dirname "$pinned_view_file")" "$(dirname "$pinned_script_file")"
    local pinned_view_json="$RUN_DIR/skoolit-view-api.json"
    local pinned_script_json="$RUN_DIR/skoolit-script-api.json"
    gh api "repos/forge-trust/skoolit/contents/$PINNED_VIEW?ref=$PINNED_SKOOLIT_COMMIT" > "$pinned_view_json"
    gh api "repos/forge-trust/skoolit/contents/$PINNED_SCRIPT?ref=$PINNED_SKOOLIT_COMMIT" > "$pinned_script_json"
    python3 - "$pinned_view_json" "$pinned_view_file" "$pinned_script_json" "$pinned_script_file" <<'PY'
import base64
import hashlib
import json
import pathlib
import sys

for source_json, destination in ((sys.argv[1], sys.argv[2]), (sys.argv[3], sys.argv[4])):
    response = json.loads(pathlib.Path(source_json).read_text())
    content = base64.b64decode("".join(response["content"].split()), validate=True)
    git_blob_sha = hashlib.sha1(b"blob " + str(len(content)).encode() + b"\0" + content).hexdigest()
    if git_blob_sha != response.get("sha"):
        raise SystemExit(f"Pinned Skoolit source blob identity mismatch for {response.get('path')}.")
    pathlib.Path(destination).write_bytes(content)
    pathlib.Path(destination + ".git-blob-sha").write_text(git_blob_sha + "\n")
PY
    [[ -f "$pinned_view_file" && -f "$pinned_script_file" ]] || fail "Pinned Skoolit files were not materialized from GitHub."

    STAGE="packing coordinated local packages"
    local projects=(
        "ForgeTrust.AppSurface.Core/ForgeTrust.AppSurface.Core.csproj"
        "ForgeTrust.AppSurface.Theming/ForgeTrust.AppSurface.Theming.csproj"
        "Config/ForgeTrust.AppSurface.Config/ForgeTrust.AppSurface.Config.csproj"
        "Auth/ForgeTrust.AppSurface.Auth/ForgeTrust.AppSurface.Auth.csproj"
        "Intelligence/ForgeTrust.AppSurface.Intelligence/ForgeTrust.AppSurface.Intelligence.csproj"
        "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"
        "Web/ForgeTrust.AppSurface.Web.Tailwind/ForgeTrust.AppSurface.Web.Tailwind.csproj"
        "Web/ForgeTrust.RazorWire/ForgeTrust.RazorWire.csproj"
    )
    local project project_name log
    for project in "${projects[@]}"; do
        project_name="$(basename "$project" .csproj)"
        printf 'Packing %s %s with the normal asset freshness guard...\n' "$project_name" "$PACKAGE_VERSION"
        log="$RUN_DIR/pack-$project_name.log"
        dotnet pack "$REPO_ROOT/$project" --configuration Release --output "$RUN_DIR/local-feed" \
            "-p:PackageVersion=$PACKAGE_VERSION" \
            -p:VerifyRazorWireGeneratedAssetsBeforePack=true > "$log" 2>&1 || { cat "$log" >&2; fail "dotnet pack failed for $project_name; see $log"; }
    done

    local package="$RUN_DIR/local-feed/ForgeTrust.RazorWire.$PACKAGE_VERSION.nupkg"
    [[ -f "$package" ]] || fail "RazorWire package missing from the local dependency feed: $package"
    for package_id in ForgeTrust.AppSurface.Core ForgeTrust.AppSurface.Web ForgeTrust.AppSurface.Web.Tailwind ForgeTrust.RazorWire; do
        [[ -f "$RUN_DIR/local-feed/$package_id.$PACKAGE_VERSION.nupkg" ]] || fail "Required coordinated package absent from local feed: $package_id"
    done

    STAGE="checking package and asset identities"
    local package_hash source_asset_hash consumer_policy_hash embedded_path
    package_hash="$(shasum -a 256 "$package" | awk '{print $1}')"
    source_asset_hash="$(shasum -a 256 "$source_asset" | awk '{print $1}')"
    consumer_policy_hash="$(shasum -a 256 "$consumer_policy" | awk '{print $1}')"
    embedded_path="$(python3 - "$package" "$RUN_DIR/packaged-form-interactions.js" <<'PY'
import pathlib
import re
import sys
import zipfile

archive_path = pathlib.Path(sys.argv[1])
destination = pathlib.Path(sys.argv[2])
with zipfile.ZipFile(archive_path) as archive:
    matches = [name for name in archive.namelist() if pathlib.PurePosixPath(name).parts[-2:] == ("razorwire", "form-interactions.js")]
    if len(matches) != 1:
        raise SystemExit(f"Expected exactly one packaged form-interactions.js; found {matches!r}.")
    destination.write_bytes(archive.read(matches[0]))
    print(matches[0])
PY
)"
    local package_asset_hash
    package_asset_hash="$(shasum -a 256 "$RUN_DIR/packaged-form-interactions.js" | awk '{print $1}')"
    [[ "$source_asset_hash" == "$package_asset_hash" ]] || fail "Packed runtime asset is not byte-identical to this worktree's generated asset."
    printf '%s\n' "$package_asset_hash" > "$RUN_DIR/form-interactions.sha256"

    STAGE="checking the V4 consumer boundary and source-shaped date policy"
    python3 - "$consumer_policy" "$consumer_model" "$consumer_controller" "$consumer_view" <<'PY'
import pathlib
import re
import sys

policy_path, model_path, controller_path, view_path = map(pathlib.Path, sys.argv[1:])
policy = policy_path.read_text()
fixture_code = "\n".join(path.read_text() for path in (model_path, controller_path, view_path))

required_policy_contract = (
    "detail?.root",
    "detail?.row",
    "razorwire:form-collection:add",
    "razorwire:form-collection:duplicate",
    'select[data-v4-date-shape]',
    '[data-date-endpoint="end"]',
    "const hideEnd = !isRange && !hasEndErrors",
    'endControls.querySelectorAll("[data-valmsg-for]")',
    "endControls.hidden = hideEnd",
    "endControls.disabled = hideEnd",
)
missing = [contract for contract in required_policy_contract if contract not in policy]
if missing:
    raise SystemExit(f"V4 app-owned policy is missing expected event/temporal behavior: {missing!r}.")

# The fixture policy may react to RazorWire's existing event details and synchronize V4 date UI.
# Collection shape, binder paths, and DOM identities remain runtime-owned.
forbidden_policy_patterns = {
    "collection command ownership": r"data-rw-form-collection-(?:add|duplicate|remove)",
    "row cloning or insertion": r"\b(?:cloneNode|insertAdjacentHTML|insertAdjacentElement|appendChild|insertBefore)\s*\(",
    "row removal": r"\bremoveChild\s*\(|\.remove\s*\(",
    "name rewriting": r"(?:\.name\s*=|setAttribute\s*\(\s*['\"]name['\"])",
    "ID rewriting": r"(?:\.id\s*=|setAttribute\s*\(\s*['\"]id['\"])",
    "binder-path replacement": r"\.(?:replace|replaceAll)\s*\(",
}
for label, pattern in forbidden_policy_patterns.items():
    if re.search(pattern, policy):
        raise SystemExit(f"V4 app policy contains framework structural/name/ID rewriting ({label}).")

# The pinned V4 source does not have a maximum date count. Guard the faithful fixture against
# reintroducing the earlier illustrative eight-date limit as executable code.
date_cap_patterns = (
    r"(?:Dates|dates)\s*\.\s*(?:Count|Length)\s*(?:>=|>|==)\s*8\b",
    r"\b8\s*(?:<=|<|==)\s*(?:[^\n;]{0,80}\b(?:Dates|dates)\s*\.\s*(?:Count|Length))",
    r"\b(?:Max|Maximum|Limit)\w*(?:Date|date)\w*\s*=\s*8\b",
)
for pattern in date_cap_patterns:
    if re.search(pattern, fixture_code, re.IGNORECASE):
        raise SystemExit("Faithful V4 fixture reintroduced an unsupported eight-date cap.")
PY

    STAGE="copying and pinning PackageReference consumer host"
    local host_dir="$RUN_DIR/consumer-host"
    mkdir -p "$host_dir"
    rsync -a --exclude '/bin/' --exclude '/obj/' --exclude '/TestResults/' \
        "$REPO_ROOT/examples/razorwire-mvc/" "$host_dir/"
    python3 - "$host_dir/RazorWireWebExample.csproj" "$PACKAGE_VERSION" <<'PY'
import pathlib
import re
import sys
import xml.etree.ElementTree as ET

project_path = pathlib.Path(sys.argv[1])
version = sys.argv[2]
text = project_path.read_text()

razorwire_project = '    <ProjectReference Include="../../Web/ForgeTrust.RazorWire/ForgeTrust.RazorWire.csproj" />'
if text.count(razorwire_project) != 1:
    raise SystemExit("Copied example did not contain exactly one RazorWire ProjectReference.")
text = text.replace(
    razorwire_project,
    f'    <PackageReference Include="ForgeTrust.RazorWire" Version="[{version}]" />')

tailwind_project = '    <ProjectReference Include="../../Web/ForgeTrust.AppSurface.Web.Tailwind/ForgeTrust.AppSurface.Web.Tailwind.csproj" />'
if text.count(tailwind_project) != 1:
    raise SystemExit("Copied example did not contain exactly one Tailwind ProjectReference.")
text = text.replace(
    tailwind_project,
    f'    <PackageReference Include="ForgeTrust.AppSurface.Web.Tailwind" Version="[{version}]" />')

tailwind_import = r'..\..\Web\ForgeTrust.AppSurface.Web.Tailwind\build\ForgeTrust.AppSurface.Web.Tailwind.targets'
import_pattern = re.compile(r'<Import\b[^>]*Project="([^"]*ForgeTrust\.AppSurface\.Web\.Tailwind\\build\\ForgeTrust\.AppSurface\.Web\.Tailwind\.targets)"[^>]*/>')
import_matches = list(import_pattern.finditer(text))
if len(import_matches) != 1 or tailwind_import not in import_matches[0].group(1):
    raise SystemExit("Copied example did not contain exactly one Tailwind targets import.")
import_match = import_matches[0]
# NuGet imports the packed Tailwind build target; no repository source import remains.
text = text[:import_match.start()] + text[import_match.end():]

if "<IsExampleProject>true</IsExampleProject>" not in text:
    text = text.replace("    <ImplicitUsings>enable</ImplicitUsings>",
                        "    <ImplicitUsings>enable</ImplicitUsings>\n    <IsExampleProject>true</IsExampleProject>")
root = ET.fromstring(text)
if any(element.tag.rsplit("}", 1)[-1] == "ProjectReference" for element in root.iter()):
    raise SystemExit("Copied package consumer still contains a source ProjectReference.")
for package_id in ("ForgeTrust.RazorWire", "ForgeTrust.AppSurface.Web.Tailwind"):
    matches = [element for element in root.iter()
               if element.tag.rsplit("}", 1)[-1] == "PackageReference" and element.get("Include") == package_id]
    if len(matches) != 1 or matches[0].get("Version") != f"[{version}]":
        raise SystemExit(f"Copied host does not reference exactly one pinned {package_id} package.")
project_path.write_text(text)
PY
    cat > "$host_dir/Directory.Packages.props" <<'EOF'
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
    <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
  </PropertyGroup>
</Project>
EOF
    cat > "$host_dir/Directory.Build.props" <<'EOF'
<Project>
  <PropertyGroup>
    <IsExampleProject>true</IsExampleProject>
    <IsPackable>false</IsPackable>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
  </PropertyGroup>
</Project>
EOF
    write_nuget_config "$host_dir/NuGet.config" "$RUN_DIR/local-feed"

    STAGE="restoring and building isolated PackageReference host"
    dotnet restore "$host_dir/RazorWireWebExample.csproj" --configfile "$host_dir/NuGet.config" \
        > "$RUN_DIR/consumer-restore.log" 2>&1 || { cat "$RUN_DIR/consumer-restore.log" >&2; fail "Isolated consumer restore failed."; }
    dotnet build "$host_dir/RazorWireWebExample.csproj" --configuration Release --no-restore \
        > "$RUN_DIR/consumer-build.log" 2>&1 || { cat "$RUN_DIR/consumer-build.log" >&2; fail "Isolated consumer build failed."; }
    python3 - "$host_dir/obj/project.assets.json" "$PACKAGE_VERSION" "$RUN_DIR/nuget-packages" <<'PY'
import json
import pathlib
import re
import sys

assets = json.loads(pathlib.Path(sys.argv[1]).read_text())
version = sys.argv[2]
libraries = assets.get("libraries", {})
for identity, library in libraries.items():
    if identity.lower().startswith("forgetrust.") and library.get("type") != "package":
        raise SystemExit(f"Source-project substitution in package consumer: {identity} ({library.get('type')!r}).")
for package_id in ("ForgeTrust.RazorWire", "ForgeTrust.AppSurface.Web.Tailwind"):
    expected = f"{package_id}/{version}"
    expected_lower = expected.lower()
    identities = [(key, value) for key, value in libraries.items() if key.lower() == expected_lower]
    if not identities:
        raise SystemExit(f"Restore assets do not contain the exact package {expected}.")
    if len(identities) != 1:
        raise SystemExit(f"Restore assets contain duplicate case-insensitive identities for {expected}.")
    matching = identities[0][1]
    if matching.get("type") != "package":
        raise SystemExit(f"Expected {expected} to restore as a NuGet package, got {matching.get('type')!r}.")
    expected_package_path = f"{package_id.lower()}/{version.lower()}"
    if matching.get("path", "").lower() != expected_package_path:
        raise SystemExit(f"Expected package path {expected_package_path}, got {matching.get('path')!r}.")
    package_prefix = f"{package_id.lower()}/"
    if any(key.lower().startswith(package_prefix) and key.lower() != expected_lower for key in libraries):
        display_package = "RazorWire" if package_id == "ForgeTrust.RazorWire" else package_id
        raise SystemExit(f"Restore assets contain an unexpected {display_package} package version.")

    dependencies = assets.get("project", {}).get("frameworks", {}).get("net10.0", {}).get("dependencies", {})
    dependency_matches = [value for key, value in dependencies.items() if key.lower() == package_id.lower()]
    if len(dependency_matches) != 1:
        normalized_range = ""
    else:
        dependency = dependency_matches[0]
        normalized_range = dependency.get("version", "") if isinstance(dependency, dict) else ""
    if not re.fullmatch(rf"\[{re.escape(version)}(?:,\s*{re.escape(version)})?\]", normalized_range):
        raise SystemExit(f"Copied host project.assets.json does not pin {package_id} exactly to {version}; got {normalized_range!r}.")

    package_cache_entry = pathlib.Path(sys.argv[3]) / package_id.lower() / version.lower()
    if not package_cache_entry.is_dir() or not (package_cache_entry / ".nupkg.metadata").is_file():
        raise SystemExit(f"Exact package was not restored into this run's private NuGet cache: {package_cache_entry}.")
PY

    local asset_relative source_commit
    asset_relative="${source_asset#"$REPO_ROOT/"}"
    source_commit="$(cat "$RUN_DIR/source-commit.txt")"
    python3 - "$RUN_DIR/receipt.md" "$source_commit" "$PACKAGE_VERSION" "$package_hash" "$PINNED_SKOOLIT_COMMIT" \
        "$asset_relative" "$source_asset_hash" "$embedded_path" "$package_asset_hash" "$consumer_policy_hash" \
        "$pinned_view_file" "$pinned_script_file" "$RUN_DIR/nuget-packages" <<'PY'
import hashlib
import pathlib
import sys

(destination, source_commit, package_version, package_hash, skoolit_commit, asset_relative, source_asset_hash,
 embedded_path, package_asset_hash, policy_hash, view_path, script_path, cache_path) = sys.argv[1:]
view = pathlib.Path(view_path)
script = pathlib.Path(script_path)
view_hash = hashlib.sha256(view.read_bytes()).hexdigest()
script_hash = hashlib.sha256(script.read_bytes()).hexdigest()
view_blob = pathlib.Path(view_path + ".git-blob-sha").read_text().strip()
script_blob = pathlib.Path(script_path + ".git-blob-sha").read_text().strip()
receipt = f'''# Skoolit V4 exact-package consumer proof

## Producer and package identity

- Worktree commit: {source_commit}
- Worktree status snapshot: source-status.txt
- Tracked source changes: source-diff.patch (binary-capable diff against HEAD)
- Producer input bytes: source-inputs.json (SHA-256 and length of tracked and nonignored new files; ignored build outputs are excluded)
- Package ID/version: ForgeTrust.RazorWire {package_version}
- Package file: local-feed/ForgeTrust.RazorWire.{package_version}.nupkg
- Package SHA-256: {package_hash}
- Pack command: `dotnet pack` in Release for the coordinated local dependency graph; see `pack-*.log`.
- RazorWire freshness guard: enabled with `VerifyRazorWireGeneratedAssetsBeforePack=true`; pack succeeded without a bypass.
- Coordinated packages include ForgeTrust.AppSurface.Core, ForgeTrust.AppSurface.Web, and ForgeTrust.RazorWire; internal transitive project-reference packages are also in the isolated local feed.
- Source generated asset: `{asset_relative}`
- Source asset SHA-256: {source_asset_hash}
- Asset path inside the nupkg: `{embedded_path}`
- Embedded package asset SHA-256: {package_asset_hash}
- Source and embedded asset hashes match: yes
- V4 policy source: `examples/razorwire-mvc/wwwroot/js/SkoolitV4CollectionPolicy.js`
- V4 policy SHA-256: {policy_hash}
- Policy contract checks: existing add/duplicate event details (`detail.root`/`detail.row`), V4 temporal feedback, and end-control visibility/submission with validation-error recovery passed.
- Structural-ownership checks: no application-owned collection add/duplicate/remove commands, row cloning/insertion/removal, binder-path replacement, or `name`/`id` rewriting found in the V4 policy.
- Date-count policy check: no executable eight-date cap found in the faithful V4 model, controller, or view.

## Consumer host

- Copied host: `consumer-host/`
- Package reference: `<PackageReference Include="ForgeTrust.RazorWire" Version="[{package_version}]" />`
- Tailwind build/package dependency: `<PackageReference Include="ForgeTrust.AppSurface.Web.Tailwind" Version="[{package_version}]" />`
- Restore sources: this run's local feed for all `ForgeTrust.*` packages and nuget.org for external packages.
- NuGet package cache: this run's private `{cache_path}` directory, isolated from global cache entries.
- All source ProjectReferences and repository Tailwind imports in copied project: removed
- Restore: passed; `consumer-restore.log`
- Release build: passed; `consumer-build.log`
- Exact package identity in `obj/project.assets.json`: verified as package `{package_version}` from this run's isolated cache.
- Host route: `/Reactivity/SkoolitV4Collections?mode=physical`

## Pinned consumer and adaptation

- Skoolit source commit: {skoolit_commit}
- Pinned view: `src/Skoolit.Web/Views/ForwardingProof/EvidenceCandidate.cshtml` (Git blob SHA-1 `{view_blob}`, SHA-256 `{view_hash}`)
- Pinned page script: `src/Skoolit.Web/wwwroot/js/alpha-evidence-wizard.js` (Git blob SHA-1 `{script_blob}`, SHA-256 `{script_hash}`)
- Evidence copies: `skoolit-source/src/Skoolit.Web/Views/ForwardingProof/EvidenceCandidate.cshtml` and `skoolit-source/src/Skoolit.Web/wwwroot/js/alpha-evidence-wizard.js`, fetched from the immutable Skoolit commit above.
- Renderer adaptation: before rendering a POST, the adapter requires action marker count to equal the bound action count and each action's date marker count to equal its bound date count; each owner-local list must contain nonblank keys unique under ASP.NET's case-insensitive field-path comparison. A mismatch returns HTTP 400. Accepted keys retain their posted strings and marker order, and ModelState remains intact, preserving attempted values and error paths without ordinal rekeying.
- Consumer behavior retained in the fixture source: V4 temporal parts and optional source years, Discrete end-part rejection, source unsaved-date feedback, ordinary MVC submission, and server-side no-JavaScript commands. Browser/no-JavaScript round-trip verification remains pending.
- Persistence: none in the fixture.
- Adoption claim: this isolated version-bound proof does not establish live Skoolit adoption.

This receipt records exact package preparation, copied-host build, and runtime asset serving only. It does not claim browser interaction or the sparse invalid/resubmit/no-JavaScript sequence; main-owned browser QA should use the URL emitted by `start` and run the V4 tests against this copied PackageReference host.
'''
pathlib.Path(destination).write_text(receipt)
PY
    STAGE="publishing completed preparation"
    publish_prepared_run "$run_id"
    printf 'PackageReference host built. Run %s start %s to launch the isolated host.\n' "$0" "$run_id"
    printf 'Receipt: %s/receipt.md\n' "$RUN_DIR"
}

case "$COMMAND" in
    prepare)
        prepare
        ;;
    start)
        resolve_run_dir "$@"
        start_host
        ;;
    stop)
        resolve_run_dir "$@"
        [[ -f "$RUN_DIR/host.pid" ]] || fail "No host PID is recorded for $RUN_DIR."
        pid="$(cat "$RUN_DIR/host.pid")"
        dll="$RUN_DIR/consumer-host/bin/Release/net10.0/RazorWireWebExample.dll"
        args="$(ps -p "$pid" -o args= 2>/dev/null || true)"
        if [[ "$args" != *"$dll"* ]]; then
            if kill -0 "$pid" 2>/dev/null; then
                fail "Recorded PID $pid does not identify this isolated host; refusing to signal it."
            fi
            rm -f "$RUN_DIR/host.pid"
            printf 'Isolated host is already stopped.\n'
        else
            kill "$pid"
            for _ in $(seq 1 20); do
                kill -0 "$pid" 2>/dev/null || break
                sleep 1
            done
            kill -0 "$pid" 2>/dev/null && fail "Host PID $pid did not exit after SIGTERM."
            rm -f "$RUN_DIR/host.pid"
            printf 'Stopped isolated host PID %s.\n' "$pid"
        fi
        ;;
    *)
        fail "Usage: $0 [prepare|start [run-id]|stop [run-id]]"
        ;;
esac
