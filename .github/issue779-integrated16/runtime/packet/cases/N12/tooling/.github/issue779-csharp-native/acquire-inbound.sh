#!/usr/bin/bash
# Source preparation only. Trusted operator verifies/installs this script BEFORE Bash.
# No acquired code, managed entry, systemd operation, or authority is executed here.
set -euo pipefail
export PATH=/usr/bin:/usr/sbin LC_ALL=C LANG=C
umask 077
readonly SOURCE=914046e1e998ca89a5f0f5be39a90bbfb88a431d
readonly PARENT=71d004d6cf5a791ddbb8e73e3c538248985a8505
readonly SOURCE_MAP=d841ad3d4c91b7b84452c36b12622fbadab622b83e0e6cf0f887c2d2128674e6
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
[[ ${v[transport-sha256]} == e57caad169cb456f227260dce060c5fb5b0b6015125aaed0fa3e1bab6a4b610b &&
   ${v[fixture-sha256]} == 633f150f60fcfec584e75c6cb96e588b9bd2202791f0409aa813c8125b318136 &&
   ${v[audit-sha256]} == 0be5002ebb3d44e55b9404d557259248cd75ea5d223fb565466903cb0d13774b &&
   ${v[source-review-sha256]} == 44cff73178836337c31ba6d08827dc10ad169f5f59307b5c30a8a30ae0ae88a6 ]] || fail frozen-input-pins
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
REVIEWED_READER_SHA256 = '1864447ee9be3d65e6ec3d67e29f961067890d2638a71b3b7f574e7b29a027be'
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
for spec in prepare-root-inputs-v2.sh:transport checkpoint-n12-data-integration.sh:fixture prepare-os-audit-v2.py:audit source-review.json:source-review; do
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
for spec in prepare-root-inputs-v2.sh:transport checkpoint-n12-data-integration.sh:fixture prepare-os-audit-v2.py:audit source-review.json:source-review; do
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
