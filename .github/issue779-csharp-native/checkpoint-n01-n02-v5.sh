#!/usr/bin/env bash
# Source-only candidate; all runtime records are produced only by actual explicit execution.
set -euo pipefail
export LC_ALL=C PATH=/usr/sbin:/usr/bin:/sbin:/bin
umask 077
readonly FIXTURE_SECONDS=600 CLEANUP_RESERVE=30
readonly NEGATIVE_CASE=N06 NEGATIVE_SOURCE_COUNT=2827
readonly NEGATIVE_CORE_BASE=a358d47ef36939718536b13c27b038faf307299b
readonly NEGATIVE_SOURCE_REVISION=5b2a125b0d5bc72d10b11aeea62176d4a0ac2fec NEGATIVE_SOURCE_CAPTURE_CLEAR=1
readonly NEGATIVE_FIXTURE_REVIEW_CLEAR=1 NEGATIVE_RAW_STREAM_PRODUCER_CLEAR=1
readonly NEGATIVE_PYTHON_TRUST_CLEAR=1 NEGATIVE_ROOT_TERMINAL_CLEAR=1
readonly NEGATIVE_KERNEL_PARSER_SHA256=90da0002db90ddb8b769bd3530551f10363ce772bc99327bdd27976be71cf3ab
readonly MAX_MANIFEST_BYTES=8388608 MAX_ROWS=32768 MAX_NODES=65536
readonly MAX_INPUT_FILE_BYTES=2147483648 MAX_INPUT_TREE_BYTES=4294967296
readonly MAX_NODE_JSON_BYTES=4194304 CORE_FILE_BYTES=268435456 CORE_TREE_BYTES=1073741824 CORE_NODES=8192
mode=prepare-only; mode_selected=0; phase=work; diagnostic_stage=arguments; diagnostic_failure_reported=0
source_root= source_manifest= source_manifest_sha256= payload_root= payload_manifest= payload_manifest_sha256=
runtime_root= runtime_manifest= runtime_manifest_sha256= source_review= source_review_sha256=
build_receipt= build_receipt_sha256= os_audit= os_audit_sha256= reviewed_script_sha256=
source_revision= base_revision= workflow_identity= entry= runtime_host= entry_sha256= n02_uid= n02_gid=
source_nodes= source_nodes_sha256= payload_nodes= payload_nodes_sha256= runtime_nodes= runtime_nodes_sha256=
n03_helper_root= n03_helper_map= n03_helper_map_sha256= n03_helper_nodes= n03_helper_nodes_sha256= n03_helper_entry_sha256= n03_helper_generation=
n03_exit= n03_result_sha256=
readonly N03_INDEPENDENT_REVIEW_CLEAR=1
readonly N03_BROKER_NAME=_apt N03_WORKER_NAME=nobody N03_SHARED_GROUP=nogroup N03_STARTUP_KIB=262144
fail() { printf 'CHECKPOINT_REJECTED:%s\n' "$1" >&2; exit 1; }
while (($#)); do
 case "$1" in
 --prepare-only|--execute) ((mode_selected==0)) || fail duplicate-mode; mode=${1#--}; mode_selected=1; shift ;;
 --source-root|--source-manifest|--source-manifest-sha256|--source-nodes|--source-nodes-sha256|--payload-root|--payload-manifest|--payload-manifest-sha256|--payload-nodes|--payload-nodes-sha256|--runtime-root|--runtime-manifest|--runtime-manifest-sha256|--runtime-nodes|--runtime-nodes-sha256|--source-review|--source-review-sha256|--build-receipt|--build-receipt-sha256|--os-audit|--os-audit-sha256|--reviewed-script-sha256|--source-revision|--base-revision|--workflow-identity|--entry|--runtime-host|--entry-sha256|--n02-uid|--n02-gid|--n03-helper-root|--n03-helper-map|--n03-helper-map-sha256|--n03-helper-nodes|--n03-helper-nodes-sha256|--n03-helper-entry-sha256|--n03-helper-generation)
 (($#>=2)) || fail option-value; key=${1#--}; key=${key//-/_}; [[ -z ${!key} ]] || fail duplicate-option
 printf -v "$key" '%s' "$2"; shift 2 ;;
 *) fail unknown-option ;;
 esac
done
if [[ $mode == prepare-only && -z $source_root ]]; then
 printf '%s\n' 'PREPARATION_ONLY:NO_INPUTS_VERIFIED:N01_NOT_RUN:N02_NOT_RUN'; exit 0
fi
((NEGATIVE_SOURCE_CAPTURE_CLEAR==1 && NEGATIVE_FIXTURE_REVIEW_CLEAR==1)) || fail negative-source-and-review-pending
[[ $NEGATIVE_SOURCE_REVISION =~ ^[0-9a-f]{40}$ && $source_revision == "$NEGATIVE_SOURCE_REVISION" ]] || fail negative-source-pin
((N03_INDEPENDENT_REVIEW_CLEAR==1)) || fail N03-integration-review-pending
[[ $OSTYPE == linux* ]] || fail Linux-x64
for t in dd sha256sum stat find timeout head jq readelf awk sort cmp cp chmod chown install systemd-run systemctl getent setpriv strace date sleep od tr cut cat grep wc readlink uname setsid ps bash mv; do command -v "$t" >/dev/null || fail missing-trusted-tool; done
monotonic() { local up rest; IFS=' ' read -r up rest </proc/uptime || return 1; [[ $up =~ ^[0-9]+\.[0-9]+$ ]] || return 1; printf '%s' "${up%%.*}"; }
fixture_start=$(monotonic) || fail monotonic-clock
readonly fixture_start hard_end=$((fixture_start+FIXTURE_SECONDS)) work_end=$((fixture_start+FIXTURE_SECONDS-CLEANUP_RESERVE))
left() {
 local now end=$work_end
 [[ $phase != cleanup ]] || end=$hard_end
 # Bash dynamic scope carries the private N03 startup bound through all nested helpers/substitutions.
 if [[ $phase != cleanup && ${n03_startup_active:-0} == 1 ]]; then
  [[ ${n03_startup_end:-} =~ ^[0-9]+$ ]] || return 1
  ((n03_startup_end>=end)) || end=$n03_startup_end
 fi
 now=$(monotonic) || return 1
 ((now<end)) || return 1
 printf '%s' "$((end-now))"
}
remaining() { left || fail fixture-deadline; }
try_bounded() { local n; n=$(left) || return 124; timeout --signal=KILL "$n" "$@"; }
report_bounded_failure() {
 local code=$1 tool=other stage=other
 ((diagnostic_failure_reported==0)) || return 0
 diagnostic_failure_reported=1
 case "$diagnostic_stage" in
  arguments|platform|source-input|tool-input|runtime-input|os-input|root-layout|tool-copy|runtime-copy|subject-copy|request-preparation|sealed-inputs|n02|n01) stage=$diagnostic_stage ;;
 esac
 case "${2##*/}" in
  uname|stat|sha256sum|jq|head|find|readelf|readlink|install|cp|chown|chmod|date|getent|systemctl|sleep|ps|grep|mv|bash|strace|setpriv|setsid|timeout|awk|sort|cmp|od|tr|cut|cat|wc) tool=${2##*/} ;;
 esac
 [[ $code =~ ^[0-9]+$ ]] && ((code>=1 && code<=255)) || code=1
 printf 'FIXTURE_BOUNDED_FAILURE:%s:%s:%d\n' "$stage" "$tool" "$code" >&2 || :
}
bounded() {
 local code=0
 try_bounded "$@" || code=$?
 if ((code!=0)); then report_bounded_failure "$code" "${1:-}"; fail bounded-operation; fi
 remaining >/dev/null
}
diagnostic_stage=platform
[[ $(bounded uname -m) == x86_64 ]] || fail Linux-x64
lexical() {
 local path=$1 s; [[ $path == /* && $path != / && $path != *'//'* && $path != */ && ${#path} -le 4096 && ! $path =~ [[:cntrl:]] ]] || fail absolute-path
 IFS=/ read -r -a components <<<"${path#/}"
 for s in "${components[@]}"; do [[ -n $s && $s != . && $s != .. ]] || fail path-component; done
}
absolute() { local path=$1 s current=; lexical "$path"; for s in "${components[@]}"; do current+=/$s; [[ ! -L $current ]] || fail deployment-symlink; done; }
sha() { [[ $1 =~ ^[0-9a-f]{64}$ ]] || fail sha256-syntax; }
pin_file() {
 local p=$1 h=$2 limit=$3 before after size measured
 absolute "$p"; sha "$h"; [[ -f $p && ! -L $p ]] || fail regular-file
 before=$(bounded stat -c '%d:%i:%s:%f:%h:%u:%g' -- "$p"); size=$(bounded stat -c %s -- "$p")
 [[ $size =~ ^[0-9]+$ ]] && ((size<=limit)) || fail file-byte-bound
 [[ $(bounded stat -c %h -- "$p") == 1 ]] || fail hardlink
 measured=$(bounded sha256sum -- "$p"); after=$(bounded stat -c '%d:%i:%s:%f:%h:%u:%g' -- "$p")
 [[ $before == "$after" && ${measured:0:64} == "$h" ]] || fail file-pin-changed
}
ancestor() {
 local p=$1 uid perms; absolute "$p"
 while :; do
  [[ -d $p && ! -L $p ]] || fail ancestor-type
  uid=$(bounded stat -c %u -- "$p"); perms=$(bounded stat -c %a -- "$p")
  [[ $uid == 0 ]] && (((8#$perms & 0022)==0)) || fail unsafe-root-ancestor
  [[ $p == / ]] && break; p=${p%/*}; [[ -n $p ]] || p=/
 done
}
# Node JSON is the authenticated build producer's compact sorted ASCII JSON plus LF.
# Re-encoding is only for this Python-produced node format, never for C# evidence files.
validate_node_inventory() {
 local file=$1 digest=$2 root_name=$3 canonical measured
 pin_file "$file" "$digest" "$MAX_NODE_JSON_BYTES"
 canonical=$(bounded jq -acS . "$file")
 measured=$(printf '%s\n' "$canonical" | bounded sha256sum)
 [[ ${measured:0:64} == "$digest" ]] || fail node-canonical-or-duplicate
 bounded jq -e --arg root "$root_name" '
  def rel: type=="string" and length>0 and utf8bytelength<=4096 and
   (startswith("/")|not) and (split("/")|all(.!="" and .!="." and .!="..")) and
   (explode|all(.>=32 and .!=127));
  def mode: type=="string" and test("^0[0-7]{3}$");
  def bytes: type=="number" and floor==. and .>=0 and .<=2147483648;
  . as $r | keys==["directories","files","root_name","schema"] and
  .schema=="issue779-build-node-inventory-v1" and .root_name==$root and
  (.files|type=="object" and length>0 and length<=32768) and
  (.directories|type=="object" and length>0) and
  ((.files|length)+(.directories|length)<=65536) and (.directories|has(".")) and
  all(.files|to_entries[]; (.key|rel) and (.value|type=="object" and
   keys==["bytes","mode","sha256"] and (.bytes|bytes) and (.mode|mode) and
   (.sha256|type=="string" and test("^[0-9a-f]{64}$")))) and
  all(.directories|to_entries[]; (.key=="." or (.key|rel)) and
   (.value|type=="object" and keys==["mode"] and (.mode|mode))) and
  all(.files|keys[]; . as $p | ($r.directories|has($p)|not)) and
  all(($r.files|keys[]),($r.directories|keys[]|select(.!="."));
   split("/") as $parts | all(range(1;($parts|length));
    . as $n | ($parts[0:$n]|join("/")) as $ancestor | $r.directories|has($ancestor))) and
  (if $root=="source" then
   all(.files[];.mode=="0644" or .mode=="0755") and
   all(.directories[];.mode=="0700")
  else all(.files[];.mode=="0444" or .mode=="0555") and
   all(.directories[];.mode=="0555") end)
 ' "$file" >/dev/null || fail node-schema
 pin_file "$file" "$digest" "$MAX_NODE_JSON_BYTES"
}
# Exact JSON directory membership includes authenticated empty directories.
verify_tree_unbatched() {
 local root=$1 manifest=$2 expected=$3 node_file=$4 node_digest=$5 root_name=$6 sealed=${7:-0} policy_extra=${8:-0}
 local line m h rel extra previous= p size before after measured actual desired nodes kind declared_bytes node_rows depth
 local rows=0 total=0 root_before root_after ancestor_nodes
 absolute "$root"; [[ -d $root && ! -L $root ]] || fail input-root
 ancestor_nodes=${#components[@]}
 root_before=$(bounded stat -c '%d:%i:%f:%u:%g' -- "$root"); pin_file "$manifest" "$expected" "$MAX_MANIFEST_BYTES"
 validate_node_inventory "$node_file" "$node_digest" "$root_name"
 local -A wanted=() node_modes=() node_hashes=() node_bytes=() manifest_seen=()
 node_rows=$(bounded jq -r '(.directories|to_entries|sort_by(.key)[]|["d",.key,.value.mode]|join("\t")),
  (.files|to_entries|sort_by(.key)[]|["f",.key,.value.mode,.value.sha256,(.value.bytes|tostring)]|join("\t"))' "$node_file")
 while IFS=$'\t' read -r kind rel m h declared_bytes extra; do
  remaining >/dev/null
  [[ -n $rel && -z $extra && ! ${wanted[$rel]+present} ]] || fail node-row
  wanted[$rel]=$kind; node_modes[$rel]=$m
  if [[ $kind == f ]]; then node_hashes[$rel]=$h; node_bytes[$rel]=$declared_bytes; fi
 done <<<"$node_rows"
 while IFS= read -r line || [[ -n $line ]]; do
  remaining >/dev/null
  IFS=$'\t' read -r m h rel extra <<<"$line"
  [[ $line == "$m"$'\t'"$h"$'\t'"$rel" && -z $extra && $m =~ ^0[0-7]{3}$ ]] || fail manifest-shape
  sha "$h"; [[ -n $rel && $rel != /* && ${#rel} -le 4096 && ! $rel =~ [[:cntrl:]] ]] || fail manifest-path
  [[ -z $previous || $rel > $previous ]] || fail manifest-order
  previous=$rel; rows=$((rows+1)); ((rows<=MAX_ROWS)) || fail manifest-row-bound
  [[ ${wanted[$rel]:-} == f && ! ${manifest_seen[$rel]+present} && ${node_modes[$rel]} == "$m" && ${node_hashes[$rel]} == "$h" ]] || fail manifest-node-disagreement
  manifest_seen[$rel]=1
  p=$root/$rel; absolute "$p"; [[ -f $p && ! -L $p ]] || fail manifest-file
  size=$(bounded stat -c %s -- "$p"); [[ $size =~ ^[0-9]+$ ]] && ((size<=MAX_INPUT_FILE_BYTES)) || fail input-file-bound
  [[ $size == "${node_bytes[$rel]}" ]] || fail manifest-node-length
  total=$((total+size)); ((total<=MAX_INPUT_TREE_BYTES)) || fail input-tree-bound
  desired=${m#0}; if ((sealed)); then desired=444; (((8#$m & 0111)==0)) || desired=555; fi
  actual=$(bounded stat -c %a -- "$p"); [[ $actual == "$desired" ]] || fail manifest-mode
  [[ $(bounded stat -c %h -- "$p") == 1 ]] || fail manifest-hardlink
  if ((sealed)); then [[ $(bounded stat -c '%u:%g' -- "$p") == 0:0 ]] || fail sealed-owner; fi
  before=$(bounded stat -c '%d:%i:%s:%f:%h:%u:%g' -- "$p"); measured=$(bounded sha256sum -- "$p"); after=$(bounded stat -c '%d:%i:%s:%f:%h:%u:%g' -- "$p")
  [[ $before == "$after" && ${measured:0:64} == "$h" ]] || fail manifest-content
 done <"$manifest"
 ((rows>0)) || fail empty-manifest
 ((${#manifest_seen[@]}==${#node_hashes[@]})) || fail manifest-node-file-set
 if ((policy_extra)); then
  [[ ! ${wanted[fixture-policy.json]+present} ]] || fail policy-manifest-collision
  wanted[fixture-policy.json]=f; pin_file "$root/fixture-policy.json" "$policy_sha" 4096
  [[ $(bounded stat -c '%u:%g:%a:%h' -- "$root/fixture-policy.json") == 0:0:444:1 ]] || fail policy-custody
  size=$(bounded stat -c %s -- "$root/fixture-policy.json"); node_bytes[fixture-policy.json]=$size
  total=$((total+size)); ((total<=MAX_INPUT_TREE_BYTES)) || fail input-tree-bound
 fi
 ((${#wanted[@]}<=MAX_NODES)) || fail node-bound
 # Bound the byte representation before command substitution; reject controls instead of decoding paths.
 nodes=$(bounded bash -c 'set -euo pipefail; timeout --signal=KILL "$1" find -P "$2" -mindepth 1 -printf "%P\t%y\n" | head -c 16777217' -- "$(remaining)" "$root")
 ((${#nodes}<=16777216)) || fail node-inventory-byte-bound
 local -A seen=(); seen['.']=d
 while IFS=$'\t' read -r rel actual extra; do
  remaining >/dev/null
  [[ -n $rel && $rel != . ]] || fail node-path
  [[ -z $extra && ( $actual == f || $actual == d ) && ! $rel =~ [[:cntrl:]] && ${wanted[$rel]+present} && ${wanted[$rel]} == "$actual" && ! ${seen[$rel]+present} ]] || fail full-node-set
  seen[$rel]=1
  if [[ $actual == d ]]; then
   absolute "$root/$rel"
  fi
 done <<<"$nodes"
 # The root '.' is checked directly, descendants without a dot component.
 ((${#seen[@]}==${#wanted[@]})) || fail full-node-count
 for rel in "${!wanted[@]}"; do
  remaining >/dev/null
  [[ ${wanted[$rel]} == d ]] || continue
  p=$root; [[ $rel == . ]] || p=$root/$rel
  absolute "$p"; [[ -d $p && ! -L $p ]] || fail full-directory-type
  actual=$(bounded stat -c %a -- "$p")
  if ((sealed)); then [[ $(bounded stat -c '%u:%g:%a' -- "$p") == 0:0:555 ]] || fail full-directory-mode
  elif [[ $actual != "${node_modes[$rel]#0}" ]]; then
   # Only intentional root transport sealing may normalize build source directories.
   [[ $root_name == source && $(bounded stat -c '%u:%g:%a' -- "$p") == 0:0:555 ]] || fail input-directory-mode
  fi
 done
 if ((sealed)) && [[ $root_name != source ]]; then
  ((total<=CORE_TREE_BYTES && ${#wanted[@]}+ancestor_nodes<=CORE_NODES)) || fail core-tree-envelope
  for rel in "${!wanted[@]}"; do
   remaining >/dev/null
   if [[ $rel == . ]]; then depth=0; else IFS=/ read -r -a components <<<"$rel"; depth=${#components[@]}; fi
   if [[ ${wanted[$rel]} == d ]]; then ((depth<=32)) || fail core-directory-depth
   else size=${node_bytes[$rel]}; ((depth<=33 && size>0 && size<=CORE_FILE_BYTES)) || fail core-file-envelope; fi
  done
 fi
 root_after=$(bounded stat -c '%d:%i:%f:%u:%g' -- "$root"); [[ $root_before == "$root_after" ]] || fail tree-root-changed
 pin_file "$manifest" "$expected" "$MAX_MANIFEST_BYTES"
 pin_file "$node_file" "$node_digest" "$MAX_NODE_JSON_BYTES"
 printf 'FULL_TREE_POINT_SAMPLES_MATCHED:%s:%s:%s\n' "$rows" "${#wanted[@]}" "$total"
}
# Replacement block for the existing fixture, not a standalone/root entry point.
# Uses only its existing bounded/remaining/ancestor/pin_file/node-schema helpers.
batch_check_time() {
 local stamp rest now end=$work_end
 [[ $phase != cleanup ]] || end=$hard_end
 IFS=' ' read -r stamp rest </proc/uptime || fail monotonic-clock
 [[ $stamp =~ ^[0-9]+\.[0-9]+$ ]] || fail monotonic-clock
 now=${stamp%%.*}; ((now<end)) || fail fixture-deadline
}
batch_root_pin() {
 local root=$1 expected=$2 measured
 absolute "$root"; [[ -d $root && ! -L $root ]] || fail input-root
 measured=$(bounded stat -c '%d:%i:%f:%u:%g' -- "$root")
 [[ $measured == "$expected" ]] || fail tree-root-changed
}
batch_create_scratch_leaf() {
 local path=$1
 [[ ! -e $path && ! -L $path ]] || fail audit-scratch-collision
 bounded /usr/bin/dd of="$path" oflag=nofollow conv=excl count=0 status=none
}
batch_scratch_pin() {
 local path=$1 fd=$2 cap=$3 named opened size
 [[ -f $path && ! -L $path ]] || fail audit-scratch-type
 named=$(bounded stat -c '%d:%i:%s:%f:%h:%u:%g' -- "$path")
 opened=$(bounded stat -L -c '%d:%i:%s:%f:%h:%u:%g' -- "/proc/$batch_fd_owner_pid/fd/$fd")
 [[ $named == "$opened" ]] || fail audit-scratch-substitution
 [[ $(bounded stat -c '%u:%g:%a:%h' -- "$path") == 0:0:600:1 ]] || fail audit-scratch-custody
 size=$(bounded stat -c %s -- "$path")
 [[ $size =~ ^[0-9]+$ ]] && ((size<=cap)) || fail audit-scratch-byte-bound
}
batch_snapshot() {
 local root=$1 path=$2 fd=$3
 # NUL fields preserve arbitrary find names until the parser explicitly rejects controls.
 # The outer bounded task and inner timeout consume the SAME original remaining allowance.
 bounded bash -c 'set -euo pipefail; timeout --signal=KILL "$1" find -P "$2" -printf "%P\0%y\0%D\0%i\0%s\0%m\0%n\0%U\0%G\0%T@\0%C@\0" | head -c 16777217' -- "$(remaining)" "$root" >&"$fd"
 batch_scratch_pin "$path" "$fd" 16777216
}
# Data parser only: caller retains FD and supplies authenticated wanted/mode/length maps.
# Bash dynamic scope shares pre/seen/total from verify_tree; this is not an authority factory.
# Separately sourceable for future finite data controls without the fixture entry point.
batch_parse_snapshot() {
 local pass=$1 read_fd=$2 root_name=$3 sealed=$4 field n rel kind record m desired size
 local -a fields=()
  while :; do
   batch_check_time; fields=()
   if ! IFS= read -r -d '' field <&"$read_fd"; then [[ -z $field ]] || fail full-node-set; break; fi
   fields+=("$field")
   for ((n=1;n<11;n++)); do IFS= read -r -d '' field <&"$read_fd" || fail full-node-set; fields+=("$field"); done
   rel=${fields[0]}; [[ -n $rel ]] || rel=.
   kind=${fields[1]}
   [[ ! $rel =~ [[:cntrl:]] && $rel != *\\* && ${wanted[$rel]+present} && ${wanted[$rel]} == "$kind" && ! ${seen[$rel]+present} ]] || fail full-node-set
   if [[ $rel == . ]]; then lexical "$root"; else lexical "$root/$rel"; fi
   seen[$rel]=1
   for ((n=2;n<9;n++)); do [[ ${fields[n]} =~ ^[0-9]+$ ]] || fail full-node-set; done
   [[ ${fields[5]} =~ ^[0-7]{3,4}$ && ${fields[9]} =~ ^-?[0-9]+\.[0-9]+$ && ${fields[10]} =~ ^-?[0-9]+\.[0-9]+$ ]] || fail full-node-set
   printf -v record '%s\t' "${fields[@]}"; record=${record%$'\t'}
   if [[ $pass == post ]]; then [[ ${pre[$rel]:-} == "$record" ]] || fail manifest-content; continue; fi
   pre[$rel]=$record
   m=${node_modes[$rel]}; desired=${m#0}
   if [[ $kind == f ]]; then
    size=${fields[4]}; ((size<=MAX_INPUT_FILE_BYTES)) || fail input-file-bound
    [[ $size == "${node_bytes[$rel]}" ]] || fail manifest-node-length
    total=$((total+size)); ((total<=MAX_INPUT_TREE_BYTES)) || fail input-tree-bound
    if ((sealed)); then desired=444; (((8#$m & 0111)==0)) || desired=555; fi
    [[ ${fields[5]} == "$desired" ]] || fail manifest-mode
    [[ ${fields[6]} == 1 ]] || fail manifest-hardlink
    if ((sealed)); then [[ ${fields[7]}:${fields[8]} == 0:0 ]] || fail sealed-owner; fi
   elif [[ $kind == d ]]; then
    if ((sealed)); then [[ ${fields[7]}:${fields[8]}:${fields[5]} == 0:0:555 ]] || fail full-directory-mode
    elif [[ ${fields[5]} != "$desired" ]]; then
     [[ $root_name == source && ${fields[7]}:${fields[8]}:${fields[5]} == 0:0:555 ]] || fail input-directory-mode
    fi
   else fail full-node-set; fi
  done
}
# Exact full JSON/TSV/physical membership, including authenticated empty directories.
verify_tree() {
 if [[ $mode == prepare-only ]]; then verify_tree_unbatched "$@"; return; fi
 local -r batch_fd_owner_pid=$BASHPID # The actual shell that opens every scratch descriptor.
 local root=$1 manifest=$2 expected=$3 node_file=$4 node_digest=$5 root_name=$6 sealed=${7:-0} policy_extra=${8:-0}
 local line m h rel extra previous= kind declared_bytes node_rows depth desired size
 local root_before
 local rows=0 total=0 ancestor_nodes scratch uuid scratch_before scratch_after checksum_bytes=0
 local pre_fd post_fd check_fd read_fd field n pass record named opened path
 local -a fields=()
 local -A wanted=() node_modes=() node_hashes=() node_bytes=() manifest_seen=() pre=() seen=()
 absolute "$root"; [[ -d $root && ! -L $root ]] || fail input-root
 # Old sha256sum's first-64-byte comparison already rejected escaped backslash names.
 [[ $root != *\\* ]] || fail manifest-path
 ancestor_nodes=${#components[@]}
 root_before=$(bounded stat -c '%d:%i:%f:%u:%g' -- "$root")
 pin_file "$manifest" "$expected" "$MAX_MANIFEST_BYTES"
 validate_node_inventory "$node_file" "$node_digest" "$root_name"
 node_rows=$(bounded jq -r '(.directories|to_entries|sort_by(.key)[]|["d",.key,.value.mode]|join("\t")),
  (.files|to_entries|sort_by(.key)[]|["f",.key,.value.mode,.value.sha256,(.value.bytes|tostring)]|join("\t"))' "$node_file")
 while IFS=$'\t' read -r kind rel m h declared_bytes extra; do
  batch_check_time
  [[ -n $rel && -z $extra && $rel != *\\* && ! ${wanted[$rel]+present} ]] || fail node-row
  wanted[$rel]=$kind; node_modes[$rel]=$m
  if [[ $kind == f ]]; then node_hashes[$rel]=$h; node_bytes[$rel]=$declared_bytes; fi
 done <<<"$node_rows"
 while IFS= read -r line || [[ -n $line ]]; do
  batch_check_time
  IFS=$'\t' read -r m h rel extra <<<"$line"
  [[ $line == "$m"$'\t'"$h"$'\t'"$rel" && -z $extra && $m =~ ^0[0-7]{3}$ ]] || fail manifest-shape
  sha "$h"; [[ -n $rel && $rel != /* && ${#rel} -le 4096 && ! $rel =~ [[:cntrl:]] && $rel != *\\* ]] || fail manifest-path
  [[ -z $previous || $rel > $previous ]] || fail manifest-order
  previous=$rel; rows=$((rows+1)); ((rows<=MAX_ROWS)) || fail manifest-row-bound
  [[ ${wanted[$rel]:-} == f && ! ${manifest_seen[$rel]+present} && ${node_modes[$rel]} == "$m" && ${node_hashes[$rel]} == "$h" ]] || fail manifest-node-disagreement
  manifest_seen[$rel]=1
 done <"$manifest"
 ((rows>0)) || fail empty-manifest
 ((${#manifest_seen[@]}==${#node_hashes[@]})) || fail manifest-node-file-set
 if ((policy_extra)); then
  [[ ! ${wanted[fixture-policy.json]+present} ]] || fail policy-manifest-collision
  pin_file "$root/fixture-policy.json" "$policy_sha" 4096
  [[ $(bounded stat -c '%u:%g:%a:%h' -- "$root/fixture-policy.json") == 0:0:444:1 ]] || fail policy-custody
  wanted[fixture-policy.json]=f; node_modes[fixture-policy.json]=0444; node_hashes[fixture-policy.json]=$policy_sha
  node_bytes[fixture-policy.json]=$(bounded stat -c %s -- "$root/fixture-policy.json")
 fi
 ((${#wanted[@]}<=MAX_NODES)) || fail node-bound
 # Authenticated-input batch execution is root-only. No-input prepare-only remains unchanged.
 # A private fresh namespace has no untrusted writers; failure quarantines it, never deletes inputs.
 ((EUID==0)) || fail root-fixture
 ancestor /run
 IFS= read -r uuid </proc/sys/kernel/random/uuid; uuid=${uuid//-/}
 [[ $uuid =~ ^[0-9a-f]{32}$ ]] || fail generation
 scratch=/run/appsurface-evidence-tree-audit-$uuid
 [[ ! -e $scratch && ! -L $scratch && $scratch != "$root" && $scratch != "$root/"* ]] || fail audit-scratch-collision
 bounded install -d -o 0 -g 0 -m 700 -- "$scratch"
 [[ -d $scratch && ! -L $scratch && $(bounded stat -c '%u:%g:%a' -- "$scratch") == 0:0:700 ]] || fail audit-scratch-custody
 scratch_before=$(bounded stat -c '%d:%i:%f:%u:%g' -- "$scratch")
 # Audited GNU dd explicitly creates each scratch leaf with O_NOFOLLOW/O_EXCL.
 # Any existing leaf rejects before open; the root-0700 directory has no untrusted writer.
 for path in "$scratch/pre" "$scratch/post" "$scratch/checks"; do
  [[ ! -e $path && ! -L $path ]] || fail audit-scratch-collision
 done
 for path in "$scratch/pre" "$scratch/post" "$scratch/checks"; do
  batch_create_scratch_leaf "$path"
 done
 # Reopen without truncation, retain every FD, then compare named/open identities before writes.
 exec {pre_fd}<>"$scratch/pre" {post_fd}<>"$scratch/post" {check_fd}<>"$scratch/checks"
 batch_scratch_pin "$scratch/pre" "$pre_fd" 16777216
 batch_scratch_pin "$scratch/post" "$post_fd" 16777216
 batch_scratch_pin "$scratch/checks" "$check_fd" "$MAX_MANIFEST_BYTES"
 batch_root_pin "$root" "$root_before"
 batch_snapshot "$root" "$scratch/pre" "$pre_fd"
 # Exactly one checksum batch: expected hashes derive only from authenticated inputs.
 for rel in "${!node_hashes[@]}"; do
  batch_check_time
  path=$root/$rel; checksum_bytes=$((checksum_bytes+67+${#path}))
  ((checksum_bytes<=MAX_MANIFEST_BYTES)) || fail audit-checksum-byte-bound
  printf '%s  %s\n' "${node_hashes[$rel]}" "$path" >&"$check_fd"
 done
 batch_scratch_pin "$scratch/checks" "$check_fd" "$MAX_MANIFEST_BYTES"
 for pass in pre post; do
  if [[ $pass == post ]]; then
   # A failed/missing checksum is a fixed rejection, not a partial successful audit.
   batch_root_pin "$root" "$root_before"
   bounded sha256sum --check --strict --status -- "$scratch/checks" || fail manifest-content
   batch_scratch_pin "$scratch/checks" "$check_fd" "$MAX_MANIFEST_BYTES"
   batch_root_pin "$root" "$root_before"
   batch_snapshot "$root" "$scratch/post" "$post_fd"
  fi
  seen=(); exec {read_fd}<"$scratch/$pass"
  batch_scratch_pin "$scratch/$pass" "$read_fd" 16777216
  batch_parse_snapshot "$pass" "$read_fd" "$root_name" "$sealed"
  batch_scratch_pin "$scratch/$pass" "$read_fd" 16777216
  exec {read_fd}<&-
  ((${#seen[@]}==${#wanted[@]})) || fail full-node-count
 done
 if ((sealed)) && [[ $root_name != source ]]; then
  ((total<=CORE_TREE_BYTES && ${#wanted[@]}+ancestor_nodes<=CORE_NODES)) || fail core-tree-envelope
  for rel in "${!wanted[@]}"; do
   batch_check_time
   if [[ $rel == . ]]; then depth=0; else IFS=/ read -r -a components <<<"$rel"; depth=${#components[@]}; fi
   if [[ ${wanted[$rel]} == d ]]; then ((depth<=32)) || fail core-directory-depth
   else size=${node_bytes[$rel]}; ((depth<=33 && size>0 && size<=CORE_FILE_BYTES)) || fail core-file-envelope; fi
  done
 fi
 scratch_after=$(bounded stat -c '%d:%i:%f:%u:%g' -- "$scratch")
 [[ $scratch_before == "$scratch_after" && ! -L $scratch ]] || fail audit-scratch-substitution
 exec {pre_fd}>&- {post_fd}>&- {check_fd}>&-
 # Delete only the three retained leaves and their verified private parent after success.
 bounded rm -- "$scratch/pre" "$scratch/post" "$scratch/checks"
 bounded rm -d -- "$scratch"
 pin_file "$manifest" "$expected" "$MAX_MANIFEST_BYTES"
 pin_file "$node_file" "$node_digest" "$MAX_NODE_JSON_BYTES"
 batch_root_pin "$root" "$root_before"
 remaining >/dev/null
 printf 'FULL_TREE_POINT_SAMPLES_MATCHED:%s:%s:%s\n' "$rows" "${#wanted[@]}" "$total"
}
# OS aliases ONLY: do lexical resolution ourselves and match every actually encountered link.
# No realpath membership substitution, no links accepted in deployment manifests.
normalize_os_target() {
 local path=$1 s; local -a out=(); [[ $path == /* && ${#path}<=4096 && ! $path =~ [[:cntrl:]] ]] || fail alias-target
 IFS=/ read -r -a pieces <<<"$path"
 for s in "${pieces[@]}"; do case "$s" in ''|.) ;; ..) ((${#out[@]}>0)) || fail alias-root-escape; unset "out[$((${#out[@]}-1))]" ;; *) out+=("$s");; esac; done
 ((${#out[@]}>0)) || fail alias-root-target
 local joined; joined=$(IFS=/; printf '%s' "${out[*]}"); printf '/%s' "$joined"
}
alias_walk() {
 local record=$1 literal current= seg target metadata expected lp index=0 steps=0 rest resolved
 literal=$(bounded jq -r .literal <<<"$record"); lexical "$literal"
 local -a queue=("${components[@]}")
 while ((${#queue[@]})); do
  remaining >/dev/null; steps=$((steps+1)); ((steps<=256)) || fail alias-step-bound
  seg=${queue[0]}; queue=("${queue[@]:1}"); current+=/$seg
  if [[ -L $current ]]; then
   lp=$(bounded jq -r --argjson i "$index" '.links[$i].path//""' <<<"$record"); [[ $lp == "$current" ]] || fail alias-unreviewed-link
   metadata=$(bounded stat -c '%u:%g:%a:%d:%i' -- "$current")
   expected=$(bounded jq -r --argjson i "$index" '.links[$i]|"\(.uid):\(.gid):\(.mode):\(.device):\(.inode)"' <<<"$record")
   [[ $metadata == "$expected" ]] || fail alias-lstat-pin
   target=$(bounded readlink -- "$current"); [[ $target == "$(bounded jq -r --argjson i "$index" '.links[$i].target' <<<"$record")" ]] || fail alias-target-pin
   [[ $(bounded stat -c '%u:%g:%a:%d:%i' -- "$current") == "$metadata" ]] || fail alias-link-changed
   [[ $target == /* ]] || target=${current%/*}/$target
   resolved=$(normalize_os_target "$target"); lexical "$resolved"
   queue=("${components[@]}" "${queue[@]}"); current=; index=$((index+1))
  else
   [[ -e $current && ! -L $current && $(bounded stat -c %u -- "$current") == 0 ]] || fail alias-node
   local permissions; permissions=$(bounded stat -c %a -- "$current"); (((8#$permissions & 0022)==0)) || fail alias-writable-node
   if ((${#queue[@]})); then [[ -d $current ]] || fail alias-parent; fi
  fi
 done
 [[ $index == "$(bounded jq -r '.links|length' <<<"$record")" && $current == "$(bounded jq -r .resolved_path <<<"$record")" ]] || fail alias-resolution
 [[ -f $current && ! -L $current ]] || fail alias-final-file
 printf '%s' "$current"
}
verify_os_path() {
 local p=$1 h=$2 count row resolved first before after
 count=$(bounded jq --arg p "$p" '[.aliases[]|select(.literal==$p)]|length' "$os_audit")
 if [[ $count == 1 ]]; then
  row=$(bounded jq -c --arg p "$p" '.aliases[]|select(.literal==$p)' "$os_audit")
  [[ $(bounded jq -r .resolved_sha256 <<<"$row") == "$h" ]] || fail alias-hash-binding
  first=$(alias_walk "$row"); before=$(bounded stat -c '%d:%i:%s:%f:%h:%u:%g' -- "$first")
  pin_file "$first" "$h" "$MAX_INPUT_FILE_BYTES"
  resolved=$(alias_walk "$row"); after=$(bounded stat -c '%d:%i:%s:%f:%h:%u:%g' -- "$resolved")
  [[ $first == "$resolved" && $before == "$after" ]] || fail alias-before-after
 else [[ $count == 0 ]] || fail duplicate-alias; ancestor "${p%/*}"; pin_file "$p" "$h" "$MAX_INPUT_FILE_BYTES"; fi
}

for key in source_root source_manifest source_manifest_sha256 source_nodes source_nodes_sha256 payload_root payload_manifest payload_manifest_sha256 payload_nodes payload_nodes_sha256 runtime_root runtime_manifest runtime_manifest_sha256 runtime_nodes runtime_nodes_sha256 source_review source_review_sha256 build_receipt build_receipt_sha256 os_audit os_audit_sha256 source_revision base_revision workflow_identity entry runtime_host entry_sha256 n02_uid n02_gid; do [[ -n ${!key} ]] || fail missing-input; done
[[ $source_revision =~ ^[0-9a-f]{40}$ && $base_revision =~ ^[0-9a-f]{40}$ ]] || fail revisions
[[ $workflow_identity =~ ^[A-Za-z0-9/._:@-]{1,256}$ ]] || fail workflow
[[ $entry != /* && $runtime_host != /* && $entry != *..* && $runtime_host != *..* && $runtime_host != *=* ]] || fail relative-entry-runtime
[[ $n02_uid =~ ^[0-9]+$ && $n02_gid =~ ^[0-9]+$ ]] && ((n02_uid>0 && n02_gid>0 && n02_uid<4294967295 && n02_gid<4294967295)) || fail unprivileged-selection
pin_file "$build_receipt" "$build_receipt_sha256" "$MAX_NODE_JSON_BYTES"
bounded jq -e --arg source "$source_revision" --arg sn "$source_nodes_sha256" --arg pn "$payload_nodes_sha256" --arg rn "$runtime_nodes_sha256" \
 --arg approved "$NEGATIVE_SOURCE_REVISION" --argjson source_count "$NEGATIVE_SOURCE_COUNT" --arg st "$source_manifest_sha256" --arg pt "$payload_manifest_sha256" --arg rt "$runtime_manifest_sha256" '
 .schema=="issue779-csharp-fdd-build-v5" and .exit==0 and
 .source_commit==$source and $source==$approved and
 .native_execution==false and .checkpoint_pass==false and .build_prerequisite_only==true and
 (.artifacts|type=="object" and keys==["runtime","source","tool"]) and
 .artifacts.source.nodes_sha256==$sn and .artifacts.tool.nodes_sha256==$pn and .artifacts.runtime.nodes_sha256==$rn and
 .source_before.count==$source_count and .source_after_assets.count==$source_count and .source_after_build.count==$source_count and .source_final.count==$source_count and .artifacts.source.file_count==$source_count and
 .artifacts.source.tsv_sha256==$st and .artifacts.tool.tsv_sha256==$pt and .artifacts.runtime.tsv_sha256==$rt
' "$build_receipt" >/dev/null || fail build-node-receipt-binding
diagnostic_stage=source-input
verify_tree "$source_root" "$source_manifest" "$source_manifest_sha256" "$source_nodes" "$source_nodes_sha256" source
diagnostic_stage=tool-input
verify_tree "$payload_root" "$payload_manifest" "$payload_manifest_sha256" "$payload_nodes" "$payload_nodes_sha256" tool
diagnostic_stage=runtime-input
verify_tree "$runtime_root" "$runtime_manifest" "$runtime_manifest_sha256" "$runtime_nodes" "$runtime_nodes_sha256" runtime
diagnostic_stage=os-input
pin_file "$source_review" "$source_review_sha256" 65536
pin_file "$os_audit" "$os_audit_sha256" 1048576; pin_file "$payload_root/$entry" "$entry_sha256" "$MAX_INPUT_FILE_BYTES"
[[ -f $payload_root/${entry%.dll}.deps.json && -f $payload_root/${entry%.dll}.runtimeconfig.json && ! -e $runtime_root/sdk ]] || fail normal-FDD-input
# All objects closed/typed, unique literal identities. Numeric lstat pins come from this actual OS image.
bounded jq -e '
 def hash: type=="string" and test("^[0-9a-f]{64}$");
 def path: type=="string" and startswith("/") and length<=4096 and (explode|all(.>=32 and .!=127));
 def uint: type=="number" and floor==. and .>=0 and .<=9007199254740991;
 keys==["aliases","elf","files","schema"] and .schema=="issue779-fixture-os-elf-pins-v2" and
 (.files|type=="array" and length>0 and length<=4096) and
 (.elf|type=="array" and length>0 and length<=2048) and
 (.aliases|type=="array" and length<=64) and
 all(.files[]; keys==["path","sha256"] and (.path|path) and (.sha256|hash)) and
 all(.elf[]; keys==["interpreter","path","resolved","sha256"] and (.path|path) and (.sha256|hash) and
  (.interpreter==null or (.interpreter|path)) and (.resolved|type=="array" and length<=128) and
  all(.resolved[];keys==["path","sha256","soname"] and (.path|path) and (.sha256|hash) and (.soname|type=="string" and test("^[A-Za-z0-9_.+-]{1,256}$")))) and
 all(.aliases[];keys==["links","literal","resolved_path","resolved_sha256"] and (.literal|path) and (.resolved_path|path) and (.resolved_sha256|hash) and
  (.links|type=="array" and length>0 and length<=16) and all(.links[]; keys==["device","gid","inode","mode","path","target","uid"] and
  (.path|path) and (.target|type=="string" and length>0 and length<=4096 and (explode|all(.>=32 and .!=127))) and
  .uid==0 and .gid==0 and .mode=="777" and (.device|uint) and (.inode|uint) and .inode>0)) and
 ([.files[].path]|length== (unique|length)) and ([.elf[].path]|length== (unique|length)) and
 ([.aliases[].literal]|length== (unique|length)) and
 all(.elf[]; [.resolved[].soname]|length==(unique|length))
' "$os_audit" >/dev/null || fail OS-audit-schema
os_files=$(bounded jq -r '.files[]|[.path,.sha256]|@tsv' "$os_audit")
while IFS=$'\t' read -r p h; do verify_os_path "$p" "$h"; done <<<"$os_files"
runtime_files=$(bounded find -P "$runtime_root" -type f -print)
while IFS= read -r p; do
 magic=$(bounded head -c 4 "$p" | od -An -tx1 | tr -d ' \n'); [[ $magic == 7f454c46 ]] || continue
 bounded jq -e --arg p "$p" '[.elf[]|select(.path==$p)]|length==1' "$os_audit" >/dev/null || fail unaudited-runtime-ELF
done <<<"$runtime_files"
elf_records=$(bounded jq -c '.elf[]' "$os_audit")
while IFS= read -r record; do
 p=$(bounded jq -r .path <<<"$record"); h=$(bounded jq -r .sha256 <<<"$record"); verify_os_path "$p" "$h"
 header=$(bounded readelf -h "$p"); [[ $header == *ELF64* && $header == *'Advanced Micro Devices X86-64'* ]] || fail native-ELF
 needed=$(bounded readelf -d "$p" | awk '/\(NEEDED\)/{gsub(/.*\[/,"");gsub(/\].*/,"");print}' | sort)
 expected=$(bounded jq -r '.resolved[].soname' <<<"$record" | sort); [[ $needed == "$expected" ]] || fail ELF-dependencies
 interpreter=$(bounded readelf -l "$p" | awk '/Requesting program interpreter:/{gsub(/.*interpreter: /,"");gsub(/\].*/,"");print}')
 [[ $interpreter == "$(bounded jq -r '.interpreter//""' <<<"$record")" ]] || fail ELF-interpreter
 if [[ -n $interpreter ]]; then
  bounded jq -e --arg p "$interpreter" '[.files[]|select(.path==$p)]|length==1' "$os_audit" >/dev/null || fail literal-loader-unpinned
 fi
 deps=$(bounded jq -r '.resolved[]|[.path,.sha256]|@tsv' <<<"$record")
 while IFS=$'\t' read -r dep digest; do
  [[ -n $dep ]] || continue; verify_os_path "$dep" "$digest"
  bounded jq -e --arg p "$dep" --arg h "$digest" 'any(.files[];.path==$p and .sha256==$h) or any(.elf[];.path==$p and .sha256==$h)' "$os_audit" >/dev/null || fail dependency-unpinned
 done <<<"$deps"
done <<<"$elf_records"
[[ $(bounded head -c 4 "$runtime_root/$runtime_host" | od -An -tx1 | tr -d ' \n') == 7f454c46 ]] || fail runtime-not-ELF
pin_file "$os_audit" "$os_audit_sha256" 1048576
if [[ $mode == prepare-only ]]; then printf '%s\n' 'PREPARATION_INPUT_PINS_MATCHED:N01_NOT_RUN:N02_NOT_RUN'; exit 0; fi
((EUID==0)) || fail root-fixture
sha "$reviewed_script_sha256"; absolute "${BASH_SOURCE[0]}"; pin_file "${BASH_SOURCE[0]}" "$reviewed_script_sha256" 131072
diagnostic_stage=root-layout
ancestor /var/lib; ancestor /run; ancestor /sys/fs/cgroup/system.slice
[[ $(bounded stat -f -c %t /sys/fs/cgroup/system.slice) == 63677270 ]] || fail cgroup2-parent
for root in "$source_root" "$payload_root" "$runtime_root"; do ancestor "$root"; [[ -z $(bounded find -P "$root" \( ! -uid 0 -o -perm /022 \) -print -quit) ]] || fail mutable-input; done
IFS= read -r G </proc/sys/kernel/random/uuid; G=${G//-/}; [[ $G =~ ^[0-9a-f]{32}$ ]] || fail generation
owner=appsurface-evidence-owner-$G.service; worker=appsurface-evidence-worker-$G.service
names=("evw${G:0:28}" "evs${G:0:28}" "evr${G:0:28}")
base=/var/lib/appsurface-evidence-fixture; request_base=/run/appsurface-evidence-fixture
for base_dir in "$base" "$request_base" /run/appsurface-evidence-owners; do
 if [[ ! -e $base_dir ]]; then permissions=700; [[ $base_dir != "$base" ]] || permissions=755; bounded install -d -o 0 -g 0 -m "$permissions" "$base_dir"; fi
 ancestor "$base_dir"
done
stage=$base/$G; private=$request_base/$G; guard=/run/appsurface-evidence-owners/$G
[[ ! -e $stage && ! -e $private && ! -e $guard && ! -e /run/appsurface-evidence-$G ]] || fail collision
bounded install -d -m 700 "$stage" "$private" "$guard"
log=$private/logs; bounded install -d -m 700 "$log"
launched=0; launch_pid=; observer_pid=; success=0
declare -A owned_start=()
# Child PGs are registered immediately. Wait is called ONLY after its original PID has ceased to exist.
pg_alive() { kill -0 -- "-$1" 2>/dev/null; }
owned_identity() {
 local pid=$1 text tail; local -a fields=()
 left >/dev/null || return 124
 [[ -r /proc/$pid/stat ]] || return 1
 IFS= read -r text <"/proc/$pid/stat" || return 1
 [[ ${#text} -le 4096 && ${text%% *} == "$pid" ]] || return 1
 tail=${text##*) }; read -r -a fields <<<"$tail"
 [[ ${fields[2]:-} == "$pid" && ${fields[3]:-} == "$pid" && ${fields[19]:-} =~ ^[1-9][0-9]*$ ]] || return 1
 left >/dev/null || return 124
 printf '%s' "${fields[19]}"
}
pin_owned_pg() {
 local pid=$1 fields pg sid first second
 while kill -0 "$pid" 2>/dev/null; do
  fields=$(bounded ps -o pgid=,sid= -p "$pid"); read -r pg sid <<<"$fields"
  if [[ $pg == "$pid" && $sid == "$pid" ]]; then
   first=$(owned_identity "$pid") || fail owned-group-identity
   second=$(owned_identity "$pid") || fail owned-group-identity
   [[ $first == "$second" ]] || fail owned-group-identity
   owned_start[$pid]=$first; return 0
  fi
  bounded sleep .01
 done
 fail owned-group-registration
}
signal_owned() {
 local name=$1 pid=${!1} current
 [[ -n $pid ]] || return 0
 if ! kill -0 "$pid" 2>/dev/null; then ! pg_alive "$pid"; return; fi
 [[ ${owned_start[$pid]+present} ]] || return 1
 current=$(owned_identity "$pid") || return 1
 [[ $current == "${owned_start[$pid]}" ]] || return 1
 # The live original session/group leader is checked immediately before this signal.
 kill -KILL -- "-$pid" 2>/dev/null || { ! kill -0 "$pid" 2>/dev/null && ! pg_alive "$pid"; }
}
join_owned() {
 local name=$1 pid=${!1} status current
 [[ -n $pid ]] || return 0
 while kill -0 "$pid" 2>/dev/null; do
  left >/dev/null || return 124
  [[ ${owned_start[$pid]+present} ]] || return 125
  current=$(owned_identity "$pid") || return 125
  [[ $current == "${owned_start[$pid]}" ]] || return 125
  try_bounded sleep .05 || return 124
 done
 status=0; wait "$pid" || status=$?
 pg_alive "$pid" && return 125
 # A fully settled registration is retired independently of its preserved exit status.
 unset 'owned_start[$pid]'; printf -v "$name" '%s' ''
 return "$status"
}
cleanup() {
 local original=$? bad=0 name status=0 cg unit
 trap - EXIT INT TERM; phase=cleanup
 # Signals attempted even at the deadline; no new allowance is ever manufactured.
 for name in observer_pid launch_pid; do signal_owned "$name" || bad=1; done
 # Reap registered fixture processes before potentially slow systemd cleanup requests.
 for name in observer_pid launch_pid; do join_owned "$name" || bad=1; done
 if ((launched)); then
  try_bounded systemctl kill --kill-whom=all --signal=KILL "$worker" "$owner" >"$log/kill.log" 2>&1 || :
  try_bounded systemctl stop "$worker" "$owner" >"$log/stop.log" 2>&1 || :
 fi
 for unit in "$worker" "$owner"; do
  cg=/sys/fs/cgroup/system.slice/$unit
  if [[ -e $cg ]]; then
   [[ ! -L $cg ]] && [[ $(try_bounded stat -f -c %t "$cg") == 63677270 ]] && try_bounded grep -qx 'populated 0' "$cg/cgroup.events" || bad=1
  fi
 done
 left >/dev/null || bad=1
 if ((original!=0 || bad!=0 || success!=1)); then printf '%s\n' 'NATIVE_FIXTURE_FAILED:PATHS_AND_ACCOUNTS_PRESERVED'; exit 1; fi
 # Publication happens ONLY after joins/group checks and final original deadline check.
 try_bounded jq -jcS -n --arg g "$G" --arg source "$source_revision" --arg base "$base_revision" --arg case "$NEGATIVE_CASE" --argjson root_exit "$negative_launch" --arg policy "$policy_sha" '{schema:"issue779-negative-slot-fixture-v1",generation:$g,source_revision:$source,base_revision:$base,control:$case,root_launch_join_status:$root_exit,root_stdout_bytes:0,worker_terminal_from_original_monitor:true,original_csharp_joins_required:true,account_disposition:"preserved-quarantined",native_acceptance:false,observation_only:true,policy_sha256:$policy,other_controls:"not-qualified-by-this-fixture"}' >"$private/fixture-result.pending" || status=1
 left >/dev/null || status=1
 ((status==0)) || { printf '%s\n' 'NATIVE_FIXTURE_FAILED:PUBLICATION'; exit 1; }
 try_bounded mv -T -- "$private/fixture-result.pending" "$private/fixture-result.json" || exit 1
 left >/dev/null || { printf '%s\n' 'NATIVE_FIXTURE_FAILED:LATE_PUBLICATION'; exit 1; }
 printf '%s\n' 'NEGATIVE_SLOT_OBSERVATION_TERMINAL:NOT_ACCEPTANCE'
 left >/dev/null || { printf '%s\n' 'NATIVE_FIXTURE_FAILED:LATE_TERMINAL_OUTPUT'; exit 1; }
 exit 0
}
trap cleanup EXIT; trap 'exit 1' INT TERM
copy_tree() {
 local input=$1 dest=$2
 bounded install -d -m 700 "$dest"; bounded cp -a --no-preserve=ownership -- "$input/." "$dest/"
 bounded chown -R 0:0 "$dest"
 bounded find -P "$dest" -type f ! -perm /111 -exec chmod 444 {} +
 bounded find -P "$dest" -type f -perm /111 -exec chmod 555 {} +
 bounded find -P "$dest" -type d -exec chmod 555 {} +
}
diagnostic_stage=tool-copy; copy_tree "$payload_root" "$stage/tool"
diagnostic_stage=runtime-copy; copy_tree "$runtime_root" "$stage/runtime"
diagnostic_stage=subject-copy; copy_tree "$source_root" "$stage/subject"
diagnostic_stage=request-preparation
bounded chmod 555 "$stage"
[[ $(bounded stat -c %a "$base") == 755 ]] || fail deployment-parent
[[ -f $stage/subject/docs/designs/issue-779-csharp-supervision-core.md ]] || fail changed-path-source
host=$stage/runtime/$runtime_host; managed=$stage/tool/$entry
[[ $host != *'$'* && $host != *'%'* && $host != *'='* && $managed != *'$'* && $managed != *'%'* ]] || fail bootstrap-path
pin_file "$managed" "$entry_sha256" "$MAX_INPUT_FILE_BYTES"
[[ ! -e $stage/tool/fixture-policy.json ]] || fail policy-collision
bounded chmod 755 "$stage/tool"
bounded jq -jcS -n '{Id:"checkpoint-empty",Version:"1",ConservativeProfileId:"conservative",Profiles:[{Id:"empty",Scope:"Targeted",Resources:[],Producers:[],Obligations:[]},{Id:"conservative",Scope:"Release",Resources:[{Id:"build",Readiness:"completion",DeadlineSeconds:1,Requires:[]}],Producers:[],Obligations:[]}],Rules:[{Id:"docs",Pattern:"docs/**",ProfileId:"empty",Precedence:0}]}' >"$stage/tool/fixture-policy.json"
bounded chmod 444 "$stage/tool/fixture-policy.json"; bounded chmod 555 "$stage/tool"
policy_sha=$(bounded sha256sum "$stage/tool/fixture-policy.json"); policy_sha=${policy_sha:0:64}
verify_tree "$stage/tool" "$payload_manifest" "$payload_manifest_sha256" "$payload_nodes" "$payload_nodes_sha256" tool 1 1
verify_tree "$stage/runtime" "$runtime_manifest" "$runtime_manifest_sha256" "$runtime_nodes" "$runtime_nodes_sha256" runtime 1 0
verify_tree "$stage/subject" "$source_manifest" "$source_manifest_sha256" "$source_nodes" "$source_nodes_sha256" source 1 0
verify_tree "$source_root" "$source_manifest" "$source_manifest_sha256" "$source_nodes" "$source_nodes_sha256" source
verify_tree "$payload_root" "$payload_manifest" "$payload_manifest_sha256" "$payload_nodes" "$payload_nodes_sha256" tool
verify_tree "$runtime_root" "$runtime_manifest" "$runtime_manifest_sha256" "$runtime_nodes" "$runtime_nodes_sha256" runtime
# UTC is sampled once and capped inside the SAME monotonic work interval, not reset after N02.
job_deadline_epoch=$(( $(bounded date -u +%s) + $(remaining) - 1 ))
job_deadline=$(bounded date -u -d "@$job_deadline_epoch" +%Y-%m-%dT%H:%M:%SZ)
request=$private/request.json
bounded jq -jcS -n --arg tool "$stage/tool" --arg runtime "$stage/runtime" --arg host "$host" --arg entry "$managed" --arg subject "$stage/subject" --arg base "$base_revision" --arg source "$source_revision" --arg workflow "$workflow_identity" --arg deadline "$job_deadline" '{schema:"evidence-supervisor-linux-v1",mode:"observation",tool_root:$tool,runtime_root:$runtime,runtime_host:$host,entry_path:$entry,policy_file:($tool+"/fixture-policy.json"),subject_root:$subject,base_revision:$base,subject_revision:$source,workflow_identity:$workflow,paths:["docs/designs/issue-779-csharp-supervision-core.md"],observation_profile_ids:["empty"],observation_producer_ids:[],job_deadline_utc:$deadline,admission_seconds:10,start_seconds:30,collection_seconds:10,cleanup_seconds:60,stopping_seconds:5}' >"$request"
bounded chmod 600 "$request"; [[ $(bounded stat -c '%u:%g:%a:%h' "$request") == 0:0:600:1 && $(bounded stat -c %s "$request") -le 65536 ]] || fail request-shape
bounded jq -e 'length==20' "$request" >/dev/null || fail request-fields
bounded sha256sum "$request" "$stage/tool/fixture-policy.json" >"$private/request-policy.sha256"
absent_accounts() {
 local n code
 for n in "${names[0]}" "${names[1]}"; do code=0; try_bounded getent passwd "$n" >/dev/null || code=$?; [[ $code == 2 ]] || fail NSS-passwd-not-absent; remaining >/dev/null; done
 for n in "${names[@]}"; do code=0; try_bounded getent group "$n" >/dev/null || code=$?; [[ $code == 2 ]] || fail NSS-group-not-absent; remaining >/dev/null; done
}
unit_absent() { local state; state=$(bounded systemctl show "$1" -p LoadState --value); [[ $state == not-found ]] || fail unit-collision; }
absent_accounts; unit_absent "$owner"; unit_absent "$worker"
diagnostic_stage=n02
set +e
(ulimit -f 262144; ulimit -c 0; try_bounded strace -f -qq -e trace=openat,openat2,mkdir,mkdirat,unlink,unlinkat,rmdir,connect -o "$log/n02.trace" setpriv --reuid="$n02_uid" --regid="$n02_gid" --clear-groups /usr/bin/env -i PATH=/usr/bin:/bin HOME=/nonexistent LANG=C.UTF-8 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_CLI_HOME=/tmp "$host" "$managed" evidence supervise --request "$request") >"$log/n02.stdout" 2>"$log/n02.stderr"
n02=$?; set -e; remaining >/dev/null
[[ $n02 == 1 && ! -s $log/n02.stdout ]] || fail N02-outcome
bounded grep -q '^ASEVD402:' "$log/n02.stderr" || fail N02-error
[[ $(bounded stat -c %s "$log/n02.stderr") -le 1024 && $(bounded stat -c %s "$log/n02.trace") -le 1048576 ]] || fail N02-log-bound
if try_bounded grep -F -e "$private" -e "/run/appsurface-evidence-$G" -e "$guard" -e /run/dbus/system_bus_socket "$log/n02.trace" >/dev/null; then fail N02-protected-IO; else code=$?; [[ $code == 1 ]] || fail N02-trace-inspection; fi
if try_bounded grep -F -e "$request" -e "$stage" "$log/n02.stderr" >/dev/null; then fail N02-path-leak; else code=$?; [[ $code == 1 ]] || fail N02-error-inspection; fi
absent_accounts; [[ ! -e /run/appsurface-evidence-$G ]] || fail N02-workspace; unit_absent "$owner"; unit_absent "$worker"
# Pure data guards use bounded reads; the original fixture owns all commands/deadlines.
n03_validate_event_data() {
 local count=$1 file=$2 event_keys
 case "$count" in
  1) event_keys='[["broker_gid","broker_pid","broker_start_ticks","broker_uid","schema","stage"]]' ;;
  3) event_keys='[["broker_gid","broker_pid","broker_start_ticks","broker_uid","schema","stage"],["identity_kind","peer_gid","peer_pid","peer_uid","schema","stage"],["peer_pid","request_bytes","schema","sent_bytes","stage"]]' ;;
  4) event_keys='[["broker_gid","broker_pid","broker_start_ticks","broker_uid","schema","stage"],["identity_kind","peer_gid","peer_pid","peer_uid","schema","stage"],["peer_pid","request_bytes","schema","sent_bytes","stage"],["native_acceptance","peer_pid","request_bytes","schema","sent_bytes","status"]]' ;;
  *) return 1 ;;
 esac
 [[ $(bounded stat -c %s -- "$file") -le 4096 ]] || return 1
 # ASCII is the fixed helper's JSON encoding; limits include each terminal LF.
 bounded head -c 4097 -- "$file" | bounded jq -Rse --argjson count "$count" 'split("\n") as $lines | ($lines|length)==($count+1) and $lines[-1]=="" and all($lines[0:$count][]; length>0 and length<=1023 and startswith("{") and endswith("}") and (explode|all(.>=32 and .<=126)))' >/dev/null || return 1 # N03-raw-lines
 # --stream preserves duplicate leaf paths before ordinary object parsing collapses them.
 bounded head -c 4097 -- "$file" | bounded jq --stream -se --argjson keys "$event_keys" 'def flat($wanted): length==(($wanted|length)+1) and (.[-1]|length)==1 and (.[-1][0]|type)=="array" and (.[-1][0]|length)==1 and .[-1][0]==.[-2][0] and all(.[0:-1][]; length==2 and (.[0]|type)=="array" and (.[0]|length)==1 and (.[0][0]|type)=="string" and ((.[1]|type)=="string" or (.[1]|type)=="number" or (.[1]|type)=="boolean" or (.[1]|type)=="null")) and ([.[0:-1][]|.[0][0]]|sort)==$wanted; reduce .[] as $event ({done:[],pending:[]}; if ($event|length)==2 then .pending+=[$event] else .done+=[(.pending+[$event])] | .pending=[] end) | . as $state | ($state.pending|length)==0 and ($state.done|length)==($keys|length) and all(range(0;($keys|length)); . as $i | $state.done[$i] | flat($keys[$i]))' >/dev/null || return 1 # N03-flat-stream
 [[ $(bounded stat -c %s -- "$file") -le 4096 ]] || return 1
}
n03_run() (
 set -euo pipefail; umask 077
 n03_fd_owner_pid=$BASHPID; readonly n03_fd_owner_pid
 [[ $# == 11 && $EUID == 0 && $source_revision == "$NEGATIVE_SOURCE_REVISION" ]] || fail N03-input
 helper_root=$1 helper_map=$2 helper_map_sha=$3 helper_nodes=$4 helper_nodes_sha=$5 helper_entry_sha=$6
 broker_name=$7 worker_name=$8 shared_name=$9 startup_kib=${10} helper_generation=${11}
 n03_broker_pid= n03_worker_pid= release_fd= dir_fd= socket_pin= first_failure= result_status=rejected
 n03_startup_active=0
 broker_exit= worker_exit=0 sampled_pid= sampled_start= broker_actual_pid= trace_peer_seen=0 observed=0
 broker_pw= worker_pw= group_row= bu= bg= wu= wg= gu= gid= peer_pid= candidate= broker_ready= peer_ready= eof_ready=
 parent=/run/appsurface-evidence-n03-peers n03dir= n03log= sock= before_dir= after_dir= current_cgroup=
 declare -gA owned_start=()
 n03dir=$parent/$G; n03log=$log/n03; sock=$n03dir/broker/control.sock
 n03_cleanup() {
  local original=$? bad=0 s name code=0
  trap - EXIT INT TERM; n03_startup_active=0; phase=cleanup
  # Root release is never a verdict. Closing the previously empty anonymous pipe follows R+LF.
  if [[ -n $release_fd ]]; then printf 'R\n' >&"$release_fd" || bad=1; exec {release_fd}>&- || bad=1; release_fd=; fi
  for name in n03_worker_pid n03_broker_pid; do
   if [[ -n ${!name} ]]; then signal_owned "$name" || bad=1; fi
  done
  for name in n03_worker_pid n03_broker_pid; do
   if [[ -n ${!name} ]]; then join_owned "$name" || bad=1; fi
  done
  if [[ -n $dir_fd ]]; then
   s=$(try_bounded stat -Lc '%d:%i:%u:%g:%a' "/proc/$n03_fd_owner_pid/fd/$dir_fd") || bad=1
   [[ $s == "$before_dir" ]] || bad=1; exec {dir_fd}<&- || bad=1
  fi
  left >/dev/null || bad=1
  if ((original!=0 || bad!=0 || observed!=1)); then
   # Failure/quarantine: no account/socket/path deletion, no successful containment assertion.
   try_bounded jq -jcS -n --arg status "$result_status" --argjson worker "${sampled_pid:-null}" --argjson broker "${broker_actual_pid:-null}" '{schema:"issue779-n03-peer-observation-v1",control:"N03",status:$status,worker_pid:$worker,broker_pid:$broker,worker_exit:null,broker_exit:null,request_bytes:null,sent_bytes:null,owned_process_groups_joined:null,native_acceptance:false,systemd_worker_authority:false}' >"$n03log/failure.pending" || :
   if left >/dev/null; then try_bounded mv -T -- "$n03log/failure.pending" "$n03log/failure.json" || :; fi
   printf 'N03_INCONCLUSIVE_OR_REJECTED:RETAINED\n' >&2; exit 1
  fi
  try_bounded jq -jcS -n --arg source "$source_revision" --argjson worker "$sampled_pid" --argjson broker "$broker_actual_pid" \
   '{schema:"issue779-n03-peer-observation-v1",control:"N03",status:"observed",source_commit:$source,worker_pid:$worker,broker_pid:$broker,worker_exit:1,broker_exit:0,worker_stdout_bytes:0,asevd402_observed:true,kernel_peer_bound_to_owned_worker:true,request_bytes:0,sent_bytes:0,ready_request_observed:false,descriptor_supplied:false,owned_process_groups_joined:true,stream_custody:"private-files-after-original-process-reap",native_acceptance:false,systemd_worker_authority:false}' >"$n03log/result.pending" || code=1
  left >/dev/null || code=1; ((code==0)) || exit 1
  try_bounded mv -T -- "$n03log/result.pending" "$n03log/result.json" || exit 1
  left >/dev/null || exit 1
  printf 'N03_OBSERVATION_TERMINAL:NOT_NATIVE_ACCEPTANCE\n'
  left >/dev/null || exit 1; exit 0
 }
 trap n03_cleanup EXIT; trap 'exit 1' INT TERM
 [[ $startup_kib =~ ^[1-9][0-9]*$ ]] && ((startup_kib<=262144)) || fail N03-file-limit
 # No expiry is created here. The helper uses work_end*1000, exactly the inherited uptime domain.
 remaining >/dev/null; ((hard_end-work_end==30 && hard_end-fixture_start==600)) || fail N03-clock-binding
 n03_startup_end=; n03_startup_end=$(monotonic) || fail N03-clock-binding
 n03_startup_end=$((n03_startup_end+15)); ((n03_startup_end<=work_end)) || n03_startup_end=$work_end
 n03_startup_check() { local now; now=$(monotonic) || fail N03-clock-binding; ((now<n03_startup_end && now<work_end)) || fail N03-startup-bound; }
 n03_startup_active=1; n03_startup_check
 [[ $broker_name == _apt && $worker_name == nobody && $shared_name == nogroup ]] || fail N03-fixed-NSS-names
 [[ $broker_name != "$worker_name" ]] || fail N03-distinct-users
 broker_pw=$(bounded getent passwd "$broker_name"); worker_pw=$(bounded getent passwd "$worker_name"); group_row=$(bounded getent group "$shared_name")
 [[ $broker_pw != *$'\n'* && $worker_pw != *$'\n'* && $group_row != *$'\n'* ]] || fail N03-NSS-ambiguous
 IFS=: read -r bn bx bu bg bge bh bs extra <<<"$broker_pw"; [[ $bn == "$broker_name" && -z ${extra:-} ]] || fail N03-NSS-broker
 IFS=: read -r wn wx wu wg wge wh ws extra <<<"$worker_pw"; [[ $wn == "$worker_name" && -z ${extra:-} ]] || fail N03-NSS-worker
 IFS=: read -r gn gx gid members extra <<<"$group_row"; [[ $gn == "$shared_name" && -z ${extra:-} ]] || fail N03-NSS-group
 for x in "$bu" "$wu" "$bg" "$wg" "$gid"; do [[ $x =~ ^[1-9][0-9]*$ ]] && ((x<4294967295)) || fail N03-NSS-id; done
 [[ $bu != "$wu" && $bg == 65534 && $wg == 65534 && $gid == 65534 ]] || fail N03-NSS-relationship
 # Separate complete bundle. Root transport seals it; this procedure never copies into product/tool.
 ancestor "$helper_root"; [[ $helper_generation =~ ^[0-9a-f]{32}$ && $helper_root == /var/lib/appsurface-evidence-n03-helpers/$helper_generation/bundle ]] || fail N03-helper-namespace
 hentry=$helper_root/NativePeerBroker.dll
 verify_tree "$helper_root" "$helper_map" "$helper_map_sha" "$helper_nodes" "$helper_nodes_sha" tool 1 0
 pin_file "$hentry" "$helper_entry_sha" "$CORE_FILE_BYTES"
 p=$helper_root perms=
 while :; do
  perms=$(bounded stat -c %a -- "$p"); (((8#$perms & 0001)!=0)) || fail N03-helper-not-searchable
  [[ $p == / ]] && break; p=${p%/*}; [[ -n $p ]] || p=/
 done
 [[ ! -e $n03dir && ! -L $n03dir && ! -e $n03log && ! -L $n03log ]] || fail N03-collision
 if [[ ! -e $parent ]]; then bounded install -d -o 0 -g 0 -m 755 "$parent"; fi
 ancestor "$parent"; [[ $(bounded stat -c '%u:%g:%a' "$parent") == 0:0:755 ]] || fail N03-parent
 bounded install -d -o 0 -g "$gid" -m 710 "$n03dir"
 bounded install -d -o "$bu" -g "$gid" -m 710 "$n03dir/broker"
 bounded install -d -o 0 -g 0 -m 700 "$n03log"
 exec {dir_fd}<"$n03dir/broker"
 before_dir=$(bounded stat -c '%d:%i:%u:%g:%a' "$n03dir/broker")
 [[ $(bounded stat -Lc '%d:%i:%u:%g:%a' "/proc/$n03_fd_owner_pid/fd/$dir_fd") == "$before_dir" ]] || fail N03-retained-parent
 current_cgroup=$(bounded cat /proc/self/cgroup)
 [[ $current_cgroup =~ ^0::/system.slice/issue779-native-tool-[0-9a-f]{32}-[0-9]+\.service$ ]] || fail N03-root-containment
 # Actual ELF/loader/entry/root before-interpreter audit belongs to reviewed root caller, not this JSON.
 fixed_env=(PATH=/usr/bin:/bin HOME=/nonexistent LANG=C.UTF-8 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_CLI_HOME=/tmp)
 [[ $(bounded getent passwd "$broker_name") == "$broker_pw" && $(bounded getent passwd "$worker_name") == "$worker_pw" && $(bounded getent group "$shared_name") == "$group_row" ]] || fail N03-NSS-drift
 n03_startup_check
 exec {release_fd}> >(
  exec {dir_fd}<&-
  ulimit -f "$startup_kib"; ulimit -c 0
  exec setsid setpriv --no-new-privs --reuid="$bu" --regid="$gid" --clear-groups \
   /usr/bin/env -i "${fixed_env[@]}" "$host" "$hentry" --socket "$sock" --broker-uid "$bu" --broker-gid "$gid" \
   --worker-uid "$wu" --worker-gid "$gid" --deadline-uptime-ms "$((work_end*1000))" >"$n03log/broker.events.jsonl" 2>"$n03log/broker.stderr"
 )
 n03_broker_pid=$!; pin_owned_pg "$n03_broker_pid"
 # Direct private file sinks: no pump task can be credited; actual reap and PG absence remain mandatory.
 while :; do
  n03_startup_check; remaining >/dev/null
  if [[ -s $n03log/broker.events.jsonl ]]; then
   [[ $(bounded stat -c %s -- "$n03log/broker.events.jsonl") -le 4096 ]] || fail N03-event-bound
   broker_ready=$(bounded head -c 1025 -- "$n03log/broker.events.jsonl")
   if [[ -n ${broker_ready:-} ]]; then break; fi
  fi
  kill -0 "$n03_broker_pid" 2>/dev/null || fail N03-broker-before-ready; bounded sleep .01
 done
 [[ ${#broker_ready} -le 1024 ]] || fail N03-event-bound
 n03_validate_event_data 1 "$n03log/broker.events.jsonl" || fail N03-ready
 bounded jq -e --argjson uid "$bu" --argjson gid "$gid" 'keys==["broker_gid","broker_pid","broker_start_ticks","broker_uid","schema","stage"] and .schema=="issue779-n03-managed-broker-event-v1" and .stage=="ready" and .broker_uid==$uid and .broker_gid==$gid and (.broker_pid|type)=="number" and .broker_pid>0 and .broker_pid<=2147483647 and (.broker_pid|floor)==.broker_pid and (.broker_start_ticks|type)=="number" and .broker_start_ticks>0 and .broker_start_ticks<=9007199254740991 and (.broker_start_ticks|floor)==.broker_start_ticks' <<<"$broker_ready" >/dev/null || fail N03-ready
 broker_actual_pid=$(bounded jq -r .broker_pid <<<"$broker_ready")
 [[ $broker_actual_pid == "$n03_broker_pid" ]] || fail N03-broker-exec-chain
 n03_sample() {
  local pid=$1 uid=$2 entry=$3 output=$4 parent_pid=$5 text tail s1 s2 u1 g1 u2 g2 cg1 cg2 exe1 groups argfile status1 status2 nnp1 nnp2 cap1 cap2
  local -a f=() args=()
  [[ -r /proc/$pid/stat && -r /proc/$pid/status ]] || return 1
  IFS= read -r text <"/proc/$pid/stat" || return 1; [[ ${text%% *} == "$pid" ]] || return 1
  tail=${text##*) }; read -r -a f <<<"$tail"; s1=${f[19]:-}
  [[ $s1 =~ ^[1-9][0-9]*$ ]] || return 1
  [[ ${f[1]:-} == "$parent_pid" ]] || return 1
  if [[ $entry == "$managed" ]]; then [[ ${f[2]:-} == "$parent_pid" && ${f[3]:-} == "$parent_pid" ]] || return 1; else [[ ${f[2]:-} == "$pid" && ${f[3]:-} == "$pid" ]] || return 1; fi
  status1=$(bounded head -c 16385 "/proc/$pid/status"); ((${#status1}<=16384)) || return 1
  u1=$(bounded awk '$1=="Uid:"{print $2":"$3":"$4":"$5}' <<<"$status1")
  g1=$(bounded awk '$1=="Gid:"{print $2":"$3":"$4":"$5}' <<<"$status1")
  groups=$(bounded awk '$1=="Groups:"{for(i=2;i<=NF;i++)print $i}' <<<"$status1")
  nnp1=$(bounded awk '$1=="NoNewPrivs:"{print $2}' <<<"$status1")
  cap1=$(bounded awk '$1=="CapEff:"{print $2}' <<<"$status1")
  [[ $nnp1 == 1 && $cap1 =~ ^0{16}$ ]] || return 1
  [[ $u1 == "$uid:$uid:$uid:$uid" && $g1 == "$gid:$gid:$gid:$gid" && -z $groups ]] || return 1
  cg1=$(bounded cat "/proc/$pid/cgroup"); exe1=$(bounded readlink "/proc/$pid/exe")
  [[ $cg1 == "$current_cgroup" && $exe1 == "$host" ]] || return 1
  [[ $(bounded stat -Lc '%d:%i:%s:%f' "/proc/$pid/exe") == "$(bounded stat -c '%d:%i:%s:%f' "$host")" ]] || return 1
  local actual_hash expected_hash
  actual_hash=$(bounded sha256sum "/proc/$pid/exe"); expected_hash=$(bounded sha256sum "$host"); [[ ${actual_hash:0:64} == "${expected_hash:0:64}" ]] || return 1
  argfile=$output.argv; bounded head -c 16385 "/proc/$pid/cmdline" >"$argfile"
  (( $(bounded stat -c %s "$argfile")<=16384 )) || return 1
  while IFS= read -r -d '' x; do args+=("$x"); ((${#args[@]}<=14)) || return 1; done <"$argfile"
  if [[ $entry == "$managed" ]]; then
   [[ ${#args[@]} == 6 && ${args[0]} == "$host" && ${args[1]} == "$managed" && ${args[2]} == evidence && ${args[3]} == worker && ${args[4]} == --control && ${args[5]} == "$sock" ]] || return 1
  else
   local -a expected=("$host" "$hentry" --socket "$sock" --broker-uid "$bu" --broker-gid "$gid" --worker-uid "$wu" --worker-gid "$gid" --deadline-uptime-ms "$((work_end*1000))")
   [[ ${#args[@]} == ${#expected[@]} ]] || return 1
   for ((x=0;x<${#args[@]};x++)); do [[ ${args[x]} == "${expected[x]}" ]] || return 1; done
  fi
  status2=$(bounded head -c 16385 "/proc/$pid/status"); ((${#status2}<=16384)) || return 1
  u2=$(bounded awk '$1=="Uid:"{print $2":"$3":"$4":"$5}' <<<"$status2"); g2=$(bounded awk '$1=="Gid:"{print $2":"$3":"$4":"$5}' <<<"$status2"); cg2=$(bounded cat "/proc/$pid/cgroup")
  nnp2=$(bounded awk '$1=="NoNewPrivs:"{print $2}' <<<"$status2")
  cap2=$(bounded awk '$1=="CapEff:"{print $2}' <<<"$status2")
  [[ $nnp2 == 1 && $cap2 =~ ^0{16}$ && $nnp1 == "$nnp2" && $cap1 == "$cap2" ]] || return 1
  IFS= read -r text <"/proc/$pid/stat" || return 1; tail=${text##*) }; read -r -a f <<<"$tail"; s2=${f[19]:-}
  [[ $s1 == "$s2" && $u1 == "$u2" && $g1 == "$g2" && $cg1 == "$cg2" && $(bounded readlink "/proc/$pid/exe") == "$host" ]] || return 1
  bounded jq -jcS -n --argjson pid "$pid" --arg ticks "$s1" --arg uid "$u1" --arg gid "$g1" --arg cg "$cg1" '{schema:"issue779-n03-owned-image-sample-v1",pid:$pid,start_ticks:$ticks,uid4:$uid,gid4:$gid,cgroup:$cg,exact_managed_argv:true,image_pinned:true,empty_groups:true,no_new_privs:1,effective_capabilities_zero:true}' >"$output"
  remaining >/dev/null
 }
 # Broker image has execed through env/setpriv in the original registered session.
 broker_parent=; broker_parent=$(bounded awk '$1=="PPid:"{print $2}' "/proc/$broker_actual_pid/status")
 [[ $broker_parent == "$BASHPID" ]] || fail N03-broker-parent
 n03_sample "$broker_actual_pid" "$bu" "$hentry" "$n03log/broker-live.json" "$broker_parent" || fail N03-broker-live
 [[ $(bounded jq -r .start_ticks "$n03log/broker-live.json") == "$(bounded jq -r .broker_start_ticks <<<"$broker_ready")" ]] || fail N03-broker-start
 [[ -S $sock && ! -L $sock && $(bounded stat -c '%u:%g:%a:%h' "$sock") == "$bu:$gid:660:1" ]] || fail N03-socket
 socket_pin=$(bounded stat -c '%d:%i:%u:%g:%a' "$sock")
 n03_startup_check
 (exec {dir_fd}<&-; exec {release_fd}>&-; ulimit -f "$startup_kib"; ulimit -c 0
  exec setsid strace -ff -qq -s 16384 -e trace=execve,getsockopt,connect,setresuid,setresgid,getuid,getgid,geteuid,getegid \
   -o "$n03log/worker.trace" setpriv --no-new-privs --reuid="$wu" --regid="$gid" --clear-groups /usr/bin/env -i "${fixed_env[@]}" "$host" "$managed" evidence worker --control "$sock"
 ) >"$n03log/worker.stdout" 2>"$n03log/worker.stderr" & n03_worker_pid=$!
 pin_owned_pg "$n03_worker_pid"
 # Discover ONLY the direct actual child of this owned tracer; no arbitrary PID or fallback tuple.
 while kill -0 "$n03_worker_pid" 2>/dev/null; do
  n03_startup_check; remaining >/dev/null
  if [[ -r /proc/$n03_worker_pid/task/$n03_worker_pid/children ]]; then
   children=; IFS= read -r children <"/proc/$n03_worker_pid/task/$n03_worker_pid/children" || :
   read -r candidate extra <<<"$children"
   if [[ $candidate =~ ^[1-9][0-9]*$ && -z ${extra:-} ]]; then
    if n03_sample "$candidate" "$wu" "$managed" "$n03log/worker-live.json" "$n03_worker_pid"; then
     sampled_pid=$candidate; sampled_start=$(bounded jq -r .start_ticks "$n03log/worker-live.json"); break
    fi
   fi
  fi
  bounded sleep .01
 done
 n03_startup_check
 [[ -n $sampled_pid ]] || { result_status=inconclusive; fail N03-inconclusive-worker-live; }
 worker_exit=0; join_owned n03_worker_pid || worker_exit=$?
 [[ $worker_exit == 1 && -z $n03_worker_pid && ! -s $n03log/worker.stdout ]] || fail N03-worker-outcome
 bounded grep -q '^ASEVD402:' "$n03log/worker.stderr" || fail N03-worker-diagnostic
 [[ $(bounded stat -c %s "$n03log/worker.stderr") -le 1024 ]] || fail N03-worker-stderr-bound
 bounded grep -Eq "getsockopt\\([0-9]+, SOL_SOCKET, SO_PEERCRED, \\{pid=$broker_actual_pid, uid=$bu, gid=$gid\\}, \\[12\\]\\) += 0" "$n03log/worker.trace.$sampled_pid" || fail N03-inconclusive-peer-trace
 exec_line=; exec_line=$(bounded grep -F "execve(\"$host\", [\"$host\", \"$managed\", \"evidence\", \"worker\", \"--control\", \"$sock\"]," "$n03log/worker.trace.$sampled_pid")
 [[ $exec_line != *$'\n'* && $exec_line == *' = 0' ]] || fail N03-inconclusive-exec-trace
 trace_peer_seen=1
 bounded cp --no-dereference -- "$n03log/worker.trace.$sampled_pid" "$n03log/worker-peer.trace"
 t1= t2=; t1=$(bounded sha256sum "$n03log/worker-peer.trace"); t2=$(bounded sha256sum "$n03log/worker.trace.$sampled_pid"); [[ ${t1:0:64} == "${t2:0:64}" ]] || fail N03-trace-copy
 [[ $(bounded stat -c %s "$n03log/worker.trace.$sampled_pid") -le 1048576 ]] || fail N03-trace-bound
 # Closed exact four lines; extra event/keys/bytes are rejected. Helper exit is not authority.
 while :; do
  remaining >/dev/null
  [[ $(bounded stat -c %s -- "$n03log/broker.events.jsonl") -le 4096 ]] || fail N03-event-bound
  if (( $(bounded head -c 4097 -- "$n03log/broker.events.jsonl" | bounded wc -l)>=3 )); then break; fi
  kill -0 "$n03_broker_pid" 2>/dev/null || fail N03-broker-before-EOF; bounded sleep .01
 done
 n03_validate_event_data 3 "$n03log/broker.events.jsonl" || fail N03-peer-binding
 bounded jq -se --argjson peer "$sampled_pid" --argjson uid "$wu" --argjson gid "$gid" \
  'length==3 and (.[1]|keys)==["identity_kind","peer_gid","peer_pid","peer_uid","schema","stage"] and .[1].schema=="issue779-n03-managed-broker-event-v1" and .[1].stage=="peer" and .[1].peer_pid==$peer and .[1].peer_uid==$uid and .[1].peer_gid==$gid and .[1].identity_kind=="kernel-so-peercred-sample" and (.[2]|keys)==["peer_pid","request_bytes","schema","sent_bytes","stage"] and .[2].schema=="issue779-n03-managed-broker-event-v1" and .[2].stage=="eof" and .[2].peer_pid==$peer and .[2].request_bytes==0 and .[2].sent_bytes==0' "$n03log/broker.events.jsonl" >/dev/null || fail N03-peer-binding
 after_dir=$(bounded stat -c '%d:%i:%u:%g:%a' "$n03dir/broker"); [[ $after_dir == "$before_dir" && $(bounded stat -c '%d:%i:%u:%g:%a' "$sock") == "$socket_pin" ]] || fail N03-socket-substitution
 n03_sample "$broker_actual_pid" "$bu" "$hentry" "$n03log/broker-live-after.json" "$broker_parent" || fail N03-broker-continuity
 [[ $(bounded jq -r .start_ticks "$n03log/broker-live-after.json") == "$(bounded jq -r .start_ticks "$n03log/broker-live.json")" ]] || fail N03-broker-continuity
 # Original work allowance resumes only after owned worker exit plus authenticated peer/EOF and broker continuity.
 n03_startup_active=0
 printf 'R\n' >&"$release_fd"; exec {release_fd}>&-; release_fd=
 broker_exit=0; join_owned n03_broker_pid || broker_exit=$?
 [[ $broker_exit == 0 && -z $n03_broker_pid && ! -s $n03log/broker.stderr ]] || fail N03-broker-outcome
 n03_validate_event_data 4 "$n03log/broker.events.jsonl" || fail N03-broker-result
 bounded jq -se --argjson peer "$sampled_pid" 'length==4 and (.[3]|keys)==["native_acceptance","peer_pid","request_bytes","schema","sent_bytes","status"] and .[3].schema=="issue779-n03-managed-broker-result-v1" and .[3].status=="observed" and .[3].peer_pid==$peer and .[3].request_bytes==0 and .[3].sent_bytes==0 and .[3].native_acceptance==false' "$n03log/broker.events.jsonl" >/dev/null || fail N03-broker-result
 [[ $(bounded stat -c %s "$n03log/broker.events.jsonl") -le 4096 ]] || fail N03-event-bound
 [[ $(bounded getent passwd "$broker_name") == "$broker_pw" && $(bounded getent passwd "$worker_name") == "$worker_pw" && $(bounded getent group "$shared_name") == "$group_row" ]] || fail N03-NSS-after
 verify_tree "$helper_root" "$helper_map" "$helper_map_sha" "$helper_nodes" "$helper_nodes_sha" tool 1 0
 observed=1
)

for key in n03_helper_root n03_helper_map n03_helper_map_sha256 n03_helper_nodes n03_helper_nodes_sha256 n03_helper_entry_sha256 n03_helper_generation; do [[ -n ${!key} ]] || fail N03-missing-input; done
n03_run "$n03_helper_root" "$n03_helper_map" "$n03_helper_map_sha256" "$n03_helper_nodes" "$n03_helper_nodes_sha256" "$n03_helper_entry_sha256" "$N03_BROKER_NAME" "$N03_WORKER_NAME" "$N03_SHARED_GROUP" "$N03_STARTUP_KIB" "$n03_helper_generation"
n03_exit=0
n03_result_sha256=$(bounded sha256sum "$log/n03/result.json"); n03_result_sha256=${n03_result_sha256:0:64}
remaining >/dev/null
# Existing N01 owner recipe follows unchanged.

printf '%s\n' "$G" >"$guard/armed"; bounded chmod 600 "$guard/armed"
[[ $(bounded stat -c '%u:%g:%a:%h' "$guard/armed") == 0:0:600:1 ]] || fail armed-guard
fixed_env=(PATH=/usr/bin:/bin HOME=/nonexistent LANG=C.UTF-8 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_CLI_HOME=/tmp)
unsafe='DOTNET_STARTUP_HOOKS DOTNET_ADDITIONAL_DEPS DOTNET_SHARED_STORE DOTNET_ROOT DOTNET_ROOT_X64 DOTNET_HOST_PATH DOTNET_ROLL_FORWARD DOTNET_ROLL_FORWARD_TO_PRERELEASE DOTNET_MULTILEVEL_LOOKUP CORECLR_ENABLE_PROFILING CORECLR_PROFILER CORECLR_PROFILER_PATH CORECLR_PROFILER_PATH_64 COR_ENABLE_PROFILING COR_PROFILER COR_PROFILER_PATH COMPlus_ReadyToRun COMPlus_ZapDisable'
props=(--property=Type=exec --property=User=0 --property=Group=0 --property=KillMode=control-group --property=Restart=no --property=RemainAfterExit=no --property=SendSIGKILL=yes --property=FinalKillSignal=9 --property=RuntimeMaxSec=240s --property=TimeoutStopSec=5s "--property=ConditionPathExists=$guard/armed" --property=PassEnvironment= "--property=UnsetEnvironment=$unsafe" "--property=Environment=${fixed_env[*]}")
diagnostic_stage=sealed-inputs
# Full destination and alias checks immediately before native dispatch. No extra nodes are skipped.
verify_tree "$stage/tool" "$payload_manifest" "$payload_manifest_sha256" "$payload_nodes" "$payload_nodes_sha256" tool 1 1
verify_tree "$stage/runtime" "$runtime_manifest" "$runtime_manifest_sha256" "$runtime_nodes" "$runtime_nodes_sha256" runtime 1 0
verify_tree "$stage/subject" "$source_manifest" "$source_manifest_sha256" "$source_nodes" "$source_nodes_sha256" source 1 0
pin_file "$build_receipt" "$build_receipt_sha256" "$MAX_NODE_JSON_BYTES"
while IFS=$'\t' read -r p h; do verify_os_path "$p" "$h"; done <<<"$os_files"
pin_file "$os_audit" "$os_audit_sha256" 1048576
(( $(remaining)>246 && job_deadline_epoch-$(bounded date -u +%s)>245 )) || fail native-life-outside-fixture
diagnostic_stage=n01
launched=1
(ulimit -f 8192; exec setsid timeout --signal=KILL "$(remaining)" systemd-run --quiet --wait --pipe --unit="$owner" "${props[@]}" /usr/bin/env -i "${fixed_env[@]}" "$host" "$managed" evidence supervise --request "$request" >"$log/n01.stdout" 2>"$log/n01.stderr") & launch_pid=$!
pin_owned_pg "$launch_pid"

# Fixed negative case: no caller case, PID, UID/GID, path or environment selector.
set +e; join_owned launch_pid; negative_launch=$?; set -e
[[ $negative_launch == 1 && -z $launch_pid && ! -s $log/n01.stdout ]] || fail negative-root-outcome
[[ $(bounded stat -c '%u:%g:%a:%h' "$log/n01.stderr") == 0:0:600:1 ]] || fail negative-root-stream-owner
[[ $(bounded stat -c %s "$log/n01.stderr") -le 14336 ]] || fail negative-root-stream-bound
workspace=/run/appsurface-evidence-$G
[[ -d $workspace && ! -L $workspace && $(bounded stat -c '%u:%a' "$workspace") == 0:750 ]] || fail negative-generation
# The immutable holder currently emits hash/count JSON only. Independent raw stream retention is missing.
# This fixed gate remains false until separately reviewed original-holder producer integration exists.
((NEGATIVE_RAW_STREAM_PRODUCER_CLEAR==1 && NEGATIVE_PYTHON_TRUST_CLEAR==1 && NEGATIVE_ROOT_TERMINAL_CLEAR==1)) || fail negative-prerequisite-pending
for alternate_python_path in /usr/lib/python312.zip /usr/bin/pyvenv.cfg /usr/pyvenv.cfg; do
 [[ ! -e $alternate_python_path && ! -L $alternate_python_path ]] || fail negative-python-alternate-prefix
done
# Existing OS audit schema must contain this fixed interpreter and COMPLETE fixed stdlib graph.
# Current donor producer does not populate it; promotion remains false until that source-owned gap is reviewed.
bounded jq -e 'any(.files[];.path=="/usr/bin/python3.12") and any(.elf[];.path=="/usr/bin/python3.12")' "$os_audit" >/dev/null || fail negative-python-audit-pending
ancestor /usr/lib/python3.12
# Exact complete audited membership is rechecked before and after individual pins.
negative_stdlib_exact() {
 [[ -n $1 && ${#1} -le 1048576 && ${#2} -le 1048576 && $1 == "$2" ]]
}
negative_stdlib_inventory() {
 ancestor /usr/lib/python3.12
 [[ $(bounded find -P /usr/lib/python3.12 ! -type d ! -type f ! -type l -print -quit) == '' ]] || fail negative-stdlib-special-node
 [[ $(bounded find -P /usr/lib/python3.12 \( ! -uid 0 -o ! -gid 0 -o \( ! -type l -a -perm /022 \) \) -print -quit) == '' ]] || fail negative-stdlib-mutable
 bounded /usr/bin/bash -o pipefail -c '/usr/bin/find -P /usr/lib/python3.12 \( -type f -o -type l \) -print | LC_ALL=C /usr/bin/sort | /usr/bin/head -c 1048577'
}
stdlib_expected=$(bounded jq -er '[.files[]|select(.path|startswith("/usr/lib/python3.12/"))|.path]|sort|if length>0 then join("\n") else error("empty-fixed-stdlib") end' "$os_audit")
stdlib_files=$(negative_stdlib_inventory)
negative_stdlib_exact "$stdlib_expected" "$stdlib_files" || fail negative-stdlib-membership
# Literal file aliases use the exact recorded root-owned chain; directory aliases reject.
while IFS= read -r python_file; do
 [[ $python_file == /usr/lib/python3.12/* && $python_file != *$'\t'* && $python_file != *$'\n'* ]] || fail negative-stdlib-name
 python_hash=$(bounded jq -er --arg p "$python_file" '[.files[]|select(.path==$p)] as $rows | if ($rows|length)==1 then $rows[0].sha256 else error("missing-fixed-pin") end' "$os_audit")
 verify_os_path "$python_file" "$python_hash"
 if [[ $(bounded head -c 4 "$python_file" | od -An -tx1 | tr -d ' \n') == 7f454c46 ]]; then
  python_resolved=$python_file
  if [[ $(bounded jq -r --arg p "$python_file" '[.aliases[]|select(.literal==$p)]|length' "$os_audit") == 1 ]]; then
   python_alias=$(bounded jq -c --arg p "$python_file" ' .aliases[]|select(.literal==$p)' "$os_audit")
   python_resolved=$(alias_walk "$python_alias")
  fi
  bounded jq -e --arg p "$python_resolved" '[.elf[]|select(.path==$p)]|length==1' "$os_audit" >/dev/null || fail negative-stdlib-elf-unreviewed
 fi
done <<<"$stdlib_files"
stdlib_after=$(negative_stdlib_inventory)
negative_stdlib_exact "$stdlib_expected" "$stdlib_after" || fail negative-stdlib-membership-after
pin_file "$os_audit" "$os_audit_sha256" 1048576
remaining >/dev/null
bounded /usr/bin/env -i PATH=/usr/bin:/bin LC_ALL=C /usr/bin/python3.12 -I -S -B - "$private" "$workspace" "$G" "$policy_sha" <<'NEGATIVE_PY'

import hashlib,json,os,re,stat,sys
CASE = 'N06'
private,workspace,generation,policy_hash=sys.argv[1:]
MAX_ROOT_STDERR=14336
def require(v):
    if not v: raise ValueError('negative-fixture-data-rejected')
def pairs(items):
    result={}; seen=set()
    for k,v in items:
        require(k.casefold() not in seen); seen.add(k.casefold()); result[k]=v
    return result
def decode(b):
    return json.loads(b.decode('utf-8'),object_pairs_hook=pairs,parse_constant=lambda _:require(False))
def fingerprint(s):
    return (s.st_dev,s.st_ino,s.st_mode,s.st_nlink,s.st_uid,s.st_gid,s.st_size,s.st_mtime_ns,s.st_ctime_ns)
def parents(path):
    require(path.startswith('/') and not path.endswith('/') and '//' not in path)
    parts=path.split('/')[1:]; require(all(x and x not in ('.','..') for x in parts))
    current=''
    for part in parts[:-1]:
        current+='/'+part; s=os.lstat(current)
        require(stat.S_ISDIR(s.st_mode) and not stat.S_ISLNK(s.st_mode))
def read_private(path,limit,uid=0,gid=0,mode=0o600):
    parents(path); named=os.lstat(path)
    require(stat.S_ISREG(named.st_mode) and named.st_nlink==1 and named.st_uid==uid
            and named.st_gid==gid and stat.S_IMODE(named.st_mode)==mode and named.st_size<=limit)
    fd=os.open(path,os.O_RDONLY|os.O_NOFOLLOW|os.O_NONBLOCK|os.O_CLOEXEC)
    try:
        before=os.fstat(fd); require(fingerprint(before)==fingerprint(named))
        data=b''
        while len(data)<=limit:
            block=os.read(fd,min(65536,limit+1-len(data)))
            if not block: break
            data+=block
        require(len(data)<=limit and len(data)==before.st_size)
        require(fingerprint(os.fstat(fd))==fingerprint(before)
                and fingerprint(os.lstat(path))==fingerprint(before))
        return data
    finally: os.close(fd)
def save(name,data):
    require('/' not in name and name not in ('.','..'))
    p=private+'/'+name; require(not os.path.lexists(p))
    fd=os.open(p,os.O_WRONLY|os.O_CREAT|os.O_EXCL|os.O_NOFOLLOW|os.O_CLOEXEC,0o600)
    try:
        s=os.fstat(fd); require(stat.S_ISREG(s.st_mode) and s.st_nlink==1 and s.st_uid==s.st_gid==0)
        offset=0
        while offset<len(data):
            n=os.write(fd,data[offset:]); require(n>0); offset+=n
        os.fsync(fd); require(os.fstat(fd).st_size==len(data))
    finally: os.close(fd)
    require(read_private(p,len(data))==data)
def ident(path,value,kind):
    require(type(value) is dict and set(value)=={'device_major','device_minor','inode','uid','gid','mode','links','length'})
    require(all(type(v) is int and v>=0 for v in value.values()))
    parents(path); s=os.lstat(path)
    require((kind=='d' and stat.S_ISDIR(s.st_mode)) or (kind=='f' and stat.S_ISREG(s.st_mode))
            or (kind=='l' and stat.S_ISLNK(s.st_mode)))
    measured={'device_major':os.major(s.st_dev),'device_minor':os.minor(s.st_dev),'inode':s.st_ino,
              'uid':s.st_uid,'gid':s.st_gid,'mode':s.st_mode,'links':s.st_nlink,'length':s.st_size}
    require(measured==value); return s
"""Closed fixture data checks only. Never authenticate native actors or grant acceptance."""
import base64
import hashlib
import io
import json
import re
import tarfile

# Replaced by the renderer from the exact frozen a358 C# enum declarations.
ENUMS = {'EvidenceControlOperation': ['Ready', 'Stop', 'Wait', 'Exit', 'Run', 'Artifacts', 'Artifact', 'ApplicationStart', 'ResourceWait'], 'EvidenceNativeObservationErrorKind': ['Unknown', 'Admission', 'Accounts', 'OutputPipe', 'ControlLine', 'Cancelled', 'Timeout', 'Io', 'AccessDenied', 'Unsupported', 'Disposed', 'Argument', 'InvalidData', 'InvalidOperation'], 'EvidenceNativeObservationPhase': ['Unknown', 'CallerCancellation', 'ProtectedInput', 'JobDeadline', 'BackendConnect', 'OwnerActivation', 'Plan', 'AccountCreate', 'WorkspaceCreate', 'ListenerBind', 'WorkerCreate', 'WorkerStart', 'ServerCreate', 'ServerLifetime', 'ServerRun', 'ServerCompletion', 'WorkerExit', 'WorkerStop', 'WorkerCompletion', 'BeginTeardown', 'Custody', 'FileVerification', 'CleanupBegin', 'ServerCancel', 'ListenerClose', 'ServerJoin', 'WorkerJoin', 'CleanupCustody', 'AccountsClose', 'WorkerClose', 'CustodyClose', 'WorkspaceClose', 'OwnerFinalCheck', 'ServerLifetimeClose', 'JobClose', 'OwnerClose', 'InputClose', 'BackendClose', 'FinalDeadline', 'ResultCheck'], 'LinuxAccountPreparationStage': ['Unknown', 'NamesAbsent', 'ReserveUtility', 'UtilityCreate', 'UtilityExecute', 'IdentityRead', 'OwnershipVerify', 'CleanupCheck', 'CleanupNameCheck', 'CleanupUtility', 'FinalNamesAbsent', 'FinalOwnershipCheck'], 'LinuxAccountUtilityStage': ['Unknown', 'OwnerCheck', 'Pipes', 'BackendConnect', 'Recipe', 'Start', 'CloseWrites', 'UnitRead', 'TerminalCheck', 'ObservationDelay', 'BeginTeardown', 'Stop', 'GroupRead', 'OutputJoin', 'PipeDispose', 'BackendDispose', 'PhysicalSettlement', 'FinalOwnerCheck'], 'LinuxControlFailureStage': ['Unknown', 'PeerCheck', 'WorkerExitTask', 'RequestLifetime', 'AcceptLoop', 'HandlerJoin', 'CapacityWait', 'Accept', 'AcceptJoin', 'ControlRegistration', 'HandlerDispatch', 'RequestRead', 'RequestClassify', 'CleanupRegistration', 'Stop', 'WaitJoin', 'ReplyGate', 'ReadyAuthorization', 'ReadyClaim', 'ReadyData', 'ResponseData', 'WaitClaim', 'ExitClaim', 'ResponseWrite', 'ConnectionRelease', 'PostWriteCheck', 'ReplyCommit', 'HandlerFailureCommit', 'ReplyGateRelease', 'ControlRelease', 'CleanupRegistrationClose', 'ExitCommit', 'AcceptCancel', 'ListenerClose', 'PendingAcceptJoin', 'HandlersJoin', 'DescendantsStop', 'ControlsJoin', 'FinalCancellation', 'CleanupBound', 'OwnerCheck', 'ProtocolIncomplete', 'WorkerTerminalTaskCompleted', 'ReplyGateClose', 'ListenerState', 'ListenerCancellation', 'ListenerWorkspace', 'ListenerParent', 'ListenerSocketMetadata', 'ListenerSocketName', 'ListenerEndpoint', 'ListenerWorkerSelection', 'ListenerOwnerIdentity', 'ListenerWorkerIdentity', 'ListenerDescriptor', 'ListenerNativeAccept', 'ListenerAcceptedPeer', 'ProcessState', 'ProcessSelection', 'ProcessExpectedSample', 'ProcessExpectedPid', 'ProcessExpectedStartTime', 'ProcessExpectedLiveState', 'ProcessExpectedUid', 'ProcessExpectedGid', 'ProcessExpectedCgroup', 'ProcessInitialContinuity', 'ProcessRepeatedContinuity', 'ProcessReadContinuity', 'ProcessRetainedProcRoot', 'ProcessRetainedProcess', 'ProcessRetainedStatus', 'ProcessRetainedStat', 'ProcessRetainedCgroup', 'ProcessNamedProcRoot', 'ProcessNamedProcess', 'ProcessNamedStatus', 'ProcessNamedStat', 'ProcessNamedCgroup', 'ProcessFirstStatRead', 'ProcessFirstStatParse', 'ProcessStatusRead', 'ProcessStatusParse', 'ProcessCgroupRead', 'ProcessCgroupParse', 'ProcessLastStatRead', 'ProcessLastStatParse', 'ProcessFileSystemInspect', 'ProcessFileSystemType', 'ProcessDirectoryStat', 'ProcessDirectoryInode', 'ProcessDirectoryType', 'ProcessRetainedProcessDeviceMajor', 'ProcessRetainedProcessDeviceMinor', 'ProcessRetainedProcessInode', 'ProcessRetainedProcessUid', 'ProcessRetainedProcessGid', 'ProcessRetainedProcessMode', 'ProcessRetainedProcessMetadata', 'ListenerAdmissionDrain', 'ListenerSocketClose', 'ListenerNamedSocketClose', 'ListenerParentClose', 'ListenerNativeAcceptOperationAborted', 'ListenerNativeAcceptInterrupted', 'ListenerNativeAcceptConnectionAborted', 'ListenerNativeAcceptSocketOther', 'ListenerAcceptedClose'], 'LinuxCustodyNodeKind': ['Generation', 'Control', 'Broker', 'Output', 'RawResults', 'Slot', 'Descriptor', 'Socket', 'Plan', 'Manifest', 'Summary'], 'LinuxCustodyOperation': ['Unknown', 'Platform', 'Settlement', 'AccountOwner', 'NodeSelection', 'Open', 'RetainedStat', 'AncestorPolicy', 'OriginalPolicy', 'BaselineComparison', 'NamedOpen', 'NamedStat', 'NamedComparison', 'InventoryRead', 'InventoryDecode', 'InventoryPolicy', 'InventoryComparison', 'HashRead', 'HashEof', 'HashFinalize', 'HashComparison', 'Chown', 'Chmod', 'TerminalPolicy', 'OriginalOwnerClose', 'FileRead', 'FileVerification', 'AccountRelease', 'RootRecheck', 'Cancellation', 'HolderState', 'OwnerIdentity'], 'LinuxRunAccountFailure': ['InvalidData', 'IdentityMismatch', 'NssFailed', 'UnsupportedPlatform', 'OperationFailed', 'CleanupFailed'], 'LinuxRunAccountOperation': ['CreateUser', 'CreateResultsGroup', 'DeleteUser', 'DeleteGroup'], 'LinuxSystemdStartError': ['Other', 'AccessDenied', 'InvalidArgs', 'NoReply', 'ServiceUnknown', 'UnknownMethod', 'UnitExists', 'LoadFailed', 'NoSuchUnit'], 'SupervisionCustodyFailure': ['None', 'Cancelled', 'SettlementValidationFailed', 'PreflightFailed', 'MutationFailed', 'LocalCloseFailed', 'FinalNativeRecheckFailed']}
CODES = frozenset(('ASEVD402','ASEVD404','ASEVD407','ASEVD409','ASEVD410','ASEVD420','ASEVD421'))
ROOT_TERMINAL = b'ASEVD410: The protected empty Observation execution or final cleanup could not be established. Fix: use an explicit mode and supported protected worker. See start-here/evidencehost.md.\n'
WORKER_TERMINAL = b'ASEVD409: Fresh output allocation or activation failed. Fix: use an explicit mode and supported protected worker. See start-here/evidencehost.md.\n'
NEGATIVE_CAPS = {
    'negative-setup.json':1024, 'negative-post-join.json':1024,
    'negative-worker-projection.json':1024, 'negative-kernel-observation.json':4097,
    'negative-worker-raw.json':4097, 'negative-root-failure.json':1024,
    'negative-root-terminal.txt':1024, 'negative-descriptor.json':65536,
    'negative-account-ids.tsv':128,
    'n05-worker.stdout':0, 'n05-worker.stderr':2048,
    'n06-worker.stdout':0, 'n06-worker.stderr':2048,
}

def require_data(value):
    if not value: raise ValueError('negative-fixture-data-rejected')

def unique_pairs(items):
    result={}; folded=set()
    for key,value in items:
        require_data(key.casefold() not in folded)
        folded.add(key.casefold()); result[key]=value
    return result

def decode_data(raw):
    return json.loads(raw.decode('utf-8'),object_pairs_hook=unique_pairs,
                      parse_constant=lambda _:require_data(False))

def exact_object(value,keys):
    require_data(type(value) is dict and set(value)==set(keys))

def enum_data(value,name,nullable=False):
    require_data(value is None and nullable or type(value) is str and value in ENUMS[name])

def code_data(value):
    require_data(value is None or type(value) is str and value in CODES)

def family(value):
    enum_data(value['error_kind'],'EvidenceNativeObservationErrorKind')
    code_data(value['diagnostic_code'])

def validate_failure(value):
    """Exact v4 serializer member sets and finite values, including null projections."""
    exact_object(value,('schema','phase','error_kind','diagnostic_code','account_failure','control_failure','custody_failure'))
    require_data(value['schema']=='evidence-native-observation-failure-v4')
    enum_data(value['phase'],'EvidenceNativeObservationPhase'); family(value)
    account=value['account_failure']
    if account is not None:
        exact_object(account,('preparation_stage','utility_stage','operation','error_kind','diagnostic_code','account_code','exec_main_code','exec_main_status','dbus_category'))
        enum_data(account['preparation_stage'],'LinuxAccountPreparationStage')
        enum_data(account['utility_stage'],'LinuxAccountUtilityStage')
        enum_data(account['operation'],'LinuxRunAccountOperation',True);family(account)
        enum_data(account['account_code'],'LinuxRunAccountFailure',True)
        enum_data(account['dbus_category'],'LinuxSystemdStartError',True)
        code,status=account['exec_main_code'],account['exec_main_status']
        require_data(code is None and status is None or type(code) is int and 1<=code<=6 and type(status) is int and 0<=status<=255)
    control=value['control_failure']
    if control is not None:
        require_data(value['phase'] in ('ServerRun','ServerCompletion'))
        exact_object(control,('stage','operation','error_kind','diagnostic_code'))
        enum_data(control['stage'],'LinuxControlFailureStage')
        enum_data(control['operation'],'EvidenceControlOperation',True);family(control)
    custody=value['custody_failure']
    if custody is not None:
        require_data(value['phase'] in ('Custody','CleanupCustody','FileVerification','AccountsClose'))
        exact_object(custody,('procedure','node_kind','operation','error_kind','diagnostic_code'))
        enum_data(custody['procedure'],'SupervisionCustodyFailure')
        enum_data(custody['node_kind'],'LinuxCustodyNodeKind',True)
        enum_data(custody['operation'],'LinuxCustodyOperation');family(custody)
    return value

def validate_fixture_frames(raw,case):
    """Seven exact LF frames, real producer raw bytes; returns detached data only."""
    require_data(case in ('N05','N06') and type(raw) is bytes and 0<len(raw)<=14336)
    require_data(raw.endswith(b'\n') and b'\r' not in raw)
    lines=raw.splitlines(keepends=True)
    require_data(len(lines)==7 and all(x.endswith(b'\n') for x in lines))
    require_data(all(0<len(x)<=n for x,n in zip(lines,(1024,1024,1024,4097,4097,1024,1024))))
    require_data(lines[6]==ROOT_TERMINAL)
    setup,after,projection,kernel,worker,failure=[decode_data(x) for x in lines[:6]]
    schema='issue779-n05-slot-inspection-v1' if case=='N05' else 'issue779-n06-symlink-inspection-v1'
    keys=('schema','phase','parent','slot','sentinel','sha256') if case=='N05' else ('schema','phase','parent','target','link','target_sha256','sentinel','sha256')
    exact_object(setup,keys);exact_object(after,keys)
    require_data(setup['schema']==after['schema']==schema and setup['phase']=='setup' and after['phase']=='post_join')
    require_data({k:v for k,v in setup.items() if k!='phase'}=={k:v for k,v in after.items() if k!='phase'})
    exact_object(projection,('schema','origin','allocation','terminal_diagnostic','stdout_bytes','stderr_bytes','native_authority'))
    require_data(projection['schema']=='issue779-'+case.lower()+'-joined-worker-output-v1'
                 and projection['origin']=='joined-worker-output' and projection['terminal_diagnostic']=='ASEVD409'
                 and type(projection['stdout_bytes']) is int and projection['stdout_bytes']==0
                 and type(projection['stderr_bytes']) is int and 0<projection['stderr_bytes']<=2048
                 and projection['native_authority'] is False)
    alloc=projection['allocation'];exact_object(alloc,('schema','phase','operation','stageOutcome','terminalCode','errorClass','nativeErrno'))
    require_data(alloc['schema']=='evidence-allocation-failure-v1' and alloc['phase']=='Allocation'
                 and alloc['operation']=='CreateSlot' and alloc['stageOutcome']=='Failed'
                 and alloc['terminalCode']=='StageFailed' and alloc['errorClass']=='Io'
                 and (alloc['nativeErrno'] is None or type(alloc['nativeErrno']) is int and 1<=alloc['nativeErrno']<=4095))
    exact_object(worker,('schema','stdout_base64','stderr_base64','stdout_bytes','stderr_bytes','native_authority'))
    require_data(worker['schema']=='issue779-'+case.lower()+'-joined-worker-raw-v1' and worker['native_authority'] is False
                 and worker['stdout_base64']=='' and type(worker['stdout_bytes']) is int and worker['stdout_bytes']==0
                 and type(worker['stderr_bytes']) is int and 0<worker['stderr_bytes']<=2048
                 and type(worker['stderr_base64']) is str)
    stdout=base64.b64decode(worker['stdout_base64'],validate=True)
    stderr=base64.b64decode(worker['stderr_base64'],validate=True)
    require_data(base64.b64encode(stdout).decode('ascii')==worker['stdout_base64']
                 and base64.b64encode(stderr).decode('ascii')==worker['stderr_base64']
                 and stdout==b'' and len(stderr)==worker['stderr_bytes']==projection['stderr_bytes'])
    worker_lines=stderr.splitlines(keepends=True)
    require_data(len(worker_lines)==2 and worker_lines[1]==WORKER_TERMINAL and decode_data(worker_lines[0])==alloc)
    validate_failure(failure)
    return {'lines':lines,'kernel':kernel,'stdout':stdout,'stderr':stderr,'failure':failure}

def inspect_retention(data,allowed):
    """Canonical bounded USTAR whitelist inspection only, not a native result verifier."""
    require_data(type(data) is bytes and 0<len(data)<=33619968)
    names=[];total=0;values={};entries=[]
    with tarfile.open(fileobj=io.BytesIO(data),mode='r:') as archive:
        for member in archive:
            require_data(member.name in allowed and member.name not in names and member.isfile()
                         and member.mode==0o600 and member.uid==member.gid==member.mtime==0
                         and not member.linkname and not member.pax_headers and not member.uname and not member.gname
                         and 0<=member.size<=NEGATIVE_CAPS.get(member.name,8388608))
            names.append(member.name);total+=member.size;require_data(total<=33554432+4096 and len(names)<=len(allowed))
            stream=archive.extractfile(member);require_data(stream is not None)
            with stream: content=stream.read(member.size+1)
            require_data(len(content)==member.size);values[member.name]=content;entries.append((member.name,content))
    require_data(names==sorted(names) and 'retention-selection.json' in values)
    selection=decode_data(values['retention-selection.json'])
    exact_object(selection,('schema','selection_status','data_file_count','data_bytes','missing_fixed_file_count'))
    require_data(selection['schema']=='issue779-native-retention-selection-v1'
                 and selection['selection_status'] in ('parent-absent','parent-empty','one-namespace')
                 and type(selection['data_file_count']) is int and selection['data_file_count']==len(names)-1
                 and type(selection['data_bytes']) is int and selection['data_bytes']==total-len(values['retention-selection.json'])
                 and type(selection['missing_fixed_file_count']) is int and selection['missing_fixed_file_count']>=0)
    canonical=io.BytesIO()
    with tarfile.open(fileobj=canonical,mode='w:',format=tarfile.USTAR_FORMAT) as rebuilt:
        for name,content in entries:
            info=tarfile.TarInfo(name);info.size=len(content);info.mode=0o600;info.uid=info.gid=info.mtime=0
            rebuilt.addfile(info,io.BytesIO(content))
    require_data(canonical.getvalue()==data)
    return values

"""Strict detached negative-record consistency checks; no OS access or native authority."""
import json
import re

MAX_JSON_BYTES = 4096
MAX_STREAM_BYTES = 1024 * 1024
MAX_RECEIVED_LIMIT = 16 * 1024 * 1024
UINT32_MAX = (1 << 32) - 1
UINT64_MAX = (1 << 64) - 1
TOP = frozenset(('schema', 'generation', 'worker_unit', 'process', 'ready', 'terminal',
                 'cgroup', 'pumps', 'joins', 'observation_only', 'native_authority', 'native_acceptance'))


class KernelRecordRejected(ValueError):
    """Closed data rejection, deliberately retaining no supplied values or cause text."""


def _reject():
    raise KernelRecordRejected('negative-kernel-data-rejected')


def _require(value):
    if not value:
        _reject()


def _uint(value, low, high):
    _require(type(value) is int and low <= value <= high)


def _digest(value):
    _require(type(value) is str and re.fullmatch('[0-9a-f]{64}', value) is not None)


def _object(value, keys):
    _require(type(value) is dict and set(value) == set(keys))


def _pairs(pairs):
    result = {}
    folded = set()
    for key, value in pairs:
        canonical = key.casefold()
        _require(canonical not in folded)
        folded.add(canonical)
        result[key] = value
    return result


def _stream(value, expected_sha256, expected_bytes):
    _object(value, ('received_bytes', 'retained_bytes', 'discarded_bytes', 'eof', 'failure', 'sha256'))
    for key in ('received_bytes', 'retained_bytes'):
        _uint(value[key], 0, MAX_STREAM_BYTES)
        _require(value[key] == expected_bytes)
    _uint(value['discarded_bytes'], 0, 0)
    _require(value['eof'] is True and value['failure'] == 'None')
    _digest(value['sha256'])
    _require(value['sha256'] == expected_sha256)


def check_kernel_record(raw, *, expected_generation, expected_uid, expected_gid,
                        expected_pid, expected_starttime_ticks, expected_descriptor_sha256,
                        expected_stdout_sha256, expected_stdout_bytes,
                        expected_stderr_sha256, expected_stderr_bytes):
    """Return True for detached data consistency only; never issue admission or acceptance.

    ``raw`` is exactly a JSON object of at most 4096 UTF-8 bytes, optionally followed
    by one LF. Expected values are caller data, not authenticated identities. The
    original source-built holder and root-private stream capture must independently
    establish provenance. No path, command, timer, descriptor or mutable lease is
    consumed or created. Every rejection has one fixed message and no chained cause.
    Both stream hashes/counts must come from full captured bytes outside this parser.
    The negative-case terminal is fixed CLD_EXITED(1), status 1; zero and signals reject.
    """
    try:
        _require(type(raw) is bytes and 0 < len(raw) <= MAX_JSON_BYTES + 1)
        body = raw[:-1] if raw.endswith(b'\n') else raw
        _require(0 < len(body) <= MAX_JSON_BYTES and body.startswith(b'{') and body.endswith(b'}'))
        _require(type(expected_generation) is str
                 and re.fullmatch('[0-9a-f]{32}', expected_generation) is not None
                 and expected_generation != '0' * 32)
        _uint(expected_uid, 1, UINT32_MAX - 1)
        _uint(expected_gid, 1, UINT32_MAX - 1)
        _uint(expected_pid, 1, (1 << 31) - 1)
        _uint(expected_starttime_ticks, 1, UINT64_MAX)
        for value in (expected_descriptor_sha256, expected_stdout_sha256, expected_stderr_sha256):
            _digest(value)
        _uint(expected_stdout_bytes, 0, MAX_STREAM_BYTES)
        _uint(expected_stderr_bytes, 0, MAX_STREAM_BYTES)
        value = json.loads(body.decode('utf-8', errors='strict'), object_pairs_hook=_pairs,
                           parse_constant=lambda _: _reject())
        _object(value, TOP)
        _require(value['schema'] == 'issue779-negative-kernel-observation-v1')
        _require(value['generation'] == expected_generation)
        unit = 'appsurface-evidence-worker-' + expected_generation + '.service'
        group = '/system.slice/' + unit
        _require(value['worker_unit'] == unit)
        process = value['process']
        _object(process, ('pid', 'starttime_ticks', 'uid4', 'gid4', 'control_group'))
        _uint(process['pid'], 1, (1 << 31) - 1)
        _uint(process['starttime_ticks'], 1, UINT64_MAX)
        _require(process['pid'] == expected_pid and process['starttime_ticks'] == expected_starttime_ticks)
        for name, expected in (('uid4', expected_uid), ('gid4', expected_gid)):
            ids = process[name]
            _require(type(ids) is list and len(ids) == 4)
            for identity in ids:
                _uint(identity, 1, UINT32_MAX - 1)
                _require(identity == expected)
        _require(process['control_group'] == group)
        ready = value['ready']
        _object(ready, ('committed', 'descriptor_sha256'))
        _require(ready['committed'] is True)
        _digest(ready['descriptor_sha256'])
        _require(ready['descriptor_sha256'] == expected_descriptor_sha256)
        terminal = value['terminal']
        _object(terminal, ('exec_main_pid', 'exec_main_code', 'exec_main_status', 'active_state', 'sub_state'))
        _uint(terminal['exec_main_pid'], 1, (1 << 31) - 1)
        _uint(terminal['exec_main_code'], 1, 1)
        _uint(terminal['exec_main_status'], 1, 1)
        _require(terminal['exec_main_pid'] == expected_pid)
        _require((terminal['active_state'], terminal['sub_state']) in
                 (('active', 'exited'), ('inactive', 'dead'), ('failed', 'failed')))
        cg = value['cgroup']
        _object(cg, ('exists', 'populated', 'frozen', 'device_major', 'device_minor', 'inode'))
        _require(type(cg['exists']) is bool)
        if cg['exists']:
            _require(cg['populated'] is False and cg['frozen'] is False)
            _uint(cg['device_major'], 0, UINT32_MAX)
            _uint(cg['device_minor'], 0, UINT32_MAX)
            _uint(cg['inode'], 1, UINT64_MAX)
        else:
            _require(all(cg[k] is None for k in ('populated', 'frozen', 'device_major', 'device_minor', 'inode')))
        pumps = value['pumps']
        _object(pumps, ('stdout', 'stderr', 'received_bytes', 'received_byte_limit', 'failure', 'discarded_bytes'))
        _stream(pumps['stdout'], expected_stdout_sha256, expected_stdout_bytes)
        _stream(pumps['stderr'], expected_stderr_sha256, expected_stderr_bytes)
        _uint(pumps['received_bytes'], 0, MAX_RECEIVED_LIMIT)
        _uint(pumps['received_byte_limit'], 1, MAX_RECEIVED_LIMIT)
        _uint(pumps['discarded_bytes'], 0, 0)
        _require(pumps['received_bytes'] == expected_stdout_bytes + expected_stderr_bytes
                 and pumps['received_bytes'] <= pumps['received_byte_limit'] and pumps['failure'] == 'None')
        _object(value['joins'], ('startup', 'pending_stop', 'monitor', 'server', 'pumps'))
        _require(all(x is True for x in value['joins'].values()))
        _require(value['observation_only'] is True and value['native_authority'] is False
                 and value['native_acceptance'] is False)
        return True
    except (ValueError, TypeError, KeyError, OverflowError, RecursionError, UnicodeError):
        raise KernelRecordRejected('negative-kernel-data-rejected') from None

def body():
    require(re.fullmatch('[0-9a-f]{32}',generation) is not None)
    p=os.lstat(private); require(stat.S_ISDIR(p.st_mode) and p.st_uid==p.st_gid==0 and stat.S_IMODE(p.st_mode)==0o700)
    raw=read_private(private+'/logs/n01.stderr',MAX_ROOT_STDERR)
    require(read_private(private+'/logs/n01.stdout',0)==b'')
    checked_frames=validate_fixture_frames(raw,CASE)
    require(raw.endswith(b'\n')); lines=raw.splitlines(keepends=True)
    require(len(lines)==7 and all(x.endswith(b'\n') and b'\r' not in x for x in lines))
    require(all(len(x)<=limit for x,limit in zip(lines,(1024,1024,1024,4097,4097,1024,1024))))
    setup,after,projection,kernel,raw_worker,failure=[decode(x) for x in lines[:6]]
    schema='issue779-n05-slot-inspection-v1' if CASE=='N05' else 'issue779-n06-symlink-inspection-v1'
    keys={'schema','phase','parent','slot','sentinel','sha256'} if CASE=='N05' else {'schema','phase','parent','target','link','target_sha256','sentinel','sha256'}
    require(set(setup)==keys and set(after)==keys and setup['schema']==after['schema']==schema
            and setup['phase']=='setup' and after['phase']=='post_join')
    require({k:v for k,v in setup.items() if k!='phase'}=={k:v for k,v in after.items() if k!='phase'})
    require(set(projection)=={'schema','origin','allocation','terminal_diagnostic','stdout_bytes','stderr_bytes','native_authority'})
    require(projection['schema']=='issue779-'+CASE.lower()+'-joined-worker-output-v1'
            and projection['origin']=='joined-worker-output' and projection['terminal_diagnostic']=='ASEVD409'
            and type(projection['stdout_bytes']) is int and projection['stdout_bytes']==0
            and type(projection['stderr_bytes']) is int and 0<projection['stderr_bytes']<=2048
            and projection['native_authority'] is False)
    alloc=projection['allocation']; require(set(alloc)=={'schema','phase','operation','stageOutcome','terminalCode','errorClass','nativeErrno'})
    require(alloc['schema']=='evidence-allocation-failure-v1' and alloc['phase']=='Allocation'
            and alloc['operation']=='CreateSlot' and alloc['stageOutcome']=='Failed'
            and alloc['terminalCode']=='StageFailed' and alloc['errorClass']=='Io'
            and (alloc['nativeErrno'] is None or type(alloc['nativeErrno']) is int and 1<=alloc['nativeErrno']<=4095))
    validate_failure(failure)
    require(lines[6]==ROOT_TERMINAL)
    descriptor_path=workspace+'/worker/worker-control.json'
    # Header supplies worker GID only as comparison data; final read authenticates the actual closed leaf.
    ds=os.lstat(descriptor_path); require(ds.st_gid>0)
    descriptor_bytes=read_private(descriptor_path,65536,0,ds.st_gid,0o440)
    descriptor=decode(descriptor_bytes)
    require(descriptor['schema']=='evidence-worker-linux-v1' and descriptor['unit']=='appsurface-evidence-worker-'+generation+'.service'
            and descriptor['run_id']=='csharp/'+generation and descriptor['output_parent']==workspace+'/output'
            and descriptor['descriptor_path']==descriptor_path and descriptor['policy_sha256']==policy_hash)
    uid=descriptor['worker_uid']; gid=descriptor['worker_gid']
    require(type(uid) is int and type(gid) is int and 0<uid<4294967295 and 0<gid<4294967295 and gid==ds.st_gid)
    for key in ('subject_uid','subject_gid','worker_pid'):
        require(type(descriptor[key]) is int and 0<descriptor[key]<4294967295)
    parent=workspace+'/output'; ident(parent,after['parent'],'d')
    require(after['parent']['uid']==uid and after['parent']['gid']==gid and after['parent']['mode']==0o40700)
    if CASE=='N05':
        slot=parent+'/evidence'; ident(slot,after['slot'],'d')
        require(after['slot']['uid']==uid and after['slot']['gid']==gid and after['slot']['mode']==0o40700 and after['slot']['links']==2)
        require(sorted(os.listdir(parent))==['evidence'] and sorted(os.listdir(slot))==['occupied-sentinel'])
        sentinel=slot+'/occupied-sentinel'; expected=b'issue779-n05-occupied-slot-v1\n'
    else:
        target=parent+'/symlink-target'; link=parent+'/evidence'
        ident(target,after['target'],'d'); before=ident(link,after['link'],'l')
        require(after['target']['uid']==uid and after['target']['gid']==gid and after['target']['mode']==0o40700
                and after['target']['links']==2 and after['link']['uid']==uid and after['link']['gid']==gid
                and after['link']['mode']==0o120777 and after['link']['links']==1)
        require(os.readlink(link)=='symlink-target' and fingerprint(os.lstat(link))==fingerprint(before))
        require(after['target_sha256']==hashlib.sha256(b'symlink-target').hexdigest())
        require(sorted(os.listdir(parent))==['evidence','symlink-target'] and sorted(os.listdir(target))==['target-sentinel'])
        sentinel=target+'/target-sentinel'; expected=b'issue779-n06-symlink-target-v1\n'
    ident(sentinel,after['sentinel'],'f')
    full=read_private(sentinel,len(expected),uid,gid,0o600)
    require(full==expected and after['sha256']==hashlib.sha256(full).hexdigest())
    raw_dir=os.lstat(workspace+'/raw-results')
    require(stat.S_ISDIR(raw_dir.st_mode) and raw_dir.st_uid==descriptor['subject_uid'] and raw_dir.st_gid>0
            and stat.S_IMODE(raw_dir.st_mode)==0o710 and os.listdir(workspace+'/raw-results')==[])
    # Pending source-bound producer publishes its ACTUAL original immutable full pair, never fabricated bytes.
    import base64
    require(set(raw_worker)=={'schema','stdout_base64','stderr_base64','stdout_bytes','stderr_bytes','native_authority'})
    require(raw_worker['schema']=='issue779-'+CASE.lower()+'-joined-worker-raw-v1' and raw_worker['native_authority'] is False)
    require(raw_worker['stdout_base64']=='' and type(raw_worker['stdout_bytes']) is int and raw_worker['stdout_bytes']==0)
    require(type(raw_worker['stderr_bytes']) is int and 0<raw_worker['stderr_bytes']<=2048 and type(raw_worker['stderr_base64']) is str)
    stdout=base64.b64decode(raw_worker['stdout_base64'],validate=True)
    stderr=base64.b64decode(raw_worker['stderr_base64'],validate=True)
    require(base64.b64encode(stdout).decode('ascii')==raw_worker['stdout_base64'] and base64.b64encode(stderr).decode('ascii')==raw_worker['stderr_base64'])
    require(len(stderr)==raw_worker['stderr_bytes'])
    require(stdout==b'' and len(stderr)==projection['stderr_bytes'])
    expected_alloc=decode(stderr.splitlines(keepends=True)[0]); require(expected_alloc==alloc)
    worker_terminal=b'ASEVD409: Fresh output allocation or activation failed. Fix: use an explicit mode and supported protected worker. See start-here/evidencehost.md.\n'
    require(len(stderr.splitlines(keepends=True))==2 and stderr.splitlines(keepends=True)[1]==worker_terminal)
    require(type(kernel['process']['starttime_ticks']) is int)
    check_kernel_record(lines[3],expected_generation=generation,expected_uid=uid,expected_gid=gid,
        expected_pid=descriptor['worker_pid'],expected_starttime_ticks=kernel['process']['starttime_ticks'],
        expected_descriptor_sha256=hashlib.sha256(descriptor_bytes).hexdigest(),
        expected_stdout_sha256=hashlib.sha256(stdout).hexdigest(),expected_stdout_bytes=len(stdout),
        expected_stderr_sha256=hashlib.sha256(stderr).hexdigest(),expected_stderr_bytes=len(stderr))
    for name,data in zip(('negative-setup.json','negative-post-join.json','negative-worker-projection.json',
                          'negative-kernel-observation.json','negative-worker-raw.json','negative-root-failure.json','negative-root-terminal.txt'),lines):
        save(name,data)
    save(CASE.lower()+'-worker.stdout',stdout)
    save(CASE.lower()+'-worker.stderr',stderr)
    save('negative-descriptor.json',descriptor_bytes)
    save('negative-account-ids.tsv',('\t'.join(str(x) for x in (uid,gid,descriptor['subject_uid'],descriptor['subject_gid'],raw_dir.st_gid))+'\n').encode('ascii'))
try: body()
except Exception:
    print('NEGATIVE_RECORDS_REJECTED',file=sys.stderr); sys.exit(1)

NEGATIVE_PY
# Forward AND reverse immutable NSS facts must still exist. Quarantine is expected, never absence.
[[ $(bounded stat -c '%u:%g:%a:%h' "$private/negative-account-ids.tsv") == 0:0:600:1 ]] || fail negative-account-data
IFS=$'\t' read -r wu wg su sg rg extra <"$private/negative-account-ids.tsv"
[[ -z ${extra:-} ]] || fail negative-account-data
for value in "$wu" "$wg" "$su" "$sg" "$rg"; do [[ $value =~ ^[1-9][0-9]*$ ]] && ((value<4294967295)) || fail negative-account-id; done
for index in 0 1; do
 n=${names[$index]}; uid=$wu; gid=$wg; ((index==0)) || { uid=$su; gid=$sg; }
 forward=$(bounded getent passwd "$n"); reverse=$(bounded getent passwd "$uid")
 [[ $forward == "$reverse" && $forward != *$'\n'* ]] || fail negative-user-NSS
 IFS=: read -r pn px pu pg rest <<<"$forward"
 [[ $pn == "$n" && $pu == "$uid" && $pg == "$gid" ]] || fail negative-user-identity
done
for index in 0 1 2; do
 n=${names[$index]}; gid=$wg; ((index!=1)) || gid=$sg; ((index!=2)) || gid=$rg
 forward=$(bounded getent group "$n"); reverse=$(bounded getent group "$gid")
 [[ $forward == "$reverse" && $forward != *$'\n'* ]] || fail negative-group-NSS
 IFS=: read -r gn gx gg members extra <<<"$forward"
 [[ $gn == "$n" && $gg == "$gid" && -z $members && -z ${extra:-} ]] || fail negative-group-identity
done
# Original process-group cleanup and deadlines still run through the original EXIT trap.
remaining >/dev/null; n01=$negative_launch; success=1
