using ForgeTrust.AppSurface.PostgreSql.Testing;
using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

public sealed class PostgreSqlCohortDatabaseRetryTests
{
    private static readonly TimeSpan TestOperationTimeout = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task FailedCloneDrop_RetainsCaseLeaseUntilRetryAllowsTemplateCleanup()
    {
        await using var anchor = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        PostgreSqlIntegrationTestDatabase? template = null;
        PostgreSqlCohortReadyDatabase? caseDatabase = null;
        Task<PostgreSqlCohortReadyDatabase>? caseDatabaseCreation = null;
        string? templateName = null;
        string? caseDatabaseName = null;
        var allowCreation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new PostgreSqlCohortFixtureCore(
            TemplateOptions(anchor),
            fixtureDrainTimeout: TimeSpan.FromMilliseconds(100),
            beforeCaseDatabaseCreateAsync: () => new ValueTask(allowCreation.Task),
            afterTemplatePreparedAsync: preparedTemplate =>
            {
                template = preparedTemplate;
                templateName = new NpgsqlConnectionStringBuilder(preparedTemplate.ConnectionString).Database;
                return ValueTask.CompletedTask;
            });

        try
        {
            await fixture.InitializeAsync().WaitAsync(TestOperationTimeout);
            caseDatabaseCreation = fixture.CreateReadyDatabaseAsync("cohort-retry-tests", "failed-drop-retry").AsTask();
            await Assert.ThrowsAsync<TimeoutException>(() => caseDatabaseCreation.WaitAsync(TimeSpan.Zero));
            allowCreation.SetResult();
            caseDatabase = await caseDatabaseCreation.WaitAsync(TestOperationTimeout);
            caseDatabaseName = new NpgsqlConnectionStringBuilder(caseDatabase.Database.ConnectionString).Database;

            var firstDropFailure = new InvalidOperationException("Injected first clone-drop failure.");
            var observedDropFailure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                caseDatabase.Database.DisposeAsync(_ => ValueTask.FromException(firstDropFailure)).AsTask());
            Assert.IsType<InvalidOperationException>(observedDropFailure);

            var disposalTimeout = await Assert.ThrowsAsync<PostgreSqlFixtureException>(
                () => fixture.DisposeAsync().AsTask());
            Assert.Contains("still has case databases", disposalTimeout.Message, StringComparison.Ordinal);
            Assert.NotNull(caseDatabaseName);
            Assert.NotNull(templateName);
            Assert.True(await DatabaseExistsAsync(anchor, caseDatabaseName));
            Assert.True(await DatabaseExistsAsync(anchor, templateName));

            await caseDatabase.Database.DisposeAsync().AsTask().WaitAsync(TestOperationTimeout);
            Assert.False(await DatabaseExistsAsync(anchor, caseDatabaseName));
            await fixture.DisposeAsync().AsTask().WaitAsync(TestOperationTimeout);

            Assert.False(await DatabaseExistsAsync(anchor, templateName));
        }
        finally
        {
            allowCreation.TrySetResult();
            if (caseDatabaseCreation is not null)
            {
                try
                {
                    caseDatabase ??= await caseDatabaseCreation;
                }
                catch (Exception exception) when (!PostgreSqlFixture.IsFatalCleanupException(exception))
                {
                    // Preserve the test's original failure and continue fixture cleanup.
                }
            }

            if (caseDatabase is not null)
            {
                await caseDatabase.Database.DisposeAsync().AsTask().WaitAsync(TestOperationTimeout);
            }

            try
            {
                await fixture.DisposeAsync().AsTask().WaitAsync(TestOperationTimeout);
            }
            catch (PostgreSqlFixtureException) when (template is not null && caseDatabase is not null)
            {
                await template.DisposeAsync().AsTask().WaitAsync(TestOperationTimeout);
            }
        }
    }

    [Fact]
    public async Task FailedCaseCreationCleanup_RetainsLeaseForFixtureTeardownRetry()
    {
        await using var anchor = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await using var lease = await PostgreSqlDatabaseLease.CreateAsync(anchor.ConnectionString, "cohort_failed_setup");
        await using var fixture = new PostgreSqlCohortFixtureCore(
            FreshOptions(anchor),
            createCaseDatabaseAsync: _ => ValueTask.FromException<PostgreSqlIntegrationTestDatabase>(
                new PostgreSqlDatabaseCleanupException(
                    lease,
                    new InvalidOperationException("Injected compensating drop failure."),
                    new InvalidOperationException("Injected setup failure."))));

        var creationError = await Assert.ThrowsAsync<PostgreSqlFixtureException>(() =>
            fixture.CreateReadyDatabaseAsync("cohort-retry-tests", "failed-create-cleanup").AsTask());
        Assert.Contains(lease.Name, creationError.Message, StringComparison.Ordinal);
        Assert.True(await DatabaseExistsAsync(anchor, lease.Name));

        await fixture.DisposeAsync().AsTask().WaitAsync(TestOperationTimeout);

        Assert.False(await DatabaseExistsAsync(anchor, lease.Name));
    }

    [Fact]
    public async Task FailedTemplateDisposal_CanBeRetriedAfterTheFirstFailure()
    {
        await using var anchor = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        PostgreSqlIntegrationTestDatabase? template = null;
        string? templateName = null;
        var disposeAttempts = 0;
        await using var fixture = new PostgreSqlCohortFixtureCore(
            TemplateOptions(anchor),
            afterTemplatePreparedAsync: preparedTemplate =>
            {
                template = preparedTemplate;
                templateName = new NpgsqlConnectionStringBuilder(preparedTemplate.ConnectionString).Database;
                return ValueTask.CompletedTask;
            },
            disposeResourceAsync: async resource =>
            {
                if (ReferenceEquals(resource, template) && Interlocked.Increment(ref disposeAttempts) == 1)
                {
                    throw new InvalidOperationException("Injected first template-drop failure.");
                }

                await resource.DisposeAsync();
            });

        await fixture.InitializeAsync().WaitAsync(TestOperationTimeout);
        var firstDisposal = await Assert.ThrowsAsync<PostgreSqlFixtureException>(
            () => fixture.DisposeAsync().AsTask().WaitAsync(TestOperationTimeout));
        Assert.Contains("Cohort fixture teardown", firstDisposal.Message, StringComparison.Ordinal);
        Assert.NotNull(templateName);
        Assert.True(await DatabaseExistsAsync(anchor, templateName));

        await fixture.DisposeAsync().AsTask().WaitAsync(TestOperationTimeout);

        Assert.Equal(2, disposeAttempts);
        Assert.False(await DatabaseExistsAsync(anchor, templateName));
    }

    private static PostgreSqlCohortFixtureOptions TemplateOptions(PostgreSqlIntegrationTestDatabase anchor) =>
        PostgreSqlCohortFixtureOptions.Parse(
            nameof(PostgreSqlCohortDatabaseRetryTests),
            "template",
            anchor.ConnectionString,
            timingsFilePath: null);

    private static PostgreSqlCohortFixtureOptions FreshOptions(PostgreSqlIntegrationTestDatabase anchor) =>
        PostgreSqlCohortFixtureOptions.Parse(
            nameof(PostgreSqlCohortDatabaseRetryTests),
            "fresh",
            anchor.ConnectionString,
            timingsFilePath: null);

    private static async Task<bool> DatabaseExistsAsync(
        PostgreSqlIntegrationTestDatabase anchor,
        string databaseName)
    {
        await using var connection = await anchor.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_database WHERE datname = @name)",
            connection);
        command.Parameters.AddWithValue("name", databaseName);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

}
