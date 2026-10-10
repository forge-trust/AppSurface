#!/usr/bin/bash
# Root OS data collector only. Externally verify/install BEFORE Bash interpretation.
# A zero collector exit never overrides the original fixture result or proves settlement.
# Fixed dispatch marker, created by main BEFORE fixture dispatch:
# {"schema":"issue779-native-dispatch-marker-v1","generation":"32lowerhex",
#  "fixture_parent_absent":true}
# Main must observe actual parent absence; metadata here is continuity, not authority.
set -euo pipefail
export PATH=/usr/bin:/usr/sbin LC_ALL=C LANG=C
umask 077
readonly FILE_CAP=8388608 TOTAL_CAP=33554432 ARCHIVE_CAP=33619968
declare -A v=()
success=0
fail() { printf 'NATIVE_RETENTION_REJECTED:%s\n' "$1" >&2; exit 1; }
while (($#)); do
 case "$1" in
 --execute) [[ -z ${v[execute]+x} ]] || fail duplicate-option; v[execute]=1; shift ;;
 --generation|--deadline-monotonic-ms|--reviewed-script-sha256)
  (($#>=2)) || fail option-value; k=${1#--}; [[ -z ${v[$k]+x} ]] || fail duplicate-option; v[$k]=$2; shift 2 ;;
 *) fail unknown-option ;;
 esac
done
for k in execute generation deadline-monotonic-ms reviewed-script-sha256; do [[ -n ${v[$k]-} ]] || fail missing-option; done
[[ $EUID == 0 && $OSTYPE == linux* && ${v[generation]} =~ ^[0-9a-f]{32}$ && ${v[reviewed-script-sha256]} =~ ^[0-9a-f]{64}$ ]] || fail root-platform-input
mono() {
 local stamp rest whole fraction
 IFS=' ' read -r stamp rest </proc/uptime || return 1
 [[ $stamp =~ ^[0-9]+\.[0-9]+$ ]] || return 1
 whole=${stamp%%.*}; fraction=${stamp#*.}000; fraction=${fraction:0:3}
 printf '%s' "$((10#$whole*1000+10#$fraction))"
}
[[ ${v[deadline-monotonic-ms]} =~ ^[1-9][0-9]{0,14}$ ]] || fail deadline-shape
start=$(mono) || fail clock
readonly hard_end=${v[deadline-monotonic-ms]} work_end=$((${v[deadline-monotonic-ms]}-1000))
((hard_end-start>1000 && hard_end-start<=30000)) || fail original-retention-deadline
remaining() { local now left; now=$(mono) || return 1; ((now<work_end)) || return 1; left=$((work_end-now)); printf '%d.%03d' "$((left/1000))" "$((left%1000))"; }
check() { remaining >/dev/null || fail original-deadline; }
bounded() { local seconds; seconds=$(remaining) || fail original-deadline; /usr/bin/timeout --signal=KILL "$seconds" "$@" 2>/dev/null || fail bounded-operation; check; }
for cmd in timeout stat sha256sum find sort cmp jq dd mkdir chmod chown tar mv sync wc head bash; do [[ -x /usr/bin/$cmd ]] || fail missing-trusted-tool; done
absolute() {
 local path=$1 part current=; local -a parts
 [[ $path == /* && $path != / && $path != */ && $path != *'//'* && ${#path} -le 4096 && ! $path =~ [[:cntrl:]] && $path != *\\* ]] || fail absolute-path
 IFS=/ read -r -a parts <<<"${path#/}"
 for part in "${parts[@]}"; do
  [[ -n $part && $part != . && $part != .. && ${#part} -le 255 ]] || fail path-component
  current+=/$part; [[ ! -L $current ]] || fail path-link
 done
}
protected_parent() {
 local path=$1 fact uid gid mode
 absolute "$path"
 while :; do
  [[ -d $path && ! -L $path ]] || fail parent-kind
  fact=$(bounded /usr/bin/stat -c '%u:%g:%a' -- "$path"); IFS=: read -r uid gid mode <<<"$fact"
  [[ $uid == 0 && $gid == 0 && $mode =~ ^[0-7]{3,4}$ ]] && (((8#$mode & 0022)==0)) || fail parent-metadata
  [[ $path == / ]] && break; path=${path%/*}; [[ -n $path ]] || path=/
 done
}
identity() { bounded /usr/bin/stat -c '%d:%i:%s:%f:%h:%u:%g:%y:%z' -- "$1"; }
file_fact() {
 local path=$1 cap=$2
 protected_parent "${path%/*}"; absolute "$path"
 [[ -f $path && ! -L $path && $(bounded /usr/bin/stat -c '%u:%g:%a:%h' -- "$path") == 0:0:600:1 ]] || fail file-custody
 [[ $(bounded /usr/bin/stat -c %s -- "$path") -le $cap ]] || fail file-bound
}
# Self source may be root0500 after external privileged-interpretation bootstrap.
protected_parent "${BASH_SOURCE[0]%/*}"; absolute "${BASH_SOURCE[0]}"
[[ -f ${BASH_SOURCE[0]} && ! -L ${BASH_SOURCE[0]} && $(bounded /usr/bin/stat -c '%u:%g:%a:%h' -- "${BASH_SOURCE[0]}") == 0:0:500:1 && $(bounded /usr/bin/stat -c %s -- "${BASH_SOURCE[0]}") -le 131072 ]] || fail installed-script-custody
self_before=$(identity "${BASH_SOURCE[0]}"); self_hash=$(bounded /usr/bin/sha256sum -- "${BASH_SOURCE[0]}")
[[ ${self_hash:0:64} == "${v[reviewed-script-sha256]}" && $(identity "${BASH_SOURCE[0]}") == "$self_before" ]] || fail installed-script-pin
control=/var/lib/appsurface-evidence-input-${v[generation]}/control
protected_parent "$control"; [[ $(bounded /usr/bin/stat -c '%u:%g:%a' -- "$control") == 0:0:700 ]] || fail control-root
control_pin=$(bounded /usr/bin/stat -c '%d:%i:%u:%g:%a' -- "$control")
marker=$control/fixture-dispatch-marker.json
file_fact "$marker" 4096; marker_before=$(identity "$marker"); marker_hash=$(bounded /usr/bin/sha256sum -- "$marker"); marker_hash=${marker_hash:0:64}
bounded /usr/bin/jq -e --arg g "${v[generation]}" '
 type=="object" and keys==["fixture_parent_absent","generation","schema"] and
 .schema=="issue779-native-dispatch-marker-v1" and .generation==$g and .fixture_parent_absent==true
 ' "$marker" >/dev/null
[[ $(identity "$marker") == "$marker_before" ]] || fail dispatch-marker-changed
stage=$control/retention-staging
for p in "$stage" "$control/native.tar" "$control/native.tar.pending" "$control/retention-selection.json"; do [[ ! -e $p && ! -L $p ]] || fail reused-output; done
on_exit() {
 local code=$? now
 trap - EXIT
 now=$(mono) || now=$hard_end
 if ((code!=0 || success!=1 || now>=work_end)); then
  printf '%s\n' 'NATIVE_RETENTION_FAILED:ORIGINAL_FIXTURE_RESULT_UNCHANGED' >&2
  ((code!=0)) || code=1
 fi
 exit "$code"
}
trap on_exit EXIT
bounded /usr/bin/mkdir -m 0700 -- "$stage" "$stage/logs" "$stage/logs/n03"
parent=/run/appsurface-evidence-fixture
status=parent-absent; selected=; parent_before=; child_before=
if [[ -e $parent || -L $parent ]]; then
 protected_parent "$parent"; [[ $(bounded /usr/bin/stat -c '%u:%g:%a' -- "$parent") == 0:0:700 ]] || fail fixture-parent-custody
 parent_before=$(identity "$parent")
 bounded /usr/bin/bash -o pipefail -c '/usr/bin/find -P "$1" -mindepth 1 -maxdepth 1 -printf "%f\t%y\t%U\t%G\t%m\n" | /usr/bin/head -c 4097' -- "$parent" >"$control/fixture-children.txt"
 [[ $(bounded /usr/bin/stat -c %s -- "$control/fixture-children.txt") -le 4096 ]] || fail fixture-selection-bound
 count=$(bounded /usr/bin/wc -l <"$control/fixture-children.txt")
 ((count<=1)) || fail nonunique-fixture-namespace
 if ((count==0)); then status=parent-empty
 else
  IFS=$'\t' read -r name kind uid gid mode extra <"$control/fixture-children.txt"
  [[ $name =~ ^[0-9a-f]{32}$ && $kind == d && $uid == 0 && $gid == 0 && $mode == 700 && -z ${extra-} ]] || fail fixture-child-custody
  selected=$parent/$name; protected_parent "$selected"; child_before=$(identity "$selected"); status=one-namespace
 fi
 [[ $(identity "$parent") == "$parent_before" ]] || fail fixture-selection-changed
fi
# No discovery below these fixed names. Missing files are retained as missing counts.
inner=(logs/n03/broker.events.jsonl logs/n03/broker.stderr logs/n03/worker.stdout logs/n03/worker.stderr logs/n03/worker-peer.trace logs/n03/broker-live.json logs/n03/broker-live.json.argv logs/n03/broker-live-after.json logs/n03/broker-live-after.json.argv logs/n03/worker-live.json logs/n03/worker-live.json.argv logs/n03/result.json logs/n03/failure.json fixture-result.json raw-evidence-plan.json raw-evidence-manifest.json raw-evidence-summary.json worker-live.json request-policy.sha256 final-files.sha256 logs/n01.stdout logs/n01.stderr logs/n02.stdout logs/n02.stderr logs/n02.trace logs/observer.stdout logs/observer.stderr logs/kill.log logs/stop.log logs/n02.file-limit logs/n02.limits logs/n02.facts.json logs/n02.io.trace logs/n02.credentials.json logs/n02-higher.stdout logs/n02-higher.stderr logs/n02-higher.trace logs/n02-higher.file-limit logs/n02-higher.limits logs/n02-higher.facts.json logs/n02-higher.io.trace logs/n02-higher.credentials.json n02-startup-limit-diagnostic.pending n02-startup-limit-diagnostic.json)
inner+=(negative-setup.json negative-post-join.json negative-worker-projection.json negative-kernel-observation.json negative-worker-raw.json negative-root-failure.json negative-root-terminal.txt negative-descriptor.json negative-account-ids.tsv n05-worker.stdout n05-worker.stderr n06-worker.stdout n06-worker.stderr)
inner+=(logs/n04-helper.stderr logs/n04-helper.stdout n04-account-ids.tsv n04-consistency.json n04-descriptor.json n04-helper-live.json n04-helper-result.json n04-kernel-observation.json n04-root-failure.json n04-root-terminal.txt)

declare -a sources=() targets=() identities=() hashes=()
total=0; present=0; missing=0
add_file() {
 local from=$1 relative=$2 before size hash cap=$FILE_CAP
 check
 if [[ ! -e $from && ! -L $from ]]; then missing=$((missing+1)); return; fi
 case "$relative" in
  fixture-result.json) cap=4096 ;;
  logs/n01.stderr) cap=6144 ;;
  logs/n01.stdout) cap=0 ;;
  logs/n04-helper.stderr) cap=0 ;;
  logs/n04-helper.stdout) cap=4096 ;;
  n04-account-ids.tsv) cap=128 ;;
  n04-consistency.json) cap=2048 ;;
  n04-descriptor.json) cap=65536 ;;
  n04-helper-live.json) cap=2048 ;;
  n04-helper-result.json) cap=4096 ;;
  n04-kernel-observation.json) cap=4097 ;;
  n04-root-failure.json) cap=1024 ;;
  n04-root-terminal.txt) cap=1024 ;;
  negative-setup.json) cap=1024 ;;
  negative-post-join.json) cap=1024 ;;
  negative-worker-projection.json) cap=1024 ;;
  negative-kernel-observation.json) cap=4097 ;;
  negative-worker-raw.json) cap=4097 ;;
  negative-root-failure.json) cap=1024 ;;
  negative-root-terminal.txt) cap=1024 ;;
  negative-descriptor.json) cap=65536 ;;
  negative-account-ids.tsv) cap=128 ;;
  n05-worker.stdout) cap=0 ;;
  n05-worker.stderr) cap=2048 ;;
  n06-worker.stdout) cap=0 ;;
  n06-worker.stderr) cap=2048 ;;
 esac
 file_fact "$from" "$cap"; before=$(identity "$from"); size=$(bounded /usr/bin/stat -c %s -- "$from")
 ((total+size<=TOTAL_CAP)) || fail retained-total-bound
 hash=$(bounded /usr/bin/sha256sum -- "$from"); hash=${hash:0:64}
 [[ $(identity "$from") == "$before" ]] || fail source-changed
 sources+=("$from"); targets+=("$relative"); identities+=("$before"); hashes+=("$hash")
 total=$((total+size)); present=$((present+1))
}
add_file "$control/fixture-stdout.log" fixture-stdout.log
add_file "$control/fixture-stderr.log" fixture-stderr.log
for name in "${inner[@]}"; do
 if [[ -n $selected ]]; then add_file "$selected/$name" "$name"; else missing=$((missing+1)); fi
done
for ((i=0;i<present;i++)); do
 from=${sources[$i]}; to=$stage/${targets[$i]}; size=$(bounded /usr/bin/stat -c %s -- "$from")
 [[ $(identity "$from") == "${identities[$i]}" && ! -e $to && ! -L $to ]] || fail source-before-copy
 bounded /usr/bin/dd if="$from" of="$to" iflag=nofollow,nonblock,count_bytes oflag=nofollow conv=fsync,excl count="$((size+1))" status=none
 bounded /usr/bin/chmod 0600 -- "$to"
 file_fact "$to" "$FILE_CAP"
 [[ $(bounded /usr/bin/stat -c %s -- "$to") == "$size" && $(identity "$from") == "${identities[$i]}" ]] || fail copy-shape-or-source-change
 hash=$(bounded /usr/bin/sha256sum -- "$to"); [[ ${hash:0:64} == "${hashes[$i]}" ]] || fail copy-hash
done
bounded /usr/bin/jq -ncS --arg status "$status" --argjson count "$present" --argjson bytes "$total" --argjson missing "$missing" '
 {schema:"issue779-native-retention-selection-v1",selection_status:$status,data_file_count:$count,data_bytes:$bytes,missing_fixed_file_count:$missing}
 ' >"$control/retention-selection.json"
file_fact "$control/retention-selection.json" 4096
selection_size=$(bounded /usr/bin/stat -c %s -- "$control/retention-selection.json")
bounded /usr/bin/dd if="$control/retention-selection.json" of="$stage/retention-selection.json" iflag=nofollow,nonblock,count_bytes oflag=nofollow conv=fsync,excl count="$((selection_size+1))" status=none
file_fact "$stage/retention-selection.json" 4096
bounded /usr/bin/cmp -- "$control/retention-selection.json" "$stage/retention-selection.json"
# List contains only fixed literals, never supplied archive members or recursive roots.
printf '%s\n' retention-selection.json "${targets[@]}" >"$control/retention-members.unsorted"
bounded /usr/bin/sort -- "$control/retention-members.unsorted" >"$control/retention-members.txt"
# Canonical USTAR: file-only entries, exact fixed order, zero owner/group/time, mode600.
# The size ceiling covers payload32MiB plus fixed headers/padding; no partial success.
(ulimit -f 32832; cd -- "$stage"; bounded /usr/bin/tar --format=ustar --sort=name --owner=0 --group=0 --numeric-owner --mtime=@0 --mode=0600 --no-recursion --verbatim-files-from --files-from="$control/retention-members.txt" --create --file="$control/native.tar.pending") >"$control/retention-tar.stdout" 2>"$control/retention-tar.stderr"
file_fact "$control/native.tar.pending" "$ARCHIVE_CAP"
for ((i=0;i<present;i++)); do
 file_fact "${sources[$i]}" "$FILE_CAP"
 [[ $(identity "${sources[$i]}") == "${identities[$i]}" ]] || fail source-final-identity
 hash=$(bounded /usr/bin/sha256sum -- "${sources[$i]}"); [[ ${hash:0:64} == "${hashes[$i]}" ]] || fail source-final-hash
done
if [[ -n $parent_before ]]; then
 [[ $(identity "$parent") == "$parent_before" ]] || fail fixture-parent-final-change
 [[ -z $selected || $(identity "$selected") == "$child_before" ]] || fail fixture-child-final-change
else [[ ! -e $parent && ! -L $parent ]] || fail fixture-parent-appeared
fi
file_fact "$marker" 4096; hash=$(bounded /usr/bin/sha256sum -- "$marker")
[[ $(identity "$marker") == "$marker_before" && ${hash:0:64} == "$marker_hash" ]] || fail marker-final-change
[[ $(bounded /usr/bin/stat -c '%d:%i:%u:%g:%a' -- "$control") == "$control_pin" ]] || fail control-final-identity
bounded /usr/bin/sync -f -- "$control/native.tar.pending"
check; bounded /usr/bin/mv -T -- "$control/native.tar.pending" "$control/native.tar"
file_fact "$control/native.tar" "$ARCHIVE_CAP"; bounded /usr/bin/sync -f -- "$control"
check; printf 'NATIVE_RETENTION_COMPLETED:DATA_ONLY:FILES=%s:BYTES=%s\n' "$present" "$total"; check; success=1
