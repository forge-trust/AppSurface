#!/usr/bin/bash
# Source preparation only. Trusted operator verifies/installs this script BEFORE Bash.
# No acquired code, managed entry, systemd operation, or authority is executed here.
set -euo pipefail
export PATH=/usr/bin:/usr/sbin LC_ALL=C LANG=C
umask 077
readonly SOURCE=914046e1e998ca89a5f0f5be39a90bbfb88a431d
readonly PARENT=71d004d6cf5a791ddbb8e73e3c538248985a8505
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
[[ ${v[transport-sha256]} == 7425a0d800cdb6723c284c7f45805654d525939e9c3a598e26f660d32d2bc11f &&
   ${v[fixture-sha256]} == ef1f114f6b64209cda6004be0dc5caa3d40bc62f8eb8485e1fc003c28c32d400 &&
   ${v[audit-sha256]} == 0be5002ebb3d44e55b9404d557259248cd75ea5d223fb565466903cb0d13774b &&
   ${v[source-review-sha256]} == e689617dab021d032257f68205743fa8b1bf4314d6503e62e6d7bd14cc12cd7f ]] || fail frozen-input-pins
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
REVIEWED_READER_SHA256 = '7425fb000d34daf95df22a1053c8aff37049cb136d7b4113c994ec1279acb87e'
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
keys==["authority","commands","exit","helper_bytes","helper_directories","helper_entry_sha256","helper_files","helper_nodes_sha256","helper_root","helper_tsv_sha256","native_execution","private_diagnostics_sha256","recipe_sha256","runtime_required","schema","sdk_required","sdk_sha256","source_pins"] and .schema=="issue779-n03-helper-build-handoff-v1" and .exit==0 and .authority==false and .native_execution==false and
.recipe_sha256=="a0188f106b75e1320aefac501c1057e66da973bda8c9e08a862bfebddd65390e" and .sdk_required=="10.0.401" and .runtime_required=="10.0.12" and (.sdk_sha256|digest) and (.private_diagnostics_sha256|digest) and
.source_pins=={"Program.cs":"02a28424a2d57c56213fae8618d50df1019986cadf89246e2ce11411a23c967a","NativePeerBroker.csproj":"92da6c96d4c88fae754ece54af775a00c1d713c7e44b9a76bda7cc768773514e","packages.lock.json":"a29c6aa8cfb81874ff8bb78dc369d7416f28c9b8cc47e99592bfc019b20c41eb"} and
(.helper_files|integer) and .helper_files>0 and (.helper_directories|integer) and .helper_directories>0 and (.helper_files+.helper_directories)<=8192 and
(.helper_bytes|integer) and .helper_bytes<=1073741824 and (.helper_tsv_sha256|digest) and (.helper_nodes_sha256|digest) and (.helper_entry_sha256|digest) and
(.commands|type=="array" and length==3)' "$work/helper-build-receipt.json" >/dev/null
bounded /usr/bin/jq -acS . "$work/helper-build-receipt.json" >"$work/helper-receipt-canonical.json"
bounded /usr/bin/cmp -- "$work/helper-build-receipt.json" "$work/helper-receipt-canonical.json"
helper_diagnostics=${v[helper-build-root]}/helper-build-private-diagnostics.json
helper_diagnostics_sha=$(bounded /usr/bin/jq -r .private_diagnostics_sha256 "$work/helper-build-receipt.json")
pin "$helper_diagnostics" "$helper_diagnostics_sha" 1048576
copy_file "$helper_diagnostics" "$work/helper-build-private-diagnostics.json" "$helper_diagnostics_sha" 1048576 0600
adapter_path="${BASH_SOURCE[0]%/*}/helper_receipt_adapter.py"
validator_path="${BASH_SOURCE[0]%/*}/helper_receipt_validator.py"
trusted_pin "$adapter_path" "04e5f6c5a2a388ced1df983520371ac03cdadf1fdb0c185537625b13bd43165c" 32768
trusted_pin "$validator_path" "106c6b5202f4795a351ef76b259177571d1a0691bf9f2e908ad145155104b630" 16384
bounded /usr/bin/python3.12 -I -S -B "$validator_path" N03 "$work/helper-build-receipt.json" "$work/helper-build-private-diagnostics.json" "a0188f106b75e1320aefac501c1057e66da973bda8c9e08a862bfebddd65390e" >/dev/null
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
