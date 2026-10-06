"""Prove the verifier stops its owned stage and child on interruption or timeout."""

import json
import os
from pathlib import Path
import signal
import subprocess
import sys
import tempfile
import time

repo = Path(__file__).resolve().parents[2]
verifier = repo / "scripts/verify-evidencehost-cleanup-package.sh"
workspace = Path(tempfile.mkdtemp(prefix="evidencehost-verifier-interruption-"))
shim = workspace / "dotnet"
shim.write_text("""#!/usr/bin/env python3
import json, os, pathlib, signal, time
signal.signal(signal.SIGINT, signal.SIG_IGN)
signal.signal(signal.SIGTERM, signal.SIG_IGN)
child = os.fork()
if child == 0:
 while True: time.sleep(0.1)
pathlib.Path(os.environ['EVIDENCEHOST_SHIM_RECEIPT']).write_text(json.dumps({'pid':os.getpid(),'child':child,'pgid':os.getpgrp()}))
while True: time.sleep(0.1)
""")
shim.chmod(0o755)


def live(pid):
    status = subprocess.run(["ps", "-p", str(pid), "-o", "stat="], text=True, capture_output=True, check=False)
    if status.returncode not in (0, 1):
        raise RuntimeError(f"Could not inspect fixture process {pid}: {status.stderr}")
    value = status.stdout.strip()
    return bool(value) and not value.startswith("Z")


results = []
for mode, signum, expected_exit in (("SIGINT", signal.SIGINT, 130), ("SIGTERM", signal.SIGTERM, 143), ("deadline", None, 1)):
    work = workspace / mode
    marker = workspace / f"{mode}.json"
    env = os.environ.copy()
    env["PATH"] = str(workspace) + os.pathsep + env["PATH"]
    env["EVIDENCEHOST_SHIM_RECEIPT"] = str(marker)
    log = workspace / f"{mode}.log"
    process = None
    owned = None
    try:
        with log.open("wb") as output:
            argv = [str(verifier), "--work-directory", str(work), "--package-version", "0.2.0-interruption-proof",
                    "--total-timeout-seconds", "2" if signum is None else "30"]
            process = subprocess.Popen(argv, env=env, stdout=output, stderr=subprocess.STDOUT, start_new_session=True)
            limit = time.monotonic() + 5
            while not marker.exists() and process.poll() is None and time.monotonic() < limit:
                time.sleep(0.02)
            assert marker.exists(), log.read_text()
            owned = json.loads(marker.read_text())
            assert owned["pid"] == owned["pgid"], "Fixture stage must own its process group."
            if signum is not None:
                process.send_signal(signum)
            assert process.wait(timeout=8) == expected_exit, log.read_text()
            limit = time.monotonic() + 2
            while any(live(owned[key]) for key in ("pid", "child")) and time.monotonic() < limit:
                time.sleep(0.02)
            assert not any(live(owned[key]) for key in ("pid", "child")), f"{mode} left an owned process alive."
            assert "package consumer: FAIL" in (work / "result.txt").read_text()
            results.append({"mode": mode, "exit_code": expected_exit, "owned_processes_alive": 0})
    finally:
        # The fixture also cleans up on assertion failure, independently of the verifier.
        if owned:
            try:
                os.killpg(owned["pgid"], signal.SIGKILL)
            except ProcessLookupError:
                pass
        if process is not None and process.poll() is None:
            os.killpg(process.pid, signal.SIGKILL)
            process.wait(timeout=5)

print(json.dumps({"result": "PASS", "workspace": str(workspace), "scenarios": results}, indent=2))
