#!/usr/bin/bash
# Source preparation only. Trusted operator verifies/installs this script BEFORE Bash.
# No acquired code, managed entry, systemd operation, or authority is executed here.
set -euo pipefail
export PATH=/usr/bin:/usr/sbin LC_ALL=C LANG=C
umask 077
readonly SOURCE=5b4ed381badb91de47d793715d05b6ae7737e39e
readonly PARENT=914046e1e998ca89a5f0f5be39a90bbfb88a431d
readonly SOURCE_PARENT=914046e1e998ca89a5f0f5be39a90bbfb88a431d
readonly SOURCE_MAP=93f8bd47943773663375d0f37c440a0ea7a07a701359a4414944f87918ea389f
readonly BUILD_SOURCE_MAP=758a0f769ca67ce6f1fda2eea3569a373efafb192e2d2c2cce2224809b352a7d
readonly FILE_CAP=268435456 TREE_CAP=1073741824 NODE_CAP=8192 RESERVE_MS=5000
declare -A v=() roots=() maps=() nodes=() map_hash=() node_hash=()
output=; work=; success=0
fail() { printf 'INBOUND_REJECTED:%s\n' "$1" >&2; exit 1; }
while (($#)); do
 case "$1" in
 --execute) [[ -z ${v[execute]+x} ]] || fail duplicate-option; v[execute]=1; shift ;;
 --generation|--build-root|--reviewed-root|--deadline-monotonic-ms|--reviewed-script-sha256|--transport-sha256|--fixture-sha256|--audit-sha256|--source-review-sha256|--source-capture-sha256|--n10-parser-sha256)
  (($#>=2)) || fail option-value; k=${1#--}; [[ -z ${v[$k]+x} ]] || fail duplicate-option; v[$k]=$2; shift 2 ;;
 *) fail unknown-option ;;
 esac
done
for k in execute generation build-root reviewed-root deadline-monotonic-ms reviewed-script-sha256 transport-sha256 fixture-sha256 audit-sha256 source-review-sha256 source-capture-sha256 n10-parser-sha256; do
 [[ -n ${v[$k]-} ]] || fail missing-option
done
readonly N10_INDEPENDENT_REVIEW_CLEAR=1
((N10_INDEPENDENT_REVIEW_CLEAR==1)) || fail n10-independent-review-pending
[[ $EUID == 0 && $OSTYPE == linux* && ${v[generation]} =~ ^[0-9a-f]{32}$ ]] || fail root-platform-generation
for k in reviewed-script-sha256 transport-sha256 fixture-sha256 audit-sha256 source-review-sha256 source-capture-sha256 n10-parser-sha256; do
 [[ ${v[$k]} =~ ^[0-9a-f]{64}$ ]] || fail digest-shape
done
[[ ${v[source-capture-sha256]} == 4ad23a2fdeccf9abbcd06724b9c97e3a4c1751ecb45e8321cc35bd79c036d540 && ${v[n10-parser-sha256]} == 504bed99afcb6faf1aac03f245bc7520afa88c02aebea116f9dcf1ec4ccbc98d ]] || fail n10-source-parser-pins
mono() {
 local stamp rest whole fraction
 IFS=' ' read -r stamp rest </proc/uptime || return 1
 [[ $stamp =~ ^[0-9]+\.[0-9]+$ ]] || return 1
 whole=${stamp%%.*}; fraction=${stamp#*.}000; fraction=${fraction:0:3}
 printf '%s' "$((10#$whole*1000+10#$fraction))"
}
[[ ${v[deadline-monotonic-ms]} =~ ^[1-9][0-9]{0,14}$ ]] || fail deadline-shape
start=$(mono) || fail monotonic-clock
readonly hard_end=${v[deadline-monotonic-ms]} work_end=$((${v[deadline-monotonic-ms]}-RESERVE_MS))
((hard_end-start>RESERVE_MS && hard_end-start<=120000)) || fail original-deadline-bound
remaining() { local now left; now=$(mono) || return 1; ((now<work_end)) || return 1; left=$((work_end-now)); printf '%d.%03d' "$((left/1000))" "$((left%1000))"; }
check() { remaining >/dev/null || fail original-deadline; }
bounded() { local seconds; seconds=$(remaining) || fail original-deadline; /usr/bin/timeout --signal=KILL "$seconds" "$@" 2>/dev/null || fail bounded-operation; check; }
for cmd in bash timeout stat sha256sum find sort cmp jq dd mkdir chmod chown cp head wc sync; do
 [[ -x /usr/bin/$cmd ]] || fail missing-trusted-tool
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
protected_parent() {
 local path=$1 fact uid gid mode
 absolute "$path"
 while :; do
  [[ -d $path && ! -L $path ]] || fail protected-parent-kind
  fact=$(bounded /usr/bin/stat -c '%u:%g:%a' -- "$path"); IFS=: read -r uid gid mode <<<"$fact"
  [[ $uid == 0 && $gid == 0 && $mode =~ ^[0-7]{3,4}$ ]] && (((8#$mode & 0022)==0)) || fail protected-parent-metadata
  [[ $path == / ]] && break; path=${path%/*}; [[ -n $path ]] || path=/
 done
}
identity() { bounded /usr/bin/stat -c '%d:%i:%s:%f:%h:%u:%g:%y:%z' -- "$1"; }
pin() {
 local path=$1 hash=$2 cap=$3 before after size
 absolute "$path"; [[ -f $path && ! -L $path ]] || fail regular-input
 before=$(identity "$path"); size=$(bounded /usr/bin/stat -c %s -- "$path")
 [[ $size =~ ^[0-9]+$ ]] && ((size<=cap)) || fail file-size-bound
 [[ $(bounded /usr/bin/stat -c %h -- "$path") == 1 ]] || fail file-links
 after=$(bounded /usr/bin/sha256sum -- "$path"); [[ ${after:0:64} == "$hash" && $(identity "$path") == "$before" ]] || fail file-pin-changed
}
trusted_pin() {
 local path=$1 hash=$2 cap=$3 fact uid gid mode links
 protected_parent "${path%/*}"; pin "$path" "$hash" "$cap"
 fact=$(bounded /usr/bin/stat -c '%u:%g:%a:%h' -- "$path"); IFS=: read -r uid gid mode links <<<"$fact"
 [[ $uid == 0 && $gid == 0 && $links == 1 && $mode =~ ^[0-7]{3,4}$ ]] && (((8#$mode & 0022)==0)) || fail reviewed-input-metadata
}
trusted_pin "${BASH_SOURCE[0]}" "${v[reviewed-script-sha256]}" 131072
absolute "${v[build-root]}"; absolute "${v[reviewed-root]}"; protected_parent "${v[reviewed-root]}"
[[ -d ${v[build-root]} && ! -L ${v[build-root]} ]] || fail build-root-kind
receipt=${v[build-root]}/receipts/build-receipt.json
[[ ! -e ${v[build-root]}/receipts/late-publication-failure.json && ! -L ${v[build-root]}/receipts/late-publication-failure.json ]] || fail late-build-failure
absolute "$receipt"; [[ -f $receipt && ! -L $receipt && $(bounded /usr/bin/stat -c %h -- "$receipt") == 1 && $(bounded /usr/bin/stat -c %s -- "$receipt") -le 1048576 ]] || fail build-receipt-shape
receipt_before=$(identity "$receipt"); receipt_hash=$(bounded /usr/bin/sha256sum -- "$receipt"); receipt_hash=${receipt_hash:0:64}
# Success has no top-level failure member. Command records have failure:null.
bounded /usr/bin/python3 -B - "$receipt" <<'INTEGRATED_BUILD_RECEIPT'
import base64, hashlib, zlib
REVIEWED_READER_PACKED = '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'
REVIEWED_READER_LENGTH = 14103
REVIEWED_READER_SHA256 = '4cf3d010dbc9e988e0553f98f917908b1af450349b7f9a0b8eb8006beedabcbe'
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
[[ $(identity "$receipt") == "$receipt_before" ]] || fail build-receipt-changed
for spec in prepare-root-inputs-v2.sh:transport checkpoint-n10-pending-start.sh:fixture prepare-os-audit-v2.py:audit source-review.json:source-review check_n10_pending_start.py:n10-parser; do
 name=${spec%:*}; key=${spec#*:}; cap=131072; [[ $key != source-review ]] || cap=65536; [[ $key != n10-parser ]] || cap=131072
 trusted_pin "${v[reviewed-root]}/$name" "${v[$key-sha256]}" "$cap"
done
capture=${v[reviewed-root]}/capture-receipt.json
pin "$capture" "${v[source-capture-sha256]}" 2097152
bounded /usr/bin/jq -e --arg head "$SOURCE" --arg parent "$PARENT" --arg source_parent "$SOURCE_PARENT" '.schema=="issue779-csharp-common-source-capture-v4" and .validation_commit==$head and .previous_validation_commit==$source_parent and .source_count==2872 and .source_tree=="3bafd0fcd5321f1b29323f46df94b77eb48fa859" and .clean==true and .index_checksum_verified==true and .index_tree_blob_modes_equal==true and .no_alternates==true and .native_credit==false and .draft_pr_created==false and .all16_final_integrated_native_verified==false and (.source.sha256|type)=="object" and (.source.sha256|length)==2872 and (.source.modes|type)=="object" and (.source.modes|length)==2872 and ((.source.sha256|keys)==(.source.modes|keys))' "$capture" >/dev/null
capture_map=$(bounded /usr/bin/jq -cSj '{source_sha256:.source.sha256,source_modes:.source.modes}' "$capture") || fail capture-map-projection
capture_map_sha=$(printf '%s' "$capture_map" | bounded /usr/bin/sha256sum); capture_map_sha=${capture_map_sha:0:64}
[[ $capture_map_sha == "$SOURCE_MAP" ]] || fail canonical-source-map-digest
output=/var/lib/appsurface-evidence-inbound-${v[generation]}
protected_parent /var/lib; [[ ! -e $output && ! -L $output ]] || fail destination-exists
for input in "${v[build-root]}" "${v[reviewed-root]}"; do
 [[ $input != "$output" && $input != "$output/"* && $output != "$input/"* ]] || fail input-output-overlap
done
on_exit() {
 local code=$? now seconds left confirmed=0
 trap - EXIT
 now=$(mono) || now=$hard_end
 ((code!=0 || success!=1 || now>=work_end)) || exit 0
 # Never delete partial data. Confirm the original root700 within its existing
 # reserve; missing time or unavailable identity is unknown, not a custody claim.
 if ((now<hard_end)) && [[ -n ${outer_pin-} ]]; then
  left=$((hard_end-now)); printf -v seconds '%d.%03d' "$((left/1000))" "$((left%1000))"
  [[ $(/usr/bin/timeout --signal=KILL "$seconds" /usr/bin/stat -c '%d:%i:%u:%g:%a' -- "$output" 2>/dev/null) == "$outer_pin" ]] && confirmed=1
 fi
 if ((confirmed)); then printf '%s\n' 'INBOUND_FAILED:PARTIAL_ROOT700_CONFIRMED' >&2
 else printf '%s\n' 'INBOUND_FAILED:PARTIAL_QUARANTINE_UNKNOWN' >&2; fi
 ((code!=0)) || code=1
 exit "$code"
}
trap on_exit EXIT
bounded /usr/bin/mkdir -m 0700 -- "$output"
[[ $(bounded /usr/bin/stat -c '%u:%g:%a' -- "$output") == 0:0:700 ]] || fail fresh-root-custody
outer_pin=$(bounded /usr/bin/stat -c '%d:%i:%u:%g:%a' -- "$output")
bounded /usr/bin/mkdir -m 0700 -- "$output/control" "$output/scripts"
work=$output/control
copy_file() {
 local from=$1 to=$2 hash=$3 cap=$4 mode=$5 before size
 pin "$from" "$hash" "$cap"; before=$(identity "$from"); size=$(bounded /usr/bin/stat -c %s -- "$from")
 [[ ! -e $to && ! -L $to ]] || fail copy-collision
 bounded /usr/bin/dd if="$from" of="$to" iflag=nofollow,nonblock,count_bytes oflag=nofollow conv=fsync,excl count="$((size+1))" status=none
 [[ $(identity "$from") == "$before" && $(bounded /usr/bin/stat -c %s -- "$to") == "$size" ]] || fail copy-substitution
 bounded /usr/bin/chown --no-dereference 0:0 -- "$to"; bounded /usr/bin/chmod "$mode" -- "$to"
 trusted_pin "$to" "$hash" "$cap"; pin "$from" "$hash" "$cap"
}
copy_file "$receipt" "$work/build-receipt.json" "$receipt_hash" 1048576 0600
capture=${v[reviewed-root]}/capture-receipt.json
pin "$capture" "${v[source-capture-sha256]}" 2097152
bounded /usr/bin/jq -e --arg head "$SOURCE" --arg parent "$PARENT" --arg source_parent "$SOURCE_PARENT" '
 .schema=="issue779-csharp-common-source-capture-v4" and .validation_commit==$head and .previous_validation_commit==$source_parent and .source_count==2872 and .source_tree=="3bafd0fcd5321f1b29323f46df94b77eb48fa859" and .clean==true and .index_checksum_verified==true and .index_tree_blob_modes_equal==true and .no_alternates==true and .native_credit==false and .draft_pr_created==false and .all16_final_integrated_native_verified==false and (.source.sha256|type)=="object" and (.source.sha256|length)==2872 and (.source.modes|type)=="object" and (.source.modes|length)==2872 and ((.source.sha256|keys)==(.source.modes|keys))
' "$capture" >/dev/null
trusted_pin "${v[reviewed-root]}/check_n10_pending_start.py" "${v[n10-parser-sha256]}" 131072
copy_file "$capture" "$work/source-capture.json" "${v[source-capture-sha256]}" 2097152 0600
trusted_pin "${v[reviewed-root]}/check_n10_pending_start.py" "${v[n10-parser-sha256]}" 131072
copy_file "${v[reviewed-root]}/check_n10_pending_start.py" "$output/scripts/check_n10_pending_start.py" "${v[n10-parser-sha256]}" 131072 0444
# Restored from the reviewed N11 acquisition implementation's shared
# source/tool/runtime manifest path (v15 lines 189-252); helper-only authority
# and its fourth tree are intentionally excluded for N10.
for kind in source tool runtime; do
 roots[$kind]=${v[build-root]}/handoff/$kind
 maps[$kind]=$work/$kind.tsv; nodes[$kind]=$work/$kind-nodes.json
 map_hash[$kind]=$(bounded /usr/bin/jq -r --arg name "$kind" '.artifacts[$name].tsv_sha256' "$work/build-receipt.json")
 node_hash[$kind]=$(bounded /usr/bin/jq -r --arg name "$kind" '.artifacts[$name].nodes_sha256' "$work/build-receipt.json")
 metadata_base=${v[build-root]}/handoff
 copy_file "$metadata_base/$kind.tsv" "${maps[$kind]}" "${map_hash[$kind]}" 1048576 0600
 copy_file "$metadata_base/$kind-nodes.json" "${nodes[$kind]}" "${node_hash[$kind]}" 4194304 0600
 bounded /usr/bin/jq -acS . "${nodes[$kind]}" >"$work/$kind-canonical.json"
 bounded /usr/bin/cmp -- "${nodes[$kind]}" "$work/$kind-canonical.json"
 bounded /usr/bin/jq -e --arg kind "$kind" --slurpfile receipt "$work/build-receipt.json" '
  def rel: type=="string" and utf8bytelength<=4096 and (test("[\u0000-\u001f\u007f\\\\:]")|not) and
   (split("/")|all(.[]; length>0 and .!="." and .!=".." and .!=".git" and utf8bytelength<=255));
  . as $m | type=="object" and keys==["directories","files","root_name","schema"] and .schema=="issue779-build-node-inventory-v1" and .root_name==$kind and
  (.files|type=="object") and (.directories|type=="object") and .directories["."]!=null and
  (.files|length)==$receipt[0].artifacts[$kind].file_count and
  ((.files|length)+(.directories|length))==$receipt[0].artifacts[$kind].node_count and
  all(.directories|to_entries[]; (.key=="." or (.key|rel)) and (.value|type=="object" and keys==["mode"] and .mode==(if $kind=="source" then "0700" else "0555" end))) and
  all(.files|to_entries[]; (.key|rel) and $m.directories[.key]==null and (.value|type=="object" and keys==["bytes","mode","sha256"] and
   (.sha256|type=="string" and test("^[0-9a-f]{64}$")) and (.bytes|type=="number" and floor==. and .>=0 and .<=268435456) and
   (if $kind=="source" then .mode=="0644" or .mode=="0755" else .mode=="0444" or .mode=="0555" end))) and
  ([.files[].bytes]|add)==$receipt[0].artifacts[$kind].total_bytes and
  (if $kind=="source" then (.files|length)==2872 else true end)
 ' "${nodes[$kind]}" >/dev/null
 bounded /usr/bin/jq -r '.files|to_entries|sort_by(.key)[]|[.value.mode,.value.sha256,.key]|@tsv' "${nodes[$kind]}" >"$work/$kind-derived.tsv"
 bounded /usr/bin/cmp -- "${maps[$kind]}" "$work/$kind-derived.tsv"
 bounded /usr/bin/jq -r '.files|to_entries|sort_by(.key)[]|.value.sha256+"  "+.key' "${nodes[$kind]}" >"$work/$kind-checksums.txt"
done
snapshot() {
 local tree=$1 target=$2 size count
 absolute "$tree"; [[ -d $tree && ! -L $tree ]] || fail tree-kind
 bounded /usr/bin/bash -o pipefail -c '/usr/bin/find -P "$1" -printf "%P\t%y\t%m\t%U\t%G\t%n\t%s\t%D\t%i\t%T@\t%C@\n" | /usr/bin/head -c 8388609' -- "$tree" >"$target.raw"
 size=$(bounded /usr/bin/stat -c %s -- "$target.raw"); count=$(bounded /usr/bin/wc -l <"$target.raw")
 ((size<=8388608 && count>0 && count<=NODE_CAP)) || fail inventory-bound
 bounded /usr/bin/sort -- "$target.raw" >"$target"
}
validate() {
 local kind=$1 inventory=$2 owner=$3
 bounded /usr/bin/jq -Rse --slurpfile map "${nodes[$kind]}" --arg owner "$owner" '
  def num: test("^[0-9]+$");
  $map[0] as $m | if endswith("\n") then split("\n")[:-1]|map(split("\t")) else error("shape") end |
  map(if .[0]=="" then .[0]="." else . end) as $r |
  ($r|map(.[0])|sort)==(($m.files|keys)+($m.directories|keys)|sort) and
  ($r|map(.[0])|unique|length)==($r|length) and
  all($r[]; length==11 and (.[1]=="d" or .[1]=="f") and (.[3]|num) and (.[4]|num) and
   (if $owner=="root" then .[3]=="0" and .[4]=="0" else true end) and
   (.[5]|num) and (.[6]|num) and (.[7]|num) and (.[8]|num) and
   (if .[1]=="d" then $m.directories[.[0]]!=null and .[2]==$m.directories[.[0]].mode[1:] else
    $m.files[.[0]]!=null and .[5]=="1" and (.[6]|tonumber)==$m.files[.[0]].bytes and .[2]==$m.files[.[0]].mode[1:] end)) and
  ([$r[]|select(.[1]=="f")|.[6]|tonumber]|add)<=1073741824
 ' "$inventory" > /dev/null
}
verify() {
 local kind=$1 tree=$2 owner=$3 tag=$4
 snapshot "$tree" "$work/$kind-$tag-before.txt"; validate "$kind" "$work/$kind-$tag-before.txt" "$owner"
 (cd -- "$tree"; bounded /usr/bin/sha256sum --check --strict --status "$work/$kind-checksums.txt") >"$work/$kind-$tag-hash.log" 2>&1
 snapshot "$tree" "$work/$kind-$tag-after.txt"; validate "$kind" "$work/$kind-$tag-after.txt" "$owner"
 bounded /usr/bin/cmp -- "$work/$kind-$tag-before.txt" "$work/$kind-$tag-after.txt"
}
for kind in source tool runtime; do verify "$kind" "${roots[$kind]}" original original-first; done
for kind in source tool runtime; do
 bounded /usr/bin/cp --recursive --no-dereference --preserve=mode,timestamps -- "${roots[$kind]}" "$output/$kind"
 snapshot "$output/$kind" "$work/$kind-copied.txt"; validate "$kind" "$work/$kind-copied.txt" original
 bounded /usr/bin/find -P "$output/$kind" -exec /usr/bin/chown --no-dereference 0:0 -- '{}' +
 bounded /usr/bin/sync -f -- "$output/$kind"
 verify "$kind" "$output/$kind" root destination-final
 verify "$kind" "${roots[$kind]}" original original-final
 bounded /usr/bin/cmp -- "$work/$kind-original-first-before.txt" "$work/$kind-original-final-after.txt"
 metadata_base=${v[build-root]}/handoff
 pin "$metadata_base/$kind.tsv" "${map_hash[$kind]}" 1048576
 pin "$metadata_base/$kind-nodes.json" "${node_hash[$kind]}" 4194304
done
for spec in prepare-root-inputs-v2.sh:transport checkpoint-n10-pending-start.sh:fixture prepare-os-audit-v2.py:audit source-review.json:source-review; do
 name=${spec%:*}; key=${spec#*:}; cap=131072; destination=$output/scripts/$name; mode=0444
 if [[ $key == source-review ]]; then cap=65536; destination=$work/source-review.json; mode=0600; fi
 trusted_pin "${v[reviewed-root]}/$name" "${v[$key-sha256]}" "$cap"
 copy_file "${v[reviewed-root]}/$name" "$destination" "${v[$key-sha256]}" "$cap" "$mode"
done
pin "$receipt" "$receipt_hash" 1048576
[[ $(identity "$receipt") == "$receipt_before" ]] || fail original-receipt-changed
[[ ! -e ${v[build-root]}/receipts/late-publication-failure.json && ! -L ${v[build-root]}/receipts/late-publication-failure.json ]] || fail late-build-failure
for kind in source tool runtime; do
 snapshot "$output/$kind" "$work/$kind-last.txt"
 bounded /usr/bin/cmp -- "$work/$kind-destination-final-after.txt" "$work/$kind-last.txt"
done
trusted_pin "${BASH_SOURCE[0]}" "${v[reviewed-script-sha256]}" 131072
bounded /usr/bin/jq -ncS --arg g "${v[generation]}" --arg source "$SOURCE" --argjson deadline "$hard_end" --arg receipt "$receipt_hash" '
 {schema:"issue779-native-inbound-acquisition-v1",generation:$g,source_commit:$source,build_receipt_sha256:$receipt,
 original_deadline_monotonic_ms:$deadline,source_count:2872,acquired_code_executed:false,native_acceptance_claim:false}
 ' >"$work/acquisition-receipt.json"
bounded /usr/bin/sync -f -- "$work/acquisition-receipt.json"
[[ $(bounded /usr/bin/stat -c '%d:%i:%u:%g:%a' -- "$output") == "$outer_pin" ]] || fail final-outer-identity
check; printf '%s\n' 'INBOUND_ACQUIRED:DATA_ONLY:ORIGINAL_TRANSPORT_DEADLINE_UNCHANGED'; check; success=1
