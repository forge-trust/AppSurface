#!/usr/bin/env bash
# SOURCE-ONLY insertion library, never a standalone root entry. Pin before Bash interpretation.
[[ ${BASH_SOURCE[0]} != "$0" ]] || { printf 'N03_REJECTED:source-only-procedure\n' >&2; exit 1; }
# Requires authenticated fixture functions/variables: bounded, try_bounded, remaining, left,
# ancestor, pin_file, verify_tree, pin_owned_pg, signal_owned, join_owned; original600/cleanup30.
# Root caller additionally registers this subshell under its EXISTING utility cgroup/deadline.
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
 [[ $# == 11 && $EUID == 0 && $source_revision == 31c9ff5c8782102e0917e0c992bbdecc35e115d0 ]] || fail N03-input
 helper_root=$1 helper_map=$2 helper_map_sha=$3 helper_nodes=$4 helper_nodes_sha=$5 helper_entry_sha=$6
 broker_name=$7 worker_name=$8 shared_name=$9 startup_kib=${10} helper_generation=${11}
 n03_broker_pid= n03_worker_pid= release_fd= read_fd= dir_fd= socket_pin= first_failure= result_status=rejected
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
  if [[ -n $read_fd ]]; then exec {read_fd}<&- || bad=1; fi
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
