#!/usr/bin/bash
# V2 batched preparation source only. Operator verifies this file BEFORE starting the interpreter.
set -euo pipefail
export PATH=/usr/bin:/usr/sbin LC_ALL=C LANG=C
umask 077
readonly SOURCE=957da1f23ff743778fa39e3e559511ccdb281345
readonly PARENT=fc59bb1254294d624f6779f928020edfe988dd5e
readonly SOURCE_PARENT=fc59bb1254294d624f6779f928020edfe988dd5e
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
[[ ${values[generation]} =~ ^[0-9a-f]{32}$ && ${values[source-commit]} == 957da1f23ff743778fa39e3e559511ccdb281345 ]] || fail source-generation-pin
[[ ${values[n15-parser-sha256]} == 52fdaf3a770c1e22ae8758253c3d43deb330b19c30f513262e734d60b376dd3d && ${values[n15-retry-sha256]} == 9ab6bb3f3b96a284485ba7fb8eac9aa49a2b4e0f0bed4756ec9a17d166cad3c6 && ${values[n15-finalizer-sha256]} =~ ^[0-9a-f]{64}$ && ${values[source-capture-sha256]} == 6d723d1d944485b785e400849db941c3e869065371e2978806abc6b3dec7f0c4 ]] || fail n15-source-parser-pins
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
bounded /usr/bin/jq -e --arg head "$SOURCE" --arg parent "$PARENT" --arg source_parent "$SOURCE_PARENT" '.schema=="issue779-csharp-common-source-capture-v4" and .validation_commit==$head and .previous_validation_commit==$source_parent and .source_count==2872 and .source_tree=="51a3f946d42f4141af1cc8d297161dd362b79fd0" and .clean==true and .index_checksum_verified==true and .index_tree_blob_modes_equal==true and .no_alternates==true and .native_credit==false and .all16_final_integrated_native_verified==false and (.source.sha256|type=="object" and length==2872) and (.source.modes|type=="object" and length==2872)' "${values[source-capture]}" >/dev/null
bounded /usr/bin/python3 -B - "${values[build-receipt]}" <<'INTEGRATED_BUILD_RECEIPT'
import base64, hashlib, zlib
REVIEWED_READER_PACKED = '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'
REVIEWED_READER_LENGTH = 14103
REVIEWED_READER_SHA256 = '36361364788645b72086ba49fb4097875bd806e30770285d4d0edc050b85760b'
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
