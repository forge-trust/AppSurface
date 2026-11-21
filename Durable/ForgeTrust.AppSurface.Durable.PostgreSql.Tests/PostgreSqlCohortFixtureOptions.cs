namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

internal enum PostgreSqlCohortFixtureMode
{
    Fresh,
    Template
}

internal sealed record PostgreSqlCohortFixtureOptions
{
    internal const string FixtureModeVariable = "APPSURFACE_DURABLE_POSTGRES_TEST_FIXTURE";
    internal const string ConnectionStringVariable = "APPSURFACE_POSTGRES_TEST_CONNECTION";
    internal const string TimingsFileVariable = "APPSURFACE_DURABLE_FIXTURE_TIMINGS_FILE";

    private PostgreSqlCohortFixtureOptions(
        string cohortName,
        PostgreSqlCohortFixtureMode mode,
        string? connectionString,
        string? timingsFilePath)
    {
        CohortName = cohortName;
        Mode = mode;
        ConnectionString = connectionString;
        TimingsFilePath = timingsFilePath;
    }

    internal string CohortName { get; }

    internal PostgreSqlCohortFixtureMode Mode { get; }

    internal string? ConnectionString { get; }

    internal string? TimingsFilePath { get; }

    internal bool UsesTemplate => Mode == PostgreSqlCohortFixtureMode.Template;

    internal static PostgreSqlCohortFixtureOptions FromEnvironment(string cohortName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cohortName);

        return Parse(
            cohortName,
            Environment.GetEnvironmentVariable(FixtureModeVariable),
            Environment.GetEnvironmentVariable(ConnectionStringVariable),
            Environment.GetEnvironmentVariable(TimingsFileVariable));
    }

    internal static PostgreSqlCohortFixtureOptions Parse(
        string cohortName,
        string? modeValue,
        string? connectionString,
        string? timingsFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cohortName);

        var mode = modeValue switch
        {
            null or "fresh" => PostgreSqlCohortFixtureMode.Fresh,
            "template" => PostgreSqlCohortFixtureMode.Template,
            _ => throw new PostgreSqlFixtureException(
                $"{FixtureModeVariable} for {cohortName} must be exactly 'fresh' or 'template'.")
        };

        if (mode == PostgreSqlCohortFixtureMode.Template && string.IsNullOrWhiteSpace(connectionString))
        {
            throw new PostgreSqlFixtureException(
                $"{FixtureModeVariable}=template for {cohortName} requires {ConnectionStringVariable}.");
        }

        if (timingsFilePath is not null && string.IsNullOrWhiteSpace(timingsFilePath))
        {
            throw new PostgreSqlFixtureException(
                $"{TimingsFileVariable} for {cohortName} must name a new CSV file when it is supplied.");
        }

        return new PostgreSqlCohortFixtureOptions(
            cohortName,
            mode,
            string.IsNullOrWhiteSpace(connectionString) ? null : connectionString,
            timingsFilePath);
    }
}
