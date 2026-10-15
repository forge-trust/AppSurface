#!/usr/bin/bash
# V2 batched preparation source only. Operator verifies this file BEFORE starting the interpreter.
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
readonly FILE_CAP=268435456 TREE_CAP=1073741824 NODE_CAP=8192
readonly MAP_CAP=1048576 NODES_CAP=4194304 CONTROL_CAP=1048576 RESERVE_MS=5000
mode=prepare-only; phase=arguments; output=; succeeded=0
declare -A values=() roots=() maps=() nodes=() map_shas=() node_shas=() map_hash=() node_hash=()
fail() { printf 'ROOT_TRANSPORT_REJECTED:%s\n' "$1" >&2; exit 1; }
verify_source_capture_map() {
 local path=$1 digest
 digest=$(bounded /usr/bin/jq -cSj '{source_sha256:.source.sha256,source_modes:.source.modes}' "$path" | bounded /usr/bin/sha256sum) || fail source-capture-map
 [[ ${digest:0:64} == "$SOURCE_MAP" ]] || fail source-capture-map-digest
}
while (($#)); do
 case "$1" in
  --prepare-only|--execute) [[ -z ${values[mode]+x} ]] || fail duplicate-option; values[mode]=1; mode=${1#--}; shift ;;
  --*) (($#>=2)) || fail option-value; key=${1#--}; [[ -z ${values[$key]+x} ]] || fail duplicate-option; values[$key]=$2; shift 2 ;;
  *) fail unknown-option ;;
 esac
done
for key in "${!values[@]}"; do
 case "$key" in
 mode|reviewed-script-sha256|generation|deadline-monotonic-ms|source-commit|source-root|source-map|source-map-sha256|source-nodes|source-nodes-sha256|tool-root|tool-map|tool-map-sha256|tool-nodes|tool-nodes-sha256|runtime-root|runtime-map|runtime-map-sha256|runtime-nodes|runtime-nodes-sha256|source-review|source-review-sha256|build-receipt|build-receipt-sha256|fixture|fixture-sha256|os-audit-script|os-audit-script-sha256|n09-parser-root|n09-parser-sha256|projection-parser-root|projection-parser-pins|source-capture|source-capture-sha256) ;;
 *) fail unknown-option ;;
 esac
done
if [[ $mode == prepare-only ]]; then
 printf '%s\n' 'PREPARATION_ONLY:NO_INPUTS_VERIFIED:NO_TRANSPORT:NO_FIXTURE_EXECUTION'; exit 0
fi
readonly N09_INDEPENDENT_REVIEW_CLEAR=1
((N09_INDEPENDENT_REVIEW_CLEAR==1)) || fail n09-independent-review-pending
[[ $EUID == 0 && $OSTYPE == linux* ]] || fail trusted-linux-root-required
for key in reviewed-script-sha256 generation deadline-monotonic-ms source-commit source-root source-map source-map-sha256 source-nodes source-nodes-sha256 tool-root tool-map tool-map-sha256 tool-nodes tool-nodes-sha256 runtime-root runtime-map runtime-map-sha256 runtime-nodes runtime-nodes-sha256 source-review source-review-sha256 build-receipt build-receipt-sha256 fixture fixture-sha256 os-audit-script os-audit-script-sha256 n09-parser-root n09-parser-sha256 projection-parser-root projection-parser-pins source-capture source-capture-sha256; do
 [[ -n ${values[$key]-} ]] || fail missing-input
done
hash_syntax() { [[ $1 =~ ^[0-9a-f]{64}$ ]] || fail digest-syntax; }
for key in "${!values[@]}"; do [[ $key != *sha256 ]] || hash_syntax "${values[$key]}"; done
[[ ${values[generation]} =~ ^[0-9a-f]{32}$ && ${values[source-commit]} == 71d004d6cf5a791ddbb8e73e3c538248985a8505 ]] || fail source-generation-pin
[[ ${values[n09-parser-sha256]} == "$N09_PARSER_SHA256" && ${values[source-capture-sha256]} == 8f5822ab0f5da555ef142a28321631f1960e61463fe14a83b33e83d521934046 ]] || fail n09-source-parser-pins
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
[[ ${values[projection-parser-root]} == "${values[n09-parser-root]}" && ${values[projection-parser-pins]} == "${values[projection-parser-root]}/projection-parser-pins.tsv" ]] || fail projection-parser-fixed-paths
pin "${values[n09-parser-root]}/check_n09_records.py" "$N09_PARSER_SHA256" 131072
[[ $(bounded /usr/bin/stat -c '%u:%g:%a:%h' -- "${values[n09-parser-root]}/check_n09_records.py") == 0:0:444:1 ]] || fail n09-parser-custody
pin "${values[projection-parser-pins]}" "$PROJECTION_PINS_SHA256" 2048
[[ $(bounded /usr/bin/stat -c '%u:%g:%a:%h' -- "${values[projection-parser-pins]}") == 0:0:444:1 ]] || fail projection-parser-pins-custody
projection_pins_before=$(identity "${values[projection-parser-pins]}")
mapfile -t projection_rows <"${values[projection-parser-pins]}"
[[ $(identity "${values[projection-parser-pins]}") == "$projection_pins_before" && ${#projection_rows[@]} == 3 ]] || fail projection-parser-pin-rows
[[ ${projection_rows[0]} == $'check_kernel_record.py\t'"$KERNEL_PARSER_SHA256" && ${projection_rows[1]} == $'check_cancellation_records.py\t'"$CANCELLATION_PARSER_SHA256" && ${projection_rows[2]} == $'cancellation_adapter.py\t'"$CANCELLATION_ADAPTER_SHA256" ]] || fail projection-parser-pin-rows
for spec in check_kernel_record.py:$KERNEL_PARSER_SHA256 check_cancellation_records.py:$CANCELLATION_PARSER_SHA256 cancellation_adapter.py:$CANCELLATION_ADAPTER_SHA256; do
 name=${spec%%:*}; hash=${spec#*:}; parser_file=${values[projection-parser-root]}/$name
 pin "$parser_file" "$hash" 65536
 [[ $(bounded /usr/bin/stat -c '%u:%g:%a:%h' -- "$parser_file") == 0:0:444:1 ]] || fail projection-parser-source-custody
done
bounded /usr/bin/jq -e --arg head "$SOURCE" --arg parent "$PARENT" '.schema=="issue779-csharp-common-source-capture-v4" and .validation_commit==$head and .previous_validation_commit==$parent and .source_count==2872 and .source_tree=="d15b1f3a7532947d525e4d74cdc7457f6b8923ea" and .clean==true and .index_checksum_verified==true and .index_tree_blob_modes_equal==true and .no_alternates==true and .native_credit==false and .draft_pr_created==false and .all16_final_integrated_native_verified==false and (.source|type)=="object" and ((.source|keys)==["modes","sha256"]) and (.source.sha256|type)=="object" and (.source.modes|type)=="object" and (.source.sha256|length)==2872 and (.source.modes|length)==2872 and ((.source.sha256|keys)==(.source.modes|keys))' "${values[source-capture]}" >/dev/null
verify_source_capture_map "${values[source-capture]}"
bounded /usr/bin/python3 -B - "${values[build-receipt]}" <<'INTEGRATED_BUILD_RECEIPT'
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
copy_pinned "${values[fixture]}" "$output/reviewed/checkpoint-n09-retained-slot.sh" "${values[fixture-sha256]}" 131072 0444
copy_pinned "${values[os-audit-script]}" "$output/reviewed/prepare-os-audit-v2.py" "${values[os-audit-script-sha256]}" 131072 0444
copy_pinned "${values[n09-parser-root]}/check_n09_records.py" "$output/reviewed/check_n09_records.py" "$N09_PARSER_SHA256" 131072 0444
for spec in check_kernel_record.py:$KERNEL_PARSER_SHA256 check_cancellation_records.py:$CANCELLATION_PARSER_SHA256 cancellation_adapter.py:$CANCELLATION_ADAPTER_SHA256; do
 name=${spec%%:*}; hash=${spec#*:}
 copy_pinned "${values[projection-parser-root]}/$name" "$output/reviewed/$name" "$hash" 65536 0444
done
copy_pinned "${values[projection-parser-pins]}" "$output/reviewed/projection-parser-pins.tsv" "$PROJECTION_PINS_SHA256" 2048 0444
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
