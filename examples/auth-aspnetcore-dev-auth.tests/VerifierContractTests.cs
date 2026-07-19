using System.Diagnostics;
using System.Globalization;

namespace AuthAspNetCoreDevAuthExample.Tests;

public sealed class VerifierContractTests
{
    [Fact]
    public async Task MissingCommand_FailsDuringPreflightBeforeBuild()
    {
        await using var fixture = await VerifierFixture.CreateAsync("success");
        fixture.RemoveDotnetShim();

        var result = await fixture.RunAsync();

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("[stage=PREFLIGHT reason=MISSING_COMMAND]", result.CombinedOutput, StringComparison.Ordinal);
        Assert.Contains("dotnet", result.CombinedOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("build", fixture.ReadEvents(), StringComparison.Ordinal);
        Assert.Empty(fixture.ChildProcessIds);
        fixture.AssertFailureEvidenceIsSafe();
    }

    [Theory]
    [InlineData("0", null, "INVALID_PORT")]
    [InlineData("65536", null, "INVALID_PORT")]
    [InlineData("18446744073709551617", null, "INVALID_PORT")]
    [InlineData("not-a-port", null, "INVALID_PORT")]
    [InlineData("61258\nPORT_TOKEN_SENTINEL", null, "INVALID_PORT")]
    [InlineData(null, "0", "INVALID_TIMEOUT")]
    [InlineData(null, "301", "INVALID_TIMEOUT")]
    [InlineData(null, "18446744073709551617", "INVALID_TIMEOUT")]
    [InlineData(null, "1.5", "INVALID_TIMEOUT")]
    public async Task InvalidInput_FailsDuringPreflightBeforeBuild(
        string? port,
        string? timeout,
        string reason)
    {
        await using var fixture = await VerifierFixture.CreateAsync("success");

        var result = await fixture.RunAsync(port: port, timeout: timeout);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains($"[stage=PREFLIGHT reason={reason}]", result.CombinedOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("build", fixture.ReadEvents(), StringComparison.Ordinal);
        Assert.Empty(fixture.ChildProcessIds);
        fixture.AssertFailureEvidenceIsSafe();
        Assert.DoesNotContain("PORT_TOKEN_SENTINEL", result.CombinedOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("PORT_TOKEN_SENTINEL", fixture.ReadStageSummaryEvidence(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildFailure_StopsBeforeLaunchAndReadiness()
    {
        await using var fixture = await VerifierFixture.CreateAsync("build-failure");

        var result = await fixture.RunAsync();

        Assert.Equal(3, result.ExitCode);
        Assert.Contains("[stage=BUILD]", result.CombinedOutput, StringComparison.Ordinal);
        Assert.Contains("[stage=BUILD reason=BUILD_FAILED]", result.CombinedOutput, StringComparison.Ordinal);
        Assert.Contains("compiler fixture failed", result.CombinedOutput, StringComparison.Ordinal);
        Assert.Equal(["build"], fixture.ReadEventLines());
        Assert.Empty(fixture.ChildProcessIds);
        fixture.AssertFailureEvidenceIsSafe();
    }

    [Theory]
    [InlineData("early-exit", 23, "[REDACTED]")]
    [InlineData("occupied-port", 98, "address already in use")]
    public async Task ChildExit_IsReportedImmediatelyWithItsStatus(
        string mode,
        int childExitCode,
        string safeDiagnostic)
    {
        await using var fixture = await VerifierFixture.CreateAsync(mode);

        var result = await fixture.RunAsync();

        Assert.Equal(3, result.ExitCode);
        Assert.Contains("reason=CHILD_EXITED", result.CombinedOutput, StringComparison.Ordinal);
        Assert.Contains(childExitCode.ToString(CultureInfo.InvariantCulture), result.CombinedOutput, StringComparison.Ordinal);
        Assert.Contains(safeDiagnostic, fixture.ReadApplicationLogEvidence(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("curl ", fixture.ReadEvents(), StringComparison.Ordinal);
        fixture.AssertNoChildrenRemain();
        fixture.AssertFailureEvidenceIsSafe();
    }

    [Fact]
    public async Task ForeignEndpointCannotSatisfyReadinessWithoutChildOwnedListenEvidence()
    {
        await using var fixture = await VerifierFixture.CreateAsync("foreign-endpoint");

        var result = await fixture.RunAsync(timeout: "1");

        Assert.Equal(4, result.ExitCode);
        Assert.Contains("[stage=READINESS reason=READINESS_TIMEOUT]", result.CombinedOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("curl ", fixture.ReadEvents(), StringComparison.Ordinal);
        fixture.AssertNoChildrenRemain();
        fixture.AssertFailureEvidenceIsSafe();
    }

    [Fact]
    public async Task ReadinessTimeout_IsDistinctAndLeavesNoChild()
    {
        await using var fixture = await VerifierFixture.CreateAsync("timeout");

        var result = await fixture.RunAsync(timeout: "1");

        Assert.Equal(4, result.ExitCode);
        Assert.Contains("[stage=READINESS]", result.CombinedOutput, StringComparison.Ordinal);
        Assert.Contains("[stage=READINESS reason=READINESS_TIMEOUT]", result.CombinedOutput, StringComparison.Ordinal);
        Assert.Contains("has not reported its configured address", result.CombinedOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("curl ", fixture.ReadEvents(), StringComparison.Ordinal);
        fixture.AssertNoChildrenRemain();
    }

    [Theory]
    [InlineData("http-transport-failure", "HTTP_TRANSPORT_FAILED")]
    [InlineData("http-timeout", "HTTP_TRANSPORT_FAILED")]
    [InlineData("http-contract-failure", "HTTP_CONTRACT_FAILED")]
    public async Task HttpFailure_IsClassifiedAfterChildOwnedListenEvidence(string mode, string reason)
    {
        await using var fixture = await VerifierFixture.CreateAsync(mode);

        var result = await fixture.RunAsync();

        Assert.Equal(5, result.ExitCode);
        Assert.Contains($"[stage=HTTP_PROOF reason={reason}]", result.CombinedOutput, StringComparison.Ordinal);
        fixture.AssertFirstCurlFollowsListenEvidence();
        fixture.AssertNoChildrenRemain();
        fixture.AssertFailureEvidenceIsSafe();
    }

    [Fact]
    public async Task RedirectToAnotherLoopbackPort_IsNotFollowedAndReceivesNoPersonaCookie()
    {
        await using var fixture = await VerifierFixture.CreateAsync("cross-port-redirect");

        var result = await fixture.RunAsync();

        Assert.Equal(5, result.ExitCode);
        Assert.Contains("[stage=HTTP_PROOF reason=HTTP_CONTRACT_FAILED]", result.CombinedOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("follow-redirects", fixture.ReadEvents(), StringComparison.Ordinal);
        Assert.DoesNotContain("redirect-cookie-leak", fixture.ReadEvents(), StringComparison.Ordinal);
        fixture.AssertNoChildrenRemain();
        fixture.AssertFailureEvidenceIsSafe();
    }

    [Theory]
    [InlineData("61258")]
    [InlineData("61259")]
    [InlineData("061258")]
    public async Task Success_ExercisesTheWorkflowWithChildScopedHostSettingsAndCleansEverything(string port)
    {
        await using var fixture = await VerifierFixture.CreateAsync("success");

        var result = await fixture.RunAsync(port: port);
        var normalizedPort = int.Parse(port, NumberStyles.None, CultureInfo.InvariantCulture)
            .ToString(CultureInfo.InvariantCulture);

        Assert.True(result.ExitCode == 0, result.CombinedOutput);
        Assert.Contains("[stage=COMPLETE reason=PASSED]", result.CombinedOutput, StringComparison.Ordinal);
        Assert.Contains($"url=http://127.0.0.1:{normalizedPort}", result.CombinedOutput, StringComparison.Ordinal);
        Assert.Contains("environment=Development reload=false", fixture.ReadEvents(), StringComparison.Ordinal);
        Assert.Contains("cookie-source=jar", fixture.ReadEvents(), StringComparison.Ordinal);
        Assert.DoesNotContain("cookie-source=header", fixture.ReadEvents(), StringComparison.Ordinal);
        Assert.Contains("curl-config=disabled", fixture.ReadEvents(), StringComparison.Ordinal);
        Assert.Contains("redirects=disabled", fixture.ReadEvents(), StringComparison.Ordinal);
        Assert.Contains("proxy=disabled", fixture.ReadEvents(), StringComparison.Ordinal);
        Assert.Contains("timeouts=connect:5,total:15", fixture.ReadEvents(), StringComparison.Ordinal);
        fixture.AssertFirstCurlFollowsListenEvidence();
        Assert.Contains("/_appsurface/dev-auth/status", fixture.ReadEvents(), StringComparison.Ordinal);
        Assert.Contains("/_appsurface/dev-auth/select/viewer", fixture.ReadEvents(), StringComparison.Ordinal);
        Assert.Contains("/api/auth-proof", fixture.ReadEvents(), StringComparison.Ordinal);
        fixture.AssertNoChildrenRemain();
        Assert.Empty(fixture.FindEvidenceDirectories());
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.TemporaryDirectory));
    }

    [Fact]
    public async Task LeadingZeroTimeout_UsesCanonicalDecimalDeadline()
    {
        await using var fixture = await VerifierFixture.CreateAsync("timeout");

        var result = await fixture.RunAsync(timeout: "008", processTimeout: TimeSpan.FromSeconds(15));

        Assert.Equal(4, result.ExitCode);
        Assert.Contains("[stage=READINESS reason=READINESS_TIMEOUT]", result.CombinedOutput, StringComparison.Ordinal);
        Assert.Contains("timeout=8s", result.CombinedOutput, StringComparison.Ordinal);
        fixture.AssertNoChildrenRemain();
        fixture.AssertFailureEvidenceIsSafe();
    }

    [Fact]
    public async Task FailureEvidence_HasExactAllowlistCapsAndRedactsSensitiveSentinels()
    {
        await using var fixture = await VerifierFixture.CreateAsync("sensitive-http-failure");

        var result = await fixture.RunAsync();

        Assert.Equal(5, result.ExitCode);
        fixture.AssertFailureEvidenceIsSafe();

        var log = fixture.ReadApplicationLogEvidence();
        Assert.True(log.Split('\n').Length <= 81, "The retained log must contain no more than 80 lines plus a possible terminal split.");
        Assert.True(new FileInfo(fixture.ApplicationLogEvidencePath).Length <= 32 * 1024);
        Assert.True(new FileInfo(fixture.ApplicationLogEvidencePath).Length <= 4 * 1024, "Current canonical evidence should remain well below the defense-in-depth byte cap.");
        Assert.NotEqual(141, result.ExitCode);
        Assert.DoesNotContain("COOKIE_SENTINEL", log, StringComparison.Ordinal);
        Assert.DoesNotContain("TOKEN_SENTINEL", log, StringComparison.Ordinal);
        Assert.DoesNotContain("person@example.com", log, StringComparison.Ordinal);
        Assert.DoesNotContain("admin-1", log, StringComparison.Ordinal);
        Assert.DoesNotContain("RESPONSE_SENTINEL", log, StringComparison.Ordinal);
        Assert.DoesNotContain("UNKNOWN_SECRET_SENTINEL", log, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", log, StringComparison.Ordinal);
        Assert.DoesNotContain("COOKIE_SENTINEL", result.CombinedOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("TOKEN_SENTINEL", result.CombinedOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("UNKNOWN_SECRET_SENTINEL", result.CombinedOutput, StringComparison.Ordinal);
        fixture.AssertNoChildrenRemain();
    }

    [Theory]
    [InlineData("INT", 130, "INTERRUPTED_INT")]
    [InlineData("TERM", 143, "INTERRUPTED_TERM")]
    public async Task Signal_UsesFixedExitStatusAndReapsTheChild(
        string signal,
        int expectedExitCode,
        string expectedReason)
    {
        await using var fixture = await VerifierFixture.CreateAsync("timeout");

        var result = await fixture.RunAndSignalAsync(signal);

        Assert.Equal(expectedExitCode, result.ExitCode);
        fixture.AssertStageSummaryReason(expectedReason);
        fixture.AssertNoChildrenRemain();
        fixture.AssertFailureEvidenceIsSafe();
    }

    [Theory]
    [InlineData("INT", 130, "INTERRUPTED_INT")]
    [InlineData("TERM", 143, "INTERRUPTED_TERM")]
    public async Task RepeatedSignal_IsMaskedUntilTheOwnedChildIsReaped(
        string signal,
        int expectedExitCode,
        string expectedReason)
    {
        await using var fixture = await VerifierFixture.CreateAsync("resistant-child");

        var result = await fixture.RunAndSignalTwiceAsync(signal, TimeSpan.FromSeconds(12));

        Assert.Equal(expectedExitCode, result.ExitCode);
        Assert.Contains("[stage=CLEANUP reason=TERM_ESCALATED_TO_KILL]", result.CombinedOutput, StringComparison.Ordinal);
        fixture.AssertStageSummaryReason(expectedReason);
        fixture.AssertNoChildrenRemain();
        fixture.AssertFailureEvidenceIsSafe();
    }

    [Fact]
    public async Task TermSignal_EscalatesToKillForResistantChildAndReapsIt()
    {
        await using var fixture = await VerifierFixture.CreateAsync("resistant-child");

        var result = await fixture.RunAndSignalAsync("TERM", TimeSpan.FromSeconds(12));

        Assert.Equal(143, result.ExitCode);
        Assert.Contains("[stage=CLEANUP reason=TERM_ESCALATED_TO_KILL]", result.CombinedOutput, StringComparison.Ordinal);
        fixture.AssertStageSummaryReason("INTERRUPTED_TERM");
        fixture.AssertNoChildrenRemain();
        fixture.AssertFailureEvidenceIsSafe();
    }

    [Fact]
    public async Task ProductionSignalPrimitive_IgnoresBashEnvironmentFunctionOverride()
    {
        await using var fixture = await VerifierFixture.CreateAsync("production-builtin-immune");

        var result = await fixture.RunAsync();

        Assert.True(result.ExitCode == 0, result.CombinedOutput);
        Assert.DoesNotContain("reason=RETRYING_REAP", result.CombinedOutput, StringComparison.Ordinal);
        fixture.AssertNoChildrenRemain();
        Assert.Empty(fixture.FindEvidenceDirectories());
    }

    [Fact]
    public async Task ChildExitImmediatelyBeforeCleanup_IsReapedWithoutChangingSuccess()
    {
        await using var fixture = await VerifierFixture.CreateAsync("exit-before-cleanup");

        var result = await fixture.RunAsync();

        Assert.True(result.ExitCode == 0, result.CombinedOutput);
        Assert.Contains("[stage=COMPLETE reason=PASSED]", result.CombinedOutput, StringComparison.Ordinal);
        Assert.Contains("[stage=CLEANUP reason=CHILD_ALREADY_EXITED]", result.CombinedOutput, StringComparison.Ordinal);
        fixture.AssertNoChildrenRemain();
        Assert.Empty(fixture.FindEvidenceDirectories());
    }

    private sealed class VerifierFixture : IAsyncDisposable
    {
        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

        private VerifierFixture(string root, string mode)
        {
            Root = root;
            Mode = mode;
            ScriptPath = Path.Combine(root, "examples", "auth-aspnetcore-dev-auth", "verify.sh");
            ShimDirectory = Path.Combine(root, "shims");
            TemporaryDirectory = Path.Combine(root, "tmp");
            EventsPath = Path.Combine(root, "events.log");
            BashEnvironmentPath = Path.Combine(root, "bash-env.sh");
        }

        private string Root { get; }

        private string Mode { get; }

        private string ScriptPath { get; }

        private string ShimDirectory { get; }

        internal string TemporaryDirectory { get; }

        private string EventsPath { get; }

        private string BashEnvironmentPath { get; }

        internal IReadOnlyList<int> ChildProcessIds => ReadEventLines()
            .Where(line => line.StartsWith("child-pid ", StringComparison.Ordinal))
            .Select(line => int.Parse(line.AsSpan("child-pid ".Length), CultureInfo.InvariantCulture))
            .Distinct()
            .ToArray();

        internal string ApplicationLogEvidencePath => Path.Combine(
            Assert.Single(FindEvidenceDirectories()),
            "application-log-tail.txt");

        internal static async Task<VerifierFixture> CreateAsync(string mode)
        {
            var root = Path.Combine(Path.GetTempPath(), $"appsurface-verifier-contract-{Guid.NewGuid():N}");
            var fixture = new VerifierFixture(root, mode);
            Directory.CreateDirectory(Path.GetDirectoryName(fixture.ScriptPath)!);
            Directory.CreateDirectory(fixture.ShimDirectory);
            Directory.CreateDirectory(fixture.TemporaryDirectory);

            var productionScript = FindRepositoryFile("examples", "auth-aspnetcore-dev-auth", "verify.sh");
            File.Copy(productionScript, fixture.ScriptPath);
            await File.WriteAllTextAsync(
                Path.Combine(Path.GetDirectoryName(fixture.ScriptPath)!, "AuthAspNetCoreDevAuthExample.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />\n");

            var dllDirectory = Path.Combine(
                Path.GetDirectoryName(fixture.ScriptPath)!,
                "bin",
                "Release",
                "net10.0");
            Directory.CreateDirectory(dllDirectory);
            await File.WriteAllTextAsync(Path.Combine(dllDirectory, "AuthAspNetCoreDevAuthExample.dll"), string.Empty);

            await fixture.WriteExecutableAsync("dotnet", DotnetShim);
            await fixture.WriteExecutableAsync("curl", CurlShim);
            await File.WriteAllTextAsync(fixture.BashEnvironmentPath, BashEnvironmentFixture);
            var curlHome = Path.Combine(root, "curl-home");
            Directory.CreateDirectory(curlHome);
            await File.WriteAllTextAsync(Path.Combine(curlHome, ".curlrc"), "location\nproxy = http://127.0.0.1:1\n");
            foreach (var command in new[] { "awk", "bash", "cat", "cut", "date", "dirname", "grep", "head", "mktemp", "rm", "sed", "sleep", "tail", "tr" })
            {
                fixture.LinkSystemCommand(command);
            }

            return fixture;
        }

        internal void RemoveDotnetShim()
        {
            File.Delete(Path.Combine(ShimDirectory, "dotnet"));
        }

        internal Task<VerifierResult> RunAsync(
            string? port = null,
            string? timeout = "2",
            TimeSpan? processTimeout = null)
        {
            return RunCoreAsync(port, timeout, signal: null, processTimeout ?? DefaultTimeout);
        }

        internal Task<VerifierResult> RunAndSignalAsync(string signal, TimeSpan? processTimeout = null)
        {
            return RunCoreAsync(port: null, timeout: "30", signal, processTimeout ?? DefaultTimeout);
        }

        internal async Task<VerifierResult> RunAndSignalTwiceAsync(string signal, TimeSpan processTimeout)
        {
            return await RunCoreAsync(
                port: null,
                timeout: "30",
                signal: signal,
                processTimeout: processTimeout,
                sendSecondSignal: true);
        }

        internal string ReadEvents()
        {
            return File.Exists(EventsPath) ? File.ReadAllText(EventsPath) : string.Empty;
        }

        internal string[] ReadEventLines()
        {
            return File.Exists(EventsPath)
                ? File.ReadAllLines(EventsPath).Where(line => line.Length > 0).ToArray()
                : [];
        }

        internal string[] FindEvidenceDirectories()
        {
            return Directory.Exists(TemporaryDirectory)
                ? Directory.EnumerateDirectories(TemporaryDirectory, "*", SearchOption.AllDirectories)
                    .Where(directory => File.Exists(Path.Combine(directory, "stage-summary.txt")))
                    .ToArray()
                : [];
        }

        internal string ReadApplicationLogEvidence()
        {
            return File.ReadAllText(ApplicationLogEvidencePath);
        }

        internal string ReadStageSummaryEvidence()
        {
            return File.ReadAllText(Path.Combine(Assert.Single(FindEvidenceDirectories()), "stage-summary.txt"));
        }

        internal void AssertStageSummaryReason(string expectedReason)
        {
            Assert.Contains(
                $"reason={expectedReason}",
                File.ReadAllLines(Path.Combine(Assert.Single(FindEvidenceDirectories()), "stage-summary.txt")));
        }

        internal void AssertFirstCurlFollowsListenEvidence()
        {
            var events = ReadEventLines();
            var listenIndex = Array.FindIndex(events, line => line.StartsWith("listen ", StringComparison.Ordinal));
            var curlIndex = Array.FindIndex(events, line => line.StartsWith("curl ", StringComparison.Ordinal));
            Assert.True(listenIndex >= 0, $"No child-owned listen event was recorded. Events:{Environment.NewLine}{string.Join(Environment.NewLine, events)}");
            Assert.True(curlIndex > listenIndex, $"curl ran before the child-owned listen event. Events:{Environment.NewLine}{string.Join(Environment.NewLine, events)}");
        }

        internal void AssertNoChildrenRemain()
        {
            foreach (var pid in ChildProcessIds)
            {
                using var probe = Process.Start(new ProcessStartInfo
                {
                    FileName = "/bin/kill",
                    ArgumentList = { "-0", pid.ToString(CultureInfo.InvariantCulture) },
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                });
                Assert.NotNull(probe);
                probe.WaitForExit();
                Assert.NotEqual(0, probe.ExitCode);
            }
        }

        internal void AssertFailureEvidenceIsSafe()
        {
            var evidenceDirectory = Assert.Single(FindEvidenceDirectories());
            var entries = Directory.EnumerateFileSystemEntries(evidenceDirectory)
                .Select(entry => Path.GetFileName(entry)!)
                .Order(StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(["application-log-tail.txt", "stage-summary.txt"], entries);
            Assert.All(Directory.EnumerateFiles(evidenceDirectory), file =>
            {
                var attributes = File.GetAttributes(file);
                Assert.False(attributes.HasFlag(FileAttributes.Directory));
                Assert.False(attributes.HasFlag(FileAttributes.ReparsePoint));
            });
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var pid in ChildProcessIds)
            {
                await SendSignalAsync(pid, "KILL", requireSuccess: false);
            }

            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }

        private async Task<VerifierResult> RunCoreAsync(
            string? port,
            string? timeout,
            string? signal,
            TimeSpan processTimeout,
            bool sendSecondSignal = false)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                WorkingDirectory = Path.GetDirectoryName(ScriptPath)!,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add(ScriptPath);
            startInfo.Environment["PATH"] = ShimDirectory;
            startInfo.Environment["TMPDIR"] = TemporaryDirectory;
            startInfo.Environment["VERIFIER_FIXTURE_MODE"] = Mode;
            startInfo.Environment["VERIFIER_FIXTURE_EVENTS"] = EventsPath;
            startInfo.Environment["BASH_ENV"] = BashEnvironmentPath;
            startInfo.Environment["CURL_HOME"] = Path.Combine(Root, "curl-home");
            startInfo.Environment["APP_SURFACE_DEV_AUTH_PORT"] = port ?? "61258";
            if (timeout is not null)
            {
                startInfo.Environment["APP_SURFACE_DEV_AUTH_READY_TIMEOUT_SECONDS"] = timeout;
            }

            using var process = Process.Start(startInfo);
            Assert.NotNull(process);
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();

            if (signal is not null)
            {
                await WaitForChildLaunchAsync(processTimeout);
                await SendSignalAsync(process.Id, signal);
                if (sendSecondSignal)
                {
                    await Task.Delay(100);
                    await SendSignalAsync(process.Id, signal, requireSuccess: false);
                }
            }

            using var cancellation = new CancellationTokenSource(processTimeout);
            try
            {
                await process.WaitForExitAsync(cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException($"Verifier fixture '{Mode}' exceeded {processTimeout}.");
            }

            return new VerifierResult(
                process.ExitCode,
                await standardOutput,
                await standardError);
        }

        private async Task WaitForChildLaunchAsync(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (ChildProcessIds.Count > 0)
                {
                    return;
                }

                await Task.Delay(20);
            }

            throw new TimeoutException($"Verifier fixture '{Mode}' did not launch a child.");
        }

        private async Task WriteExecutableAsync(string name, string contents)
        {
            var path = Path.Combine(ShimDirectory, name);
            await File.WriteAllTextAsync(path, contents);
            if (OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("Verifier contract fixtures require Unix executable permissions.");
            }

            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        private void LinkSystemCommand(string command)
        {
            var target = new[] { Path.Combine("/bin", command), Path.Combine("/usr/bin", command) }
                .FirstOrDefault(File.Exists)
                ?? throw new FileNotFoundException($"Required Unix fixture command '{command}' was not found.");
            File.CreateSymbolicLink(Path.Combine(ShimDirectory, command), target);
        }

        private static async Task SendSignalAsync(int pid, string signal, bool requireSuccess = true)
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "/bin/kill",
                ArgumentList = { $"-{signal}", pid.ToString(CultureInfo.InvariantCulture) },
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            });
            Assert.NotNull(process);
            await process.WaitForExitAsync();
            if (requireSuccess)
            {
                Assert.Equal(0, process.ExitCode);
            }
        }

        private static string FindRepositoryFile(params string[] relativeSegments)
        {
            var copiedFixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", relativeSegments[^1]);
            if (File.Exists(copiedFixture))
            {
                return copiedFixture;
            }

            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current is not null)
            {
                var candidate = Path.Combine([current.FullName, .. relativeSegments]);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                current = current.Parent;
            }

            throw new FileNotFoundException($"Could not locate {Path.Combine(relativeSegments)} from {AppContext.BaseDirectory}.");
        }
    }

    private sealed record VerifierResult(int ExitCode, string StandardOutput, string StandardError)
    {
        internal string CombinedOutput => StandardOutput + Environment.NewLine + StandardError;
    }

    private const string DotnetShim = """
        #!/usr/bin/env bash
        set -u
        mode="${VERIFIER_FIXTURE_MODE:?}"
        events="${VERIFIER_FIXTURE_EVENTS:?}"

        if [[ "${1:-}" == "build" ]]; then
          printf '%s\n' 'build' >> "$events"
          if [[ "$mode" == "build-failure" ]]; then
            printf '%s\n' 'compiler fixture failed' >&2
            exit 42
          fi
          exit 0
        fi

        printf 'launch environment=%s reload=%s\n' "${DOTNET_ENVIRONMENT:-}" "${DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE:-}" >> "$events"
        printf 'child-pid %s\n' "$$" >> "$events"
        launch_url=''
        while [[ "$#" -gt 0 ]]; do
          if [[ "$1" == '--urls' ]]; then
            launch_url="$2"
            shift 2
          else
            shift
          fi
        done
        : "${launch_url:?missing --urls fixture argument}"

        case "$mode" in
          early-exit)
            printf '%s\n' 'fixture child exited'
            exit 23
            ;;
          occupied-port)
            printf 'Failed to bind to address %s: address already in use.\n' "$launch_url"
            exit 98
            ;;
          timeout|foreign-endpoint)
            trap 'exit 0' TERM INT
            while :; do sleep 1; done
            ;;
          resistant-child)
            trap '' TERM
            while :; do sleep 1; done
            ;;
          sensitive-http-failure)
            awk 'BEGIN { for (line = 0; line < 70; line++) { for (i = 0; i < 8192; i++) printf "x"; printf "\\n" } }'
            printf '%s\n' 'Set-Cookie: .AppSurface.DevAuth.Persona=COOKIE_SENTINEL'
            printf '%s\n' 'Authorization: Bearer TOKEN_SENTINEL'
            printf '%s\n' 'contact=person@example.com subject=admin-1'
            printf '%s\n' '{"secret":"RESPONSE_SENTINEL"}'
            printf '%s\n' 'api key is UNKNOWN_SECRET_SENTINEL'
            printf '%s\n' 'info: TOKEN_SENTINEL[0]'
            printf '%s\n' 'TOKEN_SENTINELException: COOKIE_SENTINEL'
            awk 'BEGIN { for (line = 0; line < 5; line++) { for (i = 0; i < 8192; i++) printf "x"; printf "\\n" } }'
            ;;
        esac

        printf 'Now listening on: %s\n' "$launch_url"
        printf 'listen %s\n' "$launch_url" >> "$events"
        trap 'exit 0' TERM INT
        while :; do sleep 1; done
        """;

    private const string BashEnvironmentFixture = """
        if [[ "${VERIFIER_FIXTURE_MODE:-}" == "production-builtin-immune" ]]; then
          kill() {
            case "${1:-}" in
              -TERM|-KILL) return 1 ;;
              *) builtin kill "$@" ;;
            esac
          }
        fi

        """;

    private const string CurlShim = """
        #!/usr/bin/env bash
        set -u
        mode="${VERIFIER_FIXTURE_MODE:?}"
        events="${VERIFIER_FIXTURE_EVENTS:?}"
        method='GET'
        cookie=''
        cookie_file=''
        cookie_jar=''
        follow_redirects=0
        proxy_enabled=0
        connect_timeout=''
        total_timeout=''
        headers=''
        output=''
        url=''

        if [[ -f "${CURL_HOME:-}/.curlrc" ]]; then
          follow_redirects=1
          proxy_enabled=1
        fi
        if [[ "${1:-}" == "--disable" || "${1:-}" == "-q" ]]; then
          follow_redirects=0
          proxy_enabled=0
          printf '%s\n' 'curl-config=disabled' >> "$events"
        fi

        while [[ "$#" -gt 0 ]]; do
          case "$1" in
            --disable|-q) shift ;;
            -X) method="$2"; shift 2 ;;
            -D) headers="$2"; shift 2 ;;
            -o) output="$2"; shift 2 ;;
            --cookie|-b)
              cookie_file="$2"
              [[ -f "$cookie_file" ]] && cookie="$(cat "$cookie_file")"
              shift 2
              ;;
            --cookie-jar|-c) cookie_jar="$2"; shift 2 ;;
            -H)
              case "$2" in
                Cookie:*) cookie="${2#Cookie: }"; printf '%s\n' 'cookie-source=header' >> "$events" ;;
              esac
              shift 2
              ;;
            -w) shift 2 ;;
            --no-location) follow_redirects=0; printf '%s\n' 'redirects=disabled' >> "$events"; shift ;;
            --noproxy) proxy_enabled=0; printf '%s\n' 'proxy=disabled' >> "$events"; shift 2 ;;
            --connect-timeout) connect_timeout="$2"; shift 2 ;;
            --max-time) total_timeout="$2"; shift 2 ;;
            -L) follow_redirects=1; printf '%s\n' 'follow-redirects' >> "$events"; shift ;;
            -*) shift ;;
            *) url="$1"; shift ;;
          esac
        done

        [[ -n "$cookie_file" ]] && printf '%s\n' 'cookie-source=jar' >> "$events"
        printf 'timeouts=connect:%s,total:%s\n' "$connect_timeout" "$total_timeout" >> "$events"
        [[ "$proxy_enabled" -eq 1 ]] && printf '%s\n' 'proxy=used' >> "$events"
        printf 'curl %s %s\n' "$method" "$url" >> "$events"
        if [[ "$mode" == "http-transport-failure" ]]; then
          exit 7
        fi
        if [[ "$mode" == "http-timeout" ]]; then
          exit 28
        fi

        path_with_port="${url#http://127.0.0.1:}"
        path="/${path_with_port#*/}"
        status='200'
        body=''
        set_cookie=''
        case "$method $path" in
          'GET /')
            body='<meta name="viewport" content="width=device-width, initial-scale=1">
        <style>@media(max-width:640px){position:static}.demo-dev-auth { margin: 12px 16px; }</style>
        <header>AppSurface local proof</header>
        <aside aria-label="AppSurface development authentication state">AppSurface DevAuth proof is running.</aside>
        <main>proof</main>'
            [[ "$mode" == "http-contract-failure" || "$mode" == "sensitive-http-failure" ]] && body='wrong root body'
            ;;
          'GET /_appsurface/dev-auth/') body='AppSurface Dev Auth [FAKE LOCAL AUTH]' ;;
          'POST /_appsurface/dev-auth/select/admin') body='Local Admin'; set_cookie='Set-Cookie: .AppSurface.DevAuth.Persona=fixture-cookie; Path=/' ;;
          'POST /_appsurface/dev-auth/select/viewer') body='Local Viewer'; set_cookie='Set-Cookie: .AppSurface.DevAuth.Persona=viewer-cookie; Path=/' ;;
          'GET /api/auth-proof')
            case "$cookie" in
              *viewer-cookie*) status='403'; body='{"appsurfaceAuthOutcome":"Forbid"}' ;;
              *fixture-cookie*)
                if [[ "$mode" == "cross-port-redirect" ]]; then
                  if [[ "$follow_redirects" -eq 1 ]]; then
                    printf 'redirect-cookie-leak %s\n' "$cookie" >> "$events"
                    body='{"result":"allowed","subject":"admin-1"}'
                  else
                    status='302'
                    body='redirect refused by verifier contract'
                  fi
                else
                  body='{"result":"allowed","subject":"admin-1"}'
                fi
                ;;
              *) status='401'; body='{"appsurfaceAuthOutcome":"Challenge"}' ;;
            esac
            ;;
          'GET /_appsurface/dev-auth/status')
            case "$cookie" in
              *fixture-cookie*) body='{"enabled":true,"environment":"Development","scheme":"AppSurface.DevAuth","pathPrefix":"/_appsurface/dev-auth","personaId":"admin","displayName":"Local Admin","subject":"admin-1","isAnonymous":false,"warnings":[]}' ;;
              *) body='{"enabled":true,"environment":"Development","scheme":"AppSurface.DevAuth","pathPrefix":"/_appsurface/dev-auth","personaId":null,"displayName":null,"subject":null,"isAnonymous":true,"warnings":[]}' ;;
            esac
            ;;
          'POST /_appsurface/dev-auth/clear') body='Anonymous'; set_cookie='Set-Cookie: .AppSurface.DevAuth.Persona=; Path=/' ;;
          *) body='fixture response' ;;
        esac

        if [[ -n "$headers" ]]; then
          printf 'HTTP/1.1 %s Fixture\r\n%s\r\n\r\n' "$status" "$set_cookie" > "$headers"
        fi
        if [[ -n "$cookie_jar" && -n "$set_cookie" ]]; then
          printf '%s' "$set_cookie" > "$cookie_jar"
        fi
        if [[ -n "$output" ]]; then
          printf '%s' "$body" > "$output"
          printf '%s' "$status"
        else
          printf '%s' "$body"
        fi

        if [[ "$mode" == "exit-before-cleanup" && "$method $path" == "GET /api/auth-proof" && "$status" == "401" ]]; then
          child_pid="$(awk '/^child-pid / { print $2; exit }' "$events")"
          /bin/kill -KILL "$child_pid"
        fi
        """;
}
