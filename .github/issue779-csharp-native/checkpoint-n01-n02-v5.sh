#!/usr/bin/env bash
# Source-only candidate; all runtime records are produced only by actual explicit execution.
set -euo pipefail
export LC_ALL=C PATH=/usr/sbin:/usr/bin:/sbin:/bin
umask 077
readonly FIXTURE_SECONDS=600 CLEANUP_RESERVE=30
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
 --arg st "$source_manifest_sha256" --arg pt "$payload_manifest_sha256" --arg rt "$runtime_manifest_sha256" '
 .schema=="issue779-csharp-fdd-build-v5" and .exit==0 and
 .source_commit==$source and $source=="25e449cef5169c47177d19ca813c79acaab265a0" and
 .native_execution==false and .checkpoint_pass==false and .build_prerequisite_only==true and
 (.artifacts|type=="object" and keys==["runtime","source","tool"]) and
 .artifacts.source.nodes_sha256==$sn and .artifacts.tool.nodes_sha256==$pn and .artifacts.runtime.nodes_sha256==$rn and
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
 try_bounded jq -jcS -n --arg g "$G" --arg source "$source_revision" --arg base "$base_revision" --arg policy "$policy_sha" --arg owner "$owner" --arg worker "$worker" --argjson n01 "$n01" --argjson n02 "$n02" --argjson n03 "$n03_exit" --arg n03sha "$n03_result_sha256" '{schema:"issue779-native-n01-n02-n03-fixture-v1",generation:$g,source_revision:$source,base_revision:$base,policy_sha256:$policy,owner_unit:$owner,worker_unit:$worker,n01_exit:$n01,n02_exit:$n02,n03_exit:$n03,n03_result_sha256:$n03sha,native_controls_executed:["N02","N03","N01"],file_inspection:"raw-bytes-sha-json-facts-only",canonical_verification:"actual-csharp-RootCustody",owned_process_groups_joined:true,trusted_enabled:false,remaining_thirteen_controls:"pending"}' >"$private/fixture-result.pending" || status=1
 left >/dev/null || status=1
 ((status==0)) || { printf '%s\n' 'NATIVE_FIXTURE_FAILED:PUBLICATION'; exit 1; }
 try_bounded mv -T -- "$private/fixture-result.pending" "$private/fixture-result.json" || exit 1
 left >/dev/null || { printf '%s\n' 'NATIVE_FIXTURE_FAILED:LATE_PUBLICATION'; exit 1; }
 printf '%s\n' 'N01_N02_N03_TERMINAL:ONLY_ACTUAL_THREE_CASES'
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
 [[ $# == 11 && $EUID == 0 && $source_revision == 25e449cef5169c47177d19ca813c79acaab265a0 ]] || fail N03-input
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
# Observer is its own retained process group. It can only inspect this generated worker/path tuple.
(ulimit -f 8192; exec setsid timeout --signal=KILL "$(remaining)" bash -c '
 set -euo pipefail; export LC_ALL=C PATH=/usr/sbin:/usr/bin:/sbin:/bin
 end=$1; worker=$2; host=$3; managed=$4; control=$5; account=$6; sample=$7
 now() { local u x; IFS=" " read -r u x </proc/uptime; printf "%s" "${u%%.*}"; }
 left() { local n; n=$(now); ((n<end)) || return 124; printf "%s" "$((end-n))"; }
 run() { local n; n=$(left) || return 124; timeout --signal=KILL "$n" "$@"; }
 start() { local text tail; text=$(run cat "/proc/$1/stat"); [[ ${text%% *} == "$1" ]]; tail=${text##*) }; read -r -a f <<<"$tail"; [[ ${f[19]:-} =~ ^[1-9][0-9]*$ ]]; printf "%s" "${f[19]}"; }
 host_pin=$(run stat -c "%d:%i:%s:%f" "$host"); host_hash=$(run sha256sum "$host"); host_hash=${host_hash:0:64}
 while left >/dev/null; do
  code=0; facts=$(run systemctl show "$worker" -p LoadState -p MainPID) || code=$?
  ((${#facts}<=4096)) || exit 1
  pid=; load=; seen_pid=0; seen_load=0
  while IFS="=" read -r key value; do
   case "$key" in MainPID) ((seen_pid==0)) || exit 1; seen_pid=1; pid=$value ;; LoadState) ((seen_load==0)) || exit 1; seen_load=1; load=$value ;; *) exit 1 ;; esac
  done <<<"$facts"
  [[ $seen_pid == 1 && $seen_load == 1 && $pid =~ ^[0-9]+$ ]] || exit 1
  if ((code!=0)); then [[ $code == 1 && $load == not-found && $pid == 0 ]] || exit 1; fi
  if [[ $load == not-found && $pid == 0 ]]; then run sleep .02; continue; fi
  [[ $code == 0 && $load == loaded ]] || exit 1
  if [[ $pid =~ ^[1-9][0-9]*$ && -r /proc/$pid/status ]]; then
   s1=$(start "$pid") || exit 1
   uid1=$(run awk '\''$1=="Uid:"{print $2":"$3":"$4":"$5}'\'' "/proc/$pid/status")
   gid1=$(run awk '\''$1=="Gid:"{print $2":"$3":"$4":"$5}'\'' "/proc/$pid/status")
   cg1=$(run cat "/proc/$pid/cgroup")
   exe1=$(run readlink "/proc/$pid/exe") || exit 1
   if [[ $exe1 != "$host" ]]; then
    # env -i and the exec transition are pending, NEVER positive observations.
    [[ $exe1 == /usr/bin/env ]] || exit 1; run sleep .02; continue
   fi
   [[ $(run stat -Lc "%d:%i:%s:%f" "/proc/$pid/exe") == "$host_pin" ]] || exit 1
   image=$(run sha256sum "/proc/$pid/exe"); [[ ${image:0:64} == "$host_hash" ]] || exit 1
   argv_file=${sample%.json}.argv
   run head -c 16385 "/proc/$pid/cmdline" >"$argv_file"
   size=$(run stat -c %s "$argv_file"); ((size>0 && size<=16384)) || exit 1
   args=(); while IFS= read -r -d "" a; do args+=("$a"); ((${#args[@]}<=6)) || exit 1; done <"$argv_file"
   [[ ${#args[@]} == 6 && ${args[0]} == "$host" && ${args[1]} == "$managed" && ${args[2]} == evidence && ${args[3]} == worker && ${args[4]} == --control && ${args[5]} == "$control" ]] || exit 1
   uid2=$(run awk '\''$1=="Uid:"{print $2":"$3":"$4":"$5}'\'' "/proc/$pid/status")
   gid2=$(run awk '\''$1=="Gid:"{print $2":"$3":"$4":"$5}'\'' "/proc/$pid/status")
   cg2=$(run cat "/proc/$pid/cgroup"); exe2=$(run readlink "/proc/$pid/exe"); s2=$(start "$pid")
   [[ $s1 == "$s2" && $uid1 == "$uid2" && $gid1 == "$gid2" && $cg1 == "$cg2" && $cg1 == "0::/system.slice/$worker" && $exe2 == "$host" && $(run stat -Lc "%d:%i:%s:%f" "/proc/$pid/exe") == "$host_pin" ]] || exit 1
   IFS=: read -r -a us <<<"$uid1"; IFS=: read -r -a gs <<<"$gid1"
   [[ ${#us[@]} == 4 && ${#gs[@]} == 4 ]] || exit 1
   for v in "${us[@]}"; do [[ $v =~ ^[1-9][0-9]*$ && $v == "${us[0]}" ]] || exit 1; done
   for v in "${gs[@]}"; do [[ $v =~ ^[1-9][0-9]*$ && $v == "${gs[0]}" ]] || exit 1; done
   pw=$(run getent passwd "$account"); gr=$(run getent group "$account")
   IFS=: read -r pn px pu rest <<<"$pw"; IFS=: read -r gn gx gg rest <<<"$gr"
   [[ $pn == "$account" && $gn == "$account" && $pu == "${us[0]}" && $gg == "${gs[0]}" ]] || exit 1
   run jq -jcS -n --argjson pid "$pid" --arg uid "$uid1" --arg gid "$gid1" --arg cg "$cg1" --arg ticks "$s1" '\''{pid:$pid,uid4:$uid,gid4:$gid,cgroup:$cg,start_time_ticks:$ticks,post_exec_image_pinned:true,exact_worker_arguments:true,ready_protocol_observed:false}'\'' >"$sample"
   left >/dev/null; exit 0
  fi
  run sleep .02
 done; exit 1
' -- "$work_end" "$worker" "$host" "$managed" "/run/appsurface-evidence-$G/worker/broker/control.sock" "${names[0]}" "$private/worker-live.json" >"$log/observer.stdout" 2>"$log/observer.stderr") & observer_pid=$!
pin_owned_pg "$observer_pid"
set +e; join_owned launch_pid; n01=$?; set -e
if ((n01 != 0)); then
 printf 'FIXTURE_N01_LAUNCH_JOIN_STATUS:%d\n' "$n01" >&2
 fail N01-launch-outcome
fi
set +e; join_owned observer_pid; observed=$?; set -e
# The join retires every fully settled registration; failed exits stay failed.
[[ $n01 == 0 && $observed == 0 && -s $private/worker-live.json ]] || fail N01-outcome
[[ $(bounded stat -c %s "$log/n01.stdout") -le 4096 && ! -s $log/n01.stderr ]] || fail N01-console-bound
bounded jq -e 'keys==["ClaimKind","CleanupCompleted","Eligibility","ExecutionVerdict","Mode"] and .Mode=="Observation" and .ClaimKind=="ObservationOnly" and .Eligibility=="Informational" and .ExecutionVerdict=="Passed" and .CleanupCompleted==true' "$log/n01.stdout" >/dev/null || fail N01-summary
absent_accounts
workspace=/run/appsurface-evidence-$G; slot=$workspace/output/evidence
[[ -d $slot && ! -L $slot && -z $(bounded find -P "$workspace/raw-results" -mindepth 1 -print -quit) ]] || fail final-workspace
[[ $(bounded find -P "$slot" -mindepth 1 -maxdepth 1 -printf x | wc -c) == 3 ]] || fail final-file-set
for name in evidence-plan.json evidence-manifest.json evidence-summary.json; do
 p=$slot/$name; absolute "$p"
 [[ -f $p && $(bounded stat -c '%u:%g:%a:%h' "$p") == 0:0:400:1 && $(bounded stat -c %s "$p") -le 20971520 ]] || fail final-file-custody
 h=$(bounded sha256sum "$p"); h=${h:0:64}
 bounded cp -- "$p" "$private/raw-$name"; bounded chmod 600 "$private/raw-$name"
 pin_file "$private/raw-$name" "$h" 20971520; pin_file "$p" "$h" 20971520
 bounded jq -e 'type=="object"' "$private/raw-$name" >/dev/null || fail final-JSON-inspection
 printf '%s  %s\n' "$h" "$name" >>"$private/final-files.sha256"
done
bounded jq -e '.Mode=="Observation" and .ClaimKind=="ObservationOnly" and .Eligibility=="Informational" and .ExecutionVerdict=="Passed" and .Metrics.CleanupCompleted==true and (.ResourceResults|length)==0 and (.ProducerResults|length)==0' "$private/raw-evidence-manifest.json" >/dev/null || fail manifest-result
# Raw bytes/hash/JSON facts are inspection ONLY. Actual C# RootCustody owns canonical verification.
bounded jq -e --arg source "$source_revision" '
 keys==["asevd402_observed","broker_exit","broker_pid","control","descriptor_supplied","kernel_peer_bound_to_owned_worker","native_acceptance","owned_process_groups_joined","ready_request_observed","request_bytes","schema","sent_bytes","source_commit","status","stream_custody","systemd_worker_authority","worker_exit","worker_pid","worker_stdout_bytes"] and
 .schema=="issue779-n03-peer-observation-v1" and .source_commit==$source and .control=="N03" and .status=="observed" and
 .worker_exit==1 and .broker_exit==0 and .worker_stdout_bytes==0 and .asevd402_observed==true and
 .kernel_peer_bound_to_owned_worker==true and .request_bytes==0 and .sent_bytes==0 and .ready_request_observed==false and .descriptor_supplied==false and .owned_process_groups_joined==true and
 .native_acceptance==false and .systemd_worker_authority==false and .stream_custody=="private-files-after-original-process-reap" and
 ([.worker_pid,.broker_pid]|all(.[]; type=="number" and floor==. and .>0 and .<=2147483647)) and .worker_pid!=.broker_pid
 ' "$log/n03/result.json" >/dev/null || fail N03-final-packet
[[ $(bounded sha256sum "$log/n03/result.json") == "$n03_result_sha256  $log/n03/result.json" ]] || fail N03-result-changed
remaining >/dev/null; success=1
