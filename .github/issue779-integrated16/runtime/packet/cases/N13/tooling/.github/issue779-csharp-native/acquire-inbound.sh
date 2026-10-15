#!/usr/bin/bash
# Fixed N13/N14 data acquisition. Never executes acquired code or grants native authority.
set -euo pipefail
export PATH=/usr/bin:/usr/sbin LC_ALL=C LANG=C
umask 077
readonly SOURCE=914046e1e998ca89a5f0f5be39a90bbfb88a431d
readonly PARENT=71d004d6cf5a791ddbb8e73e3c538248985a8505
readonly SOURCE_MAP=85d264688e73c14ea5e4552a4e7aed1f8b1c3311a3f358dc89818bef0ed87099
readonly CANONICAL_MAP=a61f03468ca7fff6ef363a510c7945f3927c966fa3b62291e4c34c3c50a23c98
readonly CAPTURE=b070e3a38e726c698bc2608cd86987a204ceaf164bc12dff53d7d3d196a1c953
readonly COUNT=2872
readonly COMMON=e844c36efe5c0ccdaeebd675a1c7f6a82879787de88175d7883a279fa69f0811
readonly PARSER=276df6fdd6cb9f8e623e9b09c62362d75d2b31adc07a6b2ca05e6a8c8e6ad4d1
readonly PREPARATION=b57b3b52970cd2a0d841ff0429c6bfb602d93b8d61711d1329eb87f51afede50
readonly N13=cd9e37d4f64a23471ea6c64f6286bcc36657c9fcdf7731678bddc108a986aafc
readonly N14=d82860a2c21ea1e5c80d6a0d34db1e5fecff03dbbde4bad08350ddcab9f450a2
readonly RESERVE_MS=5000 FILE_CAP=268435456 TREE_CAP=1073741824 NODE_CAP=8192
fail(){ printf 'INBOUND_REJECTED:%s\n' "$1" >&2; exit 1; }
declare -A v=() roots=() map_hash=() node_hash=()
while (($#)); do
 case "$1" in
  --execute) [[ -z ${v[execute]+x} ]] || fail duplicate-option; v[execute]=1; shift ;;
  --case|--generation|--build-root|--reviewed-root|--deadline-monotonic-ms|--reviewed-script-sha256|--transport-sha256|--fixture-sha256|--source-capture-sha256)
   (($#>=2)) || fail option-value; k=${1#--}; [[ -z ${v[$k]+x} ]] || fail duplicate-option; v[$k]=$2; shift 2 ;;
  *) fail unknown-option ;;
 esac
done
for k in execute case generation build-root reviewed-root deadline-monotonic-ms reviewed-script-sha256 transport-sha256 fixture-sha256 source-capture-sha256; do [[ -n ${v[$k]-} ]] || fail missing-option; done
[[ ${v[case]} == N13 || ${v[case]} == N14 ]] || fail case-selector
[[ $EUID == 0 && $OSTYPE == linux* && ${v[generation]} =~ ^[0-9a-f]{32}$ ]] || fail root-platform-generation
for k in reviewed-script-sha256 transport-sha256 fixture-sha256 source-capture-sha256; do [[ ${v[$k]} =~ ^[0-9a-f]{64}$ ]] || fail digest-shape; done
[[ ${v[source-capture-sha256]} == "$CAPTURE" ]] || fail source-capture-pin
expected_fixture=$N13; [[ ${v[case]} != N14 ]] || expected_fixture=$N14
[[ ${v[fixture-sha256]} == "$expected_fixture" ]] || fail fixture-pin
mono(){ local stamp rest whole fraction; IFS=' ' read -r stamp rest </proc/uptime || return 1; [[ $stamp =~ ^[0-9]+\.[0-9]+$ ]] || return 1; whole=${stamp%%.*}; fraction=${stamp#*.}000; fraction=${fraction:0:3}; printf '%s' "$((10#$whole*1000+10#$fraction))"; }
[[ ${v[deadline-monotonic-ms]} =~ ^[1-9][0-9]{0,14}$ ]] || fail deadline-shape
start=$(mono) || fail monotonic-clock
readonly hard_end=${v[deadline-monotonic-ms]} work_end=$((${v[deadline-monotonic-ms]}-RESERVE_MS))
((hard_end-start>RESERVE_MS && hard_end-start<=120000)) || fail original-deadline-bound
remaining(){ local now left; now=$(mono) || return 1; ((now<work_end)) || return 1; left=$((work_end-now)); printf '%d.%03d' "$((left/1000))" "$((left%1000))"; }
check(){ remaining >/dev/null || fail original-deadline; }
bounded(){ local seconds; seconds=$(remaining) || fail original-deadline; /usr/bin/timeout --signal=KILL "$seconds" "$@" 2>/dev/null || fail bounded-operation; check; }
for cmd in bash timeout stat sha256sum find sort cmp jq dd mkdir chmod chown cp head wc sync; do [[ -x /usr/bin/$cmd ]] || fail missing-trusted-tool; done
absolute(){ local path=$1 part current=; local -a parts; [[ $path == /* && $path != / && $path != */ && $path != *'//'* && ${#path} -le 4096 && ! $path =~ [[:cntrl:]] && $path != *\\* ]] || fail absolute-path; IFS=/ read -r -a parts <<<"${path#/}"; for part in "${parts[@]}"; do [[ -n $part && $part != . && $part != .. && ${#part} -le 255 ]] || fail path-component; current+=/$part; [[ ! -L $current ]] || fail path-link; done; }
protected_parent(){ local path=$1 fact uid gid mode; absolute "$path"; while :; do [[ -d $path && ! -L $path ]] || fail protected-parent; fact=$(bounded /usr/bin/stat -c '%u:%g:%a' -- "$path"); IFS=: read -r uid gid mode <<<"$fact"; [[ $uid == 0 && $gid == 0 && $mode =~ ^[0-7]{3,4}$ ]] && (((8#$mode & 0022)==0)) || fail parent-custody; [[ $path == / ]] && break; path=${path%/*}; [[ -n $path ]] || path=/; done; }
identity(){ bounded /usr/bin/stat -c '%d:%i:%s:%f:%h:%u:%g:%y:%z' -- "$1"; }
pin(){ local path=$1 hash=$2 cap=$3 before size actual; absolute "$path"; [[ -f $path && ! -L $path ]] || fail regular-input; before=$(identity "$path"); size=$(bounded /usr/bin/stat -c %s -- "$path"); [[ $size =~ ^[0-9]+$ ]] && ((size<=cap)) || fail file-size; [[ $(bounded /usr/bin/stat -c %h -- "$path") == 1 ]] || fail file-links; actual=$(bounded /usr/bin/sha256sum -- "$path"); [[ ${actual:0:64} == "$hash" && $(identity "$path") == "$before" ]] || fail input-pin; }
trusted_pin(){ local path=$1 hash=$2 cap=$3 fact uid gid mode links; protected_parent "${path%/*}"; pin "$path" "$hash" "$cap"; fact=$(bounded /usr/bin/stat -c '%u:%g:%a:%h' -- "$path"); IFS=: read -r uid gid mode links <<<"$fact"; [[ $uid == 0 && $gid == 0 && $links == 1 && $mode =~ ^[0-7]{3,4}$ ]] && (((8#$mode & 0022)==0)) || fail trusted-file-custody; }
trusted_pin "${BASH_SOURCE[0]}" "${v[reviewed-script-sha256]}" 131072
trusted_pin "${v[reviewed-root]}/prepare-root-inputs-v2.sh" "${v[transport-sha256]}" 131072
absolute "${v[build-root]}"; absolute "${v[reviewed-root]}"; protected_parent "${v[reviewed-root]}"
[[ -d ${v[build-root]} && ! -L ${v[build-root]} ]] || fail build-root-kind
receipt=${v[build-root]}/receipts/build-receipt.json
[[ ! -e ${v[build-root]}/receipts/late-publication-failure.json && ! -L ${v[build-root]}/receipts/late-publication-failure.json ]] || fail late-build-failure
absolute "$receipt"; [[ -f $receipt && ! -L $receipt && $(bounded /usr/bin/stat -c %h -- "$receipt") == 1 && $(bounded /usr/bin/stat -c %s -- "$receipt") -le 4194304 ]] || fail receipt-shape
receipt_before=$(identity "$receipt"); receipt_hash=$(bounded /usr/bin/sha256sum -- "$receipt"); receipt_hash=${receipt_hash:0:64}
bounded /usr/bin/python3 -B - "$receipt" <<'INTEGRATED_BUILD_RECEIPT'
import base64, hashlib, zlib
REVIEWED_READER_PACKED = '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'
REVIEWED_READER_LENGTH = 14103
REVIEWED_READER_SHA256 = '4941f2b3a7164b028c6e19152d8772777c66e9f735d994641dc4084e152e6e50'
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
[[ $(identity "$receipt") == "$receipt_before" ]] || fail receipt-changed
for pair in "checkpoint-owner-death-common.sh:$COMMON" "check_owner_death_record.py:$PARSER" "preparation.json:$PREPARATION"; do name=${pair%%:*}; hash=${pair#*:}; trusted_pin "${v[reviewed-root]}/$name" "$hash" 131072; done
fixture=checkpoint-n13-owner-killed.sh; [[ ${v[case]} != N14 ]] || fixture=checkpoint-n14-owner-stopped.sh
trusted_pin "${v[reviewed-root]}/$fixture" "${v[fixture-sha256]}" 131072
capture=${v[reviewed-root]}/source-capture.json
pin "$capture" "$CAPTURE" 4194304
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
bounded /usr/bin/jq -e --arg h "$SOURCE" --arg p "$PARENT" --arg t "c5dd0770c00b14b61f32851adeaad1cce4f2d7a8" --argjson n "$COUNT" "$capture_query" "$capture" >/dev/null
output=/var/lib/appsurface-evidence-inbound-${v[generation]}; protected_parent /var/lib; [[ ! -e $output && ! -L $output ]] || fail destination-exists
for input in "${v[build-root]}" "${v[reviewed-root]}"; do [[ $input != "$output" && $input != "$output/"* && $output != "$input/"* ]] || fail input-output-overlap; done
success=0; outer_pin=; work=
on_exit(){ local code=$? now left seconds confirmed=0; trap - EXIT; now=$(mono) || now=$hard_end; if ((code!=0 || success!=1 || now>=work_end)); then if ((now<hard_end)) && [[ -n ${outer_pin-} ]]; then left=$((hard_end-now)); printf -v seconds '%d.%03d' "$((left/1000))" "$((left%1000))"; [[ $(/usr/bin/timeout --signal=KILL "$seconds" /usr/bin/stat -c '%d:%i:%u:%g:%a' -- "$output" 2>/dev/null) == "$outer_pin:700" ]] && confirmed=1; fi; if ((confirmed)); then printf 'INBOUND_FAILED:PARTIAL_ROOT700_CONFIRMED:%s\n' "${v[generation]}" >&2; else printf 'INBOUND_FAILED:PARTIAL_QUARANTINE_UNKNOWN:%s\n' "${v[generation]}" >&2; fi; ((code!=0)) || code=1; fi; exit "$code"; }
trap on_exit EXIT
bounded /usr/bin/mkdir -m 0700 -- "$output"; [[ $(bounded /usr/bin/stat -c '%u:%g:%a' -- "$output") == 0:0:700 ]] || fail fresh-root-custody
outer_pin=$(bounded /usr/bin/stat -c '%d:%i:%u:%g' -- "$output"); bounded /usr/bin/mkdir -m 0700 -- "$output/control" "$output/scripts"; work=$output/control
copy_file(){ local from=$1 to=$2 hash=$3 cap=$4 mode=$5 before size; pin "$from" "$hash" "$cap"; before=$(identity "$from"); size=$(bounded /usr/bin/stat -c %s -- "$from"); [[ ! -e $to && ! -L $to ]] || fail copy-collision; bounded /usr/bin/dd if="$from" of="$to" iflag=nofollow,nonblock,count_bytes oflag=nofollow conv=fsync,excl count="$((size+1))" status=none; [[ $(identity "$from") == "$before" && $(bounded /usr/bin/stat -c %s -- "$to") == "$size" ]] || fail copy-substitution; bounded /usr/bin/chown --no-dereference 0:0 -- "$to"; bounded /usr/bin/chmod "$mode" -- "$to"; trusted_pin "$to" "$hash" "$cap"; pin "$from" "$hash" "$cap"; }
copy_file "$receipt" "$work/build-receipt.json" "$receipt_hash" 4194304 0600
copy_file "$capture" "$work/source-capture.json" "$CAPTURE" 4194304 0600
snapshot(){ local tree=$1 target=$2 size count; absolute "$tree"; [[ -d $tree && ! -L $tree ]] || fail tree-kind; bounded /usr/bin/bash -o pipefail -c '/usr/bin/find -P "$1" -printf "%P\t%y\t%m\t%U\t%G\t%n\t%s\t%D\t%i\t%T@\t%C@\n" | /usr/bin/head -c 8388609' -- "$tree" >"$target.raw"; size=$(bounded /usr/bin/stat -c %s -- "$target.raw"); count=$(bounded /usr/bin/wc -l <"$target.raw"); ((size<=8388608 && count>0 && count<=NODE_CAP)) || fail inventory-bound; bounded /usr/bin/sort -- "$target.raw" >"$target"; }
validate_tree(){ local kind=$1 inventory=$2 owner=$3; bounded /usr/bin/jq -Rse --slurpfile map "$work/$kind-nodes.json" --arg owner "$owner" '
 def num: test("^[0-9]+$"); $map[0] as $m | if endswith("\n") then split("\n")[:-1]|map(split("\t")) else error("shape") end | map(if .[0]=="" then .[0]="." else . end) as $r |
 ($r|map(.[0])|sort)==(($m.files|keys)+($m.directories|keys)|sort) and ($r|map(.[0])|unique|length)==($r|length) and
 all($r[]; length==11 and (.[1]=="d" or .[1]=="f") and (.[3]|num) and (.[4]|num) and (.[5]|num) and (.[6]|num) and (.[7]|num) and (.[8]|num) and (if $owner=="root" then .[3]=="0" and .[4]=="0" else true end) and
  (if .[1]=="d" then $m.directories[.[0]]!=null and .[2]==$m.directories[.[0]].mode[1:] else $m.files[.[0]]!=null and .[5]=="1" and (.[6]|tonumber)==$m.files[.[0]].bytes and .[2]==$m.files[.[0]].mode[1:] end)) and ([$r[]|select(.[1]=="f")|.[6]|tonumber]|add)<=1073741824
 ' "$inventory" >/dev/null; }
verify_tree(){ local kind=$1 tree=$2 owner=$3 tag=$4; snapshot "$tree" "$work/$kind-$tag-before.txt"; validate_tree "$kind" "$work/$kind-$tag-before.txt" "$owner"; (cd -- "$tree" && bounded /usr/bin/sha256sum --check --strict --status "$work/$kind-checksums.txt") >"$work/$kind-$tag-hash.log" 2>&1; snapshot "$tree" "$work/$kind-$tag-after.txt"; validate_tree "$kind" "$work/$kind-$tag-after.txt" "$owner"; bounded /usr/bin/cmp -- "$work/$kind-$tag-before.txt" "$work/$kind-$tag-after.txt"; }
for kind in source tool runtime; do
 root=${v[build-root]}/handoff/$kind; roots[$kind]=$root
 map_hash[$kind]=$(bounded /usr/bin/jq -r --arg k "$kind" '.artifacts[$k].tsv_sha256' "$receipt"); node_hash[$kind]=$(bounded /usr/bin/jq -r --arg k "$kind" '.artifacts[$k].nodes_sha256' "$receipt")
 copy_file "${v[build-root]}/handoff/$kind.tsv" "$work/$kind.tsv" "${map_hash[$kind]}" 1048576 0600
 copy_file "${v[build-root]}/handoff/$kind-nodes.json" "$work/$kind-nodes.json" "${node_hash[$kind]}" 4194304 0600
 bounded /usr/bin/jq -acS . "$work/$kind-nodes.json" >"$work/$kind-nodes-canonical.json"; bounded /usr/bin/cmp -- "$work/$kind-nodes.json" "$work/$kind-nodes-canonical.json" || fail node-canonical
 bounded /usr/bin/jq -e --arg kind "$kind" --slurpfile receipt "$receipt" '
  def rel: type=="string" and utf8bytelength<=4096 and (test("[\u0000-\u001f\u007f\\\\:]")|not) and (split("/")|all(.[]; length>0 and .!="." and .!=".." and .!=".git" and utf8bytelength<=255));
  . as $m | type=="object" and keys==["directories","files","root_name","schema"] and .schema=="issue779-build-node-inventory-v1" and .root_name==$kind and (.files|type)=="object" and (.directories|type)=="object" and .directories["."]!=null and (.files|length)==$receipt[0].artifacts[$kind].file_count and ((.files|length)+(.directories|length))==$receipt[0].artifacts[$kind].node_count and ([.files[].bytes]|add)==$receipt[0].artifacts[$kind].total_bytes and all(.directories|to_entries[]; (.key=="." or (.key|rel)) and (.value|type=="object" and keys==["mode"] and .mode==(if $kind=="source" then "0700" else "0555" end))) and all(.files|to_entries[]; (.key|rel) and $m.directories[.key]==null and (.value|type=="object" and keys==["bytes","mode","sha256"] and (.sha256|test("^[0-9a-f]{64}$")) and (.bytes|type=="number" and floor==. and .>=0 and .<=268435456) and (if $kind=="source" then .mode=="0644" or .mode=="0755" else .mode=="0444" or .mode=="0555" end))) and (if $kind=="source" then (.files|length)==2872 else true end)
 ' "$work/$kind-nodes.json" >/dev/null
 bounded /usr/bin/jq -r '.files|to_entries|sort_by(.key)[]|[.value.mode,.value.sha256,.key]|@tsv' "$work/$kind-nodes.json" >"$work/$kind-derived.tsv"; bounded /usr/bin/cmp -- "$work/$kind.tsv" "$work/$kind-derived.tsv" || fail map-mismatch
 bounded /usr/bin/jq -r '.files|to_entries|sort_by(.key)[]|.value.sha256+"  "+.key' "$work/$kind-nodes.json" >"$work/$kind-checksums.txt"
 verify_tree "$kind" "$root" original original-first
 bounded /usr/bin/cp --recursive --no-dereference --preserve=mode,timestamps -- "$root" "$output/$kind"
 snapshot "$output/$kind" "$work/$kind-copied.txt"; validate_tree "$kind" "$work/$kind-copied.txt" original
 bounded /usr/bin/find -P "$output/$kind" -exec /usr/bin/chown --no-dereference 0:0 -- '{}' +; bounded /usr/bin/sync -f -- "$output/$kind"
 verify_tree "$kind" "$output/$kind" root destination-final; verify_tree "$kind" "$root" original original-final
 bounded /usr/bin/cmp -- "$work/$kind-original-first-before.txt" "$work/$kind-original-final-after.txt" || fail original-tree-changed
 done
for spec in "checkpoint-owner-death-common.sh:$COMMON" "check_owner_death_record.py:$PARSER" "$fixture:${v[fixture-sha256]}"; do name=${spec%%:*}; hash=${spec#*:}; trusted_pin "${v[reviewed-root]}/$name" "$hash" 131072; copy_file "${v[reviewed-root]}/$name" "$output/scripts/$name" "$hash" 131072 0444; done
trusted_pin "${v[reviewed-root]}/preparation.json" "$PREPARATION" 131072
copy_file "${v[reviewed-root]}/preparation.json" "$work/fixture-preparation.json" "$PREPARATION" 131072 0600
bounded /usr/bin/jq -ncS --arg g "${v[generation]}" --arg source "$SOURCE" --arg parent "$PARENT" --arg case "${v[case]}" --arg receipt "$receipt_hash" --arg capture "$CAPTURE" --argjson deadline "$hard_end" '{schema:"issue779-owner-death-inbound-acquisition-v1",generation:$g,source_commit:$source,source_parent:$parent,source_count:2872,case:$case,build_receipt_sha256:$receipt,source_capture_sha256:$capture,original_deadline_monotonic_ms:$deadline,acquired_code_executed:false,native_execution:false,native_acceptance_claim:false}' >"$work/acquisition-receipt.json"
bounded /usr/bin/sync -f -- "$work/acquisition-receipt.json"; [[ $(bounded /usr/bin/stat -c '%d:%i:%u:%g:%a' -- "$output") == "$outer_pin:700" ]] || fail final-outer-identity
check; printf '%s\n' 'INBOUND_ACQUIRED:DATA_ONLY:ORIGINAL_TRANSPORT_DEADLINE_UNCHANGED'; check; success=1
