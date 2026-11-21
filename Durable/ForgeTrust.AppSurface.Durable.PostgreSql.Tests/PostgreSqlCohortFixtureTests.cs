using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

public sealed class PostgreSqlCohortFixtureTests
{
    private static readonly TimeSpan TestOperationTimeout = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task InitializeAsync_SharesConcurrentInitializationAndCreatesReadyClones()
    {
        await using var anchor = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var initializationReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueInitialization = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var preparationCount = 0;
        var options = TemplateOptions(anchor);
        await using var fixture = new PostgreSqlCohortFixtureCore(
            options,
            afterTemplatePreparedAsync: async _ =>
            {
                Interlocked.Increment(ref preparationCount);
                initializationReached.SetResult();
                await continueInitialization.Task;
            });

        var firstInitialization = fixture.InitializeAsync();
        try
        {
            await initializationReached.Task.WaitAsync(TestOperationTimeout);
            var secondInitialization = fixture.InitializeAsync();
            Assert.Same(firstInitialization, secondInitialization);
        }
        finally
        {
            continueInitialization.TrySetResult();
        }

        await firstInitialization.WaitAsync(TestOperationTimeout);
        await fixture.InitializeAsync().WaitAsync(TestOperationTimeout);

        await using var first = await fixture.CreateReadyDatabaseAsync("fixture-tests", "issue-931");
        await using var second = await fixture.CreateReadyDatabaseAsync("fixture-tests", "issue-931");

        Assert.Equal(1, preparationCount);
        Assert.True(first.Status.IsCompatible);
        Assert.Equal(first.RuntimeEpoch, first.Status.ActiveRuntimeEpoch);
        Assert.Equal(second.RuntimeEpoch, second.Status.ActiveRuntimeEpoch);
        Assert.NotEqual(first.Status.StoreId, second.Status.StoreId);
        Assert.NotEqual(first.RuntimeEpoch, second.RuntimeEpoch);
    }

    [Fact]
    public async Task DisposeAsync_RacingInitializationWaitsForTemplateAndRejectsAcquisition()
    {
        await using var anchor = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var preparationReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continuePreparation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? templateName = null;
        await using var fixture = new PostgreSqlCohortFixtureCore(
            TemplateOptions(anchor),
            afterTemplatePreparedAsync: async template =>
            {
                templateName = new NpgsqlConnectionStringBuilder(template.ConnectionString).Database;
                preparationReached.SetResult();
                await continuePreparation.Task;
            });

        var initialization = fixture.InitializeAsync();
        Task? disposal = null;
        try
        {
            await preparationReached.Task.WaitAsync(TestOperationTimeout);
            disposal = fixture.DisposeAsync().AsTask();
            var rejectedAcquisition = fixture.CreateReadyDatabaseAsync("fixture-tests", "issue-931").AsTask();

            var rejection = await Assert.ThrowsAsync<PostgreSqlFixtureException>(
                () => rejectedAcquisition.WaitAsync(TestOperationTimeout));
            Assert.Contains("closing", rejection.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(disposal.IsCompleted);
        }
        finally
        {
            continuePreparation.TrySetResult();
        }

        await initialization.WaitAsync(TestOperationTimeout);
        Assert.NotNull(disposal);
        await disposal;

        Assert.NotNull(templateName);
        Assert.False(await DatabaseExistsAsync(anchor, templateName));
    }

    [Fact]
    public async Task DisposeAsync_TimesOutWithActiveHandlesAndCompletesAfterTheyClose()
    {
        await using var anchor = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        string? templateName = null;
        await using var fixture = new PostgreSqlCohortFixtureCore(
            TemplateOptions(anchor),
            fixtureDrainTimeout: TimeSpan.FromMilliseconds(100),
            afterTemplatePreparedAsync: template =>
            {
                templateName = new NpgsqlConnectionStringBuilder(template.ConnectionString).Database;
                return ValueTask.CompletedTask;
            });
        await fixture.InitializeAsync().WaitAsync(TestOperationTimeout);

        await using var first = await fixture.CreateReadyDatabaseAsync("fixture-tests", "issue-931");
        await using var second = await fixture.CreateReadyDatabaseAsync("fixture-tests", "issue-931");
        var additionalSource = first.Database.CreateDataSource();

        var disposal = fixture.DisposeAsync().AsTask();
        var repeatedDisposal = fixture.DisposeAsync().AsTask();
        var timeout = await Assert.ThrowsAsync<PostgreSqlFixtureException>(() => disposal);
        var repeatedTimeout = await Assert.ThrowsAsync<PostgreSqlFixtureException>(() => repeatedDisposal);
        Assert.Contains("still has case databases", timeout.Message, StringComparison.Ordinal);
        Assert.Contains("still has case databases", repeatedTimeout.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<PostgreSqlFixtureException>(() =>
            fixture.CreateReadyDatabaseAsync("fixture-tests", "issue-931").AsTask().WaitAsync(TestOperationTimeout));
        Assert.NotNull(templateName);
        Assert.True(await DatabaseExistsAsync(anchor, templateName));

        await first.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => additionalSource.OpenConnectionAsync().AsTask());
        Assert.True(await DatabaseExistsAsync(anchor, templateName));
        await second.DisposeAsync();
        await fixture.DisposeAsync();

        Assert.False(await DatabaseExistsAsync(anchor, templateName));
    }

    [Fact]
    public async Task CreateReadyDatabaseAsync_FailedAcquireReleasesLeaseAndPreservesSafeDiagnostics()
    {
        await using var anchor = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var failure = new InvalidOperationException("Host=fixture-private.invalid;Password=private-fixture-sentinel");
        var options = TemplateOptions(anchor);
        await using var fixture = new PostgreSqlCohortFixtureCore(
            options,
            fixtureDrainTimeout: TimeSpan.FromMilliseconds(100),
            beforeCaseDatabaseCreateAsync: () => ValueTask.FromException(failure));

        var acquireError = await Assert.ThrowsAsync<PostgreSqlFixtureException>(
            () => fixture.CreateReadyDatabaseAsync("fixture-tests", "issue-931").AsTask());
        Assert.DoesNotContain("fixture-private.invalid", acquireError.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private-fixture-sentinel", acquireError.ToString(), StringComparison.Ordinal);

        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task CreateReadyDatabaseAsync_ActivationFailureDisposesCloneReleasesLeaseAndCleansTemplate()
    {
        await using var anchor = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var seededEpoch = Guid.NewGuid();
        string? templateName = null;
        var timingPath = Path.Combine(Path.GetTempPath(), $"postgresql-cohort-failure-{Guid.NewGuid():N}.csv");
        var options = PostgreSqlCohortFixtureOptions.Parse(
            nameof(PostgreSqlCohortFixtureTests),
            "template",
            anchor.ConnectionString,
            timingPath);

        try
        {
            await using var fixture = new PostgreSqlCohortFixtureCore(
                options,
                fixtureDrainTimeout: TimeSpan.FromMilliseconds(100),
                afterTemplatePreparedAsync: async template =>
                {
                    templateName = new NpgsqlConnectionStringBuilder(template.ConnectionString).Database;
                    await new PostgreSqlDurableRuntimeSchemaManager(template.DataSource)
                        .InitializeRuntimeEpochAsync(seededEpoch, "fixture-tests", "issue-931-invalid-template");
                });

            await fixture.InitializeAsync().WaitAsync(TestOperationTimeout);

            var activationError = await Assert.ThrowsAsync<PostgreSqlFixtureException>(
                () => fixture.CreateReadyDatabaseAsync("fixture-tests", "issue-931").AsTask());

            Assert.Contains("active runtime epoch", activationError.Message, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(templateName);

            await fixture.DisposeAsync().AsTask().WaitAsync(TestOperationTimeout);

            Assert.False(await DatabaseExistsAsync(anchor, templateName));
            var phases = (await File.ReadAllLinesAsync(timingPath))
                .Skip(1)
                .Select(row => row.Split(',')[0])
                .ToArray();
            Assert.Equal(1, phases.Count(phase => phase == "database_setup"));
            Assert.Equal(1, phases.Count(phase => phase == "database_teardown"));
        }
        finally
        {
            File.Delete(timingPath);
        }
    }

    [Fact]
    public async Task DisposeAsync_WritesSetupAndTeardownTimingRowsForReadyAndEmptyDatabases()
    {
        await using var anchor = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var timingPath = Path.Combine(Path.GetTempPath(), $"postgresql-fixture-{Guid.NewGuid():N}.csv");
        var options = PostgreSqlCohortFixtureOptions.Parse(
            nameof(PostgreSqlCohortFixtureTests),
            "template",
            anchor.ConnectionString,
            timingPath);

        try
        {
            await using var fixture = new PostgreSqlCohortFixtureCore(options);
            var ready = await fixture.CreateReadyDatabaseAsync("fixture-tests", "issue-931");
            await ready.DisposeAsync();
            var empty = await fixture.CreateEmptyDatabaseAsync();
            await empty.DisposeAsync();
            await fixture.DisposeAsync();

            var rows = await File.ReadAllLinesAsync(timingPath);
            Assert.Equal("phase,elapsed_ms", rows[0]);
            var phases = rows.Skip(1)
                .Select(row => row.Split(','))
                .Select(columns =>
                {
                    Assert.Equal(2, columns.Length);
                    Assert.True(double.TryParse(columns[1], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var elapsed));
                    Assert.True(double.IsFinite(elapsed));
                    Assert.True(elapsed >= 0);
                    return columns[0];
                })
                .ToArray();

            Assert.Equal(1, phases.Count(phase => phase == "template_prepare"));
            Assert.Equal(1, phases.Count(phase => phase == "template_teardown"));
            Assert.Equal(2, phases.Count(phase => phase == "database_setup"));
            Assert.Equal(2, phases.Count(phase => phase == "database_teardown"));
        }
        finally
        {
            File.Delete(timingPath);
        }
    }

    private static PostgreSqlCohortFixtureOptions TemplateOptions(PostgreSqlIntegrationTestDatabase anchor) =>
        PostgreSqlCohortFixtureOptions.Parse(
            nameof(PostgreSqlCohortFixtureTests),
            "template",
            anchor.ConnectionString,
            timingsFilePath: null);

    private static async Task<bool> DatabaseExistsAsync(PostgreSqlIntegrationTestDatabase anchor, string databaseName)
    {
        await using var command = anchor.DataSource.CreateCommand(
            "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_database WHERE datname = @database_name);");
        command.Parameters.AddWithValue("database_name", databaseName);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

}
