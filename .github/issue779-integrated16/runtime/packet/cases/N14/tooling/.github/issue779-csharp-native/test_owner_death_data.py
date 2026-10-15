"""Detached N13/N14 receipt and shell-order controls; no OS operation is performed."""
import copy
import base64
import hashlib
import json
import subprocess
import unittest
from pathlib import Path
import check_owner_death_record as parser

GEN = "0123456789abcdef0123456789abcdef"
OWNER = f"appsurface-evidence-owner-{GEN}.service"
WORKER = f"appsurface-evidence-worker-{GEN}.service"

def record(case):
    phase = f"NATIVE_PRIVATE_PHASE:{case}:ACCEPTED_BLOCKED_WORK"
    owner = {"pid": 101, "starttime": 11, "uid": 0, "gid": 0,
             "unit": OWNER, "cgroup": "/system.slice/" + OWNER}
    worker = {"pid": 202, "starttime": 22, "uid": 2001, "gid": 2002,
              "unit": WORKER, "cgroup": "/system.slice/" + WORKER}
    sig_usec = 123456789
    elapsed_usec = 241234567 if case == "N14" else 123456789
    return {"schema":"issue779-owner-death-native-fixture-v1", "case":case,
        "generation":GEN, "phase_line":phase, "accepted_reply_committed":True,
        "original_request_pending":True, "owner":owner, "worker":worker,
        "signal":{"name":"SIGKILL" if case=="N13" else "SIGSTOP", "target_pid":101,
            "pre_signal_owner":copy.deepcopy(owner), "syscall_succeeded":True,
            "elapsed_seconds":sig_usec//1_000_000, "from_activation_usec":sig_usec},
        "systemd_bound":{"active_enter_usec":90000000,"runtime_max_usec":240000000,
            "runtime_max_show":"4min","timeout_stop_usec":5000000,"timeout_stop_show":"5s",
            "final_kill_policy":"SendSIGKILL=yes;FinalKillSignal=9;KillMode=control-group",
            "timer_rearmed":False,"new_acceptance":False},
        "termination":{"owner_exited":True,"worker_exited":True,
            "owner_unit_terminal":True,"worker_unit_terminal":True,
            "owner_active_state":"failed","worker_active_state":"failed",
            "owner_sub_state":"failed","worker_sub_state":"failed",
            "owner_terminal_main_pid":0,"worker_terminal_main_pid":0,
            "owner_cgroup_empty":True,"worker_cgroup_empty":True,
            "elapsed_seconds":elapsed_usec//1_000_000,
            "from_activation_usec":elapsed_usec,
            "owner_exec_main_code":2,"owner_exec_main_status":9},
        "pipes":{"root_stdout_eof":True,"root_stderr_eof":True,
            "stdout_bytes":len((phase+"\n").encode()),"stderr_bytes":0,
            "stdout_sha256":"a"*64,"stderr_sha256":"b"*64,"launch_exit":1},
        "positive_files_before":0,"positive_files_after":0,
        "accounts":{"generation_bound":True,"retained":True,"reusable":False,
            "names":["evw"+GEN[:28],"evs"+GEN[:28],"evr"+GEN[:28]],
            "worker_uid":2001,"worker_gid":2002,"subject_uid":3001,"subject_gid":3002,
            "results_gid":4001},
        "workspace":{"generation_bound":True,"quarantined":True,"retained":True},
        "owner_death_outcome_preserved":True,"native_credit":False}

class PreparationBindingControls(unittest.TestCase):
    def test_embedded_preparation_file_digest_is_current_and_wrong_digest_rejects(self):
        folder = Path(__file__).parent
        review = json.loads((folder / "source-review.json").read_bytes())
        raw = base64.b64decode(review["fixture_preparation_base64"], validate=True)
        self.assertEqual(hashlib.sha256(raw).hexdigest(), review["fixture_preparation_sha256"])
        preparation = json.loads(raw)
        self.assertEqual(preparation["source_capture_head"], "5b4ed381badb91de47d793715d05b6ae7737e39e")
        self.assertEqual(preparation["source_capture_parent"], "914046e1e998ca89a5f0f5be39a90bbfb88a431d")
        self.assertEqual(preparation["source_capture_count"], 2872)
        expected = {}
        for name in ("checkpoint-n13-owner-killed.sh", "checkpoint-n14-owner-stopped.sh",
                     "checkpoint-owner-death-common.sh", "check_owner_death_record.py"):
            path = folder / name
            expected[name] = {"bytes": path.stat().st_size, "mode": format(path.stat().st_mode & 0o7777, "04o"),
                              "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}
        self.assertEqual(preparation["files"], expected)
        wrong = copy.deepcopy(preparation)
        wrong["files"]["checkpoint-owner-death-common.sh"]["sha256"] = "0" * 64
        self.assertEqual(wrong["source_capture_head"], preparation["source_capture_head"])
        self.assertNotEqual(wrong["files"], expected)
        transport = (folder / "prepare-root-inputs-v2.sh").read_text()
        self.assertIn('.files["checkpoint-owner-death-common.sh"].sha256=="' +
                      expected["checkpoint-owner-death-common.sh"]["sha256"] + '"', transport)
        self.assertNotIn('.files["checkpoint-owner-death-common.sh"].sha256=="' + "0" * 64 + '"', transport)

class ReceiptControls(unittest.TestCase):
    def test_both_cases_accept_failed_terminal_services(self):
        for case in ("N13", "N14"):
            self.assertTrue(parser.validate(record(case)))

    def test_systemd_v255_duration_strings_are_fixed_and_reject_changed_or_malformed_values(self):
        for case in ("N13", "N14"):
            self.assertTrue(parser.validate(record(case)))
        shell = Path("checkpoint-owner-death-common.sh").read_text()
        normalizer = next(line for line in shell.splitlines()
                          if line.startswith("systemd_policy_usec(){"))
        for prop, raw, expected in (("RuntimeMaxUSec", "4min", "240000000"),
                                    ("TimeoutStopUSec", "5s", "5000000"),
                                    ("RuntimeMaxUSec", "240000000", "240000000")):
            result = subprocess.run(["/bin/bash", "-c",
                normalizer + '\nsystemd_policy_usec "$1" "$2"', "duration-control", prop, raw],
                capture_output=True, text=True, timeout=1, check=False)
            self.assertEqual((result.returncode, result.stdout), (0, expected))
        for field, value in (("runtime_max_show", "3min"),
                             ("timeout_stop_show", "6s"),
                             ("runtime_max_show", "4min4min"),
                             ("timeout_stop_show", "infinity"),
                             ("runtime_max_show", "4min\n")):
            item = record("N13")
            item["systemd_bound"][field] = value
            with self.assertRaises(ValueError):
                parser.validate(item)
        for prop, raw in (("RuntimeMaxUSec", "3min"),
                          ("RuntimeMaxUSec", "4min4min"),
                          ("TimeoutStopUSec", "infinity"),
                          ("TimeoutStopUSec", "5s\n")):
            result = subprocess.run(["/bin/bash", "-c",
                normalizer + '\nsystemd_policy_usec "$1" "$2"', "duration-control", prop, raw],
                capture_output=True, text=True, timeout=1, check=False)
            self.assertNotEqual(result.returncode, 0)
        item = record("N14")
        item["systemd_bound"]["runtime_max_usec"] = 240000000.0
        with self.assertRaises(ValueError):
            parser.validate(item)

    def test_signal_shape_is_exactly_six_fields_and_single_field_changes_reject(self):
        for mutate in ("remove", "add"):
            item=record("N13")
            if mutate == "remove": item["signal"].pop("from_activation_usec")
            else: item["signal"]["extra"] = 1
            with self.assertRaises(ValueError): parser.validate(item)

    def test_microseconds_are_independent_and_crosschecked_with_seconds(self):
        item=record("N13"); item["termination"]["from_activation_usec"] = item["termination"]["elapsed_seconds"]
        with self.assertRaises(ValueError): parser.validate(item)
        item=record("N14"); item["termination"]["elapsed_seconds"] += 1
        with self.assertRaises(ValueError): parser.validate(item)

    def test_nonterminal_or_nonzero_mainpid_rejects_even_with_failed_label(self):
        for field, value in (("owner_active_state", "active"),
                             ("worker_active_state", "deactivating"),
                             ("owner_terminal_main_pid", 101)):
            item=record("N13"); item["termination"][field]=value
            with self.assertRaises(ValueError): parser.validate(item)

class SourceOrderControls(unittest.TestCase):
    def test_collision_checks_and_helpers_precede_guard_and_launch(self):
        text=Path("checkpoint-owner-death-common.sh").read_text()
        for definition in ("name_absent(){", "unit_absent(){"):
            self.assertLess(text.index(definition), text.index('for n in "${names[@]}"'))
        checks=text.index('for n in "${names[@]}"')
        self.assertLess(checks, text.index('printf \'%s\\n\' "$G" >"$guard/armed"'))
        self.assertLess(checks, text.index("launched=1"))
        normalizer=text.index('systemd_policy_usec(){')
        first_use=text.index('runtime_usec=$(systemd_policy_usec')
        self.assertLess(normalizer, first_use)
        self.assertIn('RuntimeMaxUSec:4min|RuntimeMaxUSec:240000000', text)
        self.assertIn('TimeoutStopUSec:5s|TimeoutStopUSec:5000000', text)
        self.assertIn('--arg runtime_show "$runtime_show" --arg timeout_show "$timeout_show"', text)

    def test_real_output_root_is_only_inspected_after_phase_and_never_created(self):
        text=Path("checkpoint-owner-death-common.sh").read_text()
        self.assertIn('expected_output=/run/appsurface-evidence-$G/output', text)
        phase=text.index('((phase_seen==1)) || fail accepted-phase-timeout')
        check=text.index('output-root-after-phase')
        self.assertLess(phase, check)
        self.assertFalse(any('$output' in line for line in text.splitlines() if 'install -d' in line))
        self.assertNotIn('output-not-empty-before', text)
        self.assertGreater(check, text.index('launched=1'))


class PreparationCallerChainControls(unittest.TestCase):
    EXPECTED_PREPARATION_SHA = "10faea449fbbbd8b38ef4a6f60b78b3baa7bba2593857a5bbd2f04c09a6b4fb3"

    def test_embedded_preparation_digest_is_shared_by_workflow_runner_and_root_chain(self):
        folder = Path(__file__).parent
        review = json.loads((folder / "source-review.json").read_bytes())
        raw = base64.b64decode(review["fixture_preparation_base64"], validate=True)
        digest = hashlib.sha256(raw).hexdigest()
        self.assertEqual(digest, self.EXPECTED_PREPARATION_SHA)
        self.assertEqual(review["fixture_preparation_sha256"], digest)

        workflow = (folder.parent / "workflows" / "issue779-csharp-linux-native.yml").read_text()
        self.assertIn("sha256(data).hexdigest()!=review['fixture_preparation_sha256']", workflow)
        self.assertIn("p=root/'preparation.json'", workflow)
        self.assertIn("with p.open('xb') as f: f.write(data)", workflow)

        runner = (folder / "run-native.py").read_text()
        self.assertIn(f"PREPARATION_SHA = '{digest}'", runner)
        self.assertIn("'preparation.json':PREPARATION_SHA", runner)
        self.assertIn("'--fixture-preparation-sha256',PREPARATION_SHA", runner)

        acquisition = (folder / "acquire-inbound.sh").read_text()
        self.assertIn(f"readonly PREPARATION={digest}", acquisition)
        self.assertIn('trusted_pin "${v[reviewed-root]}/preparation.json" "$PREPARATION"', acquisition)
        self.assertIn('copy_file "${v[reviewed-root]}/preparation.json" "$work/fixture-preparation.json" "$PREPARATION"', acquisition)

        transport = (folder / "prepare-root-inputs-v2.sh").read_text()
        self.assertIn(f"readonly PREPARATION_SHA={digest}", transport)
        self.assertIn('fixture-preparation-sha256]} == "$PREPARATION_SHA"', transport)
        self.assertIn('trusted_pin "${values[fixture-preparation]}" "$PREPARATION_SHA"', transport)
        self.assertIn('copy_pinned "${values[fixture-preparation]}" "$work/fixture-preparation.json" "$PREPARATION_SHA"', transport)

    def test_workflow_invokes_mode_0644_build_wrappers_through_bash_and_refreshes_pins(self):
        folder = Path(__file__).parent
        workflow = (folder.parent / "workflows" / "issue779-csharp-linux-native.yml").read_text()
        self.assertIn("branches: [codex/issue779-csharp-linux-native-20261011-owner-death-current-source-v11]", workflow)
        self.assertIn("stat.S_IMODE(before.st_mode) != 0o644", workflow)
        for name in ("build-n13.sh", "build-n14.sh"):
            path = folder.parent / "issue779-csharp-build" / name
            self.assertEqual(format(path.stat().st_mode & 0o7777, "04o"), "0644")
            self.assertIn(f"/usr/bin/bash .github/issue779-csharp-build/{name} --execute", workflow)
            self.assertNotIn(f"          .github/issue779-csharp-build/{name} --execute", workflow)

        runner_sha = hashlib.sha256((folder / "run-native.py").read_bytes()).hexdigest()
        runtime_sha = hashlib.sha256((folder / "prepare-ubuntu-runtime.py").read_bytes()).hexdigest()
        self.assertIn(f"RUNNER_SHA = '{runner_sha}'", (folder / "prepare-ubuntu-runtime.py").read_text())
        builder = folder.parent / "issue779-csharp-build" / "build-fdd.sh"
        self.assertIn(f"NATIVE_RUNNER_SHA = '{runner_sha}'", builder.read_text())
        for variable, relative in (
            ("BUILD_SCRIPT_SHA256", "../issue779-csharp-build/build-fdd.sh"),
            ("ACQUISITION_SHA256", "acquire-inbound.sh"),
            ("ROOT_TRANSPORT_SHA256", "prepare-root-inputs-v2.sh"),
            ("ORCHESTRATOR_SHA256", "run-native.py"),
            ("UBUNTU_RUNTIME_SHA256", "prepare-ubuntu-runtime.py"),
            ("OWNER_DATA_TEST_SHA256", "test_owner_death_data.py"),
        ):
            digest = hashlib.sha256((folder / relative).read_bytes()).hexdigest()
            self.assertIn(f"{variable}: {digest}", workflow)
        self.assertIn(f"ORCHESTRATOR_SHA256: {runner_sha}", workflow)
        self.assertIn(f"UBUNTU_RUNTIME_SHA256: {runtime_sha}", workflow)


class PreMeasurementCleanupControls(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        text = Path("checkpoint-owner-death-common.sh").read_text()
        begin = "# BEGIN PREMEASUREMENT CLEANUP PROCEDURE"
        end = "# END PREMEASUREMENT CLEANUP PROCEDURE"
        cls.procedure = text.split(begin, 1)[1].split(end, 1)[0].replace('/usr/bin/python3.12', '"$PYTHON_GROUP_EXEC"')
        cls.owner_code = text.split("<<'LAUNCHER_OWNER_PY' || :\n", 1)[1].split('\nLAUNCHER_OWNER_PY', 1)[0]
        cls.client_code = text.split("<<'LAUNCHER_CLIENT_PY' || :\n", 1)[1].split('\nLAUNCHER_CLIENT_PY', 1)[0]

    def _prepare_case(self, case):
        import os
        import sys
        import tempfile
        from pathlib import Path

        temp = tempfile.TemporaryDirectory(prefix="n13-n14-cleanup-")
        root = Path(temp.name)
        state_root = root / "state"
        cg_root = root / "cgroup"
        job_file = root / "jobs"
        call_file = root / "calls"
        quarantine = root / "QUARANTINED"
        group_pid_file = root / "group-pid"
        launch_ready_file = root / "launcher.setsid-ready"
        launch_events_file = root / "launch-events"
        state_root.mkdir()
        cg_root.mkdir()
        fifo_paths = {}
        if case.startswith("launch-"):
            import os

            for name in ("start", "release", "ready", "hold", "exit"):
                path = root / (name + ".fifo")
                os.mkfifo(path)
                fifo_paths[name] = path
        units = (WORKER, OWNER)
        rows = []
        for unit in units:
            state = state_root / unit
            state.mkdir()
            if case in ("pending-start", "absent"):
                values = {
                    "LoadState": "not-found", "ActiveState": "inactive",
                    "SubState": "dead", "MainPID": "0", "ControlGroup": ""
                }
                if case == "pending-start":
                    rows.append(f"41 {unit} start")
            else:
                cg = "/system.slice/" + unit
                if case == "foreign" and unit == OWNER:
                    cg = "/system.slice/foreign.service"
                values = {
                    "LoadState": "loaded", "ActiveState": "active",
                    "SubState": "running", "MainPID": "101",
                    "ControlGroup": cg
                }
                rows.append(f"41 {unit} start")
                cg_dir = cg_root / unit
                cg_dir.mkdir()
                (cg_dir / "cgroup.events").write_text("populated 1\n")
            for key, value in values.items():
                (state / key).write_text(value)
        job_file.write_text("\n".join(rows) + ("\n" if rows else ""))

        prefix = r"""#!/bin/bash
set -euo pipefail
GEN=0123456789abcdef0123456789abcdef
OWNER=appsurface-evidence-owner-$GEN.service
WORKER=appsurface-evidence-worker-$GEN.service
owner=$OWNER
worker=$WORKER
G=$GEN
CLEANUP_RESERVE=30
FAKE_NOW=1000
hard_end_ms=601000
if [[ $CLEANUP_CASE == budget-expired ]]; then FAKE_NOW=100000; hard_end_ms=100000; fi
launched=1
measurement_done=0
success=0
launch_pid=
launch_pgid=
quarantine=$QUARANTINE_FILE
STATE_ROOT=$STATE_ROOT
JOB_FILE=$JOB_FILE
CALL_FILE=$CALL_FILE
cleanup_cgroup_root=$CG_ROOT
mark_failure(){ printf '%s\n' 'N13_N14_MEASUREMENT_FAILED_OUTPUT_QUARANTINED' >"$quarantine"; }
mono_ms(){
  if [[ $CLEANUP_CASE == launch-* || $CLEANUP_CASE == group ]]; then
    "$PYTHON_GROUP_EXEC" -c 'import time; print(time.monotonic_ns()//1000000)'
  else
    printf '%s' "$FAKE_NOW"
  fi
}
timeout(){ shift 3; "$@"; }
sleep(){
  if [[ $CLEANUP_CASE == launch-visible && ${VISIBILITY_RELEASED:-0} == 0 ]]; then
    VISIBILITY_RELEASED=1
    for unit in "$worker" "$owner"; do settle_unit "$unit"; done
  elif [[ $CLEANUP_CASE == launch-* ]]; then
    /bin/sleep "${1:-0.01}"
  else
    /bin/sleep 0.01
    FAKE_NOW=$((FAKE_NOW+1000))
  fi
}
state_get(){
  local unit=$1 key=$2
  /bin/cat "$STATE_ROOT/$unit/$key"
}
state_set(){
  local unit=$1 key=$2 value=$3
  printf '%s' "$value" >"$STATE_ROOT/$unit/$key"
}
settle_unit(){
  local unit=$1
  state_set "$unit" LoadState loaded
  state_set "$unit" ActiveState failed
  state_set "$unit" SubState failed
  state_set "$unit" MainPID 0
  state_set "$unit" ControlGroup "/system.slice/$unit"
  if [[ -d $cleanup_cgroup_root/$unit ]]; then
    printf 'populated 0\n' >"$cleanup_cgroup_root/$unit/cgroup.events"
  fi
  : >"$JOB_FILE"
}
systemctl(){
  printf 'CALL:%s\n' "$*" >>"$CALL_FILE"
  local op=$1 unit prop
  shift
  case $op in
    show)
      unit=$1
      shift
      while (($#)); do
        if [[ $1 == -p ]]; then
          prop=$2
          case $prop in
            LoadState|ActiveState|SubState|MainPID|ControlGroup)
              printf '%s=%s\n' "$prop" "$(state_get "$unit" "$prop")" ;;
            *) return 9 ;;
          esac
          shift 2
        else
          shift
        fi
      done
      ;;
    list-jobs)
      /bin/cat "$JOB_FILE"
      ;;
    stop)
      if [[ $1 == --no-block ]]; then shift; fi
      if [[ $CLEANUP_CASE == stop-error ]]; then return 7; fi
      if [[ $CLEANUP_CASE == launch-leader-exited ]]; then
        printf x >"$RELEASE_FIFO"
        IFS= read -r -n 1 ready_ack <"$READY_FIFO" || return 7
        printf x >"$EXIT_FIFO"
        while ! cleanup_child_zombie "$launch_pid"; do
          local now
          now=$(mono_ms) || return 7
          ((now<cleanup_deadline_ms)) || return 7
          cleanup_exec sleep .01 || return 7
        done
      fi
      if [[ $CLEANUP_CASE != stop-timeout && $CLEANUP_CASE != launch-visible ]]; then
        while (($#)); do settle_unit "$1"; shift; done
      fi
      [[ $CLEANUP_CASE != launch-visible ]] || return 0
      ;;
    kill)
      while [[ $1 == --* ]]; do shift; done
      while (($#)); do settle_unit "$1"; shift; done
      ;;
    *) return 8 ;;
  esac
}
"""
        prefix += "read -r -d '' LAUNCHER_OWNER_CODE <<'OWNER_PY' || :\n" + self.owner_code + "\nOWNER_PY\n"
        prefix += "read -r -d '' LAUNCHER_OWNER_CLIENT <<'CLIENT_PY' || :\n" + self.client_code + "\nCLIENT_PY\n"
        script = prefix + self.procedure + r"""
cleanup_cgroup_root=$CG_ROOT
# The fixture is Linux-only; use the host's process-state view when running this
# extracted control on a development host without procfs.
if [[ ! -r /proc/self/stat ]]; then
  cleanup_guard_exited(){
    local state
    state=$(ps -o stat= -p "$launch_guard_pid" 2>/dev/null) || { /bin/kill -0 "$launch_guard_pid" 2>/dev/null && return 1; return 0; }
    [[ ${state:0:1} == Z ]]
  }

fi
if [[ $CLEANUP_CASE == group ]]; then
  launch_ready_file=$LAUNCH_READY_FILE
  setup_ms=$(mono_ms)
  hard_end_ms=$((setup_ms+10000))
  launcher_owner_start "$GROUP_PID_FILE" "$GROUP_PID_FILE.sock" "$LAUNCH_READY_FILE" "$hard_end_ms" /bin/sleep 30 || exit 70
  printf '%s' "$launch_pid" >"$GROUP_PID_FILE"
  cleanup_started_ms=$(mono_ms) || exit 70
  cleanup_deadline_ms=$((cleanup_started_ms+5000))
  while [[ ! -f $launch_ready_file ]]; do
    cleanup_now=$(mono_ms) || exit 70
    ((cleanup_now<cleanup_deadline_ms)) || exit 70
  done
  cleanup_launch_ready || exit 70
  if cleanup_launch_group; then
    if /bin/kill -0 -- "-$launch_pgid" 2>/dev/null; then exit 8; fi
  else
    exit 9
  fi
  printf 'GROUP_REAPED\n'
  exit 0
fi
if [[ $CLEANUP_CASE == launch-* ]]; then
  CLEANUP_RESERVE=3
  cleanup_setup_ms=$(mono_ms) || exit 72
  hard_end_ms=$((cleanup_setup_ms+10000))
  launch_ready_file=$LAUNCH_READY_FILE
  launcher_owner_start "$GROUP_PID_FILE" "$GROUP_PID_FILE.sock" "$LAUNCH_READY_FILE" "$hard_end_ms" "$PYTHON_GROUP_EXEC" -c "$GROUP_CHILD_CODE" || exit 70
  IFS= read -r -n 1 started <"$START_FIFO" || exit 71
  if [[ $CLEANUP_CASE == launch-visible ]]; then
    printf x >"$RELEASE_FIFO"
    IFS= read -r -n 1 ready_ack <"$READY_FIFO" || exit 71
  fi
  if [[ $CLEANUP_CASE == launch-pending || $CLEANUP_CASE == launch-timeout || $CLEANUP_CASE == launch-late-unit ]]; then
    /bin/rm "$LAUNCH_READY_FILE"
  fi
  launch_started_ms=$(mono_ms) || exit 72
  cleanup_rc=0
  premeasurement_cleanup || cleanup_rc=$?
  launch_finished_ms=$(mono_ms) || exit 73
  mark_failure
  printf 'PREMEASUREMENT_RC=%s\nLAUNCH_PID=%s\nLAUNCH_PGID=%s\nLAUNCH_STATUS=%s\nCLEANUP_ELAPSED_MS=%s\n' \
    "$cleanup_rc" "$launch_pid" "$launch_pgid" "${launch_status:-}" "$((launch_finished_ms-launch_started_ms))"
  exit 23
fi
if (exit 23); then :; else cleanup; fi
"""
        env = os.environ.copy()
        env.update({
            "PATH": "/usr/bin:/bin",
            "CLEANUP_CASE": case,
            "STATE_ROOT": str(state_root),
            "CG_ROOT": str(cg_root),
            "JOB_FILE": str(job_file),
            "CALL_FILE": str(call_file),
            "QUARANTINE_FILE": str(quarantine),
            "GROUP_PID_FILE": str(group_pid_file),
            "LAUNCH_READY_FILE": str(launch_ready_file),
            "PYTHON_GROUP_EXEC": sys.executable,
        })
        if fifo_paths:
            import textwrap

            mode = case.removeprefix("launch-")
            child_code = textwrap.dedent(r"""
                import os, signal, sys

                def fifo_write(path, token):
                    with open(path, "wb", buffering=0) as stream:
                        stream.write(token)

                def fifo_read(path):
                    with open(path, "rb", buffering=0) as stream:
                        return stream.read(1)

                mode = os.environ["CHILD_MODE"]
                if mode in ("pending", "timeout", "late-unit"):
                    def on_term(signum, frame):
                        with open(os.environ["LAUNCH_EVENTS_FILE"], "a") as stream:
                            stream.write("TERM\n")
                        if mode == "late-unit":
                            from pathlib import Path
                            state_root = Path(os.environ["STATE_ROOT"])
                            for unit in state_root.iterdir():
                                for key, value in {
                                    "LoadState": "loaded", "ActiveState": "active",
                                    "SubState": "running", "MainPID": "101",
                                    "ControlGroup": "/system.slice/" + unit.name,
                                }.items():
                                    (unit / key).write_text(value)
                            with open(os.environ["JOB_FILE"], "w") as stream:
                                stream.write("41 " + next(state_root.iterdir()).name + " start\n")
                        if mode == "timeout":
                            signal.signal(signal.SIGTERM, signal.SIG_IGN)
                        else:
                            os._exit(0)
                    signal.signal(signal.SIGTERM, on_term)
                    fifo_write(os.environ["START_FIFO"], b"S")
                    fifo_read(os.environ["HOLD_FIFO"])
                    os._exit(0)

                fifo_write(os.environ["START_FIFO"], b"S")
                fifo_read(os.environ["RELEASE_FIFO"])
                fifo_write(os.environ["READY_FIFO"], b"R")
                if mode == "leader-exited":
                    fifo_read(os.environ["EXIT_FIFO"])
                    os._exit(0)
                fifo_read(os.environ["HOLD_FIFO"])
                os._exit(0)
            """).strip()
            env.update({
                "CHILD_MODE": mode,
                "GROUP_CHILD_CODE": child_code,
                "LAUNCH_EVENTS_FILE": str(launch_events_file),
                "LAUNCH_READY_FILE": str(launch_ready_file),
                "START_FIFO": str(fifo_paths["start"]),
                "RELEASE_FIFO": str(fifo_paths["release"]),
                "READY_FIFO": str(fifo_paths["ready"]),
                "HOLD_FIFO": str(fifo_paths["hold"]),
                "EXIT_FIFO": str(fifo_paths["exit"]),
            })
        return temp, script, env, call_file, quarantine, group_pid_file

    def _run_cleanup(self, case):
        import subprocess

        temp, script, env, call_file, quarantine, group_pid_file = self._prepare_case(case)
        try:
            proc = subprocess.run(
                ["/bin/bash", "-c", script], env=env, text=True,
                capture_output=True, timeout=6, check=False)
            calls = call_file.read_text() if call_file.exists() else ""
            marker = quarantine.read_text() if quarantine.exists() else ""
            return proc, calls, marker, group_pid_file
        finally:
            temp.cleanup()

    def test_current_common_fixture_is_bash_syntax_valid(self):
        import subprocess
        fixture = Path("checkpoint-owner-death-common.sh")
        proc = subprocess.run(["/bin/bash", "-n", str(fixture)],
                              capture_output=True, text=True, timeout=2, check=False)
        self.assertEqual(proc.returncode, 0, proc.stderr)

    def test_premeasurement_stop_settles_generated_units_jobs_and_cgroups_but_keeps_failure(self):
        proc, calls, marker, _ = self._run_cleanup("stop")
        self.assertEqual(proc.returncode, 23)
        self.assertIn("CALL:stop --no-block " + WORKER + " " + OWNER, calls)
        self.assertNotIn("CALL:kill", calls)
        self.assertIn("MEASUREMENT_FAILED_OUTPUT_QUARANTINED", marker)

    def test_not_found_units_with_exact_pending_start_jobs_are_stopped_before_exit(self):
        proc, calls, marker, _ = self._run_cleanup("pending-start")
        self.assertEqual(proc.returncode, 23)
        self.assertIn("CALL:stop --no-block " + WORKER + " " + OWNER, calls)
        self.assertIn("MEASUREMENT_FAILED_OUTPUT_QUARANTINED", marker)

    def test_already_absent_generated_units_and_cgroups_need_no_signal_and_keep_failure(self):
        proc, calls, marker, _ = self._run_cleanup("absent")
        self.assertEqual(proc.returncode, 23)
        self.assertIn("CALL:show " + WORKER, calls)
        self.assertIn("CALL:show " + OWNER, calls)
        self.assertIn("CALL:list-jobs", calls)
        self.assertNotIn("CALL:stop", calls)
        self.assertNotIn("CALL:kill", calls)
        self.assertNotIn("CLEANUP_UNRESOLVED", proc.stderr)
        self.assertIn("MEASUREMENT_FAILED_OUTPUT_QUARANTINED", marker)

    def test_stop_timeout_and_stop_error_fall_back_to_generated_unit_kill_without_green_status(self):
        for case in ("stop-timeout", "stop-error"):
            with self.subTest(case=case):
                proc, calls, marker, _ = self._run_cleanup(case)
                self.assertEqual(proc.returncode, 23)
                self.assertIn("CALL:kill --kill-whom=all --signal=KILL " + WORKER + " " + OWNER, calls)
                self.assertIn("MEASUREMENT_FAILED_OUTPUT_QUARANTINED", marker)

    def test_foreign_cgroup_facts_fail_closed_without_signalling_units(self):
        proc, calls, marker, _ = self._run_cleanup("foreign")
        self.assertEqual(proc.returncode, 23)
        self.assertNotIn("CALL:stop", calls)
        self.assertNotIn("CALL:kill", calls)
        self.assertIn("MEASUREMENT_FAILED_OUTPUT_QUARANTINED", marker)
        self.assertIn("CLEANUP_UNRESOLVED", proc.stderr)

    def test_expired_deadline_fails_closed_and_preserves_original_failure(self):
        proc, calls, marker, _ = self._run_cleanup("budget-expired")
        self.assertEqual(proc.returncode, 23)
        self.assertEqual(calls, "")
        self.assertIn("MEASUREMENT_FAILED_OUTPUT_QUARANTINED", marker)
        self.assertIn("CLEANUP_UNRESOLVED", proc.stderr)

    def test_generated_launcher_process_group_is_terminated_and_reaped(self):
        import os
        import signal
        import subprocess

        temp, script, env, _, _, group_pid_file = self._prepare_case("group")
        try:
            proc = subprocess.Popen(
                ["/bin/bash", "-c", script], env=env, text=True,
                stdout=subprocess.PIPE, stderr=subprocess.PIPE, start_new_session=True)
            try:
                stdout, stderr = proc.communicate(timeout=6)
            except subprocess.TimeoutExpired:
                if group_pid_file.exists():
                    try:
                        os.killpg(int(group_pid_file.read_text()), signal.SIGKILL)
                    except ProcessLookupError:
                        pass
                try:
                    os.killpg(proc.pid, signal.SIGKILL)
                except ProcessLookupError:
                    pass
                proc.communicate()
                self.fail("extracted cleanup procedure exceeded its bounded control")
            self.assertEqual(proc.returncode, 0, stderr)
            self.assertIn("GROUP_REAPED", stdout)
        finally:
            if group_pid_file.exists():
                try:
                    os.killpg(int(group_pid_file.read_text()), signal.SIGKILL)
                except ProcessLookupError:
                    pass
            if proc.poll() is None:
                try:
                    os.killpg(proc.pid, signal.SIGKILL)
                except ProcessLookupError:
                    pass
                proc.communicate(timeout=2)
            if proc.stdout:
                proc.stdout.close()
            if proc.stderr:
                proc.stderr.close()
            temp.cleanup()

    def _run_launcher_case(self, case):
        import os
        import signal
        import subprocess

        temp, script, env, call_file, quarantine, group_pid_file = self._prepare_case(case)
        proc = None
        try:
            proc = subprocess.Popen(
                ["/bin/bash", "-c", script], env=env, text=True,
                stdout=subprocess.PIPE, stderr=subprocess.PIPE, start_new_session=True)
            stdout, stderr = proc.communicate(timeout=8)
            pid = int(group_pid_file.read_text())
            with self.assertRaises(ProcessLookupError):
                os.kill(pid, 0)
            with self.assertRaises(ProcessLookupError):
                os.killpg(pid, 0)
            facts = dict(line.split("=", 1) for line in stdout.splitlines() if "=" in line)
            self.assertEqual(proc.returncode, 23, stderr)
            self.assertEqual(facts["PREMEASUREMENT_RC"], "0", stderr)
            self.assertEqual(facts["LAUNCH_PID"], "")
            self.assertEqual(facts["LAUNCH_PGID"], "")
            self.assertLess(int(facts["CLEANUP_ELAPSED_MS"]), 3000)
            self.assertIn("MEASUREMENT_FAILED_OUTPUT_QUARANTINED", quarantine.read_text())
            self.assertNotIn("CLEANUP_UNRESOLVED", stderr)
            events_path = Path(env["LAUNCH_EVENTS_FILE"])
            events = events_path.read_text() if events_path.exists() else ""
            return facts, events, call_file.read_text()
        finally:
            if proc is not None and proc.poll() is None:
                # This test owns the session, and the exact child may have formed
                # its separate session before cleanup failed. Join both on error.
                if group_pid_file.exists():
                    pid = int(group_pid_file.read_text())
                    for send in (lambda: os.killpg(pid, signal.SIGKILL),
                                 lambda: os.kill(pid, signal.SIGKILL)):
                        try:
                            send()
                        except ProcessLookupError:
                            pass
                try:
                    os.killpg(proc.pid, signal.SIGKILL)
                except ProcessLookupError:
                    pass
                proc.communicate(timeout=2)
            temp.cleanup()

    def test_parent_owned_pending_launcher_is_terminated_and_waited(self):
        facts, events, _ = self._run_launcher_case("launch-pending")
        self.assertEqual(facts["LAUNCH_STATUS"], "0")
        self.assertEqual(events, "TERM\n")

    def test_parent_owned_launcher_ignoring_term_is_killed_and_waited_within_same_deadline(self):
        facts, events, _ = self._run_launcher_case("launch-timeout")
        self.assertEqual(facts["LAUNCH_STATUS"], "137")
        self.assertEqual(events, "TERM\n")

    def test_retained_launcher_group_during_unit_stop_is_terminated_and_waited(self):
        facts, _, calls = self._run_launcher_case("launch-visible")
        self.assertEqual(facts["LAUNCH_STATUS"], "143")
        self.assertIn("CALL:stop --no-block " + WORKER + " " + OWNER, calls)

    def test_launcher_exited_before_final_join_is_reaped_without_undefined_group(self):
        facts, _, _ = self._run_launcher_case("launch-leader-exited")
        self.assertEqual(facts["LAUNCH_STATUS"], "0")

    def test_late_generated_start_after_initial_unit_join_is_stopped_after_launcher_reap(self):
        facts, events, calls = self._run_launcher_case("launch-late-unit")
        self.assertEqual(facts["LAUNCH_STATUS"], "0")
        self.assertEqual(events, "TERM\n")
        stop = "CALL:stop --no-block " + WORKER + " " + OWNER
        self.assertEqual(calls.count(stop), 2)


class LauncherSignalOwnershipControls(unittest.TestCase):
    """Real child ownership; no systemd, privilege or native acceptance claim."""
    @classmethod
    def setUpClass(cls):
        text = Path('checkpoint-owner-death-common.sh').read_text()
        cls.code = text.split("<<'LAUNCHER_OWNER_PY' || :\n", 1)[1].split('\nLAUNCHER_OWNER_PY', 1)[0]

    def _run(self, argv, check, seconds=8):
        import contextlib, os, signal, socket, subprocess, sys, tempfile, time
        with tempfile.TemporaryDirectory(prefix='lo-', dir='/tmp') as folder:
            root = Path(folder)
            deadline = time.monotonic() + seconds
            proc = subprocess.Popen([sys.executable, '-I', '-S', '-B', '-c', self.code,
                str(root/'pid'), str(root/'sock'), str(root/'ready'),
                str(int(deadline*1000)), *argv], stdout=subprocess.DEVNULL,
                stderr=subprocess.PIPE, start_new_session=True)
            def request(operation, pid=None):
                with socket.socket(socket.AF_UNIX) as stream:
                    stream.settimeout(min(1, max(.01, deadline-time.monotonic())))
                    stream.connect(str(root/'sock'))
                    stream.sendall((operation+' '+str(pid or child)+'\n').encode())
                    data = b''
                    while not data.endswith(b'\n'):
                        chunk = stream.recv(64)
                        if not chunk: raise EOFError()
                        data += chunk
                    return data.decode().strip().split()
            try:
                while not (root/'pid').exists():
                    self.assertLess(time.monotonic(), deadline)
                    self.assertIsNone(proc.poll())
                    time.sleep(.01)
                child = int((root/'pid').read_text())
                check(proc, child, request, deadline)
            finally:
                if proc.poll() is None:
                    with contextlib.suppress(OSError, EOFError): request('kill')
                    until=min(deadline,time.monotonic()+1)
                    while time.monotonic()<until:
                        try:
                            if request('state')[0]=='exited': request('join'); break
                        except (OSError,EOFError): break
                        time.sleep(.01)
                if proc.poll() is None:
                    # This test is the unreaping parent of its own helper group.
                    os.killpg(proc.pid, signal.SIGKILL)
                proc.communicate(timeout=2)
                proc.stderr.close()

    def test_exited_child_stays_reserved_across_term_kill_and_is_reaped_only_on_join(self):
        import os, time
        def check(proc, pid, request, deadline):
            while request('state')[0] != 'exited':
                self.assertLess(time.monotonic(),deadline); time.sleep(.01)
            os.kill(pid,0)
            self.assertEqual(request('term'), ['exited','0','7'])
            os.kill(pid,0)
            self.assertEqual(request('kill'), ['exited','0','7'])
            os.kill(pid,0)
            self.assertEqual(request('join'), ['joined','0','7'])
            self.assertEqual(proc.wait(timeout=1),0)
            with self.assertRaises(ProcessLookupError): os.kill(pid,0)
        self._run(['/bin/sh','-c','exit 7'],check)

    def test_cooperative_stop_holds_original_child_until_join(self):
        import os, time
        def check(proc,pid,request,deadline):
            request('term')
            while request('state')[0] != 'exited':
                self.assertLess(time.monotonic(),deadline); time.sleep(.01)
            os.kill(pid,0)
            self.assertEqual(request('join'),['joined','0','143'])
            self.assertEqual(proc.wait(timeout=1),0)
        self._run(['/bin/sleep','30'],check)

    def test_wrong_pid_rejects_without_signalling_a_separate_live_child(self):
        import subprocess
        canary=subprocess.Popen(['/bin/sleep','30'],start_new_session=True)
        try:
            def check(proc,pid,request,deadline):
                with self.assertRaises((EOFError,ConnectionResetError)):
                    request('kill',canary.pid)
                self.assertNotEqual(proc.wait(timeout=2),0)
                self.assertIsNone(canary.poll())
            self._run(['/bin/sleep','30'],check)
        finally:
            canary.terminate();canary.wait(timeout=2)

    def test_final_deadline_cleanup_does_not_skip_kill_after_an_expired_group_scan(self):
        import os, time
        def check(proc,pid,request,deadline):
            self.assertNotEqual(proc.wait(timeout=3),0)
            with self.assertRaises(ProcessLookupError): os.kill(pid,0)
        self._run(['/bin/sleep','30'],check,seconds=2)

    def test_malformed_request_joins_live_descendant_after_its_leader_exits(self):
        import os, sys, tempfile, time
        with tempfile.TemporaryDirectory(prefix='ld-', dir='/tmp') as folder:
            marker = str(Path(folder)/'descendant')
            code = "import os,time; p=os.fork(); " + "\nif p==0:\n open(" + repr(marker) + ", 'w').write(str(os.getpid())); time.sleep(30)\nelse: os._exit(0)"
            def check(proc,pid,request,deadline):
                while not Path(marker).exists() or request('state')[0] != 'exited':
                    self.assertLess(time.monotonic(),deadline); time.sleep(.01)
                descendant = int(Path(marker).read_text())
                self.assertEqual(request('state')[:2],['exited','1'])
                with self.assertRaises((EOFError,ConnectionResetError)): request('bad-operation')
                self.assertNotEqual(proc.wait(timeout=3),0)
                with self.assertRaises(ProcessLookupError): os.kill(pid,0)
                with self.assertRaises(ProcessLookupError): os.kill(descendant,0)
            self._run([sys.executable,'-c',code],check)

    def test_live_join_rejects_and_cleans_its_owned_child_without_success_ack(self):
        import os
        def check(proc,pid,request,deadline):
            with self.assertRaises((EOFError,ConnectionResetError)): request('join')
            self.assertNotEqual(proc.wait(timeout=2),0)
            with self.assertRaises(ProcessLookupError): os.kill(pid,0)
        self._run(['/bin/sleep','30'],check)

if __name__ == "__main__":
    unittest.main(verbosity=2)
