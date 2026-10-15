#!/usr/bin/bash
# Source preparation only. Trusted operator verifies/installs this script BEFORE Bash.
# No acquired code, managed entry, systemd operation, or authority is executed here.
set -euo pipefail
export PATH=/usr/bin:/usr/sbin LC_ALL=C LANG=C
umask 077
readonly SOURCE=71d004d6cf5a791ddbb8e73e3c538248985a8505
readonly PARENT=957da1f23ff743778fa39e3e559511ccdb281345
readonly SOURCE_MAP=312af063686918960e6082eba3db47709fd75beaf4199ee6e486a93bd42bd1d9
readonly BUILD_SOURCE_MAP=308346155ca912db28838729e5081791e2a7b6a50bcac726093c6f07d1d85abe
readonly KERNEL_PARSER_SHA256=90da0002db90ddb8b769bd3530551f10363ce772bc99327bdd27976be71cf3ab
readonly CANCELLATION_PARSER_SHA256=c45238cc3bd129a19af3d04dfa12bc83a82345b130e43f7296808ecee7e5cee6
readonly CANCELLATION_ADAPTER_SHA256=8b02473ad90695df1840a14af0e02d5d17c3bee79177eb826a228f3345b6ee0f
readonly PROJECTION_PINS_SHA256=889840621877bec5fbb3fa0cce5311be016a019d8b77ed0b329417deb10c8cc9
readonly N09_PARSER_SHA256=c4039008467aae0e7abbd82a85b7cf551b38c307f19d546236202a5d74b3647e
readonly FILE_CAP=268435456 TREE_CAP=1073741824 NODE_CAP=8192 RESERVE_MS=5000
declare -A v=() roots=() maps=() nodes=() map_hash=() node_hash=()
output=; work=; success=0
fail() { printf 'INBOUND_REJECTED:%s\n' "$1" >&2; exit 1; }
verify_source_capture_map() {
 local path=$1 digest
 digest=$(bounded /usr/bin/jq -cSj '{source_sha256:.source.sha256,source_modes:.source.modes}' "$path" | bounded /usr/bin/sha256sum) || fail source-capture-map
 [[ ${digest:0:64} == "$SOURCE_MAP" ]] || fail source-capture-map-digest
}
while (($#)); do
 case "$1" in
 --execute) [[ -z ${v[execute]+x} ]] || fail duplicate-option; v[execute]=1; shift ;;
 --generation|--build-root|--reviewed-root|--deadline-monotonic-ms|--reviewed-script-sha256|--transport-sha256|--fixture-sha256|--audit-sha256|--source-review-sha256|--source-capture-sha256|--n09-parser-sha256)
  (($#>=2)) || fail option-value; k=${1#--}; [[ -z ${v[$k]+x} ]] || fail duplicate-option; v[$k]=$2; shift 2 ;;
 *) fail unknown-option ;;
 esac
done
for k in execute generation build-root reviewed-root deadline-monotonic-ms reviewed-script-sha256 transport-sha256 fixture-sha256 audit-sha256 source-review-sha256 source-capture-sha256 n09-parser-sha256; do
 [[ -n ${v[$k]-} ]] || fail missing-option
done
readonly N09_INDEPENDENT_REVIEW_CLEAR=1
((N09_INDEPENDENT_REVIEW_CLEAR==1)) || fail n09-independent-review-pending
[[ $EUID == 0 && $OSTYPE == linux* && ${v[generation]} =~ ^[0-9a-f]{32}$ ]] || fail root-platform-generation
for k in reviewed-script-sha256 transport-sha256 fixture-sha256 audit-sha256 source-review-sha256 source-capture-sha256 n09-parser-sha256; do
 [[ ${v[$k]} =~ ^[0-9a-f]{64}$ ]] || fail digest-shape
done
[[ ${v[source-capture-sha256]} == 8f5822ab0f5da555ef142a28321631f1960e61463fe14a83b33e83d521934046 && ${v[n09-parser-sha256]} == "$N09_PARSER_SHA256" ]] || fail n09-source-parser-pins
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
REVIEWED_READER_SHA256 = 'eae6a5335d57f4d6ed7b62e61a918d3c29cee923f044858b75aac801f23c0570'
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
for spec in prepare-root-inputs-v2.sh:transport checkpoint-n09-retained-slot.sh:fixture prepare-os-audit-v2.py:audit source-review.json:source-review check_n09_records.py:n09-parser; do
 name=${spec%:*}; key=${spec#*:}; cap=131072; [[ $key != source-review ]] || cap=65536; [[ $key != n09-parser ]] || cap=131072
 trusted_pin "${v[reviewed-root]}/$name" "${v[$key-sha256]}" "$cap"
done
for spec in check_kernel_record.py:$KERNEL_PARSER_SHA256 check_cancellation_records.py:$CANCELLATION_PARSER_SHA256 cancellation_adapter.py:$CANCELLATION_ADAPTER_SHA256 projection-parser-pins.tsv:$PROJECTION_PINS_SHA256; do
 name=${spec%%:*}; hash=${spec#*:}; cap=65536; [[ $name != projection-parser-pins.tsv ]] || cap=2048
 source=${v[reviewed-root]}/$name
 trusted_pin "$source" "$hash" "$cap"
 [[ $(bounded /usr/bin/stat -c '%u:%g:%a:%h' -- "$source") == 0:0:444:1 ]] || fail projection-parser-source-mode
done
capture=${v[reviewed-root]}/capture-receipt.json
pin "$capture" "${v[source-capture-sha256]}" 2097152
bounded /usr/bin/jq -e --arg head "$SOURCE" --arg parent "$PARENT" '.schema=="issue779-csharp-common-source-capture-v4" and .validation_commit==$head and .previous_validation_commit==$parent and .source_count==2872 and .source_tree=="d15b1f3a7532947d525e4d74cdc7457f6b8923ea" and .clean==true and .index_checksum_verified==true and .index_tree_blob_modes_equal==true and .no_alternates==true and .native_credit==false and .draft_pr_created==false and .all16_final_integrated_native_verified==false and (.source|type)=="object" and ((.source|keys)==["modes","sha256"]) and (.source.sha256|type)=="object" and (.source.modes|type)=="object" and (.source.sha256|length)==2872 and (.source.modes|length)==2872 and ((.source.sha256|keys)==(.source.modes|keys))' "$capture" >/dev/null
verify_source_capture_map "$capture"
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
bounded /usr/bin/jq -e --arg head "$SOURCE" --arg parent "$PARENT" '
 .schema=="issue779-csharp-common-source-capture-v4" and .validation_commit==$head and .previous_validation_commit==$parent and .source_count==2872 and .source_tree=="d15b1f3a7532947d525e4d74cdc7457f6b8923ea" and .clean==true and .index_checksum_verified==true and .index_tree_blob_modes_equal==true and .no_alternates==true and .native_credit==false and .draft_pr_created==false and .all16_final_integrated_native_verified==false and (.source|type)=="object" and ((.source|keys)==["modes","sha256"]) and (.source.sha256|type)=="object" and (.source.modes|type)=="object" and (.source.sha256|length)==2872 and (.source.modes|length)==2872 and ((.source.sha256|keys)==(.source.modes|keys))
' "$capture" >/dev/null
verify_source_capture_map "$capture"
trusted_pin "${v[reviewed-root]}/check_n09_records.py" "$N09_PARSER_SHA256" 131072
copy_file "$capture" "$work/source-capture.json" "${v[source-capture-sha256]}" 2097152 0600
trusted_pin "${v[reviewed-root]}/check_n09_records.py" "$N09_PARSER_SHA256" 131072
copy_file "${v[reviewed-root]}/check_n09_records.py" "$output/scripts/check_n09_records.py" "$N09_PARSER_SHA256" 131072 0444
copy_file "${v[reviewed-root]}/check_kernel_record.py" "$output/scripts/check_kernel_record.py" "$KERNEL_PARSER_SHA256" 65536 0444
copy_file "${v[reviewed-root]}/check_cancellation_records.py" "$output/scripts/check_cancellation_records.py" "$CANCELLATION_PARSER_SHA256" 65536 0444
copy_file "${v[reviewed-root]}/cancellation_adapter.py" "$output/scripts/cancellation_adapter.py" "$CANCELLATION_ADAPTER_SHA256" 65536 0444
copy_file "${v[reviewed-root]}/projection-parser-pins.tsv" "$output/scripts/projection-parser-pins.tsv" "$PROJECTION_PINS_SHA256" 2048 0444
# Restored from the reviewed N11 acquisition implementation's shared
# source/tool/runtime manifest path (v15 lines 189-252); helper-only authority
# and its fourth tree are intentionally excluded for N09.
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
for spec in prepare-root-inputs-v2.sh:transport checkpoint-n09-retained-slot.sh:fixture prepare-os-audit-v2.py:audit source-review.json:source-review; do
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
