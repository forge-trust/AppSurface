#!/usr/bin/env bash
set -euo pipefail
# Run only in this bundle's disposable native Linux x64 checkout.
task_repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/repo" && pwd)"
task_receipts="$(dirname "$task_repo")/receipts"
mkdir -p "$task_receipts"
exec > >(tee "$task_receipts/run.log") 2>&1
cd "$task_repo"
task_finish() {
  local task_exit="$?"
  trap - EXIT
  set +e
  if [[ -d TestResults/coverage-merged ]]; then
    cp -a TestResults/coverage-merged "$task_receipts/coverage-merged"
  fi
  python3 ../verify-snapshot.py > "$task_receipts/source-after.txt" 2>&1
  local task_source_exit="$?"
  printf '%s\n' "$task_source_exit" > "$task_receipts/source-after-exit.txt"
  git status --porcelain=v1 > "$task_receipts/status-after.txt"
  if [[ "$task_exit" == 0 && "$task_source_exit" != 0 ]]; then task_exit=1; fi
  printf '%s\n' "$task_exit" > "$task_receipts/exit-status.txt"
  exit "$task_exit"
}
trap task_finish EXIT
[[ "$(uname -s)" == Linux && "$(uname -m)" == x86_64 && "$(id -u)" != 0 ]] || { printf 'Requires native Linux x64 and a non-root runner.\n' >&2; exit 2; }
for task_tool in dotnet docker node pnpm python3 git sudo setpriv timeout pwsh; do command -v "$task_tool" >/dev/null || { printf 'Missing prerequisite: %s\n' "$task_tool" >&2; exit 2; }; done
sudo -n true
docker info >/dev/null
[[ "$(node --version)" == v24.* ]] || { printf 'Use Node 24.\n' >&2; exit 2; }
[[ "$(pnpm --version)" == 11.1.3 ]] || { printf 'Use pnpm 11.1.3.\n' >&2; exit 2; }
python3 ../verify-snapshot.py | tee "$task_receipts/source-before.txt"
git rev-parse HEAD origin/main > "$task_receipts/revisions.txt"
git status --porcelain=v1 > "$task_receipts/status-before.txt"
[[ ! -s "$task_receipts/status-before.txt" ]] || { printf 'Snapshot checkout must be clean.\n' >&2; exit 2; }
# Compiler process isolation preserves source, selection, thresholds and gate policy.
export UseSharedCompilation=false MSBUILDDISABLENODEREUSE=1
python3 -B tests/evidencehost-consumer/test_linux_launcher.py 2>&1 | tee "$task_receipts/launcher.log"
python3 -B tests/evidencehost-consumer/test_runtime_proof.py 2>&1 | tee "$task_receipts/proof-driver.log"
dotnet restore ForgeTrust.AppSurface.slnx --locked-mode 2>&1 | tee "$task_receipts/restore.log"
dotnet restore tests/evidencehost-consumer/LifecycleWorker/LifecycleWorker.csproj --locked-mode 2>&1 | tee "$task_receipts/lifecycle-restore.log"
dotnet restore tests/evidencehost-consumer/ControlProtocolWorker/ControlProtocolWorker.csproj --locked-mode 2>&1 | tee "$task_receipts/control-restore.log"
pnpm --dir Web install --frozen-lockfile 2>&1 | tee "$task_receipts/pnpm-install.log"
pnpm --dir Web run assets:build 2>&1 | tee "$task_receipts/assets-build.log"
dotnet build Cli/ForgeTrust.AppSurface.Cli.Tests/ForgeTrust.AppSurface.Cli.Tests.csproj --no-restore 2>&1 | tee "$task_receipts/cli-build.log"
dotnet build Aspire/ForgeTrust.AppSurface.Aspire.Tests/ForgeTrust.AppSurface.Aspire.Tests.csproj --no-restore 2>&1 | tee "$task_receipts/aspire-build.log"
dotnet build tests/evidencehost-consumer/LifecycleWorker/LifecycleWorker.csproj --no-restore 2>&1 | tee "$task_receipts/lifecycle-build.log"
dotnet build tests/evidencehost-consumer/ControlProtocolWorker/ControlProtocolWorker.csproj --no-restore 2>&1 | tee "$task_receipts/control-build.log"
dotnet build Web/ForgeTrust.RazorWire.IntegrationTests/ForgeTrust.RazorWire.IntegrationTests.csproj --no-restore 2>&1 | tee "$task_receipts/browser-build.log"
# Browser dependencies and binaries are installed using the repository's pinned Playwright payload.
pwsh Web/ForgeTrust.RazorWire.IntegrationTests/bin/Debug/net10.0/playwright.ps1 install --with-deps chromium 2>&1 | tee "$task_receipts/playwright.log"
python3 -B tests/evidencehost-consumer/test_lifecycle_worker.py 2>&1 | tee "$task_receipts/lifecycle.log"
sudo -n python3 -B tests/evidencehost-consumer/test_control_protocol.py --worker-dll "$task_repo/tests/evidencehost-consumer/ControlProtocolWorker/bin/Debug/net10.0/EvidenceHost.ControlProtocolWorker.dll" 2>&1 | tee "$task_receipts/control.log"
task_dotnet="$(command -v dotnet)"
task_cache="${NUGET_PACKAGES:-$HOME/.nuget/packages}"
task_uid="$(id -u)"
task_gid="$(id -g)"
task_subject=65533
[[ "$task_subject" != "$task_uid" && "$task_subject" != "$task_gid" ]] || { printf 'Choose a disjoint fixture subject identity.\n' >&2; exit 2; }
task_groups=()
for task_group in $(id -G); do
  if [[ "$task_group" != "$task_gid" && "$task_group" != 0 && "$task_group" != "$task_subject" ]]; then task_groups+=("$task_group"); fi
done
task_group_args=()
if (( ${#task_groups[@]} > 0 )); then
  task_group_list="$(IFS=,; printf '%s' "${task_groups[*]}")"
  task_group_args=(--worker-supplementary-groups "$task_group_list")
fi
# Preserve explicit sandbox markers through sudo and the fixture. Never disable the guard.
task_marker_args=()
for task_marker in CODEX_SANDBOX SANDBOX_MODE IN_SANDBOX IS_SANDBOX; do
  if printenv "$task_marker" >/dev/null 2>&1; then task_marker_args+=("$task_marker=${!task_marker}"); fi
done
# This exact wrapper is run with its documented local defaults and origin/main comparison.
# Its command is unchanged; the root broker only supplies test-owned Unix-socket fixtures.
timeout --signal=TERM --kill-after=30s 4200s sudo -n /usr/bin/env "${task_marker_args[@]}" python3 -B tests/evidencehost-consumer/test_execution_broker.py \
  --worker-uid "$task_uid" --worker-gid "$task_gid" \
  --subject-uid "$task_subject" --subject-gid "$task_subject" \
  "${task_group_args[@]}" --dotnet "$task_dotnet" \
  --reportgenerator-package "$task_cache/reportgenerator/5.5.10" \
  -- /usr/bin/env "PATH=$PATH" "NUGET_PACKAGES=$task_cache" "PLAYWRIGHT_BROWSERS_PATH=${PLAYWRIGHT_BROWSERS_PATH:-$HOME/.cache/ms-playwright}" UseSharedCompilation=false MSBUILDDISABLENODEREUSE=1 ./scripts/coverage-solution.sh \
  2>&1 | tee "$task_receipts/coverage.log"
python3 ../verify-snapshot.py
git status --porcelain=v1 > "$task_receipts/status-after.txt"
printf 'Required native coverage run passed. This bundle does not supply systemd/CI acceptance or Trusted authority.\n'
