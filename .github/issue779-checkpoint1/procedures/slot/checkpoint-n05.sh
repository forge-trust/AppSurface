#!/usr/bin/env bash
# Source-only candidate; all runtime records are produced only by actual explicit execution.
set -euo pipefail
export LC_ALL=C PATH=/usr/sbin:/usr/bin:/sbin:/bin
umask 077
readonly FIXTURE_SECONDS=600 CLEANUP_RESERVE=30
readonly NEGATIVE_CASE=N05 NEGATIVE_SOURCE_COUNT=2872
readonly NEGATIVE_CORE_BASE=a358d47ef36939718536b13c27b038faf307299b
readonly NEGATIVE_SOURCE_CAPTURE_CLEAR=1
readonly NEGATIVE_FIXTURE_REVIEW_CLEAR=1 NEGATIVE_RAW_STREAM_PRODUCER_CLEAR=1
readonly NEGATIVE_PYTHON_TRUST_CLEAR=1 NEGATIVE_ROOT_TERMINAL_CLEAR=1
readonly NEGATIVE_KERNEL_PARSER_SHA256=90da0002db90ddb8b769bd3530551f10363ce772bc99327bdd27976be71cf3ab
readonly MAX_MANIFEST_BYTES=8388608 MAX_ROWS=32768 MAX_NODES=65536
readonly MAX_INPUT_FILE_BYTES=2147483648 MAX_INPUT_TREE_BYTES=4294967296
readonly MAX_NODE_JSON_BYTES=4194304 CORE_FILE_BYTES=268435456 CORE_TREE_BYTES=1073741824 CORE_NODES=8192
mode=prepare-only; mode_selected=0; phase=work; diagnostic_stage=arguments; diagnostic_failure_reported=0
source_root= source_manifest= source_manifest_sha256= payload_root= payload_manifest= payload_manifest_sha256=
runtime_root= runtime_manifest= runtime_manifest_sha256= source_review= source_review_sha256=
binding_validator= binding_validator_sha256= binding_deadline_monotonic= source_capture= source_capture_sha256=
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
 --binding-validator|--binding-validator-sha256|--binding-deadline-monotonic|--source-capture|--source-capture-sha256|--source-root|--source-manifest|--source-manifest-sha256|--source-nodes|--source-nodes-sha256|--payload-root|--payload-manifest|--payload-manifest-sha256|--payload-nodes|--payload-nodes-sha256|--runtime-root|--runtime-manifest|--runtime-manifest-sha256|--runtime-nodes|--runtime-nodes-sha256|--source-review|--source-review-sha256|--build-receipt|--build-receipt-sha256|--os-audit|--os-audit-sha256|--reviewed-script-sha256|--source-revision|--base-revision|--workflow-identity|--entry|--runtime-host|--entry-sha256|--n02-uid|--n02-gid|--n03-helper-root|--n03-helper-map|--n03-helper-map-sha256|--n03-helper-nodes|--n03-helper-nodes-sha256|--n03-helper-entry-sha256|--n03-helper-generation)
 (($#>=2)) || fail option-value; key=${1#--}; key=${key//-/_}; [[ -z ${!key} ]] || fail duplicate-option
 printf -v "$key" '%s' "$2"; shift 2 ;;
 *) fail unknown-option ;;
 esac
done
if [[ $mode == prepare-only && -z $source_root ]]; then
 printf '%s\n' 'PREPARATION_ONLY:NO_INPUTS_VERIFIED:N01_NOT_RUN:N02_NOT_RUN'; exit 0
fi
((NEGATIVE_SOURCE_CAPTURE_CLEAR==1 && NEGATIVE_FIXTURE_REVIEW_CLEAR==1)) || fail negative-source-and-review-pending
[[ $source_revision =~ ^[0-9a-f]{40}$ ]] || fail negative-source-pin
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
fixture_stage_observation() {
 local event=$1 stage_name=$2 now left_seconds
 case "$event" in begin|end) ;; *) fail timing-event ;; esac
 case "$stage_name" in declaration-cache|ordinary-os-initial|elf-closure|sealed-trees|ordinary-os-final|negative-python-qualification) ;; *) fail timing-stage ;; esac
 now=$(monotonic) || fail monotonic-clock
 left_seconds=$(remaining)
 printf '{"schema":"issue779-fixture-stage-observation-v1","status":"observations-only","stage":"%s","event":"%s","elapsed_seconds":%d,"remaining_seconds":%d}\n' "$stage_name" "$event" "$((now-fixture_start))" "$left_seconds" >&2 || fail timing-write
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
 local p=$1 h=$2 limit=$3 before after size links measured
 absolute "$p"; sha "$h"; [[ -f $p && ! -L $p ]] || fail regular-file
 before=$(bounded stat -c '%d:%i:%s:%f:%h:%u:%g' -- "$p")
 [[ $before =~ ^([0-9]+):([0-9]+):([0-9]+):([0-9a-f]+):([0-9]+):([0-9]+):([0-9]+)$ ]] || fail file-metadata
 size=${BASH_REMATCH[3]}; links=${BASH_REMATCH[5]}
 [[ $size =~ ^[0-9]+$ ]] && ((size<=limit)) || fail file-byte-bound
 [[ $links == 1 ]] || fail hardlink
 measured=$(bounded sha256sum -- "$p"); after=$(bounded stat -c '%d:%i:%s:%f:%h:%u:%g' -- "$p")
 [[ $before == "$after" && ${measured:0:64} == "$h" ]] || fail file-pin-changed
}
ancestor() {
 local p=$1 uid perms metadata; absolute "$p"
 while :; do
  [[ -d $p && ! -L $p ]] || fail ancestor-type
  metadata=$(bounded stat -c '%u:%a' -- "$p")
  [[ $metadata =~ ^([0-9]+):([0-7]+)$ ]] || fail unsafe-root-ancestor
  uid=${BASH_REMATCH[1]}; perms=${BASH_REMATCH[2]}
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
# BEGIN N06 alias declaration cache candidate
# Immutable DECLARATIONS only, never a cached filesystem check/result.
os_alias_declarations_ready=0
declare -a os_alias_resolved=() os_alias_hashes=() os_alias_starts=() os_alias_counts=()
declare -a os_alias_link_paths=() os_alias_link_targets=() os_alias_link_metadata=()
negative_os_alias_index() {
 local LC_ALL=C p=$1 low=0 high=$((${#os_cache_alias_paths[@]}-1)) mid
 [[ $os_cache_ready == 1 && $os_alias_declarations_ready == 1 ]] || return 1
 while ((low<=high)); do
  mid=$(((low+high)/2))
  if [[ $p == "${os_cache_alias_paths[$mid]}" ]]; then printf '%s' "$mid"; return 0
  elif [[ $p < "${os_cache_alias_paths[$mid]}" ]]; then high=$((mid-1))
  else low=$((mid+1)); fi
 done
 return 1
}
negative_os_alias_declarations_initialize() {
 local rows kind ai ordinal path target metadata extra next=0 current=-1 seen=0 count=0 expected
 [[ $os_alias_declarations_ready == 0 && ${#os_alias_resolved[@]} == 0 && ${#os_alias_link_paths[@]} == 0 ]] || fail alias-declaration-replay
 rows=$(bounded jq -r '
  .aliases|sort_by(.literal)|to_entries[] | .key as $i | .value as $a |
  (["A",$i,$a.literal,$a.resolved_path,$a.resolved_sha256,($a.links|length)]|map(tostring)|join("\u001f")),
  ($a.links|to_entries[] | ["L",$i,.key,.value.path,.value.target,
   "\(.value.uid):\(.value.gid):\(.value.mode):\(.value.device):\(.value.inode)"]|map(tostring)|join("\u001f"))
 ' "$os_audit")
 [[ ${#rows} -le 1048576 ]] || fail alias-declaration-bytes
 expected=${#os_cache_alias_paths[@]}
 ((expected<=64)) || fail alias-declaration-count
 if [[ -n $rows ]]; then
  while IFS=$'\x1f' read -r kind ai ordinal path target metadata extra; do
   [[ -z $extra && $ai =~ ^[0-9]{1,2}$ ]] || fail alias-declaration-row
   ai=$((10#$ai))
   if [[ $kind == A ]]; then
    ((current<0 || seen==count)) || fail alias-declaration-incomplete
    ((ai==next && ai<expected)) || fail alias-declaration-order
    # Header columns: tag, alias index, literal, resolved path, hash, link count.
    [[ $ordinal == "${os_cache_alias_paths[$ai]}" && $metadata =~ ^[0-9]{1,2}$ ]] || fail alias-declaration-header
    lexical "$path"; sha "$target"
    count=$((10#$metadata)); ((count>=1 && count<=16)) || fail alias-declaration-links
    [[ $(negative_os_file_hash_during_initialize "$ordinal") == "$target" ]] || fail alias-declaration-hash
    os_alias_resolved+=("$path"); os_alias_hashes+=("$target")
    os_alias_starts+=("${#os_alias_link_paths[@]}"); os_alias_counts+=("$count")
    current=$ai; next=$((next+1)); seen=0
   elif [[ $kind == L ]]; then
    ((current>=0 && ai==current && seen<count)) || fail alias-declaration-link-order
    [[ $ordinal =~ ^[0-9]{1,2}$ ]] || fail alias-declaration-link-index
    ((10#$ordinal==seen)) || fail alias-declaration-link-index
    lexical "$path"
    [[ -n $target && ${#target}<=4096 && ! $target =~ [[:cntrl:]] && $metadata =~ ^0:0:777:[0-9]+:[1-9][0-9]*$ ]] || fail alias-declaration-link
    os_alias_link_paths+=("$path"); os_alias_link_targets+=("$target"); os_alias_link_metadata+=("$metadata")
    seen=$((seen+1))
   else fail alias-declaration-kind; fi
  done <<<"$rows"
 fi
 ((next==expected && (current<0 || seen==count) && ${#os_alias_resolved[@]}==expected && ${#os_alias_hashes[@]}==expected && ${#os_alias_starts[@]}==expected && ${#os_alias_counts[@]}==expected)) || fail alias-declaration-membership
 ((${#os_alias_link_paths[@]}==${#os_alias_link_targets[@]} && ${#os_alias_link_paths[@]}==${#os_alias_link_metadata[@]} && ${#os_alias_link_paths[@]}<=1024)) || fail alias-declaration-link-membership
 os_alias_declarations_ready=1
 readonly os_alias_declarations_ready
 readonly -a os_alias_resolved os_alias_hashes os_alias_starts os_alias_counts os_alias_link_paths os_alias_link_targets os_alias_link_metadata
}
# Initialization runs BEFORE os_cache_ready is published. Lookup is declaration-only.
negative_os_file_hash_during_initialize() {
 local LC_ALL=C p=$1 low=0 high=$((${#os_cache_file_paths[@]}-1)) mid
 while ((low<=high)); do
  mid=$(((low+high)/2))
  if [[ $p == "${os_cache_file_paths[$mid]}" ]]; then printf '%s' "${os_cache_file_hashes[$mid]}"; return 0
  elif [[ $p < "${os_cache_file_paths[$mid]}" ]]; then high=$((mid-1))
  else low=$((mid+1)); fi
 done
 return 1
}
# END N06 alias declaration cache candidate
alias_walk() {
 local alias_id=$1 literal current= seg target metadata expected lp index=0 steps=0 rest resolved slot
 [[ $os_alias_declarations_ready == 1 && $alias_id =~ ^[0-9]{1,2}$ ]] || fail alias-declaration-index
 alias_id=$((10#$alias_id)); ((alias_id<${#os_cache_alias_paths[@]})) || fail alias-declaration-index
 literal=${os_cache_alias_paths[$alias_id]}; lexical "$literal"
 local -a queue=("${components[@]}")
 while ((${#queue[@]})); do
  remaining >/dev/null; steps=$((steps+1)); ((steps<=256)) || fail alias-step-bound
  seg=${queue[0]}; queue=("${queue[@]:1}"); current+=/$seg
  if [[ -L $current ]]; then
   ((index<os_alias_counts[alias_id])) || fail alias-unreviewed-link
   slot=$((os_alias_starts[alias_id]+index)); lp=${os_alias_link_paths[$slot]}; [[ $lp == "$current" ]] || fail alias-unreviewed-link
   metadata=$(bounded stat -c '%u:%g:%a:%d:%i' -- "$current")
   expected=${os_alias_link_metadata[$slot]}
   [[ $metadata == "$expected" ]] || fail alias-lstat-pin
   target=$(bounded readlink -- "$current"); [[ $target == "${os_alias_link_targets[$slot]}" ]] || fail alias-target-pin
   [[ $(bounded stat -c '%u:%g:%a:%d:%i' -- "$current") == "$metadata" ]] || fail alias-link-changed
   [[ $target == /* ]] || target=${current%/*}/$target
   resolved=$(normalize_os_target "$target"); lexical "$resolved"
   queue=("${components[@]}" "${queue[@]}"); current=; index=$((index+1))
  else
   [[ -e $current && ! -L $current ]] || fail alias-node
   local node_metadata node_uid permissions
   node_metadata=$(bounded stat -c '%u:%a' -- "$current")
   [[ $node_metadata =~ ^([0-9]+):([0-7]+)$ ]] || fail alias-node
   node_uid=${BASH_REMATCH[1]}; permissions=${BASH_REMATCH[2]}
   [[ $node_uid == 0 ]] || fail alias-node
   (((8#$permissions & 0022)==0)) || fail alias-writable-node
   if ((${#queue[@]})); then [[ -d $current ]] || fail alias-parent; fi
  fi
 done
 [[ $index == "${os_alias_counts[$alias_id]}" && $current == "${os_alias_resolved[$alias_id]}" ]] || fail alias-resolution
 [[ -f $current && ! -L $current ]] || fail alias-final-file
 printf '%s' "$current"
}
# Data tables are built once from the already completely validated, SHA-pinned
# audit. They cache no filesystem results. Lookups use sorted indexed arrays and
# integer subscripts, never eval or caller-supplied associative-array subscripts.
os_cache_ready=0
declare -a os_cache_file_paths=() os_cache_file_hashes=() os_cache_alias_paths=() os_cache_alias_rows=()
negative_os_file_hash() {
 local LC_ALL=C p=$1 low=0 high=$((${#os_cache_file_paths[@]}-1)) mid
 [[ $os_cache_ready == 1 ]] || return 1
 while ((low<=high)); do
  mid=$(((low+high)/2))
  if [[ $p == "${os_cache_file_paths[$mid]}" ]]; then printf '%s' "${os_cache_file_hashes[$mid]}"; return 0
  elif [[ $p < "${os_cache_file_paths[$mid]}" ]]; then high=$((mid-1))
  else low=$((mid+1)); fi
 done
 return 1
}
negative_os_alias_record() {
 local LC_ALL=C p=$1 low=0 high=$((${#os_cache_alias_paths[@]}-1)) mid
 [[ $os_cache_ready == 1 ]] || return 1
 while ((low<=high)); do
  mid=$(((low+high)/2))
  if [[ $p == "${os_cache_alias_paths[$mid]}" ]]; then printf '%s' "${os_cache_alias_rows[$mid]}"; return 0
  elif [[ $p < "${os_cache_alias_paths[$mid]}" ]]; then high=$((mid-1))
  else low=$((mid+1)); fi
 done
 return 1
}
negative_os_cache_initialize() {
 local LC_ALL=C rows p h row previous= expected_files expected_aliases file_hash
 [[ $os_cache_ready == 0 && ${#os_cache_file_paths[@]} == 0 && ${#os_cache_alias_paths[@]} == 0 ]] || fail OS-cache-replay
 expected_files=$(bounded jq -er '.files|length' "$os_audit")
 expected_aliases=$(bounded jq -er '.aliases|length' "$os_audit")
 [[ $expected_files =~ ^[0-9]+$ && $expected_aliases =~ ^[0-9]+$ ]] && ((expected_files>0 && expected_files<=4096 && expected_aliases<=64)) || fail OS-cache-count
 rows=$(bounded jq -r '.files|sort_by(.path)|.[]|[.path,.sha256]|@tsv' "$os_audit")
 [[ -n $rows && ${#rows} -le 1048576 ]] || fail OS-cache-file-bytes
 while IFS=$'\t' read -r p h; do
  lexical "$p"; sha "$h"
  [[ -z $previous || $previous < "$p" ]] || fail OS-cache-file-order
  previous=$p; os_cache_file_paths+=("$p"); os_cache_file_hashes+=("$h")
 done <<<"$rows"
 ((${#os_cache_file_paths[@]}==expected_files && ${#os_cache_file_hashes[@]}==expected_files)) || fail OS-cache-file-membership
 rows=$(bounded jq -c '.aliases|sort_by(.literal)|.[]' "$os_audit")
 [[ ${#rows} -le 1048576 ]] || fail OS-cache-alias-bytes
 previous=
 if [[ -n $rows ]]; then
  while IFS= read -r row; do
   p=$(bounded jq -er '.literal' <<<"$row"); lexical "$p"
   h=$(bounded jq -er '.resolved_sha256' <<<"$row"); sha "$h"
   [[ -z $previous || $previous < "$p" ]] || fail OS-cache-alias-order
   previous=$p; os_cache_alias_paths+=("$p"); os_cache_alias_rows+=("$row")
   # Alias metadata remains the exact validated row; require its literal file
   # digest to match before it can be used by any physical audit operation.
   local low=0 high=$((${#os_cache_file_paths[@]}-1)) mid found=0
   while ((low<=high)); do
    mid=$(((low+high)/2))
    if [[ $p == "${os_cache_file_paths[$mid]}" ]]; then
     [[ $h == "${os_cache_file_hashes[$mid]}" ]] || fail OS-cache-alias-file-hash
     found=1; break
    elif [[ $p < "${os_cache_file_paths[$mid]}" ]]; then high=$((mid-1))
    else low=$((mid+1)); fi
   done
   ((found==1)) || fail OS-cache-alias-file-membership
  done <<<"$rows"
 fi
 ((${#os_cache_alias_paths[@]}==expected_aliases && ${#os_cache_alias_rows[@]}==expected_aliases)) || fail OS-cache-alias-membership
 negative_os_alias_declarations_initialize
 os_cache_ready=1
 readonly os_cache_ready
 readonly -a os_cache_file_paths os_cache_file_hashes os_cache_alias_paths os_cache_alias_rows
}

verify_os_path() {
 local p=$1 h=$2 count row resolved first before after
 [[ $os_cache_ready == 1 ]] || fail OS-cache-uninitialized
 resolved=$(negative_os_file_hash "$p") || fail OS-cache-file-unpinned
 [[ $resolved == "$h" ]] || fail OS-cache-file-hash
 if row=$(negative_os_alias_index "$p"); then count=1; else count=0; fi
 if [[ $count == 1 ]]; then
  [[ ${os_alias_hashes[$row]} == "$h" ]] || fail alias-hash-binding
  first=$(alias_walk "$row"); before=$(bounded stat -c '%d:%i:%s:%f:%h:%u:%g' -- "$first")
  pin_file "$first" "$h" "$MAX_INPUT_FILE_BYTES"
  resolved=$(alias_walk "$row"); after=$(bounded stat -c '%d:%i:%s:%f:%h:%u:%g' -- "$resolved")
  [[ $first == "$resolved" && $before == "$after" ]] || fail alias-before-after
 else [[ $count == 0 ]] || fail duplicate-alias; ancestor "${p%/*}"; pin_file "$p" "$h" "$MAX_INPUT_FILE_BYTES"; fi
}

# BEGIN ordinary-os-batch candidate definitions
# Source definitions only. Requires the unchanged fixture clock, cache, custody,
# bounded-command, lexical, and alias helpers. No standalone/root entry point.
# All arrays are indexed by integer; path text is never an evaluated subscript.
ordinary_os_batch_alias_index() {
 local p=$1 low=0 high=$((${#os_cache_alias_paths[@]}-1)) mid
 osb_alias_index=-1
 while ((low<=high)); do
  remaining >/dev/null
  mid=$(((low+high)/2))
  if [[ $p == "${os_cache_alias_paths[$mid]}" ]]; then osb_alias_index=$mid; return 0
  elif [[ $p < "${os_cache_alias_paths[$mid]}" ]]; then high=$((mid-1))
  else low=$((mid+1)); fi
 done
}
ordinary_os_batch_leaf_index() {
 local p=$1 low=0 high=$((${#osb_leaf_paths[@]}-1)) mid
 osb_leaf_index=-1
 while ((low<=high)); do
  remaining >/dev/null
  mid=$(((low+high)/2))
  if [[ $p == "${osb_leaf_paths[$mid]}" ]]; then osb_leaf_index=$mid; return 0
  elif [[ $p < "${osb_leaf_paths[$mid]}" ]]; then high=$((mid-1))
  else low=$((mid+1)); fi
 done
}
ordinary_os_batch_decimal_at_most() {
 local value=$1 cap=$2
 [[ $value =~ ^(0|[1-9][0-9]{0,19})$ && $cap =~ ^(0|[1-9][0-9]{0,19})$ ]] || return 1
 ((${#value}<${#cap})) && return 0
 ((${#value}==${#cap})) && [[ $value == "$cap" || $value < "$cap" ]]
}
# Detached physical-stat data parser. The full-pass owner supplies exact ordered
# node/leaf sets, retained input FD, and pre records through Bash dynamic scope.
# Tests can exercise this parser without constructing a root/native owner.
ordinary_os_batch_parse_snapshot() {
 local pass=$1 read_fd=$2 field n i=0 p record mode leaf
 local -a fields=()
 [[ $pass == pre || $pass == post ]] || fail OS-batch-pass
 while :; do
  remaining >/dev/null; fields=(); field=
  if ! IFS= read -r -d '' field <&"$read_fd"; then [[ -z $field ]] || fail OS-batch-partial-row; break; fi
  fields+=("$field")
  for ((n=1;n<10;n++)); do field=; IFS= read -r -d '' field <&"$read_fd" || fail OS-batch-partial-row; fields+=("$field"); done
  ((i<${#osb_node_paths[@]})) || fail OS-batch-node-count
  p=${fields[0]}; [[ $p == "${osb_node_paths[$i]}" ]] || fail OS-batch-node-order
  for n in 1 2 3 5 6 7; do [[ ${fields[$n]} =~ ^(0|[1-9][0-9]{0,19})$ ]] || fail OS-batch-stat-number; done
  [[ ${fields[2]} != 0 && ${fields[4]} =~ ^[0-9a-f]{1,8}$ ]] || fail OS-batch-stat-mode
  for n in 8 9; do [[ -n ${fields[$n]} && ${#fields[$n]} -le 64 && ! ${fields[$n]} =~ [[:cntrl:]] ]] || fail OS-batch-stat-time; done
  mode=$((16#${fields[4]}))
  [[ ${fields[6]} == 0 ]] && (( (mode & 0022)==0 )) || fail OS-batch-owner-mode
  ordinary_os_batch_leaf_index "$p"; leaf=$osb_leaf_index
  if ((leaf>=0)); then
   (( (mode & 0170000)==0100000 )) && [[ ${fields[5]} == 1 ]] || fail OS-batch-file-type-links
   ordinary_os_batch_decimal_at_most "${fields[3]}" "$MAX_INPUT_FILE_BYTES" || fail OS-batch-file-bytes
  else (( (mode & 0170000)==0040000 )) || fail OS-batch-ancestor-type; fi
  record=${fields[1]}
  for ((n=2;n<10;n++)); do record+=$'\t'${fields[$n]}; done
  if [[ $pass == pre ]]; then osb_pre_records+=("$record")
  else [[ $record == "${osb_pre_records[$i]}" ]] || fail OS-batch-before-after; fi
  ((i+=1))
 done
 ((i==${#osb_node_paths[@]})) || fail OS-batch-node-count
}
# All stat invocations use physical/default (no -L) semantics. Exact ancestor
# membership and types are checked in BOTH snapshots, including / itself.
ordinary_os_batch_snapshot() {
 local path=$1 fd=$2 i=0 arg_bytes row_bytes projected=0 n
 local -a chunk=()
 # GNU stat's fixed numeric/timestamp fields are bounded. Reserve 1024 bytes
 # per row beyond its exact path bytes before dispatch; the parser additionally
 # enforces each numeric/time field length and the retained-file 16 MiB cap.
 for ((n=0;n<${#osb_node_paths[@]};n++)); do
  remaining >/dev/null
  ((projected+=${#osb_node_paths[$n]}+1024))
  ((projected<=16777216)) || fail OS-batch-inventory-bytes
 done
 while ((i<${#osb_node_paths[@]})); do
  remaining >/dev/null; chunk=(); arg_bytes=0
  while ((i<${#osb_node_paths[@]})); do
   row_bytes=$((${#osb_node_paths[$i]}+1))
   ((arg_bytes+row_bytes<=16384)) || break
   chunk+=("${osb_node_paths[$i]}"); ((arg_bytes+=row_bytes)); ((i+=1))
  done
  ((${#chunk[@]}>0)) || fail OS-batch-argument-bound
  # No pipeline/function child inherits the fixture cleanup trap. The original
  # bounded dispatcher joins this one trusted stat command before FD pinning.
  bounded stat --printf '%n\0%d\0%i\0%s\0%f\0%h\0%u\0%g\0%y\0%z\0' -- "${chunk[@]}" >&"$fd"
  batch_scratch_pin "$path" "$fd" 16777216
  osb_snapshot_bytes=$(bounded stat -c %s -- "$path")
  remaining >/dev/null
 done
}
# The manifest path is selected by the full-pass owner; this separately testable
# procedure performs no qualification and can return only command success/fault.
ordinary_os_batch_check_hashes() {
 remaining >/dev/null
 bounded sha256sum --check --strict --status -- "$1"
 remaining >/dev/null
}
ordinary_os_batch_verify_aliases() {
 local i p h
 for ((i=0;i<${#os_cache_alias_paths[@]};i++)); do
  remaining >/dev/null; p=${os_cache_alias_paths[$i]}; h=$(negative_os_file_hash "$p") || fail OS-cache-file-unpinned
  verify_os_path "$p" "$h"
 done
}
# Fixed full-pass API; accepts neither paths nor a replacement owner/token.
ordinary_os_batch_full_pass() {
 local pass=$1 uuid scratch root_before root_after p h parent i bytes=0 rows=0 row_bytes previous=
 local osb_alias_index=-1 osb_leaf_index=-1 osb_snapshot_bytes=0 ordinary_count=0 alias_count=0
 local paths_fd sorted_fd pre_fd post_fd checks_fd read_fd
 local -r batch_fd_owner_pid=$BASHPID
 local -a osb_leaf_paths=() osb_leaf_hashes=() osb_node_paths=() osb_pre_records=()
 [[ $pass == initial || $pass == final ]] || fail OS-batch-pass
 [[ $os_cache_ready == 1 ]] || fail OS-cache-uninitialized
 ((${#os_cache_file_paths[@]}>=1 && ${#os_cache_file_paths[@]}<=4096 && ${#os_cache_file_paths[@]}==${#os_cache_file_hashes[@]} && ${#os_cache_alias_paths[@]}<=64)) || fail OS-batch-cache-count
 remaining >/dev/null
 IFS= read -r uuid </proc/sys/kernel/random/uuid || fail OS-batch-scratch-name
 [[ $uuid =~ ^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$ ]] || fail OS-batch-scratch-name
 scratch=/run/appsurface-evidence-os-audit-$uuid
 ancestor /run; [[ ! -e $scratch && ! -L $scratch ]] || fail audit-scratch-collision
 bounded install -d -o 0 -g 0 -m 700 -- "$scratch"
 root_before=$(bounded stat -c '%d:%i:%f:%u:%g:%a' -- "$scratch")
 [[ $(bounded stat -c '%u:%g:%a' -- "$scratch") == 0:0:700 ]] || fail audit-scratch-custody
 for p in paths sorted pre post checks; do batch_create_scratch_leaf "$scratch/$p"; done
 exec {paths_fd}<>"$scratch/paths"; exec {sorted_fd}<>"$scratch/sorted"
 exec {pre_fd}<>"$scratch/pre"; exec {post_fd}<>"$scratch/post"; exec {checks_fd}<>"$scratch/checks"
 for ((i=0;i<${#os_cache_file_paths[@]};i++)); do
  remaining >/dev/null; p=${os_cache_file_paths[$i]}; h=${os_cache_file_hashes[$i]}
  lexical "$p"; [[ $p != *\\* ]] || fail OS-batch-hash-name; sha "$h"
  [[ -z $previous || $previous < "$p" ]] || fail OS-batch-cache-order; previous=$p
  ordinary_os_batch_alias_index "$p"
  if ((osb_alias_index>=0)); then ((alias_count+=1)); continue; fi
  osb_leaf_paths+=("$p"); osb_leaf_hashes+=("$h"); ((ordinary_count+=1))
  parent=$p
  while :; do
   remaining >/dev/null; row_bytes=$((${#parent}+1)); ((bytes+=row_bytes)); ((rows+=1))
   ((bytes<=16777216 && rows<=65536)) || fail OS-batch-inventory-bound
   printf '%s\n' "$parent" >&"$paths_fd"
   [[ $parent != / ]] || break
   parent=${parent%/*}; [[ -n $parent ]] || parent=/
  done
 done
 ((ordinary_count+alias_count==${#os_cache_file_paths[@]} && alias_count==${#os_cache_alias_paths[@]})) || fail OS-batch-file-membership
 batch_scratch_pin "$scratch/paths" "$paths_fd" 16777216
 # Sorted output cannot exceed the already counted input bytes.
 bounded sort --temporary-directory="$scratch" -u -- "$scratch/paths" >&"$sorted_fd"
 batch_scratch_pin "$scratch/sorted" "$sorted_fd" 16777216
 exec {read_fd}<"$scratch/sorted"; batch_scratch_pin "$scratch/sorted" "$read_fd" 16777216
 previous=
 while IFS= read -r p <&"$read_fd"; do
  remaining >/dev/null; [[ -n $p && ! $p =~ [[:cntrl:]] && $p != *\\* && ${#p} -le 4096 && ( -z $previous || $previous < "$p" ) ]] || fail OS-batch-node-order
  [[ $p == / ]] || lexical "$p"
  osb_node_paths+=("$p"); previous=$p; ((${#osb_node_paths[@]}<=65536)) || fail OS-batch-node-count
 done
 exec {read_fd}<&-
 ordinary_os_batch_snapshot "$scratch/pre" "$pre_fd"
 exec {read_fd}<"$scratch/pre"; batch_scratch_pin "$scratch/pre" "$read_fd" 16777216
 ordinary_os_batch_parse_snapshot pre "$read_fd"; exec {read_fd}<&-
 bytes=0
 for ((i=0;i<${#osb_leaf_paths[@]};i++)); do
  remaining >/dev/null; row_bytes=$((64+2+${#osb_leaf_paths[$i]}+1)); ((bytes+=row_bytes))
  ((bytes<=8388608)) || fail OS-batch-check-bytes
  printf '%s  %s\n' "${osb_leaf_hashes[$i]}" "${osb_leaf_paths[$i]}" >&"$checks_fd"
 done
 batch_scratch_pin "$scratch/checks" "$checks_fd" 8388608
 if ((ordinary_count>0)); then ordinary_os_batch_check_hashes "$scratch/checks"; fi
 osb_snapshot_bytes=0; ordinary_os_batch_snapshot "$scratch/post" "$post_fd"
 exec {read_fd}<"$scratch/post"; batch_scratch_pin "$scratch/post" "$read_fd" 16777216
 ordinary_os_batch_parse_snapshot post "$read_fd"; exec {read_fd}<&-
 # Alias literal membership was counted above; unchanged verify_os_path performs
 # BOTH original alias walks and original resolved-file pin checks on each pass.
 ordinary_os_batch_verify_aliases
 for p in paths sorted pre post checks; do
  case $p in paths) i=$paths_fd;; sorted) i=$sorted_fd;; pre) i=$pre_fd;; post) i=$post_fd;; checks) i=$checks_fd;; esac
  if [[ $p == checks ]]; then batch_scratch_pin "$scratch/$p" "$i" 8388608
  else batch_scratch_pin "$scratch/$p" "$i" 16777216; fi
 done
 root_after=$(bounded stat -c '%d:%i:%f:%u:%g:%a' -- "$scratch")
 [[ $root_after == "$root_before" ]] || fail audit-scratch-substitution
 exec {paths_fd}>&-; exec {sorted_fd}>&-; exec {pre_fd}>&-; exec {post_fd}>&-; exec {checks_fd}>&-
 bounded rm -- "$scratch/paths" "$scratch/sorted" "$scratch/pre" "$scratch/post" "$scratch/checks"
 bounded rm -d -- "$scratch"
 remaining >/dev/null
}
# END ordinary-os-batch candidate definitions

for key in source_root source_manifest source_manifest_sha256 source_nodes source_nodes_sha256 payload_root payload_manifest payload_manifest_sha256 payload_nodes payload_nodes_sha256 runtime_root runtime_manifest runtime_manifest_sha256 runtime_nodes runtime_nodes_sha256 source_review source_review_sha256 build_receipt build_receipt_sha256 os_audit os_audit_sha256 source_revision base_revision workflow_identity entry runtime_host entry_sha256 n02_uid n02_gid; do [[ -n ${!key} ]] || fail missing-input; done
[[ $source_revision =~ ^[0-9a-f]{40}$ && $base_revision =~ ^[0-9a-f]{40}$ ]] || fail revisions
[[ $workflow_identity =~ ^[A-Za-z0-9/._:@-]{1,256}$ ]] || fail workflow
[[ $entry != /* && $runtime_host != /* && $entry != *..* && $runtime_host != *..* && $runtime_host != *=* ]] || fail relative-entry-runtime
[[ $n02_uid =~ ^[0-9]+$ && $n02_gid =~ ^[0-9]+$ ]] && ((n02_uid>0 && n02_gid>0 && n02_uid<4294967295 && n02_gid<4294967295)) || fail unprivileged-selection
pin_file "$build_receipt" "$build_receipt_sha256" "$MAX_NODE_JSON_BYTES"
# Current actual publication is checked at this retained-root boundary. Native
# tree, OS/ELF, request, kernel, process and custody checks below remain mandatory.
for key in source_capture source_capture_sha256 binding_validator binding_validator_sha256 binding_deadline_monotonic; do [[ -n ${!key} ]] || fail current-binding-input; done
pin_file "$source_capture" "$source_capture_sha256" "$MAX_MANIFEST_BYTES"
pin_file "$binding_validator" "$binding_validator_sha256" 131072
bounded /usr/bin/python3 -I -S -B "$binding_validator" root-verify \
 --source-capture "$source_capture" --build-receipt "$build_receipt" \
 --source "$source_revision" --selector N05 \
 --deadline-monotonic "$binding_deadline_monotonic" >/dev/null
readonly NEGATIVE_SOURCE_REVISION=$source_revision
diagnostic_stage=source-input
verify_tree "$source_root" "$source_manifest" "$source_manifest_sha256" "$source_nodes" "$source_nodes_sha256" source
diagnostic_stage=tool-input
verify_tree "$payload_root" "$payload_manifest" "$payload_manifest_sha256" "$payload_nodes" "$payload_nodes_sha256" tool
diagnostic_stage=runtime-input
verify_tree "$runtime_root" "$runtime_manifest" "$runtime_manifest_sha256" "$runtime_nodes" "$runtime_nodes_sha256" runtime
diagnostic_stage=os-input
pin_file "$source_review" "$source_review_sha256" "$MAX_MANIFEST_BYTES"
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
fixture_stage_observation begin declaration-cache
negative_os_cache_initialize
fixture_stage_observation end declaration-cache
os_files=$(bounded jq -r '.files[]|[.path,.sha256]|@tsv' "$os_audit")
fixture_stage_observation begin ordinary-os-initial
ordinary_os_batch_full_pass initial
fixture_stage_observation end ordinary-os-initial
fixture_stage_observation begin elf-closure
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
  # Dominated: successful verify_os_path above already required this exact file declaration/hash.
 done <<<"$deps"
done <<<"$elf_records"
fixture_stage_observation end elf-closure
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
  if ! current=$(owned_identity "$pid"); then
   # The original leader may exit between the live probe and its proc read.
   # Recheck absence; a live unreadable or substituted process still rejects.
   left >/dev/null || return 124
   ! kill -0 "$pid" 2>/dev/null || return 125
   break
  fi
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
(ulimit -f 262144; ulimit -c 0; try_bounded strace -f -qq -e trace=openat,openat2,mkdir,mkdirat,unlink,unlinkat,rmdir,connect -o "$log/n02.trace" setpriv --reuid="$n02_uid" --regid="$n02_gid" --clear-groups /usr/bin/env -i PATH=/usr/bin:/bin HOME=/nonexistent LANG=C.UTF-8 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_CLI_HOME=/tmp XDG_RUNTIME_DIR=/tmp "$host" "$managed" evidence supervise --request "$request") >"$log/n02.stdout" 2>"$log/n02.stderr"
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
  try_bounded jq -cS -n --arg source "$source_revision" --argjson worker "$sampled_pid" --argjson broker "$broker_actual_pid" \
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
 fixed_env=(PATH=/usr/bin:/bin HOME=/nonexistent LANG=C.UTF-8 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_CLI_HOME=/tmp XDG_RUNTIME_DIR=/tmp)
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
fixed_env=(PATH=/usr/bin:/bin HOME=/nonexistent LANG=C.UTF-8 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_CLI_HOME=/tmp XDG_RUNTIME_DIR=/tmp)
unsafe='DOTNET_STARTUP_HOOKS DOTNET_ADDITIONAL_DEPS DOTNET_SHARED_STORE DOTNET_ROOT DOTNET_ROOT_X64 DOTNET_HOST_PATH DOTNET_ROLL_FORWARD DOTNET_ROLL_FORWARD_TO_PRERELEASE DOTNET_MULTILEVEL_LOOKUP CORECLR_ENABLE_PROFILING CORECLR_PROFILER CORECLR_PROFILER_PATH CORECLR_PROFILER_PATH_64 COR_ENABLE_PROFILING COR_PROFILER COR_PROFILER_PATH COMPlus_ReadyToRun COMPlus_ZapDisable'
props=(--property=Type=exec --property=User=0 --property=Group=0 --property=KillMode=control-group --property=Restart=no --property=RemainAfterExit=no --property=SendSIGKILL=yes --property=FinalKillSignal=9 --property=RuntimeMaxSec=240s --property=TimeoutStopSec=5s "--property=ConditionPathExists=$guard/armed" --property=PassEnvironment= "--property=UnsetEnvironment=$unsafe" "--property=Environment=${fixed_env[*]}")
diagnostic_stage=sealed-inputs
fixture_stage_observation begin sealed-trees
# Full destination and alias checks immediately before native dispatch. No extra nodes are skipped.
verify_tree "$stage/tool" "$payload_manifest" "$payload_manifest_sha256" "$payload_nodes" "$payload_nodes_sha256" tool 1 1
verify_tree "$stage/runtime" "$runtime_manifest" "$runtime_manifest_sha256" "$runtime_nodes" "$runtime_nodes_sha256" runtime 1 0
verify_tree "$stage/subject" "$source_manifest" "$source_manifest_sha256" "$source_nodes" "$source_nodes_sha256" source 1 0
pin_file "$build_receipt" "$build_receipt_sha256" "$MAX_NODE_JSON_BYTES"
fixture_stage_observation end sealed-trees
fixture_stage_observation begin ordinary-os-final
ordinary_os_batch_full_pass final
fixture_stage_observation end ordinary-os-final
pin_file "$os_audit" "$os_audit_sha256" 1048576
native_life_remaining=$(remaining)
if ! ((native_life_remaining>246 && job_deadline_epoch-$(bounded date -u +%s)>245)); then
 # Closed failure-only timing data. This does not renew an interval or dispatch native work.
 printf '{"schema":"issue779-negative-audit-cost-v1","stage":"sealed-inputs","elapsed_seconds":%d,"remaining_seconds":%d,"file_count":%d,"alias_count":%d}\n' "$(( $(monotonic)-fixture_start ))" "$native_life_remaining" "${#os_cache_file_paths[@]}" "${#os_cache_alias_paths[@]}" >&2
 fail native-life-outside-fixture
fi
diagnostic_stage=n01
launched=1
(ulimit -f 8192; exec setsid timeout --signal=KILL "$(remaining)" systemd-run --quiet --wait --pipe --unit="$owner" "${props[@]}" /usr/bin/env -i "${fixed_env[@]}" "$host" "$managed" evidence supervise --request "$request" >"$log/n01.stdout" 2>"$log/n01.stderr") & launch_pid=$!
pin_owned_pg "$launch_pid"

# Fixed negative case: no caller case, PID, UID/GID, path or environment selector.
set +e; join_owned launch_pid; negative_launch=$?; set -e
if [[ $negative_launch != 1 || -n $launch_pid || -s $log/n01.stdout ]]; then
 launch_retired=0; [[ -n $launch_pid ]] || launch_retired=1
 root_stdout_empty=0; [[ -s $log/n01.stdout ]] || root_stdout_empty=1
 printf 'NEGATIVE_LAUNCH_JOIN:%d:%d:%d\n' "$negative_launch" "$launch_retired" "$root_stdout_empty" >&2
 fail negative-root-outcome
fi
[[ $(bounded stat -c '%u:%g:%a:%h' "$log/n01.stderr") == 0:0:600:1 ]] || fail negative-root-stream-owner
[[ $(bounded stat -c %s "$log/n01.stderr") -le 14336 ]] || fail negative-root-stream-bound
workspace=/run/appsurface-evidence-$G
[[ -d $workspace && ! -L $workspace && $(bounded stat -c '%u:%a' "$workspace") == 0:750 ]] || fail negative-generation
# The immutable holder currently emits hash/count JSON only. Independent raw stream retention is missing.
# This fixed gate remains false until separately reviewed original-holder producer integration exists.
((NEGATIVE_RAW_STREAM_PRODUCER_CLEAR==1 && NEGATIVE_PYTHON_TRUST_CLEAR==1 && NEGATIVE_ROOT_TERMINAL_CLEAR==1)) || fail negative-prerequisite-pending
fixture_stage_observation begin negative-python-qualification
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
 python_hash=$(negative_os_file_hash "$python_file") || fail negative-stdlib-file-unpinned
 verify_os_path "$python_file" "$python_hash"
 if [[ $(bounded head -c 4 "$python_file" | od -An -tx1 | tr -d ' \n') == 7f454c46 ]]; then
  python_resolved=$python_file
  if python_alias=$(negative_os_alias_index "$python_file"); then
   python_resolved=$(alias_walk "$python_alias")
  fi
  bounded jq -e --arg p "$python_resolved" '[.elf[]|select(.path==$p)]|length==1' "$os_audit" >/dev/null || fail negative-stdlib-elf-unreviewed
 fi
done <<<"$stdlib_files"
stdlib_after=$(negative_stdlib_inventory)
negative_stdlib_exact "$stdlib_expected" "$stdlib_after" || fail negative-stdlib-membership-after
pin_file "$os_audit" "$os_audit_sha256" 1048576
remaining >/dev/null
fixture_stage_observation end negative-python-qualification
bounded /usr/bin/env -i PATH=/usr/bin:/bin LC_ALL=C /usr/bin/python3.12 -I -S -B - "$private" "$workspace" "$G" "$policy_sha" <<'NEGATIVE_PY'
import base64 as _issue779_b64, hashlib as _issue779_hash, zlib as _issue779_zlib
_issue779_fixed_len=35520
_issue779_fixed_sha='d9f9aa584695cd2ee753a4ce9fe2cf7c0ca2b8e5d12ae4c68f03affa2ead1145'
_issue779_fixed_data='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'
if not 0 < _issue779_fixed_len <= 262144 or len(_issue779_fixed_data)>524288:
    raise ValueError('fixed-python-bound')
_issue779_decoder=_issue779_zlib.decompressobj()
_issue779_code=_issue779_decoder.decompress(_issue779_b64.b64decode(_issue779_fixed_data,validate=True),_issue779_fixed_len+1)
if (len(_issue779_code)!=_issue779_fixed_len or not _issue779_decoder.eof or
        _issue779_decoder.unused_data or _issue779_decoder.unconsumed_tail or
        _issue779_hash.sha256(_issue779_code).hexdigest()!=_issue779_fixed_sha):
    raise ValueError('fixed-python-pin')
exec(compile(_issue779_code,'<stdin>','exec'),globals())
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
