using System.Collections.Concurrent;
using System.Diagnostics;
using Npgsql;

namespace ForgeTrust.AppSurface.PostgreSql.Testing.Tests;

public sealed class PostgreSqlFixtureIntegrationTests
{
    [Fact]
    public async Task RunFromTemplateAsync_InitializesOnceAndCreatesIndependentClones()
    {
        var connectionString = await PostgreSqlTestServer.GetRequiredConnectionStringAsync();
        var fixture = new PostgreSqlFixture();
        var templatePrefix = PostgreSqlTestServer.NewPrefix("cached_template");
        var cloneNames = new ConcurrentBag<string>();
        var initializationCount = 0;

        try
        {
            var runs = Enumerable.Range(0, 4).Select(index => fixture.RunFromTemplateAsync(
                connectionString,
                PostgreSqlTestServer.NewPrefix($"cached_clone_{index}"),
                templatePrefix,
                async (templateConnectionString, cancellationToken) =>
                {
                    Interlocked.Increment(ref initializationCount);
                    await PostgreSqlTestServer.ExecuteAsync(
                        templateConnectionString,
                        "CREATE TABLE fixture_seed (value integer NOT NULL); INSERT INTO fixture_seed VALUES (17);");
                },
                async (cloneConnectionString, cancellationToken) =>
                {
                    cloneNames.Add(await PostgreSqlTestServer.ReadScalarAsync<string>(
                        cloneConnectionString,
                        "SELECT current_database();"));
                    var seed = await PostgreSqlTestServer.ReadScalarAsync<int>(
                        cloneConnectionString,
                        "SELECT value FROM fixture_seed;");
                    Assert.Equal(17, seed);
                }));

            await Task.WhenAll(runs);
        }
        finally
        {
            await PostgreSqlTestServer.CleanupAsync(fixture);
        }

        Assert.Equal(1, initializationCount);
        Assert.Equal(4, cloneNames.Count);
        Assert.Equal(4, cloneNames.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task RunFromTemplateAsync_CloneChangesRemainPrivateToEachClone()
    {
        var connectionString = await PostgreSqlTestServer.GetRequiredConnectionStringAsync();
        var fixture = new PostgreSqlFixture();
        var templatePrefix = PostgreSqlTestServer.NewPrefix("private_clone_template");
        var cloneNames = new ConcurrentBag<string>();

        try
        {
            async Task RunCloneAsync(int value)
            {
                await fixture.RunFromTemplateAsync(
                    connectionString,
                    PostgreSqlTestServer.NewPrefix($"private_clone_{value}"),
                    templatePrefix,
                    async (templateConnectionString, cancellationToken) =>
                    {
                        await PostgreSqlTestServer.ExecuteAsync(
                            templateConnectionString,
                            "CREATE TABLE clone_probe (value integer NOT NULL);");
                    },
                    async (cloneConnectionString, cancellationToken) =>
                    {
                        cloneNames.Add(await PostgreSqlTestServer.ReadScalarAsync<string>(
                            cloneConnectionString,
                            "SELECT current_database();"));
                        Assert.Equal(0, await PostgreSqlTestServer.ReadScalarAsync<int>(
                            cloneConnectionString,
                            "SELECT count(*)::integer FROM clone_probe;"));
                        await PostgreSqlTestServer.ExecuteAsync(
                            cloneConnectionString,
                            "INSERT INTO clone_probe VALUES (@value);",
                            "value",
                            value);
                        Assert.Equal(value, await PostgreSqlTestServer.ReadScalarAsync<int>(
                            cloneConnectionString,
                            "SELECT value FROM clone_probe;"));
                    });
            }

            await Task.WhenAll(RunCloneAsync(1), RunCloneAsync(2));

            await fixture.RunFromTemplateAsync(
                connectionString,
                PostgreSqlTestServer.NewPrefix("private_clone_fresh"),
                templatePrefix,
                static (_, _) => throw new InvalidOperationException("The cached template should not be initialized twice."),
                async (cloneConnectionString, cancellationToken) =>
                {
                    Assert.Equal(0, await PostgreSqlTestServer.ReadScalarAsync<int>(
                        cloneConnectionString,
                        "SELECT count(*)::integer FROM clone_probe;"));
                });
        }
        finally
        {
            await PostgreSqlTestServer.CleanupAsync(fixture);
        }

        Assert.Equal(2, cloneNames.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task SeparateFixtureOwners_CreateAndCleanOnlyTheirOwnTemplates()
    {
        var connectionString = await PostgreSqlTestServer.GetRequiredConnectionStringAsync();
        var firstOwner = new PostgreSqlFixture();
        var secondOwner = new PostgreSqlFixture();
        var templatePrefix = PostgreSqlTestServer.NewPrefix("owner_template");
        var firstInitializationCount = 0;
        var secondInitializationCount = 0;

        try
        {
            await firstOwner.RunFromTemplateAsync(
                connectionString,
                PostgreSqlTestServer.NewPrefix("first_owner_clone"),
                templatePrefix,
                async (templateConnectionString, cancellationToken) =>
                {
                    Interlocked.Increment(ref firstInitializationCount);
                    await PostgreSqlTestServer.ExecuteAsync(
                        templateConnectionString,
                        "CREATE TABLE owner_probe (owner integer NOT NULL); INSERT INTO owner_probe VALUES (1);");
                },
                async (cloneConnectionString, cancellationToken) =>
                    Assert.Equal(1, await PostgreSqlTestServer.ReadScalarAsync<int>(
                        cloneConnectionString,
                        "SELECT owner FROM owner_probe;")));

            await secondOwner.RunFromTemplateAsync(
                connectionString,
                PostgreSqlTestServer.NewPrefix("second_owner_clone"),
                templatePrefix,
                async (templateConnectionString, cancellationToken) =>
                {
                    Interlocked.Increment(ref secondInitializationCount);
                    await PostgreSqlTestServer.ExecuteAsync(
                        templateConnectionString,
                        "CREATE TABLE owner_probe (owner integer NOT NULL); INSERT INTO owner_probe VALUES (2);");
                },
                async (cloneConnectionString, cancellationToken) =>
                    Assert.Equal(2, await PostgreSqlTestServer.ReadScalarAsync<int>(
                        cloneConnectionString,
                        "SELECT owner FROM owner_probe;")));

            var firstCleanup = await firstOwner.CleanupAllAsync();
            Assert.Equal(1, firstCleanup.RegisteredTemplateCount);
            Assert.Equal(1, firstCleanup.RemovedTemplateCount);

            await secondOwner.RunFromTemplateAsync(
                connectionString,
                PostgreSqlTestServer.NewPrefix("second_owner_clone_after_cleanup"),
                templatePrefix,
                static (_, _) => throw new InvalidOperationException("The second owner should keep its cached template."),
                async (cloneConnectionString, cancellationToken) =>
                    Assert.Equal(2, await PostgreSqlTestServer.ReadScalarAsync<int>(
                        cloneConnectionString,
                        "SELECT owner FROM owner_probe;")));
        }
        finally
        {
            await PostgreSqlTestServer.CleanupAsync(firstOwner);
            await PostgreSqlTestServer.CleanupAsync(secondOwner);
        }

        Assert.Equal(1, firstInitializationCount);
        Assert.Equal(1, secondInitializationCount);
    }

    [Fact]
    public async Task CleanupAllAsync_WaitsForActiveCloneAndReportsConcurrentCleanup()
    {
        var connectionString = await PostgreSqlTestServer.GetRequiredConnectionStringAsync();
        var fixture = new PostgreSqlFixture();
        var templatePrefix = PostgreSqlTestServer.NewPrefix("active_cleanup_template");
        var cloneEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseClone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? cloneDatabaseName = null;
        Task? cloneRun = null;
        Task<TemplateCleanupResult>? cleanupRun = null;

        try
        {
            cloneRun = fixture.RunFromTemplateAsync(
                connectionString,
                PostgreSqlTestServer.NewPrefix("active_cleanup_clone"),
                templatePrefix,
                async (templateConnectionString, cancellationToken) =>
                    await PostgreSqlTestServer.ExecuteAsync(
                        templateConnectionString,
                        "CREATE TABLE active_cleanup_probe (value integer NOT NULL);"),
                async (cloneConnectionString, cancellationToken) =>
                {
                    cloneDatabaseName = await PostgreSqlTestServer.ReadScalarAsync<string>(
                        cloneConnectionString,
                        "SELECT current_database();");
                    cloneEntered.TrySetResult();
                    await releaseClone.Task.WaitAsync(cancellationToken);
                });

            await cloneEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cleanupRun = fixture.CleanupAllAsync().AsTask();

            var rejectedRun = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.RunFromTemplateAsync(
                connectionString,
                PostgreSqlTestServer.NewPrefix("active_cleanup_rejected_clone"),
                templatePrefix,
                static (_, _) => Task.CompletedTask,
                static (_, _) => Task.CompletedTask));
            Assert.Contains("being cleaned up", rejectedRun.Message, StringComparison.Ordinal);

            var concurrentCleanup = await Assert.ThrowsAsync<TemplateCleanupException>(() =>
                fixture.CleanupAllAsync().AsTask());
            Assert.Single(concurrentCleanup.Result.Outcomes);
            Assert.Equal(TemplateCleanupStatus.AlreadyInProgress, concurrentCleanup.Result.Outcomes[0].Status);

            releaseClone.TrySetResult();
            await cloneRun;
            var cleanup = await cleanupRun.WaitAsync(TimeSpan.FromSeconds(20));

            Assert.Equal(1, cleanup.RegisteredTemplateCount);
            Assert.Equal(1, cleanup.RemovedTemplateCount);
            Assert.NotNull(cloneDatabaseName);
            Assert.False(await PostgreSqlTestServer.DatabaseExistsAsync(connectionString, cloneDatabaseName));
            Assert.False(await PostgreSqlTestServer.DatabaseExistsAsync(connectionString, cleanup.Outcomes[0].DatabaseName));
        }
        finally
        {
            releaseClone.TrySetResult();
            if (cloneRun is not null)
            {
                await cloneRun;
            }

            if (cleanupRun is not null)
            {
                await cleanupRun;
            }

            await PostgreSqlTestServer.CleanupAsync(fixture);
        }
    }

    [Fact]
    public async Task CleanupTemplatesAsync_IsSelectiveAndLongPrefixesRemainDistinct()
    {
        var connectionString = await PostgreSqlTestServer.GetRequiredConnectionStringAsync();
        var fixture = new PostgreSqlFixture();
        var sharedPrefix = new string('l', 70);
        var firstTemplatePrefix = $"{sharedPrefix}_first";
        var secondTemplatePrefix = $"{sharedPrefix}_second";

        try
        {
            await CreateTemplateAsync(firstTemplatePrefix, 11);
            await CreateTemplateAsync(secondTemplatePrefix, 22);
            var templateNames = await GetDatabaseNamesWithPrefixAsync(connectionString, sharedPrefix[..46]);

            Assert.Equal(2, templateNames.Count);

            var firstCleanup = await fixture.CleanupTemplatesAsync(connectionString, [firstTemplatePrefix]);

            Assert.Equal(1, firstCleanup.RegisteredTemplateCount);
            Assert.Equal(1, firstCleanup.RemovedTemplateCount);
            Assert.Single(firstCleanup.Outcomes);
            Assert.False(await PostgreSqlTestServer.DatabaseExistsAsync(connectionString, firstCleanup.Outcomes[0].DatabaseName));
            var remainingTemplateName = templateNames.Single(name => name != firstCleanup.Outcomes[0].DatabaseName);
            Assert.True(await PostgreSqlTestServer.DatabaseExistsAsync(connectionString, remainingTemplateName));

            await fixture.RunFromTemplateAsync(
                connectionString,
                PostgreSqlTestServer.NewPrefix("long_prefix_survivor"),
                secondTemplatePrefix,
                static (_, _) => throw new InvalidOperationException("The selected cleanup must preserve the other template."),
                async (cloneConnectionString, cancellationToken) =>
                    Assert.Equal(22, await PostgreSqlTestServer.ReadScalarAsync<int>(
                        cloneConnectionString,
                        "SELECT value FROM long_prefix_probe;")));
        }
        finally
        {
            await PostgreSqlTestServer.CleanupAsync(fixture);
        }

        async Task CreateTemplateAsync(string prefix, int value)
        {
            await fixture.RunFromTemplateAsync(
                connectionString,
                PostgreSqlTestServer.NewPrefix("long_prefix_clone"),
                prefix,
                async (templateConnectionString, cancellationToken) =>
                    await PostgreSqlTestServer.ExecuteAsync(
                        templateConnectionString,
                        $"CREATE TABLE long_prefix_probe (value integer NOT NULL); INSERT INTO long_prefix_probe VALUES ({value});"),
                async (cloneConnectionString, cancellationToken) =>
                    Assert.Equal(value, await PostgreSqlTestServer.ReadScalarAsync<int>(
                        cloneConnectionString,
                        "SELECT value FROM long_prefix_probe;")));
        }
    }

    [Fact]
    public async Task CleanupAllAsync_ReportsCanceledCleanupAndAllowsRetry()
    {
        var connectionString = await PostgreSqlTestServer.GetRequiredConnectionStringAsync();
        var fixture = new PostgreSqlFixture();
        var templatePrefix = PostgreSqlTestServer.NewPrefix("cleanup_retry");

        try
        {
            await fixture.RunFromTemplateAsync(
                connectionString,
                PostgreSqlTestServer.NewPrefix("cleanup_retry_clone"),
                templatePrefix,
                async (templateConnectionString, cancellationToken) =>
                    await PostgreSqlTestServer.ExecuteAsync(templateConnectionString, "CREATE TABLE retry_probe (value integer);"),
                static (_, _) => Task.CompletedTask);

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var exception = await Assert.ThrowsAsync<TemplateCleanupException>(async () =>
                await fixture.CleanupAllAsync(cancellation.Token));

            Assert.Equal(1, exception.Result.RegisteredTemplateCount);
            Assert.Equal(0, exception.Result.RemovedTemplateCount);
            Assert.Single(exception.Result.Failures);
            Assert.Contains("CanceledException", exception.Result.Failures[0].Failure, StringComparison.Ordinal);

            var retry = await fixture.CleanupAllAsync();
            Assert.Equal(1, retry.RegisteredTemplateCount);
            Assert.Equal(1, retry.RemovedTemplateCount);
            Assert.Empty(retry.IncompleteOutcomes);
        }
        finally
        {
            await PostgreSqlTestServer.CleanupAsync(fixture);
        }
    }

    [Fact]
    public async Task RunFreshAsync_PropagatesActionFailureAndDropsTheDatabase()
    {
        var connectionString = await PostgreSqlTestServer.GetRequiredConnectionStringAsync();
        var fixture = new PostgreSqlFixture();
        var expected = new InvalidOperationException("fresh callback failure sentinel");
        string? databaseName = null;

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.RunFreshAsync(
            connectionString,
            PostgreSqlTestServer.NewPrefix("fresh_failure"),
            async (freshConnectionString, cancellationToken) =>
            {
                databaseName = await PostgreSqlTestServer.ReadScalarAsync<string>(
                    freshConnectionString,
                    "SELECT current_database();");
                await PostgreSqlTestServer.ExecuteAsync(
                    freshConnectionString,
                    "CREATE TABLE failure_probe (value integer NOT NULL);");
                throw expected;
            }));

        Assert.Same(expected, actual);
        Assert.NotNull(databaseName);
        Assert.False(await PostgreSqlTestServer.DatabaseExistsAsync(connectionString, databaseName));
    }

    [Fact]
    public async Task RunFromTemplateAsync_FailedInitializationIsCleanedAndCanBeRetried()
    {
        var connectionString = await PostgreSqlTestServer.GetRequiredConnectionStringAsync();
        var fixture = new PostgreSqlFixture();
        var templatePrefix = PostgreSqlTestServer.NewPrefix("initializer_retry");
        var initializationCount = 0;
        var expected = new InvalidOperationException("template initialization failure sentinel");

        try
        {
            var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.RunFromTemplateAsync(
                connectionString,
                PostgreSqlTestServer.NewPrefix("initializer_retry_clone"),
                templatePrefix,
                async (templateConnectionString, cancellationToken) =>
                {
                    Interlocked.Increment(ref initializationCount);
                    await PostgreSqlTestServer.ExecuteAsync(
                        templateConnectionString,
                        "CREATE TABLE initialization_probe (value integer);");
                    throw expected;
                },
                static (_, _) => Task.CompletedTask));

            Assert.Same(expected, actual);
            Assert.False(await HasDatabaseWithPrefixAsync(connectionString, templatePrefix));

            await fixture.RunFromTemplateAsync(
                connectionString,
                PostgreSqlTestServer.NewPrefix("initializer_retry_clone_after_failure"),
                templatePrefix,
                async (templateConnectionString, cancellationToken) =>
                {
                    Interlocked.Increment(ref initializationCount);
                    await PostgreSqlTestServer.ExecuteAsync(
                        templateConnectionString,
                        "CREATE TABLE initialization_probe (value integer NOT NULL); INSERT INTO initialization_probe VALUES (29);");
                },
                async (cloneConnectionString, cancellationToken) =>
                    Assert.Equal(29, await PostgreSqlTestServer.ReadScalarAsync<int>(
                        cloneConnectionString,
                        "SELECT value FROM initialization_probe;")));
        }
        finally
        {
            await PostgreSqlTestServer.CleanupAsync(fixture);
        }

        Assert.Equal(2, initializationCount);
    }

    [Fact]
    public async Task RunFromTemplateAsync_FailedRollbackDropIsRetainedForCleanupRetry()
    {
        var connectionString = await PostgreSqlTestServer.GetRequiredConnectionStringAsync();
        var fixture = new PostgreSqlFixture();
        var templatePrefix = PostgreSqlTestServer.NewPrefix("initializer_drop_retry");
        var templateDatabaseNames = new List<string>();
        var initializationCount = 0;

        try
        {
            var rollbackFailure = await Assert.ThrowsAsync<PostgresException>(() => fixture.RunFromTemplateAsync(
                connectionString,
                PostgreSqlTestServer.NewPrefix("initializer_drop_retry_clone"),
                templatePrefix,
                async (templateConnectionString, cancellationToken) =>
                {
                    Interlocked.Increment(ref initializationCount);
                    var databaseName = await PostgreSqlTestServer.ReadScalarAsync<string>(
                        templateConnectionString,
                        "SELECT current_database();");
                    templateDatabaseNames.Add(databaseName);
                    await SetDatabaseTemplateFlagAsync(connectionString, databaseName, true);
                    throw new InvalidOperationException("template initializer failure after blocking rollback");
                },
                static (_, _) => Task.CompletedTask));

            Assert.Contains("template", rollbackFailure.Message, StringComparison.OrdinalIgnoreCase);
            var databaseName = Assert.Single(templateDatabaseNames);
            var cleanupFailure = await Assert.ThrowsAsync<TemplateCleanupException>(async () =>
                await fixture.CleanupAllAsync());

            Assert.Equal(1, cleanupFailure.Result.RegisteredTemplateCount);
            Assert.Equal(0, cleanupFailure.Result.RemovedTemplateCount);
            var failedOutcome = Assert.Single(cleanupFailure.Result.Failures);
            Assert.Equal(TemplateCleanupStatus.Failed, failedOutcome.Status);
            Assert.Equal(databaseName, failedOutcome.DatabaseName);
            Assert.NotNull(failedOutcome.Failure);
            Assert.True(await PostgreSqlTestServer.DatabaseExistsAsync(connectionString, databaseName));

            await SetDatabaseTemplateFlagAsync(connectionString, databaseName, false);
            var retry = await fixture.CleanupAllAsync();

            Assert.Equal(1, retry.RegisteredTemplateCount);
            Assert.Equal(1, retry.RemovedTemplateCount);
            Assert.Equal(databaseName, Assert.Single(retry.Outcomes).DatabaseName);
            Assert.False(await PostgreSqlTestServer.DatabaseExistsAsync(connectionString, databaseName));

            await fixture.RunFromTemplateAsync(
                connectionString,
                PostgreSqlTestServer.NewPrefix("initializer_drop_retry_clone_after_cleanup"),
                templatePrefix,
                async (templateConnectionString, cancellationToken) =>
                {
                    Interlocked.Increment(ref initializationCount);
                    var reinitializedDatabaseName = await PostgreSqlTestServer.ReadScalarAsync<string>(
                        templateConnectionString,
                        "SELECT current_database();");
                    templateDatabaseNames.Add(reinitializedDatabaseName);
                    await PostgreSqlTestServer.ExecuteAsync(
                        templateConnectionString,
                        "CREATE TABLE rollback_retry_probe (value integer NOT NULL); INSERT INTO rollback_retry_probe VALUES (41);");
                },
                async (cloneConnectionString, cancellationToken) =>
                    Assert.Equal(41, await PostgreSqlTestServer.ReadScalarAsync<int>(
                        cloneConnectionString,
                        "SELECT value FROM rollback_retry_probe;")));

            Assert.Equal(2, initializationCount);
        }
        finally
        {
            foreach (var databaseName in templateDatabaseNames)
            {
                if (await PostgreSqlTestServer.DatabaseExistsAsync(connectionString, databaseName))
                {
                    await SetDatabaseTemplateFlagAsync(connectionString, databaseName, false);
                }
            }

            await PostgreSqlTestServer.CleanupAsync(fixture);
        }
    }

    [Fact]
    public async Task ProcessExit_RemovesTemplateOwnedByNormallyExitingChildProcess()
    {
        var connectionString = await PostgreSqlTestServer.GetRequiredConnectionStringAsync();
        var templatePrefix = PostgreSqlTestServer.NewPrefix("process_exit_template");
        var processName = "ForgeTrust.AppSurface.PostgreSql.Testing.Tests"
            + (OperatingSystem.IsWindows() ? ".exe" : string.Empty);
        var startInfo = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, processName))
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("--process-exit-fallback-child");
        startInfo.Environment[PostgreSqlTestServer.ConnectionEnvironmentVariable] = connectionString;
        startInfo.Environment[PostgreSqlFixtureTestProcess.TemplatePrefixEnvironmentVariable] = templatePrefix;

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The PostgreSQL fixture child process did not start.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException("The PostgreSQL fixture child process did not exit within 30 seconds.");
        }

        var output = await standardOutput;
        var error = await standardError;
        Assert.True(process.ExitCode == 0, $"Child process exited {process.ExitCode}. stdout: {output} stderr: {error}");

        var templateDatabaseName = output.Trim();
        Assert.StartsWith(templatePrefix[..Math.Min(templatePrefix.Length, 46)], templateDatabaseName, StringComparison.Ordinal);
        Assert.False(await PostgreSqlTestServer.DatabaseExistsAsync(connectionString, templateDatabaseName));
    }

    [Fact]
    public async Task Cancellation_DoesNotCancelSharedInitializationAndFreshDatabaseIsDropped()
    {
        var connectionString = await PostgreSqlTestServer.GetRequiredConnectionStringAsync();
        var fixture = new PostgreSqlFixture();
        var templatePrefix = PostgreSqlTestServer.NewPrefix("cancellation_template");
        var initializationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseInitialization = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var initializationCount = 0;
        var initializationTokenCanceled = false;
        string? freshDatabaseName = null;

        try
        {
            using var waiterCancellation = new CancellationTokenSource();
            var canceledWaiter = fixture.RunFromTemplateAsync(
                connectionString,
                PostgreSqlTestServer.NewPrefix("cancelled_waiter_clone"),
                templatePrefix,
                async (templateConnectionString, cancellationToken) =>
                {
                    Interlocked.Increment(ref initializationCount);
                    initializationEntered.TrySetResult();
                    await releaseInitialization.Task.WaitAsync(cancellationToken);
                    initializationTokenCanceled = cancellationToken.IsCancellationRequested;
                    await PostgreSqlTestServer.ExecuteAsync(
                        templateConnectionString,
                        "CREATE TABLE cancellation_probe (value integer NOT NULL); INSERT INTO cancellation_probe VALUES (31);");
                },
                static (_, _) => Task.CompletedTask,
                waiterCancellation.Token);

            await initializationEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var survivingWaiter = fixture.RunFromTemplateAsync(
                connectionString,
                PostgreSqlTestServer.NewPrefix("surviving_waiter_clone"),
                templatePrefix,
                static (_, _) => throw new InvalidOperationException("Shared initialization must only run once."),
                async (cloneConnectionString, cancellationToken) =>
                    Assert.Equal(31, await PostgreSqlTestServer.ReadScalarAsync<int>(
                        cloneConnectionString,
                        "SELECT value FROM cancellation_probe;")));

            waiterCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledWaiter);
            Assert.False(initializationTokenCanceled);
            releaseInitialization.TrySetResult();
            await survivingWaiter;

            using var actionCancellation = new CancellationTokenSource();
            var enteredFreshAction = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var freshRun = fixture.RunFreshAsync(
                connectionString,
                PostgreSqlTestServer.NewPrefix("cancelled_fresh"),
                async (freshConnectionString, cancellationToken) =>
                {
                    freshDatabaseName = await PostgreSqlTestServer.ReadScalarAsync<string>(
                        freshConnectionString,
                        "SELECT current_database();");
                    enteredFreshAction.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                },
                actionCancellation.Token);

            await enteredFreshAction.Task.WaitAsync(TimeSpan.FromSeconds(10));
            actionCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => freshRun);
        }
        finally
        {
            releaseInitialization.TrySetResult();
            await PostgreSqlTestServer.CleanupAsync(fixture);
        }

        Assert.Equal(1, initializationCount);
        Assert.False(initializationTokenCanceled);
        Assert.NotNull(freshDatabaseName);
        Assert.False(await PostgreSqlTestServer.DatabaseExistsAsync(connectionString, freshDatabaseName));
    }

    [Fact]
    public async Task RunFreshAsync_CancellationAfterCatalogLockAcquisitionCancelsCreateAndCleansDatabase()
    {
        const long databaseCatalogLockKey = 5_300_219_492_281_001;
        var connectionString = await PostgreSqlTestServer.GetRequiredConnectionStringAsync();
        var maintenanceConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Database = "postgres",
            Pooling = false,
        }.ConnectionString;
        var applicationName = PostgreSqlTestServer.NewPrefix("cancel_after_catalog_lock");
        var fixtureConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
        {
            ApplicationName = applicationName,
            Pooling = false,
        }.ConnectionString;
        var databaseNamePrefix = PostgreSqlTestServer.NewPrefix("cancel_create_after_lock");
        var ownedDatabasePrefix = databaseNamePrefix[..Math.Min(databaseNamePrefix.Length, 46)];
        var actionInvoked = false;
        var fixture = new PostgreSqlFixture();
        var blockerReleased = false;

        await using var blockerConnection = new NpgsqlConnection(maintenanceConnectionString);
        await blockerConnection.OpenAsync();
        await using var blockerTransaction = await blockerConnection.BeginTransactionAsync();
        await using (var blockerCommand = new NpgsqlCommand(
            "LOCK TABLE pg_catalog.pg_database IN SHARE MODE;",
            blockerConnection,
            blockerTransaction))
        {
            await blockerCommand.ExecuteNonQueryAsync();
        }

        var createRun = fixture.RunFreshAsync(
            fixtureConnectionString,
            databaseNamePrefix,
            (_, _) =>
            {
                actionInvoked = true;
                return Task.CompletedTask;
            });

        try
        {
            var backendPid = await WaitForBlockedCreateAsync(
                maintenanceConnectionString,
                applicationName,
                createRun);
            Assert.True(await CancelBackendAsync(maintenanceConnectionString, backendPid));
            await WaitForCreateCommandToFinishAsync(maintenanceConnectionString, applicationName);

            await blockerTransaction.RollbackAsync();
            blockerReleased = true;
            var cancellation = await Assert.ThrowsAsync<PostgresException>(() => createRun);
            Assert.Equal(PostgresErrorCodes.QueryCanceled, cancellation.SqlState);
            Assert.False(actionInvoked);
            Assert.Empty(await GetDatabaseNamesWithPrefixAsync(maintenanceConnectionString, ownedDatabasePrefix));

            await using var lockProbe = new NpgsqlConnection(maintenanceConnectionString);
            await lockProbe.OpenAsync();
            await using var acquireProbe = new NpgsqlCommand(
                $"SELECT pg_try_advisory_lock({databaseCatalogLockKey});",
                lockProbe);
            Assert.True((bool)(await acquireProbe.ExecuteScalarAsync())!);
            await using var releaseProbe = new NpgsqlCommand(
                $"SELECT pg_advisory_unlock({databaseCatalogLockKey});",
                lockProbe);
            Assert.True((bool)(await releaseProbe.ExecuteScalarAsync())!);
        }
        finally
        {
            if (!blockerReleased)
            {
                await blockerTransaction.RollbackAsync();
            }

            if (!createRun.IsCompleted)
            {
                await CancelNamedCreateAsync(maintenanceConnectionString, applicationName);
            }

            try
            {
                await createRun;
            }
            catch (PostgresException)
            {
                // Preserve the test assertion while ensuring fixture teardown has completed.
            }
        }
    }

    [Fact]
    public async Task WithClusterInitializationLockAsync_SerializesClusterWideInitialization()
    {
        var connectionString = await PostgreSqlTestServer.GetRequiredConnectionStringAsync();
        var firstActionEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstAction = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondActionEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = PostgreSqlFixture.WithClusterInitializationLockAsync(connectionString, async cancellationToken =>
        {
            firstActionEntered.TrySetResult();
            await releaseFirstAction.Task.WaitAsync(cancellationToken);
        });

        await firstActionEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = PostgreSqlFixture.WithClusterInitializationLockAsync(connectionString, cancellationToken =>
        {
            secondActionEntered.TrySetResult();
            return Task.CompletedTask;
        });

        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(200));
            Assert.False(secondActionEntered.Task.IsCompleted);
            releaseFirstAction.TrySetResult();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(secondActionEntered.Task.IsCompletedSuccessfully);
        }
        finally
        {
            releaseFirstAction.TrySetResult();
        }
    }

    private static async Task<bool> HasDatabaseWithPrefixAsync(string connectionString, string prefix)
    {
        var maintenanceConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Database = "postgres",
            Pooling = false,
        }.ConnectionString;

        await using var connection = new NpgsqlConnection(maintenanceConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM pg_database WHERE left(datname, length(@prefix)) = @prefix);",
            connection);
        command.Parameters.AddWithValue("prefix", prefix[..Math.Min(prefix.Length, 46)]);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<IReadOnlyList<string>> GetDatabaseNamesWithPrefixAsync(string connectionString, string prefix)
    {
        var maintenanceConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Database = "postgres",
            Pooling = false,
        }.ConnectionString;

        await using var connection = new NpgsqlConnection(maintenanceConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT datname FROM pg_database WHERE left(datname, length(@prefix)) = @prefix ORDER BY datname;",
            connection);
        command.Parameters.AddWithValue("prefix", prefix);
        await using var reader = await command.ExecuteReaderAsync();
        var names = new List<string>();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedCaseDrop_RetainsTheDatabaseUntilHostCleanupCanRetry(bool useTemplate)
    {
        var connection = await PostgreSqlTestServer.GetRequiredConnectionStringAsync();
        var fixture = new PostgreSqlFixture();
        var templatePrefix = PostgreSqlTestServer.NewPrefix("parent");
        var casePrefix = PostgreSqlTestServer.NewPrefix("case");
        var actionError = new InvalidOperationException("case failed");
        string? caseName = null;
        string? templateName = null;

        async Task BlockDropAsync(string database, CancellationToken token)
        {
            caseName = new NpgsqlConnectionStringBuilder(database).Database!;
            await SetDatabaseTemplateFlagAsync(connection, caseName, true);
            throw actionError;
        }

        try
        {
            var error = await Assert.ThrowsAsync<PostgreSqlDatabaseCleanupException>(() => useTemplate
                ? fixture.RunFromTemplateAsync(connection, casePrefix, templatePrefix, (database, token) =>
                {
                    templateName = new NpgsqlConnectionStringBuilder(database).Database;
                    return Task.CompletedTask;
                }, BlockDropAsync)
                : fixture.RunFreshAsync(connection, casePrefix, BlockDropAsync));
            Assert.Equal(caseName, error.Database.Name);
            Assert.Same(actionError, error.OperationError);
            Assert.IsType<PostgresException>(error.CleanupError);

            var blocked = await Assert.ThrowsAsync<TemplateCleanupException>(() => fixture.CleanupAllAsync().AsTask());
            Assert.Single(blocked.Result.Failures);
            Assert.True(await PostgreSqlTestServer.DatabaseExistsAsync(connection, caseName!));
            if (useTemplate)
            {
                Assert.True(await PostgreSqlTestServer.DatabaseExistsAsync(connection, templateName!));
                Assert.Equal(templateName, Assert.Single(blocked.Result.Outcomes).DatabaseName);
            }
            else
            {
                Assert.Empty(blocked.Result.Outcomes);
                Assert.Equal(caseName, Assert.Single(blocked.Result.FreshDatabaseOutcomes).DatabaseName);
            }

            await SetDatabaseTemplateFlagAsync(connection, caseName!, false);
            var retry = await fixture.CleanupAllAsync();
            Assert.Equal(useTemplate ? 1 : 0, retry.RemovedTemplateCount);
            Assert.False(await PostgreSqlTestServer.DatabaseExistsAsync(connection, caseName!));
            if (useTemplate)
            {
                Assert.False(await PostgreSqlTestServer.DatabaseExistsAsync(connection, templateName!));
            }

            await error.Database.DisposeAsync();
            Assert.Empty((await fixture.CleanupAllAsync()).FreshDatabaseOutcomes);
        }
        finally
        {
            if (caseName != null && await PostgreSqlTestServer.DatabaseExistsAsync(connection, caseName))
            {
                await SetDatabaseTemplateFlagAsync(connection, caseName, false);
            }

            await fixture.CleanupAllAsync();
        }
    }

    [Fact]
    public async Task CanceledCreate_ExposesVerifiedLeaseWhenRollbackFails()
    {
        var connection = await PostgreSqlTestServer.GetRequiredConnectionStringAsync();
        using var cancel = new CancellationTokenSource();
        var error = await Assert.ThrowsAsync<PostgreSqlDatabaseCleanupException>(() => PostgreSqlDatabaseLease.CreateAsync(
            connection, "rollback", async (name, _) =>
            {
                await SetDatabaseTemplateFlagAsync(connection, name, true);
                cancel.Cancel();
            }, cancel.Token));

        try
        {
            Assert.IsAssignableFrom<OperationCanceledException>(error.OperationError);
            Assert.IsType<PostgresException>(error.CleanupError);
            var stillExists = await Assert.ThrowsAsync<PostgreSqlDatabaseOwnershipException>(() =>
                error.Database.DropAsync(_ => ValueTask.CompletedTask, CancellationToken.None).AsTask());
            Assert.Contains("still exists after drop", stillExists.Message);
        }
        finally
        {
            await SetDatabaseTemplateFlagAsync(connection, error.Database.Name, false);
            await error.Database.DisposeAsync();
        }

        Assert.False(await PostgreSqlTestServer.DatabaseExistsAsync(connection, error.Database.Name));
    }

    private static async Task SetDatabaseTemplateFlagAsync(string connectionString, string databaseName, bool isTemplate)
    {
        var maintenanceConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Database = "postgres",
            Pooling = false,
        }.ConnectionString;

        await using var connection = new NpgsqlConnection(maintenanceConnectionString);
        await connection.OpenAsync();
        using var builder = new NpgsqlCommandBuilder();
        var quotedDatabaseName = builder.QuoteIdentifier(databaseName);
        await using var command = new NpgsqlCommand(
            $"ALTER DATABASE {quotedDatabaseName} WITH IS_TEMPLATE {isTemplate};",
            connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> WaitForBlockedCreateAsync(
        string connectionString,
        string applicationName,
        Task createRun)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(timeout.Token);

        const string query = """
            SELECT activity.pid
            FROM pg_catalog.pg_stat_activity AS activity
            WHERE activity.application_name = @application_name
              AND activity.state = 'active'
              AND activity.query LIKE 'CREATE DATABASE %'
              AND activity.wait_event_type = 'Lock'
              AND EXISTS (
                  SELECT 1
                  FROM pg_catalog.pg_locks AS advisory_lock
                  WHERE advisory_lock.pid = activity.pid
                    AND advisory_lock.locktype = 'advisory'
                    AND advisory_lock.mode = 'ExclusiveLock'
                    AND advisory_lock.granted);
            """;

        while (true)
        {
            if (createRun.IsCompleted)
            {
                await createRun;
                throw new InvalidOperationException("The database CREATE command completed before it could be canceled.");
            }

            await using var command = new NpgsqlCommand(query, connection);
            command.Parameters.AddWithValue("application_name", applicationName);
            var backendPid = await command.ExecuteScalarAsync(timeout.Token);
            if (backendPid is int pid)
            {
                return pid;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token);
        }
    }

    private static async Task WaitForCreateCommandToFinishAsync(string connectionString, string applicationName)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(timeout.Token);
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS (
                SELECT 1
                FROM pg_catalog.pg_stat_activity
                WHERE application_name = @application_name
                  AND state = 'active'
                  AND query LIKE 'CREATE DATABASE %');
            """, connection);
        command.Parameters.AddWithValue("application_name", applicationName);

        while ((bool)(await command.ExecuteScalarAsync(timeout.Token))!)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token);
        }
    }

    private static async Task<bool> CancelBackendAsync(string connectionString, int backendPid)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT pg_cancel_backend(@backend_pid);", connection);
        command.Parameters.AddWithValue("backend_pid", backendPid);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static async Task CancelNamedCreateAsync(string connectionString, string applicationName)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT pg_cancel_backend(pid)
            FROM pg_catalog.pg_stat_activity
            WHERE application_name = @application_name
              AND state = 'active'
              AND query LIKE 'CREATE DATABASE %';
            """, connection);
        command.Parameters.AddWithValue("application_name", applicationName);
        await command.ExecuteScalarAsync();
    }
}
