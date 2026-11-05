using System.Diagnostics;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Executable contract checks for the optional Unix invocation wrapper.</summary>
public sealed class CoverageFanInScriptTests
{
    [Theory]
    [InlineData("accepted", "HEAD~1", 0, 2)]
    [InlineData("rejected", "HEAD~1", 1, 2)]
    [InlineData("accepted", "", 0, 1)]
    [InlineData("invalid", "HEAD~1", 2, 0)]
    public async Task NativeWrapperRetainsCanonicalReportsAndNeverTurnsAdapterRefusalIntoSuccess(
        string outcome, string diffBase, int expectedExitCode, int expectedGateCalls)
    {
        if (OperatingSystem.IsWindows())
            throw Xunit.Sdk.SkipException.ForSkip("The optional Bash wrapper is Unix-only; the installed API/CLI have native Windows tests.");
        using var temp = TestDirectory.Create();
        var root = temp.CreateDirectory("repository");
        var scripts = temp.CreateDirectory("repository/scripts");
        var bin = temp.CreateDirectory("bin");
        var coverage = temp.CreateDirectory("coverage");
        var dogfood = TestPathUtils.PathUnder(temp.Path, "fan-in");
        var gateCalls = TestPathUtils.PathUnder(temp.Path, "gate-calls.txt");
        var repo = GitRepositoryRootResolver.FindRepositoryRoot(AppContext.BaseDirectory);
        File.Copy(Path.Join(repo, "scripts", "coverage-gate-fanin.sh"), Path.Join(scripts, "coverage-gate-fanin.sh"));
        // The adapter boundary is simulated here; the real installed workflow is covered by
        // the separate package proof. This fixture exercises the actual shell orchestration.
        var adapter = temp.WriteFile("bin/dotnet", """
            #!/usr/bin/env bash
            set -euo pipefail
            if [[ "$FIXTURE_OUTCOME" == invalid ]]; then exit 2; fi
            mkdir -p "$COVERAGE_DOGFOOD_OUTPUT/accepted" "$COVERAGE_DOGFOOD_OUTPUT/composed"
            printf '<coverage />' > "$COVERAGE_DOGFOOD_OUTPUT/composed/coverage.cobertura.xml"
            printf '<coverage />' > "$COVERAGE_DOGFOOD_OUTPUT/composed/primary-paths.cobertura.xml"
            if [[ "$FIXTURE_OUTCOME" == rejected ]]; then
              printf '{}' > "$COVERAGE_DOGFOOD_OUTPUT/accepted/rejection.json"
              exit 1
            fi
            printf '{}' > "$COVERAGE_DOGFOOD_OUTPUT/accepted/acceptance.json"
            """);
        var cli = temp.WriteFile("bin/appsurface", """
            #!/usr/bin/env bash
            set -euo pipefail
            [[ "$1" == coverage && "$2" == gate ]]
            shift 2
            input= output= patch=false
            while [[ "$#" -gt 0 ]]; do
              case "$1" in
                --coverage) input="$2" ;;
                --output) output="$2" ;;
                --min-patch-line) [[ "$2" == 95 ]]; patch=true ;;
                --min-patch-branch) [[ "$2" == 85 ]] ;;
                --min-line) [[ "$2" == 95 ]] ;;
                --min-branch) [[ "$2" == 85 ]] ;;
                --patch-line-mode) [[ "$2" == codecov ]] ;;
                --diff-base) [[ "$2" == HEAD~1 ]] ;;
                *) exit 91 ;;
              esac
              shift 2
            done
            [[ -f "$input" && "$input" != "$output/coverage.cobertura.xml" ]]
            printf '%s\n%s\n' "$input" "$output" >> "$FIXTURE_GATE_CALLS"
            mkdir -p "$output"
            passed=true
            if [[ "$patch" == true && "$FIXTURE_OUTCOME" == rejected ]]; then passed=false; fi
            printf '{"passed":%s}' "$passed" > "$output/coverage-gate.json"
            printf '# Gate: %s\n' "$passed" > "$output/coverage-gate.md"
            if [[ "$patch" == true ]]; then
              printf '{}' > "$output/coverage-patch-targets.json"
              printf '# Targets\n' > "$output/coverage-patch-targets.md"
            fi
            [[ "$passed" == true ]]
            """);
        File.SetUnixFileMode(adapter, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(cli, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var start = new ProcessStartInfo("bash")
        { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(Path.Join(scripts, "coverage-gate-fanin.sh"));
        start.Environment["PATH"] = bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        start.Environment["COVERAGE_CLI_PATH"] = cli;
        start.Environment["COVERAGE_DOGFOOD_OUTPUT"] = dogfood;
        start.Environment["COVERAGE_DIRECTORY"] = coverage;
        start.Environment["COVERAGE_FANIN_NATIVE"] = TestPathUtils.PathUnder(temp.Path, "native");
        start.Environment["COVERAGE_GATE_DIFF_BASE"] = diffBase;
        start.Environment["COVERAGE_RUN_SCOPE"] = "fixture";
        start.Environment["COVERAGE_RUN_ID"] = "current-run";
        start.Environment["COVERAGE_RUN_ATTEMPT"] = "1";
        start.Environment["FIXTURE_OUTCOME"] = outcome;
        start.Environment["FIXTURE_GATE_CALLS"] = gateCalls;
        using var process = Process.Start(start)!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
        try { await process.WaitForExitAsync(deadline.Token); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        Assert.True(process.ExitCode == expectedExitCode, (await stdout) + (await stderr));
        var calls = File.Exists(gateCalls) ? File.ReadAllLines(gateCalls) : [];
        Assert.Equal(expectedGateCalls * 2, calls.Length);
        Assert.Equal(expectedGateCalls > 0, File.Exists(Path.Join(coverage, "aggregate-gate", "coverage-gate.json")));
        Assert.Equal(expectedGateCalls == 2, File.Exists(Path.Join(coverage, "coverage-gate.json")));
        if (expectedGateCalls == 2)
        {
            Assert.Equal(Path.Join(dogfood, "composed", "coverage.cobertura.xml"), calls[2]);
            Assert.Equal(coverage, calls[3]);
            Assert.True(File.Exists(Path.Join(coverage, "coverage-gate.md")));
            Assert.True(File.Exists(Path.Join(coverage, "coverage-patch-targets.json")));
            Assert.True(File.Exists(Path.Join(coverage, "coverage-patch-targets.md")));
            using var report = JsonDocument.Parse(File.ReadAllBytes(Path.Join(coverage, "coverage-gate.json")));
            Assert.Equal(outcome == "accepted", report.RootElement.GetProperty("passed").GetBoolean());
        }
    }
}
