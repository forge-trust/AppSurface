#!/usr/bin/bash
# Source preparation only. Trusted operator verifies/installs this script BEFORE Bash.
# No acquired code, managed entry, systemd operation, or authority is executed here.
set -euo pipefail
export PATH=/usr/bin:/usr/sbin LC_ALL=C LANG=C
umask 077
readonly SOURCE=fc59bb1254294d624f6779f928020edfe988dd5e
readonly PARENT=1794a99f9f9857e923fdb2cc8cc81fe0bd5c8c38
readonly SOURCE_MAP=1436ddaa0842f6ab1a8c398b2d5d335dfca83d7b6f82358b08f2b56e197dda1f
readonly FILE_CAP=268435456 TREE_CAP=1073741824 NODE_CAP=8192 RESERVE_MS=5000
declare -A v=() roots=() maps=() nodes=() map_hash=() node_hash=()
output=; work=; success=0
readonly N07_INTEGRATION_REVIEW_CLEAR=1
((N07_INTEGRATION_REVIEW_CLEAR==1)) || { printf "INBOUND_REJECTED:n07-integration-pending\n" >&2; exit 1; }
fail() { printf 'INBOUND_REJECTED:%s\n' "$1" >&2; exit 1; }
while (($#)); do
 case "$1" in
 --execute) [[ -z ${v[execute]+x} ]] || fail duplicate-option; v[execute]=1; shift ;;
 --generation|--build-root|--reviewed-root|--deadline-monotonic-ms|--reviewed-script-sha256|--transport-sha256|--fixture-sha256|--audit-sha256|--source-review-sha256|--helper-build-root|--helper-build-receipt-sha256|--n07-helper-build-root|--n07-helper-build-receipt-sha256|--n07-parser-sha256|--n07-live-parser-sha256)
  (($#>=2)) || fail option-value; k=${1#--}; [[ -z ${v[$k]+x} ]] || fail duplicate-option; v[$k]=$2; shift 2 ;;
 *) fail unknown-option ;;
 esac
done
for k in execute generation build-root reviewed-root deadline-monotonic-ms reviewed-script-sha256 transport-sha256 fixture-sha256 audit-sha256 source-review-sha256 helper-build-root helper-build-receipt-sha256 n07-helper-build-root n07-helper-build-receipt-sha256 n07-parser-sha256 n07-live-parser-sha256; do
 [[ -n ${v[$k]-} ]] || fail missing-option
done
readonly N03_INDEPENDENT_REVIEW_CLEAR=1
((N03_INDEPENDENT_REVIEW_CLEAR==1)) || fail n03-review-pending
[[ $EUID == 0 && $OSTYPE == linux* && ${v[generation]} =~ ^[0-9a-f]{32}$ ]] || fail root-platform-generation
for k in reviewed-script-sha256 transport-sha256 fixture-sha256 audit-sha256 source-review-sha256 helper-build-receipt-sha256 n07-helper-build-receipt-sha256 n07-parser-sha256 n07-live-parser-sha256; do
 [[ ${v[$k]} =~ ^[0-9a-f]{64}$ ]] || fail digest-shape
done
[[ ${v[transport-sha256]} == 87751e90fdcbea8073bdec13239989fb594f6e18bf3e42709eaaa8663c037b50 &&
   ${v[fixture-sha256]} == d81cfee0e36762c902156355c789c0d2260ed0d10634773402172940edc8af6e &&
   ${v[audit-sha256]} == 6d0691864264040db5097560a34a75033df7d22c81662b2b2cd52801fa673a08 &&
   ${v[source-review-sha256]} == de012d44beeee8531c8b0e19de6bf8fc61fdeb517e82d4d804281bb868c8ba5d ]] || fail frozen-input-pins
[[ ${v[n07-parser-sha256]} == 96d01d088300aeffb5fa7eab7387c3b4be319d249e796edcb189aceb441548e7 ]] || fail n07-parser-pin
[[ ${v[n07-live-parser-sha256]} == ff957a62af596c9ae2278685a756493c709f3906bf9194d13a2946109cb792b1 ]] || fail n07-parser-pin
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
REVIEWED_READER_SHA256 = 'eb83f746bccdc725b76509c5bbabafff647e2a0bd2dd40061aa146187c649c7e'
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
absolute "${v[n07-helper-build-root]}"; [[ -d ${v[n07-helper-build-root]} && ! -L ${v[n07-helper-build-root]} ]] || fail n07-helper-build-root
[[ ! -e ${v[n07-helper-build-root]}/late-helper-publication-failure.json && ! -L ${v[n07-helper-build-root]}/late-helper-publication-failure.json && ! -e ${v[n07-helper-build-root]}/helper-build-receipt.pending.json && ! -L ${v[n07-helper-build-root]}/helper-build-receipt.pending.json ]] || fail n07-helper-pending
n07_helper_receipt=${v[n07-helper-build-root]}/helper-build-receipt.json
pin "$n07_helper_receipt" "${v[n07-helper-build-receipt-sha256]}" 1048576
copy_file "$n07_helper_receipt" "$work/n07-helper-build-receipt.json" "${v[n07-helper-build-receipt-sha256]}" 1048576 0600
bounded /usr/bin/jq -e 'def integer: type=="number" and floor==. and .>=0;
def digest: type=="string" and test("^[0-9a-f]{64}$");
keys==["authority","commands","exit","helper_bytes","helper_directories","helper_entry_sha256","helper_files","helper_nodes_sha256","helper_root","helper_tsv_sha256","native_execution","private_diagnostics_sha256","recipe_sha256","runtime_required","schema","sdk_required","sdk_sha256","source_pins"] and .schema=="issue779-n07-helper-build-handoff-v1" and .exit==0 and .authority==false and .native_execution==false and
.recipe_sha256=="f5f2e4696568d207e6e340d80c14a78410c21ae0427a4bde9787aa091586d4bf" and .sdk_required=="10.0.401" and .runtime_required=="10.0.12" and (.sdk_sha256|digest) and (.private_diagnostics_sha256|digest) and
.source_pins=={"Program.cs":"20ddf7f54bf335ec49ab0d221b3b49bb2695f1c61d959786a585806ace332d9d","PossibleStopRegistration.cs":"3c1e57f7030c2e2a1a54bfa2853744248bf3bad4cc71af5b94ca912a87d0d3af","N07CoordinatorData.cs":"f85d30a95b2bbe66ae8b21ff66ea812aee1c0e801fa03f49b8246d91a202d91a","NativeRootCoordinator.csproj":"9ea64016a8932db2b62910d08a579236a8e3a2b20e2cd438a0b27bc92f5269d1","packages.lock.json":"a29c6aa8cfb81874ff8bb78dc369d7416f28c9b8cc47e99592bfc019b20c41eb"} and
(.helper_files|integer) and .helper_files>0 and (.helper_directories|integer) and .helper_directories>0 and (.helper_files+.helper_directories)<=8192 and
(.helper_bytes|integer) and .helper_bytes<=1073741824 and (.helper_tsv_sha256|digest) and (.helper_nodes_sha256|digest) and (.helper_entry_sha256|digest) and
(.commands|type=="array" and length==3)' "$work/n07-helper-build-receipt.json" >/dev/null
bounded /usr/bin/jq -acS . "$work/n07-helper-build-receipt.json" >"$work/n07-helper-receipt-canonical.json"
bounded /usr/bin/cmp -- "$work/n07-helper-build-receipt.json" "$work/n07-helper-receipt-canonical.json"
n07_helper_diagnostics=${v[n07-helper-build-root]}/helper-build-private-diagnostics.json
n07_helper_diagnostics_sha=$(bounded /usr/bin/jq -r .private_diagnostics_sha256 "$work/n07-helper-build-receipt.json")
pin "$n07_helper_diagnostics" "$n07_helper_diagnostics_sha" 1048576
copy_file "$n07_helper_diagnostics" "$work/n07-helper-build-private-diagnostics.json" "$n07_helper_diagnostics_sha" 1048576 0600
bounded /usr/bin/python3.12 -I -S -B "$validator_path" N07 "$work/n07-helper-build-receipt.json" "$work/n07-helper-build-private-diagnostics.json" "f5f2e4696568d207e6e340d80c14a78410c21ae0427a4bde9787aa091586d4bf" >/dev/null
for kind in source tool runtime helper n07helper; do
 roots[$kind]=${v[build-root]}/handoff/$kind
 [[ $kind != helper ]] || roots[$kind]=${v[helper-build-root]}/helper
 [[ $kind != n07helper ]] || roots[$kind]=${v[n07-helper-build-root]}/helper
 maps[$kind]=$work/$kind.tsv; nodes[$kind]=$work/$kind-nodes.json
 receipt_for_kind=$work/helper-build-receipt.json; [[ $kind != n07helper ]] || receipt_for_kind=$work/n07-helper-build-receipt.json
 if [[ $kind == helper || $kind == n07helper ]]; then
  map_hash[$kind]=$(bounded /usr/bin/jq -r .helper_tsv_sha256 "$receipt_for_kind")
  node_hash[$kind]=$(bounded /usr/bin/jq -r .helper_nodes_sha256 "$receipt_for_kind")
 else
 map_hash[$kind]=$(bounded /usr/bin/jq -r --arg name "$kind" '.artifacts[$name].tsv_sha256' "$work/build-receipt.json")
 node_hash[$kind]=$(bounded /usr/bin/jq -r --arg name "$kind" '.artifacts[$name].nodes_sha256' "$work/build-receipt.json")
 fi
 metadata_base=${v[build-root]}/handoff; metadata_name=$kind; [[ $kind != helper ]] || metadata_base=${v[helper-build-root]}
 [[ $kind != n07helper ]] || { metadata_base=${v[n07-helper-build-root]}; metadata_name=helper; }
 copy_file "$metadata_base/$metadata_name.tsv" "${maps[$kind]}" "${map_hash[$kind]}" 1048576 0600
 copy_file "$metadata_base/$metadata_name-nodes.json" "${nodes[$kind]}" "${node_hash[$kind]}" 4194304 0600
 bounded /usr/bin/jq -acS . "${nodes[$kind]}" >"$work/$kind-canonical.json"
 bounded /usr/bin/cmp -- "${nodes[$kind]}" "$work/$kind-canonical.json"
 bounded /usr/bin/jq -e --arg kind "$kind" --slurpfile receipt "$work/build-receipt.json" --slurpfile helper "$receipt_for_kind" '
  def rel: type=="string" and utf8bytelength<=4096 and (test("[\u0000-\u001f\u007f\\\\:]")|not) and
   (split("/")|all(.[]; length>0 and .!="." and .!=".." and .!=".git" and utf8bytelength<=255));
  . as $m | type=="object" and keys==["directories","files","root_name","schema"] and .schema=="issue779-build-node-inventory-v1" and .root_name==(if $kind=="helper" or $kind=="n07helper" then "tool" else $kind end) and
  (.files|type=="object") and (.directories|type=="object") and .directories["."]!=null and
  (.files|length)==(if $kind=="helper" or $kind=="n07helper" then $helper[0].helper_files else $receipt[0].artifacts[$kind].file_count end) and
  ((.files|length)+(.directories|length))==(if $kind=="helper" or $kind=="n07helper" then ($helper[0].helper_files+$helper[0].helper_directories) else $receipt[0].artifacts[$kind].node_count end) and
  all(.directories|to_entries[]; (.key=="." or (.key|rel)) and (.value|type=="object" and keys==["mode"] and .mode==(if $kind=="source" then "0700" else "0555" end))) and
  all(.files|to_entries[]; (.key|rel) and $m.directories[.key]==null and (.value|type=="object" and keys==["bytes","mode","sha256"] and
   (.sha256|type=="string" and test("^[0-9a-f]{64}$")) and (.bytes|type=="number" and floor==. and .>=0 and .<=268435456) and
   (if $kind=="source" then .mode=="0644" or .mode=="0755" else .mode=="0444" or .mode=="0555" end))) and
  ([.files[].bytes]|add)==(if $kind=="helper" or $kind=="n07helper" then $helper[0].helper_bytes else $receipt[0].artifacts[$kind].total_bytes end) and
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
for a in source tool runtime helper n07helper; do for b in source tool runtime helper n07helper; do
 [[ $a == "$b" ]] && continue
 [[ ${roots[$a]} != "${roots[$b]}" && ${roots[$a]} != "${roots[$b]}/"* ]] || fail helper-overlap
 done; done
bounded /usr/bin/jq -e --arg entry "$(bounded /usr/bin/jq -r .helper_entry_sha256 "$work/helper-build-receipt.json")" '.files["NativePeerBroker.dll"].sha256==$entry' "${nodes[helper]}" >/dev/null
bounded /usr/bin/jq -e --arg entry "$(bounded /usr/bin/jq -r .helper_entry_sha256 "$work/n07-helper-build-receipt.json")" ' .files["NativeRootCoordinator.dll"].sha256==$entry ' "${nodes[n07helper]}" >/dev/null
for kind in source tool runtime helper n07helper; do verify "$kind" "${roots[$kind]}" original original-first; done
for kind in source tool runtime helper n07helper; do
 bounded /usr/bin/cp --recursive --no-dereference --preserve=mode,timestamps -- "${roots[$kind]}" "$output/$kind"
 snapshot "$output/$kind" "$work/$kind-copied.txt"; validate "$kind" "$work/$kind-copied.txt" original
 bounded /usr/bin/find -P "$output/$kind" -exec /usr/bin/chown --no-dereference 0:0 -- '{}' +
 bounded /usr/bin/sync -f -- "$output/$kind"
 verify "$kind" "$output/$kind" root destination-final
 verify "$kind" "${roots[$kind]}" original original-final
 bounded /usr/bin/cmp -- "$work/$kind-original-first-before.txt" "$work/$kind-original-final-after.txt"
 metadata_base=${v[build-root]}/handoff; metadata_name=$kind; [[ $kind != helper ]] || metadata_base=${v[helper-build-root]}
 [[ $kind != n07helper ]] || { metadata_base=${v[n07-helper-build-root]}; metadata_name=helper; }
 pin "$metadata_base/$metadata_name.tsv" "${map_hash[$kind]}" 1048576
 pin "$metadata_base/$metadata_name-nodes.json" "${node_hash[$kind]}" 4194304
done
for spec in prepare-root-inputs-v2.sh:transport checkpoint-n01-n02-v5.sh:fixture prepare-os-audit-v2.py:audit source-review.json:source-review; do
 name=${spec%:*}; key=${spec#*:}; cap=131072; destination=$output/scripts/$name; mode=0444
 if [[ $key == source-review ]]; then cap=65536; destination=$work/source-review.json; mode=0600; fi
 trusted_pin "${v[reviewed-root]}/$name" "${v[$key-sha256]}" "$cap"
 copy_file "${v[reviewed-root]}/$name" "$destination" "${v[$key-sha256]}" "$cap" "$mode"
done
for spec in n07-record-data.py:n07-parser n07-helper-live.py:n07-live-parser; do
 name=${spec%:*}; key=${spec#*:}
 copy_file "${v[reviewed-root]}/$name" "$output/scripts/$name" "${v[$key-sha256]}" 65536 0444
 done
pin "$n07_helper_receipt" "${v[n07-helper-build-receipt-sha256]}" 1048576
[[ ! -e ${v[n07-helper-build-root]}/late-helper-publication-failure.json && ! -L ${v[n07-helper-build-root]}/late-helper-publication-failure.json ]] || fail late-n07-helper-publication
pin "$helper_receipt" "${v[helper-build-receipt-sha256]}" 1048576
[[ ! -e ${v[helper-build-root]}/late-helper-publication-failure.json && ! -L ${v[helper-build-root]}/late-helper-publication-failure.json ]] || fail late-helper-publication
pin "$receipt" "$receipt_hash" 1048576
[[ $(identity "$receipt") == "$receipt_before" ]] || fail original-receipt-changed
[[ ! -e ${v[build-root]}/receipts/late-publication-failure.json && ! -L ${v[build-root]}/receipts/late-publication-failure.json ]] || fail late-build-failure
for kind in source tool runtime helper n07helper; do
 snapshot "$output/$kind" "$work/$kind-last.txt"
 bounded /usr/bin/cmp -- "$work/$kind-destination-final-after.txt" "$work/$kind-last.txt"
done
trusted_pin "${BASH_SOURCE[0]}" "${v[reviewed-script-sha256]}" 131072
bounded /usr/bin/jq -ncS --arg g "${v[generation]}" --arg source "$SOURCE" --argjson deadline "$hard_end" --arg receipt "$receipt_hash" '
 {schema:"issue779-native-inbound-acquisition-v1",generation:$g,source_commit:$source,build_receipt_sha256:$receipt,
 original_deadline_monotonic_ms:$deadline,source_count:0,acquired_code_executed:false,native_acceptance_claim:false}
 ' >"$work/acquisition-receipt.json"
bounded /usr/bin/sync -f -- "$work/acquisition-receipt.json"
[[ $(bounded /usr/bin/stat -c '%d:%i:%u:%g:%a' -- "$output") == "$outer_pin" ]] || fail final-outer-identity
check; printf '%s\n' 'INBOUND_ACQUIRED:DATA_ONLY:ORIGINAL_TRANSPORT_DEADLINE_UNCHANGED'; check; success=1
