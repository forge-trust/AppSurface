#!/usr/bin/env bash
set -euo pipefail
umask 077
task_runner_started=$SECONDS
task_root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
task_repo="$task_root/repo"
task_receipts="$task_root/receipts"
mkdir -m 700 "$task_receipts"
exec > >(tee "$task_receipts/run.log") 2>&1
cd "$task_repo"
finish() {
  local code="$?" verify_code=0
  trap - EXIT
  set +e
  python3 -B "$task_root/verify-snapshot.py" > "$task_receipts/source-after.json" 2>&1
  verify_code="$?"
  printf '%s\n' "$verify_code" > "$task_receipts/source-after-exit.txt"
  git status --porcelain=v1 > "$task_receipts/status-after.txt"
  if [[ "$verify_code" != 0 ]]; then code=1; fi
  printf '%s\n' "$code" > "$task_receipts/exit-status.txt"
  exit "$code"
}
trap finish EXIT
[[ "$(uname -s)" == Linux && "$(uname -m)" == x86_64 && "$(id -u)" != 0 ]] || exit 2
for tool in dotnet node pnpm python3 git sudo setpriv timeout; do command -v "$tool" >/dev/null || exit 2; done
sudo -n true
[[ "$(node --version)" == v24.* && "$(pnpm --version)" == 11.1.3 ]] || exit 2
python3 -B "$task_root/verify-snapshot.py" > "$task_receipts/source-before.json"
git rev-parse HEAD origin/main > "$task_receipts/revisions.txt"
git status --porcelain=v1 > "$task_receipts/status-before.txt"
[[ ! -s "$task_receipts/status-before.txt" ]] || exit 2
export UseSharedCompilation=false MSBUILDDISABLENODEREUSE=1
# Same SDK/assets prerequisites as native17, narrowed to this test project.
timeout --signal=TERM --kill-after=10s 180s dotnet restore Cli/ForgeTrust.AppSurface.Cli.Tests/ForgeTrust.AppSurface.Cli.Tests.csproj --locked-mode > "$task_receipts/restore.log" 2>&1
timeout --signal=TERM --kill-after=10s 120s pnpm --dir Web install --frozen-lockfile > "$task_receipts/pnpm-install.log" 2>&1
timeout --signal=TERM --kill-after=10s 120s pnpm --dir Web run assets:build > "$task_receipts/assets-build.log" 2>&1
timeout --signal=TERM --kill-after=10s 180s dotnet build Cli/ForgeTrust.AppSurface.Cli.Tests/ForgeTrust.AppSurface.Cli.Tests.csproj --no-restore > "$task_receipts/cli-build.log" 2>&1
# Run all 13 portable fixture controls exactly once on Linux, without root.
timeout --signal=TERM --kill-after=5s 45s python3 -B tests/evidencehost-consumer/test_execution_broker_fixture.py -v > "$task_receipts/portable.log" 2>&1
python3 - "$task_receipts/portable.log" <<'PY'
import re,sys
raw=open(sys.argv[1],encoding='utf-8').read()
if not re.search(r'^Ran 13 tests in ',raw,re.M) or not re.search(r'^OK$',raw,re.M): raise SystemExit(1)
PY
task_uid="$(id -u)"; task_gid="$(id -g)"; task_subject=65533
task_dotnet="$(command -v dotnet)"; task_cache="${NUGET_PACKAGES:-$HOME/.nuget/packages}"
[[ "$task_uid" != 0 && "$task_gid" != 0 && "$task_uid" != "$task_subject" && "$task_gid" != "$task_subject" ]] || exit 2
[[ -d "$task_cache/reportgenerator/5.5.10" ]] || { printf 'Missing pinned ReportGenerator package.\n'; exit 2; }
task_groups=()
for group in $(id -G); do
  if [[ "$group" != "$task_gid" && "$group" != 0 && "$group" != "$task_subject" ]]; then task_groups+=("$group"); fi
done
task_group_args=()
if (( ${#task_groups[@]} > 0 )); then
  group_list="$(IFS=,; printf '%s' "${task_groups[*]}")"
  task_group_args=(--worker-supplementary-groups "$group_list")
fi
task_marker_args=()
for marker in CODEX_SANDBOX SANDBOX_MODE IN_SANDBOX IS_SANDBOX; do
  if printenv "$marker" >/dev/null 2>&1; then task_marker_args+=("$marker=${!marker}"); fi
done
# Reserve the entire root-command wrapper and cleanup before launching it.
# 720s covers its 630s TERM bound, 20s KILL grace, and final source verification.
[[ $((1500 - (SECONDS - task_runner_started))) -ge 720 ]] || exit 2
# Root fixture authenticates the actual non-root child PID/UID/GID. setpriv retains
# existing no-new-privileges state; this runner never disables sandbox markers.
set +e
timeout --signal=TERM --kill-after=20s 630s sudo -n /usr/bin/env "${task_marker_args[@]}" python3 -B tests/evidencehost-consumer/test_execution_broker.py \
  --worker-uid "$task_uid" --worker-gid "$task_gid" \
  --subject-uid 65533 --subject-gid 65533 "${task_group_args[@]}" \
  --dotnet "$task_dotnet" --reportgenerator-package "$task_cache/reportgenerator/5.5.10" \
  --command-timeout-seconds 600 -- /usr/bin/env "PATH=$PATH" "NUGET_PACKAGES=$task_cache" \
  UseSharedCompilation=false MSBUILDDISABLENODEREUSE=1 \
  "$task_dotnet" test "$task_repo/Cli/ForgeTrust.AppSurface.Cli.Tests/ForgeTrust.AppSurface.Cli.Tests.csproj" \
  --no-build --no-restore --filter 'FullyQualifiedName~EvidenceLinuxControlProtocolTests' \
  --results-directory "$task_receipts/trx" --logger 'trx;LogFileName=root-protocol.trx' \
  --logger 'console;verbosity=normal' > "$task_receipts/focused.log" 2>&1
task_focus_exit="$?"
set -e
printf '%s\n' "$task_focus_exit" > "$task_receipts/focused-exit.txt"
[[ "$task_focus_exit" == 0 ]] || exit "$task_focus_exit"
python3 - "$task_receipts/trx/root-protocol.trx" "$task_receipts/counts.json" <<'PY'
import json,sys,xml.etree.ElementTree as ET
root=ET.parse(sys.argv[1]).getroot(); ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
counters=root.find('t:ResultSummary/t:Counters',ns).attrib
results=root.findall('t:Results/t:UnitTestResult',ns)
if len(results)!=9 or any(r.attrib['outcome']!='Passed' for r in results): raise SystemExit(1)
if any(int(counters[k])!=v for k,v in {'total':9,'executed':9,'passed':9,'failed':0,'notExecuted':0}.items()): raise SystemExit(1)
with open(sys.argv[2],'x') as f: json.dump({'cli_counters':counters,'portable_passed':13,'coverage_credit':False,'systemd_acceptance':False,'qualification':False},f,sort_keys=True)
PY
# No XPlat collection is selected: this packet establishes IPC behavior only.
