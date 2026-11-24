namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

public sealed class PostgreSqlCohortFixtureOptionsTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("fresh", false)]
    [InlineData("template", true)]
    public void Parse_AcceptsOnlyTheExplicitFixtureModes(string? mode, bool usesTemplate)
    {
        var options = PostgreSqlCohortFixtureOptions.Parse(
            "DurableCohort",
            mode,
            "Host=localhost;Database=fixture;Username=fixture",
            "timings.csv");

        Assert.Equal(usesTemplate, options.UsesTemplate);
        Assert.Equal("timings.csv", options.TimingsFilePath);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("fresh")]
    public void Parse_FreshModeAllowsTheExistingTestServerFallback(string? mode)
    {
        var options = PostgreSqlCohortFixtureOptions.Parse("WorkCohort", mode, null, null);

        Assert.False(options.UsesTemplate);
        Assert.Null(options.ConnectionString);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Fresh")]
    [InlineData("FRESH")]
    [InlineData("template ")]
    [InlineData(" disabled")]
    public void Parse_RejectsUnknownOrNonExactFixtureModes(string mode)
    {
        var exception = Assert.Throws<PostgreSqlFixtureException>(() =>
            PostgreSqlCohortFixtureOptions.Parse(
                "DurableCohort",
                mode,
                "Host=localhost;Database=fixture;Username=fixture;Password=must-not-appear",
                null));

        Assert.Equal(
            $"{PostgreSqlCohortFixtureOptions.FixtureModeVariable} for DurableCohort must be exactly 'fresh' or 'template'.",
            exception.Message);
        Assert.DoesNotContain("must-not-appear", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_RequiresAConnectionStringInTemplateMode(string? connectionString)
    {
        var exception = Assert.Throws<PostgreSqlFixtureException>(() =>
            PostgreSqlCohortFixtureOptions.Parse("FlowCohort", "template", connectionString, null));

        Assert.Contains(PostgreSqlCohortFixtureOptions.ConnectionStringVariable, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Parse_RejectsAnEmptyTimingPathWhenTheVariableIsSet(string path)
    {
        var exception = Assert.Throws<PostgreSqlFixtureException>(() =>
            PostgreSqlCohortFixtureOptions.Parse("FlowCohort", "fresh", null, path));

        Assert.Contains(PostgreSqlCohortFixtureOptions.TimingsFileVariable, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_UsesSafeFixtureMessagesAndExternalExceptionTypes()
    {
        var safe = new PostgreSqlFixtureException("Actionable safe fixture detail.");
        var external = new InvalidOperationException("Password=private;Host=private.example");

        Assert.Equal("Actionable safe fixture detail.", PostgreSqlFixtureException.Describe(safe));
        Assert.Equal(nameof(InvalidOperationException), PostgreSqlFixtureException.Describe(external));

        var aggregate = PostgreSqlFixtureException.Describe(new AggregateException(safe, external));
        Assert.Contains("Actionable safe fixture detail.", aggregate, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), aggregate, StringComparison.Ordinal);
        Assert.DoesNotContain("private", aggregate, StringComparison.Ordinal);
    }
}
