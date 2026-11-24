namespace ForgeTrust.AppSurface.PostgreSql.Testing.Tests;

internal static class PostgreSqlFixtureTestProcess
{
    internal const string TemplatePrefixEnvironmentVariable = "APPSURFACE_POSTGRES_TEST_CHILD_TEMPLATE_PREFIX";

    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 1 || args[0] != "--process-exit-fallback-child")
        {
            return 2;
        }

        var connectionString = Environment.GetEnvironmentVariable(PostgreSqlTestServer.ConnectionEnvironmentVariable);
        var templatePrefix = Environment.GetEnvironmentVariable(TemplatePrefixEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(connectionString) || string.IsNullOrWhiteSpace(templatePrefix))
        {
            return 2;
        }

        var fixture = new PostgreSqlFixture();
        string? templateDatabaseName = null;
        await fixture.RunFromTemplateAsync(
            connectionString,
            PostgreSqlTestServer.NewPrefix("process_exit_clone"),
            templatePrefix,
            async (templateConnectionString, cancellationToken) =>
            {
                templateDatabaseName = await PostgreSqlTestServer.ReadScalarAsync<string>(
                    templateConnectionString,
                    "SELECT current_database();");
                await PostgreSqlTestServer.ExecuteAsync(
                    templateConnectionString,
                    "CREATE TABLE process_exit_probe (value integer NOT NULL); INSERT INTO process_exit_probe VALUES (43);");
            },
            async (cloneConnectionString, cancellationToken) =>
            {
                var value = await PostgreSqlTestServer.ReadScalarAsync<int>(
                    cloneConnectionString,
                    "SELECT value FROM process_exit_probe;");
                if (value != 43)
                {
                    throw new InvalidOperationException("The child process template clone returned an unexpected value.");
                }
            });

        Console.WriteLine(templateDatabaseName);
        return templateDatabaseName is null ? 3 : 0;
    }
}
