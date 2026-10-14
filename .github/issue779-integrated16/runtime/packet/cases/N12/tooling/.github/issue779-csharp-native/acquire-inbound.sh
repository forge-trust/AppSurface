#!/usr/bin/bash
# Source preparation only. Trusted operator verifies/installs this script BEFORE Bash.
# No acquired code, managed entry, systemd operation, or authority is executed here.
set -euo pipefail
export PATH=/usr/bin:/usr/sbin LC_ALL=C LANG=C
umask 077
readonly SOURCE=957da1f23ff743778fa39e3e559511ccdb281345
readonly PARENT=fc59bb1254294d624f6779f928020edfe988dd5e
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
[[ ${v[transport-sha256]} == 4750e6c1457df83dfb28f67fa039a20adbc073a6de0529ca122c80c31bafb31e &&
   ${v[fixture-sha256]} == fa63ed7d27cb46e5c62a15621ebff4513501268371bd2fefca99c914bf968da7 &&
   ${v[audit-sha256]} == 0be5002ebb3d44e55b9404d557259248cd75ea5d223fb565466903cb0d13774b &&
   ${v[source-review-sha256]} == 0fcfbb08c354f85554a50a3f1b2116368eedf6cebc3c3956848dcf820b4fa989 ]] || fail frozen-input-pins
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
REVIEWED_READER_SHA256 = 'f0f1ed1e7125882a6421991800a510f145855a04c3181f22587ca5886c512b4c'
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
