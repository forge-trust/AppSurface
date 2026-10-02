#!/usr/bin/env bash
# Opt-in, non-claiming image feasibility check on a native Linux x64 runner.
# The protected-base evidence workflow must not use this branch-owned result as
# a subject, isolation, artifact, or gate attestation.
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd -P)"
runner_temp="$(cd "${RUNNER_TEMP:?RUNNER_TEMP is required}" && pwd -P)"

if [[ "$(uname -s)" != Linux || "$(uname -m)" != x86_64 ]]; then
  echo 'Native Linux x64 is required for the offline subject image check.' >&2
  exit 2
fi
if [[ "$(dotnet --version)" != 10.0.401 ]]; then
  echo 'The host restore must use the image-pinned .NET SDK 10.0.401.' >&2
  exit 2
fi

work_root="$(mktemp -d "$runner_temp/appsurface-subject-image.XXXXXXXX")"
global_packages="$work_root/global-packages"
context="$work_root/context"
cold_root="$work_root/cold"
image_tag='appsurface-subject-native-validation:local'
rid_project_relative='Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests'
rid_project="$repository_root/$rid_project_relative/ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests.csproj"
mkdir -m 0700 "$global_packages" "$context" "$cold_root"

check_no_untracked_rid_locks() {
  local untracked_path
  local untracked_files
  untracked_files="$(git -C "$repository_root" ls-files --others --exclude-standard -- "$rid_project_relative")" || return 2
  while IFS= read -r untracked_path; do
    [[ -n "$untracked_path" ]] || continue
    case "${untracked_path##*/}" in
      packages.*.lock.json)
        printf 'Untracked RID-specific lock found in %s; git status --short:\n' \
          "$rid_project_relative" >&2
        git -C "$repository_root" status --short --untracked-files=all -- \
          "$rid_project_relative" >&2 || return 2
        return 1
        ;;
    esac
  done <<< "$untracked_files"
}

# This ordinary branch validation restore supplies a clean Linux package cache.
# The production evidence controller must never restore a PR head on its host.
if ! dotnet restore "$repository_root/ForgeTrust.AppSurface.slnx" \
  --locked-mode --packages "$global_packages" --verbosity quiet; then
  check_no_untracked_rid_locks || true
  exit 2
fi

# This project selects its lock file from NETCoreSdkRuntimeIdentifier. Record
# that host evaluation and explicitly verify the image's linux-x64 lock target
# before assembling or building the candidate image; locked mode never rewrites
# either lock file.
rid_properties_path="$work_root/keycloak-rid-properties.json"
rid_evidence_path="$work_root/keycloak-linux-x64-rid-preflight.json"
rid_preflight_status='failed'
rid_preflight_diagnostic='preflight-incomplete'
host_sdk_rid='unavailable'
host_lock_file='unavailable'
if ! dotnet msbuild "$rid_project" -nologo \
  -getProperty:NETCoreSdkRuntimeIdentifier,NuGetLockFilePath > "$rid_properties_path"; then
  if ! check_no_untracked_rid_locks; then
    rid_preflight_diagnostic='untracked-rid-specific-lock-file'
  else
    rid_preflight_diagnostic='msbuild-property-query-failed'
  fi
elif ! rid_properties="$(python3 -c '
import json
import sys
with open(sys.argv[1], encoding="utf-8") as source:
    result = json.load(source)
properties = result.get("Properties", result)
print("\t".join((
    str(properties.get("NETCoreSdkRuntimeIdentifier", "")),
    str(properties.get("NuGetLockFilePath", "")),
)))
' "$rid_properties_path")"; then
  if ! check_no_untracked_rid_locks; then
    rid_preflight_diagnostic='untracked-rid-specific-lock-file'
  else
    rid_preflight_diagnostic='msbuild-property-output-invalid'
  fi
else
  IFS=$'\t' read -r host_sdk_rid host_lock_file <<< "$rid_properties"
  if ! check_no_untracked_rid_locks; then
    rid_preflight_diagnostic='untracked-rid-specific-lock-file'
  elif [[ "$host_sdk_rid" != 'linux-x64' ]]; then
    rid_preflight_diagnostic='host-sdk-rid-does-not-match-image'
  elif [[ "${host_lock_file##*/}" != 'packages.linux-x64.lock.json' ]]; then
    rid_preflight_diagnostic='host-lock-file-does-not-select-linux-x64'
  elif ! git -C "$repository_root" ls-files --error-unmatch -- \
    "$rid_project_relative/${host_lock_file##*/}" >/dev/null 2>&1; then
    rid_preflight_diagnostic='selected-linux-x64-lock-is-not-tracked'
  elif ! dotnet restore "$rid_project" --locked-mode \
    --disable-parallel -m:1 --packages "$global_packages" --verbosity minimal; then
    if ! check_no_untracked_rid_locks; then
      rid_preflight_diagnostic='untracked-rid-specific-lock-file-after-linux-restore'
    else
      rid_preflight_diagnostic='serial-linux-x64-lock-selection-restore-failed'
    fi
  elif ! check_no_untracked_rid_locks; then
    rid_preflight_diagnostic='untracked-rid-specific-lock-file-after-linux-restore'
  else
    rid_preflight_status='pass'
    rid_preflight_diagnostic=''
  fi
fi
RID_EVIDENCE_PATH="$rid_evidence_path" \
RID_PREFLIGHT_STATUS="$rid_preflight_status" \
RID_PREFLIGHT_DIAGNOSTIC="$rid_preflight_diagnostic" \
RID_PROJECT="$rid_project" \
HOST_SDK_RID="$host_sdk_rid" \
HOST_LOCK_FILE="$host_lock_file" \
RID_RESTORE_TARGET='linux-x64' \
SDK_VERSION='10.0.401' \
python3 -c '
import json
import os
from pathlib import Path
record = {
    "schema": "appsurface-subject-host-rid-preflight-v1",
    "status": os.environ["RID_PREFLIGHT_STATUS"],
    "diagnostic": os.environ["RID_PREFLIGHT_DIAGNOSTIC"] or None,
    "project": os.environ["RID_PROJECT"],
    "hostNETCoreSdkRuntimeIdentifier": os.environ["HOST_SDK_RID"],
    "evaluatedNuGetLockFilePath": os.environ["HOST_LOCK_FILE"],
    "requiredLockSelectionRuntimeIdentifier": os.environ["RID_RESTORE_TARGET"],
    "restoreRuntimeArgument": None,
    "serialLockedProjectRestore": os.environ["RID_PREFLIGHT_STATUS"] == "pass",
    "sdkVersion": os.environ["SDK_VERSION"],
}
Path(os.environ["RID_EVIDENCE_PATH"]).write_text(
    json.dumps(record, indent=2, sort_keys=True) + "\n", encoding="utf-8"
)
'
if [[ "$rid_preflight_status" != 'pass' ]]; then
  printf 'Host RID evaluation: NETCoreSdkRuntimeIdentifier=%s NuGetLockFilePath=%s\n' \
    "$host_sdk_rid" "$host_lock_file" >&2
  printf 'Host RID preflight failed (%s); evidence: %s\n' \
    "$rid_preflight_diagnostic" "$rid_evidence_path" >&2
  exit 2
fi
printf 'Host RID evaluation: NETCoreSdkRuntimeIdentifier=%s NuGetLockFilePath=%s\n' \
  "$host_sdk_rid" "$host_lock_file"

python3 "$repository_root/scripts/evidence-gate-build-offline-feed.py" \
  --checkout "$repository_root" \
  --solution ForgeTrust.AppSurface.slnx \
  --global-packages "$global_packages" \
  --output "$context/locked-dependencies"
cp "$repository_root/scripts/evidence-gate-subject-entrypoint.py" \
  "$context/evidence-gate-subject-entrypoint.py"
docker build --platform linux/amd64 \
  --file "$repository_root/scripts/evidence-gate-subject-image/Containerfile" \
  --tag "$image_tag" "$context"
docker_image_identity="$(docker image inspect --format '{{.Id}}|{{.Os}}|{{.Architecture}}' "$image_tag")"
IFS='|' read -r docker_image_id docker_image_os docker_image_arch <<< "$docker_image_identity"
printf 'local image ID=%s platform=%s/%s\n' "$docker_image_id" "$docker_image_os" "$docker_image_arch"

# Podman has a separate image store. Transfer this exact Docker candidate as a
# local archive, require the image IDs to match, and smoke it before the Docker
# cold restore so the two checks report independently. This lane never claims
# eligibility or publishes the candidate.
podman_evidence_path="$work_root/rootless-podman-smoke.json"
podman_probe_path="$work_root/podman-live-scratch.json"
podman_created_inspect_path="$work_root/podman-created-inspect.json"
podman_final_inspect_path="$work_root/podman-final-inspect.json"
podman_created_checks_path="$work_root/podman-created-checks.json"
podman_final_checks_path="$work_root/podman-final-checks.json"
podman_container_name="appsurface-subject-smoke-$$"
podman_container_id='unavailable'
podman_cli_path='unavailable'
podman_version='unavailable'
podman_info='unavailable'
podman_rootless=false
podman_image_id='unavailable'
podman_status='pending'
podman_phase='preflight'
podman_diagnostic=''
podman_container_created=false
podman_create_attempted=false
podman_container_started=false
podman_zero_exit=false
podman_configuration_verified=false
podman_probe_verified=false
podman_container_removed=false
podman_cleanup_attempted=false
podman_cleanup_succeeded=false
podman_evidence_written=false
scratch_tmpfs_options='unavailable'
scratch_bytes='unavailable'
scratch_inodes='unavailable'
scratch_uid='unavailable'
scratch_gid='unavailable'
podman_runner_uid="$(id -u)"

write_podman_evidence() {
  PODMAN_EVIDENCE_PATH="$podman_evidence_path" \
  PODMAN_PROBE_PATH="$podman_probe_path" \
  PODMAN_CREATED_INSPECT_PATH="$podman_created_inspect_path" \
  PODMAN_FINAL_INSPECT_PATH="$podman_final_inspect_path" \
  PODMAN_CREATED_CHECKS_PATH="$podman_created_checks_path" \
  PODMAN_FINAL_CHECKS_PATH="$podman_final_checks_path" \
  PODMAN_STATUS="$podman_status" PODMAN_PHASE="$podman_phase" \
  PODMAN_DIAGNOSTIC="$podman_diagnostic" PODMAN_RUNNER_UID="$podman_runner_uid" \
  PODMAN_CLI_PATH="$podman_cli_path" PODMAN_VERSION="$podman_version" \
  PODMAN_INFO="$podman_info" PODMAN_ROOTLESS="$podman_rootless" \
  DOCKER_IMAGE_ID="$docker_image_id" PODMAN_IMAGE_ID="$podman_image_id" \
  IMAGE_TAG="$image_tag" PODMAN_CONTAINER_NAME="$podman_container_name" \
  PODMAN_CONTAINER_ID="$podman_container_id" \
  PODMAN_CONTAINER_CREATED="$podman_container_created" \
  PODMAN_CONTAINER_STARTED="$podman_container_started" \
  PODMAN_ZERO_EXIT="$podman_zero_exit" \
  PODMAN_CONFIGURATION_VERIFIED="$podman_configuration_verified" \
  PODMAN_PROBE_VERIFIED="$podman_probe_verified" \
  PODMAN_CONTAINER_REMOVED="$podman_container_removed" \
  PODMAN_CLEANUP_ATTEMPTED="$podman_cleanup_attempted" \
  PODMAN_CLEANUP_SUCCEEDED="$podman_cleanup_succeeded" \
  SCRATCH_TMPFS_OPTIONS="$scratch_tmpfs_options" \
  SCRATCH_BYTES="$scratch_bytes" SCRATCH_INODES="$scratch_inodes" \
  SCRATCH_UID="$scratch_uid" SCRATCH_GID="$scratch_gid" \
  IMAGE_OS="$docker_image_os" IMAGE_ARCH="$docker_image_arch" \
  python3 -c '
import json
import os
import re
from pathlib import Path

def read_json(path):
    try:
        return json.loads(Path(path).read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError):
        return None

def integer(name):
    try:
        return int(os.environ[name])
    except ValueError:
        return None

def flag(name):
    return os.environ[name] == "true"

final_objects = read_json(os.environ["PODMAN_FINAL_INSPECT_PATH"])
final = final_objects[0] if isinstance(final_objects, list) and len(final_objects) == 1 and isinstance(final_objects[0], dict) else None
observation = read_json(os.environ["PODMAN_PROBE_PATH"])
created_checks = read_json(os.environ["PODMAN_CREATED_CHECKS_PATH"])
final_checks = read_json(os.environ["PODMAN_FINAL_CHECKS_PATH"])
final_state = final.get("State") if isinstance(final, dict) else None
final_state = final_state if isinstance(final_state, dict) else {}
final_config = final.get("Config") if isinstance(final, dict) else None
final_config = final_config if isinstance(final_config, dict) else {}
final_host = final.get("HostConfig") if isinstance(final, dict) else None
final_host = final_host if isinstance(final_host, dict) else {}
final_mounts = final.get("Mounts") if isinstance(final, dict) else None
final_mounts = final_mounts if isinstance(final_mounts, list) else []
record = {
    "schema": "appsurface-subject-rootless-podman-smoke-v1",
    "status": os.environ["PODMAN_STATUS"],
    "phase": os.environ["PODMAN_PHASE"],
    "diagnostic": os.environ["PODMAN_DIAGNOSTIC"] or None,
    "claimEligible": False,
    "published": False,
    "runner": {
        "uid": integer("PODMAN_RUNNER_UID"),
        "podmanPath": os.environ["PODMAN_CLI_PATH"],
        "podmanVersion": os.environ["PODMAN_VERSION"],
        "rootlessInfo": os.environ["PODMAN_INFO"],
        "rootlessVerified": flag("PODMAN_ROOTLESS"),
    },
    "image": {
        "reference": os.environ["IMAGE_TAG"],
        "transfer": "docker-save-podman-load",
        "dockerImageId": os.environ["DOCKER_IMAGE_ID"],
        "podmanImageId": os.environ["PODMAN_IMAGE_ID"],
        "platform": os.environ["IMAGE_OS"] + "/" + os.environ["IMAGE_ARCH"],
        "identityMatched": (
            re.fullmatch(r"[0-9a-f]{64}", os.environ["DOCKER_IMAGE_ID"].removeprefix("sha256:")) is not None
            and os.environ["DOCKER_IMAGE_ID"].removeprefix("sha256:")
            == os.environ["PODMAN_IMAGE_ID"].removeprefix("sha256:")
        ),
    },
    "container": {
        "name": os.environ["PODMAN_CONTAINER_NAME"],
        "id": os.environ["PODMAN_CONTAINER_ID"],
        "created": flag("PODMAN_CONTAINER_CREATED"),
        "started": flag("PODMAN_CONTAINER_STARTED"),
        "zeroExit": flag("PODMAN_ZERO_EXIT"),
        "configurationVerified": flag("PODMAN_CONFIGURATION_VERIFIED"),
        "liveScratchVerified": flag("PODMAN_PROBE_VERIFIED"),
        "removed": flag("PODMAN_CONTAINER_REMOVED"),
        "cleanupAttempted": flag("PODMAN_CLEANUP_ATTEMPTED"),
        "cleanupSucceeded": flag("PODMAN_CLEANUP_SUCCEEDED"),
        "createdChecks": created_checks,
        "finalChecks": final_checks,
        "inspect": ({
            "networkMode": final_host.get("NetworkMode"),
            "readOnlyRootfs": final_host.get("ReadonlyRootfs"),
            "user": final_config.get("User"),
            "state": final_state.get("Status"),
            "exitCode": final_state.get("ExitCode"),
            "mounts": [
                {key: mount.get(key) for key in ("Type", "Destination", "RW")}
                for mount in final_mounts if isinstance(mount, dict)
            ],
        } if isinstance(final, dict) else None),
        "liveObservation": observation,
    },
    "scratchContract": {
        "options": os.environ["SCRATCH_TMPFS_OPTIONS"],
        "bytes": integer("SCRATCH_BYTES"),
        "inodes": integer("SCRATCH_INODES"),
        "uid": integer("SCRATCH_UID"),
        "gid": integer("SCRATCH_GID"),
    },
}
Path(os.environ["PODMAN_EVIDENCE_PATH"]).write_text(
    json.dumps(record, sort_keys=True, separators=(",", ":")) + "\n", encoding="utf-8"
)
'
}

podman_smoke_cleanup() {
  local exit_code=$?
  trap - EXIT
  if [[ "$podman_evidence_written" == true ]]; then
    exit "$exit_code"
  fi
  if [[ "$podman_create_attempted" == true && "$podman_cleanup_succeeded" != true ]]; then
    podman_cleanup_attempted=true
    if timeout --signal=TERM --kill-after=5s 30s \
      "$podman_cli_path" --remote=false rm --force --ignore \
        "$podman_container_name" >/dev/null 2>&1; then
      podman_cleanup_succeeded=true
      if [[ "$podman_container_created" == true ]]; then
        podman_container_removed=true
      fi
    else
      podman_status='failed'
      podman_phase='cleanup'
      if [[ -n "$podman_diagnostic" ]]; then
        podman_diagnostic="$podman_diagnostic;container-cleanup-failed"
      else
        podman_diagnostic='container-cleanup-failed'
      fi
      [[ "$exit_code" -ne 0 ]] || exit_code=1
    fi
  fi
  if [[ "$podman_status" == pending || "$podman_status" == running ]]; then
    podman_status='failed'
    podman_diagnostic='smoke-interrupted'
    [[ "$exit_code" -ne 0 ]] || exit_code=1
  fi
  if ! write_podman_evidence; then
    printf 'Could not write Podman smoke evidence at %s.\n' "$podman_evidence_path" >&2
    [[ "$exit_code" -ne 0 ]] || exit_code=1
  else
    podman_evidence_written=true
    printf 'Podman smoke evidence path: %s\n' "$podman_evidence_path"
    printf 'PODMAN_SMOKE_EVIDENCE='
    cat "$podman_evidence_path"
  fi
  exit "$exit_code"
}

podman_fail() {
  podman_status='failed'
  podman_phase="$1"
  podman_diagnostic="$2"
  printf 'Rootless Podman smoke failed during %s: %s\n' "$podman_phase" "$podman_diagnostic" >&2
  exit 2
}

trap podman_smoke_cleanup EXIT
if [[ "$docker_image_os/$docker_image_arch" != 'linux/amd64' ]]; then
  podman_fail 'preflight' 'candidate-image-platform-mismatch'
fi
if [[ "$podman_runner_uid" == '0' ]]; then
  podman_fail 'preflight' 'runner-is-root'
fi
if ! command -v timeout >/dev/null 2>&1; then
  podman_fail 'preflight' 'timeout-command-unavailable'
fi
podman_cli_path="$(command -v podman || true)"
if [[ -z "$podman_cli_path" ]]; then
  podman_cli_path='unavailable'
  podman_fail 'preflight' 'podman-unavailable'
fi

podman_phase='rootless-preflight'
if ! podman_info="$(timeout --signal=TERM --kill-after=5s 30s \
  "$podman_cli_path" --remote=false info --format '{{.Host.Security.Rootless}}')"; then
  podman_fail "$podman_phase" 'podman-info-failed'
fi
if [[ "$podman_info" != 'true' ]]; then
  podman_fail "$podman_phase" 'podman-is-not-rootless'
fi
podman_rootless=true
if ! podman_version="$(timeout --signal=TERM --kill-after=5s 30s \
  "$podman_cli_path" --remote=false version --format '{{.Client.Version}}')"; then
  podman_fail "$podman_phase" 'podman-version-unavailable'
fi

podman_phase='read-scratch-contract'
if ! scratch_contract="$(python3 -c '
import runpy
import sys
values = runpy.run_path(sys.argv[1])
print("|".join((
    values["SCRATCH_TMPFS_OPTIONS"],
    str(values["MAX_PROFILE_SCRATCH_BYTES"]),
    str(values["MAX_PROFILE_SCRATCH_INODES"]),
    str(values["CONTAINER_UID"]),
    str(values["CONTAINER_GID"]),
)))
' "$repository_root/scripts/evidence-gate-subject.py")"; then
  podman_fail "$podman_phase" 'launcher-scratch-contract-unavailable'
fi
IFS='|' read -r scratch_tmpfs_options scratch_bytes scratch_inodes scratch_uid scratch_gid <<< "$scratch_contract"
if [[ "$scratch_bytes" != '4294967296' || "$scratch_inodes" != '262144' \
  || "$scratch_uid" != '65532' || "$scratch_gid" != '65532' ]]; then
  podman_fail "$podman_phase" 'launcher-scratch-contract-changed'
fi

podman_phase='image-transfer'
podman_archive="$work_root/candidate-image.docker.tar"
if ! timeout --signal=TERM --kill-after=5s 180s \
  docker save --output "$podman_archive" "$image_tag"; then
  podman_fail "$podman_phase" 'docker-save-failed'
fi
if ! timeout --signal=TERM --kill-after=5s 180s \
  "$podman_cli_path" --remote=false load --input "$podman_archive"; then
  podman_fail "$podman_phase" 'podman-load-failed'
fi
if ! podman_image_id="$(timeout --signal=TERM --kill-after=5s 30s \
  "$podman_cli_path" --remote=false image inspect --format '{{.Id}}' "$image_tag")"; then
  podman_fail "$podman_phase" 'podman-image-inspect-failed'
fi
docker_image_hex="${docker_image_id#sha256:}"
podman_image_hex="${podman_image_id#sha256:}"
if [[ ! "$docker_image_hex" =~ ^[0-9a-f]{64}$ \
  || ! "$podman_image_hex" =~ ^[0-9a-f]{64}$ \
  || "$podman_image_hex" != "$docker_image_hex" ]]; then
  podman_fail "$podman_phase" 'transferred-image-identity-mismatch'
fi
rm -f "$podman_archive"

podman_phase='container-create'
podman_probe='import json
import os
import re
import sys

expected_bytes, expected_inodes, expected_uid, expected_gid = map(int, sys.argv[1:5])
mounts = {}
lines = {}
with open("/proc/self/mountinfo", encoding="utf-8") as source:
    for line in source:
        left, separator, right = line.rstrip("\n").partition(" - ")
        fields = left.split()
        if not separator or len(fields) < 6:
            raise RuntimeError("malformed mountinfo")
        destination = fields[4].replace("\\040", " ").replace("\\011", "\t")
        if destination not in ("/", "/scratch"):
            continue
        fs_fields = right.split()
        if len(fs_fields) < 3:
            raise RuntimeError("malformed mountinfo filesystem fields")
        mounts[destination] = {
            "type": fs_fields[0],
            "mountOptions": fields[5].split(","),
            "superOptions": fs_fields[2].split(","),
        }
        lines[destination] = line.rstrip("\n")
root_mount = mounts.get("/")
scratch_mount = mounts.get("/scratch")
if root_mount is None or scratch_mount is None:
    raise RuntimeError("root or scratch mount not found")
options = set(scratch_mount["mountOptions"] + scratch_mount["superOptions"])
values = dict(item.split("=", 1) for item in options if "=" in item)
size_match = re.fullmatch(r"([0-9]+)([kKmMgGtT]?)", values.get("size", ""))
size_scale = {"": 1, "k": 1024, "m": 1024 ** 2, "g": 1024 ** 3, "t": 1024 ** 4}
option_bytes = (
    int(size_match.group(1)) * size_scale[size_match.group(2).lower()]
    if size_match else None
)
stat = os.statvfs("/scratch")
actual_bytes = stat.f_blocks * stat.f_frsize
try:
    mode = int(values.get("mode", "-1"), 8)
except ValueError:
    mode = -1
checks = {
    "nonRootIdentity": os.getuid() == expected_uid and os.getgid() == expected_gid,
    "readOnlyRootMount": "ro" in root_mount["mountOptions"],
    "scratchIsTmpfs": scratch_mount["type"] == "tmpfs",
    "scratchReadWrite": "rw" in options,
    "scratchNoSuid": "nosuid" in options,
    "scratchNoDeviceNodes": "nodev" in options,
    "scratchNoSwap": "noswap" in options,
    "scratchByteOption": option_bytes == expected_bytes,
    "scratchInodeOption": values.get("nr_inodes") == str(expected_inodes),
    "scratchByteStatvfs": actual_bytes == expected_bytes,
    "scratchInodeStatvfs": stat.f_files == expected_inodes,
    "scratchMode": mode == 0o700,
    "scratchUid": values.get("uid") == str(expected_uid),
    "scratchGid": values.get("gid") == str(expected_gid),
}
record = {
    "schema": "appsurface-subject-podman-live-mount-v1",
    "uid": os.getuid(),
    "gid": os.getgid(),
    "rootMount": {**root_mount, "mountInfo": lines["/"]},
    "scratchMount": {**scratch_mount, "mountInfo": lines["/scratch"]},
    "scratchStatvfs": {
        "blockSize": stat.f_frsize,
        "blocks": stat.f_blocks,
        "bytes": actual_bytes,
        "inodes": stat.f_files,
        "freeBytes": stat.f_bavail * stat.f_frsize,
        "freeInodes": stat.f_favail,
    },
    "checks": checks,
}
print(json.dumps(record, sort_keys=True))
if not all(checks.values()):
    sys.exit(1)
'
podman_create_attempted=true
if ! podman_container_id="$(timeout --signal=TERM --kill-after=5s 30s \
  "$podman_cli_path" --remote=false create \
  --pull=never --name "$podman_container_name" --network=none \
  --pid=private --ipc=private --uts=private --cgroupns=private \
  --read-only --read-only-tmpfs=false --http-proxy=false \
  --userns="keep-id:uid=$scratch_uid,gid=$scratch_gid" \
  --user="$scratch_uid:$scratch_gid" \
  --cap-drop=ALL --security-opt=no-new-privileges \
  --pids-limit=64 --memory=256m --cpus=0.500 \
  --tmpfs "/scratch:$scratch_tmpfs_options" \
  --entrypoint=/usr/bin/python3 "$image_tag" -c "$podman_probe" \
  "$scratch_bytes" "$scratch_inodes" "$scratch_uid" "$scratch_gid")"; then
  podman_fail "$podman_phase" 'podman-container-create-failed'
fi
podman_container_created=true

verify_podman_inspect() {
  local inspect_path="$1"
  local checks_path="$2"
  local require_scratch="$3"
  python3 - "$inspect_path" "$require_scratch" "$scratch_uid" "$scratch_gid" \
    > "$checks_path" <<'PY'
import json
import sys
with open(sys.argv[1], encoding="utf-8") as source:
    inspected = json.load(source)
if not isinstance(inspected, list) or len(inspected) != 1 or not isinstance(inspected[0], dict):
    raise SystemExit("Podman inspect did not return one container object.")
container = inspected[0]
config = container.get("Config") or {}
host = container.get("HostConfig") or {}
state = container.get("State") or {}
security = host.get("SecurityOpt")
mounts = container.get("Mounts") or []
scratch = [mount for mount in mounts if isinstance(mount, dict) and mount.get("Destination") == "/scratch"]
checks = {
    "networkNone": host.get("NetworkMode") == "none",
    "readOnlyRoot": host.get("ReadonlyRootfs") is True,
    "fixedNonRootUser": config.get("User") == f"{sys.argv[3]}:{sys.argv[4]}",
    "capabilitiesDropped": host.get("CapDrop") == ["ALL"],
    "noNewPrivileges": isinstance(security, list) and any(
        value in ("no-new-privileges", "no-new-privileges:true") for value in security
    ),
}
if sys.argv[2] == "true":
    checks["scratchMountedReadWriteTmpfs"] = (
        len(scratch) == 1 and scratch[0].get("Type") == "tmpfs" and scratch[0].get("RW") is True
    )
    checks["containerExitedZero"] = state.get("Status") == "exited" and state.get("ExitCode") == 0
print(json.dumps({"checks": checks, "state": state}, sort_keys=True))
if not all(checks.values()):
    raise SystemExit("Podman inspect did not match the smoke envelope.")
PY
}

podman_phase='container-inspect-before-start'
if ! timeout --signal=TERM --kill-after=5s 30s \
  "$podman_cli_path" --remote=false inspect "$podman_container_id" > "$podman_created_inspect_path"; then
  podman_fail "$podman_phase" 'podman-created-container-inspect-failed'
fi
if ! verify_podman_inspect "$podman_created_inspect_path" \
  "$podman_created_checks_path" false; then
  podman_fail "$podman_phase" 'podman-created-container-configuration-mismatch'
fi
podman_configuration_verified=true

podman_phase='container-start'
if ! timeout --signal=TERM --kill-after=5s 30s \
  "$podman_cli_path" --remote=false start --attach "$podman_container_id" \
    2> "$work_root/podman-start.stderr" \
  | head -c 32768 > "$podman_probe_path"; then
  podman_fail "$podman_phase" 'podman-container-start-or-live-probe-failed'
fi
podman_container_started=true
podman_zero_exit=true
if ! python3 - "$podman_probe_path" <<'PY'
import json
import sys
with open(sys.argv[1], encoding="utf-8") as source:
    record = json.load(source)
if (
    record.get("schema") != "appsurface-subject-podman-live-mount-v1"
    or not isinstance(record.get("checks"), dict)
    or not record["checks"]
    or not all(record["checks"].values())
):
    raise SystemExit("The live scratch mount observation did not verify the fixed contract.")
PY
then
  podman_fail "$podman_phase" 'podman-live-scratch-contract-mismatch'
fi
podman_probe_verified=true

podman_phase='container-inspect-after-start'
if ! timeout --signal=TERM --kill-after=5s 30s \
  "$podman_cli_path" --remote=false inspect "$podman_container_id" > "$podman_final_inspect_path"; then
  podman_fail "$podman_phase" 'podman-final-container-inspect-failed'
fi
if ! verify_podman_inspect "$podman_final_inspect_path" \
  "$podman_final_checks_path" true; then
  podman_fail "$podman_phase" 'podman-final-container-state-mismatch'
fi
podman_status='pass'
podman_phase='cleanup'
podman_cleanup_attempted=true
if ! timeout --signal=TERM --kill-after=5s 30s \
  "$podman_cli_path" --remote=false rm --force --ignore \
  "$podman_container_name" >/dev/null 2>&1; then
  podman_status='failed'
  podman_diagnostic='container-cleanup-failed'
  podman_fail "$podman_phase" "$podman_diagnostic"
fi
podman_cleanup_succeeded=true
podman_container_removed=true
podman_phase='complete'
if ! write_podman_evidence; then
  podman_status='failed'
  podman_phase='evidence-write'
  podman_diagnostic='podman-evidence-write-failed'
  podman_fail "$podman_phase" "$podman_diagnostic"
fi
podman_evidence_written=true
echo 'Rootless Podman started the transferred image with network none, read-only root, and verified 4 GiB/262144-inode scratch.'
printf 'Podman smoke evidence path: %s\n' "$podman_evidence_path"
printf 'PODMAN_SMOKE_EVIDENCE='
cat "$podman_evidence_path"

# The disposable source copy keeps package extraction and MSBuild obj writes
# away from the checkout. Only this copy, its empty cache, and the fixed offline
# source config enter the no-network validation container.
mkdir -m 0700 "$cold_root/source" "$cold_root/cache" "$cold_root/home" "$cold_root/tmp"
cp -a "$repository_root/." "$cold_root/source/"
cat > "$cold_root/offline-nuget.config" <<'CONFIG'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="locked-dependencies" value="/opt/appsurface/locked-dependencies" />
  </packageSources>
</configuration>
CONFIG
sudo chown -R 65532:65532 "$cold_root"

if timeout --signal=TERM --kill-after=10s 16m \
  docker run --rm --platform linux/amd64 \
  --pull never --network none --read-only --user 65532:65532 \
  --cap-drop ALL --security-opt no-new-privileges \
  --pids-limit 256 --memory 4g --cpus 2 \
  --mount "type=bind,src=$cold_root,dst=/work" \
  --tmpfs /tmp:rw,noexec,nosuid,nodev,size=512m,mode=1777 \
  --workdir /work/source \
  --env HOME=/work/home \
  --env DOTNET_CLI_HOME=/work/home \
  --env DOTNET_CLI_TELEMETRY_OPTOUT=1 \
  --env DOTNET_NOLOGO=1 \
  --env NUGET_PACKAGES=/work/cache \
  --env NUGET_HTTP_CACHE_PATH=/work/http-cache \
  --env TMPDIR=/work/tmp \
  --entrypoint /bin/sh "$image_tag" -c '
    exec timeout --signal=TERM --kill-after=10s 15m \
      dotnet restore /work/source/ForgeTrust.AppSurface.slnx \
      --locked-mode --disable-parallel -m:1 \
      --configfile /work/offline-nuget.config \
      --source /opt/appsurface/locked-dependencies \
      --packages /work/cache --verbosity minimal
  '; then
  :
else
  cold_restore_exit_code=$?
  printf 'Docker cold restore failed (exit %s; workload limit 15m, client limit 16m).\n' \
    "$cold_restore_exit_code" >&2
  exit "$cold_restore_exit_code"
fi

if [[ -z "$(find "$cold_root/cache" -mindepth 1 -maxdepth 1 -print -quit)" ]]; then
  echo 'The offline restore produced no packages in its empty cache.' >&2
  exit 2
fi
echo 'Native Linux x64 cold-cache locked restore passed with only the embedded feed.'
