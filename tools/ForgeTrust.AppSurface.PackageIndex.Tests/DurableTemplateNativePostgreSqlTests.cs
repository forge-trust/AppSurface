using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace ForgeTrust.AppSurface.PackageIndex.Tests;

public sealed class DurableTemplateNativePostgreSqlTests : IDisposable
{
    private readonly string _root = CreatePrivateTestRoot();

    [Fact]
    public async Task StartProvesPrivateScramClusterAndProjectsSecretOnlyToPsqlEnvironment()
    {
        using var fixture = new NativeClusterFixture(_root);
        var cluster = await fixture.StartAsync();
        var expectedPsqlPath = Path.Join(fixture.BinDirectory, "psql" + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));
        Assert.Equal(Path.GetFullPath(fixture.BinDirectory), cluster.BinDirectory);
        Assert.Equal(expectedPsqlPath, cluster.PsqlPath);
        Assert.Equal(cluster.ToolIdentity.BinDirectory, cluster.BinDirectory);
        Assert.Equal(cluster.ToolIdentity.PsqlPath, cluster.PsqlPath);
        var bootstrap = Assert.IsType<NativePostgreSqlBootstrapRequest>(fixture.Bootstrap.Request);
        var secret = bootstrap.StandardInput.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)[0];

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(cluster.OwnedRoot));
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(Path.Join(cluster.OwnedRoot, "data", "postgresql.auto.conf")));
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(Path.Join(cluster.OwnedRoot, "data", "log")));
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(Path.Join(cluster.OwnedRoot, "socket")));
        }

        Assert.Equal(2, bootstrap.StandardInput.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Contains("--pwprompt", bootstrap.Arguments);
        Assert.DoesNotContain(secret, string.Join(' ', bootstrap.Arguments), StringComparison.Ordinal);
        Assert.Contains(secret, bootstrap.StandardInput, StringComparison.Ordinal);
        Assert.Contains("secret input redacted", bootstrap.ToString(), StringComparison.Ordinal);
        var autoConfiguration = await File.ReadAllTextAsync(Path.Join(cluster.OwnedRoot, "data", "postgresql.auto.conf"));
        Assert.Contains("# initdb baseline owned by PostgreSQL", autoConfiguration, StringComparison.Ordinal);
        Assert.Contains("listen_addresses = '127.0.0.1'", autoConfiguration, StringComparison.Ordinal);
        Assert.Contains("port = ", autoConfiguration, StringComparison.Ordinal);
        Assert.True(await cluster.TryAuthenticateAsync(secret));
        Assert.False(await cluster.TryAuthenticateAsync("wrong-password"));
        Assert.Contains(fixture.Runner.Requests, request => request.OperationName == "psql authentication probe"
            && string.Equals(request.Environment!["PGPASSWORD"], secret, StringComparison.Ordinal));
        Assert.Contains(fixture.Runner.Requests, request => request.OperationName == "psql authentication probe"
            && string.Equals(request.Environment!["PGPASSWORD"], "wrong-password", StringComparison.Ordinal));
        Assert.All(fixture.Runner.Requests.Where(request => request.OperationName is "psql authentication probe" or "psql server version probe"),
            request => Assert.Equal(cluster.PsqlPath, request.FileName));
        Assert.DoesNotContain(fixture.Runner.Requests.SelectMany(request => request.Arguments), argument => argument.Contains(secret, StringComparison.Ordinal));

        var identityJson = JsonSerializer.Serialize(cluster.ToolIdentity);
        Assert.Contains("160005", identityJson, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, identityJson, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, cluster.ToolIdentity.ToString(), StringComparison.Ordinal);
        Assert.True(cluster.SetupBudgetRemainingMilliseconds is > 0 and <= DurableTemplateNativePostgreSql.SetupTimeoutMilliseconds);
        Assert.All(fixture.Runner.Requests, request =>
        {
            Assert.NotNull(request.CapturePolicy);
            Assert.All(request.Environment!.Where(pair => pair.Key != "PGPASSWORD"), pair => Assert.Null(pair.Value));
        });

        await cluster.DisposeWithBudgetAsync(1_000);
        Assert.False(Directory.Exists(cluster.OwnedRoot));
        await cluster.DisposeAsync();
    }

    [Fact]
    public async Task FixedDirectoryResolverUsesProvidedPathWithoutLaunchingAnotherResolverCommand()
    {
        using var fixture = new NativeClusterFixture(_root);
        var resolver = new FixedNativePostgreSqlToolDirectoryResolver(fixture.BinDirectory);

        var resolved = await resolver.ResolveAsync(fixture.Runner, 1234, CancellationToken.None);

        Assert.Equal(Path.GetFullPath(fixture.BinDirectory), resolved);
        Assert.Empty(fixture.Runner.Requests);
        await Assert.ThrowsAsync<PackageIndexException>(() =>
            new FixedNativePostgreSqlToolDirectoryResolver("relative-bin")
                .ResolveAsync(fixture.Runner, 1234, CancellationToken.None));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            resolver.ResolveAsync(fixture.Runner, 1234, cancelled.Token));
    }

    [Fact]
    public async Task HostResolverUsesRunnerSpecificInstalledDirectories()
    {
        using var fixture = new NativeClusterFixture(_root);
        var linux = await new NativePostgreSqlToolDirectoryResolver(NativePostgreSqlHostPlatform.Linux)
            .ResolveAsync(fixture.Runner, 3_000, CancellationToken.None);
        Assert.Equal("/usr/lib/postgresql/16/bin", linux);

        var windows = await new NativePostgreSqlToolDirectoryResolver(
                NativePostgreSqlHostPlatform.Windows,
                fixture.BinDirectory)
            .ResolveAsync(fixture.Runner, 3_000, CancellationToken.None);
        Assert.Equal(Path.GetFullPath(fixture.BinDirectory), windows);
        await Assert.ThrowsAsync<PackageIndexException>(() => new NativePostgreSqlToolDirectoryResolver(
                NativePostgreSqlHostPlatform.Windows,
                "relative")
            .ResolveAsync(fixture.Runner, 3_000, CancellationToken.None));

        var brewPrefix = Path.Join(fixture.Root, "homebrew-keg");
        Directory.CreateDirectory(Path.Join(brewPrefix, "bin"));
        fixture.Runner.BrewPrefix = brewPrefix;
        var mac = await new NativePostgreSqlToolDirectoryResolver(NativePostgreSqlHostPlatform.MacOS)
            .ResolveAsync(fixture.Runner, 3_000, CancellationToken.None);
        Assert.Equal(Path.GetFullPath(Path.Join(brewPrefix, "bin")), mac);
        var brew = Assert.Single(fixture.Runner.Requests);
        Assert.Equal(["--prefix", "postgresql@16"], brew.Arguments);
        Assert.Equal(4_096, brew.CapturePolicy!.MaximumBytesPerStream);
        Assert.All(brew.Environment!.Values, value => Assert.Null(value));

        await Assert.ThrowsAsync<PackageIndexException>(() => new NativePostgreSqlToolDirectoryResolver(
                NativePostgreSqlHostPlatform.Unsupported)
            .ResolveAsync(fixture.Runner, 3_000, CancellationToken.None));
    }

    [Theory]
    [InlineData("15.8", "15.8", "15.8", "15.8")]
    [InlineData("16.5", "17.2", "16.5", "16.5")]
    public async Task OldOrMixedToolchainFailsBeforeCreatingOwnedRoot(
        string initDbVersion,
        string postgresVersion,
        string pgCtlVersion,
        string psqlVersion)
    {
        using var fixture = new NativeClusterFixture(_root);
        fixture.Runner.Versions["initdb"] = initDbVersion;
        fixture.Runner.Versions["postgres"] = postgresVersion;
        fixture.Runner.Versions["pg_ctl"] = pgCtlVersion;
        fixture.Runner.Versions["psql"] = psqlVersion;

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => fixture.StartAsync());

        Assert.Contains("version", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(fixture.OwnedRoot));
        Assert.Null(fixture.Bootstrap.Request);
    }

    [Fact]
    public async Task ExistingRootIsNeverClaimedOrRemoved()
    {
        using var fixture = new NativeClusterFixture(_root);
        Directory.CreateDirectory(fixture.OwnedRoot);
        var sentinel = Path.Join(fixture.OwnedRoot, "keep.txt");
        File.WriteAllText(sentinel, "caller owned");

        await Assert.ThrowsAsync<PackageIndexException>(() => fixture.StartAsync());

        Assert.Equal("caller owned", File.ReadAllText(sentinel));
    }

    [Fact]
    public async Task PortAcquisitionFailureLeavesNoPartialOwnedRoot()
    {
        using var fixture = new NativeClusterFixture(_root);
        fixture.Runtime.FailPortReservation = true;

        await Assert.ThrowsAsync<PackageIndexException>(() => fixture.StartAsync());

        Assert.False(Directory.Exists(fixture.OwnedRoot));
        Assert.DoesNotContain(fixture.Runner.Requests, request => request.OperationName == "pg_ctl start");
        Assert.Null(fixture.Bootstrap.Request);
    }

    [Fact]
    public async Task LongUnixSocketPathFailsBeforeCreatingOwnedRootOrRunningInitdb()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new NativeClusterFixture(_root);
        var longParent = Path.Join(fixture.Root, new string('d', 60));
        Directory.CreateDirectory(longParent);
        var longOwnedRoot = Path.Join(longParent, "cluster");

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => DurableTemplateNativePostgreSql.StartAsync(
            longOwnedRoot,
            fixture.Runner,
            toolDirectoryResolver: new FixedNativePostgreSqlToolDirectoryResolver(fixture.BinDirectory),
            bootstrapRunner: fixture.Bootstrap,
            runtime: fixture.Runtime,
            budgets: new NativePostgreSqlBudgets(5_000, 1_000)));

        Assert.Contains("Unix socket path", error.Message, StringComparison.Ordinal);
        Assert.Contains("shorter owned root", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(longOwnedRoot));
        Assert.Null(fixture.Bootstrap.Request);
        Assert.DoesNotContain(fixture.Runner.Requests, request => request.OperationName == "pg_ctl start");
    }

    [Fact]
    public async Task MissingToolAndSymlinkAncestorFailBeforeOwnedRootCreation()
    {
        using var fixture = new NativeClusterFixture(_root);
        File.Delete(Path.Join(fixture.BinDirectory, "psql" + (OperatingSystem.IsWindows() ? ".exe" : string.Empty)));

        await Assert.ThrowsAsync<PackageIndexException>(() => fixture.StartAsync());
        Assert.False(Directory.Exists(fixture.OwnedRoot));

        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var link = Path.Join(fixture.Root, "linked-parent");
        Directory.CreateSymbolicLink(link, fixture.Root);
        var linkedRoot = Path.Join(link, "cluster");
        await Assert.ThrowsAsync<PackageIndexException>(() => DurableTemplateNativePostgreSql.StartAsync(
            linkedRoot,
            fixture.Runner,
            toolDirectoryResolver: new FixedNativePostgreSqlToolDirectoryResolver(fixture.BinDirectory),
            bootstrapRunner: fixture.Bootstrap,
            runtime: fixture.Runtime,
            budgets: new NativePostgreSqlBudgets(5_000, 1_000)));
        Assert.False(Directory.Exists(linkedRoot));
    }

    [Fact]
    public async Task RefusesRootAndUnixTerminalBeforeInitdb()
    {
        using var fixture = new NativeClusterFixture(_root);
        fixture.Runtime.RunningAsRoot = true;
        await Assert.ThrowsAsync<PackageIndexException>(() => fixture.StartAsync());
        Assert.Empty(fixture.Runner.Requests);
        Assert.False(Directory.Exists(fixture.OwnedRoot));

        if (OperatingSystem.IsWindows())
        {
            return;
        }

        fixture.Runtime.RunningAsRoot = false;
        fixture.Runtime.ControllingTerminal = true;
        var error = await Assert.ThrowsAsync<PackageIndexException>(() => fixture.StartAsync());
        Assert.Contains("runner terminal validation", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(fixture.Bootstrap.Request);
        Assert.False(Directory.Exists(fixture.OwnedRoot));
    }

    [Fact]
    public async Task FailedInitdbCleansOnlyTheNewOwnedRootAndSuppressesSecretDiagnostics()
    {
        using var fixture = new NativeClusterFixture(_root);
        fixture.Bootstrap.Result = new NativePostgreSqlBootstrapResult(1, TimedOut: false);

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => fixture.StartAsync());

        Assert.Contains("initdb", error.Message, StringComparison.OrdinalIgnoreCase);
        var bootstrap = Assert.IsType<NativePostgreSqlBootstrapRequest>(fixture.Bootstrap.Request);
        var secret = bootstrap.StandardInput.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)[0];
        Assert.DoesNotContain(secret, error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(fixture.OwnedRoot));
        Assert.DoesNotContain(fixture.Runner.Requests, request => request.OperationName == "pg_ctl start");
    }

    [Fact]
    public async Task TimedOutInitdbIsCleanedAndFailureDiagnosticOmitsBootstrapSecret()
    {
        using var fixture = new NativeClusterFixture(_root);
        fixture.Bootstrap.Result = new NativePostgreSqlBootstrapResult(-1, TimedOut: true);

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => fixture.StartAsync());

        Assert.Contains("initdb", error.Message, StringComparison.OrdinalIgnoreCase);
        var bootstrap = Assert.IsType<NativePostgreSqlBootstrapRequest>(fixture.Bootstrap.Request);
        var secret = bootstrap.StandardInput.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)[0];
        Assert.DoesNotContain(secret, error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(fixture.OwnedRoot));
        Assert.DoesNotContain("sensitive-initdb-output", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnverifiedInitdbTerminationRetainsOwnedRootWithoutStartingPostgres()
    {
        using var fixture = new NativeClusterFixture(_root);
        fixture.Bootstrap.Result = new NativePostgreSqlBootstrapResult(
            -1,
            TimedOut: true,
            ProcessTerminationVerified: false);

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => fixture.StartAsync());

        Assert.Contains("retained", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("initdb", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(fixture.OwnedRoot));
        Assert.DoesNotContain(fixture.Runner.Requests, request => request.OperationName == "pg_ctl start");
        Assert.DoesNotContain(fixture.Runner.Requests, request => request.OperationName == "pg_ctl stop");
    }

    [Fact]
    public async Task RealBootstrapRunnerRedirectsPasswordInputAndCompletesWithinRequestBudget()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var scriptPath = Path.Join(_root, "read-bootstrap-input.sh");
        WriteExecutableScript(scriptPath,
            "#!/bin/sh\n"
            + "[ ! -t 0 ] || exit 20\n"
            + "IFS= read -r first || exit 21\n"
            + "IFS= read -r second || exit 22\n"
            + "[ \"$first\" = \"$second\" ]\n");
        var runner = new NativePostgreSqlBootstrapRunner();
        var secret = "native-bootstrap-test-secret";

        var result = await runner.RunAsync(
            new NativePostgreSqlBootstrapRequest(
                scriptPath,
                [],
                _root,
                $"{secret}{Environment.NewLine}{secret}{Environment.NewLine}",
                3_000),
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.True(result.ProcessTerminationVerified);
        Assert.DoesNotContain(secret, File.ReadAllText(scriptPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RealBootstrapRunnerKillsTimedOutProcessTreeWithinBound()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var sleepPath = File.Exists("/bin/sleep") ? "/bin/sleep" : "/usr/bin/sleep";
        if (!File.Exists(sleepPath))
        {
            return;
        }

        var scriptPath = Path.Join(_root, "slow-initdb.sh");
        WriteExecutableScript(scriptPath, "#!/bin/sh\nwhile :; do \"" + sleepPath + "\" 1; done\n");
        var runner = new NativePostgreSqlBootstrapRunner();
        var clock = Stopwatch.StartNew();

        var result = await runner.RunAsync(
            new NativePostgreSqlBootstrapRequest(scriptPath, [], _root, "", 100),
            CancellationToken.None);

        Assert.True(result.TimedOut);
        Assert.True(result.ProcessTerminationVerified);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"Timed-out initdb teardown took {clock.Elapsed}.");
    }

    [Fact]
    public async Task RealBootstrapRunnerReportsUnverifiedTimeoutTerminationWithoutWaitingForever()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var sleepPath = File.Exists("/bin/sleep") ? "/bin/sleep" : "/usr/bin/sleep";
        if (!File.Exists(sleepPath))
        {
            return;
        }

        var scriptPath = Path.Join(_root, "slow-unverified-initdb.sh");
        WriteExecutableScript(scriptPath, "#!/bin/sh\nwhile :; do \"" + sleepPath + "\" 1; done\n");
        var terminationCompleted = false;
        var runner = new NativePostgreSqlBootstrapRunner(async process =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
                terminationCompleted = process.HasExited;
            }
            catch (InvalidOperationException)
            {
                terminationCompleted = process.HasExited;
            }
            return false;
        });
        var clock = Stopwatch.StartNew();

        var result = await runner.RunAsync(
            new NativePostgreSqlBootstrapRequest(scriptPath, [], _root, "", 100),
            CancellationToken.None);

        Assert.True(result.TimedOut);
        Assert.False(result.ProcessTerminationVerified);
        Assert.True(terminationCompleted);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"Unverified initdb teardown took {clock.Elapsed}.");
    }

    [Fact]
    public async Task RealBootstrapRunnerRethrowsCancellationAfterVerifiedTerminationAndDrain()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var sleepPath = File.Exists("/bin/sleep") ? "/bin/sleep" : "/usr/bin/sleep";
        if (!File.Exists(sleepPath))
        {
            return;
        }

        var scriptPath = Path.Join(_root, "cancel-initdb.sh");
        var startedPath = Path.Join(_root, "cancel-initdb.started");
        WriteExecutableScript(scriptPath,
            "#!/bin/sh\n"
            + "echo started > \"" + startedPath + "\"\n"
            + "exec \"" + sleepPath + "\" 60\n");
        var terminationVerified = false;
        var runner = new NativePostgreSqlBootstrapRunner(async process =>
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
            terminationVerified = process.HasExited;
            return terminationVerified;
        });
        using var cancellation = new CancellationTokenSource();
        var runTask = runner.RunAsync(
            new NativePostgreSqlBootstrapRequest(scriptPath, [], _root, string.Empty, 3_000),
            cancellation.Token);

        try
        {
            Assert.True(
                await WaitForFileAsync(startedPath, runTask, TimeSpan.FromSeconds(3)),
                "The bootstrap shim did not start before the test deadline.");
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);

            Assert.True(terminationVerified);
        }
        finally
        {
            cancellation.Cancel();
            if (!runTask.IsCompleted)
            {
                try
                {
                    await runTask.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (OperationCanceledException)
                {
                    // The cancellation assertion above owns the expected result.
                }
            }
        }
    }

    [Fact]
    public async Task RealBootstrapRunnerFailsClosedWhenCancelledChildLeavesOutputPipeOpen()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var sleepPath = File.Exists("/bin/sleep") ? "/bin/sleep" : "/usr/bin/sleep";
        if (!File.Exists(sleepPath))
        {
            return;
        }

        var scriptPath = Path.Join(_root, "cancel-descendant-holds-pipes.sh");
        var descendantPidPath = Path.Join(_root, "cancel-descendant.pid");
        WriteExecutableScript(scriptPath,
            "#!/bin/sh\n"
            + "\"" + sleepPath + "\" 60 &\n"
            + "echo $! > \"" + descendantPidPath + "\"\n"
            + "wait\n");
        var rootTerminationVerified = false;
        var runner = new NativePostgreSqlBootstrapRunner(async process =>
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: false);
            }

            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
            rootTerminationVerified = process.HasExited;
            return rootTerminationVerified;
        });
        using var cancellation = new CancellationTokenSource();
        var runTask = runner.RunAsync(
            new NativePostgreSqlBootstrapRequest(scriptPath, [], _root, string.Empty, 3_000),
            cancellation.Token);
        Process? descendant = null;
        DateTime descendantStartTime = default;
        string? descendantProcessName = null;

        try
        {
            Assert.True(
                await WaitForFileAsync(descendantPidPath, runTask, TimeSpan.FromSeconds(3)),
                "The bootstrap shim did not start its pipe-holding descendant before the test deadline.");
            Assert.True(int.TryParse(File.ReadAllText(descendantPidPath).Trim(), out var descendantPid));
            descendant = Process.GetProcessById(descendantPid);
            descendantStartTime = descendant.StartTime;
            descendantProcessName = descendant.ProcessName;
            Assert.Equal(Path.GetFileNameWithoutExtension(sleepPath), descendantProcessName);
            Assert.False(descendant.HasExited);

            cancellation.Cancel();

            await Assert.ThrowsAsync<NativePostgreSqlBootstrapTerminationException>(() => runTask);

            Assert.True(rootTerminationVerified);
            Assert.True(cancellation.IsCancellationRequested);
            Assert.False(descendant.HasExited);
        }
        finally
        {
            cancellation.Cancel();
            if (!runTask.IsCompleted)
            {
                try
                {
                    await runTask.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (OperationCanceledException)
                {
                    // The cancellation assertion above owns the expected result.
                }
                catch (NativePostgreSqlBootstrapTerminationException)
                {
                    // The retained-pipe assertion above owns the expected result.
                }
            }

            if (descendant is not null)
            {
                try
                {
                    if (!descendant.HasExited
                        && descendant.StartTime == descendantStartTime
                        && string.Equals(descendant.ProcessName, descendantProcessName, StringComparison.Ordinal))
                    {
                        descendant.Kill(entireProcessTree: false);
                        await descendant.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
                    }
                }
                catch (ArgumentException)
                {
                    // The identified descendant exited before test cleanup.
                }
                catch (InvalidOperationException)
                {
                    // The identified descendant exited while test cleanup was checking its identity.
                }

                descendant.Dispose();
            }
        }
    }

    [Fact]
    public async Task RealBootstrapRunnerBoundsOutputDrainWhenDescendantKeepsPipesOpen()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var sleepPath = File.Exists("/bin/sleep") ? "/bin/sleep" : "/usr/bin/sleep";
        if (!File.Exists(sleepPath))
        {
            return;
        }

        var scriptPath = Path.Join(_root, "descendant-holds-pipes.sh");
        var descendantPidPath = Path.Join(_root, "descendant.pid");
        WriteExecutableScript(scriptPath,
            "#!/bin/sh\n"
            + "\"" + sleepPath + "\" 60 &\n"
            + "echo $! > \"" + descendantPidPath + "\"\n"
            + "exit 0\n");
        var clock = Stopwatch.StartNew();

        var result = await new NativePostgreSqlBootstrapRunner().RunAsync(
            new NativePostgreSqlBootstrapRequest(scriptPath, [], _root, "", 3_000),
            CancellationToken.None);

        try
        {
            Assert.False(result.TimedOut);
            Assert.False(result.ProcessTerminationVerified);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"Bounded output-drain check took {clock.Elapsed}.");
        }
        finally
        {
            if (File.Exists(descendantPidPath)
                && int.TryParse(File.ReadAllText(descendantPidPath).Trim(), out var descendantPid))
            {
                try
                {
                    using var descendant = Process.GetProcessById(descendantPid);
                    descendant.Kill(entireProcessTree: true);
                    await descendant.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
                }
                catch (ArgumentException)
                {
                    // The descendant exited before the test could clean it up.
                }
            }
        }
    }

    [Fact]
    public async Task RealNativeClusterStartsAuthenticatesAndCleansThroughOsAdapters()
    {
        var configuredPostgreSqlBin = Environment.GetEnvironmentVariable("APPSURFACE_TEMPLATE_TEST_NATIVE_PG_BIN");
        if (string.IsNullOrWhiteSpace(configuredPostgreSqlBin))
        {
            return;
        }

        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.True(Path.IsPathFullyQualified(configuredPostgreSqlBin),
            "APPSURFACE_TEMPLATE_TEST_NATIVE_PG_BIN must be an absolute PostgreSQL binary directory.");
        var acquiredPostgreSqlBin = Path.GetFullPath(configuredPostgreSqlBin);
        var executableSuffix = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        foreach (var tool in new[] { "initdb", "postgres", "pg_ctl", "psql" })
        {
            Assert.True(File.Exists(Path.Join(acquiredPostgreSqlBin, tool + executableSuffix)),
                $"APPSURFACE_TEMPLATE_TEST_NATIVE_PG_BIN must contain {tool}{executableSuffix}.");
        }

        var temporaryDirectory = OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetFullPath(Path.GetTempPath());
        var privateTestRoot = Path.Join(temporaryDirectory, "apg" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(privateTestRoot);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(privateTestRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var ownedRoot = Path.Join(privateTestRoot, "cluster");
        DurableTemplateNativePostgreSql? cluster = null;
        var bootstrapRunner = new RecordingBootstrapRunner(new NativePostgreSqlBootstrapRunner());
        try
        {
            cluster = await DurableTemplateNativePostgreSql.StartAsync(
                ownedRoot,
                new CliWrapCommandRunner(),
                toolDirectoryResolver: new FixedNativePostgreSqlToolDirectoryResolver(acquiredPostgreSqlBin),
                bootstrapRunner: bootstrapRunner,
                budgets: new NativePostgreSqlBudgets(60_000, 10_000));

            var toolVersion = Version.Parse(cluster.ToolIdentity.Version);
            var serverVersion = Version.Parse(Assert.IsType<string>(cluster.ToolIdentity.ServerVersion));
            Assert.True(cluster.ToolIdentity.MajorVersion >= 16);
            Assert.Equal(toolVersion.Major, cluster.ToolIdentity.MajorVersion);
            Assert.Equal(toolVersion.Major, serverVersion.Major);
            Assert.Equal(toolVersion.Minor, serverVersion.Minor);
            Assert.Equal(toolVersion.Major * 10_000 + toolVersion.Minor, cluster.ToolIdentity.ServerVersionNumber);
            Assert.Contains("Host=127.0.0.1;", cluster.AdminConnectionString, StringComparison.Ordinal);
            Assert.Contains("Pooling=false", cluster.AdminConnectionString, StringComparison.Ordinal);
            Assert.True(await cluster.TryAuthenticateAsync(ExtractPassword(cluster.AdminConnectionString)));
            Assert.True(cluster.SetupBudgetRemainingMilliseconds is > 0 and <= 60_000);

            await cluster.DisposeWithBudgetAsync(10_000);
            Assert.False(Directory.Exists(ownedRoot));
            await cluster.DisposeAsync();
            cluster = null;
        }
        catch (PackageIndexException exception)
        {
            var result = bootstrapRunner.Result;
            var safeBootstrapResult = result is null
                ? bootstrapRunner.FailedToReturnResult ? "adapter-threw" : "not-run"
                : $"exit-code={result.ExitCode};timed-out={result.TimedOut};termination-verified={result.ProcessTerminationVerified}";
            throw new InvalidOperationException($"{exception.Message} Safe initdb result: {safeBootstrapResult}.", exception);
        }
        finally
        {
            if (cluster is not null)
            {
                await cluster.DisposeAsync();
            }

            if (Directory.Exists(ownedRoot))
            {
                // Do not delete a retained root here; setup may have failed to verify process termination.
            }
            else if (Directory.Exists(privateTestRoot))
            {
                Directory.Delete(privateTestRoot);
            }
        }
    }

    [Fact]
    public async Task NativeRuntimeProbesRealPortsAndValidatesOwnedProcessPidFile()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var runtime = NativePostgreSqlRuntime.Instance;
        Assert.False(runtime.IsRunningAsRoot);
        Assert.False(runtime.HasControllingTerminal);

        var port = runtime.ReserveLoopbackPort();
        Assert.InRange(port, 1, 65_535);
        using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
        {
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            var probePort = Assert.IsType<IPEndPoint>(socket.LocalEndPoint).Port;
            Assert.False(runtime.IsLoopbackPortListening(probePort));
            socket.Listen(1);
            Assert.True(runtime.IsLoopbackPortListening(probePort));
        }

        var executablePath = CreatePostgresNamedSleepProcessImage(_root);
        var dataDirectory = Path.Join(_root, "runtime-data");
        Directory.CreateDirectory(dataDirectory);
        var pidPath = Path.Join(dataDirectory, "postmaster.pid");
        Assert.Null(runtime.FindOwnedPostmaster(dataDirectory, executablePath, port));
        File.WriteAllText(pidPath, "malformed\n");
        Assert.Throws<PackageIndexException>(() => runtime.FindOwnedPostmaster(dataDirectory, executablePath, port));
        File.WriteAllText(pidPath, new string('x', 4_097));
        Assert.Throws<PackageIndexException>(() => runtime.FindOwnedPostmaster(dataDirectory, executablePath, port));

        using var process = Process.Start(new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            ArgumentList = { "60" }
        });
        Assert.NotNull(process);
        try
        {
            var identity = new NativePostgreSqlProcessIdentity(
                process.Id,
                new DateTimeOffset(process.StartTime.ToUniversalTime()).ToUnixTimeSeconds());
            File.WriteAllText(pidPath, RenderPostmasterPid(identity, dataDirectory, port));

            Assert.Equal(identity, runtime.FindOwnedPostmaster(dataDirectory, executablePath, port));
            Assert.True(runtime.IsSameProcessAlive(identity, executablePath));
            Assert.False(runtime.IsSameProcessAlive(
                identity with { StartTimeUnixSeconds = identity.StartTimeUnixSeconds + 100 },
                executablePath));
            Assert.False(runtime.IsSameProcessAlive(identity, executablePath + ".different"));

            Assert.Throws<PackageIndexException>(() => runtime.FindOwnedPostmaster(dataDirectory, executablePath, port + 1));
            File.WriteAllText(pidPath, RenderPostmasterPid(identity, dataDirectory + "-different", port));
            Assert.Throws<PackageIndexException>(() => runtime.FindOwnedPostmaster(dataDirectory, executablePath, port));
            File.WriteAllText(pidPath, RenderPostmasterPid(identity, dataDirectory, port));

            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Null(runtime.FindOwnedPostmaster(dataDirectory, executablePath, port));
            Assert.False(runtime.IsSameProcessAlive(identity, executablePath));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            }
        }
    }

    [Fact]
    public async Task OccupiedLoopbackPortDoesNotStartOrRemoveAnythingOutsideTheOwnedRoot()
    {
        using var fixture = new NativeClusterFixture(_root);
        fixture.Runtime.Listening = true;

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => fixture.StartAsync());

        Assert.Contains("loopback port check", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fixture.Runner.Requests, request => request.OperationName == "pg_ctl start");
        Assert.False(Directory.Exists(fixture.OwnedRoot));
        Assert.True(fixture.Runtime.Listening);
    }

    [Fact]
    public async Task UnverifiedStopRetainsRootAndCanBeRetriedWithinALaterSharedBudget()
    {
        using var fixture = new NativeClusterFixture(_root, setupMilliseconds: 5_000, cleanupMilliseconds: 500);
        var cluster = await fixture.StartAsync();
        fixture.Runner.StopSucceeds = false;

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => cluster.DisposeWithBudgetAsync(100).AsTask());

        Assert.Contains("retained", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(cluster.OwnedRoot));
        Assert.Equal(1, fixture.Runner.Requests.Count(request => request.OperationName == "pg_ctl stop"));

        fixture.Runner.StopSucceeds = true;
        await cluster.DisposeWithBudgetAsync(500);
        Assert.False(Directory.Exists(cluster.OwnedRoot));
    }

    [Fact]
    public async Task StopCommandExceptionRetainsRootAndCanBeRetriedWithoutExposingChildDetails()
    {
        using var fixture = new NativeClusterFixture(_root, setupMilliseconds: 5_000, cleanupMilliseconds: 200);
        var cluster = await fixture.StartAsync();
        fixture.Runner.ThrowOnStop = true;

        var error = await Assert.ThrowsAsync<PackageIndexException>(
            () => cluster.DisposeWithBudgetAsync(200).AsTask());

        Assert.Contains("retained", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fake runner exception detail", error.Message, StringComparison.Ordinal);
        Assert.True(fixture.Runtime.ServerRunning);
        Assert.True(Directory.Exists(cluster.OwnedRoot));

        fixture.Runner.ThrowOnStop = false;
        await cluster.DisposeWithBudgetAsync(1_000);
        Assert.False(fixture.Runtime.ServerRunning);
        Assert.False(Directory.Exists(cluster.OwnedRoot));
    }

    [Fact]
    public async Task MissingPostmasterIdentityWhileObservedProcessLivesRetainsRootUntilRetry()
    {
        using var fixture = new NativeClusterFixture(_root, setupMilliseconds: 5_000, cleanupMilliseconds: 200);
        var cluster = await fixture.StartAsync();
        fixture.Runtime.HidePostmasterFromLookup = true;

        var error = await Assert.ThrowsAsync<PackageIndexException>(
            () => cluster.DisposeWithBudgetAsync(200).AsTask());

        Assert.Contains("retained", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(fixture.Runtime.ServerRunning);
        Assert.True(Directory.Exists(cluster.OwnedRoot));
        Assert.DoesNotContain(fixture.Runner.Requests, request => request.OperationName == "pg_ctl stop");

        fixture.Runtime.HidePostmasterFromLookup = false;
        await cluster.DisposeWithBudgetAsync(1_000);
        Assert.False(fixture.Runtime.ServerRunning);
        Assert.False(Directory.Exists(cluster.OwnedRoot));
    }

    [Fact]
    public async Task UnverifiablePostmasterLookupRetainsRootWithoutIssuingStop()
    {
        using var fixture = new NativeClusterFixture(_root);
        var cluster = await fixture.StartAsync();
        fixture.Runtime.FailPostmasterLookup = true;

        var error = await Assert.ThrowsAsync<PackageIndexException>(
            () => cluster.DisposeWithBudgetAsync(500).AsTask());

        Assert.Contains("ownership probe", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(fixture.Runtime.ServerRunning);
        Assert.True(Directory.Exists(cluster.OwnedRoot));
        Assert.DoesNotContain(fixture.Runner.Requests, request => request.OperationName == "pg_ctl stop");

        fixture.Runtime.FailPostmasterLookup = false;
        await cluster.DisposeWithBudgetAsync(1_000);
        Assert.False(Directory.Exists(cluster.OwnedRoot));
    }

    [Fact]
    public async Task ReparsePointInsideOwnedRootBlocksStopAndDeletionUntilRemoved()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new NativeClusterFixture(_root);
        var cluster = await fixture.StartAsync();
        var outsideSentinel = Path.Join(fixture.Root, "outside-sentinel.txt");
        var linkPath = Path.Join(cluster.OwnedRoot, "untrusted-link");
        File.WriteAllText(outsideSentinel, "preserve me");
        File.CreateSymbolicLink(linkPath, outsideSentinel);

        var error = await Assert.ThrowsAsync<PackageIndexException>(
            () => cluster.DisposeWithBudgetAsync(500).AsTask());

        Assert.Contains("reparse", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(fixture.Runtime.ServerRunning);
        Assert.True(Directory.Exists(cluster.OwnedRoot));
        Assert.Equal("preserve me", File.ReadAllText(outsideSentinel));
        Assert.DoesNotContain(fixture.Runner.Requests, request => request.OperationName == "pg_ctl stop");

        File.Delete(linkPath);
        await cluster.DisposeWithBudgetAsync(1_000);
        Assert.False(Directory.Exists(cluster.OwnedRoot));
        Assert.Equal("preserve me", File.ReadAllText(outsideSentinel));
    }

    [Fact]
    public async Task ZeroSharedCleanupBudgetFailsClosedWithoutStartingAnotherCleanupWindow()
    {
        using var fixture = new NativeClusterFixture(_root);
        var cluster = await fixture.StartAsync();

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => cluster.DisposeWithBudgetAsync(0).AsTask());

        Assert.Contains("shared cleanup budget expired", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(fixture.Runtime.ServerRunning);
        Assert.True(Directory.Exists(cluster.OwnedRoot));
        await cluster.DisposeWithBudgetAsync(1_000);
        Assert.False(Directory.Exists(cluster.OwnedRoot));
    }

    [Fact]
    public async Task StartFailureCleansRootAndLateServerAfterTimedOutStartIsStopped()
    {
        using (var failedStart = new NativeClusterFixture(_root))
        {
            failedStart.Runner.StartExitCode = 7;
            await Assert.ThrowsAsync<PackageIndexException>(() => failedStart.StartAsync());
            Assert.False(failedStart.Runtime.ServerRunning);
            Assert.False(Directory.Exists(failedStart.OwnedRoot));
        }

        using var lateStart = new NativeClusterFixture(_root, setupMilliseconds: 5_000, cleanupMilliseconds: 1_500);
        lateStart.Runner.StartExitCode = -1;
        var error = await Assert.ThrowsAsync<PackageIndexException>(() => lateStart.StartAsync());
        Assert.Contains("pg_ctl start", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(lateStart.Runner.Requests, request => request.OperationName == "pg_ctl stop");
        Assert.False(lateStart.Runtime.ServerRunning);
        Assert.False(Directory.Exists(lateStart.OwnedRoot));
    }

    [Fact]
    public async Task UnexpectedPostmasterLookupFailureDuringCleanupRetainsRootAndSuppressesDetails()
    {
        using var fixture = new NativeClusterFixture(_root);
        fixture.Runtime.ThrowOnPostmasterLookupUnexpectedly = true;

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => fixture.StartAsync());

        Assert.Contains("postmaster verification", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("retained", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private process lookup detail", error.Message, StringComparison.Ordinal);
        Assert.True(fixture.Runtime.ServerRunning);
        Assert.True(Directory.Exists(fixture.OwnedRoot));
        Assert.DoesNotContain(fixture.Runner.Requests, request => request.OperationName == "pg_ctl stop");
    }

    [Fact]
    public async Task TimedOutStartWithUnidentifiedListenerRetainsOwnedRoot()
    {
        using var fixture = new NativeClusterFixture(_root, setupMilliseconds: 5_000, cleanupMilliseconds: 150);
        fixture.Runner.StartExitCode = -1;
        fixture.Runner.LateListenerOnly = true;

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => fixture.StartAsync());

        Assert.Contains("retained", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(fixture.Runtime.Listening);
        Assert.True(Directory.Exists(fixture.OwnedRoot));
        Assert.DoesNotContain(fixture.Runner.Requests, request => request.OperationName == "pg_ctl stop");
    }

    [Fact]
    public async Task ServerVersionMustMatchTheValidatedToolBuild()
    {
        using var fixture = new NativeClusterFixture(_root);
        fixture.Runner.ServerVersionOutput = "17.2|170002\n";

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => fixture.StartAsync());

        Assert.Contains("server version", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(fixture.Runtime.ServerRunning);
        Assert.False(Directory.Exists(fixture.OwnedRoot));
    }

    [Fact]
    public async Task OwnershipMarkerReplacementBlocksStopAndDeletion()
    {
        using var fixture = new NativeClusterFixture(_root);
        var cluster = await fixture.StartAsync();
        var markerPath = Path.Join(cluster.OwnedRoot, ".appsurface-native-postgresql-owner");
        var ownerToken = File.ReadAllText(markerPath);
        File.WriteAllText(markerPath, "replacement");

        await Assert.ThrowsAsync<PackageIndexException>(() => cluster.DisposeWithBudgetAsync(500).AsTask());

        Assert.True(Directory.Exists(cluster.OwnedRoot));
        Assert.True(fixture.Runtime.ServerRunning);
        File.WriteAllText(markerPath, ownerToken);
        await cluster.DisposeWithBudgetAsync(1_000);
        Assert.False(Directory.Exists(cluster.OwnedRoot));
    }

    [Fact]
    public async Task OwnershipRacePreservesForeignMarkerAndSentinelWithoutStartingCluster()
    {
        using var fixture = new NativeClusterFixture(_root);
        var markerPath = Path.Join(fixture.OwnedRoot, ".appsurface-native-postgresql-owner");
        var sentinelPath = Path.Join(fixture.OwnedRoot, "foreign-sentinel.txt");
        fixture.Runtime.OnReserveLoopbackPort = () =>
        {
            Directory.CreateDirectory(fixture.OwnedRoot);
            File.WriteAllText(markerPath, "foreign-owner-token");
            File.WriteAllText(sentinelPath, "preserve foreign contents");
        };

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => fixture.StartAsync());

        Assert.Contains("could not verify cleanup", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(fixture.OwnedRoot));
        Assert.Equal("foreign-owner-token", File.ReadAllText(markerPath));
        Assert.Equal("preserve foreign contents", File.ReadAllText(sentinelPath));
        Assert.Null(fixture.Bootstrap.Request);
        Assert.False(fixture.Runtime.ServerRunning);
        Assert.DoesNotContain(fixture.Runner.Requests, request => request.OperationName == "pg_ctl start");
        Assert.DoesNotContain(fixture.Runner.Requests, request => request.OperationName == "pg_ctl stop");
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("oversized")]
    public async Task SuccessfulInitdbWithMissingOrOversizedAutoConfigurationNeverStartsCluster(string failure)
    {
        using var fixture = new NativeClusterFixture(_root);
        var outsideSentinel = Path.Join(fixture.Root, "outside-auto-configuration-sentinel.txt");
        File.WriteAllText(outsideSentinel, "preserve caller contents");
        fixture.Bootstrap.AfterInitialize = dataDirectory =>
        {
            var settingsPath = Path.Join(dataDirectory, "postgresql.auto.conf");
            if (failure == "missing")
            {
                File.Delete(settingsPath);
            }
            else
            {
                using var stream = new FileStream(settingsPath, FileMode.Open, FileAccess.Write, FileShare.None);
                stream.SetLength(64 * 1024 + 1);
            }
        };

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => fixture.StartAsync());

        Assert.NotNull(fixture.Bootstrap.Request);
        Assert.Contains("post-initdb validation and configuration", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(fixture.Runtime.ServerRunning);
        Assert.False(Directory.Exists(fixture.OwnedRoot));
        Assert.Equal("preserve caller contents", File.ReadAllText(outsideSentinel));
        Assert.DoesNotContain(fixture.Runner.Requests, request => request.OperationName == "pg_ctl start");
        Assert.DoesNotContain(fixture.Runner.Requests, request => request.OperationName == "pg_ctl stop");
        Assert.DoesNotContain(fixture.Runner.Requests, request => request.OperationName == "psql authentication probe");
    }

    [Theory]
    [InlineData("rejected")]
    [InlineData("truncated")]
    [InlineData("exception")]
    public async Task AuthenticationFailureCleansOwnedClusterAndSuppressesSecretDiagnostics(string failure)
    {
        using var fixture = new NativeClusterFixture(_root);
        fixture.Runner.RejectAuthentication = failure == "rejected";
        if (failure != "rejected")
        {
            fixture.Runner.AuthenticationProbeHandler = request => failure == "truncated"
                ? new ExternalCommandResult(
                    0,
                    "1\n",
                    $"sensitive-auth-output {request.Environment!["PGPASSWORD"]}",
                    StandardOutputTruncated: true)
                : throw new InvalidOperationException($"sensitive-auth-output {request.Environment!["PGPASSWORD"]}");
        }

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => fixture.StartAsync());

        var bootstrap = Assert.IsType<NativePostgreSqlBootstrapRequest>(fixture.Bootstrap.Request);
        var secret = bootstrap.StandardInput.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)[0];
        Assert.Contains("SCRAM authentication", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secret, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive-auth-output", error.ToString(), StringComparison.Ordinal);
        Assert.Single(fixture.Runner.Requests, request => request.OperationName == "psql authentication probe");
        Assert.DoesNotContain(fixture.Runner.Requests, request => request.OperationName == "psql server version probe");
        Assert.Single(fixture.Runner.Requests, request => request.OperationName == "pg_ctl stop");
        Assert.False(fixture.Runtime.ServerRunning);
        Assert.False(Directory.Exists(fixture.OwnedRoot));

        if (failure == "exception")
        {
            var failingProbe = fixture.Runner.AuthenticationProbeHandler;
            fixture.Runner.AuthenticationProbeHandler = null;
            var cluster = await fixture.StartAsync();
            fixture.Runner.AuthenticationProbeHandler = failingProbe;

            Assert.False(await cluster.TryAuthenticateAsync("standalone-auth-probe-secret"));
            Assert.True(fixture.Runtime.ServerRunning);

            await cluster.DisposeWithBudgetAsync(2_000);
            Assert.False(Directory.Exists(cluster.OwnedRoot));
        }
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("truncated")]
    [InlineData("malformed")]
    [InlineData("below-floor")]
    public async Task UnusableServerVersionEvidenceStopsClusterAndSuppressesDiagnostics(string failure)
    {
        using var fixture = new NativeClusterFixture(_root);
        fixture.Runner.ServerVersionResult = failure switch
        {
            "failed" => new ExternalCommandResult(1, "16.5|160005\n", "sensitive-server-version-output"),
            "truncated" => new ExternalCommandResult(
                0,
                "16.5|160005\n",
                "sensitive-server-version-output",
                StandardOutputTruncated: true),
            "malformed" => new ExternalCommandResult(0, "16.5|invalid\n", "sensitive-server-version-output"),
            _ => new ExternalCommandResult(0, "15.8|150008\n", "sensitive-server-version-output")
        };

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => fixture.StartAsync());

        var bootstrap = Assert.IsType<NativePostgreSqlBootstrapRequest>(fixture.Bootstrap.Request);
        var secret = bootstrap.StandardInput.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)[0];
        Assert.Contains("server version verification", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sensitive-server-version-output", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, error.ToString(), StringComparison.Ordinal);
        Assert.Single(fixture.Runner.Requests, request => request.OperationName == "psql authentication probe");
        Assert.Single(fixture.Runner.Requests, request => request.OperationName == "psql server version probe");
        Assert.Single(fixture.Runner.Requests, request => request.OperationName == "pg_ctl stop");
        Assert.False(fixture.Runtime.ServerRunning);
        Assert.False(Directory.Exists(fixture.OwnedRoot));
    }

    [Theory]
    [InlineData("pg_ctl")]
    [InlineData("collector")]
    public async Task OversizedOwnedLogRetainsStoppedRootAndCanBeRecoveredAtMaximumSize(string logName)
    {
        using var fixture = new NativeClusterFixture(_root);
        var cluster = await fixture.StartAsync();
        var outsideSentinel = Path.Join(fixture.Root, "outside-log-sentinel.txt");
        File.WriteAllText(outsideSentinel, "preserve caller contents");
        var logPath = logName == "pg_ctl"
            ? Path.Join(cluster.OwnedRoot, "pg_ctl.log")
            : Path.Join(cluster.OwnedRoot, "data", "log", "appsurface-native.log");
        using (var stream = new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(1024 * 1024 + 1);
        }

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => cluster.DisposeWithBudgetAsync(2_000).AsTask());

        Assert.Contains("oversized private log", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(fixture.Runtime.ServerRunning);
        Assert.True(Directory.Exists(cluster.OwnedRoot));
        Assert.Equal(1024L * 1024 + 1, new FileInfo(logPath).Length);
        Assert.Single(fixture.Runner.Requests, request => request.OperationName == "pg_ctl stop");
        Assert.Equal("preserve caller contents", File.ReadAllText(outsideSentinel));

        using (var stream = new FileStream(logPath, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(1024 * 1024);
        }
        await cluster.DisposeWithBudgetAsync(2_000);

        Assert.False(Directory.Exists(cluster.OwnedRoot));
        Assert.Single(fixture.Runner.Requests, request => request.OperationName == "pg_ctl stop");
        Assert.Equal("preserve caller contents", File.ReadAllText(outsideSentinel));
    }

    [Fact]
    public async Task MissingOwnershipMarkerBlocksStopAndDeletionUntilRestored()
    {
        using var fixture = new NativeClusterFixture(_root);
        var cluster = await fixture.StartAsync();
        var markerPath = Path.Join(cluster.OwnedRoot, ".appsurface-native-postgresql-owner");
        var ownerToken = File.ReadAllText(markerPath);
        var sentinelPath = Path.Join(cluster.OwnedRoot, "retained-sentinel.txt");
        File.WriteAllText(sentinelPath, "preserve owned contents until verified");
        File.Delete(markerPath);

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => cluster.DisposeWithBudgetAsync(500).AsTask());

        Assert.Contains("owner marker is missing or unsafe", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(fixture.Runtime.ServerRunning);
        Assert.True(Directory.Exists(cluster.OwnedRoot));
        Assert.False(File.Exists(markerPath));
        Assert.Equal("preserve owned contents until verified", File.ReadAllText(sentinelPath));
        Assert.DoesNotContain(fixture.Runner.Requests, request => request.OperationName == "pg_ctl stop");

        File.WriteAllText(markerPath, ownerToken);
        await cluster.DisposeWithBudgetAsync(2_000);

        Assert.False(fixture.Runtime.ServerRunning);
        Assert.False(Directory.Exists(cluster.OwnedRoot));
        Assert.Single(fixture.Runner.Requests, request => request.OperationName == "pg_ctl stop");
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("missing-parent")]
    public async Task UnsafeOwnedRootIsRejectedBeforeInitdbWithDefaultBudgets(string failure)
    {
        using var fixture = new NativeClusterFixture(_root);
        var ownedRoot = failure == "relative"
            ? "relative-native-cluster"
            : Path.Join(fixture.Root, "missing-parent", "cluster");

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => DurableTemplateNativePostgreSql.StartAsync(
            ownedRoot,
            fixture.Runner,
            toolDirectoryResolver: new FixedNativePostgreSqlToolDirectoryResolver(fixture.BinDirectory),
            bootstrapRunner: fixture.Bootstrap,
            runtime: fixture.Runtime));

        Assert.Contains(failure == "relative" ? "absolute path" : "existing private temporary directory",
            error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(fixture.Bootstrap.Request);
        Assert.DoesNotContain(fixture.Runner.Requests, request => request.OperationName == "pg_ctl start");
        Assert.False(Directory.Exists(Path.Join(fixture.Root, "missing-parent")));
    }

    [Fact]
    public async Task FileCreatedAtOwnedRootAfterValidationIsPreservedWithoutRunningInitdb()
    {
        using var fixture = new NativeClusterFixture(_root);
        fixture.Runtime.OnReserveLoopbackPort = () => File.WriteAllText(fixture.OwnedRoot, "foreign root file");

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => fixture.StartAsync());

        Assert.Contains("could not create its private root", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("foreign root file", File.ReadAllText(fixture.OwnedRoot));
        Assert.Null(fixture.Bootstrap.Request);
        Assert.DoesNotContain(fixture.Runner.Requests, request => request.OperationName == "pg_ctl start");
        Assert.DoesNotContain(fixture.Runner.Requests, request => request.OperationName == "pg_ctl stop");
    }

    [Fact]
    public async Task FilesystemRootIsRejectedByTheExplicitOwnershipBoundaryBeforeClusterInitialization()
    {
        using var fixture = new NativeClusterFixture(_root);
        var root = Path.GetPathRoot(Path.GetFullPath(fixture.Root))!;

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => DurableTemplateNativePostgreSql.StartAsync(
            root, fixture.Runner,
            toolDirectoryResolver: new FixedNativePostgreSqlToolDirectoryResolver(fixture.BinDirectory),
            bootstrapRunner: fixture.Bootstrap, runtime: fixture.Runtime));

        Assert.Contains("cannot be a filesystem root", error.Message, StringComparison.Ordinal);
        Assert.Equal(4, fixture.Runner.Requests.Count);
        Assert.All(fixture.Runner.Requests, request => Assert.Equal(["--version"], request.Arguments));
        Assert.Null(fixture.Bootstrap.Request);
        Assert.True(Directory.Exists(root));
    }

    [Fact]
    public async Task PartialRootWithMarkerDirectoryAndForeignContentsIsRetainedWithoutInitdb()
    {
        using var fixture = new NativeClusterFixture(_root);
        var markerPath = Path.Join(fixture.OwnedRoot, ".appsurface-native-postgresql-owner");
        var sentinelPath = Path.Join(fixture.OwnedRoot, "foreign-contents.txt");
        fixture.Runtime.OnReserveLoopbackPort = () =>
        {
            Directory.CreateDirectory(markerPath);
            File.WriteAllText(sentinelPath, "preserve unowned contents");
        };

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => fixture.StartAsync());

        Assert.Contains("could not verify cleanup", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(markerPath));
        Assert.Equal("preserve unowned contents", File.ReadAllText(sentinelPath));
        Assert.Null(fixture.Bootstrap.Request);
        Assert.DoesNotContain(fixture.Runner.Requests, request => request.OperationName == "pg_ctl start");
        Assert.DoesNotContain(fixture.Runner.Requests, request => request.OperationName == "pg_ctl stop");
    }

    [Fact]
    public async Task StartWaitsForBothOwnedPostmasterAndListeningPortBeforeAuthenticating()
    {
        using var fixture = new NativeClusterFixture(_root);
        fixture.Runtime.MissingPostmasterProbesRemaining = 1;
        fixture.Runtime.ClosedPostmasterPortProbesRemaining = 1;
        fixture.Runner.AuthenticationProbeHandler = _ =>
        {
            Assert.True(fixture.Runtime.OwnedPostmasterObserved);
            Assert.True(fixture.Runtime.PostmasterListenerObserved);
            return new ExternalCommandResult(0, "1\n", string.Empty);
        };

        var cluster = await fixture.StartAsync();

        Assert.True(fixture.Runtime.ServerRunning);
        Assert.Single(fixture.Runner.Requests, request => request.OperationName == "psql authentication probe");
        await cluster.DisposeWithBudgetAsync(2_000);
        Assert.False(Directory.Exists(cluster.OwnedRoot));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SuccessfulStartCommandWithoutVerifiedReadinessExpiresBeforeAuthentication(bool hideIdentity)
    {
        using var fixture = new NativeClusterFixture(_root, setupMilliseconds: 1_000, cleanupMilliseconds: 1_000);
        fixture.Runtime.HidePostmasterFromLookup = hideIdentity;
        fixture.Runtime.ClosedPostmasterPortProbesRemaining = hideIdentity ? 0 : int.MaxValue;

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => fixture.StartAsync());

        Assert.Contains("postmaster verification", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(fixture.Runner.Requests, request => request.OperationName == "pg_ctl start");
        Assert.DoesNotContain(fixture.Runner.Requests, request => request.OperationName == "psql authentication probe");
        Assert.DoesNotContain(fixture.Runner.Requests, request => request.OperationName == "psql server version probe");
        if (hideIdentity)
        {
            Assert.Contains("retained", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(fixture.Runtime.ServerRunning);
            Assert.True(Directory.Exists(fixture.OwnedRoot));
            Assert.DoesNotContain(fixture.Runner.Requests, request => request.OperationName == "pg_ctl stop");
        }
        else
        {
            Assert.False(fixture.Runtime.ServerRunning);
            Assert.False(Directory.Exists(fixture.OwnedRoot));
            Assert.Single(fixture.Runner.Requests, request => request.OperationName == "pg_ctl stop");
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AuthenticationCancellationPropagatesAndCleanupUsesAnIndependentToken(bool duringStartup)
    {
        using var fixture = new NativeClusterFixture(_root);
        using var cancellation = new CancellationTokenSource();
        DurableTemplateNativePostgreSql? cluster = duringStartup ? null : await fixture.StartAsync();
        fixture.Runner.AuthenticationProbeHandler = _ =>
        {
            cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("The cancelled authentication probe must not return.");
        };

        var error = duringStartup
            ? await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.StartAsync(cancellation.Token))
            : await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                cluster!.TryAuthenticateAsync(ExtractPassword(cluster!.ConnectionString), cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        if (duringStartup)
        {
            Assert.DoesNotContain(fixture.Runner.Requests, request => request.OperationName == "psql server version probe");
        }
        else
        {
            Assert.True(fixture.Runtime.ServerRunning);
            Assert.True(Directory.Exists(fixture.OwnedRoot));
            await cluster!.DisposeWithBudgetAsync(2_000);
        }
        Assert.Single(fixture.Runner.Requests, request => request.OperationName == "pg_ctl stop");
        Assert.False(fixture.Runtime.ServerRunning);
        Assert.False(Directory.Exists(fixture.OwnedRoot));
    }

    [Theory]
    [InlineData(0, "not-a-process-id")]
    [InlineData(2, "not-a-start-time")]
    [InlineData(3, "not-a-port")]
    public void NativeRuntimeRejectsMalformedNumericPidFieldsBeforeProcessLookup(int field, string malformedValue)
    {
        var dataDirectory = Path.Join(_root, "malformed-runtime-data");
        Directory.CreateDirectory(dataDirectory);
        var pidPath = Path.Join(dataDirectory, "postmaster.pid");
        var lines = new[] { "12345", dataDirectory, "1800000000", "54321", "0", "0", string.Empty };
        lines[field] = malformedValue;
        File.WriteAllLines(pidPath, lines);

        var error = Assert.Throws<PackageIndexException>(() =>
            NativePostgreSqlRuntime.Instance.FindOwnedPostmaster(dataDirectory, "unused-postgres", 54_321));

        Assert.Contains("identity did not match", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(lines, File.ReadAllLines(pidPath));
    }

    [Fact]
    public async Task BootstrapLaunchFailureSuppressesExecutableAndCredentialDiagnostics()
    {
        var secret = "bootstrap-launch-failure-secret";
        var executablePath = Path.Join(_root, "missing-bootstrap-" + secret);
        var request = new NativePostgreSqlBootstrapRequest(executablePath, ["--pwprompt"], _root, secret, 1_000);

        var error = await Assert.ThrowsAsync<PackageIndexException>(() =>
            new NativePostgreSqlBootstrapRunner().RunAsync(request, CancellationToken.None));

        Assert.Contains("process could not start", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secret, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(executablePath, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThrowingBootstrapTerminationFailsClosedAndSuppressesPrivateFailureDetails()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var executablePath = Path.Join(_root, "throwing-termination.sh");
        var startedPath = Path.Join(_root, "throwing-termination.started");
        var sleepPath = File.Exists("/bin/sleep") ? "/bin/sleep" : "/usr/bin/sleep";
        WriteExecutableScript(executablePath,
            "#!/bin/sh\n" + "echo ready > \"" + startedPath + "\"\n"
            + "exec \"" + sleepPath + "\" 60\n");
        const string secret = "bootstrap-termination-private-secret";
        var terminationObserved = false;
        int? childId = null;
        DateTime childStartTime = default;
        var runner = new NativePostgreSqlBootstrapRunner(async process =>
        {
            childId = process.Id;
            childStartTime = process.StartTime;
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            terminationObserved = process.HasExited;
            throw new InvalidOperationException(secret);
        });

        using var cancellation = new CancellationTokenSource();
        var runTask = runner.RunAsync(
            new NativePostgreSqlBootstrapRequest(executablePath, [], _root, string.Empty, 3_000),
            cancellation.Token);
        try
        {
            Assert.True(await WaitForFileAsync(startedPath, runTask, TimeSpan.FromSeconds(3)),
                "The bootstrap child must acknowledge startup before cancellation exercises termination.");
            cancellation.Cancel();
            var error = await Assert.ThrowsAsync<NativePostgreSqlBootstrapTerminationException>(() => runTask);

            Assert.True(terminationObserved);
            Assert.DoesNotContain(secret, error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            cancellation.Cancel();
            if (childId is int processId)
            {
                try
                {
                    using var child = Process.GetProcessById(processId);
                    if (!child.HasExited && child.StartTime == childStartTime)
                    {
                        child.Kill(entireProcessTree: true);
                        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
                    }
                }
                catch (ArgumentException)
                {
                    // The specifically identified bootstrap child has already exited.
                }
            }
        }
    }

    [Fact]
    public async Task BootstrapClearsInheritedPostgreSqlEnvironmentAndDrainsBothSecretBearingStreams()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var suffix = Guid.NewGuid().ToString("N");
        var upperKey = "PG_APPSURFACE_NATIVE_TEST_" + suffix;
        var lowerKey = "pg_APPSURFACE_NATIVE_TEST_" + suffix;
        var scriptPath = Path.Join(_root, "bootstrap-child-protocol.sh");
        const string secret = "bootstrap-protocol-private-secret";
        WriteExecutableScript(scriptPath,
            "#!/bin/sh\n"
            + "[ \"${" + upperKey + "+present}\" != present ] || exit 20\n"
            + "[ \"${" + lowerKey + "+present}\" != present ] || exit 21\n"
            + "[ \"$1\" = 'argument with spaces' ] || exit 22\n"
            + "[ \"$2\" = 'literal;argument' ] || exit 23\n"
            + "IFS= read -r first || exit 24\n"
            + "IFS= read -r second || exit 25\n"
            + "[ \"$first\" = \"$second\" ] || exit 26\n"
            + "i=0\nwhile [ \"$i\" -lt 5000 ]; do\n"
            + "printf '%s\\n' \"$first\"\nprintf '%s\\n' \"$second\" >&2\n"
            + "i=$((i + 1))\ndone\nexit 7\n");
        var previousUpper = Environment.GetEnvironmentVariable(upperKey);
        var previousLower = Environment.GetEnvironmentVariable(lowerKey);
        try
        {
            Environment.SetEnvironmentVariable(upperKey, secret);
            Environment.SetEnvironmentVariable(lowerKey, secret);

            var result = await new NativePostgreSqlBootstrapRunner().RunAsync(
                new NativePostgreSqlBootstrapRequest(scriptPath, ["argument with spaces", "literal;argument"], _root,
                    $"{secret}{Environment.NewLine}{secret}{Environment.NewLine}", 5_000),
                CancellationToken.None);

            Assert.Equal(7, result.ExitCode);
            Assert.False(result.TimedOut);
            Assert.True(result.ProcessTerminationVerified);
            Assert.DoesNotContain(secret, result.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(upperKey, previousUpper);
            Environment.SetEnvironmentVariable(lowerKey, previousLower);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefaultBootstrapTerminationVerifiesTheOwnedChildOnTimeoutOrCancellation(bool cancelCaller)
    {
        if (OperatingSystem.IsWindows()) return;
        var scriptPath = Path.Join(_root, "default-termination.sh");
        var pidPath = Path.Join(_root, "default-termination.pid");
        var sleepPath = File.Exists("/bin/sleep") ? "/bin/sleep" : "/usr/bin/sleep";
        WriteExecutableScript(scriptPath,
            "#!/bin/sh\n" + "echo $$ > \"" + pidPath + ".tmp\"\n"
            + "mv \"" + pidPath + ".tmp\" \"" + pidPath + "\"\n"
            + "exec \"" + sleepPath + "\" 60\n");
        using var cancellation = new CancellationTokenSource();
        var task = new NativePostgreSqlBootstrapRunner().RunAsync(
            new NativePostgreSqlBootstrapRequest(scriptPath, [], _root, string.Empty, cancelCaller ? 30_000 : 5_000), cancellation.Token);
        Process? child = null;
        DateTime start = default;
        try
        {
            Assert.True(await WaitForFileAsync(pidPath, task, TimeSpan.FromSeconds(3)));
            try
            {
                child = Process.GetProcessById(int.Parse(File.ReadAllText(pidPath).Trim(), CultureInfo.InvariantCulture));
                start = child.StartTime;
            }
            catch (Exception exception) when (!cancelCaller && exception is
                ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Accept a disappearing process only after the real runner verifies its timed-out termination.
                var result = await task.WaitAsync(TimeSpan.FromSeconds(12));
                Assert.True(result.TimedOut);
                Assert.True(result.ProcessTerminationVerified);
                if (child is not null)
                {
                    Assert.True(child.HasExited);
                    child.Dispose();
                    child = null;
                }
            }
            if (cancelCaller)
            {
                cancellation.Cancel();
                var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
                Assert.True(cancellation.IsCancellationRequested);
                Assert.True(error.CancellationToken.IsCancellationRequested);
            }
            else
            {
                var result = await task.WaitAsync(TimeSpan.FromSeconds(12));
                Assert.True(result.TimedOut);
                Assert.True(result.ProcessTerminationVerified);
                Assert.Equal(-1, result.ExitCode);
            }
            if (child is not null) Assert.True(child.HasExited);
        }
        finally
        {
            cancellation.Cancel();
            if (child is not null)
            {
                if (!child.HasExited && child.StartTime == start)
                {
                    child.Kill(entireProcessTree: true);
                    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
                }
                child.Dispose();
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeRuntimeRejectsMissingOrForeignProcessesEvenWithValidPidFileShape(bool missingProcess)
    {
        using var current = Process.GetCurrentProcess();
        var directory = Path.Join(_root, "unowned-runtime-data");
        Directory.CreateDirectory(directory);
        var identity = new NativePostgreSqlProcessIdentity(missingProcess ? int.MaxValue : current.Id,
            new DateTimeOffset(current.StartTime.ToUniversalTime()).ToUnixTimeSeconds());
        var pidPath = Path.Join(directory, "postmaster.pid");
        var contents = RenderPostmasterPid(identity, directory, 54_321);
        File.WriteAllText(pidPath, contents);

        Assert.Null(NativePostgreSqlRuntime.Instance.FindOwnedPostmaster(directory, "unused-postgres", 54_321));
        Assert.False(NativePostgreSqlRuntime.Instance.IsSameProcessAlive(identity, "unused-postgres"));
        Assert.Equal(contents, File.ReadAllText(pidPath));
        Assert.False(current.HasExited);
    }

    [Fact]
    public async Task DefaultToolResolverSelectsTheCurrentHostAcquisitionPath()
    {
        using var fixture = new NativeClusterFixture(_root);
        fixture.Runner.BrewPrefix = fixture.Root;
        var resolver = new NativePostgreSqlToolDirectoryResolver(windowsPgBin: fixture.BinDirectory);

        var result = await resolver.ResolveAsync(fixture.Runner, 3_000, CancellationToken.None);

        if (OperatingSystem.IsMacOS())
        {
            Assert.Equal(fixture.BinDirectory, result);
            Assert.Equal(["--prefix", "postgresql@16"], Assert.Single(fixture.Runner.Requests).Arguments);
        }
        else
        {
            Assert.Equal(OperatingSystem.IsLinux() ? "/usr/lib/postgresql/16/bin" : fixture.BinDirectory, result);
            Assert.Empty(fixture.Runner.Requests);
        }
        Assert.Null(fixture.Bootstrap.Request);
    }

    [Fact]
    public async Task MacToolResolverUsesTheCanonicalInstalledKegBehindASymlink()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new NativeClusterFixture(_root);
        var keg = Path.Join(fixture.Root, "installed-keg");
        Directory.CreateDirectory(Path.Join(keg, "bin"));
        var link = Path.Join(fixture.Root, "linked-keg");
        Directory.CreateSymbolicLink(link, keg);
        fixture.Runner.BrewPrefix = link;

        var result = await new NativePostgreSqlToolDirectoryResolver(NativePostgreSqlHostPlatform.MacOS)
            .ResolveAsync(fixture.Runner, 3_000, CancellationToken.None);

        Assert.Equal(Path.Join(keg, "bin"), result);
        Assert.Equal(["--prefix", "postgresql@16"], Assert.Single(fixture.Runner.Requests).Arguments);
        Assert.True(Directory.Exists(link));
        Assert.Null(fixture.Bootstrap.Request);
    }

    [Fact]
    public async Task OwnedRootDeletionFailureReportsStoppedClusterAndRetainsBlockedContents()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.False(NativePostgreSqlRuntime.Instance.IsRunningAsRoot);
        using var fixture = new NativeClusterFixture(_root);
        var cluster = await fixture.StartAsync();
        var secret = ExtractPassword(cluster.ConnectionString);
        var blockedDirectory = Path.Join(cluster.OwnedRoot, "blocked-cleanup");
        Directory.CreateDirectory(blockedDirectory);
        var sentinelPath = Path.Join(blockedDirectory, "retained-contents.txt");
        File.WriteAllText(sentinelPath, "preserve contents when deletion is denied");
        var originalMode = File.GetUnixFileMode(blockedDirectory);
        try
        {
            File.SetUnixFileMode(blockedDirectory, UnixFileMode.UserRead | UnixFileMode.UserExecute);

            var error = await Assert.ThrowsAsync<PackageIndexException>(() => cluster.DisposeWithBudgetAsync(2_000).AsTask());

            Assert.Contains("stopped, but its owned root could not be removed", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("manual cleanup may be required", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(secret, error.ToString(), StringComparison.Ordinal);
            Assert.False(fixture.Runtime.ServerRunning);
            Assert.Single(fixture.Runner.Requests, request => request.OperationName == "pg_ctl stop");
            Assert.True(Directory.Exists(cluster.OwnedRoot));
            Assert.Equal("preserve contents when deletion is denied", File.ReadAllText(sentinelPath));
        }
        finally
        {
            if (Directory.Exists(blockedDirectory))
            {
                File.SetUnixFileMode(blockedDirectory, originalMode);
            }
        }
    }

    [Fact]
    public async Task BudgetsCannotExceedProductionCeilings()
    {
        Assert.Throws<PackageIndexException>(() => new NativePostgreSqlBudgets(0, 1).Validate());
        Assert.Throws<PackageIndexException>(() => new NativePostgreSqlBudgets(1, 0).Validate());
        Assert.Throws<PackageIndexException>(() => new NativePostgreSqlBudgets(
            DurableTemplateNativePostgreSql.SetupTimeoutMilliseconds + 1, 1).Validate());
        Assert.Throws<PackageIndexException>(() => new NativePostgreSqlBudgets(
            1, DurableTemplateNativePostgreSql.CleanupTimeoutMilliseconds + 1).Validate());
        NativePostgreSqlBudgets.Default.Validate();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static string CreatePrivateTestRoot()
    {
        var temporary = OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetFullPath(Path.GetTempPath());
        var root = Path.Join(temporary, "apg" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return root;
    }

    private static string CreatePostgresNamedSleepProcessImage(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The Unix process identity fixture is available on Unix hosts only.");
        }

        var sleepPath = File.Exists("/bin/sleep") ? "/bin/sleep" : "/usr/bin/sleep";
        Assert.True(File.Exists(sleepPath), "A local sleep executable is required for the native process identity test.");
        var executablePath = Path.Join(directory, "postgres");
        File.Copy(sleepPath, executablePath);
        File.SetUnixFileMode(executablePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return executablePath;
    }

    private static string RenderPostmasterPid(
        NativePostgreSqlProcessIdentity identity,
        string dataDirectory,
        int port)
        => string.Join(
            Environment.NewLine,
            identity.ProcessId,
            dataDirectory,
            identity.StartTimeUnixSeconds,
            port,
            "0",
            "0",
            string.Empty);

    private static string ExtractPassword(string connectionString)
    {
        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
        if (builder.TryGetValue("Password", out var password) && password is string value)
        {
            return value;
        }

        throw new InvalidOperationException("The native PostgreSQL connection string did not include a password.");
    }

    private static void WriteExecutableScript(string path, string content)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The bootstrap executable fixture is available on Unix hosts only.");
        }

        File.WriteAllText(path, content);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static async Task<bool> WaitForFileAsync(string path, Task operation, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (!File.Exists(path) && !operation.IsCompleted && clock.Elapsed < timeout)
        {
            await Task.Delay(10);
        }

        return File.Exists(path);
    }

    private sealed class RecordingBootstrapRunner(INativePostgreSqlBootstrapRunner inner) : INativePostgreSqlBootstrapRunner
    {
        internal NativePostgreSqlBootstrapResult? Result { get; private set; }
        internal bool FailedToReturnResult { get; private set; }

        public async Task<NativePostgreSqlBootstrapResult> RunAsync(
            NativePostgreSqlBootstrapRequest request,
            CancellationToken cancellationToken)
        {
            try
            {
                Result = await inner.RunAsync(request, cancellationToken);
                return Result;
            }
            catch
            {
                FailedToReturnResult = true;
                throw;
            }
        }
    }

    private sealed class NativeClusterFixture : IDisposable
    {
        private readonly int _setupMilliseconds;
        private readonly int _cleanupMilliseconds;

        internal NativeClusterFixture(string root, int setupMilliseconds = 5_000, int cleanupMilliseconds = 2_000)
        {
            Root = root;
            _setupMilliseconds = setupMilliseconds;
            _cleanupMilliseconds = cleanupMilliseconds;
            BinDirectory = Path.Join(root, "bin");
            Directory.CreateDirectory(BinDirectory);
            foreach (var name in new[] { "initdb", "postgres", "pg_ctl", "psql" })
            {
                File.WriteAllText(Path.Join(BinDirectory, name + (OperatingSystem.IsWindows() ? ".exe" : string.Empty)), name);
            }

            OwnedRoot = Path.Join(root, "cluster-" + Guid.NewGuid().ToString("N"));
            Runner = new FakeCommandRunner(Runtime);
            Bootstrap = new FakeBootstrapRunner(Runtime);
        }

        internal string Root { get; }
        internal string BinDirectory { get; }
        internal string OwnedRoot { get; }
        internal FakeRuntime Runtime { get; } = new();
        internal FakeCommandRunner Runner { get; }
        internal FakeBootstrapRunner Bootstrap { get; }

        internal Task<DurableTemplateNativePostgreSql> StartAsync(CancellationToken cancellationToken = default)
            => DurableTemplateNativePostgreSql.StartAsync(
                OwnedRoot,
                Runner,
                cancellationToken,
                new FixedNativePostgreSqlToolDirectoryResolver(BinDirectory),
                Bootstrap,
                Runtime,
                new NativePostgreSqlBudgets(_setupMilliseconds, _cleanupMilliseconds));

        public void Dispose()
        {
            if (Directory.Exists(OwnedRoot))
            {
                Directory.Delete(OwnedRoot, recursive: true);
            }
            if (Directory.Exists(BinDirectory))
            {
                Directory.Delete(BinDirectory, recursive: true);
            }
        }
    }

    private sealed class FakeBootstrapRunner(FakeRuntime runtime) : INativePostgreSqlBootstrapRunner
    {
        internal NativePostgreSqlBootstrapRequest? Request { get; private set; }
        internal NativePostgreSqlBootstrapResult Result { get; set; } = new(0, TimedOut: false);
        internal Action<string>? AfterInitialize { get; set; }

        public Task<NativePostgreSqlBootstrapResult> RunAsync(
            NativePostgreSqlBootstrapRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Request = request;
            if (Result.ExitCode == 0 && !Result.TimedOut)
            {
                var dataDirectory = request.Arguments[1];
                runtime.BootstrapPassword = request.StandardInput.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)[0];
                Directory.CreateDirectory(dataDirectory);
                File.WriteAllText(Path.Join(dataDirectory, "PG_VERSION"), "16\n");
                File.WriteAllText(
                    Path.Join(dataDirectory, "postgresql.auto.conf"),
                    "# initdb baseline owned by PostgreSQL\n# settings added by initdb\n\n");
                AfterInitialize?.Invoke(dataDirectory);
            }
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeCommandRunner(FakeRuntime runtime) : IExternalCommandRunner
    {
        internal List<ExternalCommandRequest> Requests { get; } = [];
        internal Dictionary<string, string> Versions { get; } = new(StringComparer.Ordinal)
        {
            ["initdb"] = "16.5",
            ["postgres"] = "16.5",
            ["pg_ctl"] = "16.5",
            ["psql"] = "16.5"
        };
        internal bool StopSucceeds { get; set; } = true;
        internal bool ThrowOnStop { get; set; }
        internal bool RejectAuthentication { get; set; }
        internal Func<ExternalCommandRequest, ExternalCommandResult>? AuthenticationProbeHandler { get; set; }
        internal int StartExitCode { get; set; }
        internal bool LateListenerOnly { get; set; }
        internal string ServerVersionOutput { get; set; } = "16.5|160005\n";
        internal ExternalCommandResult? ServerVersionResult { get; set; }
        internal string? BrewPrefix { get; set; }

        public Task<ExternalCommandResult> RunAsync(ExternalCommandRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            var name = Path.GetFileNameWithoutExtension(request.FileName);
            if (name == "brew")
            {
                return Task.FromResult(BrewPrefix is null
                    ? new ExternalCommandResult(1, string.Empty, "no prefix")
                    : new ExternalCommandResult(0, BrewPrefix + Environment.NewLine, string.Empty));
            }
            if (request.Arguments.SequenceEqual(["--version"]))
            {
                return Task.FromResult(new ExternalCommandResult(0, $"{name} (PostgreSQL) {Versions[name]}\n", string.Empty));
            }
            if (name == "pg_ctl" && request.Arguments.LastOrDefault() == "start")
            {
                var logIndex = Array.IndexOf(request.Arguments.ToArray(), "-l");
                if (logIndex >= 0)
                {
                    File.WriteAllText(request.Arguments[logIndex + 1], "bounded pg_ctl output");
                }
                if (StartExitCode == -1)
                {
                    if (LateListenerOnly)
                    {
                        runtime.LateListenerOnNextPortProbe = true;
                    }
                    else
                    {
                        runtime.LateServerOnNextPostmasterProbe = true;
                    }
                }
                else if (StartExitCode == 0)
                {
                    runtime.ServerRunning = true;
                }
                return Task.FromResult(new ExternalCommandResult(StartExitCode, string.Empty, string.Empty));
            }
            if (name == "pg_ctl" && request.Arguments.LastOrDefault() == "stop")
            {
                if (ThrowOnStop)
                {
                    throw new InvalidOperationException("fake runner exception detail");
                }
                if (StopSucceeds)
                {
                    runtime.ServerRunning = false;
                }
                return Task.FromResult(new ExternalCommandResult(StopSucceeds ? 0 : 1, string.Empty, "secret-safe fake failure"));
            }
            if (name == "psql")
            {
                var command = request.Arguments[Array.IndexOf(request.Arguments.ToArray(), "-c") + 1];
                if (command == "SELECT 1")
                {
                    if (AuthenticationProbeHandler is not null)
                    {
                        return Task.FromResult(AuthenticationProbeHandler(request));
                    }
                    var password = request.Environment!["PGPASSWORD"];
                    return Task.FromResult(!RejectAuthentication && password == runtime.BootstrapPassword
                        ? new ExternalCommandResult(0, "1\n", string.Empty)
                        : new ExternalCommandResult(2, string.Empty, password ?? "missing"));
                }
                return Task.FromResult(ServerVersionResult ?? new ExternalCommandResult(0, ServerVersionOutput, string.Empty));
            }

            return Task.FromResult(new ExternalCommandResult(1, string.Empty, "unexpected command"));
        }
    }

    private sealed class FakeRuntime : INativePostgreSqlRuntime
    {
        private static readonly NativePostgreSqlProcessIdentity _identity = new(12345, 1_800_000_000);

        internal bool ServerRunning { get; set; }
        internal bool Listening { get; set; }
        internal string? BootstrapPassword { get; set; }
        internal bool RunningAsRoot { get; set; }
        internal bool ControllingTerminal { get; set; }
        internal bool FailPortReservation { get; set; }
        internal Action? OnReserveLoopbackPort { get; set; }
        internal bool LateServerOnNextPostmasterProbe { get; set; }
        internal bool LateListenerOnNextPortProbe { get; set; }
        internal bool HidePostmasterFromLookup { get; set; }
        internal int MissingPostmasterProbesRemaining { get; set; }
        internal int ClosedPostmasterPortProbesRemaining { get; set; }
        internal bool OwnedPostmasterObserved { get; private set; }
        internal bool PostmasterListenerObserved { get; private set; }
        internal bool FailPostmasterLookup { get; set; }
        internal bool ThrowOnPostmasterLookupUnexpectedly { get; set; }
        public bool IsRunningAsRoot => RunningAsRoot;
        public bool HasControllingTerminal => ControllingTerminal;
        public int ReserveLoopbackPort()
        {
            if (FailPortReservation)
            {
                throw new InvalidOperationException("No loopback port was available.");
            }
            OnReserveLoopbackPort?.Invoke();
            return 54_321;
        }
        public bool IsLoopbackPortListening(int port)
        {
            if (ServerRunning && ClosedPostmasterPortProbesRemaining > 0)
            {
                ClosedPostmasterPortProbesRemaining--;
                return false;
            }
            if (LateListenerOnNextPortProbe)
            {
                LateListenerOnNextPortProbe = false;
                Listening = true;
            }
            var listening = Listening || ServerRunning;
            PostmasterListenerObserved |= ServerRunning && listening;
            return listening;
        }
        public NativePostgreSqlProcessIdentity? FindOwnedPostmaster(string dataDirectory, string postgresPath, int port)
        {
            if (ServerRunning && MissingPostmasterProbesRemaining > 0)
            {
                MissingPostmasterProbesRemaining--;
                return null;
            }
            if (FailPostmasterLookup)
            {
                throw new PackageIndexException("Native PostgreSQL ownership probe failed.");
            }
            if (ThrowOnPostmasterLookupUnexpectedly)
            {
                throw new InvalidOperationException("private process lookup detail");
            }
            if (LateServerOnNextPostmasterProbe)
            {
                LateServerOnNextPostmasterProbe = false;
                ServerRunning = true;
            }
            var identity = ServerRunning && !HidePostmasterFromLookup ? _identity : null;
            OwnedPostmasterObserved |= identity is not null;
            return identity;
        }
        public bool IsSameProcessAlive(NativePostgreSqlProcessIdentity identity, string postgresPath) => ServerRunning;
    }
}
