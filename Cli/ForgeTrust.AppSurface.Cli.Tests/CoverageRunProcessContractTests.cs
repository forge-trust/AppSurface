using System.Text;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Cli;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageRunProcessContractTests
{
    [Fact]
    public async Task OptionalMemoryObservation_ShouldNotFailWhenTheChildHasExited()
    {
        var request = CreateProbeRequest(Directory.GetCurrentDirectory(), "replacement");
        using var child = new System.Diagnostics.Process
        {
            StartInfo = new(request.FileName) { UseShellExecute = false, RedirectStandardOutput = true }
        };
        foreach (var argument in request.Arguments) child.StartInfo.ArgumentList.Add(argument);
        Assert.True(child.Start());
        await child.StandardOutput.ReadToEndAsync();
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));

        // An unavailable peak is explicit null; a reaped child must not throw into the v2/v3 adapter.
        var value = CoverageProcessTelemetry.TryGetPeakWorkingSet(child);
        Assert.True(value is null or > 0);
        Assert.Equal(0, child.ExitCode);
    }

    [Fact]
    public void OptionalMemoryObservation_ShouldReturnNullForAnUnassociatedProcess()
    {
        using var child = new System.Diagnostics.Process();
        Assert.Null(CoverageProcessTelemetry.TryGetPeakWorkingSet(child));
    }

    [Fact]
    public void ProcessRequest_ShouldPreserveLegacyDefaultsAndSnapshotOptionalInputs()
    {
        var input = Encoding.UTF8.GetBytes("snapshot");
        var environment = new Dictionary<string, string?> { ["APPSURFACE_SNAPSHOT"] = "before" };
        var defaults = CreateProbeRequest(Directory.GetCurrentDirectory(), "replacement");
        Assert.Null(defaults.EnvironmentOverrides);
        Assert.Null(defaults.StandardInputBytes);

        var request = defaults with
        {
            StandardInputBytes = input,
            EnvironmentOverrides = environment,
        };

        input[0] = (byte)'X';
        environment["APPSURFACE_SNAPSHOT"] = "after";
        var returnedInput = request.StandardInputBytes!;
        returnedInput[1] = (byte)'X';

        Assert.Equal(1024L * 1024, request.StandardOutputLimitBytes);
        Assert.False(request.StrictUtf8);
        Assert.NotNull(request.BudgetCharge);
        Assert.Equal("snapshot", Encoding.UTF8.GetString(request.StandardInputBytes!));
        Assert.Equal("before", request.EnvironmentOverrides!["APPSURFACE_SNAPSHOT"]);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            _ = CreateProbeRequest(Directory.GetCurrentDirectory(), "replacement") with
            {
                StandardInputBytes = new byte[CoverageRunProcessRequest.MaximumStandardInputBytes + 1],
            };
        });
    }

    [Fact]
    public async Task ProcessRunner_ShouldChargeConcurrentStreamsAndReturnStandardErrorSeparately()
    {
        using var directory = TestDirectory.Create();
        using var streamsStarted = new CountdownEvent(2);
        var callbackCount = 0;
        long charged = 0;
        var request = CreateProbeRequest(directory.Path, "streams", "65536") with
        {
            BudgetCharge = bytes =>
            {
                Interlocked.Add(ref charged, bytes);
                if (Interlocked.Increment(ref callbackCount) <= 2)
                {
                    streamsStarted.Signal();
                    if (!streamsStarted.Wait(TimeSpan.FromSeconds(5)))
                    {
                        throw new TimeoutException("stdout and stderr were not charged concurrently");
                    }
                }
            },
        };

        var result = await new CliWrapCoverageRunProcessRunner().RunAsync(request, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(new string('o', 65536), result.StandardOutput);
        Assert.Equal(new string('e', 65536), result.StandardError);
        Assert.Equal(result.StandardOutput + result.StandardError, result.Output);
        Assert.Equal(131072L, charged);
    }

    [Fact]
    public async Task ProcessRunner_ShouldChargeStandardInputAndKeepEnvironmentOverridesChildOnly()
    {
        using var directory = TestDirectory.Create();
        var input = Encoding.UTF8.GetBytes("input-payload");
        var childKey = $"APPSURFACE_CHILD_{Guid.NewGuid():N}".ToUpperInvariant();
        var removedKey = $"APPSURFACE_REMOVE_{Guid.NewGuid():N}".ToUpperInvariant();
        var parentPath = Environment.GetEnvironmentVariable("PATH");
        var parentChildValue = Environment.GetEnvironmentVariable(childKey);
        long charged = 0;
        var request = CreateProbeRequest(directory.Path, "echo-env", childKey, removedKey) with
        {
            EnvironmentOverrides = new Dictionary<string, string?>
            {
                [childKey] = "child-only-value",
                [removedKey] = null,
            },
            StandardInputBytes = input,
            BudgetCharge = bytes => Interlocked.Add(ref charged, bytes),
        };

        var result = await new CliWrapCoverageRunProcessRunner().RunAsync(request, CancellationToken.None);

        var expected = $"child-only-value|unset|{Encoding.UTF8.GetString(input)}";
        Assert.Equal(expected, result.StandardOutput);
        Assert.Equal(input.Length + Encoding.UTF8.GetByteCount(expected), charged);
        Assert.Equal(parentPath, Environment.GetEnvironmentVariable("PATH"));
        Assert.Equal(parentChildValue, Environment.GetEnvironmentVariable(childKey));
    }

    [Fact]
    public async Task ProcessRunner_ShouldPropagateBudgetFaultAndReleaseReapedChildLease()
    {
        using var directory = TestDirectory.Create();
        using var console = new FakeInMemoryConsole();
        await using var supervisor = CreateSupervisor(console);
        using var operation = supervisor.Start("budget fault");
        var expected = new InvalidOperationException("budget callback failed");
        var request = CreateProbeRequest(directory.Path, "flood") with
        {
            Lease = operation.ReserveProcess(),
            BudgetCharge = _ => throw expected,
        };

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new CliWrapCoverageRunProcessRunner().RunAsync(request, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15)));

        Assert.Same(expected, actual);
        Assert.Equal(0, supervisor.RegisteredProcessLeaseCount);
    }

    [Fact]
    public async Task ProcessRunner_ShouldIgnoreObserverFaultWhileBudgetStillChargesActualBytes()
    {
        using var directory = TestDirectory.Create();
        long charged = 0;
        var request = CreateProbeRequest(directory.Path, "streams", "32") with
        {
            OutputObserver = _ => throw new InvalidOperationException("best-effort observer failure"),
            BudgetCharge = bytes => Interlocked.Add(ref charged, bytes),
        };

        var result = await new CliWrapCoverageRunProcessRunner().RunAsync(request, CancellationToken.None);

        Assert.Equal(new string('o', 32), result.StandardOutput);
        Assert.Equal(new string('e', 32), result.StandardError);
        Assert.Equal(64L, charged);
    }

    [Fact]
    public async Task ProcessRunner_ShouldAcceptValidReplacementCharacterWithStrictUtf8()
    {
        using var directory = TestDirectory.Create();
        var request = CreateProbeRequest(directory.Path, "replacement") with { StrictUtf8 = true };

        var result = await new CliWrapCoverageRunProcessRunner().RunAsync(request, CancellationToken.None);

        Assert.Equal("\uFFFD", result.StandardOutput);
    }

    [Fact]
    public async Task ProcessRunner_ShouldRejectMalformedUtf8AndReleaseChildLease()
    {
        using var directory = TestDirectory.Create();
        using var console = new FakeInMemoryConsole();
        await using var supervisor = CreateSupervisor(console);
        using var operation = supervisor.Start("malformed utf8");
        var request = CreateProbeRequest(directory.Path, "invalid-utf8") with
        {
            StrictUtf8 = true,
            Lease = operation.ReserveProcess(),
        };

        await Assert.ThrowsAsync<DecoderFallbackException>(
            () => new CliWrapCoverageRunProcessRunner().RunAsync(request, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15)));

        Assert.Equal(0, supervisor.RegisteredProcessLeaseCount);
    }

    [Fact]
    public async Task ProcessRunner_ShouldStopAndReapWhenStrictOutputLimitIsExceeded()
    {
        using var directory = TestDirectory.Create();
        using var console = new FakeInMemoryConsole();
        await using var supervisor = CreateSupervisor(console);
        using var operation = supervisor.Start("strict output limit");
        var request = CreateProbeRequest(directory.Path, "flood") with
        {
            Lease = operation.ReserveProcess(),
            StrictUtf8 = true,
            StandardOutputLimitBytes = 128,
        };

        var result = await new CliWrapCoverageRunProcessRunner().RunAsync(request, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.True(result.OutputTruncated);
        Assert.True(result.StandardOutput!.Length <= 128);
        Assert.Equal(0, supervisor.RegisteredProcessLeaseCount);
    }

    [Fact]
    public async Task ProcessRunner_ShouldCancelAndReapChildAfterItStarts()
    {
        using var directory = TestDirectory.Create();
        using var console = new FakeInMemoryConsole();
        await using var supervisor = CreateSupervisor(console);
        using var operation = supervisor.Start("cancel process");
        using var cancellation = new CancellationTokenSource();
        var readyFile = Path.Join(directory.Path, "child-ready.txt");
        var request = CreateProbeRequest(directory.Path, "hold", readyFile) with
        {
            Lease = operation.ReserveProcess(),
            OutputObserver = _ => cancellation.Cancel(),
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new CliWrapCoverageRunProcessRunner().RunAsync(request, cancellation.Token).WaitAsync(TimeSpan.FromSeconds(15)));

        Assert.True(File.Exists(readyFile));
        Assert.Equal(0, supervisor.RegisteredProcessLeaseCount);
    }

    [Fact]
    public async Task ProcessRunner_ShouldKeepDefaultStreamedLogsUnbounded()
    {
        using var directory = TestDirectory.Create();
        const int outputBytesPerStream = 1_100_000;
        var outputFile = Path.Join(directory.Path, "process.log");
        long charged = 0;
        var request = CreateProbeRequest(directory.Path, "streams", outputBytesPerStream.ToString()) with
        {
            OutputFile = outputFile,
            BudgetCharge = bytes => Interlocked.Add(ref charged, bytes),
        };

        var result = await new CliWrapCoverageRunProcessRunner().RunAsync(request, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.OutputTruncated);
        Assert.Equal(2L * outputBytesPerStream, new FileInfo(outputFile).Length);
        Assert.Equal(2L * outputBytesPerStream, charged);
        Assert.True(result.StandardError.Length <= 1024 * 1024 + 128);
        Assert.Contains("[output truncated after 1048576 bytes]", result.StandardError, StringComparison.Ordinal);
        var streamedLog = File.ReadAllText(outputFile);
        Assert.Equal(outputBytesPerStream, streamedLog.Count(character => character == 'o'));
        Assert.Equal(outputBytesPerStream, streamedLog.Count(character => character == 'e'));
    }

    private static CoverageRunProcessRequest CreateProbeRequest(
        string workingDirectory,
        string mode,
        params string[] modeArguments)
    {
        var arguments = new List<string> { FindProbeAssembly(), "process", mode };
        arguments.AddRange(modeArguments);
        return new CoverageRunProcessRequest(
            "dotnet",
            arguments,
            workingDirectory,
            null,
            null,
            CoverageRunProcessLease.Detached());
    }

    private static string FindProbeAssembly()
    {
        var baseDirectory = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        var output = new DirectoryInfo(baseDirectory);
        var configuration = output.Parent?.Name ?? "Debug";
        var targetFramework = output.Name;
        for (var directory = output; directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Join(directory.FullName, "Cli", "ForgeTrust.AppSurface.Cli.Tests", "ForgeTrust.AppSurface.Cli.Tests.csproj")))
            {
                var probeAssembly = Path.Join(
                    directory.FullName,
                    "tests",
                    "ForgeTrust.AppSurface.CoverageLeaseProbe",
                    "bin",
                    configuration,
                    targetFramework,
                    "ForgeTrust.AppSurface.CoverageLeaseProbe.dll");
                Assert.True(File.Exists(probeAssembly), $"The process probe was not built: {probeAssembly}");
                return probeAssembly;
            }
        }

        throw new InvalidOperationException("The AppSurface repository root could not be found from the test output directory.");
    }

    private static CoverageRunWatchdogSupervisor CreateSupervisor(FakeInMemoryConsole console)
        => new(
            CoverageRunWatchdogMode.Off,
            TimeSpan.Zero,
            TimeSpan.Zero,
            CoverageTextWriters.Create(console.Output, console.Error),
            TimeProvider.System,
            CancellationToken.None);
}
