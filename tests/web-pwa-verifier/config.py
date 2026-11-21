"""Exercise the documented file-app command in an isolated checkout and caller directory."""

import json
import os
from pathlib import Path
import shutil
import socket
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[2]
DOTNET = os.environ.get("DOTNET_EXE", "dotnet")


def free_port():
    with socket.socket() as listener:
        listener.bind(("127.0.0.1", 0))
        return listener.getsockname()[1]


def source_checkout(destination):
    # Copy versioned inputs only, including staged new files when running locally.
    files = subprocess.check_output(
        ["git", "ls-files", "--cached", "--others", "--exclude-standard", "-z"], cwd=ROOT
    ).split(b"\0")
    for entry in filter(None, files):
        source = ROOT / os.fsdecode(entry)
        if source.is_file():
            target = destination / source.relative_to(ROOT)
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(source, target)


def main():
    with tempfile.TemporaryDirectory(prefix="pwa-config-") as temporary:
        checkout = Path(temporary).resolve() / "src"
        source_checkout(checkout)
        caller = Path(temporary) / "caller with spaces"
        caller.mkdir()
        shutil.copyfile(ROOT / "global.json", caller / "global.json")
        script = checkout / "scripts/web-pwa/verify.cs"
        production = script.with_name("appsettings.json")
        development = script.with_name("appsettings.Development.json")
        decoy = {"PwaVerify": {"Port": 0, "StartupTimeoutSeconds": 0}}
        (caller / "appsettings.json").write_text(json.dumps(decoy), encoding="utf-8")
        env = {k: v for k, v in os.environ.items() if k.upper() not in {
            "ASPNETCORE_ENVIRONMENT", "DOTNET_ENVIRONMENT", "APP_SURFACE_WEB_PWA_PORT",
            "APP_SURFACE_WEB_PWA_EVIDENCE", "PWAVERIFY__STARTUPTIMEOUTSECONDS",
        }}

        def run(*args, environment=None):
            return subprocess.run(
                [DOTNET, "run", "--file", str(script), "--", *args], cwd=caller,
                env={**env, **(environment or {})}, capture_output=True, text=True, timeout=300,
            )

        def success(path, port, *args, environment=None):
            assert not path.exists(), path
            result = run(*args, environment=environment)
            assert result.returncode == 0, result.stdout + result.stderr
            report = json.loads(path.read_text(encoding="utf-8"))
            assert (report["origin"], report["schemaVersion"], report["surface"], report["passed"]) == (
                f"http://127.0.0.1:{port}", 3, "all", True
            ), report

        output = checkout / "examples/web-pwa-install/pwa-verify-v3.json"
        success(output, 5055)
        output.unlink()
        port = free_port()
        production.write_text(json.dumps(decoy), encoding="utf-8")
        development.write_text(json.dumps({"PwaVerify": {"Port": port, "EvidencePath": "json proof.json"}}), encoding="utf-8")
        dev = {"DOTNET_ENVIRONMENT": "Development"}
        success(caller / "json proof.json", port, environment=dev)

        port = free_port()
        mapped = {**dev, "APP_SURFACE_WEB_PWA_PORT": str(port), "APP_SURFACE_WEB_PWA_EVIDENCE": "env proof.json"}
        success(caller / "env proof.json", port, environment=mapped)
        success(caller / "explicit proof.json", 5055, "--port", "5055", "--evidence-path", "explicit proof.json", environment=mapped)

        # Each failure has valid lower-priority values or defaults and a known previous report.
        prior = caller / "previous.json"
        prior.write_bytes(b"previous report")
        production.write_text('{"PwaVerify":{"Port":5055}}', encoding="utf-8")
        for args, overrides in [
            (["--port", "0"], {}), (["--port", "65536"], {}), (["--port", "bogus"], {}),
            (["--startup-timeout-seconds", "0"], {}), ([], {"APP_SURFACE_WEB_PWA_PORT": "bad-port"}),
            ([], {"PWAVERIFY__STARTUPTIMEOUTSECONDS": "0"}), (["--evidence-path", ""], {}),
            (["--evidence-path", str(caller)], {}),
        ]:
            result = run(*args, environment={"APP_SURFACE_WEB_PWA_EVIDENCE": prior.name, **overrides})
            assert result.returncode != 0, (args, result.stdout, result.stderr)
            assert "PWA verifier starting example" not in result.stderr
            assert prior.read_bytes() == b"previous report"

    print("File-app configuration contracts passed.")


if __name__ == "__main__":
    main()
