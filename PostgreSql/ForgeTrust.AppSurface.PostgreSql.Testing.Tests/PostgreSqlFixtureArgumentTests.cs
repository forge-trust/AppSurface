namespace ForgeTrust.AppSurface.PostgreSql.Testing.Tests;

public sealed class PostgreSqlFixtureArgumentTests
{
    [Theory]
    [InlineData("Database=postgres")]
    [InlineData("not-a-valid-option=sentinel-private")]
    [InlineData("Host=localhost;Port=sentinel-private")]
    public async Task Lease_RejectsInvalidEndpointsWithoutEchoingConnectionSettings(string connectionString)
    {
        var error = await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            PostgreSqlDatabaseLease.CreateAsync(connectionString, "case"));
        Assert.DoesNotContain("sentinel-private", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RunFreshAsync_RejectsMissingConnectionStrings(string? connectionString)
    {
        var fixture = new PostgreSqlFixture();

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            fixture.RunFreshAsync(connectionString!, "fresh", static (_, _) => Task.CompletedTask));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("invalid-prefix!")]
    public async Task RunFreshAsync_RejectsInvalidDatabasePrefixes(string? prefix)
    {
        var fixture = new PostgreSqlFixture();

        var exception = await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            fixture.RunFreshAsync("Host=localhost", prefix!, static (_, _) => Task.CompletedTask));

        Assert.Equal("databaseNamePrefix", exception.ParamName);
    }

    [Fact]
    public void RunFreshAsync_RejectsANullAction()
    {
        var fixture = new PostgreSqlFixture();

        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = fixture.RunFreshAsync("Host=localhost", "fresh", null!);
        });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RunFromTemplateAsync_RejectsMissingConnectionStrings(string? connectionString)
    {
        var fixture = new PostgreSqlFixture();

        await Assert.ThrowsAnyAsync<ArgumentException>(() => fixture.RunFromTemplateAsync(
            connectionString!,
            "clone",
            "template",
            static (_, _) => Task.CompletedTask,
            static (_, _) => Task.CompletedTask));
    }

    [Theory]
    [InlineData(null, "template", "databaseNamePrefix")]
    [InlineData("", "template", "databaseNamePrefix")]
    [InlineData("   ", "template", "databaseNamePrefix")]
    [InlineData("invalid-prefix!", "template", "databaseNamePrefix")]
    [InlineData("clone", null, "templateNamePrefix")]
    [InlineData("clone", "", "templateNamePrefix")]
    [InlineData("clone", "   ", "templateNamePrefix")]
    [InlineData("clone", "invalid-prefix!", "templateNamePrefix")]
    public async Task RunFromTemplateAsync_RejectsInvalidPrefixes(
        string? databasePrefix,
        string? templatePrefix,
        string expectedParamName)
    {
        var fixture = new PostgreSqlFixture();

        var exception = await Assert.ThrowsAnyAsync<ArgumentException>(() => fixture.RunFromTemplateAsync(
            "Host=localhost",
            databasePrefix!,
            templatePrefix!,
            static (_, _) => Task.CompletedTask,
            static (_, _) => Task.CompletedTask));

        Assert.Equal(expectedParamName, exception.ParamName);
    }

    [Theory]
    [InlineData("app")]
    [InlineData("app_123")]
    public void ValidateDatabaseNamePrefix_AcceptsValidPrefixesOffline(string databaseNamePrefix)
    {
        PostgreSqlFixture.ValidateDatabaseNamePrefix(databaseNamePrefix);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("invalid-prefix!")]
    public void ValidateDatabaseNamePrefix_ReportsDatabaseNamePrefixForInvalidValuesOffline(string? databaseNamePrefix)
    {
        var exception = Assert.ThrowsAny<ArgumentException>(() =>
            PostgreSqlFixture.ValidateDatabaseNamePrefix(databaseNamePrefix!));

        Assert.Equal("databaseNamePrefix", exception.ParamName);
    }

    [Fact]
    public async Task RunFromTemplateAsync_RejectsNullCallbacks()
    {
        var fixture = new PostgreSqlFixture();

        await Assert.ThrowsAsync<ArgumentNullException>(() => fixture.RunFromTemplateAsync(
            "Host=localhost",
            "clone",
            "template",
            null!,
            static (_, _) => Task.CompletedTask));
        await Assert.ThrowsAsync<ArgumentNullException>(() => fixture.RunFromTemplateAsync(
            "Host=localhost",
            "clone",
            "template",
            static (_, _) => Task.CompletedTask,
            null!));
    }

    [Fact]
    public void CleanupTemplatesAsync_RejectsMissingConnectionAndEmptyPrefixes()
    {
        var fixture = new PostgreSqlFixture();

        Assert.ThrowsAny<ArgumentException>(() => fixture.CleanupTemplatesAsync(" ", ["template"]));
        Assert.Throws<ArgumentException>(() => fixture.CleanupTemplatesAsync("Host=localhost", []));
    }

    [Fact]
    public void WithClusterInitializationLockAsync_RejectsInvalidArguments()
    {
        Assert.ThrowsAny<ArgumentException>(() =>
        {
            _ = PostgreSqlFixture.WithClusterInitializationLockAsync(" ", static _ => Task.CompletedTask);
        });
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = PostgreSqlFixture.WithClusterInitializationLockAsync("Host=localhost", null!);
        });
    }

    [Theory]
    [InlineData(typeof(OutOfMemoryException), true)]
    [InlineData(typeof(StackOverflowException), true)]
    [InlineData(typeof(AccessViolationException), true)]
    [InlineData(typeof(InvalidOperationException), false)]
    [InlineData(typeof(OperationCanceledException), false)]
    public void IsFatalCleanupException_ClassifiesCleanupFailures(Type exceptionType, bool expected)
    {
        var exception = Assert.IsAssignableFrom<Exception>(Activator.CreateInstance(exceptionType));

        Assert.Equal(expected, PostgreSqlFixture.IsFatalCleanupException(exception));
    }
}
