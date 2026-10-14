#!/usr/bin/bash
# Source preparation only. Trusted operator verifies/installs this script BEFORE Bash.
# No acquired code, managed entry, systemd operation, or authority is executed here.
set -euo pipefail
export PATH=/usr/bin:/usr/sbin LC_ALL=C LANG=C
umask 077
readonly SOURCE=957da1f23ff743778fa39e3e559511ccdb281345
readonly PARENT=fc59bb1254294d624f6779f928020edfe988dd5e
readonly SOURCE_MAP=19b5293c99dd9efd4057682af76f88e469a5e19c851b42ea6bde860a5f885034
readonly FILE_CAP=268435456 TREE_CAP=1073741824 NODE_CAP=8192 RESERVE_MS=5000
declare -A v=() roots=() maps=() nodes=() map_hash=() node_hash=()
output=; work=; success=0
readonly N04_INTEGRATION_REVIEW_CLEAR=1
((N04_INTEGRATION_REVIEW_CLEAR==1)) || { printf "INBOUND_REJECTED:n04-integration-pending\n" >&2; exit 1; }
fail() { printf 'INBOUND_REJECTED:%s\n' "$1" >&2; exit 1; }
while (($#)); do
 case "$1" in
 --execute) [[ -z ${v[execute]+x} ]] || fail duplicate-option; v[execute]=1; shift ;;
 --generation|--build-root|--reviewed-root|--deadline-monotonic-ms|--reviewed-script-sha256|--transport-sha256|--fixture-sha256|--audit-sha256|--source-review-sha256|--helper-build-root|--helper-build-receipt-sha256|--n04-helper-build-root|--n04-helper-build-receipt-sha256)
  (($#>=2)) || fail option-value; k=${1#--}; [[ -z ${v[$k]+x} ]] || fail duplicate-option; v[$k]=$2; shift 2 ;;
 *) fail unknown-option ;;
 esac
done
for k in execute generation build-root reviewed-root deadline-monotonic-ms reviewed-script-sha256 transport-sha256 fixture-sha256 audit-sha256 source-review-sha256 helper-build-root helper-build-receipt-sha256 n04-helper-build-root n04-helper-build-receipt-sha256; do
 [[ -n ${v[$k]-} ]] || fail missing-option
done
readonly N03_INDEPENDENT_REVIEW_CLEAR=1
((N03_INDEPENDENT_REVIEW_CLEAR==1)) || fail n03-review-pending
[[ $EUID == 0 && $OSTYPE == linux* && ${v[generation]} =~ ^[0-9a-f]{32}$ ]] || fail root-platform-generation
for k in reviewed-script-sha256 transport-sha256 fixture-sha256 audit-sha256 source-review-sha256 helper-build-receipt-sha256 n04-helper-build-receipt-sha256; do
 [[ ${v[$k]} =~ ^[0-9a-f]{64}$ ]] || fail digest-shape
done
[[ ${v[transport-sha256]} == 897465a00058ca73f75ed0c8b7e13fd84871cb65b74b111b677132d791aaf986 &&
   ${v[fixture-sha256]} == 84d921352e8e3cd209e5637f7deba7922c7846689b092c55c9803c8b7bca7e49 &&
   ${v[audit-sha256]} == 499dace9538a0c4d4292506759cd432b21e44722ffcf205f8e2a681fe563ec91 &&
   ${v[source-review-sha256]} == f585bba4d5e32cb049d8e4cc289881628b4af114184c654a1e1ea12f0b6d5ffd ]] || fail frozen-input-pins
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
REVIEWED_READER_SHA256 = 'dfd9d5c99a2cec150afe5ff791ba84e61064acd8c7c7f29ebf557ee14924c761'
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
absolute "${v[n04-helper-build-root]}"; [[ -d ${v[n04-helper-build-root]} && ! -L ${v[n04-helper-build-root]} ]] || fail n04-helper-build-root
[[ ! -e ${v[n04-helper-build-root]}/late-helper-publication-failure.json && ! -L ${v[n04-helper-build-root]}/late-helper-publication-failure.json && ! -e ${v[n04-helper-build-root]}/helper-build-receipt.pending.json && ! -L ${v[n04-helper-build-root]}/helper-build-receipt.pending.json ]] || fail n04-helper-pending
n04_helper_receipt=${v[n04-helper-build-root]}/helper-build-receipt.json
pin "$n04_helper_receipt" "${v[n04-helper-build-receipt-sha256]}" 1048576
copy_file "$n04_helper_receipt" "$work/n04-helper-build-receipt.json" "${v[n04-helper-build-receipt-sha256]}" 1048576 0600
bounded /usr/bin/jq -e 'def integer: type=="number" and floor==. and .>=0;
def digest: type=="string" and test("^[0-9a-f]{64}$");
keys==["authority","commands","exit","helper_bytes","helper_directories","helper_entry_sha256","helper_files","helper_nodes_sha256","helper_root","helper_tsv_sha256","native_execution","recipe_sha256","runtime_required","schema","sdk_required","sdk_sha256","source_pins"] and .schema=="issue779-n04-helper-build-handoff-v1" and .exit==0 and .authority==false and .native_execution==false and
.recipe_sha256=="88ff9817c34287f5a1454b4397106961c570f9073bbea120756ce5832ed28af3" and .sdk_required=="10.0.401" and .runtime_required=="10.0.12" and (.sdk_sha256|digest) and
.source_pins=={"Program.cs":"90277c9188d1b0c2dfbda9b9075c6640e87b99ad4a8dc21c18efc69b8f5e04c9","PossibleStopRegistration.cs":"0ed05a67f555c2b75c436ecb77e2b8f26d7aa66ab8cb3bafb5e92bb85c4df9d6","NativeRootCoordinator.csproj":"6df583dc8191ea8aa588e62d50280ec8a7e788b63a3570ccec7a26d0fbd51a87","packages.lock.json":"a29c6aa8cfb81874ff8bb78dc369d7416f28c9b8cc47e99592bfc019b20c41eb"} and
(.helper_files|integer) and .helper_files>0 and (.helper_directories|integer) and .helper_directories>0 and (.helper_files+.helper_directories)<=8192 and
(.helper_bytes|integer) and .helper_bytes<=1073741824 and (.helper_tsv_sha256|digest) and (.helper_nodes_sha256|digest) and (.helper_entry_sha256|digest) and
(.commands|type=="array" and length==3) and all(.commands[]; keys==["error","exit","forced_cleanup","group_absent","log","log_bytes","waited"] and .exit==0 and .waited==true and .group_absent==true and .forced_cleanup==false and .error==false and (.log_bytes|integer) and .log_bytes<=8388608) and
(.commands|map(.log))==["build-00.log","build-01.log","build-02.log"]' "$work/n04-helper-build-receipt.json" >/dev/null
bounded /usr/bin/jq -acS . "$work/n04-helper-build-receipt.json" >"$work/n04-helper-receipt-canonical.json"
bounded /usr/bin/cmp -- "$work/n04-helper-build-receipt.json" "$work/n04-helper-receipt-canonical.json"
for kind in source tool runtime helper n04helper; do
 roots[$kind]=${v[build-root]}/handoff/$kind
 [[ $kind != helper ]] || roots[$kind]=${v[helper-build-root]}/helper
 [[ $kind != n04helper ]] || roots[$kind]=${v[n04-helper-build-root]}/helper
 maps[$kind]=$work/$kind.tsv; nodes[$kind]=$work/$kind-nodes.json
 receipt_for_kind=$work/helper-build-receipt.json; [[ $kind != n04helper ]] || receipt_for_kind=$work/n04-helper-build-receipt.json
 if [[ $kind == helper || $kind == n04helper ]]; then
  map_hash[$kind]=$(bounded /usr/bin/jq -r .helper_tsv_sha256 "$receipt_for_kind")
  node_hash[$kind]=$(bounded /usr/bin/jq -r .helper_nodes_sha256 "$receipt_for_kind")
 else
 map_hash[$kind]=$(bounded /usr/bin/jq -r --arg name "$kind" '.artifacts[$name].tsv_sha256' "$work/build-receipt.json")
 node_hash[$kind]=$(bounded /usr/bin/jq -r --arg name "$kind" '.artifacts[$name].nodes_sha256' "$work/build-receipt.json")
 fi
 metadata_base=${v[build-root]}/handoff; metadata_name=$kind; [[ $kind != helper ]] || metadata_base=${v[helper-build-root]}
 [[ $kind != n04helper ]] || { metadata_base=${v[n04-helper-build-root]}; metadata_name=helper; }
 copy_file "$metadata_base/$metadata_name.tsv" "${maps[$kind]}" "${map_hash[$kind]}" 1048576 0600
 copy_file "$metadata_base/$metadata_name-nodes.json" "${nodes[$kind]}" "${node_hash[$kind]}" 4194304 0600
 bounded /usr/bin/jq -acS . "${nodes[$kind]}" >"$work/$kind-canonical.json"
 bounded /usr/bin/cmp -- "${nodes[$kind]}" "$work/$kind-canonical.json"
 bounded /usr/bin/jq -e --arg kind "$kind" --slurpfile receipt "$work/build-receipt.json" --slurpfile helper "$receipt_for_kind" '
  def rel: type=="string" and utf8bytelength<=4096 and (test("[\u0000-\u001f\u007f\\\\:]")|not) and
   (split("/")|all(.[]; length>0 and .!="." and .!=".." and .!=".git" and utf8bytelength<=255));
  . as $m | type=="object" and keys==["directories","files","root_name","schema"] and .schema=="issue779-build-node-inventory-v1" and .root_name==(if $kind=="helper" or $kind=="n04helper" then "tool" else $kind end) and
  (.files|type=="object") and (.directories|type=="object") and .directories["."]!=null and
  (.files|length)==(if $kind=="helper" or $kind=="n04helper" then $helper[0].helper_files else $receipt[0].artifacts[$kind].file_count end) and
  ((.files|length)+(.directories|length))==(if $kind=="helper" or $kind=="n04helper" then ($helper[0].helper_files+$helper[0].helper_directories) else $receipt[0].artifacts[$kind].node_count end) and
  all(.directories|to_entries[]; (.key=="." or (.key|rel)) and (.value|type=="object" and keys==["mode"] and .mode==(if $kind=="source" then "0700" else "0555" end))) and
  all(.files|to_entries[]; (.key|rel) and $m.directories[.key]==null and (.value|type=="object" and keys==["bytes","mode","sha256"] and
   (.sha256|type=="string" and test("^[0-9a-f]{64}$")) and (.bytes|type=="number" and floor==. and .>=0 and .<=268435456) and
   (if $kind=="source" then .mode=="0644" or .mode=="0755" else .mode=="0444" or .mode=="0555" end))) and
  ([.files[].bytes]|add)==(if $kind=="helper" or $kind=="n04helper" then $helper[0].helper_bytes else $receipt[0].artifacts[$kind].total_bytes end) and
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
for a in source tool runtime helper n04helper; do for b in source tool runtime helper n04helper; do
 [[ $a == "$b" ]] && continue
 [[ ${roots[$a]} != "${roots[$b]}" && ${roots[$a]} != "${roots[$b]}/"* ]] || fail helper-overlap
 done; done
bounded /usr/bin/jq -e --arg entry "$(bounded /usr/bin/jq -r .helper_entry_sha256 "$work/helper-build-receipt.json")" '.files["NativePeerBroker.dll"].sha256==$entry' "${nodes[helper]}" >/dev/null
bounded /usr/bin/jq -e --arg entry "$(bounded /usr/bin/jq -r .helper_entry_sha256 "$work/n04-helper-build-receipt.json")" ' .files["NativeRootCoordinator.dll"].sha256==$entry ' "${nodes[n04helper]}" >/dev/null
for kind in source tool runtime helper n04helper; do verify "$kind" "${roots[$kind]}" original original-first; done
for kind in source tool runtime helper n04helper; do
 bounded /usr/bin/cp --recursive --no-dereference --preserve=mode,timestamps -- "${roots[$kind]}" "$output/$kind"
 snapshot "$output/$kind" "$work/$kind-copied.txt"; validate "$kind" "$work/$kind-copied.txt" original
 bounded /usr/bin/find -P "$output/$kind" -exec /usr/bin/chown --no-dereference 0:0 -- '{}' +
 bounded /usr/bin/sync -f -- "$output/$kind"
 verify "$kind" "$output/$kind" root destination-final
 verify "$kind" "${roots[$kind]}" original original-final
 bounded /usr/bin/cmp -- "$work/$kind-original-first-before.txt" "$work/$kind-original-final-after.txt"
 metadata_base=${v[build-root]}/handoff; metadata_name=$kind; [[ $kind != helper ]] || metadata_base=${v[helper-build-root]}
 [[ $kind != n04helper ]] || { metadata_base=${v[n04-helper-build-root]}; metadata_name=helper; }
 pin "$metadata_base/$metadata_name.tsv" "${map_hash[$kind]}" 1048576
 pin "$metadata_base/$metadata_name-nodes.json" "${node_hash[$kind]}" 4194304
done
for spec in prepare-root-inputs-v2.sh:transport checkpoint-n01-n02-v5.sh:fixture prepare-os-audit-v2.py:audit source-review.json:source-review; do
 name=${spec%:*}; key=${spec#*:}; cap=131072; destination=$output/scripts/$name; mode=0444
 if [[ $key == source-review ]]; then cap=65536; destination=$work/source-review.json; mode=0600; fi
 trusted_pin "${v[reviewed-root]}/$name" "${v[$key-sha256]}" "$cap"
 copy_file "${v[reviewed-root]}/$name" "$destination" "${v[$key-sha256]}" "$cap" "$mode"
done
pin "$n04_helper_receipt" "${v[n04-helper-build-receipt-sha256]}" 1048576
[[ ! -e ${v[n04-helper-build-root]}/late-helper-publication-failure.json && ! -L ${v[n04-helper-build-root]}/late-helper-publication-failure.json ]] || fail late-n04-helper-publication
pin "$helper_receipt" "${v[helper-build-receipt-sha256]}" 1048576
[[ ! -e ${v[helper-build-root]}/late-helper-publication-failure.json && ! -L ${v[helper-build-root]}/late-helper-publication-failure.json ]] || fail late-helper-publication
pin "$receipt" "$receipt_hash" 1048576
[[ $(identity "$receipt") == "$receipt_before" ]] || fail original-receipt-changed
[[ ! -e ${v[build-root]}/receipts/late-publication-failure.json && ! -L ${v[build-root]}/receipts/late-publication-failure.json ]] || fail late-build-failure
for kind in source tool runtime helper n04helper; do
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
