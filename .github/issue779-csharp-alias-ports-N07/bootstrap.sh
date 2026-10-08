#!/usr/bin/bash
# Externally pin and install this script root0500 under a fresh root700 root FIRST.
set -euo pipefail
export LC_ALL=C PATH=/usr/sbin:/usr/bin:/sbin:/bin
umask 077
[[ $# == 2 ]] || exit 1
source_dir=$1
root=$2
[[ $source_dir == /* && $source_dir != *$'\n'* && $root =~ ^/var/lib/issue779-alias-[0-9a-f]{32}$ ]] || exit 1
[[ $EUID == 0 && $(/usr/bin/id -u) == 0 && $(/usr/bin/id -g) == 0 ]] || exit 1
clock_ms() {
 local up rest seconds fraction
 IFS=' ' read -r up rest </proc/uptime
 [[ $up =~ ^([0-9]+)\.([0-9]{2})$ ]] || return 1
 seconds=${BASH_REMATCH[1]}; fraction=${BASH_REMATCH[2]}
 printf '%s' "$((10#$seconds*1000+10#$fraction*10))"
}
started=$(clock_ms)
readonly started end=$((started+90000)) work=$((started+80000))
check() { local n; n=$(clock_ms); ((n<${1:-$work})); }
bounded() {
 local n limit result=0
 n=$(clock_ms); ((n<${end})) || return 124
 limit=$((end-n))
 /usr/bin/timeout --signal=KILL "$((limit/1000)).$(printf '%03d' "$((limit%1000))")s" "$@" || result=$?
 check "$end" || return 124
 return "$result"
}
fail() { printf 'ALIAS_BOOTSTRAP_REJECTED:%s\n' "$1" >&2; exit 1; }
root_directory() {
 local p=$1 uid gid mode
 [[ $p == /* ]] || fail path
 while :; do
  [[ -d $p && ! -L $p ]] || fail root-directory
  read -r uid gid mode <<<"$(bounded /usr/bin/stat -c '%u %g %a' -- "$p")"
  [[ $uid == 0 && $gid == 0 ]] && (((8#$mode & 0022)==0)) || fail root-directory-mode
  [[ $p == / ]] && break
  p=${p%/*}; [[ -n $p ]] || p=/
 done
}
root_file() {
 local p=$1 uid gid mode links
 [[ -f $p && ! -L $p ]] || fail root-file
 root_directory "${p%/*}"
 read -r uid gid mode links <<<"$(bounded /usr/bin/stat -c '%u %g %a %h' -- "$p")"
 [[ $uid == 0 && $gid == 0 && $links == 1 ]] && (((8#$mode & 0022)==0)) || fail root-file-mode
}
root_directory "$root"
[[ $(bounded /usr/bin/stat -c %a -- "$root") == 700 ]] || fail private-root
[[ ! -e $root/code && ! -L $root/code && ! -e $root/output && ! -L $root/output ]] || fail fresh-layout
bounded /usr/bin/mkdir -m 700 -- "$root/code" "$root/audit"
# No Python code has been interpreted yet. Trusted Ubuntu OS utilities only.
[[ -L /usr/bin/python3 && $(bounded /usr/bin/readlink -- /usr/bin/python3) == python3.12 ]] || fail python-alias
root_file /usr/bin/python3.12
root_directory /usr/lib/python3.12
for absent in /usr/lib/python312.zip /usr/bin/pyvenv.cfg /usr/pyvenv.cfg "$root/code/pyvenv.cfg"; do
 [[ ! -e $absent && ! -L $absent ]] || fail alternate-python-prefix
done
# -I -S -B + env-i excludes cwd/user/PYTHONPATH/site/custom bytecode writes.
# Full system stdlib metadata is checked before interpretation, including native extensions.
bounded /usr/bin/find -P /usr/lib/python3.12 -printf '%y:%U:%G:%m:%D:%i:%n:%s:%T@:%p\0' >"$root/audit/stdlib-before.meta"
[[ $(bounded /usr/bin/stat -c %s -- "$root/audit/stdlib-before.meta") -le 1048576 ]] || fail stdlib-path-bound
count=0
while IFS= read -r -d '' row; do
 check; count=$((count+1)); ((count<=8192)) || fail stdlib-node-bound
 IFS=: read -r type uid gid mode device inode links bytes stamp p <<<"$row"
 [[ $p == /usr/lib/python3.12 || $p == /usr/lib/python3.12/* ]] || fail stdlib-node-path
 [[ $p != *:* && $p != *$'\n'* && $uid == 0 && $gid == 0 && $mode =~ ^[0-7]{3,4}$ && $inode =~ ^[1-9][0-9]*$ ]] || fail stdlib-node-metadata
 case "$type" in
  d|f) (((8#$mode & 0022)==0)) || fail stdlib-node-write ;;
  l)
   real=$(bounded /usr/bin/readlink -e -- "$p")
   # Ubuntu noble's packaged sitecustomize link is outside /usr. Permit only
   # that exact declaration; root_file below still rejects writable/linked parents,
   # non-root ownership, multiple links or a writable target. -S never imports it.
   if [[ $real != /usr/* ]]; then
    [[ $p == /usr/lib/python3.12/sitecustomize.py && $real == /etc/python3.12/sitecustomize.py && $(bounded /usr/bin/readlink -- "$p") == /etc/python3.12/sitecustomize.py && -f $real && ! -L $real ]] || fail stdlib-alias-target
   fi
   if [[ -d $real ]]; then root_directory "$real"; else root_file "$real"; fi ;;
  *) fail stdlib-node-type ;;
 esac
done <"$root/audit/stdlib-before.meta"
bounded /usr/bin/sha256sum -- /usr/bin/python3.12 >"$root/audit/interpreter.sha256"
# The manifest itself is externally hard-pinned below, not trusted by its contents.
[[ -f $source_dir/input-pins.tsv && ! -L $source_dir/input-pins.tsv && $(bounded /usr/bin/stat -c %h -- "$source_dir/input-pins.tsv") == 1 ]] || fail manifest-type
manifest_before=$(bounded /usr/bin/stat -c '%d:%i:%s:%f:%h:%u:%g:%y:%z' -- "$source_dir/input-pins.tsv")
manifest_hash=$(bounded /usr/bin/sha256sum -- "$source_dir/input-pins.tsv")
[[ ${manifest_hash:0:64} == c5e56fcbba6b404771b0f7e09e13b95e293cc353701722928b723afccca3ee63 ]] || fail manifest-pin
[[ $(bounded /usr/bin/stat -c %s -- "$source_dir/input-pins.tsv") -le 4096 ]] || fail manifest-bound
bounded /usr/bin/dd if="$source_dir/input-pins.tsv" of="$root/audit/input-pins.tsv" iflag=nofollow,fullblock oflag=nofollow conv=fsync,excl bs=4096 count=1 status=none
sealed_manifest_hash=$(bounded /usr/bin/sha256sum -- "$root/audit/input-pins.tsv")
[[ ${sealed_manifest_hash:0:64} == c5e56fcbba6b404771b0f7e09e13b95e293cc353701722928b723afccca3ee63 ]] || fail sealed-manifest-pin
bounded /usr/bin/chmod 0444 -- "$root/audit/input-pins.tsv"
rows=0
while IFS=$'\t' read -r name expected bytes extra; do
 check
 case "$name" in qualify-alias.py|audit-selection.py|selection.json|donor-full-source.data|candidate-full-source.data|donor-functions.sh|candidate-functions.sh|timeout-shim.sh|ownership-control.py) ;; *) fail input-name ;; esac
 [[ -z ${extra:-} && $expected =~ ^[0-9a-f]{64}$ && $bytes =~ ^[0-9]+$ ]] && ((bytes>0 && bytes<=262144)) || fail input-shape
 rows=$((rows+1)); ((rows<=9)) || fail input-count
 p=$source_dir/$name; dest=$root/code/$name
 [[ -f $p && ! -L $p && $(bounded /usr/bin/stat -c %h -- "$p") == 1 ]] || fail input-type
 before=$(bounded /usr/bin/stat -c '%d:%i:%s:%f:%h:%u:%g:%y:%z' -- "$p")
 [[ $(bounded /usr/bin/stat -c %s -- "$p") == "$bytes" ]] || fail input-size
 # O_NOFOLLOW input + O_NOFOLLOW/O_EXCL output; cap-copy, then full pins/identity.
 bounded /usr/bin/dd if="$p" of="$dest" iflag=nofollow,fullblock oflag=nofollow conv=fsync,excl bs=65536 count=5 status=none
 after=$(bounded /usr/bin/stat -c '%d:%i:%s:%f:%h:%u:%g:%y:%z' -- "$p")
 [[ $before == "$after" && $(bounded /usr/bin/stat -c %s -- "$dest") == "$bytes" ]] || fail copied-identity
 measured=$(bounded /usr/bin/sha256sum -- "$dest")
 [[ ${measured:0:64} == "$expected" ]] || fail copied-pin
 bounded /usr/bin/chmod 0444 -- "$dest"; root_file "$dest"
done <"$root/audit/input-pins.tsv"
[[ $rows == 9 && $(bounded /usr/bin/stat -c '%d:%i:%s:%f:%h:%u:%g:%y:%z' -- "$source_dir/input-pins.tsv") == "$manifest_before" ]] || fail manifest-continuity
check
result=0
bounded /usr/bin/env -i PATH=/usr/sbin:/usr/bin:/sbin:/bin LC_ALL=C HOME=/nonexistent /usr/bin/python3 -I -S -B "$root/code/qualify-alias.py" --execute --root "$root" --deadline-boottime-ms "$end" || result=$?
# Record actual child exit even on failure. Never echo private logs or Python errors.
printf '%d\n' "$result" >"$root/audit/runner-exit.txt"
bounded /usr/bin/find -P /usr/lib/python3.12 -printf '%y:%U:%G:%m:%D:%i:%n:%s:%T@:%p\0' >"$root/audit/stdlib-after.meta" || result=1
bounded /usr/bin/cmp -s -- "$root/audit/stdlib-before.meta" "$root/audit/stdlib-after.meta" || result=1
measured=$(bounded /usr/bin/sha256sum -- /usr/bin/python3.12) || result=1
[[ $(<"$root/audit/interpreter.sha256") == "$measured" ]] || result=1
check "$end" || result=1
[[ -f $root/output/qualification.json && ! -L $root/output/qualification-late-failure.json && ! -e $root/output/qualification-late-failure.json ]] || result=1
if ((result!=0)) && [[ -d $root/output && ! -e $root/output/qualification-late-failure.json && ! -L $root/output/qualification-late-failure.json ]]; then
 (set -o noclobber; printf '{"exit":1,"clear":false,"failure_category":"bootstrap-final-binding","native_authority":false}\n' >"$root/output/qualification-late-failure.json") || :
fi
printf '%d\n' "$result" >"$root/audit/bootstrap-exit.txt"
printf 'ALIAS_BOOTSTRAP_EXIT=%d\n' "$result"
check "$end" || result=1
exit "$result"
