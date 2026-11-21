using System.Globalization;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

public sealed class PostgreSqlFixtureTimingCsvTests
{
    [Fact]
    public async Task CreateAndRecord_WritesAllPhasesWithInvariantElapsedMilliseconds()
    {
        var path = NewTimingPath();
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            await using (var timings = PostgreSqlFixtureTimingCsv.Create(path))
            {
                timings.Record("database_setup", TimeSpan.FromMilliseconds(12.5));
                timings.Record("database_teardown", TimeSpan.FromMilliseconds(2.25));
                timings.Record("template_prepare", TimeSpan.FromMilliseconds(8));
                timings.Record("template_teardown", TimeSpan.Zero);
            }

            Assert.Equal(
                [
                    "phase,elapsed_ms",
                    "database_setup,12.5",
                    "database_teardown,2.25",
                    "template_prepare,8",
                    "template_teardown,0"
                ],
                await File.ReadAllLinesAsync(path));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Create_RequiresANewFileAndPreservesExistingContents()
    {
        var path = NewTimingPath();
        const string existingContents = "keep this file";
        try
        {
            await File.WriteAllTextAsync(path, existingContents);

            var exception = Assert.Throws<PostgreSqlFixtureException>(() =>
                PostgreSqlFixtureTimingCsv.Create(path));

            Assert.Contains("Cannot create fixture timing CSV", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(existingContents, exception.Message, StringComparison.Ordinal);
            Assert.Equal(existingContents, await File.ReadAllTextAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Record_RejectsUnsupportedPhasesAndNegativeDurations()
    {
        var path = NewTimingPath();
        try
        {
            await using var timings = PostgreSqlFixtureTimingCsv.Create(path);

            Assert.Throws<PostgreSqlFixtureException>(() => timings.Record("unsupported", TimeSpan.Zero));
            Assert.Throws<PostgreSqlFixtureException>(() => timings.Record("database_setup", TimeSpan.FromMilliseconds(-1)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Record_AfterDisposalReportsASafeFixtureError()
    {
        var path = NewTimingPath();
        try
        {
            var timings = PostgreSqlFixtureTimingCsv.Create(path);
            await timings.DisposeAsync();

            var exception = Assert.Throws<PostgreSqlFixtureException>(() =>
                timings.Record("database_setup", TimeSpan.Zero));

            Assert.Contains(nameof(ObjectDisposedException), exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("ObjectDisposedException: Cannot access", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string NewTimingPath() =>
        Path.Combine(Path.GetTempPath(), $"appsurface-durable-fixture-{Guid.NewGuid():N}.csv");

    [Fact]
    public void Create_DoesNotEchoAConnectionStringInAnInvalidDestination()
    {
        var missingDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(missingDirectory, "Host=fixture;Password=sentinel-private-value.csv");
        var error = Assert.Throws<PostgreSqlFixtureException>(() => PostgreSqlFixtureTimingCsv.Create(path));

        Assert.Contains("<requested timing file>", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sentinel-private-value", error.Message, StringComparison.Ordinal);
    }
}
