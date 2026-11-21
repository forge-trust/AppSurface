"""Cross-platform process and black-box contracts for the PWA file runner."""

from __future__ import annotations

import contextlib
import http.server
import json
import os
import pathlib
import re
import shutil
import socket
import subprocess
import sys
import tempfile
import threading
import time
import urllib.request
import uuid

from native_console import launch_native_console, process_is_running, send_ctrl_c


ROOT = pathlib.Path(__file__).resolve().parents[2]
DOTNET = os.environ.get("DOTNET_EXE", "dotnet")
EVIDENCE_ROOT = pathlib.Path(os.environ.get("PWA_VERIFY_EVIDENCE_DIR", tempfile.gettempdir()))
TIMEOUT = 180
STARTUP_PHASE_TIMEOUT = 240
VERIFY_PHASE_TIMEOUT = 180
CANCEL_WATCHDOG = 10


def free_port() -> int:
    with socket.socket() as listener:
        listener.bind(("127.0.0.1", 0))
        return int(listener.getsockname()[1])


def run_command(repo_root: pathlib.Path, port: int, evidence: pathlib.Path, startup_timeout: int = 60):
    command = [
        DOTNET, "run", "--file", str(repo_root / "scripts/web-pwa/verify.cs"), "--",
        "--port", str(port), "--evidence-path", str(evidence),
        "--startup-timeout-seconds", str(startup_timeout),
    ]
    child_env = os.environ.copy()
    child_env.update({
        "DOTNET_CLI_USE_MSBUILD_SERVER": "0",
        "UseSharedCompilation": "false",
        "MSBUILDDISABLENODEREUSE": "1",
    })
    child_env["PATH"] = f"{pathlib.Path(DOTNET).resolve().parent}{os.pathsep}{child_env.get('PATH', '')}"
    child_env.pop("APP_SURFACE_WEB_PWA_PORT", None)
    child_env.pop("APP_SURFACE_WEB_PWA_EVIDENCE", None)
    child_env.pop("PWAVERIFY__STARTUPTIMEOUTSECONDS", None)
    process = launch_native_console(
        command, cwd=repo_root, env=child_env,
        stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
    )
    process.started_at = time.monotonic()
    process.stdout_lines = []
    process.stderr_lines = []

    def read_lines(stream, output: list[str]) -> None:
        for line in stream:
            output.append(line)

    assert process.stdout is not None and process.stderr is not None
    threading.Thread(target=read_lines, args=(process.stdout, process.stdout_lines), daemon=True).start()
    threading.Thread(target=read_lines, args=(process.stderr, process.stderr_lines), daemon=True).start()
    return process


def finish(process: subprocess.Popen[str], timeout: int = TIMEOUT) -> tuple[int, str, str]:
    code = process.wait(timeout=timeout)
    time.sleep(0.1)
    return code, "".join(process.stdout_lines), "".join(process.stderr_lines)


def wait_for_phase(process: subprocess.Popen[str], phase: str, timeout: float = 120) -> None:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        stderr = "".join(process.stderr_lines)
        if phase in stderr:
            return
        if process.poll() is not None:
            raise AssertionError(f"Exited before {phase!r} ({process.returncode}).\n{stderr}")
        time.sleep(0.05)
    raise AssertionError(f"Timed out waiting for {phase!r}.\n{''.join(process.stderr_lines)}")


def assert_report(path: pathlib.Path, passed: bool, exit_code: int) -> dict:
    report = json.loads(path.read_text(encoding="utf-8"))
    assert report.get("schemaVersion") == 3, f"unexpected fields: {report.keys()}"
    assert report.get("surface") == "all"
    assert report.get("passed") is passed
    assert isinstance(report.get("install"), dict)
    assert isinstance(report.get("push"), dict)
    assert isinstance(report.get("diagnostics"), list)
    assert (exit_code == 0) is passed
    return report


def assert_expected_icon_failure(report: dict) -> None:
    diagnostics = report["diagnostics"]
    matching = [item for item in diagnostics if item.get("code") == "ASPWA212"]
    expected = {
        "code": "ASPWA212",
        "severity": "error",
        "message": "Icon /icons/app-192.svg returned HTTP 404.",
        "subject": "manifest.icons[].src",
        "expected": "2xx",
        "actual": "HTTP 404",
        "fix": "Publish the icon file at the declared manifest path.",
        "docsUrl": None,
    }
    assert matching == [expected], f"Expected exactly the known missing-icon diagnostic, got {matching!r}"


def assert_port_reusable(port: int) -> None:
    with socket.socket() as listener:
        listener.settimeout(3)
        listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        listener.bind(("127.0.0.1", port))
        listener.listen(1)


@contextlib.contextmanager
def edit_text(path: pathlib.Path, transform):
    original = path.read_text(encoding="utf-8")
    updated = transform(original)
    assert updated != original, f"fixture edit failed for {path}"
    path.write_text(updated, encoding="utf-8")
    try:
        yield
    finally:
        path.write_text(original, encoding="utf-8")


@contextlib.contextmanager
def temporarily_removed(path: pathlib.Path):
    content = path.read_bytes()
    path.unlink()
    try:
        yield
    finally:
        path.write_bytes(content)


def make_fixture_checkout(destination: pathlib.Path) -> pathlib.Path:
    destination = destination.resolve()
    destination.parent.mkdir(parents=True, exist_ok=True)
    excluded = {
        ".git", ".codex", ".gstack", "bin", "obj", "node_modules",
        "artifacts", ".nuget", "TestResults", "packages", "benchmark-results",
    }
    shutil.copytree(ROOT, destination, ignore=lambda _directory, names: excluded.intersection(names))
    return destination.resolve()


def assert_children_stopped(stderr: str) -> None:
    for phase in ("PWA verifier starting example", "PWA verifier running public CLI"):
        match = re.search(re.escape(phase) + r" \(PID (\d+)\)\.", stderr)
        if match is None:
            continue
        pid = int(match.group(1))
        deadline = time.monotonic() + 3
        while time.monotonic() < deadline and process_is_running(pid):
            time.sleep(0.05)
        assert not process_is_running(pid), f"Owned {phase} PID {pid} remains after exit."


def run_success(repo_root: pathlib.Path, evidence: pathlib.Path, label: str) -> float:
    port = free_port()
    process = run_command(repo_root, port, evidence)
    try:
        wait_for_phase(process, "PWA verifier running public CLI", VERIFY_PHASE_TIMEOUT)
        code, stdout, stderr = finish(process)
        assert code == 0, f"{label} run failed ({code})\n{stdout}\n{stderr}"
        assert_report(evidence, passed=True, exit_code=0)
        assert_children_stopped(stderr)
        assert_port_reusable(port)
        return time.monotonic() - process.started_at
    finally:
        if process.poll() is None:
            send_ctrl_c(process)
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=5)


def cost_samples() -> None:
    cold = EVIDENCE_ROOT / f"pwa-cost-cold-{uuid.uuid4().hex}.json"
    warm = EVIDENCE_ROOT / f"pwa-cost-warm-{uuid.uuid4().hex}.json"
    cold_seconds = run_success(ROOT, cold, "cold")
    warm_seconds = run_success(ROOT, warm, "warm")
    print(
        f"file-runner-cost runner={os.environ.get('RUNNER_NAME', 'local')} "
        f"os={os.environ.get('RUNNER_OS', sys.platform)} "
        f"cache={os.environ.get('NUGET_PACKAGES', 'shared-default')} "
        f"cold-seconds={cold_seconds:.2f} warm-seconds={warm_seconds:.2f}"
    )


def native_success_and_failure(fixture: pathlib.Path) -> None:
    EVIDENCE_ROOT.mkdir(parents=True, exist_ok=True)
    success = EVIDENCE_ROOT / f"pwa-success-{uuid.uuid4().hex}.json"
    run_success(ROOT, success, "native")

    icon = fixture / "examples/web-pwa-install/wwwroot/icons/app-192.svg"
    failure = EVIDENCE_ROOT / f"pwa-failed-cli-{uuid.uuid4().hex}.json"
    port = free_port()
    with temporarily_removed(icon):
        process = run_command(fixture, port, failure)
        code, stdout, stderr = finish(process)
    assert code == 1, f"Expected public CLI exit 1, got {code}\n{stdout}\n{stderr}"
    assert_report(failure, passed=False, exit_code=1)
    assert_expected_icon_failure(json.loads(failure.read_text(encoding="utf-8")))
    assert_children_stopped(stderr)
    assert_port_reusable(port)


def startup_timeout(fixture: pathlib.Path, scratch: pathlib.Path) -> None:
    program = fixture / "examples/web-pwa-install/Program.cs"
    port = free_port()
    evidence = scratch / f"pwa-timeout-{uuid.uuid4().hex}.json"
    prior = '{"schemaVersion":3,"passed":true,"marker":"previous-run"}\n'
    evidence.write_text(prior, encoding="utf-8")
    delayed = lambda source: source.replace(
        "await WebApp<WebPwaInstallModule>.RunAsync(",
        "await Task.Delay(TimeSpan.FromSeconds(20));\n\nawait WebApp<WebPwaInstallModule>.RunAsync(",
        1,
    )
    with edit_text(program, delayed):
        process = run_command(fixture, port, evidence, startup_timeout=1)
        code, stdout, stderr = finish(process, timeout=30)
    assert code != 0 and "did not prove readiness" in stderr, f"Unexpected startup timeout result\n{stdout}\n{stderr}"
    assert evidence.read_text(encoding="utf-8") == prior
    assert_children_stopped(stderr)
    assert_port_reusable(port)


def native_cancellation(fixture: pathlib.Path, scratch: pathlib.Path, verifying: bool) -> None:
    program = fixture / "examples/web-pwa-install/Program.cs"
    port = free_port()
    evidence = scratch / f"pwa-cancel-{uuid.uuid4().hex}.json"
    prior = '{"schemaVersion":3,"passed":true,"marker":"previous-run"}\n'
    evidence.write_text(prior, encoding="utf-8")
    if verifying:
        marker = "    public void ConfigureEndpointAwareMiddleware(StartupContext context, IApplicationBuilder app)\n    {\n"
        delay = """        app.Use(async (http, next) =>
        {
            if (http.Request.Path != "/manifest.webmanifest"
                && http.Request.Path != "/_appsurface/pwa/status.json"
                && http.Request.Path != "/_appsurface/verify/instance")
            {
                await Task.Delay(TimeSpan.FromSeconds(5), http.RequestAborted);
            }

            await next();
        });
"""
        edit = lambda source: source.replace(marker, marker + delay, 1)
        phase = "PWA verifier running public CLI"
    else:
        edit = lambda source: source.replace(
            "await WebApp<WebPwaInstallModule>.RunAsync(",
            "await Task.Delay(TimeSpan.FromSeconds(20));\n\nawait WebApp<WebPwaInstallModule>.RunAsync(",
            1,
        )
        phase = "PWA verifier starting example"

    with edit_text(program, edit):
        process = run_command(fixture, port, evidence)
        wait_for_phase(process, phase, STARTUP_PHASE_TIMEOUT if not verifying else VERIFY_PHASE_TIMEOUT)
        cancellation_deadline = time.monotonic() + CANCEL_WATCHDOG
        send_ctrl_c(process, timeout=3)
        try:
            remaining = max(0.1, cancellation_deadline - time.monotonic())
            code = process.wait(timeout=remaining)
        except subprocess.TimeoutExpired as error:
            process.kill()
            process.wait(timeout=5)
            raise AssertionError(f"Ctrl+C exceeded 10-second watchdog for {phase}.\n{''.join(process.stderr_lines)}") from error
        stderr = "".join(process.stderr_lines)
    assert code == 130, f"Cancellation exit was {code}, expected 130.\n{stderr}"
    assert evidence.read_text(encoding="utf-8") == prior
    assert_children_stopped(stderr)
    assert_port_reusable(port)


def unrelated_listener_survives(scratch: pathlib.Path) -> None:
    port = free_port()
    manifest = b'{"name":"Foreign listener","start_url":"/","scope":"/","display":"standalone"}'

    class ForeignHandler(http.server.BaseHTTPRequestHandler):
        def do_GET(self):
            if self.path == "/manifest.webmanifest":
                self.send_response(200)
                self.send_header("Content-Type", "application/manifest+json")
                self.end_headers()
                self.wfile.write(manifest)
            elif self.path == "/_appsurface/pwa/status.json":
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.end_headers()
                self.wfile.write(b"{}")
            else:
                self.send_error(404)

        def log_message(self, _format, *_args):
            return

    server = http.server.ThreadingHTTPServer(("127.0.0.1", port), ForeignHandler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    evidence = scratch / f"pwa-foreign-{uuid.uuid4().hex}.json"
    prior = '{"schemaVersion":3,"passed":true,"marker":"previous-run"}\n'
    evidence.write_text(prior, encoding="utf-8")
    try:
        process = run_command(ROOT, port, evidence, startup_timeout=4)
        code, stdout, stderr = finish(process, timeout=30)
        assert code != 0, f"Verifier accepted an unrelated listener.\n{stdout}\n{stderr}"
        assert "No evidence published by this run" in stderr
        assert evidence.read_text(encoding="utf-8") == prior
        with urllib.request.urlopen(f"http://127.0.0.1:{port}/manifest.webmanifest", timeout=3) as response:
            assert response.read().decode("utf-8") == manifest.decode("utf-8")
    finally:
        server.shutdown()
        server.server_close()


def stale_evidence_success_replaced(fixture: pathlib.Path, scratch: pathlib.Path) -> None:
    evidence = scratch / f"pwa-stale-{uuid.uuid4().hex}.json"
    evidence.write_text('{"schemaVersion":3,"passed":false,"marker":"stale"}\n', encoding="utf-8")
    run_success(fixture, evidence, "stale evidence replacement")
    report = assert_report(evidence, passed=True, exit_code=0)
    assert "marker" not in report


def run_process_contracts(fixture: pathlib.Path, scratch: pathlib.Path) -> None:
    native_success_and_failure(fixture)
    stale_evidence_success_replaced(fixture, scratch)
    startup_timeout(fixture, scratch)
    native_cancellation(fixture, scratch, verifying=False)
    native_cancellation(fixture, scratch, verifying=True)
    unrelated_listener_survives(scratch)


def main() -> int:
    global EVIDENCE_ROOT
    EVIDENCE_ROOT = EVIDENCE_ROOT / f"run-{uuid.uuid4().hex}"
    EVIDENCE_ROOT.mkdir(parents=True, exist_ok=True)
    if sys.argv[1:] == ["--cost-samples"]:
        cost_samples()
        print(f"PWA verifier cost samples passed on {sys.platform}")
        return 0

    with tempfile.TemporaryDirectory(prefix="pwa-verifier-process-") as temporary:
        scratch = pathlib.Path(temporary)
        fixture = make_fixture_checkout(scratch / "fixture")
        run_process_contracts(fixture, scratch)
    print(f"PWA verifier process contracts passed on {sys.platform}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
