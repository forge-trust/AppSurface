using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AppSurfaceDurableWorker;
using ForgeTrust.AppSurface.Durable;
using Npgsql;
using NpgsqlTypes;
using Xunit.Abstractions;
using Microsoft.Extensions.Hosting;

namespace DurableWorkerTemplate.Tests;

public sealed class NativePostgreSqlSmokeTests
{
    private const string AdminConnectionVariable = "APPSURFACE_TEMPLATE_NATIVE_ADMIN_CONNECTION";
    private const string PgBinVariable = "APPSURFACE_TEMPLATE_NATIVE_PG_BIN";
    private const string SetupRemainingVariable = "APPSURFACE_TEMPLATE_NATIVE_SETUP_REMAINING_MS";
    private const string PortMinimumVariable = "APPSURFACE_TEMPLATE_NATIVE_PORT_MIN";
    private const string PortMaximumVariable = "APPSURFACE_TEMPLATE_NATIVE_PORT_MAX";
    private readonly ITestOutputHelper _output;

    public NativePostgreSqlSmokeTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    [Trait("Category", "NativePostgreSql")]
    public async Task NativePostgreSqlSmoke()
    {
        long cleanupStartedAt = 0;
        PostgreSqlFixture? fixture = null;
        NativeWorkerProcess? worker = null;
        string? primaryFailure = null;
        var success = false;

        try
        {
            var adminConnection = RequireEnvironmentValue(AdminConnectionVariable);
            var pgBin = RequireEnvironmentValue(PgBinVariable);
            var setupRemaining = ReadSetupBudget(RequireEnvironmentValue(SetupRemainingVariable));
            var minimumPort = ReadPort(RequireEnvironmentValue(PortMinimumVariable), PortMinimumVariable);
            var maximumPort = ReadPort(RequireEnvironmentValue(PortMaximumVariable), PortMaximumVariable);
            fixture = await PostgreSqlFixture.StartNativeAsync(adminConnection, pgBin, setupRemaining);
            var port = ReserveLoopbackPort(minimumPort, maximumPort);
            using var hostStartup = new CancellationTokenSource(FixtureBudgets.HostStartup);
            worker = NativeWorkerProcess.Start(port, fixture);
            using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            await WaitForLiveListenerAsync(worker, client, port, hostStartup.Token);

            using var observation = new CancellationTokenSource(FixtureBudgets.NativeObservation);
            using (var live = await client.GetAsync($"http://127.0.0.1:{port}/live", observation.Token))
            {
                Assert.Equal(HttpStatusCode.OK, live.StatusCode);
                using var liveJson = await ReadJsonAsync(live, observation.Token);
                Assert.Equal("Live", liveJson.RootElement.GetProperty("status").GetString());
            }

            using (var compatibility = await client.GetAsync($"http://127.0.0.1:{port}/compatibility", observation.Token))
            {
                Assert.Equal(HttpStatusCode.OK, compatibility.StatusCode);
                using var compatibilityJson = await ReadJsonAsync(compatibility, observation.Token);
                AssertAssessment(compatibilityJson.RootElement, canEnableActivation: true);
            }

            using (var readiness = await client.GetAsync($"http://127.0.0.1:{port}/ready", observation.Token))
            {
                Assert.Equal(HttpStatusCode.ServiceUnavailable, readiness.StatusCode);
                using var readinessJson = await ReadJsonAsync(readiness, observation.Token);
                AssertAssessment(readinessJson.RootElement, canEnableActivation: true);
            }

            Assert.Equal(
                fixture.DurableCatalogBeforeHost,
                await fixture.ReadDurableCatalogAsync(observation.Token));
            Assert.Equal(
                fixture.RoleCatalogBeforeHost,
                await fixture.ReadRoleCatalogAsync(observation.Token));

            // From the first possible outage mutation, restoration and teardown share the original cleanup bound.
            cleanupStartedAt = Stopwatch.GetTimestamp();
            await AssertOwnedStoreOutageAndRecoveryAsync(
                fixture, adminConnection, worker, client, port, cleanupStartedAt, observation.Token);
            success = true;
        }
        catch (Exception exception)
        {
            primaryFailure = exception.GetType().Name;
        }
        finally
        {
            if (cleanupStartedAt == 0)
            {
                cleanupStartedAt = Stopwatch.GetTimestamp();
            }
            List<Exception> cleanupFailures;
            try
            {
                cleanupFailures = await CleanupOwnedResourcesAsync(worker, fixture, cleanupStartedAt);
            }
            catch (Exception exception)
            {
                cleanupFailures = [new InvalidOperationException($"owned-cleanup-{exception.GetType().Name}")];
            }

            var elapsedMilliseconds = (long)Math.Ceiling(Stopwatch.GetElapsedTime(cleanupStartedAt).TotalMilliseconds);
            try
            {
                _output.WriteLine($"[native-cleanup] elapsed-ms={Math.Max(0, elapsedMilliseconds).ToString(CultureInfo.InvariantCulture)}");
            }
            catch
            {
                // Preserve the smoke/cleanup failure if the test-output writer is already unavailable.
            }

            if (cleanupFailures.Count != 0)
            {
                var failureTypes = string.Join(",", cleanupFailures.Select(static failure => failure.GetType().Name));
                primaryFailure = primaryFailure is null
                    ? $"cleanup:{failureTypes}"
                    : $"{primaryFailure};cleanup:{failureTypes}";
            }
        }

        if (primaryFailure is not null)
        {
            throw new InvalidOperationException($"Native PostgreSQL smoke failed ({primaryFailure}); credential-bearing diagnostics were suppressed.");
        }

        Assert.True(success, "Native smoke reached neither a passing nor a failing terminal state.");
        _output.WriteLine("[native-store-outage] unavailable readiness and restricted recovery passed");
        _output.WriteLine("[native-smoke] read-only ordinary startup passed");
    }

    private static async Task<List<Exception>> CleanupOwnedResourcesAsync(
        NativeWorkerProcess? worker,
        PostgreSqlFixture? fixture,
        long cleanupStartedAt)
    {
        var failures = new List<Exception>();
        var cleanupOperations = new SetupOperationLifetime();
        if (worker is not null)
        {
            await AttemptCleanupAsync("ordinary-host-process", worker.DisposeWithinBudgetAsync).ConfigureAwait(false);
        }

        if (fixture is not null)
        {
            await AttemptCleanupAsync("native-fixture", fixture.DisposeWithinBudgetAsync).ConfigureAwait(false);
        }

        if (cleanupOperations.PendingCount != 0)
        {
            failures.Add(new InvalidOperationException("native-owned-cleanup-unfinished"));
        }
        if (Stopwatch.GetElapsedTime(cleanupStartedAt) >= FixtureBudgets.Cleanup)
        {
            failures.Add(new TimeoutException("native-owned-cleanup-budget-exhausted"));
        }

        return failures;

        async Task AttemptCleanupAsync(string resource, Func<TimeSpan, Task> dispose)
        {
            var remaining = FixtureBudgets.Cleanup - Stopwatch.GetElapsedTime(cleanupStartedAt);
            using var observation = new CancellationTokenSource();
            if (remaining <= TimeSpan.Zero)
            {
                failures.Add(new TimeoutException($"{resource}-cleanup-budget-exhausted"));
                // Invoke disposal even after expiry, without renewing the caller's observation window.
                observation.Cancel();
            }
            else
            {
                observation.CancelAfter(remaining);
            }

            try
            {
                // Bound factory invocation too, and retain observation of any late cleanup fault.
                await cleanupOperations.AwaitAsync(resource, () =>
                {
                    var sharedRemaining = FixtureBudgets.Cleanup - Stopwatch.GetElapsedTime(cleanupStartedAt);
                    // The minimum satisfies the disposal API; the outer observation still uses the original deadline.
                    return dispose(TimeSpan.FromMilliseconds(Math.Max(1, sharedRemaining.TotalMilliseconds)));
                }, observation.Token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures.Add(new InvalidOperationException($"{resource}-cleanup-{exception.GetType().Name}"));
            }
        }
    }

    [Fact]
    public async Task Expired_cleanup_observation_invokes_each_factory_and_retains_late_faults()
    {
        var lifetime = new SetupOperationLifetime();
        var workerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fixtureEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workerCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fixtureCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var observation = new CancellationTokenSource();
        observation.Cancel();

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lifetime.AwaitAsync(
                "ordinary-host-process", () =>
                {
                    workerEntered.TrySetResult();
                    return workerCleanup.Task;
                }, observation.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lifetime.AwaitAsync(
                "native-fixture", () =>
                {
                    fixtureEntered.TrySetResult();
                    return fixtureCleanup.Task;
                }, observation.Token));

            await Task.WhenAll(workerEntered.Task, fixtureEntered.Task).WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(2, lifetime.PendingCount);
            workerCleanup.SetException(new IOException("late owned worker cleanup failure"));
            fixtureCleanup.SetException(new IOException("late owned fixture cleanup failure"));
            var failures = new List<string>();
            Assert.True(await lifetime.StopAndObservePendingAsync(
                stopOwnerAsync: null, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(100), failures));
            Assert.Empty(failures);
            using var settled = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            while (lifetime.PendingCount != 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(1), settled.Token);
            }
        }
        finally
        {
            workerCleanup.TrySetResult();
            fixtureCleanup.TrySetResult();
        }
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        Assert.InRange(Encoding.UTF8.GetByteCount(body), 1, 1024);
        return JsonDocument.Parse(body);
    }

    private static async Task AssertOwnedStoreOutageAndRecoveryAsync(
        PostgreSqlFixture fixture,
        string bootstrapConnectionString,
        NativeWorkerProcess worker,
        HttpClient client,
        int port,
        long cleanupStartedAt,
        CancellationToken observationToken)
    {
        var bootstrap = new NpgsqlConnectionStringBuilder(bootstrapConnectionString);
        var owned = new NpgsqlConnectionStringBuilder(fixture.Administrator.ConnectionString);
        var runtime = new NpgsqlConnectionStringBuilder(fixture.RuntimeConnectionString);
        var dispatcher = new NpgsqlConnectionStringBuilder(fixture.DispatcherConnectionString);
        var database = owned.Database;
        if (database is null || !database.StartsWith("appsurface_", StringComparison.Ordinal)
            || !Guid.TryParseExact(database["appsurface_".Length..], "N", out _)
            || database == bootstrap.Database || bootstrap.Database != "postgres"
            || bootstrap.Host != "127.0.0.1" || bootstrap.Username != "postgres"
            || owned.Host != bootstrap.Host || owned.Port != bootstrap.Port || owned.Username != bootstrap.Username
            || runtime.Host != owned.Host || runtime.Port != owned.Port || runtime.Database != database
            || dispatcher.Host != owned.Host || dispatcher.Port != owned.Port || dispatcher.Database != database
            || !string.Equals(fixture.DatabaseIdentitySha256,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(database))), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Native outage requires the exact fixture-owned database and loopback owner identity.");
        }

        await using var owner = await fixture.Administrator.OpenConnectionAsync(observationToken).ConfigureAwait(false);
        uint databaseOid;
        int originalConnectionLimit;
        await using (var identity = new NpgsqlCommand("""
            SELECT d.oid, d.datconnlimit, current_database(), current_user, session_user,
                   current_setting('port')::integer, d.datdba = r.oid AND r.rolsuper
            FROM pg_catalog.pg_database d
            JOIN pg_catalog.pg_roles r ON r.rolname = current_user
            WHERE d.datname = current_database() AND d.datname = @database_name;
            """, owner))
        {
            identity.Parameters.AddWithValue("database_name", database);
            await using var reader = await identity.ExecuteReaderAsync(observationToken).ConfigureAwait(false);
            Assert.True(await reader.ReadAsync(observationToken).ConfigureAwait(false));
            databaseOid = reader.GetFieldValue<uint>(0);
            originalConnectionLimit = reader.GetInt32(1);
            Assert.Equal(database, reader.GetString(2));
            Assert.Equal(owned.Username, reader.GetString(3));
            Assert.Equal(owned.Username, reader.GetString(4));
            Assert.Equal(owned.Port, reader.GetInt32(5));
            Assert.True(reader.GetBoolean(6), "Native outage connection must own the exact isolated fixture database.");
            Assert.InRange(originalConnectionLimit, -1, int.MaxValue);
            Assert.NotEqual(0, originalConnectionLimit);
            Assert.False(await reader.ReadAsync(observationToken).ConfigureAwait(false));
        }

        try
        {
            // The dedicated superuser connection remains available for restoration. Only this database is denied.
            await SetOwnedDatabaseConnectionLimitAsync(owner, database, databaseOid, 0, observationToken);
            await using (var terminate = new NpgsqlCommand("""
                SELECT COALESCE(bool_and(pg_terminate_backend(pid)), true)
                FROM pg_catalog.pg_stat_activity
                WHERE datid = @database_oid AND pid <> pg_backend_pid();
                """, owner))
            {
                terminate.Parameters.AddWithValue("database_oid", NpgsqlDbType.Oid, databaseOid);
                Assert.True(Assert.IsType<bool>(await terminate.ExecuteScalarAsync(observationToken).ConfigureAwait(false)));
            }

            await using (var restricted = new NpgsqlConnection(fixture.RuntimeConnectionString))
            {
                var failure = await Assert.ThrowsAsync<PostgresException>(() => restricted.OpenAsync(observationToken));
                Assert.Equal(PostgresErrorCodes.TooManyConnections, failure.SqlState);
            }

            worker.ThrowIfExited();
            await AssertLiveAsync(client, port, observationToken);
            using var readiness = await client.GetAsync($"http://127.0.0.1:{port}/ready", observationToken);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, readiness.StatusCode);
            using var readinessJson = await ReadJsonAsync(readiness, observationToken);
            var assessment = readinessJson.RootElement;
            Assert.Equal("Assessment", assessment.GetProperty("outcome").GetString());
            Assert.Equal("Unavailable", assessment.GetProperty("observedHealthState").GetString());
            Assert.Equal(DurableProblemCodes.StoreUnavailable, assessment.GetProperty("problemCode").GetString());
            Assert.False(assessment.GetProperty("canEnableActivation").GetBoolean());
            Assert.False(assessment.GetProperty("isReady").GetBoolean());
            await AssertLiveAsync(client, port, observationToken);
        }
        finally
        {
            // Do not use the canceled observation token: restore even when an outage assertion failed.
            var remaining = FixtureBudgets.Cleanup - Stopwatch.GetElapsedTime(cleanupStartedAt);
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException("native-outage-restoration-budget-exhausted");
            }
            using var restoration = new CancellationTokenSource(
                remaining < FixtureBudgets.NativeObservation ? remaining : FixtureBudgets.NativeObservation);
            // A canceled database command can break its connection. Reconnect as the verified fixture owner,
            // which is exempt from the restricted-client limit, rather than relying on that connection's state.
            await using var restoringOwner = await fixture.Administrator.OpenConnectionAsync(restoration.Token).ConfigureAwait(false);
            await SetOwnedDatabaseConnectionLimitAsync(
                restoringOwner, database, databaseOid, originalConnectionLimit, restoration.Token);

            remaining = FixtureBudgets.Cleanup - Stopwatch.GetElapsedTime(cleanupStartedAt);
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException("native-outage-recovery-budget-exhausted");
            }
            using var recovery = new CancellationTokenSource(
                remaining < FixtureBudgets.NativeObservation ? remaining : FixtureBudgets.NativeObservation);
            await using (var restricted = new NpgsqlConnection(fixture.RuntimeConnectionString))
            {
                await restricted.OpenAsync(recovery.Token).ConfigureAwait(false);
                await using var probe = new NpgsqlCommand("SELECT current_database(), current_user;", restricted);
                await using var reader = await probe.ExecuteReaderAsync(recovery.Token).ConfigureAwait(false);
                Assert.True(await reader.ReadAsync(recovery.Token).ConfigureAwait(false));
                Assert.Equal(database, reader.GetString(0));
                Assert.Equal(fixture.RuntimeRole, reader.GetString(1));
            }

            worker.ThrowIfExited();
            await AssertLiveAsync(client, port, recovery.Token);
            using var readiness = await client.GetAsync($"http://127.0.0.1:{port}/ready", recovery.Token);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, readiness.StatusCode);
            using var readinessJson = await ReadJsonAsync(readiness, recovery.Token);
            AssertAssessment(readinessJson.RootElement, canEnableActivation: true);
            Assert.NotEqual(DurableProblemCodes.StoreUnavailable,
                readinessJson.RootElement.GetProperty("problemCode").GetString());
            Assert.Equal(fixture.DurableCatalogBeforeHost, await fixture.ReadDurableCatalogAsync(recovery.Token));
            Assert.Equal(fixture.RoleCatalogBeforeHost, await fixture.ReadRoleCatalogAsync(recovery.Token));
        }
    }

    private static async Task SetOwnedDatabaseConnectionLimitAsync(
        NpgsqlConnection owner, string database, uint databaseOid, int connectionLimit, CancellationToken cancellationToken)
    {
        await using var format = new NpgsqlCommand("""
            SELECT format('ALTER DATABASE %I CONNECTION LIMIT %s', datname, @connection_limit)
            FROM pg_catalog.pg_database
            WHERE oid = @database_oid AND datname = @database_name AND datname = current_database();
            """, owner);
        format.Parameters.AddWithValue("connection_limit", connectionLimit);
        format.Parameters.AddWithValue("database_oid", NpgsqlDbType.Oid, databaseOid);
        format.Parameters.AddWithValue("database_name", database);
        var sql = Assert.IsType<string>(await format.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        await using var alter = new NpgsqlCommand(sql, owner);
        await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await using var verify = new NpgsqlCommand("""
            SELECT datconnlimit FROM pg_catalog.pg_database
            WHERE oid = @database_oid AND datname = @database_name AND datname = current_database();
            """, owner);
        verify.Parameters.AddWithValue("database_oid", NpgsqlDbType.Oid, databaseOid);
        verify.Parameters.AddWithValue("database_name", database);
        Assert.Equal(connectionLimit, Assert.IsType<int>(await verify.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)));
    }

    private static async Task AssertLiveAsync(HttpClient client, int port, CancellationToken cancellationToken)
    {
        using var live = await client.GetAsync($"http://127.0.0.1:{port}/live", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        using var liveJson = await ReadJsonAsync(live, cancellationToken);
        Assert.Equal("Live", liveJson.RootElement.GetProperty("status").GetString());
    }

    private static void AssertAssessment(JsonElement response, bool canEnableActivation)
    {
        Assert.Equal("Assessment", response.GetProperty("outcome").GetString());
        Assert.Equal("NotStarted", response.GetProperty("observedHealthState").GetString());
        Assert.Equal(canEnableActivation, response.GetProperty("canEnableActivation").GetBoolean());
        Assert.False(response.GetProperty("isReady").GetBoolean());
    }

    private static string RequireEnvironmentValue(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Native PostgreSQL smoke requires child environment variable {name}.");
        }

        return value;
    }

    private static TimeSpan ReadSetupBudget(string value)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds)
            || milliseconds is < 1 or > 90_000)
        {
            throw new InvalidOperationException(
                $"{SetupRemainingVariable} must contain the positive remaining milliseconds from the 90-second native setup phase.");
        }

        return TimeSpan.FromMilliseconds(milliseconds);
    }

    private static int ReadPort(string value, string variableName)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65_535)
        {
            throw new InvalidOperationException($"{variableName} must be a TCP port in the range 1 through 65535.");
        }

        return port;
    }

    private static int ReserveLoopbackPort(int minimum, int maximum)
    {
        if (minimum > maximum)
        {
            throw new InvalidOperationException("The native smoke loopback port range is reversed.");
        }

        var span = (long)maximum - minimum + 1;
        var start = minimum + RandomNumberGenerator.GetInt32(checked((int)span));
        for (var offset = 0L; offset < span; offset++)
        {
            var candidate = minimum + (int)(((start - minimum + offset) % span));
            using var listener = new TcpListener(IPAddress.Loopback, candidate);
            try
            {
                listener.Start();
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            catch (SocketException)
            {
                // Probe the remaining ports in the parent-owned loopback range.
            }
        }

        throw new InvalidOperationException("No port in the parent-owned native smoke loopback range is available.");
    }

    private static async Task WaitForLiveListenerAsync(
        NativeWorkerProcess worker,
        HttpClient client,
        int port,
        CancellationToken cancellationToken)
    {
        var address = new Uri($"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/live");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            worker.ThrowIfExited();
            try
            {
                using var response = await client.GetAsync(address, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // The normal Kestrel listener is not ready yet; retry inside the single startup deadline.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class NativeWorkerProcess
    {
        private const int MaximumCaptureBytes = 4 * 1024 * 1024;
        private readonly Process _process;
        private readonly Task _stdoutDrain;
        private readonly Task _stderrDrain;
        private int _disposeStarted;

        private NativeWorkerProcess(Process process, IReadOnlyCollection<string> redactions)
        {
            _process = process;
            _stdoutDrain = DrainAsync(process.StandardOutput.BaseStream, redactions);
            _stderrDrain = DrainAsync(process.StandardError.BaseStream, redactions);
        }

        internal static NativeWorkerProcess Start(int port, PostgreSqlFixture fixture)
        {
            var hostAssembly = typeof(WorkerApplication).Assembly.Location;
            var hostDirectory = Path.GetDirectoryName(hostAssembly)
                ?? throw new InvalidOperationException("The generated worker assembly has no output directory.");
            if (!File.Exists(hostAssembly)
                || !File.Exists(Path.ChangeExtension(hostAssembly, ".runtimeconfig.json"))
                || !File.Exists(Path.ChangeExtension(hostAssembly, ".deps.json")))
            {
                throw new InvalidOperationException("The generated worker executable output is incomplete beside its test assembly.");
            }

            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var startInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = hostDirectory,
            };
            startInfo.ArgumentList.Add(hostAssembly);
            startInfo.ArgumentList.Add("--urls");
            startInfo.ArgumentList.Add($"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}");
            RemoveSensitiveAndHostConfiguration(startInfo.Environment);
            startInfo.Environment["DOTNET_NOLOGO"] = "1";
            startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            startInfo.Environment["DOTNET_ENVIRONMENT"] = Environments.Development;
            startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = Environments.Development;
            startInfo.Environment["APPSURFACE_TEMPLATE_ACTIVATION_TOKEN"] = token;
            startInfo.Environment["Durable__DispatcherConnectionString"] = fixture.DispatcherConnectionString;
            startInfo.Environment["Durable__RuntimeConnectionString"] = fixture.RuntimeConnectionString;
            startInfo.Environment["Durable__StoreId"] = fixture.StoreId.ToString("D");
            startInfo.Environment["Durable__RuntimeEpoch"] = fixture.Epoch.ToString("D");

            var dispatcher = new NpgsqlConnectionStringBuilder(fixture.DispatcherConnectionString);
            var runtime = new NpgsqlConnectionStringBuilder(fixture.RuntimeConnectionString);
            var redactions = new[]
            {
                fixture.DispatcherConnectionString,
                fixture.RuntimeConnectionString,
                dispatcher.Password,
                runtime.Password,
                token,
            }.Where(static value => !string.IsNullOrEmpty(value))
                .Select(static value => value!)
                .ToArray();
            var process = new Process { StartInfo = startInfo };
            try
            {
                if (!process.Start())
                {
                    throw new InvalidOperationException("Could not start the generated worker executable.");
                }
            }
            catch (Win32Exception exception)
            {
                process.Dispose();
                throw new InvalidOperationException($"Could not start the generated worker executable ({exception.GetType().Name}).");
            }

            return new NativeWorkerProcess(process, redactions);
        }

        internal void ThrowIfExited()
        {
            if (_process.HasExited)
            {
                throw new InvalidOperationException($"The ordinary generated worker exited before its Kestrel listener was ready (exit={_process.ExitCode}).");
            }
        }

        internal async Task DisposeWithinBudgetAsync(TimeSpan budget)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(budget, TimeSpan.Zero);
            if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            {
                return;
            }

            var startedAt = Stopwatch.GetTimestamp();
            var failures = new List<string>();
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException) when (_process.HasExited)
            {
                // The host exited between the state check and tree termination.
            }
            catch (Win32Exception exception)
            {
                failures.Add($"kill-{exception.GetType().Name}");
            }

            var remaining = budget - Stopwatch.GetElapsedTime(startedAt);
            if (remaining > TimeSpan.Zero)
            {
                try
                {
                    await _process.WaitForExitAsync().WaitAsync(
                        remaining < FixtureBudgets.ChildTermination ? remaining : FixtureBudgets.ChildTermination)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    failures.Add("process-tree-unfinished");
                }
            }
            else
            {
                failures.Add("process-tree-budget-exhausted");
            }

            remaining = budget - Stopwatch.GetElapsedTime(startedAt);
            if (remaining > TimeSpan.Zero)
            {
                try
                {
                    await Task.WhenAll(_stdoutDrain, _stderrDrain).WaitAsync(remaining).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    failures.Add("process-output-pipes-unfinished");
                }
                catch (IOException exception)
                {
                    failures.Add($"process-output-{exception.GetType().Name}");
                }
            }
            else
            {
                failures.Add("process-output-budget-exhausted");
            }

            _process.Dispose();
            if (failures.Count != 0)
            {
                throw new InvalidOperationException($"Generated worker cleanup was incomplete: {string.Join(",", failures)}.");
            }
        }

        private static async Task DrainAsync(Stream stream, IReadOnlyCollection<string> redactions)
        {
            var buffer = new byte[16 * 1024];
            using var capture = new MemoryStream(capacity: MaximumCaptureBytes);
            while (true)
            {
                var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                var accepted = Math.Min(read, MaximumCaptureBytes - (int)capture.Length);
                if (accepted > 0)
                {
                    capture.Write(buffer, 0, accepted);
                }
            }

            var text = Encoding.UTF8.GetString(capture.GetBuffer(), 0, (int)capture.Length);
            foreach (var secret in redactions.Where(static value => !string.IsNullOrEmpty(value)))
            {
                text = text.Replace(secret, "<redacted>", StringComparison.Ordinal);
            }
            _ = text;
        }

        private static void RemoveSensitiveAndHostConfiguration(IDictionary<string, string?> environment)
        {
            foreach (var name in environment.Keys.ToArray())
            {
                if (name.StartsWith("PG", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("APPSURFACE_", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("Durable__", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("DurableActivation__", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("OpenTelemetry__", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("SECRET", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("TOKEN", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("CREDENTIAL", StringComparison.OrdinalIgnoreCase))
                {
                    environment.Remove(name);
                }
            }
        }
    }
}
