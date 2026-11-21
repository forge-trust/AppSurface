using System.Text.Json;
using Xunit.Abstractions;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

public sealed class PostgreSqlFixtureServerMetadataTests(ITestOutputHelper output)
{
    /// <summary>Provides the benchmark runner with independently observed, nonsecret measurement settings in TRX.</summary>
    [Fact]
    public async Task ReportsMeasurementServerSettings()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await using var query = database.DataSource.CreateCommand(
            """
            SELECT json_build_object(
                'version', split_part(current_setting('server_version'), ' ', 1),
                'settings', (SELECT json_object_agg(name, current_setting(name) ORDER BY name)
                             FROM pg_settings
                             WHERE name IN ('autovacuum', 'checkpoint_timeout', 'fsync',
                                            'full_page_writes', 'max_connections', 'max_wal_size',
                                            'shared_buffers', 'synchronous_commit', 'wal_level', 'work_mem')))::text;
            """);
        var receipt = Assert.IsType<string>(await query.ExecuteScalarAsync());
        using var document = JsonDocument.Parse(receipt);
        Assert.True(Version.TryParse(document.RootElement.GetProperty("version").GetString(), out _));
        Assert.Equal(10, document.RootElement.GetProperty("settings").EnumerateObject().Count());
        output.WriteLine($"FIXTURE_SERVER_METADATA {receipt}");
    }
}
