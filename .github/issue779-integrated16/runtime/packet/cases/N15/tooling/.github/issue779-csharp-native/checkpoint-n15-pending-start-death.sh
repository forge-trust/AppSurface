#!/usr/bin/env bash
set -euo pipefail
export PATH=/usr/bin:/usr/sbin LC_ALL=C LANG=C
umask 077
readonly HERE=${BASH_SOURCE[0]%/*}
readonly OBSERVER_SHA256=6215ea0bf33d24e8fc1547bcf7614ac0657b781cf4dd07738e069c6ecc3bee58
readonly PARSER_SHA256=fa1fc74882dd5018d5e04128adf3ef30ae19570f4e1eee075eca2c9b57cfa877
readonly RETRY_SHA256=9ab6bb3f3b96a284485ba7fb8eac9aa49a2b4e0f0bed4756ec9a17d166cad3c6
readonly FINALIZER_SHA256=af1122a3d37dba09e2045f2bf21a242422399ebd814643ca840a7d61fe71a507
fail(){ printf 'N15_OWNER_LAUNCH_JOIN_REJECTED:%s\n' "$1" >&2; exit 1; }
[[ ${1-} == --execute ]] || fail execute-required
shift
[[ $# == 24 && $1 == --case-generation && $3 == --request && $5 == --runtime-host \
   && $7 == --entry && $9 == --phase-stderr && ${11} == --partial-record \
   && ${13} == --result-directory && ${15} == --startup-deadline-boot-ms \
   && ${17} == --admission-deadline-boot-ms && ${19} == --job-deadline-boot-ms \
   && ${21} == --work-deadline-boot-ms && ${23} == --root-input-root ]] || fail argument-order
generation=$2 request=$4 runtime_host=$6 entry=$8 phase_stderr=${10} partial=${12}
result_dir=${14} startup_deadline=${16} admission_deadline=${18} job_deadline=${20}
work_deadline=${22} root_input=${24}
[[ $generation =~ ^[0-9a-f]{32}$ && $EUID == 0 && $OSTYPE == linux* \
   && $request == /run/appsurface-evidence-fixture/$generation/request.json \
   && $phase_stderr == /run/appsurface-evidence-fixture/$generation/logs/n15.stderr \
   && $partial == /run/appsurface-evidence-fixture/$generation/partial.json \
   && $result_dir == /run/appsurface-evidence-fixture/$generation \
   && $root_input == /var/lib/appsurface-evidence-fixture/$generation \
   && $runtime_host == "$root_input/runtime/dotnet" \
   && $entry == "$root_input/tool/ForgeTrust.AppSurface.Cli.dll" \
   && $startup_deadline =~ ^[1-9][0-9]{0,14}$ && $admission_deadline =~ ^[1-9][0-9]{0,14}$ \
   && $job_deadline =~ ^[1-9][0-9]{0,14}$ && $work_deadline =~ ^[1-9][0-9]{0,14}$ \
   && $HERE == /var/lib/appsurface-evidence-input-$generation/reviewed ]] || fail fixed-paths-or-deadlines
[[ ! -L /run && ! -L /run/appsurface-evidence-fixture \
   && $(stat -Lc '%u:%g:%a' -- /run/appsurface-evidence-fixture) == 0:0:755 \
   && $(stat -Lc '%u:%g:%a' -- "$result_dir") == 0:0:700 \
   && ! -e $result_dir/logs && ! -L $result_dir/logs ]] || fail materialized-private-workspace
/usr/bin/mkdir -m 0700 -- "$result_dir/logs"
[[ $(stat -Lc '%u:%g:%a' -- "$result_dir/logs") == 0:0:700 \
   && -f $request && ! -L $request && $(stat -Lc '%u:%g:%a:%h' -- "$request") == 0:0:600:1 \
   && -f $runtime_host && ! -L $runtime_host && -f $entry && ! -L $entry \
   && -f $HERE/observe_n15_pending_start.py && ! -L $HERE/observe_n15_pending_start.py \
   && -f $HERE/check_n15_records.py && ! -L $HERE/check_n15_records.py \
   && -f $HERE/n15-guard-reactivation-retry.sh && ! -L $HERE/n15-guard-reactivation-retry.sh \
   && -f $HERE/finalize_n15_observation.py && ! -L $HERE/finalize_n15_observation.py ]] || fail staged-inputs
for row in "$HERE/observe_n15_pending_start.py:$OBSERVER_SHA256" "$HERE/check_n15_records.py:$PARSER_SHA256" "$HERE/n15-guard-reactivation-retry.sh:$RETRY_SHA256" "$HERE/finalize_n15_observation.py:$FINALIZER_SHA256"; do
  path=${row%%:*}; expected=${row#*:}; actual=$(sha256sum -- "$path"); actual=${actual%% *}; [[ $actual == "$expected" ]] || fail staged-pin
done
boot_ms(){ local up rest frac; IFS=' ' read -r up rest </proc/uptime || return 1; [[ $up =~ ^[0-9]+\.[0-9]+$ ]] || return 1; frac=${up#*.}000; printf '%s' "$((10#${up%%.*}*1000+10#${frac:0:3}))"; }
bounded(){ local end=$1 left seconds; shift; left=$((end-$(boot_ms))) || return 1; ((left>0)) || return 124; printf -v seconds '%d.%03d' "$((left/1000))" "$((left%1000))"; timeout --foreground --signal=KILL --kill-after=1 "$seconds" "$@"; }
((startup_deadline<=job_deadline && admission_deadline<=job_deadline && work_deadline<=job_deadline)) || fail deadline-order
now=$(boot_ms); ((now<startup_deadline && now<admission_deadline && now<work_deadline)) || fail original-deadline-expired
owner=appsurface-evidence-owner-$generation.service
worker=appsurface-evidence-worker-$generation.service
owner_guard=/run/appsurface-evidence-owners/$generation
[[ ! -L /run/appsurface-evidence-owners && ! -L $owner_guard ]] || fail owner-guard-symlink
[[ ! -e $owner_guard ]] || fail owner-guard-generation-reuse
install -d -o 0 -g 0 -m 700 /run/appsurface-evidence-owners "$owner_guard"
printf '%s\n' "$generation" >"$owner_guard/armed"
chmod 600 "$owner_guard/armed"
[[ $(stat -Lc '%u:%g:%a:%h' -- "$owner_guard/armed") == 0:0:600:1 ]] || fail source-guard-not-armed
[[ ! -e $phase_stderr && ! -L $phase_stderr && ! -e $result_dir/logs/n15.stdout && ! -L $result_dir/logs/n15.stdout \
   && ! -e $partial && ! -L $partial \
   && ! -e $result_dir/owner-launch.stdout && ! -L $result_dir/owner-launch.stdout \
   && ! -e $result_dir/owner-launch.stderr && ! -L $result_dir/owner-launch.stderr ]] || fail output-reuse
install -o 0 -g 0 -m 600 /dev/null "$phase_stderr"
install -o 0 -g 0 -m 600 /dev/null "$result_dir/logs/n15.stdout"
install -o 0 -g 0 -m 600 /dev/null "$result_dir/owner-launch.stdout"
install -o 0 -g 0 -m 600 /dev/null "$result_dir/owner-launch.stderr"
unsafe='DOTNET_STARTUP_HOOKS DOTNET_ADDITIONAL_DEPS DOTNET_SHARED_STORE DOTNET_ROOT DOTNET_ROOT_X64 DOTNET_HOST_PATH DOTNET_ROLL_FORWARD DOTNET_ROLL_FORWARD_TO_PRERELEASE DOTNET_MULTILEVEL_LOOKUP CORECLR_ENABLE_PROFILING CORECLR_PROFILER CORECLR_PROFILER_PATH CORECLR_PROFILER_PATH_64 COR_ENABLE_PROFILING COR_PROFILER COR_PROFILER_PATH COMPlus_ReadyToRun COMPlus_ZapDisable'
fixed_env=(PATH=/usr/bin:/bin HOME=/nonexistent LANG=C.UTF-8 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_CLI_HOME=/tmp)
retry_bound=$job_deadline; ((work_deadline<job_deadline)) && retry_bound=$work_deadline
now=$(boot_ms); left=$((retry_bound-now)); ((left>7000)) || fail insufficient-original-budget
runtime_ms=$((left-7000)); runtime_seconds=$(awk -v ms="$runtime_ms" 'BEGIN{printf "%.3f",ms/1000}')
props=(--property=Type=exec --property=User=0 --property=Group=0 --property=KillMode=control-group --property=Restart=no --property=RemainAfterExit=no --property=SendSIGKILL=yes --property=FinalKillSignal=9 --property=RuntimeMaxSec="$runtime_seconds" --property=TimeoutStopSec=5s "--property=ConditionPathExists=$owner_guard/armed" --property=PassEnvironment= "--property=UnsetEnvironment=$unsafe" "--property=Environment=${fixed_env[*]}")
launch_status=0
systemd-run --quiet --wait --pipe --unit="$owner" "${props[@]}" /usr/bin/env -i "${fixed_env[@]}" "$runtime_host" "$entry" evidence supervise --request "$request" >"$result_dir/owner-launch.stdout" 2>"$phase_stderr" &
launch_pid=$!
printf 'N15_CSHARP_OWNER_DISPATCHED\n'
observer_status=0
observer_pid=
release_observer(){
  [[ -n $observer_pid ]] || return 0
  if [[ ! -e $result_dir/unit-reference.release && ! -L $result_dir/unit-reference.release ]]; then
    local pending=$result_dir/unit-reference.release.pending
    (set -o noclobber; printf 'release\n' >"$pending") || return 1
    mv -n -- "$pending" "$result_dir/unit-reference.release" || return 1
    [[ ! -e $pending && ! -L $pending ]] || return 1
  fi
  local status=0
  wait "$observer_pid" || status=$?
  observer_pid=
  observer_status=$status
  return "$status"
}
trap 'original_status=$?; release_observer || true; exit "$original_status"' EXIT
[[ ! -e $result_dir/unit-reference.ready && ! -L $result_dir/unit-reference.ready \
   && ! -e $result_dir/unit-reference.release && ! -L $result_dir/unit-reference.release \
   && ! -e $result_dir/unit-reference.release.pending && ! -L $result_dir/unit-reference.release.pending ]] || fail reference-marker-reuse
/usr/bin/env -i PATH=/usr/bin:/usr/sbin LANG=C LC_ALL=C PYTHONHASHSEED=0 /usr/bin/python3.12 -I -S -B "$HERE/observe_n15_pending_start.py" --execute \
  --case-generation "$generation" --phase-stderr "$phase_stderr" \
  --startup-deadline-boot-ms "$startup_deadline" --admission-deadline-boot-ms "$admission_deadline" \
  --job-deadline-boot-ms "$job_deadline" --work-deadline-boot-ms "$work_deadline" >"$partial" &
observer_pid=$!
while [[ ! -s $result_dir/unit-reference.ready ]]; do
  (( $(boot_ms)<work_deadline )) || fail reference-ready-deadline
  kill -0 "$observer_pid" 2>/dev/null || fail reference-observer-ended
  sleep 0.02
done
[[ ! -L $result_dir/unit-reference.ready \
   && $(stat -Lc '%u:%g:%a:%h:%s' -- "$result_dir/unit-reference.ready") == 0:0:600:1:6 \
   && $(cat -- "$result_dir/unit-reference.ready") == ready ]] || fail reference-ready-custody
wait "$launch_pid" || launch_status=$?
[[ ! -e /proc/$launch_pid && ! -L /proc/$launch_pid ]] || fail original-owner-launch-not-joined
[[ $(stat -Lc %s -- "$phase_stderr") -le 8192 && $(stat -Lc %s -- "$partial") -le 65536 \
   && $(stat -Lc %s -- "$result_dir/owner-launch.stdout") -le 65536 ]] || fail original-pipe-bounds
[[ $(stat -Lc '%u:%g:%a:%h' -- "$phase_stderr") == 0:0:600:1 \
   && $(stat -Lc '%u:%g:%a:%h' -- "$partial") == 0:0:600:1 \
   && $(stat -Lc '%u:%g:%a:%h' -- "$result_dir/owner-launch.stdout") == 0:0:600:1 \
   && $(stat -Lc '%u:%g:%a:%h' -- "$result_dir/owner-launch.stderr") == 0:0:600:1 ]] || fail joined-file-custody
[[ $observer_status == 0 && -s $phase_stderr && -s $partial ]] || fail source-phase-observation
kill -0 "$observer_pid" 2>/dev/null || fail reference-observer-ended
retry_out=$result_dir
bounded "$work_deadline" /usr/bin/bash --noprofile --norc "$HERE/n15-guard-reactivation-retry.sh" --execute \
  --generation "$generation" --owner-unit "$owner" --worker-unit "$worker" \
  --request "$request" --runtime-host "$runtime_host" --entry "$entry" \
  --external-guard "$owner_guard/consumed" --partial-record "$partial" \
  --work-deadline-boot-ms "$work_deadline" --job-deadline-boot-ms "$job_deadline" \
  --result-directory "$retry_out"
[[ -s $retry_out/reactivation-measurement.json && ! -L $retry_out/reactivation-measurement.json \
   && $(stat -Lc '%u:%g:%a:%h' -- "$retry_out/reactivation-measurement.json") == 0:0:600:1 ]] || fail replay-measurement-missing
if grep -Fq '"phase":"start-transient-unit-reply"' "$retry_out/retry.stderr"; then fail replay-reemitted-start-phase; fi
kill -0 "$observer_pid" 2>/dev/null || fail reference-observer-ended
/usr/bin/timeout --foreground --signal=KILL --kill-after=1 "$(awk -v ms="$((job_deadline-$(boot_ms)))" 'BEGIN{if(ms<1)exit 1;printf "%.3f",ms/1000}')" \
  /usr/bin/python3.12 -I -S -B "$HERE/finalize_n15_observation.py" --execute \
  --generation "$generation" --result-directory "$result_dir" --joined-launch-pid "$launch_pid" \
  --job-deadline-boot-ms "$job_deadline" || fail final-record-and-account-cleanup
for file in account-cleanup.json n15-final-record.json n15-record-summary.json; do
  [[ -f $result_dir/$file && ! -L $result_dir/$file \
     && $(stat -Lc '%u:%g:%a:%h' -- "$result_dir/$file") == 0:0:600:1 ]] || fail final-record-custody
done
release_observer || fail reference-observer-final-join
[[ $observer_status == 0 ]] || fail reference-observer-final-status
printf 'N15_OWNER_LAUNCH_JOIN_MEASURED:launch=%s:observer=%s:stdout=%s:phase_stderr=%s\n' "$launch_status" "$observer_status" "$(stat -Lc %s -- "$result_dir/owner-launch.stdout")" "$(stat -Lc %s -- "$phase_stderr")"
