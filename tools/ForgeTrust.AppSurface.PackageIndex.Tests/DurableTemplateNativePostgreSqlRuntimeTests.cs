using System.Diagnostics;

namespace ForgeTrust.AppSurface.PackageIndex.Tests;

public sealed class DurableTemplateNativePostgreSqlRuntimeTests
{
    [Fact]
    public void FindOwnedPostmasterReturnsNullWhenPidFileIsAbsent()
    {
        using var directory = new PrivateTestDirectory();
        var dataDirectory = Path.Join(directory.Root, "data");
        Directory.CreateDirectory(dataDirectory);

        var process = NativePostgreSqlRuntime.Instance.FindOwnedPostmaster(dataDirectory, "unused-postgres", 5432);

        Assert.Null(process);
    }

    [Fact]
    public void FindOwnedPostmasterRefusesSymlinkedPidFileBeforeReadingItsTarget()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Join(Path.GetTempPath(), $"apg-native-runtime-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var dataDirectory = Path.Join(root, "data");
            Directory.CreateDirectory(dataDirectory);
            var targetPath = Path.Join(root, "outside-postmaster.pid");
            File.WriteAllText(targetPath, "caller-owned target");
            File.CreateSymbolicLink(Path.Join(dataDirectory, "postmaster.pid"), targetPath);

            var error = Assert.Throws<PackageIndexException>(() =>
                NativePostgreSqlRuntime.Instance.FindOwnedPostmaster(dataDirectory, "unused-postgres", 5432));

            Assert.Contains("reparse point", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("caller-owned target", File.ReadAllText(targetPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FindOwnedPostmasterRejectsOversizedPidFileBeforeReadingItsContents()
    {
        using var directory = new PrivateTestDirectory();
        var dataDirectory = Path.Join(directory.Root, "data");
        Directory.CreateDirectory(dataDirectory);
        File.WriteAllText(Path.Join(dataDirectory, "postmaster.pid"), new string('x', 4097));

        var error = Assert.Throws<PackageIndexException>(() =>
            NativePostgreSqlRuntime.Instance.FindOwnedPostmaster(dataDirectory, "unused-postgres", 5432));

        Assert.Contains("safe bound", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FindOwnedPostmasterRejectsTruncatedPidFile()
    {
        using var directory = new PrivateTestDirectory();
        var dataDirectory = Path.Join(directory.Root, "data");
        Directory.CreateDirectory(dataDirectory);
        File.WriteAllText(Path.Join(dataDirectory, "postmaster.pid"), "12345\n");

        var error = Assert.Throws<PackageIndexException>(() =>
            NativePostgreSqlRuntime.Instance.FindOwnedPostmaster(dataDirectory, "unused-postgres", 5432));

        Assert.Contains("identity did not match", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FindOwnedPostmasterRejectsNonPositiveProcessId()
    {
        var root = Path.Join(Path.GetTempPath(), $"apg-native-runtime-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var dataDirectory = Path.Join(root, "data");
            Directory.CreateDirectory(dataDirectory);
            File.WriteAllText(
                Path.Join(dataDirectory, "postmaster.pid"),
                $"0{Environment.NewLine}{dataDirectory}{Environment.NewLine}1800000000{Environment.NewLine}5432{Environment.NewLine}/var/run/postgresql{Environment.NewLine}*{Environment.NewLine}");

            var error = Assert.Throws<PackageIndexException>(() =>
                NativePostgreSqlRuntime.Instance.FindOwnedPostmaster(dataDirectory, "unused-postgres", 5432));

            Assert.Contains("identity did not match", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("different-data-directory", 5432)]
    [InlineData("expected-data-directory", 5433)]
    public void FindOwnedPostmasterRejectsPidFileForDifferentCluster(string recordedDirectory, int recordedPort)
    {
        using var directory = new PrivateTestDirectory();
        var dataDirectory = Path.Join(directory.Root, "data");
        Directory.CreateDirectory(dataDirectory);
        var pidDataDirectory = recordedDirectory == "expected-data-directory"
            ? dataDirectory
            : Path.Join(directory.Root, recordedDirectory);
        WritePostmasterPid(
            Path.Join(dataDirectory, "postmaster.pid"),
            processId: 12345,
            pidDataDirectory,
            startTime: 1_800_000_000,
            recordedPort);

        var error = Assert.Throws<PackageIndexException>(() =>
            NativePostgreSqlRuntime.Instance.FindOwnedPostmaster(dataDirectory, "unused-postgres", 5432));

        Assert.Contains("identity did not match", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FindOwnedPostmasterParsesCompleteIdentityThenRejectsLiveNonPostgresProcess()
    {
        using var directory = new PrivateTestDirectory();
        var dataDirectory = Path.Join(directory.Root, "data");
        Directory.CreateDirectory(dataDirectory);
        using var process = Process.GetCurrentProcess();
        Assert.NotEqual("postgres", process.ProcessName, StringComparer.OrdinalIgnoreCase);
        var startTime = new DateTimeOffset(process.StartTime.ToUniversalTime()).ToUnixTimeSeconds();
        WritePostmasterPid(
            Path.Join(dataDirectory, "postmaster.pid"),
            process.Id,
            dataDirectory,
            startTime,
            5432);

        var identity = NativePostgreSqlRuntime.Instance.FindOwnedPostmaster(dataDirectory, "unused-postgres", 5432);

        Assert.Null(identity);
    }

    [Fact]
    public void IsSameProcessAliveRejectsLiveProcessThatIsNotPostgres()
    {
        using var process = Process.GetCurrentProcess();
        Assert.NotEqual("postgres", process.ProcessName, StringComparer.OrdinalIgnoreCase);

        var identity = new NativePostgreSqlProcessIdentity(
            process.Id,
            new DateTimeOffset(process.StartTime.ToUniversalTime()).ToUnixTimeSeconds());

        Assert.False(NativePostgreSqlRuntime.Instance.IsSameProcessAlive(identity, "unused-postgres"));
    }

    [Fact]
    public async Task StartRejectsMissingResolvedToolDirectoryBeforeCreatingOwnedRoot()
    {
        using var fixture = new ToolValidationFixture();
        var missingDirectory = Path.Join(fixture.Root, "missing-bin");

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => fixture.StartAsync(
            resolver: new FixedNativePostgreSqlToolDirectoryResolver(missingDirectory)));

        Assert.Contains("tool directory", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(fixture.OwnedRoot));
        Assert.Empty(fixture.Runner.Requests);
        Assert.Null(fixture.Bootstrap.Request);
    }

    [Fact]
    public async Task StartRejectsRelativeResolvedToolDirectoryBeforeCreatingOwnedRoot()
    {
        using var fixture = new ToolValidationFixture();

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => fixture.StartAsync(
            resolver: new ResultToolDirectoryResolver("relative-bin")));

        Assert.Contains("absolute resolved path", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fixture.Runner.Requests);
        Assert.False(Directory.Exists(fixture.OwnedRoot));
        Assert.Null(fixture.Bootstrap.Request);
    }

    [Fact]
    public async Task StartSuppressesVersionRunnerFailureBeforeCreatingOwnedRoot()
    {
        using var fixture = new ToolValidationFixture();
        fixture.Runner.Handler = (request, _) =>
        {
            if (request.OperationName == "initdb --version")
            {
                throw new InvalidOperationException("private version output");
            }

            return Task.FromResult(ToolValidationFixture.ValidVersionResult(request));
        };

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => fixture.StartAsync());

        Assert.Contains("initdb", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private version output", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(fixture.OwnedRoot));
        Assert.Null(fixture.Bootstrap.Request);
    }

    [Fact]
    public async Task MacToolResolverSuppressesBrewRunnerFailure()
    {
        using var fixture = new ToolValidationFixture();
        fixture.Runner.Handler = (_, _) => throw new InvalidOperationException("private brew output");

        var error = await Assert.ThrowsAsync<PackageIndexException>(() =>
            new NativePostgreSqlToolDirectoryResolver(NativePostgreSqlHostPlatform.MacOS)
                .ResolveAsync(fixture.Runner, 1_000, CancellationToken.None));

        Assert.Contains("Homebrew", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private brew output", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MacToolResolverPropagatesBrewCancellation()
    {
        using var fixture = new ToolValidationFixture();
        fixture.Runner.Handler = (_, _) => throw new OperationCanceledException("private cancellation detail");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new NativePostgreSqlToolDirectoryResolver(NativePostgreSqlHostPlatform.MacOS)
                .ResolveAsync(fixture.Runner, 1_000, CancellationToken.None));
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("truncated")]
    [InlineData("relative")]
    [InlineData("missing")]
    public async Task MacToolResolverRejectsUnusableBrewPrefix(string failure)
    {
        using var fixture = new ToolValidationFixture();
        fixture.Runner.Handler = (_, _) => Task.FromResult(failure switch
        {
            "failed" => new ExternalCommandResult(1, string.Empty, "private brew output"),
            "truncated" => new ExternalCommandResult(
                0,
                Path.Join(fixture.Root, "brew-keg"),
                string.Empty,
                StandardOutputTruncated: true),
            "relative" => new ExternalCommandResult(0, "relative-keg", string.Empty),
            _ => new ExternalCommandResult(0, Path.Join(fixture.Root, "missing-keg"), string.Empty)
        });

        var error = await Assert.ThrowsAsync<PackageIndexException>(() =>
            new NativePostgreSqlToolDirectoryResolver(NativePostgreSqlHostPlatform.MacOS)
                .ResolveAsync(fixture.Runner, 1_000, CancellationToken.None));

        Assert.DoesNotContain("private brew output", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("truncated")]
    [InlineData("malformed")]
    public async Task StartRejectsUnusableVersionEvidenceBeforeCreatingOwnedRoot(string failure)
    {
        using var fixture = new ToolValidationFixture();
        fixture.Runner.Handler = (request, _) =>
        {
            if (request.OperationName != "initdb --version")
            {
                return Task.FromResult(ToolValidationFixture.ValidVersionResult(request));
            }

            return Task.FromResult(failure switch
            {
                "failed" => new ExternalCommandResult(1, "initdb (PostgreSQL) 16.5\n", "private version output"),
                "truncated" => new ExternalCommandResult(
                    0,
                    "initdb (PostgreSQL) 16.5\n",
                    string.Empty,
                    StandardOutputTruncated: true),
                _ => new ExternalCommandResult(0, "private malformed version output", string.Empty)
            });
        };

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => fixture.StartAsync());

        Assert.DoesNotContain("private", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(fixture.OwnedRoot));
        Assert.Null(fixture.Bootstrap.Request);
    }

    [Fact]
    public async Task StartDetectsToolReplacementDuringVersionVerificationBeforeCreatingOwnedRoot()
    {
        using var fixture = new ToolValidationFixture();
        fixture.Runner.Handler = (request, _) =>
        {
            if (request.OperationName == "initdb --version")
            {
                File.WriteAllText(request.FileName, "replaced after hashing");
            }

            return Task.FromResult(ToolValidationFixture.ValidVersionResult(request));
        };

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => fixture.StartAsync());

        Assert.Contains("changed", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(fixture.OwnedRoot));
        Assert.Null(fixture.Bootstrap.Request);
    }

    [Fact]
    public async Task StartPropagatesToolProbeCancellationWithoutCreatingOwnedRoot()
    {
        using var fixture = new ToolValidationFixture();
        fixture.Runner.Handler = (_, _) => throw new OperationCanceledException("private cancellation detail");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.StartAsync());

        Assert.False(Directory.Exists(fixture.OwnedRoot));
        Assert.Null(fixture.Bootstrap.Request);
    }

    [Fact]
    public async Task StartRetainsOwnedRootWhenBootstrapTerminationCannotBeVerified()
    {
        using var fixture = new ToolValidationFixture();
        fixture.Bootstrap.FailTerminationVerification = true;

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => fixture.StartAsync());

        Assert.Contains("retained", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(fixture.OwnedRoot));
        Assert.DoesNotContain(fixture.Runner.Requests, request => request.OperationName == "pg_ctl start");
        Assert.DoesNotContain(fixture.Runner.Requests, request => request.OperationName == "pg_ctl stop");
    }

    [Fact]
    public async Task StartCleansOwnedRootWhenBootstrapIsCancelled()
    {
        using var fixture = new ToolValidationFixture();
        fixture.Bootstrap.Cancel = true;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.StartAsync());

        Assert.NotNull(fixture.Bootstrap.Request);
        Assert.False(Directory.Exists(fixture.OwnedRoot));
        Assert.DoesNotContain(fixture.Runner.Requests, request => request.OperationName == "pg_ctl start");
    }

    [Fact]
    public async Task StartPreservesFileAtRequestedOwnedRoot()
    {
        using var fixture = new ToolValidationFixture();
        File.WriteAllText(fixture.OwnedRoot, "caller-owned data");

        await Assert.ThrowsAsync<PackageIndexException>(() => fixture.StartAsync());

        Assert.Equal("caller-owned data", File.ReadAllText(fixture.OwnedRoot));
        Assert.Null(fixture.Bootstrap.Request);
    }

    private static void WritePostmasterPid(
        string path,
        int processId,
        string dataDirectory,
        long startTime,
        int port)
    {
        File.WriteAllText(
            path,
            string.Join(
                Environment.NewLine,
                processId,
                dataDirectory,
                startTime,
                port,
                "/var/run/postgresql",
                "*",
                string.Empty));
    }

    private sealed class PrivateTestDirectory : IDisposable
    {
        internal PrivateTestDirectory()
        {
            var temporary = OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetFullPath(Path.GetTempPath());
            Root = Path.Join(temporary, $"apg-native-runtime-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        internal string Root { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed class ToolValidationFixture : IDisposable
    {
        internal ToolValidationFixture()
        {
            _directory = new PrivateTestDirectory();
            Root = _directory.Root;
            BinDirectory = Path.Join(Root, "bin");
            Directory.CreateDirectory(BinDirectory);
            foreach (var tool in new[] { "initdb", "postgres", "pg_ctl", "psql" })
            {
                File.WriteAllText(Path.Join(BinDirectory, tool + (OperatingSystem.IsWindows() ? ".exe" : string.Empty)), tool);
            }

            OwnedRoot = Path.Join(Root, "cluster");
            Runner = new ToolValidationCommandRunner();
            Bootstrap = new ToolValidationBootstrapRunner();
        }

        private readonly PrivateTestDirectory _directory;

        internal string Root { get; }
        internal string BinDirectory { get; }
        internal string OwnedRoot { get; }
        internal ToolValidationCommandRunner Runner { get; }
        internal ToolValidationBootstrapRunner Bootstrap { get; }

        internal Task<DurableTemplateNativePostgreSql> StartAsync(
            string? ownedRoot = null,
            INativePostgreSqlToolDirectoryResolver? resolver = null)
            => DurableTemplateNativePostgreSql.StartAsync(
                ownedRoot ?? OwnedRoot,
                Runner,
                toolDirectoryResolver: resolver ?? new FixedNativePostgreSqlToolDirectoryResolver(BinDirectory),
                bootstrapRunner: Bootstrap,
                runtime: new InertNativePostgreSqlRuntime(),
                budgets: new NativePostgreSqlBudgets(5_000, 1_000));

        internal static ExternalCommandResult ValidVersionResult(ExternalCommandRequest request)
        {
            var toolName = Path.GetFileNameWithoutExtension(request.FileName);
            return new ExternalCommandResult(0, $"{toolName} (PostgreSQL) 16.5\n", string.Empty);
        }

        public void Dispose() => _directory.Dispose();
    }

    private sealed class ToolValidationCommandRunner : IExternalCommandRunner
    {
        internal List<ExternalCommandRequest> Requests { get; } = [];
        internal Func<ExternalCommandRequest, CancellationToken, Task<ExternalCommandResult>>? Handler { get; set; }

        public Task<ExternalCommandResult> RunAsync(ExternalCommandRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Handler is null
                ? Task.FromResult(ToolValidationFixture.ValidVersionResult(request))
                : Handler(request, cancellationToken);
        }
    }

    private sealed class ToolValidationBootstrapRunner : INativePostgreSqlBootstrapRunner
    {
        internal NativePostgreSqlBootstrapRequest? Request { get; private set; }
        internal bool Cancel { get; set; }
        internal bool FailTerminationVerification { get; set; }

        public Task<NativePostgreSqlBootstrapResult> RunAsync(
            NativePostgreSqlBootstrapRequest request,
            CancellationToken cancellationToken)
        {
            Request = request;
            if (Cancel)
            {
                throw new OperationCanceledException("private bootstrap cancellation detail");
            }
            if (FailTerminationVerification)
            {
                throw new NativePostgreSqlBootstrapTerminationException();
            }

            throw new InvalidOperationException("The tool-validation fixture must fail before initdb.");
        }
    }

    private sealed class ResultToolDirectoryResolver(string result) : INativePostgreSqlToolDirectoryResolver
    {
        public Task<string> ResolveAsync(
            IExternalCommandRunner commandRunner,
            int timeoutMilliseconds,
            CancellationToken cancellationToken)
            => Task.FromResult(result);
    }

    private sealed class InertNativePostgreSqlRuntime : INativePostgreSqlRuntime
    {
        public bool IsRunningAsRoot => false;
        public bool HasControllingTerminal => false;
        public int ReserveLoopbackPort() => 54321;
        public bool IsLoopbackPortListening(int port) => false;
        public NativePostgreSqlProcessIdentity? FindOwnedPostmaster(string dataDirectory, string postgresPath, int port) => null;
        public bool IsSameProcessAlive(NativePostgreSqlProcessIdentity identity, string postgresPath) => false;
    }
}
