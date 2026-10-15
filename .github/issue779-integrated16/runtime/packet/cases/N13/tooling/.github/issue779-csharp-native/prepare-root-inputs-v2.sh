#!/usr/bin/bash
# V2 batched preparation source only. Operator verifies this file BEFORE starting the interpreter.
set -euo pipefail
export PATH=/usr/bin:/usr/sbin LC_ALL=C LANG=C
umask 077
readonly SOURCE=5b4ed381badb91de47d793715d05b6ae7737e39e
readonly PARENT=914046e1e998ca89a5f0f5be39a90bbfb88a431d
readonly SOURCE_MAP=2c27553e3441cfc7e1c8b53cac0da15b6cff4a6c8c5c8d6dae01e0ba116351ea
readonly SOURCE_PROJECTION_MAP=758a0f769ca67ce6f1fda2eea3569a373efafb192e2d2c2cce2224809b352a7d
readonly CAPTURE_SHA=4ad23a2fdeccf9abbcd06724b9c97e3a4c1751ecb45e8321cc35bd79c036d540
readonly COUNT=2872
readonly PREPARATION_SHA=4c0c34dedf5f131115c113ac49a548f8257d478ba81cb85c919403690bac007b
readonly N13_FIXTURE_SHA=cd9e37d4f64a23471ea6c64f6286bcc36657c9fcdf7731678bddc108a986aafc
readonly N14_FIXTURE_SHA=d82860a2c21ea1e5c80d6a0d34db1e5fecff03dbbde4bad08350ddcab9f450a2
readonly COMMON_FIXTURE_SHA=f5d860405ecd8fe2e17285db2aac33200c861e74f97982beb3ec9cb356e7a406
readonly PARSER_SHA=352cc73606edc4c7a3af2eb96ca8238e40caa8d4f920c4500de627ee69121ffd
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
 mode|reviewed-script-sha256|generation|deadline-monotonic-ms|case|source-commit|source-root|source-map|source-map-sha256|source-nodes|source-nodes-sha256|tool-root|tool-map|tool-map-sha256|tool-nodes|tool-nodes-sha256|runtime-root|runtime-map|runtime-map-sha256|runtime-nodes|runtime-nodes-sha256|build-receipt|build-receipt-sha256|fixture|fixture-sha256|fixture-common|fixture-common-sha256|fixture-parser|fixture-parser-sha256|fixture-preparation|fixture-preparation-sha256|source-capture|source-capture-sha256) ;;
 *) fail unknown-option ;;
 esac
done
if [[ $mode == prepare-only ]]; then
 printf '%s\n' 'PREPARATION_ONLY:NO_INPUTS_VERIFIED:NO_TRANSPORT:NO_FIXTURE_EXECUTION'; exit 0
fi
readonly SOURCE_REVIEW_CLEAR=1 FIXTURE_REVIEW_CLEAR=1 ROOT_TRANSPORT_REVIEW_CLEAR=1 NATIVE_PROCEDURE_REVIEW_CLEAR=1
((SOURCE_REVIEW_CLEAR==1 && FIXTURE_REVIEW_CLEAR==1 && ROOT_TRANSPORT_REVIEW_CLEAR==1 && NATIVE_PROCEDURE_REVIEW_CLEAR==1)) || fail independent-review-pending
[[ $EUID == 0 && $OSTYPE == linux* ]] || fail trusted-linux-root-required
for key in reviewed-script-sha256 generation deadline-monotonic-ms case source-commit source-root source-map source-map-sha256 source-nodes source-nodes-sha256 tool-root tool-map tool-map-sha256 tool-nodes tool-nodes-sha256 runtime-root runtime-map runtime-map-sha256 runtime-nodes runtime-nodes-sha256 build-receipt build-receipt-sha256 fixture fixture-sha256 fixture-common fixture-common-sha256 fixture-parser fixture-parser-sha256 fixture-preparation fixture-preparation-sha256 source-capture source-capture-sha256; do
 [[ -n ${values[$key]-} ]] || fail missing-input
done
hash_syntax() { [[ $1 =~ ^[0-9a-f]{64}$ ]] || fail digest-syntax; }
for key in "${!values[@]}"; do [[ $key != *sha256 ]] || hash_syntax "${values[$key]}"; done
[[ ${values[generation]} =~ ^[0-9a-f]{32}$ && ( ${values[case]} == N13 || ${values[case]} == N14 ) ]] || fail case-generation-shape
[[ ${values[source-commit]} == "$SOURCE" && ${values[source-capture-sha256]} == "$CAPTURE_SHA" &&
   ${values[fixture-common-sha256]} == "$COMMON_FIXTURE_SHA" && ${values[fixture-parser-sha256]} == "$PARSER_SHA" &&
   ${values[fixture-preparation-sha256]} == "$PREPARATION_SHA" ]] || fail source-fixture-pins
if [[ ${values[case]} == N13 ]]; then
 expected_fixture_sha=$N13_FIXTURE_SHA; expected_fixture_name=checkpoint-n13-owner-killed.sh
else
 expected_fixture_sha=$N14_FIXTURE_SHA; expected_fixture_name=checkpoint-n14-owner-stopped.sh
fi
[[ ${values[fixture-sha256]} == "$expected_fixture_sha" && ${values[fixture]} == */$expected_fixture_name ]] || fail case-fixture-pin
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
pin "${values[build-receipt]}" "${values[build-receipt-sha256]}" "$CONTROL_CAP"
pin "${values[source-capture]}" "$CAPTURE_SHA" 2097152
pin "${values[fixture]}" "$expected_fixture_sha" 131072
pin "${values[fixture-common]}" "$COMMON_FIXTURE_SHA" 131072
pin "${values[fixture-parser]}" "$PARSER_SHA" 131072
pin "${values[fixture-preparation]}" "$PREPARATION_SHA" 131072
capture_query='
 . as $r |
 def digest: type=="string" and test("^[0-9a-f]{64}$");
 $r.schema=="issue779-csharp-common-source-capture-v4" and
 $r.validation_commit==$h and $r.previous_validation_commit==$p and $r.source_count==$n and $r.source_tree==$t and
 $r.clean==true and $r.index_checksum_verified==true and $r.index_tree_blob_modes_equal==true and $r.no_alternates==true and
 $r.native_credit==false and $r.all16_final_integrated_native_verified==false and
 ($r.source|type=="object" and keys==["modes","sha256"]) and
 ($r.source.sha256|type=="object" and length==$n) and ($r.source.modes|type=="object" and length==$n) and
 ($r.source.sha256|keys)==($r.source.modes|keys) and
 all($r.source.sha256|to_entries[]; (.value|digest) and ($r.source.modes[.key]=="0644" or $r.source.modes[.key]=="0755"))
'
bounded /usr/bin/jq -e --arg h "$SOURCE" --arg p "$PARENT" --arg t "3bafd0fcd5321f1b29323f46df94b77eb48fa859" --argjson n "$COUNT" "$capture_query" "${values[source-capture]}" >/dev/null
bounded /usr/bin/jq -e --arg head "$SOURCE" --arg parent "$PARENT" '
 .schema=="issue779-owner-death-fixture-v6-source-preparation-v1" and
 .source_capture_head==$head and .source_capture_parent==$parent and .source_capture_count==2872 and
 .review_clear==false and .native_credit==false and .native_execution==false and
 .duration_policy_usec=={outer_original_request_seconds:240,owner_runtime:120000000,stop:5000000,n14_owner_bound_seconds:125} and
 .files["checkpoint-n13-owner-killed.sh"].sha256=="cd9e37d4f64a23471ea6c64f6286bcc36657c9fcdf7731678bddc108a986aafc" and
 .files["checkpoint-n14-owner-stopped.sh"].sha256=="d82860a2c21ea1e5c80d6a0d34db1e5fecff03dbbde4bad08350ddcab9f450a2" and
 .files["checkpoint-owner-death-common.sh"].sha256=="f5d860405ecd8fe2e17285db2aac33200c861e74f97982beb3ec9cb356e7a406" and
 .files["check_owner_death_record.py"].sha256=="352cc73606edc4c7a3af2eb96ca8238e40caa8d4f920c4500de627ee69121ffd"
' "${values[fixture-preparation]}" >/dev/null
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
bounded /usr/bin/python3 -B - "$work/build-receipt.json" <<'INTEGRATED_BUILD_RECEIPT'
import base64, hashlib, zlib
REVIEWED_READER_PACKED = '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'
REVIEWED_READER_LENGTH = 14103
REVIEWED_READER_SHA256 = '00169c60ba41644c79c40a4ec3401d530db39a2eb12dcfe29e68741f8762abab'
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
for kind in source tool runtime; do
 map_hash[$kind]=$(bounded /usr/bin/jq -r --arg name "$kind" '.artifacts[$name].tsv_sha256' "$work/build-receipt.json")
 node_hash[$kind]=$(bounded /usr/bin/jq -r --arg name "$kind" '.artifacts[$name].nodes_sha256' "$work/build-receipt.json")
 [[ ${map_hash[$kind]} == "${map_shas[$kind]}" && ${node_hash[$kind]} == "${node_shas[$kind]}" ]] || fail build-manifest-pin-mismatch
 copy_pinned "${maps[$kind]}" "$work/$kind.tsv" "${map_shas[$kind]}" "$MAP_CAP" 0600
 copy_pinned "${nodes[$kind]}" "$work/$kind-nodes.json" "${node_shas[$kind]}" "$NODES_CAP" 0600
 maps[$kind]=$work/$kind.tsv; nodes[$kind]=$work/$kind-nodes.json
 bounded /usr/bin/jq -acS . "${nodes[$kind]}" >"$work/$kind-canonical.json"
 bounded /usr/bin/cmp -- "${nodes[$kind]}" "$work/$kind-canonical.json"
 bounded /usr/bin/jq -e --arg kind "$kind" --slurpfile receipt "$work/build-receipt.json" '
  def rel: type=="string" and utf8bytelength<=4096 and (test("[\u0000-\u001f\u007f\\\\:]")|not) and
   (split("/")|all(.[]; length>0 and .!="." and .!=".." and .!=".git" and utf8bytelength<=255));
  def hash: type=="string" and test("^[0-9a-f]{64}$");
  def mode: type=="string" and test("^0[0-7]{3}$");
  . as $m | type=="object" and keys==["directories","files","root_name","schema"] and
  .schema=="issue779-build-node-inventory-v1" and .root_name==$kind and
  (.files|type=="object" and length>0) and (.directories|type=="object" and .["."]!=null) and
  ((.files|length)+(.directories|length))==$receipt[0].artifacts[$kind].node_count and
  ((.files|length)+(.directories|length))<=8192 and
  (.files|length)==$receipt[0].artifacts[$kind].file_count and
  all(.directories|to_entries[]; (.key=="." or (.key|rel)) and
   (.value|type=="object" and keys==["mode"] and (.mode|mode) and
    .mode==(if $kind=="source" then "0700" else "0555" end))) and
  all(.files|to_entries[]; (.key|rel) and $m.directories[.key]==null and
   (.value|type=="object" and keys==["bytes","mode","sha256"] and
    (.sha256|hash) and (.mode|mode) and
    (.bytes|type=="number" and floor==. and .>=0 and .<=268435456) and
    (if $kind=="source" then .mode=="0644" or .mode=="0755"
     else .mode=="0444" or .mode=="0555" end))) and
  ([.files[].bytes]|add)==$receipt[0].artifacts[$kind].total_bytes and
  (if $kind=="source" then (.files|length)==2872 else true end)
 ' "${nodes[$kind]}" >/dev/null
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
copy_pinned "${values[fixture]}" "$output/reviewed/$expected_fixture_name" "$expected_fixture_sha" 131072 0444
copy_pinned "${values[fixture-common]}" "$output/reviewed/checkpoint-owner-death-common.sh" "$COMMON_FIXTURE_SHA" 131072 0444
copy_pinned "${values[fixture-parser]}" "$output/reviewed/check_owner_death_record.py" "$PARSER_SHA" 131072 0444
copy_pinned "${values[fixture-preparation]}" "$work/fixture-preparation.json" "$PREPARATION_SHA" 131072 0600
copy_pinned "${values[source-capture]}" "$work/source-capture.json" "$CAPTURE_SHA" 2097152 0600
bounded /usr/bin/chmod 0555 -- "$output/reviewed"
phase=final
[[ -d $helper && ! -L $helper ]] || fail audit-helper-final-kind
[[ $(identity "$helper") == "$helper_identity" ]] || fail audit-helper-final-identity
[[ $(bounded /usr/bin/stat -c '%u:%g:%a' -- "$helper") == 0:0:555 ]] || fail audit-helper-final-custody
[[ -z $(bounded /usr/bin/find -P "$helper" -mindepth 1 -maxdepth 1 -print -quit) ]] || fail audit-helper-final-not-empty
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
check
bounded /usr/bin/jq -n --arg generation "${values[generation]}" --arg case "${values[case]}" --arg source "${values[source-commit]}" --arg parent "$PARENT" --arg map "$SOURCE_MAP" --arg root "$output" --arg script "${values[reviewed-script-sha256]}" --arg deadline "$hard_end" --arg receipt "${values[build-receipt-sha256]}" --arg capture "$CAPTURE_SHA" --arg fixture "$expected_fixture_sha" --arg common "$COMMON_FIXTURE_SHA" --arg parser "$PARSER_SHA" '
 {schema:"issue779-owner-death-root-input-transport-v1",generation:$generation,case:$case,selected_variant:$case,
 source_commit:$source,source_parent:$parent,source_count:2872,source_map_sha256:$map,build_receipt_sha256:$receipt,
 root:$root,script_sha256:$script,source_capture_sha256:$capture,original_deadline_monotonic_ms:$deadline,
 fixture_sha256:$fixture,fixture_common_sha256:$common,fixture_parser_sha256:$parser,
 duration_policy_seconds:{original_request:240,owner_runtime:120,timeout_stop:5,n14_owner_bound:125},
 complete_membership_rechecked:true,fixture_executed:false,review_clear:false,
 source_review_clear:false,fixture_review_clear:false,root_transport_review_clear:false,
 native_execution:false,native_acceptance_claim:false}
 ' >"$work/transport-receipt.json"
bounded /usr/bin/sync -f -- "$work/transport-receipt.json"
check; [[ $(bounded /usr/bin/stat -c '%d:%i:%u:%g' -- "$output") == "$root_pin" ]] || fail output-root-substitution
bounded /usr/bin/chmod 0711 -- "$output"
check
printf 'ROOT_TRANSPORT_COMPLETED:%s:NO_FIXTURE_EXECUTION:NO_NATIVE_ACCEPTANCE\n' "${values[generation]}"
check; succeeded=1
