#!/usr/bin/bash
# V2 batched preparation source only. Operator verifies this file BEFORE starting the interpreter.
set -euo pipefail
export PATH=/usr/bin:/usr/sbin LC_ALL=C LANG=C
umask 077
readonly SOURCE=fc59bb1254294d624f6779f928020edfe988dd5e
readonly PARENT=1794a99f9f9857e923fdb2cc8cc81fe0bd5c8c38
readonly SOURCE_PARENT=1794a99f9f9857e923fdb2cc8cc81fe0bd5c8c38
readonly SOURCE_MAP=c541eb54bce090149404e8a3c0c4036e684096f0170e669aed65453a69ad894a
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
mode|reviewed-script-sha256|generation|deadline-monotonic-ms|source-commit|source-root|source-map|source-map-sha256|source-nodes|source-nodes-sha256|tool-root|tool-map|tool-map-sha256|tool-nodes|tool-nodes-sha256|runtime-root|runtime-map|runtime-map-sha256|runtime-nodes|runtime-nodes-sha256|source-review|source-review-sha256|build-receipt|build-receipt-sha256|fixture|fixture-sha256|os-audit-script|os-audit-script-sha256|n15-parser-root|n15-parser-sha256|n15-observer-sha256|n15-retry-sha256|n15-finalizer-sha256|case-workspace|case-workspace-sha256|source-capture|source-capture-sha256) ;;
 *) fail unknown-option ;;
 esac
done
if [[ $mode == prepare-only ]]; then
 printf '%s\n' 'PREPARATION_ONLY:NO_INPUTS_VERIFIED:NO_TRANSPORT:NO_FIXTURE_EXECUTION'; exit 0
fi
readonly N15_INDEPENDENT_REVIEW_CLEAR=1
((N15_INDEPENDENT_REVIEW_CLEAR==1)) || fail n15-independent-review-pending
[[ $EUID == 0 && $OSTYPE == linux* ]] || fail trusted-linux-root-required
for key in reviewed-script-sha256 generation deadline-monotonic-ms source-commit source-root source-map source-map-sha256 source-nodes source-nodes-sha256 tool-root tool-map tool-map-sha256 tool-nodes tool-nodes-sha256 runtime-root runtime-map runtime-map-sha256 runtime-nodes runtime-nodes-sha256 source-review source-review-sha256 build-receipt build-receipt-sha256 fixture fixture-sha256 os-audit-script os-audit-script-sha256 n15-parser-root n15-parser-sha256 n15-observer-sha256 n15-retry-sha256 n15-finalizer-sha256 case-workspace case-workspace-sha256 source-capture source-capture-sha256; do
 [[ -n ${values[$key]-} ]] || fail missing-input
done
hash_syntax() { [[ $1 =~ ^[0-9a-f]{64}$ ]] || fail digest-syntax; }
for key in "${!values[@]}"; do [[ $key != *sha256 ]] || hash_syntax "${values[$key]}"; done
[[ ${values[generation]} =~ ^[0-9a-f]{32}$ && ${values[source-commit]} == fc59bb1254294d624f6779f928020edfe988dd5e ]] || fail source-generation-pin
[[ ${values[n15-parser-sha256]} == 5f0ead313fe2bc5f2109903fc362bfad56a37e7553c69aeddc733c45fd173b30 && ${values[n15-retry-sha256]} == 9ab6bb3f3b96a284485ba7fb8eac9aa49a2b4e0f0bed4756ec9a17d166cad3c6 && ${values[n15-finalizer-sha256]} =~ ^[0-9a-f]{64}$ && ${values[source-capture-sha256]} == 6e46183742801e864802ab8a7f3e64cd320139a500b7b215b17befe96616d2c1 ]] || fail n15-source-parser-pins
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
pin "${values[n15-parser-root]}/check_n15_records.py" "${values[n15-parser-sha256]}" 131072
pin "${values[n15-parser-root]}/observe_n15_pending_start.py" "${values[n15-observer-sha256]}" 131072
pin "${values[n15-parser-root]}/n15-guard-reactivation-retry.sh" "${values[n15-retry-sha256]}" 32768
pin "${values[n15-parser-root]}/finalize_n15_observation.py" "${values[n15-finalizer-sha256]}" 131072
pin "${values[case-workspace]}" "${values[case-workspace-sha256]}" 131072
bounded /usr/bin/jq -e --arg head "$SOURCE" --arg parent "$PARENT" --arg source_parent "$SOURCE_PARENT" '.schema=="issue779-csharp-common-source-capture-v4" and .validation_commit==$head and .previous_validation_commit==$source_parent and .source_count==2872 and .source_tree=="c9ed8adcc08d050dc70af6885d31171038a36f86" and .clean==true and .index_checksum_verified==true and .index_tree_blob_modes_equal==true and .no_alternates==true and .native_credit==false and .all16_final_integrated_native_verified==false and (.source.sha256|type=="object" and length==2872) and (.source.modes|type=="object" and length==2872)' "${values[source-capture]}" >/dev/null
bounded /usr/bin/python3 -B - "${values[build-receipt]}" <<'INTEGRATED_BUILD_RECEIPT'
import base64, hashlib, zlib
REVIEWED_READER_PACKED = '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'
REVIEWED_READER_LENGTH = 14103
REVIEWED_READER_SHA256 = 'f2af30ed63d42e67c56b5171bc01e849b6062c27eecb956ae70ce2566a274ff6'
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
copy_pinned "${values[fixture]}" "$output/reviewed/checkpoint-n15-pending-start-death.sh" "${values[fixture-sha256]}" 131072 0444
copy_pinned "${values[os-audit-script]}" "$output/reviewed/prepare-os-audit-v2.py" "${values[os-audit-script-sha256]}" 131072 0444
copy_pinned "${values[n15-parser-root]}/check_n15_records.py" "$output/reviewed/check_n15_records.py" "${values[n15-parser-sha256]}" 131072 0444
copy_pinned "${values[n15-parser-root]}/observe_n15_pending_start.py" "$output/reviewed/observe_n15_pending_start.py" "${values[n15-observer-sha256]}" 131072 0444
copy_pinned "${values[n15-parser-root]}/n15-guard-reactivation-retry.sh" "$output/reviewed/n15-guard-reactivation-retry.sh" "${values[n15-retry-sha256]}" 32768 0444
copy_pinned "${values[n15-parser-root]}/finalize_n15_observation.py" "$output/reviewed/finalize_n15_observation.py" "${values[n15-finalizer-sha256]}" 131072 0444
copy_pinned "${values[case-workspace]}" "$output/reviewed/prepare-case-workspace.py" "${values[case-workspace-sha256]}" 131072 0444
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
 bounded /usr/bin/jq -n --arg generation "${values[generation]}" --arg source "${values[source-commit]}" --arg root "$output" --arg script "${values[reviewed-script-sha256]}" --arg deadline "$hard_end" --arg fixture "${values[fixture-sha256]}" --arg observer "${values[n15-observer-sha256]}" --arg retry "${values[n15-retry-sha256]}" --arg finalizer "${values[n15-finalizer-sha256]}" --arg audit "${values[os-audit-script-sha256]}" '
 {schema:"issue779-root-input-transport-v1",generation:$generation,source_commit:$source,
 root:$root,script_sha256:$script,original_deadline_monotonic_ms:$deadline,
 fixture_sha256:$fixture,n15_observer_sha256:$observer,n15_retry_sha256:$retry,n15_finalizer_sha256:$finalizer,os_audit_script_sha256:$audit,
 complete_membership_rechecked:true,fixture_executed:false,os_audit_executed:false,native_acceptance_claim:false}
 ' >"$work/transport-receipt.json"
bounded /usr/bin/sync -f -- "$work/transport-receipt.json"
check; [[ $(bounded /usr/bin/stat -c '%d:%i:%u:%g' -- "$output") == "$root_pin" ]] || fail output-root-substitution
bounded /usr/bin/chmod 0711 -- "$output"
check
printf 'ROOT_TRANSPORT_COMPLETED:%s:NO_FIXTURE_EXECUTION:NO_NATIVE_ACCEPTANCE\n' "${values[generation]}"
check; succeeded=1
