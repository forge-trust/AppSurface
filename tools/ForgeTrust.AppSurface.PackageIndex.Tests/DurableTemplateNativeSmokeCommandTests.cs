using System.Globalization;

namespace ForgeTrust.AppSurface.PackageIndex.Tests;

public sealed class DurableTemplateNativeSmokeCommandTests
{
    private const string CleanupMarker = "[native-cleanup] elapsed-ms=250";
    private const string SmokeMarker = "[native-smoke] read-only ordinary startup passed";

    [Fact]
    public async Task RunsSmokeWithPrivateClusterEnvironmentAndCleansClusterOnSuccess()
    {
        using var fixture = new NativeSmokeFixture();
        var environment = CreateEnvironment();
        var originalEnvironment = environment.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

        await RunSmokeAsync(fixture, environment);

        var cluster = Assert.IsType<DurableTemplateNativePostgreSql>(fixture.StartedCluster);
        var request = Assert.Single(fixture.Runner.Requests, item => item.OperationName == "native-smoke");
        Assert.Equal("dotnet", request.FileName);
        Assert.Equal("native-smoke", request.OperationName);
        Assert.Equal(
            ["test", Path.Join(fixture.Root, "generated-tests.csproj"), "--no-build", "--no-restore", "--filter",
                "FullyQualifiedName~NativePostgreSqlSmoke", "--logger", "console;verbosity=detailed"],
            request.Arguments);
        Assert.Equal(ExternalCapturePolicy.ReleaseProof, request.CapturePolicy);
        Assert.Equal(fixture.BinDirectory, request.Environment!["APPSURFACE_TEMPLATE_NATIVE_PG_BIN"]);
        Assert.StartsWith("Host=127.0.0.1;Port=54321;Database=postgres;Username=postgres;Password=",
            request.Environment["APPSURFACE_TEMPLATE_NATIVE_ADMIN_CONNECTION"], StringComparison.Ordinal);
        var setupRemaining = int.Parse(request.Environment["APPSURFACE_TEMPLATE_NATIVE_SETUP_REMAINING_MS"]!, CultureInfo.InvariantCulture);
        Assert.InRange(setupRemaining,
            1, DurableTemplateNativePostgreSql.SetupTimeoutMilliseconds);
        Assert.Equal(setupRemaining + 35_000, request.TimeoutMilliseconds);
        Assert.Equal("50000", request.Environment["APPSURFACE_TEMPLATE_NATIVE_PORT_MIN"]);
        Assert.Equal("60000", request.Environment["APPSURFACE_TEMPLATE_NATIVE_PORT_MAX"]);
        Assert.Equal("caller-owned", request.Environment["APPSURFACE_TEMPLATE_NATIVE_ADMIN_CONNECTION_OVERRIDE"]);
        Assert.Equal("preserved", request.Environment["KEEP_FOR_CONSUMER"]);
        Assert.Equal(cluster.ToolIdentity, fixture.ReceivedIdentity);
        Assert.Equal(originalEnvironment, environment);
        Assert.Equal(19_750, Assert.Single(fixture.CleanupBudgets));
        AssertClusterWasCleaned(fixture, cluster);
    }

    [Theory]
    [InlineData(1, false, false)]
    [InlineData(0, true, false)]
    [InlineData(0, false, true)]
    public async Task RejectsFailedOrTruncatedSmokeAndCleansCluster(
        int exitCode,
        bool standardOutputTruncated,
        bool standardErrorTruncated)
    {
        using var fixture = new NativeSmokeFixture
        {
            SmokeResult = new ExternalCommandResult(exitCode, ValidSmokeOutput, "bounded child error",
                standardOutputTruncated, standardErrorTruncated)
        };

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => RunSmokeAsync(fixture, CreateEnvironment()));

        Assert.Contains("failed or its bounded capture was truncated", error.Message, StringComparison.Ordinal);
        Assert.Equal(19_750, Assert.Single(fixture.CleanupBudgets));
        AssertClusterWasCleaned(fixture, Assert.IsType<DurableTemplateNativePostgreSql>(fixture.StartedCluster));
    }

    [Fact]
    public async Task RejectsMissingSuccessMarkerAndCleansCluster()
    {
        using var fixture = new NativeSmokeFixture
        {
            SmokeResult = new ExternalCommandResult(0, $"{CleanupMarker}\n", string.Empty)
        };

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => RunSmokeAsync(fixture, CreateEnvironment()));

        Assert.Contains("ordinary-startup assertion checkpoint is missing", error.Message, StringComparison.Ordinal);
        Assert.Equal(19_750, Assert.Single(fixture.CleanupBudgets));
        AssertClusterWasCleaned(fixture, Assert.IsType<DurableTemplateNativePostgreSql>(fixture.StartedCluster));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("exhausted")]
    [InlineData("over-limit")]
    [InlineData("overflow")]
    [InlineData("malformed")]
    public async Task RejectsInvalidCleanupMarkerAndCleansCluster(string markerCase)
    {
        var output = markerCase switch
        {
            "missing" => $"{SmokeMarker}\n",
            "duplicate" => $"{CleanupMarker}\n{CleanupMarker}\n{SmokeMarker}\n",
            "exhausted" => "[native-cleanup] elapsed-ms=20000\n" + SmokeMarker + "\n",
            "over-limit" => "[native-cleanup] elapsed-ms=20001\n" + SmokeMarker + "\n",
            "overflow" => "[native-cleanup] elapsed-ms=9999999999999\n" + SmokeMarker + "\n",
            "malformed" => "[native-cleanup] elapsed-ms=not-a-number\n" + SmokeMarker + "\n",
            _ => throw new ArgumentOutOfRangeException(nameof(markerCase))
        };
        using var fixture = new NativeSmokeFixture
        {
            SmokeResult = new ExternalCommandResult(0, output, string.Empty)
        };

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => RunSmokeAsync(fixture, CreateEnvironment()));

        Assert.Contains("cleanup observation is missing, ambiguous or exhausted", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, Assert.Single(fixture.CleanupBudgets));
        AssertClusterWasCleaned(fixture, Assert.IsType<DurableTemplateNativePostgreSql>(fixture.StartedCluster));
    }

    [Fact]
    public async Task CleansClusterWhenSmokeRunnerThrows()
    {
        using var fixture = new NativeSmokeFixture
        {
            SmokeException = new InvalidOperationException("private runner detail")
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => RunSmokeAsync(fixture, CreateEnvironment()));

        Assert.Equal("private runner detail", error.Message);
        Assert.Equal(20_000, Assert.Single(fixture.CleanupBudgets));
        AssertClusterWasCleaned(fixture, Assert.IsType<DurableTemplateNativePostgreSql>(fixture.StartedCluster));
    }

    [Theory]
    [InlineData(0, 20_000)]
    [InlineData(19_999, 1)]
    public async Task PassesOnlyObservedRemainderToClusterDisposal(int elapsed, int expectedBudget)
    {
        using var fixture = new NativeSmokeFixture
        {
            SmokeResult = new ExternalCommandResult(0,
                $"[native-cleanup] elapsed-ms={elapsed}\n{SmokeMarker}\n", string.Empty)
        };

        await RunSmokeAsync(fixture, CreateEnvironment());

        Assert.Equal(expectedBudget, Assert.Single(fixture.CleanupBudgets));
        AssertClusterWasCleaned(fixture, Assert.IsType<DurableTemplateNativePostgreSql>(fixture.StartedCluster));
    }

    [Fact]
    public async Task AttemptsClusterCleanupWhenSmokeRunnerCancelsBeforeReturningAResult()
    {
        using var fixture = new NativeSmokeFixture { SmokeException = new OperationCanceledException() };

        await Assert.ThrowsAsync<OperationCanceledException>(() => RunSmokeAsync(fixture, CreateEnvironment()));

        Assert.Equal(20_000, Assert.Single(fixture.CleanupBudgets));
        AssertClusterWasCleaned(fixture, Assert.IsType<DurableTemplateNativePostgreSql>(fixture.StartedCluster));
    }

    [Fact]
    public async Task DefaultDisposerStopsAndRemovesTheOwnedCluster()
    {
        using var fixture = new NativeSmokeFixture();

        await RunSmokeAsync(fixture, CreateEnvironment(), observeCleanup: false);

        Assert.Empty(fixture.CleanupBudgets);
        AssertClusterWasCleaned(fixture, Assert.IsType<DurableTemplateNativePostgreSql>(fixture.StartedCluster));
    }

    [Fact]
    public async Task CleanupFailureCannotCompleteAnOtherwiseSuccessfulSmoke()
    {
        using var fixture = new NativeSmokeFixture
        {
            CleanupException = new PackageIndexException("owned cluster cleanup was not verified")
        };

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => RunSmokeAsync(fixture, CreateEnvironment()));

        Assert.Equal("owned cluster cleanup was not verified", error.Message);
        Assert.Equal(19_750, Assert.Single(fixture.CleanupBudgets));
        AssertClusterWasCleaned(fixture, Assert.IsType<DurableTemplateNativePostgreSql>(fixture.StartedCluster));
    }

    private static string ValidSmokeOutput => $"{CleanupMarker}\n{SmokeMarker}\n";

    private static Dictionary<string, string?> CreateEnvironment() => new(StringComparer.Ordinal)
    {
        ["KEEP_FOR_CONSUMER"] = "preserved",
        ["APPSURFACE_TEMPLATE_NATIVE_ADMIN_CONNECTION"] = "caller-owned",
        ["APPSURFACE_TEMPLATE_NATIVE_ADMIN_CONNECTION_OVERRIDE"] = "caller-owned"
    };

    private static Task RunSmokeAsync(NativeSmokeFixture fixture, IReadOnlyDictionary<string, string?> environment,
        bool observeCleanup = true)
    {
        fixture.Runner.SmokeResult = fixture.SmokeResult;
        fixture.Runner.SmokeException = fixture.SmokeException;
        return DurableTemplateCommand.RunNativeSmokeAsync(
            fixture.BinDirectory,
            Path.Join(fixture.Root, "generated-tests.csproj"),
            environment,
            fixture.Runner,
            identity => fixture.ReceivedIdentity = identity,
            CancellationToken.None,
            fixture.StartClusterAsync,
            observeCleanup ? fixture.DisposeClusterAsync : null);
    }

    private static void AssertClusterWasCleaned(
        NativeSmokeFixture fixture,
        DurableTemplateNativePostgreSql cluster)
    {
        Assert.Contains(fixture.Runner.Requests, request => Path.GetFileNameWithoutExtension(request.FileName) == "pg_ctl"
            && request.Arguments.LastOrDefault() == "stop");
        Assert.False(Directory.Exists(cluster.OwnedRoot));
        Assert.Throws<ObjectDisposedException>(() => { _ = cluster.ConnectionString; });
    }

    private sealed class NativeSmokeFixture : IDisposable
    {
        private readonly string _root = Path.Join(Path.GetTempPath(), "appsurface-native-smoke-command-" + Guid.NewGuid().ToString("N"));

        internal NativeSmokeFixture()
        {
            Root = _root;
            BinDirectory = Path.Join(Root, "bin");
            Directory.CreateDirectory(BinDirectory);
            foreach (var tool in new[] { "initdb", "postgres", "pg_ctl", "psql" })
            {
                File.WriteAllText(Path.Join(BinDirectory, tool + (OperatingSystem.IsWindows() ? ".exe" : string.Empty)), tool);
            }
            Runner = new FakeCommandRunner(Runtime);
            Bootstrap = new FakeBootstrapRunner(Runtime);
        }

        internal string Root { get; }
        internal string BinDirectory { get; }
        internal FakeRuntime Runtime { get; } = new();
        internal FakeCommandRunner Runner { get; }
        internal FakeBootstrapRunner Bootstrap { get; }
        internal ExternalCommandResult SmokeResult { get; init; } = new(0, ValidSmokeOutput, string.Empty);
        internal Exception? SmokeException { get; init; }
        internal Exception? CleanupException { get; init; }
        internal DurableTemplateNativePostgreSql? StartedCluster { get; private set; }
        internal NativePostgreSqlToolIdentity? ReceivedIdentity { get; set; }
        internal List<int> CleanupBudgets { get; } = [];

        internal async ValueTask DisposeClusterAsync(DurableTemplateNativePostgreSql cluster, int remainingMilliseconds)
        {
            Assert.Same(StartedCluster, cluster);
            CleanupBudgets.Add(remainingMilliseconds);
            // This simulated disposer records the command allocation independently of filesystem scheduling.
            // The fake cluster's real teardown is separately checked by AssertClusterWasCleaned.
            await cluster.DisposeWithBudgetAsync(1_000);
            if (CleanupException is not null) throw CleanupException;
        }

        internal async Task<DurableTemplateNativePostgreSql> StartClusterAsync(
            string ownedRoot,
            IExternalCommandRunner runner,
            CancellationToken cancellationToken)
        {
            Assert.Same(Runner, runner);
            StartedCluster = await DurableTemplateNativePostgreSql.StartAsync(
                ownedRoot,
                runner,
                cancellationToken,
                new FixedToolDirectoryResolver(BinDirectory),
                Bootstrap,
                Runtime,
                new NativePostgreSqlBudgets(5_000, 1_000));
            return StartedCluster;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed class FixedToolDirectoryResolver(string binDirectory) : INativePostgreSqlToolDirectoryResolver
    {
        public Task<string> ResolveAsync(
            IExternalCommandRunner commandRunner,
            int timeoutMilliseconds,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Path.GetFullPath(binDirectory));
        }
    }

    private sealed class FakeBootstrapRunner(FakeRuntime runtime) : INativePostgreSqlBootstrapRunner
    {
        public Task<NativePostgreSqlBootstrapResult> RunAsync(
            NativePostgreSqlBootstrapRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dataDirectory = request.Arguments[1];
            runtime.BootstrapPassword = request.StandardInput.Split(Environment.NewLine,
                StringSplitOptions.RemoveEmptyEntries)[0];
            Directory.CreateDirectory(dataDirectory);
            File.WriteAllText(Path.Join(dataDirectory, "PG_VERSION"), "16\n");
            File.WriteAllText(Path.Join(dataDirectory, "postgresql.auto.conf"),
                "# initdb baseline owned by PostgreSQL\n# settings added by initdb\n\n");
            return Task.FromResult(new NativePostgreSqlBootstrapResult(0, TimedOut: false));
        }
    }

    private sealed class FakeCommandRunner(FakeRuntime runtime) : IExternalCommandRunner
    {
        internal List<ExternalCommandRequest> Requests { get; } = [];
        internal ExternalCommandResult SmokeResult { get; set; } = new(0, ValidSmokeOutput, string.Empty);
        internal Exception? SmokeException { get; set; }

        public Task<ExternalCommandResult> RunAsync(ExternalCommandRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            if (request.OperationName == "native-smoke")
            {
                if (SmokeException is not null)
                {
                    throw SmokeException;
                }
                return Task.FromResult(SmokeResult);
            }

            var name = Path.GetFileNameWithoutExtension(request.FileName);
            if (request.Arguments.SequenceEqual(["--version"]))
            {
                return Task.FromResult(new ExternalCommandResult(0, $"{name} (PostgreSQL) 16.5\n", string.Empty));
            }
            if (name == "pg_ctl" && request.Arguments.LastOrDefault() == "start")
            {
                var logArgument = Array.IndexOf(request.Arguments.ToArray(), "-l");
                File.WriteAllText(request.Arguments[logArgument + 1], "bounded fake PostgreSQL startup log");
                runtime.ServerRunning = true;
                return Task.FromResult(new ExternalCommandResult(0, string.Empty, string.Empty));
            }
            if (name == "pg_ctl" && request.Arguments.LastOrDefault() == "stop")
            {
                runtime.ServerRunning = false;
                return Task.FromResult(new ExternalCommandResult(0, string.Empty, string.Empty));
            }
            if (name == "psql")
            {
                var command = request.Arguments[Array.IndexOf(request.Arguments.ToArray(), "-c") + 1];
                if (command == "SELECT 1")
                {
                    var password = request.Environment!["PGPASSWORD"];
                    return Task.FromResult(password == runtime.BootstrapPassword
                        ? new ExternalCommandResult(0, "1\n", string.Empty)
                        : new ExternalCommandResult(2, string.Empty, "authentication failed"));
                }
                return Task.FromResult(new ExternalCommandResult(0, "16.5|160005\n", string.Empty));
            }

            return Task.FromResult(new ExternalCommandResult(1, string.Empty, "unexpected fake command"));
        }
    }

    private sealed class FakeRuntime : INativePostgreSqlRuntime
    {
        private static readonly NativePostgreSqlProcessIdentity ProcessIdentity = new(12345, 1_800_000_000);

        internal string? BootstrapPassword { get; set; }
        internal bool ServerRunning { get; set; }
        public bool IsRunningAsRoot => false;
        public bool HasControllingTerminal => false;
        public int ReserveLoopbackPort() => 54_321;
        public bool IsLoopbackPortListening(int port) => ServerRunning;
        public NativePostgreSqlProcessIdentity? FindOwnedPostmaster(string dataDirectory, string postgresPath, int port)
            => ServerRunning ? ProcessIdentity : null;
        public bool IsSameProcessAlive(NativePostgreSqlProcessIdentity identity, string postgresPath) => ServerRunning;
    }
}
