#!/usr/bin/env bash
set -euo pipefail
export LC_ALL=C PATH=/usr/sbin:/usr/bin:/sbin:/bin
umask 077
readonly FIXTURE_SECONDS=600 CLEANUP_RESERVE=30
readonly NEGATIVE_CASE=N16
readonly NEGATIVE_CORE_BASE=a358d47ef36939718536b13c27b038faf307299b
readonly N16_SOURCE_CAPTURE_REVIEW_CLEAR=1 N16_FIXTURE_REVIEW_CLEAR=1 N16_ROOT_CLEANUP_REVIEW_CLEAR=1
readonly N16_EXPECTED_SOURCE_PARENT=3002a69b8b54c5c9e8955782e34006e0db8e02bf
readonly MAX_MANIFEST_BYTES=8388608 MAX_ROWS=32768 MAX_NODES=65536
readonly MAX_INPUT_FILE_BYTES=2147483648 MAX_INPUT_TREE_BYTES=4294967296
readonly MAX_NODE_JSON_BYTES=4194304 CORE_FILE_BYTES=268435456 CORE_TREE_BYTES=1073741824 CORE_NODES=8192
n16_parser_root= n16_parser_sha256= source_capture= source_capture_sha256= root_projection= root_projection_sha256= source_count=
mode=prepare-only; mode_selected=0; phase=work; diagnostic_stage=arguments; diagnostic_failure_reported=0
source_root= source_manifest= source_manifest_sha256= payload_root= payload_manifest= payload_manifest_sha256=
runtime_root= runtime_manifest= runtime_manifest_sha256= source_review= source_review_sha256=
build_receipt= build_receipt_sha256= os_audit= os_audit_sha256= reviewed_script_sha256=
source_revision= base_revision= workflow_identity= entry= runtime_host= entry_sha256=
source_nodes= source_nodes_sha256= payload_nodes= payload_nodes_sha256= runtime_nodes= runtime_nodes_sha256=
fail() { printf 'CHECKPOINT_REJECTED:%s\n' "$1" >&2; exit 1; }
while (($#)); do
 case "$1" in
 --prepare-only|--execute) ((mode_selected==0)) || fail duplicate-mode; mode=${1#--}; mode_selected=1; shift ;;
 --n16-parser-root|--n16-parser-sha256|--source-capture|--source-capture-sha256|--root-projection|--root-projection-sha256|--source-count|--source-root|--source-manifest|--source-manifest-sha256|--source-nodes|--source-nodes-sha256|--payload-root|--payload-manifest|--payload-manifest-sha256|--payload-nodes|--payload-nodes-sha256|--runtime-root|--runtime-manifest|--runtime-manifest-sha256|--runtime-nodes|--runtime-nodes-sha256|--source-review|--source-review-sha256|--build-receipt|--build-receipt-sha256|--os-audit|--os-audit-sha256|--reviewed-script-sha256|--source-revision|--base-revision|--workflow-identity|--entry|--runtime-host|--entry-sha256)
 (($#>=2)) || fail option-value; key=${1#--}; key=${key//-/_}; [[ -z ${!key} ]] || fail duplicate-option
 printf -v "$key" '%s' "$2"; shift 2 ;;
 *) fail unknown-option ;;
 esac
done
if [[ $mode == prepare-only && -z $source_root ]]; then
 printf '%s\n' 'PREPARATION_ONLY:NO_INPUTS_VERIFIED:N16_NOT_RUN'; exit 0
fi
((N16_SOURCE_CAPTURE_REVIEW_CLEAR==1 && N16_FIXTURE_REVIEW_CLEAR==1 && N16_ROOT_CLEANUP_REVIEW_CLEAR==1)) || fail N16-independent-review-pending
[[ $OSTYPE == linux* ]] || fail Linux-x64
for t in dd sha256sum stat find timeout head jq readelf awk sort cmp cp chmod chown install systemd-run systemctl getent setpriv strace date sleep od tr cut cat grep wc readlink uname setsid ps bash mv; do command -v "$t" >/dev/null || fail missing-trusted-tool; done
monotonic() { local up rest; IFS=' ' read -r up rest </proc/uptime || return 1; [[ $up =~ ^[0-9]+\.[0-9]+$ ]] || return 1; printf '%s' "${up%%.*}"; }
fixture_start=$(monotonic) || fail monotonic-clock
readonly fixture_start hard_end=$((fixture_start+FIXTURE_SECONDS)) work_end=$((fixture_start+FIXTURE_SECONDS-CLEANUP_RESERVE))
left() {
 local now stamp rest end=$work_end
 [[ $phase != cleanup ]] || end=$hard_end
 if [[ $phase != cleanup && ${n03_startup_active:-0} == 1 ]]; then
  [[ ${n03_startup_end:-} =~ ^[0-9]+$ ]] || return 1
  ((n03_startup_end>=end)) || end=$n03_startup_end
 fi
 IFS=' ' read -r stamp rest </proc/uptime || return 1
 [[ $stamp =~ ^[0-9]+\.[0-9]+$ ]] || return 1
 now=${stamp%%.*}
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
  arguments|platform|source-input|tool-input|runtime-input|os-input|root-layout|tool-copy|runtime-copy|subject-copy|request-preparation|sealed-inputs|python-trust|n02|n01) stage=$diagnostic_stage ;;
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
   [[ -e $current && ! -L $current && $(bounded stat -c %u -- "$current") == 0 ]] || fail alias-node
   local permissions; permissions=$(bounded stat -c %a -- "$current"); (((8#$permissions & 0022)==0)) || fail alias-writable-node
   if ((${#queue[@]})); then [[ -d $current ]] || fail alias-parent; fi
  fi
 done
 [[ $index == "${os_alias_counts[$alias_id]}" && $current == "${os_alias_resolved[$alias_id]}" ]] || fail alias-resolution
 [[ -f $current && ! -L $current ]] || fail alias-final-file
 printf '%s' "$current"
}
verify_os_path() {
 local p=$1 h=$2 count row resolved first before after
 if row=$(negative_os_alias_index "$p"); then count=1; else count=0; fi
 if [[ $count == 1 ]]; then
  [[ ${os_alias_hashes[$row]} == "$h" ]] || fail alias-hash-binding
  first=$(alias_walk "$row"); before=$(bounded stat -c '%d:%i:%s:%f:%h:%u:%g' -- "$first")
  pin_file "$first" "$h" "$MAX_INPUT_FILE_BYTES"
  resolved=$(alias_walk "$row"); after=$(bounded stat -c '%d:%i:%s:%f:%h:%u:%g' -- "$resolved")
  [[ $first == "$resolved" && $before == "$after" ]] || fail alias-before-after
 else [[ $count == 0 ]] || fail duplicate-alias; ancestor "${p%/*}"; pin_file "$p" "$h" "$MAX_INPUT_FILE_BYTES"; fi
}

# BEGIN reviewed ordinary OS cache and batch definitions
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
  while ((i<${#osb_node_paths[@]} && ${#chunk[@]}<32)); do
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
# END reviewed ordinary OS cache and batch definitions

for key in source_root source_manifest source_manifest_sha256 source_nodes source_nodes_sha256 payload_root payload_manifest payload_manifest_sha256 payload_nodes payload_nodes_sha256 runtime_root runtime_manifest runtime_manifest_sha256 runtime_nodes runtime_nodes_sha256 source_review source_review_sha256 build_receipt build_receipt_sha256 os_audit os_audit_sha256 source_revision base_revision workflow_identity entry runtime_host entry_sha256 source_count source_capture source_capture_sha256 root_projection root_projection_sha256 n16_parser_root n16_parser_sha256; do [[ -n ${!key} ]] || fail missing-input; done
[[ $source_revision =~ ^[0-9a-f]{40}$ && $base_revision == "$N16_EXPECTED_SOURCE_PARENT" && $source_count =~ ^[1-9][0-9]{0,6}$ ]] || fail revisions-or-source-count
sha "$source_capture_sha256"; sha "$root_projection_sha256"; sha "$n16_parser_sha256"
pin_file "$source_capture" "$source_capture_sha256" "$MAX_NODE_JSON_BYTES"
pin_file "$root_projection" "$root_projection_sha256" "$MAX_NODE_JSON_BYTES"
[[ $(bounded jq -er '(.files|type=="object") and (.files|length) == ($n|tonumber)' --arg n "$source_count" "$source_nodes") == true ]] || fail source-map-count
[[ $workflow_identity =~ ^[A-Za-z0-9/._:@-]{1,256}$ ]] || fail workflow
[[ $entry != /* && $runtime_host != /* && $entry != *..* && $runtime_host != *..* && $runtime_host != *=* ]] || fail relative-entry-runtime
pin_file "$build_receipt" "$build_receipt_sha256" "$MAX_NODE_JSON_BYTES"
# The root projection is the actual compiled-image build receipt, not caller JSON.
[[ $root_projection_sha256 == "$build_receipt_sha256" ]] || fail root-image-build-receipt-binding
bounded cmp -- "$root_projection" "$build_receipt" >/dev/null || fail root-image-build-receipt-bytes
# Capture binds every source byte and Git mode to the original direct-parent commit.
bounded jq -e --arg source "$source_revision" --arg parent "$N16_EXPECTED_SOURCE_PARENT" \
 --argjson count "$source_count" --slurpfile nodes "$source_nodes" '
 .schema=="issue779-csharp-common-source-capture-v4" and .clean==true and
 .native_credit==false and .draft_pr_created==false and .index_tree_blob_modes_equal==true and
 .validation_commit==$source and .previous_validation_commit==$parent and
 .source_tree=="e2860d8ef477f455d2e1e27b623b463f14d12365" and .source_count==$count and
 (.source.sha256|type=="object" and length==$count) and
 (.source.modes|type=="object" and length==$count) and
 (.source.sha256|keys)==($nodes[0].files|keys) and
 (.source.modes|keys)==(.source.sha256|keys) and
 . as $capture | all($capture.source.sha256|to_entries[];
  . as $file | $nodes[0].files[$file.key].sha256==$file.value and
  $nodes[0].files[$file.key].mode==$capture.source.modes[$file.key])
' "$source_capture" >/dev/null || fail actual-source-capture-full-binding
bounded jq -e --arg source "$source_revision" --arg sn "$source_nodes_sha256" --arg pn "$payload_nodes_sha256" --arg rn "$runtime_nodes_sha256" \
 --argjson source_count "$source_count" --arg st "$source_manifest_sha256" --arg pt "$payload_manifest_sha256" --arg rt "$runtime_manifest_sha256" '
 .schema=="issue779-csharp-fdd-build-v5" and .exit==0 and
 .source_commit==$source and .private_qualification=="N16" and
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
negative_os_cache_initialize
ordinary_os_batch_full_pass initial
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
if [[ $mode == prepare-only ]]; then printf '%s\n' 'PREPARATION_INPUT_PINS_MATCHED:N16_NOT_RUN'; exit 0; fi
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
   # kill -0 and /proc identity are separate observations. The original child
   # may exit between them; only that observed disappearance permits wait.
   kill -0 "$pid" 2>/dev/null && return 125
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
owned_registration_retired() {
 local pid=$1
 [[ -n $pid ]] || return 1
 [[ ! ${owned_start[$pid]+present} ]]
}
cleanup() {
 local original=$? bad=0 name status=0 cg unit
 trap - EXIT INT TERM; phase=cleanup
 for name in launch_pid; do signal_owned "$name" || bad=1; done
 for name in launch_pid; do join_owned "$name" || bad=1; done
 if ((launched)); then
  try_bounded systemctl kill --kill-whom=all --signal=KILL "$worker" "$owner" >"$log/kill.log" 2>&1 || :
  try_bounded systemctl stop "$worker" "$owner" >"$log/stop.log" 2>&1 || :
 fi
 for unit in "$worker" "$owner"; do
  cg=/sys/fs/cgroup/system.slice/$unit
  if [[ -e $cg || -L $cg ]]; then
   [[ ! -L $cg ]] && [[ $(try_bounded stat -f -c %t "$cg") == 63677270 ]] && try_bounded grep -qx 'populated 0' "$cg/cgroup.events" || bad=1
  fi
 done
 left >/dev/null || bad=1
 if ((original!=0 || bad!=0 || success!=1)); then printf '%s\n' 'NATIVE_FIXTURE_FAILED:PATHS_AND_ACCOUNTS_PRESERVED'; exit 1; fi
 [[ -f $private/n16-record-summary.json && ! -L $private/n16-record-summary.json ]] || exit 1
 absent_accounts || exit 1
 n16_owner_load=$(try_bounded systemctl show "$owner" -p LoadState --value) || exit 1
 n16_worker_load=$(try_bounded systemctl show "$worker" -p LoadState --value) || exit 1
 [[ $n16_owner_load == not-found && $n16_worker_load == not-found ]] || exit 1
 [[ ! -e /run/appsurface-evidence-$G && ! -L /run/appsurface-evidence-$G ]] || exit 1
 [[ $launch_group_absent == true && $launch_fd_path_absent == true ]] || exit 1
 [[ ! -e /proc/$launch_original_pid && ! -L /proc/$launch_original_pid ]] || exit 1
 ! pg_alive "$launch_original_pid" || exit 1
 left >/dev/null || exit 1
 try_bounded jq -jcS -n --slurpfile r "$private/n16-record-summary.json" \
  --arg source "$source_revision" --arg capture "$source_capture_sha256" --arg projection "$root_projection_sha256" \
  --arg owner_load "$n16_owner_load" --arg worker_load "$n16_worker_load" \
  --argjson root_exit "$launch_exit" --argjson passwd_uid "$n16_uid_lookup" \
  --argjson group_gid "$n16_gid_lookup" --argjson group_results "$n16_results_gid_lookup" \
  --argjson name_checks "$n16_name_absence_checks" --argjson group_absent "$launch_group_absent" --argjson fd_absent "$launch_fd_path_absent" \
  '{schema:"issue779-n16-native-fixture-observation-v1",generation:$r[0].generation,source_commit:$source,source_capture_sha256:$capture,root_projection_sha256:$projection,root_exit:$root_exit,root_stdout_bytes:0,stderr_sha256:$r[0].stderr_sha256,worker_unit:$r[0].worker_unit,worker_pid:$r[0].worker_pid,worker_uid:$r[0].worker_uid,worker_gid:$r[0].worker_gid,results_gid:$r[0].results_gid,owner_load_state:$owner_load,worker_load_state:$worker_load,generated_passwd_group_name_checks:$name_checks,worker_uid_reverse_lookup_exit:$passwd_uid,worker_gid_reverse_lookup_exit:$group_gid,results_gid_reverse_lookup_exit:$group_results,root_launch_group_absent:$group_absent,root_launch_fd_directory_absent:$fd_absent,workspace_absent:true,observation_only:true,native_authority:false,native_acceptance:false}' \
  >"$private/n16-fixture-result.pending" || exit 1
 try_bounded chmod 600 "$private/n16-fixture-result.pending" || exit 1
 try_bounded mv -T "$private/n16-fixture-result.pending" "$private/n16-fixture-result.json" || exit 1
 left >/dev/null || exit 1
 printf '%s\n' 'N16_SOURCE_AND_OS_OBSERVATION_TERMINAL:NOT_ACCEPTANCE'
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
# One immutable N16 data parser. The runner supplies its independently pinned digest.
absolute "$n16_parser_root"; ancestor "$n16_parser_root"
pin_file "$n16_parser_root/check_n16_records.py" "$n16_parser_sha256" 65536
[[ $(bounded stat -c '%u:%g:%a:%h' "$n16_parser_root/check_n16_records.py") == 0:0:444:1 ]] || fail n16-parser-source-custody
bounded install -d -m 700 "$private/parsers"
bounded cp --no-dereference -- "$n16_parser_root/check_n16_records.py" "$private/parsers/check_n16_records.py"
bounded chmod 444 "$private/parsers/check_n16_records.py"
pin_file "$private/parsers/check_n16_records.py" "$n16_parser_sha256" 65536

[[ $(bounded find -P "$private/parsers" -mindepth 1 -maxdepth 1 -type f | bounded wc -l) == 1 ]] || fail n16-parser-member-count

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
# UTC is sampled once and capped inside the original monotonic work interval.
job_deadline_epoch=$(( $(bounded date -u +%s) + $(remaining) - 1 ))
job_deadline=$(bounded date -u -d "@$job_deadline_epoch" +%Y-%m-%dT%H:%M:%SZ)
request=$private/request.json
bounded jq -jcS -n --arg tool "$stage/tool" --arg runtime "$stage/runtime" --arg host "$host" --arg entry "$managed" --arg subject "$stage/subject" --arg base "$base_revision" --arg source "$source_revision" --arg workflow "$workflow_identity" --arg deadline "$job_deadline" '{schema:"evidence-supervisor-linux-v1",mode:"observation",tool_root:$tool,runtime_root:$runtime,runtime_host:$host,entry_path:$entry,policy_file:($tool+"/fixture-policy.json"),subject_root:$subject,base_revision:$base,subject_revision:$source,workflow_identity:$workflow,paths:["docs/designs/issue-779-csharp-supervision-core.md"],observation_profile_ids:["empty"],observation_producer_ids:[],job_deadline_utc:$deadline,admission_seconds:10,start_seconds:30,collection_seconds:10,cleanup_seconds:60,stopping_seconds:5}' >"$request"
bounded chmod 600 "$request"; [[ $(bounded stat -c '%u:%g:%a:%h' "$request") == 0:0:600:1 && $(bounded stat -c %s "$request") -le 65536 ]] || fail request-shape
bounded jq -e 'length==20' "$request" >/dev/null || fail request-fields
bounded sha256sum "$request" "$stage/tool/fixture-policy.json" >"$private/request-policy.sha256"
absent_accounts() {
 local n code
 n16_name_absence_checks=0
 for n in "${names[0]}" "${names[1]}"; do code=0; try_bounded getent passwd "$n" >/dev/null || code=$?; [[ $code == 2 ]] || fail NSS-passwd-not-absent; n16_name_absence_checks=$((n16_name_absence_checks+1)); remaining >/dev/null; done
 for n in "${names[@]}"; do code=0; try_bounded getent group "$n" >/dev/null || code=$?; [[ $code == 2 ]] || fail NSS-group-not-absent; n16_name_absence_checks=$((n16_name_absence_checks+1)); remaining >/dev/null; done
}
unit_absent() { local state; state=$(bounded systemctl show "$1" -p LoadState --value); [[ $state == not-found ]] || fail unit-collision; }
absent_accounts; unit_absent "$owner"; unit_absent "$worker"
# N16 has one same-image protected root launch; no N02 or N03 side workload.
absent_accounts; unit_absent "$owner"; unit_absent "$worker"

# Fixed original protected owner recipe, with N16 selected by the pinned private image.

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
ordinary_os_batch_full_pass final
pin_file "$os_audit" "$os_audit_sha256" 1048576
(( $(remaining)>246 && job_deadline_epoch-$(bounded date -u +%s)>245 )) || fail native-life-outside-fixture
for alternate_python_path in /usr/lib/python312.zip /usr/bin/pyvenv.cfg /usr/pyvenv.cfg; do
 [[ ! -e $alternate_python_path && ! -L $alternate_python_path ]] || fail negative-python-alternate-prefix
done
# Existing OS audit schema must contain this fixed interpreter and COMPLETE fixed stdlib graph.
# The source-pinned OS audit producer includes this graph; these checks require its exact same-host observations.
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

# Recheck the complete reviewed Python/ELF closure after the root C# process
# has exited. The audit bytes and cached declarations remain bound to the
# original source-pinned audit; no new deadline or allowance is created.
negative_python_trust_recheck() {
 diagnostic_stage=python-trust
 local current_stdlib stdlib_after module parser i
 pin_file "$os_audit" "$os_audit_sha256" 1048576
 ordinary_os_batch_full_pass final
 pin_file "$os_audit" "$os_audit_sha256" 1048576
 bounded jq -e '
  . as $audit |
  any($audit.files[];.path=="/usr/bin/python3.12") and any($audit.elf[];.path=="/usr/bin/python3.12") and
  all($audit.elf[]; . as $elf | any($audit.files[];.path==$elf.path and .sha256==$elf.sha256) and
    all($elf.resolved[]; . as $dep | any($audit.files[];.path==$dep.path and .sha256==$dep.sha256)) and
    ($elf.interpreter==null or any($audit.files[];.path==$elf.interpreter)))
 ' "$os_audit" >/dev/null || fail negative-python-closure-membership
 for alternate_python_path in /usr/lib/python312.zip /usr/bin/pyvenv.cfg /usr/pyvenv.cfg; do
  [[ ! -e $alternate_python_path && ! -L $alternate_python_path ]] || fail negative-python-alternate-prefix
 done
 current_stdlib=$(negative_stdlib_inventory)
 negative_stdlib_exact "$stdlib_expected" "$current_stdlib" || fail negative-stdlib-membership-after-csharp
 stdlib_after=$(negative_stdlib_inventory)
 negative_stdlib_exact "$current_stdlib" "$stdlib_after" || fail negative-stdlib-membership-after
 parser=$private/parsers/check_n16_records.py
 pin_file "$parser" "$n16_parser_sha256" 65536
 [[ $(bounded stat -c '%u:%g:%a:%h' "$parser") == 0:0:444:1 ]] || fail n16-parser-private-custody
 pin_file "$os_audit" "$os_audit_sha256" 1048576
 remaining >/dev/null
}

# One root-selected 240-second cap nested inside the fixture's original work interval.
monotonic_ms() { local up rest frac; IFS=" " read -r up rest </proc/uptime || return 1; [[ $up =~ ^[0-9]+\.[0-9]+$ ]] || return 1; frac=${up#*.}000; frac=${frac:0:3}; printf "%s" "$((10#${up%%.*}*1000+10#$frac))"; }
n16_root_start_ms=$(monotonic_ms) || fail n16-clock
n16_root_end_ms=$((n16_root_start_ms+240000)); ((n16_root_end_ms<work_end*1000)) || fail n16-original-root-bound
readonly n16_root_start_ms n16_root_end_ms
diagnostic_stage=n16
launched=1
(ulimit -f 262144; ulimit -c 0; exec setsid timeout --signal=KILL "$(remaining)" systemd-run --quiet --wait --pipe --unit="$owner" "${props[@]}" /usr/bin/env -i "${fixed_env[@]}" "$host" "$managed" evidence supervise --request "$request" >"$log/n16.stdout" 2>"$log/n16.stderr") & launch_pid=$!
pin_owned_pg "$launch_pid"
launch_original_pid=$launch_pid
launch_exit=0; join_owned launch_pid || launch_exit=$?
remaining >/dev/null
[[ $launch_exit == 1 && ! -s $log/n16.stdout ]] || fail n16-root-exit-or-manifest
[[ $(bounded stat -c '%u:%g:%a:%h' "$log/n16.stderr") == 0:0:600:1 && $(bounded stat -c %s "$log/n16.stderr") -le 114688 ]] || fail n16-root-stderr-bound
negative_python_trust_recheck
bounded /usr/bin/env -i PATH=/usr/bin:/bin HOME=/nonexistent PYTHONHASHSEED=0 /usr/bin/python3.12 -I -S -B "$private/parsers/check_n16_records.py"  "$log/n16.stderr" --generation "$G" --root-exit "$launch_exit" --source-commit "$source_revision"  --capture-sha256 "$source_capture_sha256" --projection-sha256 "$root_projection_sha256" >"$private/n16-record-summary.json"
bounded chmod 600 "$private/n16-record-summary.json"
[[ $(bounded stat -c '%u:%g:%a:%h' "$private/n16-record-summary.json") == 0:0:600:1 && $(bounded stat -c %s "$private/n16-record-summary.json") -le 4096 ]] || fail n16-summary-custody
n16_worker_uid=$(bounded jq -er '.worker_uid|select(type=="number" and .>0)' "$private/n16-record-summary.json") || fail n16-worker-uid
n16_worker_gid=$(bounded jq -er '.worker_gid|select(type=="number" and .>0)' "$private/n16-record-summary.json") || fail n16-worker-gid
n16_results_gid=$(bounded jq -er '.results_gid|select(type=="number" and .>0)' "$private/n16-record-summary.json") || fail n16-results-gid
[[ $(bounded jq -r .generation "$private/n16-record-summary.json") == "$G" ]] || fail n16-generation-binding
# Independent post-run NSS reverse lookups and generated-name absence.
absent_accounts
n16_uid_lookup=0; try_bounded getent passwd "$n16_worker_uid" >/dev/null || n16_uid_lookup=$?; [[ $n16_uid_lookup == 2 ]] || fail n16-worker-uid-still-present; remaining >/dev/null
n16_gid_lookup=0; try_bounded getent group "$n16_worker_gid" >/dev/null || n16_gid_lookup=$?; [[ $n16_gid_lookup == 2 ]] || fail n16-worker-gid-still-present; remaining >/dev/null
n16_results_gid_lookup=0; try_bounded getent group "$n16_results_gid" >/dev/null || n16_results_gid_lookup=$?; [[ $n16_results_gid_lookup == 2 ]] || fail n16-results-gid-still-present; remaining >/dev/null
unit_absent "$owner"; unit_absent "$worker"
[[ ! -e /run/appsurface-evidence-$G && ! -L /run/appsurface-evidence-$G ]] || fail n16-workspace-remains
for unit in "$owner" "$worker"; do
 cg=/sys/fs/cgroup/system.slice/$unit
 if [[ -e $cg || -L $cg ]]; then fail n16-cgroup-remains; fi
 remaining >/dev/null
done
owned_registration_retired "$launch_original_pid" || fail n16-original-launch-not-joined
# These observations concern the retained original launch session, not a replacement PID.
launch_group_absent=false; launch_fd_path_absent=false
! pg_alive "$launch_original_pid" || fail n16-original-launch-group-remains
launch_group_absent=true
[[ ! -e /proc/$launch_original_pid && ! -L /proc/$launch_original_pid && ! -e /proc/$launch_original_pid/fd && ! -L /proc/$launch_original_pid/fd ]] || fail n16-original-launch-proc-remains
launch_fd_path_absent=true
left >/dev/null || fail n16-final-deadline
success=1
