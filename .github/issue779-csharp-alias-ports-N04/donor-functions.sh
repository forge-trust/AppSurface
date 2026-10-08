fail() { printf 'CHECKPOINT_REJECTED:%s\n' "$1" >&2; exit 1; }

monotonic() { local up rest; IFS=' ' read -r up rest </proc/uptime || return 1; [[ $up =~ ^[0-9]+\.[0-9]+$ ]] || return 1; printf '%s' "${up%%.*}"; }

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

normalize_os_target() {
 local path=$1 s; local -a out=(); [[ $path == /* && ${#path}<=4096 && ! $path =~ [[:cntrl:]] ]] || fail alias-target
 IFS=/ read -r -a pieces <<<"$path"
 for s in "${pieces[@]}"; do case "$s" in ''|.) ;; ..) ((${#out[@]}>0)) || fail alias-root-escape; unset "out[$((${#out[@]}-1))]" ;; *) out+=("$s");; esac; done
 ((${#out[@]}>0)) || fail alias-root-target
 local joined; joined=$(IFS=/; printf '%s' "${out[*]}"); printf '/%s' "$joined"
}

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
 os_cache_ready=1
 readonly os_cache_ready
 readonly -a os_cache_file_paths os_cache_file_hashes os_cache_alias_paths os_cache_alias_rows
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
