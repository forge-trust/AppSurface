using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ForgeTrust.AppSurface.PackageIndex;

/// <summary>
/// Owns one private native PostgreSQL cluster for the generated Durable template's ordinary local-run smoke.
/// </summary>
/// <remarks>
/// The caller must provide an absent, unique root under a private runner temporary directory. The returned
/// connection string contains a generated bootstrap secret and is intended only for the local provisioning test.
/// Disposal stops the owned server and removes its root only after the postmaster and selected port are verified
/// stopped. Raw child output and PostgreSQL logs are never included in diagnostics.
/// </remarks>
internal sealed class DurableTemplateNativePostgreSql : IAsyncDisposable
{
    internal const int ToolAcquisitionTimeoutMilliseconds = 300_000;
    internal const int SetupTimeoutMilliseconds = 90_000;
    internal const int CleanupTimeoutMilliseconds = 20_000;

    private const int CapturedOutputLimitBytes = 16 * 1024;
    private const int MaximumOwnedLogBytes = 1024 * 1024;
    private const int ConservativeUnixSocketPathMaximumBytes = 103;
    private const int CleanupStopAttemptTimeoutMilliseconds = 4_000;
    private const string BootstrapUser = "postgres";
    private const string BootstrapDatabase = "postgres";
    private static readonly Regex _versionPattern = new(
        @"PostgreSQL\)?\s+(?<version>\d+\.\d+(?:\.\d+)?)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly IExternalCommandRunner _commandRunner;
    private readonly INativePostgreSqlRuntime _runtime;
    private readonly SemaphoreSlim _disposeLock = new(1, 1);
    private readonly string _rootDirectory;
    private readonly string _dataDirectory;
    private readonly string _serverLogPath;
    private readonly string _ownerMarkerPath;
    private readonly string _ownerToken;
    private readonly string _postgresPath;
    private readonly int _port;
    private readonly int _setupTimeoutMilliseconds;
    private readonly int _cleanupTimeoutMilliseconds;
    private Stopwatch? _setupClock;
    private string? _bootstrapPassword;
    private NativePostgreSqlProcessIdentity? _observedPostmaster;
    private bool _startCommandIssued;
    private bool _startCommandTimedOut;
    private bool _bootstrapAuthenticationSucceeded;
    private bool _bootstrapProcessTerminationVerified = true;
    private bool _disposed;

    private DurableTemplateNativePostgreSql(
        IExternalCommandRunner commandRunner,
        INativePostgreSqlRuntime runtime,
        string rootDirectory,
        string dataDirectory,
        string serverLogPath,
        string ownerMarkerPath,
        string ownerToken,
        int port,
        string bootstrapPassword,
        NativePostgreSqlToolIdentity toolIdentity,
        NativePostgreSqlBudgets budgets)
    {
        _commandRunner = commandRunner;
        _runtime = runtime;
        _rootDirectory = rootDirectory;
        _dataDirectory = dataDirectory;
        _serverLogPath = serverLogPath;
        _ownerMarkerPath = ownerMarkerPath;
        _ownerToken = ownerToken;
        _port = port;
        _setupTimeoutMilliseconds = budgets.SetupTimeoutMilliseconds;
        _cleanupTimeoutMilliseconds = budgets.CleanupTimeoutMilliseconds;
        _bootstrapPassword = bootstrapPassword;
        ToolIdentity = toolIdentity;
        _postgresPath = toolIdentity.PostgresPath;
    }

    /// <summary>Gets the secret-bearing bootstrap connection string while this owned cluster is running.</summary>
    internal string ConnectionString
    {
        get
        {
            var password = _bootstrapPassword;
            ObjectDisposedException.ThrowIf(password is null || _disposed, this);
            return $"Host=127.0.0.1;Port={_port};Database={BootstrapDatabase};Username={BootstrapUser};Password={password};SSL Mode=Disable;Timeout=5;Command Timeout=10;Pooling=false";
        }
    }

    /// <summary>Gets the absolute path to the validated psql binary.</summary>
    internal string PsqlPath => ToolIdentity.PsqlPath;

    /// <summary>Gets the resolved binary directory for the generated smoke child's native tool configuration.</summary>
    internal string BinDirectory => ToolIdentity.BinDirectory;

    /// <summary>Gets the bootstrap administrator connection string while the owned cluster is running.</summary>
    internal string AdminConnectionString => ConnectionString;

    /// <summary>Gets the remaining portion of the shared 90-second native setup budget in milliseconds.</summary>
    internal int SetupBudgetRemainingMilliseconds
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var clock = _setupClock ?? throw new InvalidOperationException("Native PostgreSQL setup has not started.");
            return Math.Max(0, _setupTimeoutMilliseconds - checked((int)Math.Min(clock.ElapsedMilliseconds, int.MaxValue)));
        }
    }

    /// <summary>Gets safe tool and server identity evidence with no credentials.</summary>
    internal NativePostgreSqlToolIdentity ToolIdentity { get; private set; }

    /// <summary>Gets the private owned root, for safe failure reports and ownership assertions.</summary>
    internal string OwnedRoot => _rootDirectory;

    /// <summary>
    /// Acquires the installed native toolchain, creates and starts a private SCRAM-authenticated cluster, and
    /// verifies that the generated bootstrap credential reaches the expected server.
    /// </summary>
    /// <param name="ownedRoot">Absent unique cluster root beneath a caller-owned private temporary directory.</param>
    /// <param name="commandRunner">Existing PackageIndex runner used for bounded tool/version and pg_ctl/psql calls.</param>
    /// <param name="cancellationToken">Cancellation token. Owned resources are cleaned before cancellation returns.</param>
    /// <param name="toolDirectoryResolver">Optional deterministic tool-directory seam for tests.</param>
    /// <param name="bootstrapRunner">Optional redirected-stdin initdb seam for tests.</param>
    /// <param name="runtime">Optional operating-system/process seam for tests.</param>
    /// <param name="budgets">Optional lower setup and cleanup limits for deterministic tests; production ceilings cannot be raised.</param>
    /// <returns>An owned running cluster. The caller must dispose it.</returns>
    /// <exception cref="PackageIndexException">Thrown when tools are missing/mixed/old, setup fails, or cleanup is uncertain.</exception>
    internal static async Task<DurableTemplateNativePostgreSql> StartAsync(
        string ownedRoot,
        IExternalCommandRunner commandRunner,
        CancellationToken cancellationToken = default,
        INativePostgreSqlToolDirectoryResolver? toolDirectoryResolver = null,
        INativePostgreSqlBootstrapRunner? bootstrapRunner = null,
        INativePostgreSqlRuntime? runtime = null,
        NativePostgreSqlBudgets? budgets = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownedRoot);
        ArgumentNullException.ThrowIfNull(commandRunner);

        var host = runtime ?? NativePostgreSqlRuntime.Instance;
        var timeouts = budgets ?? NativePostgreSqlBudgets.Default;
        timeouts.Validate();
        if (host.IsRunningAsRoot)
        {
            throw new PackageIndexException("Native PostgreSQL setup must run as the ordinary runner user, not root.");
        }

        try
        {
            var acquisitionClock = Stopwatch.StartNew();
            var resolver = toolDirectoryResolver ?? new NativePostgreSqlToolDirectoryResolver();
            var toolDirectory = await resolver.ResolveAsync(
                commandRunner,
                RemainingMilliseconds(acquisitionClock, ToolAcquisitionTimeoutMilliseconds, "tool acquisition"),
                cancellationToken);
            var toolIdentity = await ValidateToolsAsync(
                toolDirectory,
                commandRunner,
                acquisitionClock,
                cancellationToken);

            RequireFreshOwnedRoot(ownedRoot);
            var root = Path.GetFullPath(ownedRoot);
            var port = host.ReserveLoopbackPort();
            RequireUnixSocketPathWithinLimit(root, port);
            var password = CreateBootstrapPassword();
            var ownerToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var ownerMarkerPath = Path.Combine(root, ".appsurface-native-postgresql-owner");
            try
            {
                Directory.CreateDirectory(root);
                SetPrivateDirectoryMode(root);
                await WriteOwnerMarkerAsync(ownerMarkerPath, ownerToken, cancellationToken);
            }
            catch (Exception exception)
            {
                try
                {
                    CleanupUnstartedRoot(root, ownerMarkerPath, ownerToken);
                }
                catch (Exception)
                {
                    throw new PackageIndexException(
                        $"Native PostgreSQL could not verify cleanup of its partial root at '{root}'.");
                }

                if (exception is PackageIndexException packageException)
                {
                    throw packageException;
                }
                if (exception is OperationCanceledException)
                {
                    throw;
                }
                throw new PackageIndexException(
                    $"Native PostgreSQL could not create its private root ({exception.GetType().Name}); diagnostics were suppressed.");
            }

            var dataDirectory = Path.Combine(root, "data");
            var serverLogPath = Path.Combine(root, "pg_ctl.log");
            var cluster = new DurableTemplateNativePostgreSql(
                commandRunner,
                host,
                root,
                dataDirectory,
                serverLogPath,
                ownerMarkerPath,
                ownerToken,
                port,
                password,
                toolIdentity,
                timeouts);

            var setupClock = Stopwatch.StartNew();
            cluster._setupClock = setupClock;
            try
            {
                await cluster.InitializeAndStartAsync(
                    bootstrapRunner ?? new NativePostgreSqlBootstrapRunner(),
                    setupClock,
                    cancellationToken);
                return cluster;
            }
            catch (OperationCanceledException)
            {
                await cluster.CleanupAfterFailedStartAsync();
                throw;
            }
            catch
            {
                var failedPhase = cluster._failurePhase;
                try
                {
                    await cluster.CleanupAfterFailedStartAsync();
                }
                catch (PackageIndexException)
                {
                    throw new PackageIndexException(
                        $"Native PostgreSQL setup failed during {failedPhase}; stop could not be verified, so the owned root was retained at '{root}'.");
                }

                throw new PackageIndexException($"Native PostgreSQL setup failed during {failedPhase}; child diagnostics were suppressed.");
            }
        }
        catch (PackageIndexException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new PackageIndexException(
                $"Native PostgreSQL setup failed ({exception.GetType().Name}); child diagnostics were suppressed.");
        }
    }

    private string _failurePhase = "tool acquisition";

    /// <summary>Attempts a password-only psql probe without returning or retaining child diagnostics.</summary>
    /// <param name="password">Password to test; it is projected only into this psql child's environment.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True only when PostgreSQL accepts the password and returns the expected probe value.</returns>
    internal async Task<bool> TryAuthenticateAsync(string password, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        ObjectDisposedException.ThrowIf(_disposed, this);

        ExternalCommandResult result;
        try
        {
            result = await RunExternalAsync(
                ToolIdentity.PsqlPath,
                ["-w", "-X", "-A", "-t", "-h", "127.0.0.1", "-p", _port.ToString(CultureInfo.InvariantCulture), "-U", BootstrapUser, "-d", BootstrapDatabase, "-c", "SELECT 1"],
                "psql authentication probe",
                "checking native PostgreSQL authentication",
                5_000,
                password,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }

        return result.ExitCode == 0
            && !result.StandardOutputTruncated
            && string.Equals(result.StandardOutput.Trim(), "1", StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
        => DisposeWithBudgetAsync(_cleanupTimeoutMilliseconds);

    /// <summary>
    /// Stops and removes this cluster using a caller-shared cleanup allowance, capped by the fixture's 20-second
    /// maximum. Callers coordinating generated-host and cluster teardown should pass the time remaining from their
    /// single cleanup clock.
    /// </summary>
    /// <param name="cleanupBudgetRemainingMilliseconds">Remaining milliseconds in the caller's shared cleanup budget.</param>
    /// <returns>A task that completes after verified stop and owned-root removal.</returns>
    internal async ValueTask DisposeWithBudgetAsync(int cleanupBudgetRemainingMilliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cleanupBudgetRemainingMilliseconds);
        await _disposeLock.WaitAsync();
        try
        {
            if (_disposed)
            {
                return;
            }
            if (cleanupBudgetRemainingMilliseconds == 0)
            {
                throw new PackageIndexException(
                    $"Native PostgreSQL shared cleanup budget expired; the owned root was retained at '{_rootDirectory}'.");
            }

            await CleanupAsync(
                allowLateStart: false,
                Math.Min(cleanupBudgetRemainingMilliseconds, _cleanupTimeoutMilliseconds));
            _bootstrapPassword = null;
            _disposed = true;
        }
        finally
        {
            _disposeLock.Release();
        }
    }

    private async Task InitializeAndStartAsync(
        INativePostgreSqlBootstrapRunner bootstrapRunner,
        Stopwatch setupClock,
        CancellationToken cancellationToken)
    {
        _failurePhase = "runner terminal validation";
        if (!OperatingSystem.IsWindows() && _runtime.HasControllingTerminal)
        {
            throw new PackageIndexException("Native PostgreSQL initdb requires the runner process to have no controlling Unix terminal.");
        }

        _failurePhase = "initdb";
        var password = _bootstrapPassword ?? throw new InvalidOperationException("Bootstrap credential was already cleared.");
        var initDbRequest = new NativePostgreSqlBootstrapRequest(
            ToolIdentity.InitDbPath,
            ["-D", _dataDirectory, "--username=postgres", "--encoding=UTF8", "--locale=C", "--auth-local=scram-sha-256", "--auth-host=scram-sha-256", "--pwprompt"],
            _rootDirectory,
            $"{password}{Environment.NewLine}{password}{Environment.NewLine}",
            RemainingMilliseconds(setupClock, SetupTimeoutMilliseconds, "native cluster setup"));
        NativePostgreSqlBootstrapResult initResult;
        try
        {
            initResult = await bootstrapRunner.RunAsync(initDbRequest, cancellationToken);
        }
        catch (NativePostgreSqlBootstrapTerminationException)
        {
            _bootstrapProcessTerminationVerified = false;
            throw new PackageIndexException("Native PostgreSQL initdb process termination could not be verified; child diagnostics were suppressed.");
        }

        _bootstrapProcessTerminationVerified = initResult.ProcessTerminationVerified;
        if (!_bootstrapProcessTerminationVerified)
        {
            throw new PackageIndexException("Native PostgreSQL initdb process termination could not be verified; child diagnostics were suppressed.");
        }
        if (initResult.TimedOut)
        {
            throw new PackageIndexException("Native PostgreSQL initdb timed out; child diagnostics were suppressed.");
        }
        if (initResult.ExitCode != 0)
        {
            throw new PackageIndexException($"Native PostgreSQL initdb failed with exit code {initResult.ExitCode}; child diagnostics were suppressed.");
        }

        _failurePhase = "post-initdb validation and configuration";
        RequireOwnedRoot(_rootDirectory, _ownerMarkerPath, _ownerToken);
        RequireNoReparsePointsWithin(_rootDirectory);
        ConfigurePrivateCluster();

        _failurePhase = "loopback port check";
        if (_runtime.IsLoopbackPortListening(_port))
        {
            throw new PackageIndexException("Native PostgreSQL selected loopback port is already occupied.");
        }

        _failurePhase = "pg_ctl start";
        _startCommandIssued = true;
        var remaining = RemainingMilliseconds(setupClock, _setupTimeoutMilliseconds, "native cluster setup");
        var startResult = await RunExternalAsync(
            ToolIdentity.PgCtlPath,
            ["-D", _dataDirectory, "-l", _serverLogPath, "-w", "-t", Math.Max(1, remaining / 1000).ToString(CultureInfo.InvariantCulture), "start"],
            "pg_ctl start",
            "starting the private native PostgreSQL cluster",
            remaining,
            password: null,
            cancellationToken);
        _startCommandTimedOut = startResult.ExitCode == -1;
        if (startResult.ExitCode != 0)
        {
            throw new PackageIndexException(
                startResult.ExitCode == -1
                    ? "Native PostgreSQL pg_ctl start timed out; child diagnostics were suppressed."
                    : $"Native PostgreSQL pg_ctl start failed with exit code {startResult.ExitCode}; child diagnostics were suppressed.");
        }

        _failurePhase = "postmaster verification";
        NativePostgreSqlProcessIdentity process;
        try
        {
            process = await WaitForOwnedPostmasterAsync(setupClock, cancellationToken);
        }
        catch (PackageIndexException)
        {
            _startCommandTimedOut = true;
            throw;
        }
        _observedPostmaster = process;

        _failurePhase = "SCRAM authentication";
        if (!await TryAuthenticateAsync(password, cancellationToken))
        {
            throw new PackageIndexException("Native PostgreSQL rejected the generated bootstrap credential; child diagnostics were suppressed.");
        }
        _bootstrapAuthenticationSucceeded = true;

        _failurePhase = "server version verification";
        var serverVersionResult = await RunExternalAsync(
            ToolIdentity.PsqlPath,
            ["-w", "-X", "-A", "-t", "-h", "127.0.0.1", "-p", _port.ToString(CultureInfo.InvariantCulture), "-U", BootstrapUser, "-d", BootstrapDatabase, "-c", "SELECT current_setting('server_version') || '|' || current_setting('server_version_num')"],
            "psql server version probe",
            "verifying the native PostgreSQL server version",
            RemainingMilliseconds(setupClock, SetupTimeoutMilliseconds, "native cluster setup"),
            password,
            cancellationToken);
        if (serverVersionResult.ExitCode != 0 || serverVersionResult.StandardOutputTruncated)
        {
            throw new PackageIndexException("Native PostgreSQL server version probe failed; child diagnostics were suppressed.");
        }

        var serverVersion = ParseServerVersion(serverVersionResult.StandardOutput, ToolIdentity.Version);
        ToolIdentity = ToolIdentity with
        {
            ServerVersion = serverVersion.Version,
            ServerVersionNumber = serverVersion.VersionNumber
        };

        _failurePhase = "owned log bound";
        RequireOwnedLogsBounded();
    }

    private async Task<NativePostgreSqlProcessIdentity> WaitForOwnedPostmasterAsync(
        Stopwatch setupClock,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var remaining = RemainingMilliseconds(setupClock, _setupTimeoutMilliseconds, "native cluster setup");
            var process = _runtime.FindOwnedPostmaster(_dataDirectory, _postgresPath, _port);
            if (process is not null)
            {
                if (!_runtime.IsLoopbackPortListening(_port))
                {
                    await Task.Delay(Math.Min(100, remaining), cancellationToken);
                    continue;
                }

                return process;
            }

            await Task.Delay(Math.Min(100, remaining), cancellationToken);
        }
    }

    private void ConfigurePrivateCluster()
    {
        var settingsPath = Path.Combine(_dataDirectory, "postgresql.auto.conf");
        var settingsFile = new FileInfo(settingsPath);
        if (!settingsFile.Exists
            || (settingsFile.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0
            || settingsFile.Length > 64 * 1024)
        {
            throw new PackageIndexException("Native PostgreSQL initdb did not create a safe auto-configuration file.");
        }

        var logDirectory = Path.Combine(_dataDirectory, "log");
        Directory.CreateDirectory(logDirectory);
        SetPrivateDirectoryMode(logDirectory);
        var settings = new StringBuilder()
            .AppendLine("listen_addresses = '127.0.0.1'")
            .Append("port = ").AppendLine(_port.ToString(CultureInfo.InvariantCulture))
            .AppendLine("logging_collector = on")
            .AppendLine("log_directory = 'log'")
            .AppendLine("log_filename = 'appsurface-native.log'")
            .AppendLine("log_rotation_age = 0")
            .AppendLine("log_rotation_size = 1024")
            .AppendLine("log_truncate_on_rotation = on")
            .AppendLine("log_statement = 'none'")
            .AppendLine("log_min_error_statement = 'panic'")
            .AppendLine("log_parameter_max_length = 0")
            .AppendLine("log_parameter_max_length_on_error = 0");
        if (!OperatingSystem.IsWindows())
        {
            var socketDirectory = Path.Combine(_rootDirectory, "socket");
            Directory.CreateDirectory(socketDirectory);
            SetPrivateDirectoryMode(socketDirectory);
            settings.Append("unix_socket_directories = '")
                .Append(EscapePostgreSqlConfigValue(socketDirectory))
                .AppendLine("'");
        }

        using var stream = new FileStream(settingsPath, FileMode.Append, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.WriteLine();
        writer.Write(settings.ToString());
        writer.Flush();
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(settingsPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static void RequireUnixSocketPathWithinLimit(string root, int port)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var socketPath = Path.Combine(root, "socket", $".s.PGSQL.{port.ToString(CultureInfo.InvariantCulture)}");
        if (Encoding.UTF8.GetByteCount(socketPath) > ConservativeUnixSocketPathMaximumBytes)
        {
            throw new PackageIndexException(
                $"Native PostgreSQL Unix socket path exceeds the conservative {ConservativeUnixSocketPathMaximumBytes}-byte limit; use a shorter owned root.");
        }
    }

    private async Task CleanupAfterFailedStartAsync()
    {
        try
        {
            await CleanupAsync(allowLateStart: _startCommandIssued, _cleanupTimeoutMilliseconds);
            _bootstrapPassword = null;
            _disposed = true;
        }
        catch (PackageIndexException)
        {
            throw;
        }
        catch
        {
            throw new PackageIndexException(
                $"Native PostgreSQL failed setup and cleanup could not be verified; the owned root was retained at '{_rootDirectory}'.");
        }
    }

    private async Task CleanupAsync(bool allowLateStart, int cleanupTimeoutMilliseconds)
    {
        if (!_bootstrapProcessTerminationVerified)
        {
            throw new PackageIndexException(
                $"Native PostgreSQL initdb process termination could not be verified; the owned root was retained at '{_rootDirectory}'.");
        }

        var deadline = Stopwatch.StartNew();
        var absentSinceMilliseconds = -1L;
        var stopAttemptedForProcessId = -1;

        while (deadline.ElapsedMilliseconds < cleanupTimeoutMilliseconds)
        {
            RequireOwnedRoot(_rootDirectory, _ownerMarkerPath, _ownerToken);
            RequireNoReparsePointsWithin(_rootDirectory);
            var remaining = cleanupTimeoutMilliseconds - checked((int)deadline.ElapsedMilliseconds);
            var process = _runtime.FindOwnedPostmaster(_dataDirectory, _postgresPath, _port);
            if (process is not null)
            {
                _observedPostmaster = process;
                absentSinceMilliseconds = -1;
                if (stopAttemptedForProcessId != process.ProcessId)
                {
                    stopAttemptedForProcessId = process.ProcessId;
                    var stopTimeout = Math.Min(CleanupStopAttemptTimeoutMilliseconds, remaining);
                    var stopSeconds = Math.Max(1, stopTimeout / 1000);
                    _ = await RunExternalAsync(
                        ToolIdentity.PgCtlPath,
                        ["-D", _dataDirectory, "-m", "fast", "-w", "-t", stopSeconds.ToString(CultureInfo.InvariantCulture), "stop"],
                        "pg_ctl stop",
                        "stopping the owned native PostgreSQL cluster",
                        stopTimeout,
                        password: null,
                        CancellationToken.None,
                        suppressRunnerExceptions: true);
                }
            }
            else
            {
                if (_observedPostmaster is not null
                    && _runtime.IsSameProcessAlive(_observedPostmaster, _postgresPath))
                {
                    absentSinceMilliseconds = -1;
                }
                else
                {
                    var portListening = _runtime.IsLoopbackPortListening(_port);
                    if ((_bootstrapAuthenticationSucceeded
                            || _observedPostmaster is not null
                            || (allowLateStart && _startCommandIssued))
                        && portListening)
                    {
                        absentSinceMilliseconds = -1;
                    }
                    else
                    {
                        absentSinceMilliseconds = absentSinceMilliseconds < 0
                            ? deadline.ElapsedMilliseconds
                            : absentSinceMilliseconds;
                        var stableAbsence = deadline.ElapsedMilliseconds - absentSinceMilliseconds;
                        var lateStartWaitComplete = !allowLateStart || !_startCommandTimedOut
                            ? stableAbsence >= 500
                            : stableAbsence >= cleanupTimeoutMilliseconds;
                        if (lateStartWaitComplete)
                        {
                            break;
                        }
                    }
                }
            }

            await Task.Delay(Math.Min(100, Math.Max(1, remaining)));
        }

        var finalProcess = _runtime.FindOwnedPostmaster(_dataDirectory, _postgresPath, _port);
        var sameProcessAlive = _observedPostmaster is not null
            && _runtime.IsSameProcessAlive(_observedPostmaster, _postgresPath);
        var finalPortListening = (_bootstrapAuthenticationSucceeded
                || _observedPostmaster is not null
                || (allowLateStart && _startCommandIssued))
            && _runtime.IsLoopbackPortListening(_port);
        if (finalProcess is not null || sameProcessAlive || finalPortListening)
        {
            throw new PackageIndexException(
            $"Native PostgreSQL stop was not verified within {cleanupTimeoutMilliseconds} ms; the owned root was retained at '{_rootDirectory}'.");
        }

        if (_startCommandIssued && allowLateStart && _startCommandTimedOut
            && deadline.ElapsedMilliseconds >= cleanupTimeoutMilliseconds - 500)
        {
            // The full cleanup window was spent observing a timed-out pg_ctl start for a late postmaster.
            // A root is removable only when no owned PID, matching process, or authenticated listener remains.
        }

        RequireOwnedRoot(_rootDirectory, _ownerMarkerPath, _ownerToken);
        RequireNoReparsePointsWithin(_rootDirectory);
        RequireOwnedLogsBounded();
        try
        {
            Directory.Delete(_rootDirectory, recursive: true);
        }
        catch
        {
            throw new PackageIndexException(
                $"Native PostgreSQL stopped, but its owned root could not be removed; manual cleanup may be required at '{_rootDirectory}'.");
        }
    }

    private async Task<ExternalCommandResult> RunExternalAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string operationName,
        string timeoutDescription,
        int timeoutMilliseconds,
        string? password,
        CancellationToken cancellationToken,
        bool suppressRunnerExceptions = false)
    {
        var environment = GetPostgreSqlEnvironmentOverrides();
        if (password is not null)
        {
            environment["PGPASSWORD"] = password;
        }

        ExternalCommandResult result;
        try
        {
            result = await _commandRunner.RunAsync(
                new ExternalCommandRequest(
                    fileName,
                    arguments,
                    _rootDirectory,
                    operationName,
                    timeoutDescription,
                            Math.Max(1, timeoutMilliseconds),
                    environment,
                    new ExternalCapturePolicy(CapturedOutputLimitBytes)),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (suppressRunnerExceptions)
            {
                return new ExternalCommandResult(-1, string.Empty, string.Empty);
            }

            throw new PackageIndexException(
                $"Native PostgreSQL {operationName} could not run ({exception.GetType().Name}); child diagnostics were suppressed.");
        }

        return result;
    }

    private void RequireOwnedLogsBounded()
    {
        foreach (var path in new[] { _serverLogPath, Path.Combine(_dataDirectory, "log", "appsurface-native.log") })
        {
            var file = new FileInfo(path);
            if (!file.Exists)
            {
                continue;
            }

            if ((file.Attributes & FileAttributes.ReparsePoint) != 0 || file.Length > MaximumOwnedLogBytes)
            {
                throw new PackageIndexException("Native PostgreSQL created an unsafe or oversized private log.");
            }
        }
    }

    private static async Task<NativePostgreSqlToolIdentity> ValidateToolsAsync(
        string toolDirectory,
        IExternalCommandRunner commandRunner,
        Stopwatch acquisitionClock,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(toolDirectory) || !Path.IsPathFullyQualified(toolDirectory))
        {
            throw new PackageIndexException("Native PostgreSQL tool directory must be an absolute resolved path.");
        }

        var resolvedDirectory = Path.GetFullPath(toolDirectory);
        var directory = new DirectoryInfo(resolvedDirectory);
        if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new PackageIndexException("Native PostgreSQL tool directory is missing or is a reparse point.");
        }

        var executableSuffix = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        var paths = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["initdb"] = Path.Combine(resolvedDirectory, "initdb" + executableSuffix),
            ["postgres"] = Path.Combine(resolvedDirectory, "postgres" + executableSuffix),
            ["pg_ctl"] = Path.Combine(resolvedDirectory, "pg_ctl" + executableSuffix),
            ["psql"] = Path.Combine(resolvedDirectory, "psql" + executableSuffix)
        };
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, path) in paths)
        {
            var file = new FileInfo(path);
            if (!file.Exists || (file.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0
                || !SamePath(file.DirectoryName!, resolvedDirectory))
            {
                throw new PackageIndexException($"Native PostgreSQL tool '{name}' is missing or is not a regular file in the resolved tool directory.");
            }

            hashes.Add(name, await HashFileAsync(path, cancellationToken));
        }

        string? commonVersion = null;
        foreach (var name in new[] { "initdb", "postgres", "pg_ctl", "psql" })
        {
            var timeout = RemainingMilliseconds(acquisitionClock, ToolAcquisitionTimeoutMilliseconds, "tool acquisition");
            ExternalCommandResult result;
            try
            {
                result = await commandRunner.RunAsync(
                    new ExternalCommandRequest(
                        paths[name],
                        ["--version"],
                        resolvedDirectory,
                        $"{name} --version",
                        "validating the native PostgreSQL toolchain",
                        timeout,
                        GetPostgreSqlEnvironmentOverrides(),
                        new ExternalCapturePolicy(CapturedOutputLimitBytes)),
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new PackageIndexException(
                    $"Native PostgreSQL tool '{name}' could not be inspected ({exception.GetType().Name}); child diagnostics were suppressed.");
            }

            if (result.ExitCode != 0 || result.StandardOutputTruncated)
            {
                throw new PackageIndexException($"Native PostgreSQL tool '{name}' version check failed; child diagnostics were suppressed.");
            }

            var versionMatch = _versionPattern.Match(result.StandardOutput);
            if (!versionMatch.Success)
            {
                throw new PackageIndexException($"Native PostgreSQL tool '{name}' returned an unsupported version string.");
            }

            var version = versionMatch.Groups["version"].Value;
            if (commonVersion is null)
            {
                commonVersion = version;
            }
            else if (!string.Equals(commonVersion, version, StringComparison.Ordinal))
            {
                throw new PackageIndexException("Native PostgreSQL tools do not report the same build version.");
            }
        }

        var majorText = commonVersion!.Split('.')[0];
        if (!int.TryParse(majorText, NumberStyles.None, CultureInfo.InvariantCulture, out var major) || major < 16)
        {
            throw new PackageIndexException("Native PostgreSQL server version 16 or later is required.");
        }

        foreach (var (name, path) in paths)
        {
            var currentHash = await HashFileAsync(path, cancellationToken);
            if (!string.Equals(currentHash, hashes[name], StringComparison.Ordinal))
            {
                throw new PackageIndexException("Native PostgreSQL tool changed while its version was being verified.");
            }
        }

        return new NativePostgreSqlToolIdentity(
            resolvedDirectory,
            paths["initdb"],
            paths["postgres"],
            paths["pg_ctl"],
            paths["psql"],
            commonVersion,
            major,
            null,
            null,
            hashes);
    }

    private static (string Version, int VersionNumber) ParseServerVersion(string output, string toolVersion)
    {
        var parts = output.Trim().Split('|', StringSplitOptions.TrimEntries);
        if (parts.Length != 2
            || !Version.TryParse(parts[0], out var serverVersion)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var versionNumber)
            || versionNumber < 160000)
        {
            throw new PackageIndexException("Native PostgreSQL server version is below the supported PostgreSQL 16 floor or malformed.");
        }

        var toolParts = toolVersion.Split('.');
        if (toolParts.Length < 2
            || serverVersion.Major.ToString(CultureInfo.InvariantCulture) != toolParts[0]
            || serverVersion.Minor.ToString(CultureInfo.InvariantCulture) != toolParts[1])
        {
            throw new PackageIndexException("Native PostgreSQL server version does not match the validated tool build.");
        }

        return (serverVersion.ToString(), versionNumber);
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    private static string CreateBootstrapPassword()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        try
        {
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', 'A').Replace('/', 'B');
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static Dictionary<string, string?> GetPostgreSqlEnvironmentOverrides()
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var values = new Dictionary<string, string?>(comparer);
        foreach (var key in new[]
        {
            "PGDATA", "PGHOST", "PGHOSTADDR", "PGPORT", "PGUSER", "PGPASSWORD", "PGDATABASE",
            "PGSERVICE", "PGSERVICEFILE", "PGPASSFILE", "PGOPTIONS", "PGSSLMODE", "PGCHANNELBINDING",
            "PGCLIENTENCODING", "PGSYSCONFDIR"
        })
        {
            values[key] = null;
        }

        return values;
    }

    private static int RemainingMilliseconds(Stopwatch clock, int maximumMilliseconds, string operation)
    {
        var remaining = maximumMilliseconds - checked((int)Math.Min(clock.ElapsedMilliseconds, int.MaxValue));
        if (remaining <= 0)
        {
            throw new PackageIndexException($"Native PostgreSQL {operation} exceeded its {maximumMilliseconds} ms budget.");
        }

        return remaining;
    }

    private static void RequireFreshOwnedRoot(string root)
    {
        if (!Path.IsPathFullyQualified(root))
        {
            throw new PackageIndexException("Native PostgreSQL owned root must be an absolute path.");
        }

        var fullPath = Path.GetFullPath(root);
        if (Path.GetPathRoot(fullPath) == fullPath.TrimEnd(Path.DirectorySeparatorChar))
        {
            throw new PackageIndexException("Native PostgreSQL owned root cannot be a filesystem root.");
        }

        if (File.Exists(fullPath) || Directory.Exists(fullPath))
        {
            throw new PackageIndexException("Native PostgreSQL owned root must be unique and absent.");
        }

        var parent = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
        {
            throw new PackageIndexException("Native PostgreSQL owned root parent must be an existing private temporary directory.");
        }

        RequireNoReparseAncestors(parent);
    }

    private static void RequireNoReparseAncestors(string path)
    {
        var current = new DirectoryInfo(Path.GetFullPath(path));
        while (current is not null)
        {
            if (!current.Exists || (current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new PackageIndexException("Native PostgreSQL owned root has a missing or reparse-point ancestor.");
            }

            current = current.Parent;
        }
    }

    private static async Task WriteOwnerMarkerAsync(string path, string token, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true);
            var bytes = Encoding.ASCII.GetBytes(token);
            await stream.WriteAsync(bytes, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch
        {
            throw new PackageIndexException("Native PostgreSQL could not create its unique private ownership marker.");
        }
    }

    private static void RequireOwnedRoot(string root, string markerPath, string expectedToken)
    {
        var parent = Path.GetDirectoryName(root);
        if (string.IsNullOrEmpty(parent))
        {
            throw new PackageIndexException("Native PostgreSQL owned root has no verifiable parent; cleanup was refused.");
        }
        RequireNoReparseAncestors(parent);
        if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0
            || !File.Exists(markerPath) || (File.GetAttributes(markerPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new PackageIndexException("Native PostgreSQL owned root or owner marker is missing or unsafe; cleanup was refused.");
        }

        string marker;
        try
        {
            marker = File.ReadAllText(markerPath, Encoding.ASCII);
        }
        catch
        {
            throw new PackageIndexException("Native PostgreSQL ownership marker could not be verified; cleanup was refused.");
        }

        if (!string.Equals(marker, expectedToken, StringComparison.Ordinal))
        {
            throw new PackageIndexException("Native PostgreSQL ownership marker did not match; cleanup was refused.");
        }
    }

    private static void CleanupUnstartedRoot(string root, string markerPath, string expectedToken)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        var parent = Path.GetDirectoryName(root);
        if (string.IsNullOrEmpty(parent))
        {
            throw new PackageIndexException("Native PostgreSQL partial root parent could not be verified.");
        }
        RequireNoReparseAncestors(parent);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new PackageIndexException("Native PostgreSQL partial root became a reparse point.");
        }

        if (File.Exists(markerPath))
        {
            RequireOwnedRoot(root, markerPath, expectedToken);
            RequireNoReparsePointsWithin(root);
            Directory.Delete(root, recursive: true);
            return;
        }

        if (Directory.EnumerateFileSystemEntries(root).Any())
        {
            throw new PackageIndexException("Native PostgreSQL partial root has content without a verifiable owner marker.");
        }
        Directory.Delete(root, recursive: false);
    }

    private static void RequireNoReparsePointsWithin(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.TryPop(out var directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new PackageIndexException("Native PostgreSQL owned tree contains a reparse point; cleanup was refused.");
                }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    stack.Push(entry);
                }
            }
        }
    }

    private static void SetPrivateDirectoryMode(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static string EscapePostgreSqlConfigValue(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "''", StringComparison.Ordinal);

    private static bool SamePath(string left, string right)
        => string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

/// <summary>Secret-free evidence for one same-directory native PostgreSQL toolchain and its running server.</summary>
internal sealed record NativePostgreSqlToolIdentity(
    string BinDirectory,
    string InitDbPath,
    string PostgresPath,
    string PgCtlPath,
    string PsqlPath,
    string Version,
    int MajorVersion,
    string? ServerVersion,
    int? ServerVersionNumber,
    IReadOnlyDictionary<string, string> Sha256);

/// <summary>Injectable setup and cleanup limits, always bounded by the approved production ceilings.</summary>
/// <remarks>Tool acquisition remains fixed at five minutes and is outside the cluster setup budget.</remarks>
internal sealed record NativePostgreSqlBudgets(int SetupTimeoutMilliseconds, int CleanupTimeoutMilliseconds)
{
    /// <summary>The production native smoke budgets: ninety seconds for setup and twenty for cleanup.</summary>
    internal static NativePostgreSqlBudgets Default { get; } = new(
        DurableTemplateNativePostgreSql.SetupTimeoutMilliseconds,
        DurableTemplateNativePostgreSql.CleanupTimeoutMilliseconds);

    /// <summary>Rejects non-positive test budgets and values above the production limits.</summary>
    internal void Validate()
    {
        if (SetupTimeoutMilliseconds is <= 0 or > DurableTemplateNativePostgreSql.SetupTimeoutMilliseconds
            || CleanupTimeoutMilliseconds is <= 0 or > DurableTemplateNativePostgreSql.CleanupTimeoutMilliseconds)
        {
            throw new PackageIndexException("Native PostgreSQL setup and cleanup budgets must be positive and cannot exceed their production limits.");
        }
    }
}

/// <summary>Resolves the native tool directory from the three supported smoke runners.</summary>
internal interface INativePostgreSqlToolDirectoryResolver
{
    /// <summary>Resolves one installed PostgreSQL binary directory before the shared acquisition deadline.</summary>
    Task<string> ResolveAsync(
        IExternalCommandRunner commandRunner,
        int timeoutMilliseconds,
        CancellationToken cancellationToken);
}

internal enum NativePostgreSqlHostPlatform
{
    Linux,
    MacOS,
    Windows,
    Unsupported
}

/// <summary>Default Linux16, Windows PGBIN, and resolved Homebrew PostgreSQL16 directory resolver.</summary>
internal sealed class NativePostgreSqlToolDirectoryResolver : INativePostgreSqlToolDirectoryResolver
{
    private readonly NativePostgreSqlHostPlatform _platform;
    private readonly string? _windowsPgBin;
    private readonly string _brewExecutable;

    internal NativePostgreSqlToolDirectoryResolver(
        NativePostgreSqlHostPlatform? platform = null,
        string? windowsPgBin = null,
        string brewExecutable = "brew")
    {
        _platform = platform ?? GetCurrentPlatform();
        _windowsPgBin = windowsPgBin ?? Environment.GetEnvironmentVariable("PGBIN");
        _brewExecutable = brewExecutable;
    }

    /// <inheritdoc />
    public async Task<string> ResolveAsync(
        IExternalCommandRunner commandRunner,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commandRunner);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeoutMilliseconds);
        switch (_platform)
        {
            case NativePostgreSqlHostPlatform.Linux:
                return "/usr/lib/postgresql/16/bin";
            case NativePostgreSqlHostPlatform.Windows:
                if (string.IsNullOrWhiteSpace(_windowsPgBin) || !Path.IsPathFullyQualified(_windowsPgBin))
                {
                    throw new PackageIndexException("Native PostgreSQL Windows runner requires an absolute PGBIN directory.");
                }
                return Path.GetFullPath(_windowsPgBin);
            case NativePostgreSqlHostPlatform.MacOS:
                ExternalCommandResult brewResult;
                try
                {
                    brewResult = await commandRunner.RunAsync(
                        new ExternalCommandRequest(
                            _brewExecutable,
                            ["--prefix", "postgresql@16"],
                            Path.GetFullPath(Path.GetTempPath()),
                            "brew --prefix postgresql@16",
                            "resolving the installed PostgreSQL 16 Homebrew keg",
                            timeoutMilliseconds,
                            new Dictionary<string, string?>
                            {
                                ["PGDATA"] = null,
                                ["PGHOST"] = null,
                                ["PGUSER"] = null,
                                ["PGPASSWORD"] = null
                            },
                            new ExternalCapturePolicy(4096)),
                        cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    throw new PackageIndexException(
                        $"Native PostgreSQL Homebrew keg resolution failed ({exception.GetType().Name}); output was suppressed.");
                }

                if (brewResult.ExitCode != 0 || brewResult.StandardOutputTruncated)
                {
                    throw new PackageIndexException("Native PostgreSQL Homebrew keg resolution failed; output was suppressed.");
                }

                var prefix = brewResult.StandardOutput.Trim();
                if (!Path.IsPathFullyQualified(prefix))
                {
                    throw new PackageIndexException("Homebrew returned a non-absolute PostgreSQL 16 keg path.");
                }

                var prefixDirectory = new DirectoryInfo(Path.GetFullPath(prefix));
                if (!prefixDirectory.Exists)
                {
                    throw new PackageIndexException("Resolved Homebrew PostgreSQL 16 keg is missing.");
                }
                var resolvedPrefix = prefixDirectory.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? prefixDirectory.FullName;
                return Path.Combine(Path.GetFullPath(resolvedPrefix), "bin");
            default:
                throw new PackageIndexException("Native PostgreSQL smoke supports only Linux, macOS, and Windows runners.");
        }
    }

    private static NativePostgreSqlHostPlatform GetCurrentPlatform()
        => OperatingSystem.IsLinux() ? NativePostgreSqlHostPlatform.Linux
            : OperatingSystem.IsMacOS() ? NativePostgreSqlHostPlatform.MacOS
            : OperatingSystem.IsWindows() ? NativePostgreSqlHostPlatform.Windows
            : NativePostgreSqlHostPlatform.Unsupported;
}

/// <summary>Uses a workflow-resolved PostgreSQL bin directory without performing a second acquisition step.</summary>
internal sealed class FixedNativePostgreSqlToolDirectoryResolver(string binDirectory)
    : INativePostgreSqlToolDirectoryResolver
{
    /// <inheritdoc />
    public Task<string> ResolveAsync(
        IExternalCommandRunner commandRunner,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commandRunner);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeoutMilliseconds);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(binDirectory) || !Path.IsPathFullyQualified(binDirectory))
        {
            throw new PackageIndexException("Fixed native PostgreSQL bin directory must be an absolute path.");
        }

        return Task.FromResult(Path.GetFullPath(binDirectory));
    }
}

/// <summary>Data needed to execute initdb with its bootstrap password supplied only through redirected stdin.</summary>
internal sealed class NativePostgreSqlBootstrapRequest
{
    internal NativePostgreSqlBootstrapRequest(
        string executablePath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string standardInput,
        int timeoutMilliseconds)
    {
        ExecutablePath = executablePath;
        Arguments = arguments;
        WorkingDirectory = workingDirectory;
        StandardInput = standardInput;
        TimeoutMilliseconds = timeoutMilliseconds;
    }

    internal string ExecutablePath { get; }
    internal IReadOnlyList<string> Arguments { get; }
    internal string WorkingDirectory { get; }
    internal string StandardInput { get; }
    internal int TimeoutMilliseconds { get; }

    /// <inheritdoc />
    public override string ToString() => "NativePostgreSqlBootstrapRequest(secret input redacted)";
}

/// <summary>Exit result for redirected-input initdb; raw process output is deliberately omitted.</summary>
/// <param name="ExitCode">The child process exit code, or -1 when it exceeded its time limit.</param>
/// <param name="TimedOut">Whether the child process exceeded its requested time limit.</param>
/// <param name="ProcessTerminationVerified">Whether initdb and its redirected output streams were confirmed stopped.</param>
internal sealed record NativePostgreSqlBootstrapResult(
    int ExitCode,
    bool TimedOut,
    bool ProcessTerminationVerified = true);

internal sealed class NativePostgreSqlBootstrapTerminationException : Exception
{
}

/// <summary>Runs initdb with no TTY, no console window, and bounded-memory output draining.</summary>
internal interface INativePostgreSqlBootstrapRunner
{
    /// <summary>Runs initdb using exactly the request's redirected standard input and timeout.</summary>
    Task<NativePostgreSqlBootstrapResult> RunAsync(
        NativePostgreSqlBootstrapRequest request,
        CancellationToken cancellationToken);
}

internal sealed class NativePostgreSqlBootstrapRunner : INativePostgreSqlBootstrapRunner
{
    private const int OutputDrainTimeoutMilliseconds = 2_000;
    private const int BootstrapProcessStopVerificationTimeoutMilliseconds = 2_000;
    private readonly Func<Process, Task<bool>> _terminateProcessTreeAsync;

    internal NativePostgreSqlBootstrapRunner(Func<Process, Task<bool>>? terminateProcessTreeAsync = null)
    {
        _terminateProcessTreeAsync = terminateProcessTreeAsync ?? TerminateProcessTreeAsync;
    }

    /// <inheritdoc />
    public async Task<NativePostgreSqlBootstrapResult> RunAsync(
        NativePostgreSqlBootstrapRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var startInfo = new ProcessStartInfo
        {
            FileName = request.ExecutablePath,
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        ClearPostgreSqlEnvironment(startInfo.Environment);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new PackageIndexException("Native PostgreSQL initdb process did not start.");
            }
        }
        catch
        {
            throw new PackageIndexException("Native PostgreSQL initdb process could not start; output was suppressed.");
        }

        var stdoutDrain = DrainAsync(process.StandardOutput, CancellationToken.None);
        var stderrDrain = DrainAsync(process.StandardError, CancellationToken.None);
        try
        {
            await process.StandardInput.WriteAsync(request.StandardInput.AsMemory(), cancellationToken);
            await process.StandardInput.FlushAsync(cancellationToken);
            process.StandardInput.Close();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(request.TimeoutMilliseconds);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                var terminationVerified = await _terminateProcessTreeAsync(process);
                var outputDrainVerified = await WaitForOutputDrainsAsync(stdoutDrain, stderrDrain, CancellationToken.None);
                return new NativePostgreSqlBootstrapResult(
                    -1,
                    TimedOut: true,
                    ProcessTerminationVerified: terminationVerified && outputDrainVerified);
            }

            if (!await WaitForOutputDrainsAsync(stdoutDrain, stderrDrain, cancellationToken))
            {
                return new NativePostgreSqlBootstrapResult(
                    process.ExitCode,
                    TimedOut: false,
                    ProcessTerminationVerified: false);
            }
            return new NativePostgreSqlBootstrapResult(process.ExitCode, TimedOut: false);
        }
        catch
        {
            var terminationVerified = false;
            var outputDrainVerified = false;
            try
            {
                terminationVerified = await _terminateProcessTreeAsync(process);
                if (terminationVerified)
                {
                    outputDrainVerified = await WaitForOutputDrainsAsync(stdoutDrain, stderrDrain, CancellationToken.None);
                }
            }
            catch
            {
                // Failure to verify either cleanup step must retain the owned root.
            }

            if (!terminationVerified || !outputDrainVerified)
            {
                throw new NativePostgreSqlBootstrapTerminationException();
            }

            throw;
        }
    }

    private static async Task<bool> WaitForOutputDrainsAsync(
        Task stdoutDrain,
        Task stderrDrain,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.WhenAll(stdoutDrain, stderrDrain).WaitAsync(
                TimeSpan.FromMilliseconds(OutputDrainTimeoutMilliseconds),
                cancellationToken);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private static async Task DrainAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        while (await reader.ReadAsync(buffer.AsMemory(), cancellationToken) != 0)
        {
            // Drain both streams to avoid blocking initdb while retaining no secret-adjacent diagnostics.
        }
    }

    private static async Task<bool> TerminateProcessTreeAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            try
            {
                if (!process.HasExited)
                {
                    return false;
                }
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
        catch
        {
            return false;
        }

        try
        {
            await process.WaitForExitAsync().WaitAsync(
                TimeSpan.FromMilliseconds(BootstrapProcessStopVerificationTimeoutMilliseconds));
            return process.HasExited;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void ClearPostgreSqlEnvironment(IDictionary<string, string?> environment)
    {
        foreach (var key in environment.Keys.Where(static key => key.StartsWith("PG", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            environment.Remove(key);
        }
    }
}

/// <summary>Observed identity parsed from the private cluster's PostgreSQL-owned postmaster.pid.</summary>
internal sealed record NativePostgreSqlProcessIdentity(int ProcessId, long StartTimeUnixSeconds);

/// <summary>Injectable OS boundary for root/TTY checks, ephemeral ports, and postmaster ownership verification.</summary>
internal interface INativePostgreSqlRuntime
{
    /// <summary>Whether the current Unix effective user is root.</summary>
    bool IsRunningAsRoot { get; }

    /// <summary>Whether this Unix process has a controlling terminal.</summary>
    bool HasControllingTerminal { get; }

    /// <summary>Reserves an unused loopback TCP port and releases it before PostgreSQL startup.</summary>
    int ReserveLoopbackPort();

    /// <summary>Gets whether a TCP listener currently accepts connections on the selected IPv4 loopback port.</summary>
    bool IsLoopbackPortListening(int port);

    /// <summary>Finds and validates the live owned postmaster, or returns null when its PID file/process is absent.</summary>
    NativePostgreSqlProcessIdentity? FindOwnedPostmaster(string dataDirectory, string postgresPath, int port);

    /// <summary>Checks whether a previously observed process identity is still the same live PostgreSQL process.</summary>
    bool IsSameProcessAlive(NativePostgreSqlProcessIdentity identity, string postgresPath);
}

internal sealed class NativePostgreSqlRuntime : INativePostgreSqlRuntime
{
    internal static NativePostgreSqlRuntime Instance { get; } = new();

    private NativePostgreSqlRuntime()
    {
    }

    /// <inheritdoc />
    public bool IsRunningAsRoot => !OperatingSystem.IsWindows() && GetEffectiveUserId() == 0;

    /// <inheritdoc />
    public bool HasControllingTerminal
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                return false;
            }

            try
            {
                using var terminal = File.Open("/dev/tty", FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    /// <inheritdoc />
    public int ReserveLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <inheritdoc />
    public bool IsLoopbackPortListening(int port)
    {
        using var client = new TcpClient(AddressFamily.InterNetwork);
        try
        {
            var connect = client.ConnectAsync(IPAddress.Loopback, port);
            return connect.Wait(TimeSpan.FromMilliseconds(250)) && client.Connected;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (AggregateException exception) when (exception.InnerException is SocketException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public NativePostgreSqlProcessIdentity? FindOwnedPostmaster(string dataDirectory, string postgresPath, int port)
    {
        var pidPath = Path.Combine(dataDirectory, "postmaster.pid");
        if (!File.Exists(pidPath))
        {
            return null;
        }
        if ((File.GetAttributes(pidPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new PackageIndexException("Native PostgreSQL postmaster PID file is a reparse point; cleanup was refused.");
        }

        var file = new FileInfo(pidPath);
        if (file.Length > 4096)
        {
            throw new PackageIndexException("Native PostgreSQL postmaster PID file exceeded its safe bound; cleanup was refused.");
        }
        var lines = File.ReadAllLines(pidPath);
        if (lines.Length < 6
            || !int.TryParse(lines[0], NumberStyles.None, CultureInfo.InvariantCulture, out var processId)
            || !long.TryParse(lines[2], NumberStyles.None, CultureInfo.InvariantCulture, out var startTime)
            || !int.TryParse(lines[3], NumberStyles.None, CultureInfo.InvariantCulture, out var observedPort)
            || !string.Equals(Path.GetFullPath(lines[1]), Path.GetFullPath(dataDirectory), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            || observedPort != port
            || processId <= 0)
        {
            throw new PackageIndexException("Native PostgreSQL postmaster identity did not match the owned cluster; cleanup was refused.");
        }

        return IsSameProcessAlive(new NativePostgreSqlProcessIdentity(processId, startTime), postgresPath)
            ? new NativePostgreSqlProcessIdentity(processId, startTime)
            : null;
    }

    /// <inheritdoc />
    public bool IsSameProcessAlive(NativePostgreSqlProcessIdentity identity, string postgresPath)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(identity.ProcessId);
        }
        catch (ArgumentException)
        {
            return false;
        }

        using (process)
        {
            try
            {
                if (process.HasExited
                    || !string.Equals(Path.GetFileNameWithoutExtension(process.ProcessName), "postgres", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var actualPath = process.MainModule?.FileName;
                if (actualPath is null || !SameExecutablePath(actualPath, postgresPath))
                {
                    return false;
                }

                var actualStartTime = new DateTimeOffset(process.StartTime.ToUniversalTime()).ToUnixTimeSeconds();
                return Math.Abs(actualStartTime - identity.StartTimeUnixSeconds) <= 2;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                throw new PackageIndexException("Native PostgreSQL process identity could not be verified; cleanup was refused.");
            }
        }
    }

    private static bool SameExecutablePath(string left, string right)
        => string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();
}
