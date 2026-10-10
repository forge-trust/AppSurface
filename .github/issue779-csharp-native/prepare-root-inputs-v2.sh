#!/usr/bin/bash
# V2 batched preparation source only. Operator verifies this file BEFORE starting the interpreter.
set -euo pipefail
export PATH=/usr/bin:/usr/sbin LC_ALL=C LANG=C
umask 077
readonly SOURCE=dd941d06ec1a038fd4d82228005f14fd3c6440a0
readonly PARENT=dbfc4cbda2aa7de0d5a544b47895f209138767e7
readonly SOURCE_MAP=7f03d27843888046b7baf640cf678a83c2854267019e6b799b2d7253a7229e0d
readonly FILE_CAP=268435456 TREE_CAP=1073741824 NODE_CAP=8192
readonly MAP_CAP=1048576 NODES_CAP=4194304 CONTROL_CAP=1048576 RESERVE_MS=5000
mode=prepare-only; phase=arguments; output=; succeeded=0
declare -A values=() roots=() maps=() nodes=() map_shas=() node_shas=() map_hash=() node_hash=()
fail() { printf 'ROOT_TRANSPORT_REJECTED:%s\n' "$1" >&2; exit 1; }
while (($#)); do
 case "$1" in
  --prepare-only|--execute) [[ -z ${values[mode]+x} ]] || fail duplicate-option; values[mode]=1; mode=${1#--}; shift ;;
  --*) (($#>=2)) || fail option-value; key=${1#--}; [[ -z ${values[$key]+x} ]] || fail duplicate-option; values[$key]=$2; shift 2 ;;
  *) fail unknown-option ;;
 esac
done
for key in "${!values[@]}"; do
 case "$key" in
 mode|reviewed-script-sha256|generation|deadline-monotonic-ms|source-commit|source-root|source-map|source-map-sha256|source-nodes|source-nodes-sha256|tool-root|tool-map|tool-map-sha256|tool-nodes|tool-nodes-sha256|runtime-root|runtime-map|runtime-map-sha256|runtime-nodes|runtime-nodes-sha256|source-review|source-review-sha256|build-receipt|build-receipt-sha256|fixture|fixture-sha256|os-audit-script|os-audit-script-sha256|n16-parser-root|n16-parser-sha256|source-capture|source-capture-sha256) ;;
 *) fail unknown-option ;;
 esac
done
if [[ $mode == prepare-only ]]; then
 printf '%s\n' 'PREPARATION_ONLY:NO_INPUTS_VERIFIED:NO_TRANSPORT:NO_FIXTURE_EXECUTION'; exit 0
fi
readonly N16_INDEPENDENT_REVIEW_CLEAR=1
((N16_INDEPENDENT_REVIEW_CLEAR==1)) || fail n16-independent-review-pending
[[ $EUID == 0 && $OSTYPE == linux* ]] || fail trusted-linux-root-required
for key in reviewed-script-sha256 generation deadline-monotonic-ms source-commit source-root source-map source-map-sha256 source-nodes source-nodes-sha256 tool-root tool-map tool-map-sha256 tool-nodes tool-nodes-sha256 runtime-root runtime-map runtime-map-sha256 runtime-nodes runtime-nodes-sha256 source-review source-review-sha256 build-receipt build-receipt-sha256 fixture fixture-sha256 os-audit-script os-audit-script-sha256 n16-parser-root n16-parser-sha256 source-capture source-capture-sha256; do
 [[ -n ${values[$key]-} ]] || fail missing-input
done
hash_syntax() { [[ $1 =~ ^[0-9a-f]{64}$ ]] || fail digest-syntax; }
for key in "${!values[@]}"; do [[ $key != *sha256 ]] || hash_syntax "${values[$key]}"; done
[[ ${values[generation]} =~ ^[0-9a-f]{32}$ && ${values[source-commit]} == dd941d06ec1a038fd4d82228005f14fd3c6440a0 ]] || fail source-generation-pin
[[ ${values[n16-parser-sha256]} == a585ab88e24f4d0dbb1232f5783f7dd447b8c0658046f068be391570f8e8930e && ${values[source-capture-sha256]} == 2ba8a8d962bfd3d78a7d080b5abb550ceb54796156acdb2b1b1b3e502586d128 ]] || fail n16-source-parser-pins
mono_ms() {
 local stamp rest whole fraction
 IFS=' ' read -r stamp rest </proc/uptime || return 1
 [[ $stamp =~ ^[0-9]+\.[0-9]+$ ]] || return 1
 whole=${stamp%%.*}; fraction=${stamp#*.}000; fraction=${fraction:0:3}
 printf '%s' "$((10#$whole*1000+10#$fraction))"
}
[[ ${values[deadline-monotonic-ms]} =~ ^[1-9][0-9]{0,14}$ ]] || fail deadline-syntax
start_ms=$(mono_ms) || fail monotonic-clock
readonly hard_end=${values[deadline-monotonic-ms]}
((hard_end-start_ms>RESERVE_MS && hard_end-start_ms<=120000)) || fail original-deadline-bound
readonly work_end=$((hard_end-RESERVE_MS))
remaining() { local now left; now=$(mono_ms) || return 1; ((now<work_end)) || return 1; left=$((work_end-now)); printf '%d.%03d' "$((left/1000))" "$((left%1000))"; }
check() { remaining >/dev/null || fail transport-deadline; }
# No caller PATH, shell expression, or selected executable. These are trusted OS prerequisites.
bounded() { local seconds; seconds=$(remaining) || fail transport-deadline; /usr/bin/timeout --signal=KILL "$seconds" "$@" || fail bounded-operation; check; }
for command in /usr/bin/bash /usr/bin/head /usr/bin/timeout /usr/bin/stat /usr/bin/sha256sum /usr/bin/find /usr/bin/sort /usr/bin/cmp /usr/bin/jq /usr/bin/dd /usr/bin/mkdir /usr/bin/chmod /usr/bin/chown /usr/bin/cp /usr/bin/wc /usr/bin/sync; do
 [[ -x $command ]] || fail missing-trusted-command
done
absolute() {
 local path=$1 part current=; local -a parts
 [[ $path == /* && $path != / && $path != */ && $path != *'//'* && ${#path} -le 4096 && ! $path =~ [[:cntrl:]] && $path != *\\* ]] || fail absolute-path
 IFS=/ read -r -a parts <<<"${path#/}"
 for part in "${parts[@]}"; do
  [[ -n $part && $part != . && $part != .. && ${#part} -le 255 ]] || fail path-component
  current+=/$part; [[ ! -L $current ]] || fail path-link
 done
}
ancestors() {
 local path=$1 fact uid gid permissions
 absolute "$path"
 while :; do
  [[ -d $path && ! -L $path ]] || fail parent-type
  fact=$(bounded /usr/bin/stat -c '%u:%g:%a' -- "$path"); IFS=: read -r uid gid permissions <<<"$fact"
  [[ $uid == 0 && $gid == 0 && $permissions =~ ^[0-7]{3,4}$ ]] && (((8#$permissions & 0022)==0)) || fail untrusted-parent
  [[ $path == / ]] && break; path=${path%/*}; [[ -n $path ]] || path=/
 done
}
identity() { bounded /usr/bin/stat -c '%d:%i:%s:%f:%h:%u:%g:%Y:%Z' -- "$1"; }
pin() {
 local path=$1 expected=$2 cap=$3 before after size h
 absolute "$path"; ancestors "${path%/*}"
 [[ -f $path && ! -L $path ]] || fail file-type
 before=$(identity "$path"); size=$(bounded /usr/bin/stat -c %s -- "$path")
 [[ $size =~ ^[0-9]+$ ]] && ((size<=cap)) || fail file-bound
 [[ $(bounded /usr/bin/stat -c '%h:%u:%g' -- "$path") == 1:0:0 ]] || fail input-file-owner-link
 h=$(bounded /usr/bin/sha256sum -- "$path"); after=$(identity "$path")
 [[ $before == "$after" && ${h:0:64} == "$expected" ]] || fail file-pin-changed
}
absolute "${BASH_SOURCE[0]}"; pin "${BASH_SOURCE[0]}" "${values[reviewed-script-sha256]}" 131072
for kind in source tool runtime; do
 roots[$kind]=${values[$kind-root]}; maps[$kind]=${values[$kind-map]}; nodes[$kind]=${values[$kind-nodes]}
 map_shas[$kind]=${values[$kind-map-sha256]}; node_shas[$kind]=${values[$kind-nodes-sha256]}
 ancestors "${roots[$kind]}"; pin "${maps[$kind]}" "${map_shas[$kind]}" "$MAP_CAP"; pin "${nodes[$kind]}" "${node_shas[$kind]}" "$NODES_CAP"
done
for a in source tool runtime; do for b in source tool runtime; do
 [[ $a == "$b" ]] && continue
 [[ ${roots[$a]} != "${roots[$b]}" && ${roots[$a]} != "${roots[$b]}/"* ]] || fail overlapping-inputs
done; done
for spec in source-review:65536 build-receipt:1048576 fixture:131072 os-audit-script:131072; do
 key=${spec%:*}; pin "${values[$key]}" "${values[$key-sha256]}" "${spec#*:}"
done
pin "${values[source-capture]}" "${values[source-capture-sha256]}" 2097152
pin "${values[n16-parser-root]}/check_n16_records.py" "${values[n16-parser-sha256]}" 131072
bounded /usr/bin/jq -e --arg head "$SOURCE" --arg parent "$PARENT" '.schema=="issue779-n16-private-source-capture-v1" and .captured_head==$head and .captured_parent==$parent and .source_count==2849 and .clean==true and .index_tree_matches_commit==true and .native_credit==false and .native_execution==false and .pushed==false and (.full_source|type=="object" and length==2849) and (.source_sha256|type=="object" and length==2849) and (.source_modes|type=="object" and length==2849)' "${values[source-capture]}" >/dev/null
bounded /usr/bin/jq -e --arg source "$SOURCE" --arg parent "$PARENT" --arg map "$SOURCE_MAP" --arg capture "${values[source-capture-sha256]}" '
 . as $r | .schema=="issue779-csharp-fdd-build-v5" and .exit==0 and (.failure?==null) and .source_commit==$source and .harness_parent==$source and .harness_retry_parent==$source and .harness_retry_parent_parent==$parent and .diagnostics==[] and .native_execution==false and .checkpoint_pass==false and .private_qualification=="N16" and .selected_variant=="N16" and .build_prerequisite_only==true and .capture_input_sha256==$capture and .capture_projection_sha256=="91d29c240aa28735c599fd037c99de34e194e489ceb6388eb0c3441fc972a850" and
 (.commands|length)==43 and (.commands|map(.ordinal))==[range(0;43)] and all(.commands[]; .exit==0 and .failure==null and .waited==true and .group_absent==true and .timed_out==false and .forced_cleanup==false and (.logs|length)==2) and
 all(["source_before","source_after_assets","source_after_build","source_final"][]; . as $k | $r[$k].head==$source and $r[$k].count==2849 and $r[$k].physical_sha256_modes==true and $r[$k].physical_git_sha1==true and $r[$k].index_tree_matches==true and $r[$k].source_map_sha256==$map) and
 ($r.linux_pipe_regression|type)=="object" and ($r.linux_pipe_regression|keys)==["counters","method","native_acceptance","root_factory_exercised","runtime_basis","trx_sha256","unprivileged_library_behavior_only"] and $r.linux_pipe_regression.method=="OwnedRawPipeReadWrappingUsesHandleModeAndJoinsBothEofs" and ($r.linux_pipe_regression.counters|type)=="object" and all(["total","executed","passed"][]; $r.linux_pipe_regression.counters[.] == "1") and all(["failed","error","notExecuted","timeout","aborted"][]; $r.linux_pipe_regression.counters[.] == "0") and ($r.linux_pipe_regression.trx_sha256|type=="string" and test("^[0-9a-f]{64}$")) and $r.linux_pipe_regression.unprivileged_library_behavior_only==true and $r.linux_pipe_regression.root_factory_exercised==false and $r.linux_pipe_regression.native_acceptance==false and $r.linux_pipe_regression.runtime_basis=="selected SDK dotnet host"
' "${values[build-receipt]}" >/dev/null
output=/var/lib/appsurface-evidence-input-${values[generation]}
ancestors /var/lib; [[ ! -e $output && ! -L $output ]] || fail reused-destination
for kind in source tool runtime; do
 [[ $output != "${roots[$kind]}" && $output != "${roots[$kind]}/"* && ${roots[$kind]} != "$output/"* ]] || fail destination-overlap
done
# Failure keeps partial root-only paths. There is deliberately no recursive delete or success cleanup.
on_exit() {
 local code=$? now left seconds confirmed=0
 trap - EXIT
 if ((code==0 && succeeded==1)); then
  now=$(mono_ms) || now=$work_end
  if ((now>=work_end)); then code=1; succeeded=0; fi
 fi
 if ((code!=0 || succeeded==0)); then
  # Attempt only this retained generation, within the ORIGINAL reserved deadline.
  now=$(mono_ms) || now=$hard_end
  if ((now<hard_end)) && [[ -n ${root_pin-} ]]; then
   left=$((hard_end-now)); printf -v seconds '%d.%03d' "$((left/1000))" "$((left%1000))"
   if [[ $(/usr/bin/timeout --signal=KILL "$seconds" /usr/bin/stat -c '%d:%i:%u:%g' -- "$output" 2>/dev/null) == "$root_pin" ]]; then
    now=$(mono_ms) || now=$hard_end
    if ((now<hard_end)); then
     left=$((hard_end-now)); printf -v seconds '%d.%03d' "$((left/1000))" "$((left%1000))"
     /usr/bin/timeout --signal=KILL "$seconds" /usr/bin/chmod 0700 -- "$output" >/dev/null 2>&1 || :
    fi
    now=$(mono_ms) || now=$hard_end
    if ((now<hard_end)); then
     left=$((hard_end-now)); printf -v seconds '%d.%03d' "$((left/1000))" "$((left%1000))"
     [[ $(/usr/bin/timeout --signal=KILL "$seconds" /usr/bin/stat -c '%d:%i:%u:%g:%a' -- "$output" 2>/dev/null) == "$root_pin:700" ]] && confirmed=1
    fi
   fi
  fi
  if ((confirmed)); then printf 'ROOT_TRANSPORT_PARTIAL_RETAINED_ROOT700:%s\n' "${values[generation]}" >&2
  else printf 'ROOT_TRANSPORT_PARTIAL_RETAINED_QUARANTINE_UNKNOWN:%s\n' "${values[generation]}" >&2; fi
  ((code!=0)) || code=1
 fi
 exit "$code"
}
trap on_exit EXIT
bounded /usr/bin/mkdir -m 0700 -- "$output"
root_pin=$(bounded /usr/bin/stat -c '%d:%i:%u:%g' -- "$output")
bounded /usr/bin/mkdir -m 0700 -- "$output/control" "$output/reviewed"
work=$output/control
helper=$output/empty-audit-helper
check
bounded /usr/bin/mkdir -m 0700 -- "$helper"
absolute "$helper"
[[ -d $helper && ! -L $helper ]] || fail audit-helper-kind
bounded /usr/bin/chown --no-dereference 0:0 -- "$helper"
bounded /usr/bin/chmod 0555 -- "$helper"
[[ $(bounded /usr/bin/stat -c '%u:%g:%a' -- "$helper") == 0:0:555 ]] || fail audit-helper-custody
[[ -z $(bounded /usr/bin/find -P "$helper" -mindepth 1 -maxdepth 1 -print -quit) ]] || fail audit-helper-not-empty
helper_identity=$(identity "$helper")
check
relative() {
 local name=$1 part; local -a parts
 [[ -n $name && $name != /* && $name != */ && $name != *'//'* && ${#name} -le 4096 && ! $name =~ [[:cntrl:]] && $name != *\\* && $name != *:* ]] || fail relative-path
 IFS=/ read -r -a parts <<<"$name"
 for part in "${parts[@]}"; do [[ -n $part && $part != . && $part != .. && $part != .git && ${#part} -le 255 ]] || fail relative-component; done
}
copy_pinned() {
 local from=$1 target=$2 sha=$3 cap=$4 permissions=$5 before after size
 pin "$from" "$sha" "$cap"; before=$(identity "$from"); size=$(bounded /usr/bin/stat -c %s -- "$from")
 [[ ! -e $target && ! -L $target ]] || fail copy-collision
 # Bounded nonblocking/no-follow read; exclusive output and one extra byte expose growth.
 bounded /usr/bin/dd if="$from" of="$target" iflag=nofollow,nonblock,count_bytes oflag=nofollow conv=fsync,excl count="$((size+1))" status=none
 after=$(identity "$from"); [[ $before == "$after" && $(bounded /usr/bin/stat -c %s -- "$target") == "$size" ]] || fail copy-input-changed
 bounded /usr/bin/chown 0:0 -- "$target"; bounded /usr/bin/chmod "$permissions" -- "$target"
 pin "$target" "$sha" "$cap"; pin "$from" "$sha" "$cap"
}
phase=metadata
for kind in source tool runtime; do
 copy_pinned "${maps[$kind]}" "$work/$kind.tsv" "${map_shas[$kind]}" "$MAP_CAP" 0600
 copy_pinned "${nodes[$kind]}" "$work/$kind-nodes.json" "${node_shas[$kind]}" "$NODES_CAP" 0600
 maps[$kind]=$work/$kind.tsv; nodes[$kind]=$work/$kind-nodes.json
 map_hash[$kind]=$(bounded /usr/bin/jq -r --arg name "$kind" '.artifacts[$name].tsv_sha256' "$work/build-receipt.json")
 node_hash[$kind]=$(bounded /usr/bin/jq -r --arg name "$kind" '.artifacts[$name].nodes_sha256' "$work/build-receipt.json")
 bounded /usr/bin/jq -acS . "${nodes[$kind]}" >"$work/$kind-canonical.json"
 bounded /usr/bin/cmp -- "${nodes[$kind]}" "$work/$kind-canonical.json"
 bounded /usr/bin/jq -e --arg kind "$kind" --slurpfile receipt "$work/build-receipt.json" '
  def rel: type=="string" and utf8bytelength<=4096 and (test("[\u0000-\u001f\u007f\\\\:]")|not) and (split("/")|all(.[]; length>0 and .!="." and .!=".." and .!=".git" and utf8bytelength<=255));
  . as $m | type=="object" and keys==["directories","files","root_name","schema"] and .schema=="issue779-build-node-inventory-v1" and .root_name==$kind and
  (.files|type=="object") and (.directories|type=="object") and .directories["."]!=null and
  (.files|length)==$receipt[0].artifacts[$kind].file_count and ((.files|length)+(.directories|length))==$receipt[0].artifacts[$kind].node_count and
  all(.directories|to_entries[]; (.key=="." or (.key|rel)) and (.value|type=="object" and keys==["mode"] and .mode==(if $kind=="source" then "0700" else "0555" end))) and
  all(.files|to_entries[]; (.key|rel) and $m.directories[.key]==null and (.value|type=="object" and keys==["bytes","mode","sha256"] and (.sha256|type=="string" and test("^[0-9a-f]{64}$")) and (.bytes|type=="number" and floor==. and .>=0 and .<=268435456) and (if $kind=="source" then .mode=="0644" or .mode=="0755" else .mode=="0444" or .mode=="0555" end))) and
  ([.files[].bytes]|add)==$receipt[0].artifacts[$kind].total_bytes and (if $kind=="source" then (.files|length)==2849 else true end)
 ' "${nodes[$kind]}" >/dev/null
 bounded /usr/bin/jq -r '.files|to_entries|sort_by(.key)[]|[.value.mode,.value.sha256,.key]|@tsv' "${nodes[$kind]}" >"$work/$kind-derived.tsv"
 bounded /usr/bin/cmp -- "${maps[$kind]}" "$work/$kind-derived.tsv"
 bounded /usr/bin/jq -r '.files|to_entries|sort_by(.key)[]|.value.sha256+"  "+.key' "${nodes[$kind]}" >"$work/$kind-checksums.txt"
done

for kind in source tool runtime; do
 copy_pinned "${maps[$kind]}" "$work/$kind.tsv" "${map_shas[$kind]}" "$MAP_CAP" 0600
 copy_pinned "${nodes[$kind]}" "$work/$kind-nodes.json" "${node_shas[$kind]}" "$NODES_CAP" 0600
 maps[$kind]=$work/$kind.tsv; nodes[$kind]=$work/$kind-nodes.json
 bounded /usr/bin/jq -e --arg name "$kind" '
  def hash: type=="string" and test("^[0-9a-f]{64}$");
  def mode: type=="string" and test("^0[0-7]{3}$");
  def rel:
   type=="string" and utf8bytelength<=4096 and
   (test("[\u0000-\u001f\u007f\\\\:]")|not) and
   (split("/")|all(.[]; length>0 and .!="." and .!=".." and .!=".git" and utf8bytelength<=255));
  type=="object" and keys==["directories","files","root_name","schema"] and
  .schema=="issue779-build-node-inventory-v1" and .root_name==$name and
  (.files|type=="object") and (.directories|type=="object") and
  (.files|length)>0 and ((.files|length)+(.directories|length)<=8192) and
  .directories["."]!=null and
  all(.directories|to_entries[];
   (.key=="." or (.key|rel)) and
   (.value|type=="object" and keys==["mode"] and (.mode|mode))) and
  all(.files|to_entries[];
   (.key|rel) and (.value|type=="object" and keys==["bytes","mode","sha256"] and
    (.mode|mode) and (.sha256|hash) and
    (.bytes|type=="number" and floor==. and .>=0 and .<=268435456))) and
  (if $name=="source" then (.files|length)==2849 and
    all(.files[]; .mode=="0644" or .mode=="0755") else true end)
 ' "${nodes[$kind]}" >"$work/$kind-schema.log"
 bounded /usr/bin/jq -r '.files|to_entries|sort_by(.key)[]|[.value.mode,.value.sha256,.key]|@tsv' "${nodes[$kind]}" >"$work/$kind-derived.tsv"
 bounded /usr/bin/cmp -- "${maps[$kind]}" "$work/$kind-derived.tsv"
 bounded /usr/bin/jq -r '.files|to_entries|sort_by(.key)[]|.value.sha256+"  "+.key' "${nodes[$kind]}" >"$work/$kind-checksums.txt"
done

# One fixed native traversal collects full node identities, including empty directories.
# The sealed inbound/frozen trusted-root prerequisite is what permits batched opens.
snapshot() {
 local root=$1 target=$2 rows size
 absolute "$root"; ancestors "$root"
 bounded /usr/bin/bash -o pipefail -c '/usr/bin/find -P "$1" -printf "%P\t%y\t%m\t%U\t%G\t%n\t%s\t%D\t%i\t%T@\t%C@\n" | /usr/bin/head -c 8388609' -- "$root" >"$target.raw"
 size=$(bounded /usr/bin/stat -c %s -- "$target.raw"); ((size<=8388608)) || fail inventory-byte-bound
 rows=$(bounded /usr/bin/wc -l <"$target.raw"); ((rows>0 && rows<=NODE_CAP)) || fail inventory-node-bound
 bounded /usr/bin/sort -- "$target.raw" >"$target"
}
validate_snapshot() {
 local kind=$1 inventory=$2 seal=$3
 # Rows charged above BEFORE jq slurp. Numeric byte sums remain exact below 1 GiB.
 bounded /usr/bin/jq -Rse --slurpfile maps "${nodes[$kind]}" --arg kind "$kind" --arg seal "$seal" '
  def numeric: test("^[0-9]+$");
  def nonzero: numeric and test("[1-9]");
  def safe_mode: test("^[0-7]{3}$") and
    ((.[1:2]|test("[2367]"))|not) and ((.[2:3]|test("[2367]"))|not);
  $maps[0] as $m |
  if endswith("\n") then split("\n")[:-1]|map(split("\t")) else error("inventory-shape") end |
  map(if .[0]=="" then .[0]="." else . end) as $rows |
  (($m.files|keys)+($m.directories|keys)|sort) as $expected |
  ($rows|map(.[0])|sort)==$expected and
  ($rows|map(.[0])|unique|length)==($rows|length) and
  all($rows[];
   length==11 and (.[1]=="d" or .[1]=="f") and
   (.[2]|safe_mode) and .[3]=="0" and .[4]=="0" and
   (.[5]|nonzero) and (.[6]|numeric) and (.[7]|numeric) and (.[8]|nonzero) and
   (.[9]|test("^-?[0-9]+\\.[0-9]+$")) and (.[10]|test("^-?[0-9]+\\.[0-9]+$")) and
   (if .[1]=="d" then $m.directories[.[0]]!=null and
     .[2]==(if $seal=="input" then $m.directories[.[0]].mode[1:] else "555" end)
    else $m.files[.[0]]!=null and .[5]=="1" and
     (.[6]|tonumber)==$m.files[.[0]].bytes and
     .[2]==(if $seal=="input" or $kind=="source" then $m.files[.[0]].mode[1:]
       elif ($m.files[.[0]].mode[1:]|test("[1357]")) then "555" else "444" end)
    end)) and
  ([$rows[]|select(.[1]=="f")|.[6]|tonumber]|add)<=1073741824
 ' "$inventory" >"$inventory.validation.log"
}
hash_tree() {
 local kind=$1 root=$2 label=$3
 # Paths are closed and validated. No shell expands checksum filenames.
 (cd -- "$root"; bounded /usr/bin/sha256sum --check --strict --status "$work/$kind-checksums.txt") >"$work/$kind-$label-hash.log" 2>&1
}
verify_snapshot_and_hash() {
 local kind=$1 root=$2 seal=$3 label=$4
 snapshot "$root" "$work/$kind-$label-before.txt"
 validate_snapshot "$kind" "$work/$kind-$label-before.txt" "$seal"
 hash_tree "$kind" "$root" "$label"
 snapshot "$root" "$work/$kind-$label-after.txt"
 validate_snapshot "$kind" "$work/$kind-$label-after.txt" "$seal"
 bounded /usr/bin/cmp -- "$work/$kind-$label-before.txt" "$work/$kind-$label-after.txt"
}

phase=inbound-validation
for kind in source tool runtime; do
 verify_snapshot_and_hash "$kind" "${roots[$kind]}" input input-initial
done
phase=copy
for kind in source tool runtime; do
 destination=$output/$kind
 [[ ! -e $destination && ! -L $destination ]] || fail tree-copy-collision
 # No link following, no caller filter, no omission: complete tree, including empty dirs.
 bounded /usr/bin/cp --recursive --no-dereference --preserve=mode,timestamps -- "${roots[$kind]}" "$destination"
 snapshot "$destination" "$work/$kind-copied.txt"
 validate_snapshot "$kind" "$work/$kind-copied.txt" input
 hash_tree "$kind" "$destination" copied
 if [[ $kind != source ]]; then
  bounded /usr/bin/find -P "$destination" -type f -perm /111 -exec /usr/bin/chmod 0555 -- '{}' +
  bounded /usr/bin/find -P "$destination" -type f ! -perm /111 -exec /usr/bin/chmod 0444 -- '{}' +
 fi
 bounded /usr/bin/find -P "$destination" -depth -type d -exec /usr/bin/chmod 0555 -- '{}' +
 bounded /usr/bin/sync -f -- "$destination"
 verify_snapshot_and_hash "$kind" "$destination" sealed sealed-final
done
phase=control
copy_pinned "${values[source-review]}" "$work/source-review.json" "${values[source-review-sha256]}" 65536 0600
copy_pinned "${values[build-receipt]}" "$work/build-receipt.json" "${values[build-receipt-sha256]}" "$CONTROL_CAP" 0600
copy_pinned "${values[fixture]}" "$output/reviewed/checkpoint-n16-accepted-work.sh" "${values[fixture-sha256]}" 131072 0444
copy_pinned "${values[os-audit-script]}" "$output/reviewed/prepare-os-audit-v2.py" "${values[os-audit-script-sha256]}" 131072 0444
copy_pinned "${values[n16-parser-root]}/check_n16_records.py" "$output/reviewed/check_n16_records.py" "${values[n16-parser-sha256]}" 131072 0444
copy_pinned "${values[source-capture]}" "$work/source-capture.json" "${values[source-capture-sha256]}" 2097152 0600
bounded /usr/bin/chmod 0555 -- "$output/reviewed"
phase=final
for kind in source tool runtime; do
 verify_snapshot_and_hash "$kind" "${roots[$kind]}" input input-final
 bounded /usr/bin/cmp -- "$work/$kind-input-initial-before.txt" "$work/$kind-input-final-after.txt"
 # No later process may modify sealed destination trees under the root-private generation.
 destination=$output/$kind
 snapshot "$destination" "$work/$kind-destination-final.txt"
 bounded /usr/bin/cmp -- "$work/$kind-sealed-final-after.txt" "$work/$kind-destination-final.txt"
 pin "${values[$kind-map]}" "${map_shas[$kind]}" "$MAP_CAP"
 pin "${values[$kind-nodes]}" "${node_shas[$kind]}" "$NODES_CAP"
done
pin "${BASH_SOURCE[0]}" "${values[reviewed-script-sha256]}" 131072
pin "${values[source-capture]}" "${values[source-capture-sha256]}" 2097152
[[ -d $helper && ! -L $helper ]] || fail audit-helper-final-kind
[[ $(identity "$helper") == "$helper_identity" ]] || fail audit-helper-final-identity
[[ $(bounded /usr/bin/stat -c '%u:%g:%a' -- "$helper") == 0:0:555 ]] || fail audit-helper-final-custody
[[ -z $(bounded /usr/bin/find -P "$helper" -mindepth 1 -maxdepth 1 -print -quit) ]] || fail audit-helper-final-not-empty
check
bounded /usr/bin/jq -n --arg generation "${values[generation]}" --arg source "${values[source-commit]}" --arg root "$output" --arg script "${values[reviewed-script-sha256]}" --arg deadline "$hard_end" --arg fixture "${values[fixture-sha256]}" --arg audit "${values[os-audit-script-sha256]}" '
 {schema:"issue779-root-input-transport-v1",generation:$generation,source_commit:$source,
 root:$root,script_sha256:$script,original_deadline_monotonic_ms:$deadline,
 fixture_sha256:$fixture,os_audit_script_sha256:$audit,
 complete_membership_rechecked:true,fixture_executed:false,os_audit_executed:false,native_acceptance_claim:false}
 ' >"$work/transport-receipt.json"
bounded /usr/bin/sync -f -- "$work/transport-receipt.json"
check; [[ $(bounded /usr/bin/stat -c '%d:%i:%u:%g' -- "$output") == "$root_pin" ]] || fail output-root-substitution
bounded /usr/bin/chmod 0711 -- "$output"
check
printf 'ROOT_TRANSPORT_COMPLETED:%s:NO_FIXTURE_EXECUTION:NO_NATIVE_ACCEPTANCE\n' "${values[generation]}"
check; succeeded=1
