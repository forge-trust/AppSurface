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
        string? templateName = null;
        string? caseDatabaseName = null;
        var fixture = new PostgreSqlCohortFixtureCore(
            TemplateOptions(anchor),
            fixtureDrainTimeout: TimeSpan.FromMilliseconds(100),
            afterTemplatePreparedAsync: preparedTemplate =>
            {
                template = preparedTemplate;
                templateName = new NpgsqlConnectionStringBuilder(preparedTemplate.ConnectionString).Database;
                return ValueTask.CompletedTask;
            });

        try
        {
            await fixture.InitializeAsync().WaitAsync(TestOperationTimeout);
            caseDatabase = await fixture.CreateReadyDatabaseAsync("cohort-retry-tests", "failed-drop-retry")
                .AsTask()
                .WaitAsync(TestOperationTimeout);
            caseDatabaseName = new NpgsqlConnectionStringBuilder(caseDatabase.Database.ConnectionString).Database;

            var firstDropFailure = new InvalidOperationException("Injected first clone-drop failure.");
            var observedDropFailure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                caseDatabase.Database.DisposeAsync(_ => ValueTask.FromException(firstDropFailure)).AsTask());
            Assert.Same(firstDropFailure, observedDropFailure);

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

    private static PostgreSqlCohortFixtureOptions TemplateOptions(PostgreSqlIntegrationTestDatabase anchor) =>
        PostgreSqlCohortFixtureOptions.Parse(
            nameof(PostgreSqlCohortDatabaseRetryTests),
            "template",
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
