#!/usr/bin/bash
# Source preparation only. Trusted operator verifies/installs this script BEFORE Bash.
# No acquired code, managed entry, systemd operation, or authority is executed here.
set -euo pipefail
export PATH=/usr/bin:/usr/sbin LC_ALL=C LANG=C
umask 077
readonly SOURCE=957da1f23ff743778fa39e3e559511ccdb281345
readonly PARENT=fc59bb1254294d624f6779f928020edfe988dd5e
readonly SOURCE_MAP=3609d645da23c550e88fadb7f8df8175206c5fd4af02bc73341a2ec87b7fb009
readonly FILE_CAP=268435456 TREE_CAP=1073741824 NODE_CAP=8192 RESERVE_MS=5000
declare -A v=() roots=() maps=() nodes=() map_hash=() node_hash=()
output=; work=; success=0
fail() { printf 'INBOUND_REJECTED:%s\n' "$1" >&2; exit 1; }
while (($#)); do
 case "$1" in
 --execute) [[ -z ${v[execute]+x} ]] || fail duplicate-option; v[execute]=1; shift ;;
 --generation|--build-root|--reviewed-root|--deadline-monotonic-ms|--reviewed-script-sha256|--transport-sha256|--fixture-sha256|--audit-sha256|--source-review-sha256|--helper-build-root|--helper-build-receipt-sha256)
  (($#>=2)) || fail option-value; k=${1#--}; [[ -z ${v[$k]+x} ]] || fail duplicate-option; v[$k]=$2; shift 2 ;;
 *) fail unknown-option ;;
 esac
done
for k in execute generation build-root reviewed-root deadline-monotonic-ms reviewed-script-sha256 transport-sha256 fixture-sha256 audit-sha256 source-review-sha256 helper-build-root helper-build-receipt-sha256; do
 [[ -n ${v[$k]-} ]] || fail missing-option
done
readonly N03_INDEPENDENT_REVIEW_CLEAR=1
((N03_INDEPENDENT_REVIEW_CLEAR==1)) || fail n03-review-pending
[[ $EUID == 0 && $OSTYPE == linux* && ${v[generation]} =~ ^[0-9a-f]{32}$ ]] || fail root-platform-generation
for k in reviewed-script-sha256 transport-sha256 fixture-sha256 audit-sha256 source-review-sha256 helper-build-receipt-sha256; do
 [[ ${v[$k]} =~ ^[0-9a-f]{64}$ ]] || fail digest-shape
done
[[ ${v[transport-sha256]} == b072ab925ef797f0664d5fb88f2f704a12d14173d19924de28a3f522a34b928d &&
   ${v[fixture-sha256]} == e2879a97039dae265c515ad663cb4dcaaa2f468855590e1f5036c6a1f4b16829 &&
   ${v[audit-sha256]} == 0be5002ebb3d44e55b9404d557259248cd75ea5d223fb565466903cb0d13774b &&
   ${v[source-review-sha256]} == 09fef890ed350fbcb64d62bc41d442695218584f00707b87a7268ca063d6b9b3 ]] || fail frozen-input-pins
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
REVIEWED_READER_LENGTH = 14104
REVIEWED_READER_SHA256 = 'c61e25ce66e22c87d16a7ca7091291c5b18d6201db81e3d4ab7c4ad85e7c8e4a'
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
for spec in prepare-root-inputs-v2.sh:transport checkpoint-n01-n02-v5.sh:fixture prepare-os-audit-v2.py:audit source-review.json:source-review; do
 name=${spec%:*}; key=${spec#*:}; cap=131072; [[ $key != source-review ]] || cap=65536
 trusted_pin "${v[reviewed-root]}/$name" "${v[$key-sha256]}" "$cap"
done
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
absolute "${v[helper-build-root]}"; [[ -d ${v[helper-build-root]} && ! -L ${v[helper-build-root]} ]] || fail helper-build-root
[[ ! -e ${v[helper-build-root]}/late-helper-publication-failure.json && ! -L ${v[helper-build-root]}/late-helper-publication-failure.json ]] || fail late-helper-publication
helper_receipt=${v[helper-build-root]}/helper-build-receipt.json
pin "$helper_receipt" "${v[helper-build-receipt-sha256]}" 1048576
copy_file "$helper_receipt" "$work/helper-build-receipt.json" "${v[helper-build-receipt-sha256]}" 1048576 0600
bounded /usr/bin/jq -e 'def integer: type=="number" and floor==. and .>=0;
def digest: type=="string" and test("^[0-9a-f]{64}$");
keys==["authority","commands","exit","helper_bytes","helper_directories","helper_entry_sha256","helper_files","helper_nodes_sha256","helper_root","helper_tsv_sha256","native_execution","recipe_sha256","runtime_required","schema","sdk_required","sdk_sha256","source_pins"] and .schema=="issue779-n03-helper-build-handoff-v1" and .exit==0 and .authority==false and .native_execution==false and
.recipe_sha256=="7bc0906967cfba542b3919e12855fb5f0e8946d0fe062a5d8371d4a5142101f4" and .sdk_required=="10.0.401" and .runtime_required=="10.0.12" and (.sdk_sha256|digest) and
.source_pins=={"Program.cs":"02a28424a2d57c56213fae8618d50df1019986cadf89246e2ce11411a23c967a","NativePeerBroker.csproj":"92da6c96d4c88fae754ece54af775a00c1d713c7e44b9a76bda7cc768773514e","packages.lock.json":"a29c6aa8cfb81874ff8bb78dc369d7416f28c9b8cc47e99592bfc019b20c41eb"} and
(.helper_files|integer) and .helper_files>0 and (.helper_directories|integer) and .helper_directories>0 and (.helper_files+.helper_directories)<=8192 and
(.helper_bytes|integer) and .helper_bytes<=1073741824 and (.helper_tsv_sha256|digest) and (.helper_nodes_sha256|digest) and (.helper_entry_sha256|digest) and
(.commands|type=="array" and length==3) and all(.commands[]; keys==["error","exit","forced_cleanup","group_absent","log","log_bytes","waited"] and .exit==0 and .waited==true and .group_absent==true and .forced_cleanup==false and .error==false and (.log_bytes|integer) and .log_bytes<=8388608) and
(.commands|map(.log))==["build-00.log","build-01.log","build-02.log"]' "$work/helper-build-receipt.json" >/dev/null
bounded /usr/bin/jq -acS . "$work/helper-build-receipt.json" >"$work/helper-receipt-canonical.json"
bounded /usr/bin/cmp -- "$work/helper-build-receipt.json" "$work/helper-receipt-canonical.json"
for kind in source tool runtime helper; do
 roots[$kind]=${v[build-root]}/handoff/$kind
 [[ $kind != helper ]] || roots[$kind]=${v[helper-build-root]}/helper
 maps[$kind]=$work/$kind.tsv; nodes[$kind]=$work/$kind-nodes.json
 if [[ $kind == helper ]]; then
  map_hash[$kind]=$(bounded /usr/bin/jq -r .helper_tsv_sha256 "$work/helper-build-receipt.json")
  node_hash[$kind]=$(bounded /usr/bin/jq -r .helper_nodes_sha256 "$work/helper-build-receipt.json")
 else
 map_hash[$kind]=$(bounded /usr/bin/jq -r --arg name "$kind" '.artifacts[$name].tsv_sha256' "$work/build-receipt.json")
 node_hash[$kind]=$(bounded /usr/bin/jq -r --arg name "$kind" '.artifacts[$name].nodes_sha256' "$work/build-receipt.json")
 fi
 metadata_base=${v[build-root]}/handoff; [[ $kind != helper ]] || metadata_base=${v[helper-build-root]}
 copy_file "$metadata_base/$kind.tsv" "${maps[$kind]}" "${map_hash[$kind]}" 1048576 0600
 copy_file "$metadata_base/$kind-nodes.json" "${nodes[$kind]}" "${node_hash[$kind]}" 4194304 0600
 bounded /usr/bin/jq -acS . "${nodes[$kind]}" >"$work/$kind-canonical.json"
 bounded /usr/bin/cmp -- "${nodes[$kind]}" "$work/$kind-canonical.json"
 bounded /usr/bin/jq -e --arg kind "$kind" --slurpfile receipt "$work/build-receipt.json" --slurpfile helper "$work/helper-build-receipt.json" '
  def rel: type=="string" and utf8bytelength<=4096 and (test("[\u0000-\u001f\u007f\\\\:]")|not) and
   (split("/")|all(.[]; length>0 and .!="." and .!=".." and .!=".git" and utf8bytelength<=255));
  . as $m | type=="object" and keys==["directories","files","root_name","schema"] and .schema=="issue779-build-node-inventory-v1" and .root_name==(if $kind=="helper" then "tool" else $kind end) and
  (.files|type=="object") and (.directories|type=="object") and .directories["."]!=null and
  (.files|length)==(if $kind=="helper" then $helper[0].helper_files else $receipt[0].artifacts[$kind].file_count end) and
  ((.files|length)+(.directories|length))==(if $kind=="helper" then ($helper[0].helper_files+$helper[0].helper_directories) else $receipt[0].artifacts[$kind].node_count end) and
  all(.directories|to_entries[]; (.key=="." or (.key|rel)) and (.value|type=="object" and keys==["mode"] and .mode==(if $kind=="source" then "0700" else "0555" end))) and
  all(.files|to_entries[]; (.key|rel) and $m.directories[.key]==null and (.value|type=="object" and keys==["bytes","mode","sha256"] and
   (.sha256|type=="string" and test("^[0-9a-f]{64}$")) and (.bytes|type=="number" and floor==. and .>=0 and .<=268435456) and
   (if $kind=="source" then .mode=="0644" or .mode=="0755" else .mode=="0444" or .mode=="0555" end))) and
  ([.files[].bytes]|add)==(if $kind=="helper" then $helper[0].helper_bytes else $receipt[0].artifacts[$kind].total_bytes end) and
  (if $kind=="source" then (.files|length)==2872 else true end)
 ' "${nodes[$kind]}" > /dev/null
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
# Validate actual full source trees before the first tree copy. Original runner
# ownership is data, not safe-live-snapshot authority; repeated whole checks reject drift.
for a in source tool runtime helper; do for b in source tool runtime helper; do
 [[ $a == "$b" ]] && continue
 [[ ${roots[$a]} != "${roots[$b]}" && ${roots[$a]} != "${roots[$b]}/"* ]] || fail helper-overlap
 done; done
bounded /usr/bin/jq -e --arg entry "$(bounded /usr/bin/jq -r .helper_entry_sha256 "$work/helper-build-receipt.json")" '.files["NativePeerBroker.dll"].sha256==$entry' "${nodes[helper]}" >/dev/null
for kind in source tool runtime helper; do verify "$kind" "${roots[$kind]}" original original-first; done
for kind in source tool runtime helper; do
 bounded /usr/bin/cp --recursive --no-dereference --preserve=mode,timestamps -- "${roots[$kind]}" "$output/$kind"
 snapshot "$output/$kind" "$work/$kind-copied.txt"; validate "$kind" "$work/$kind-copied.txt" original
 bounded /usr/bin/find -P "$output/$kind" -exec /usr/bin/chown --no-dereference 0:0 -- '{}' +
 bounded /usr/bin/sync -f -- "$output/$kind"
 verify "$kind" "$output/$kind" root destination-final
 verify "$kind" "${roots[$kind]}" original original-final
 bounded /usr/bin/cmp -- "$work/$kind-original-first-before.txt" "$work/$kind-original-final-after.txt"
 metadata_base=${v[build-root]}/handoff; [[ $kind != helper ]] || metadata_base=${v[helper-build-root]}
 pin "$metadata_base/$kind.tsv" "${map_hash[$kind]}" 1048576
 pin "$metadata_base/$kind-nodes.json" "${node_hash[$kind]}" 4194304
done
for spec in prepare-root-inputs-v2.sh:transport checkpoint-n01-n02-v5.sh:fixture prepare-os-audit-v2.py:audit source-review.json:source-review; do
 name=${spec%:*}; key=${spec#*:}; cap=131072; destination=$output/scripts/$name; mode=0444
 if [[ $key == source-review ]]; then cap=65536; destination=$work/source-review.json; mode=0600; fi
 trusted_pin "${v[reviewed-root]}/$name" "${v[$key-sha256]}" "$cap"
 copy_file "${v[reviewed-root]}/$name" "$destination" "${v[$key-sha256]}" "$cap" "$mode"
done
pin "$helper_receipt" "${v[helper-build-receipt-sha256]}" 1048576
[[ ! -e ${v[helper-build-root]}/late-helper-publication-failure.json && ! -L ${v[helper-build-root]}/late-helper-publication-failure.json ]] || fail late-helper-publication
pin "$receipt" "$receipt_hash" 1048576
[[ $(identity "$receipt") == "$receipt_before" ]] || fail original-receipt-changed
[[ ! -e ${v[build-root]}/receipts/late-publication-failure.json && ! -L ${v[build-root]}/receipts/late-publication-failure.json ]] || fail late-build-failure
for kind in source tool runtime helper; do
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
