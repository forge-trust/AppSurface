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
  if [[ "$verify_code" != 0 ]]; then
    code=1
    # Bounded observation only: never repair source or override the verifier failure.
    timeout --signal=TERM --kill-after=1s 6s python3 -B - "$task_root/snapshot.json" "$task_repo" > "$task_receipts/source-drift.json" <<'PYDRIFT'
import hashlib,json,os,stat,sys,time
result={"expected_files":0,"inspected_files":0,"changed_files":0,"content_drift":0,"mode_drift":0,
        "unavailable_files":0,"scan_complete":False,"records_truncated":False,"records":[],"scan_error":None}
started=time.monotonic(); total_bytes=0; root_fd=None
try:
    with open(sys.argv[1],"rb") as stream: raw=stream.read(8*1024*1024+1)
    if len(raw)>8*1024*1024: raise ValueError()
    record=json.loads(raw); expected=record["source_sha256"]; modes=record["source_modes"]
    if len(expected)>10000 or set(expected)!=set(modes): raise ValueError()
    result["expected_files"]=len(expected)
    root_fd=os.open(sys.argv[2],os.O_RDONLY|os.O_DIRECTORY|os.O_NOFOLLOW)
    for name in sorted(expected):
        if time.monotonic()-started>=4: result["scan_error"]="deadline"; break
        parts=name.split("/")
        if len(name)>512 or any(part in ("",".","..") for part in parts): raise ValueError()
        item={"path":name,"expected_sha256":expected[name],"expected_mode":modes[name],
              "actual_sha256":None,"actual_mode":None,"state":"unavailable"}
        directory=os.dup(root_fd); fd=None
        try:
            for part in parts[:-1]:
                next_fd=os.open(part,os.O_RDONLY|os.O_DIRECTORY|os.O_NOFOLLOW,dir_fd=directory)
                os.close(directory); directory=next_fd
            before=os.stat(parts[-1],dir_fd=directory,follow_symlinks=False)
            item["actual_mode"]=format(stat.S_IMODE(before.st_mode),"04o")
            if not stat.S_ISREG(before.st_mode) or before.st_size>16*1024*1024: raise ValueError()
            fd=os.open(parts[-1],os.O_RDONLY|os.O_NOFOLLOW|os.O_NONBLOCK,dir_fd=directory)
            opened=os.fstat(fd)
            if (before.st_dev,before.st_ino)!= (opened.st_dev,opened.st_ino) or not stat.S_ISREG(opened.st_mode): raise ValueError()
            digest=hashlib.sha256(); count=0
            while True:
                if time.monotonic()-started>=4: raise TimeoutError()
                chunk=os.read(fd,65536)
                if not chunk: break
                count+=len(chunk); total_bytes+=len(chunk)
                if count>16*1024*1024 or total_bytes>128*1024*1024: raise ValueError()
                digest.update(chunk)
            after=os.fstat(fd); named=os.stat(parts[-1],dir_fd=directory,follow_symlinks=False)
            identity=lambda value:(value.st_dev,value.st_ino,value.st_mode,value.st_size,value.st_mtime_ns,value.st_ctime_ns)
            if identity(before)!=identity(after) or identity(after)!=identity(named): raise ValueError()
            item["actual_sha256"]=digest.hexdigest(); item["state"]="observed"
        except (OSError,ValueError,TimeoutError):
            result["unavailable_files"]+=1
        finally:
            if fd is not None: os.close(fd)
            os.close(directory)
        result["inspected_files"]+=1
        content=item["actual_sha256"] is not None and item["actual_sha256"]!=expected[name]
        mode=item["actual_mode"] is not None and item["actual_mode"]!=modes[name]
        result["content_drift"]+=int(content); result["mode_drift"]+=int(mode)
        if content or mode or item["state"]!="observed":
            result["changed_files"]+=1
            if len(result["records"])<16: result["records"].append(item)
            else: result["records_truncated"]=True
    result["scan_complete"]=result["inspected_files"]==result["expected_files"]
except (OSError,ValueError,TypeError,KeyError):
    result["scan_error"]="snapshot-or-read-unavailable"
finally:
    if root_fd is not None: os.close(root_fd)
encoded=json.dumps(result,sort_keys=True,separators=(",",":"))
if len(encoded.encode())>16384:
    result["records"]=[]; result["records_truncated"]=True
    encoded=json.dumps(result,sort_keys=True,separators=(",",":"))
print(encoded)
PYDRIFT
    printf '%s\n' "$?" > "$task_receipts/source-drift-exit.txt"
  fi
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
export UseSharedCompilation=false MSBUILDDISABLENODEREUSE=1 PYTHONDONTWRITEBYTECODE=1
# Same SDK/assets prerequisites as native17, narrowed to this test project.
( umask 022; timeout --signal=TERM --kill-after=10s 180s dotnet restore Cli/ForgeTrust.AppSurface.Cli.Tests/ForgeTrust.AppSurface.Cli.Tests.csproj --locked-mode ) > "$task_receipts/restore.log" 2>&1
( umask 022; timeout --signal=TERM --kill-after=10s 120s pnpm --dir Web install --frozen-lockfile ) > "$task_receipts/pnpm-install.log" 2>&1
( umask 022; timeout --signal=TERM --kill-after=10s 120s pnpm --dir Web run assets:build ) > "$task_receipts/assets-build.log" 2>&1
( umask 022; timeout --signal=TERM --kill-after=10s 180s dotnet build Cli/ForgeTrust.AppSurface.Cli.Tests/ForgeTrust.AppSurface.Cli.Tests.csproj --no-restore ) > "$task_receipts/cli-build.log" 2>&1
# Trusted prerequisites must preserve the pinned source before either test lane.
python3 -B "$task_root/verify-snapshot.py" > "$task_receipts/source-after-prerequisites.json"
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
