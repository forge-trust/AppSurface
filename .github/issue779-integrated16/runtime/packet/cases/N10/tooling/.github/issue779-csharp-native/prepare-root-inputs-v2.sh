#!/usr/bin/bash
# V2 batched preparation source only. Operator verifies this file BEFORE starting the interpreter.
set -euo pipefail
export PATH=/usr/bin:/usr/sbin LC_ALL=C LANG=C
umask 077
readonly SOURCE=914046e1e998ca89a5f0f5be39a90bbfb88a431d
readonly PARENT=71d004d6cf5a791ddbb8e73e3c538248985a8505
readonly SOURCE_PARENT=71d004d6cf5a791ddbb8e73e3c538248985a8505
readonly SOURCE_MAP=3f6379185c3ec98ddc7c1f1457b16427dcf3ee820a863e9fd78bf68ca578520e
readonly BUILD_SOURCE_MAP=a61f03468ca7fff6ef363a510c7945f3927c966fa3b62291e4c34c3c50a23c98
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
 mode|reviewed-script-sha256|generation|deadline-monotonic-ms|source-commit|source-root|source-map|source-map-sha256|source-nodes|source-nodes-sha256|tool-root|tool-map|tool-map-sha256|tool-nodes|tool-nodes-sha256|runtime-root|runtime-map|runtime-map-sha256|runtime-nodes|runtime-nodes-sha256|source-review|source-review-sha256|build-receipt|build-receipt-sha256|fixture|fixture-sha256|os-audit-script|os-audit-script-sha256|n10-parser-root|n10-parser-sha256|source-capture|source-capture-sha256) ;;
 *) fail unknown-option ;;
 esac
done
if [[ $mode == prepare-only ]]; then
 printf '%s\n' 'PREPARATION_ONLY:NO_INPUTS_VERIFIED:NO_TRANSPORT:NO_FIXTURE_EXECUTION'; exit 0
fi
readonly N10_INDEPENDENT_REVIEW_CLEAR=1
((N10_INDEPENDENT_REVIEW_CLEAR==1)) || fail n10-independent-review-pending
[[ $EUID == 0 && $OSTYPE == linux* ]] || fail trusted-linux-root-required
for key in reviewed-script-sha256 generation deadline-monotonic-ms source-commit source-root source-map source-map-sha256 source-nodes source-nodes-sha256 tool-root tool-map tool-map-sha256 tool-nodes tool-nodes-sha256 runtime-root runtime-map runtime-map-sha256 runtime-nodes runtime-nodes-sha256 source-review source-review-sha256 build-receipt build-receipt-sha256 fixture fixture-sha256 os-audit-script os-audit-script-sha256 n10-parser-root n10-parser-sha256 source-capture source-capture-sha256; do
 [[ -n ${values[$key]-} ]] || fail missing-input
done
hash_syntax() { [[ $1 =~ ^[0-9a-f]{64}$ ]] || fail digest-syntax; }
for key in "${!values[@]}"; do [[ $key != *sha256 ]] || hash_syntax "${values[$key]}"; done
[[ ${values[generation]} =~ ^[0-9a-f]{32}$ && ${values[source-commit]} == 914046e1e998ca89a5f0f5be39a90bbfb88a431d ]] || fail source-generation-pin
[[ ${values[n10-parser-sha256]} == 504bed99afcb6faf1aac03f245bc7520afa88c02aebea116f9dcf1ec4ccbc98d && ${values[source-capture-sha256]} == b070e3a38e726c698bc2608cd86987a204ceaf164bc12dff53d7d3d196a1c953 ]] || fail n10-source-parser-pins
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
pin "${values[n10-parser-root]}/check_n10_pending_start.py" "${values[n10-parser-sha256]}" 131072
bounded /usr/bin/jq -e --arg head "$SOURCE" --arg parent "$PARENT" --arg source_parent "$SOURCE_PARENT" '.schema=="issue779-csharp-common-source-capture-v4" and .validation_commit==$head and .previous_validation_commit==$source_parent and .source_count==2872 and .source_tree=="c5dd0770c00b14b61f32851adeaad1cce4f2d7a8" and .clean==true and .index_checksum_verified==true and .index_tree_blob_modes_equal==true and .no_alternates==true and .native_credit==false and .draft_pr_created==false and .all16_final_integrated_native_verified==false and (.source.sha256|type)=="object" and (.source.sha256|length)==2872 and (.source.modes|type)=="object" and (.source.modes|length)==2872 and ((.source.sha256|keys)==(.source.modes|keys))' "${values[source-capture]}" >/dev/null
capture_map=$(bounded /usr/bin/jq -cSj '{source_sha256:.source.sha256,source_modes:.source.modes}' ${values[source-capture]}) || fail capture-map-projection
capture_map_sha=$(printf '%s' "$capture_map" | bounded /usr/bin/sha256sum); capture_map_sha=${capture_map_sha:0:64}
[[ $capture_map_sha == "$SOURCE_MAP" ]] || fail canonical-source-map-digest
bounded /usr/bin/python3 -B - "${values[build-receipt]}" <<'INTEGRATED_BUILD_RECEIPT'
import base64, hashlib, zlib
REVIEWED_READER_PACKED = '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'
REVIEWED_READER_LENGTH = 14103
REVIEWED_READER_SHA256 = '8ac420e122bb116c0510d7561e89fec0f98c4bdd9f5fbaa3ca08dd795c5f3b65'
if not 0 < REVIEWED_READER_LENGTH <= 32768 or len(REVIEWED_READER_PACKED) > 65536:
    raise ValueError('adapter-reader-size')
_packed = base64.b64decode(REVIEWED_READER_PACKED, validate=True)
_decoder = zlib.decompressobj()
_reader = _decoder.decompress(_packed, REVIEWED_READER_LENGTH + 1)
if (len(_reader) != REVIEWED_READER_LENGTH or not _decoder.eof or
        _decoder.unused_data or _decoder.unconsumed_tail):
    raise ValueError('adapter-reader-compression')
if hashlib.sha256(_reader).hexdigest() != REVIEWED_READER_SHA256:
    raise ValueError('adapter-reader-pin')
exec(compile(_reader, '<pinned-fixed-receipt-reader>', 'exec'), {})
INTEGRATED_BUILD_RECEIPT
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
copy_pinned "${values[build-receipt]}" "$work/build-receipt.json" "${values[build-receipt-sha256]}" "$CONTROL_CAP" 0600
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
  ([.files[].bytes]|add)==$receipt[0].artifacts[$kind].total_bytes and (if $kind=="source" then (.files|length)==2872 else true end)
 ' "${nodes[$kind]}" >/dev/null
 bounded /usr/bin/jq -r '.files|to_entries|sort_by(.key)[]|[.value.mode,.value.sha256,.key]|@tsv' "${nodes[$kind]}" >"$work/$kind-derived.tsv"
 bounded /usr/bin/cmp -- "${maps[$kind]}" "$work/$kind-derived.tsv"
 bounded /usr/bin/jq -r '.files|to_entries|sort_by(.key)[]|.value.sha256+"  "+.key' "${nodes[$kind]}" >"$work/$kind-checksums.txt"
done

for kind in source tool runtime; do
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
  (if $name=="source" then (.files|length)==2872 and
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
copy_pinned "${values[fixture]}" "$output/reviewed/checkpoint-n10-pending-start.sh" "${values[fixture-sha256]}" 131072 0444
copy_pinned "${values[os-audit-script]}" "$output/reviewed/prepare-os-audit-v2.py" "${values[os-audit-script-sha256]}" 131072 0444
copy_pinned "${values[n10-parser-root]}/check_n10_pending_start.py" "$output/reviewed/check_n10_pending_start.py" "${values[n10-parser-sha256]}" 131072 0444
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
