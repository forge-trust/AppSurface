"""Package-consumer smoke checks for the file-based Console and Config sample.

Run: python3 tests/web-pwa-verifier/package.py
"""

from __future__ import annotations

import json
import os
import shutil
import subprocess
import tempfile
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
EXAMPLE = ROOT / "examples/file-based-console"
SYNTHETIC_SECRET = "synthetic-file-based-proof-only"
SECRET_RESOLUTION_MARKER = "Synthetic typed secret matched in memory."
HELP_SECRET_FAILURE_MARKER = "HELP_RESOLVED_SECRET_PROVIDER"
TIMEOUT_SECONDS = 180


def dotnet_executable() -> str:
    configured = os.environ.get("DOTNET_EXE")
    if configured:
        return configured
    available = shutil.which("dotnet")
    if available:
        return available
    raise AssertionError("Set DOTNET_EXE or install the pinned .NET SDK.")


def run_command(
    dotnet: str,
    program: Path,
    artifacts: Path,
    working_directory: Path,
    arguments: list[str],
    extra_environment: dict[str, str] | None = None,
) -> subprocess.CompletedProcess[str]:
    environment = os.environ.copy()
    environment.update(
        {
            "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
            "DOTNET_NOLOGO": "1",
            "DOTNET_SKIP_FIRST_TIME_EXPERIENCE": "1",
            "NUGET_PACKAGES": str(working_directory.parent / "nuget" / "packages"),
            "NUGET_HTTP_CACHE_PATH": str(working_directory.parent / "nuget" / "http"),
            "NUGET_PLUGINS_CACHE_PATH": str(working_directory.parent / "nuget" / "plugins"),
            "NUGET_SCRATCH": str(working_directory.parent / "nuget" / "scratch"),
        }
    )
    for name in tuple(environment):
        if name.upper().startswith("DEMO__"):
            environment.pop(name)
    environment.update(extra_environment or {})

    return subprocess.run(
        [
            dotnet,
            "run",
            "--file",
            str(program),
            "--artifacts-path",
            str(artifacts),
            "--",
            *arguments,
        ],
        cwd=working_directory,
        env=environment,
        capture_output=True,
        text=True,
        timeout=TIMEOUT_SECONDS,
        check=False,
    )


def output_text(result: subprocess.CompletedProcess[str]) -> str:
    return f"{result.stdout}\n{result.stderr}"


def safe_output(result: subprocess.CompletedProcess[str]) -> str:
    return output_text(result).replace(SYNTHETIC_SECRET, "[redacted synthetic secret]")


def assert_secret_redacted(result: subprocess.CompletedProcess[str], description: str) -> None:
    assert SYNTHETIC_SECRET not in output_text(result), (
        f"{description} printed the synthetic secret:\n{safe_output(result)}"
    )


def assert_success(
    result: subprocess.CompletedProcess[str],
    description: str,
    expected_output: str,
) -> None:
    assert result.returncode == 0, (
        f"{description} exited {result.returncode}:\n{safe_output(result)}"
    )
    output = output_text(result)
    assert expected_output in output, f"{description} missed {expected_output!r}:\n{safe_output(result)}"
    assert_secret_redacted(result, description)


def main() -> int:
    dotnet = dotnet_executable()
    global_json_source = ROOT / "global.json"
    pinned_sdk_version = json.loads(global_json_source.read_text(encoding="utf-8"))["sdk"]["version"]
    with tempfile.TemporaryDirectory(prefix="pwa-file-package-consumer-") as temporary:
        consumer = Path(temporary).resolve()
        (consumer / "NuGet.Config").write_text(
            '<configuration><packageSources><clear/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>',
            encoding="utf-8",
        )
        program = consumer / "Program.cs"
        program_source = (EXAMPLE / "Program.cs").read_text(encoding="utf-8")
        resolve_method = (
            "    public ConfigSecretProviderResolution Resolve(ConfigSecretReference reference, ConfigSecretResolutionContext context) =>\n"
            "        ConfigSecretProviderResolution.Resolved(SyntheticCredential.Value, ConfigSecretSourceMetadata.Create(Id));"
        )
        throwing_resolve_method = (
            "    public ConfigSecretProviderResolution Resolve(ConfigSecretReference reference, ConfigSecretResolutionContext context) =>\n"
            "        throw new InvalidOperationException(\"HELP_RESOLVED_SECRET_PROVIDER\");"
        )
        assert program_source.count(resolve_method) == 1, (
            "Could not instrument the copied synthetic secret provider for the help check."
        )
        program.write_text(program_source, encoding="utf-8")
        shutil.copyfile(EXAMPLE / "appsettings.json", consumer / "appsettings.json")
        shutil.copyfile(global_json_source, consumer / "global.json")
        help_directory = consumer / "help"
        help_directory.mkdir()
        help_program = help_directory / "Program.cs"
        help_program.write_text(
            program_source.replace(resolve_method, throwing_resolve_method, 1),
            encoding="utf-8",
        )
        shutil.copyfile(EXAMPLE / "appsettings.json", help_directory / "appsettings.json")
        caller = consumer / "caller"
        caller.mkdir()
        (caller / "appsettings.json").write_text(
            '{"Demo":{"Port":4444,"Region":"caller-cwd","Endpoint":"https://caller.example.test",'
            '"EvidencePath":"caller.json","Credentials":{"ApiKey":{"key":"synthetic","version":"1",'
            '"provider":"file-based-sample"}}}}',
            encoding="utf-8",
        )
        artifacts = consumer / ".artifacts"

        sdk = subprocess.run(
            [dotnet, "--version"],
            cwd=caller,
            capture_output=True,
            text=True,
            timeout=30,
            check=False,
        )
        assert sdk.returncode == 0, f"Could not query the .NET SDK:\n{sdk.stderr}"
        assert sdk.stdout.strip() == pinned_sdk_version, (
            f"Expected .NET SDK {pinned_sdk_version}, got {sdk.stdout.strip()!r}."
        )

        help_result = run_command(
            dotnet,
            help_program,
            consumer / ".artifacts-help",
            caller,
            ["--help"],
        )
        assert_success(help_result, "help", "inspect")
        assert HELP_SECRET_FAILURE_MARKER not in output_text(help_result), (
            "Help invoked the secret provider."
        )

        default_result = run_command(dotnet, program, artifacts, caller, ["inspect"])
        assert_success(
            default_result,
            "default settings",
            "Effective port: 5454. Effective region: us-east-1. "
            f"{SECRET_RESOLUTION_MARKER}",
        )

        explicit_result = run_command(
            dotnet,
            program,
            artifacts,
            caller,
            ["inspect", "--port", "5055", "--region", "ap-southeast-2"],
        )
        assert_success(
            explicit_result,
            "explicit arguments",
            "Effective port: 5055. Effective region: ap-southeast-2. "
            f"{SECRET_RESOLUTION_MARKER}",
        )

        malformed_settings = consumer / "malformed-settings"
        malformed_settings.mkdir()
        shutil.copyfile(program, malformed_settings / "Program.cs")
        (malformed_settings / "appsettings.json").write_text(
            '{"Demo":{"Port":"invalid-port","Region":"us-east-1","Endpoint":"https://api.example.test",'
            '"EvidencePath":"proof.json","Credentials":{"ApiKey":{"key":"synthetic","version":"1",'
            '"provider":"file-based-sample"}}}}',
            encoding="utf-8",
        )
        shutil.copyfile(ROOT / "global.json", malformed_settings / "global.json")
        malformed_settings_caller = malformed_settings / "caller"
        malformed_settings_caller.mkdir()
        malformed_json_result = run_command(
            dotnet,
            malformed_settings / "Program.cs",
            malformed_settings / ".artifacts",
            malformed_settings_caller,
            ["inspect", "--port", "5056"],
        )
        assert_success(
            malformed_json_result,
            "explicit port over malformed JSON value",
            "Effective port: 5056. Effective region: us-east-1. "
            f"{SECRET_RESOLUTION_MARKER}",
        )

        malformed_environment_result = run_command(
            dotnet,
            program,
            artifacts,
            caller,
            ["inspect", "--port", "5057"],
            {"DEMO__PORT": "not-a-port"},
        )
        assert_success(
            malformed_environment_result,
            "explicit port over malformed environment value",
            "Effective port: 5057. Effective region: us-east-1. "
            f"{SECRET_RESOLUTION_MARKER}",
        )

        malformed_default_result = run_command(
            dotnet,
            malformed_settings / "Program.cs",
            malformed_settings / ".artifacts",
            malformed_settings_caller,
            ["inspect"],
        )
        assert malformed_default_result.returncode != 0, (
            "Malformed configured port unexpectedly succeeded without an explicit switch:\n"
            f"{safe_output(malformed_default_result)}"
        )
        assert_secret_redacted(malformed_default_result, "malformed configured port")

        invalid_port_result = run_command(
            dotnet,
            program,
            artifacts,
            caller,
            ["inspect", "--port", "70000"],
        )
        assert invalid_port_result.returncode != 0, (
            "Out-of-range port unexpectedly succeeded:\n"
            f"{safe_output(invalid_port_result)}"
        )
        assert "between 1 and 65535" in output_text(invalid_port_result), (
            "Out-of-range port did not report range validation:\n"
            f"{safe_output(invalid_port_result)}"
        )
        assert SECRET_RESOLUTION_MARKER not in output_text(invalid_port_result), (
            "Invalid settings reached the successful command output."
        )
        assert_secret_redacted(invalid_port_result, "invalid port")

    print(f"File-based package-consumer smoke checks passed with .NET SDK {pinned_sdk_version}.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
