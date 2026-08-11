using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

public sealed class PostgreSqlIntegrationTestDatabaseTests
{
    [Fact]
    public async Task ExecuteContainerStartupProbeAsync_RetriesTimedOutNpgsqlConnection()
    {
        var attempts = 0;
        var delays = new List<TimeSpan>();

        await PostgreSqlIntegrationTestDatabase.ExecuteContainerStartupProbeAsync(
            _ =>
            {
                attempts++;
                return attempts == 1
                    ? ValueTask.FromException(new NpgsqlException("Timed out opening PostgreSQL.", new TimeoutException()))
                    : ValueTask.CompletedTask;
            },
            (delay, _) =>
            {
                delays.Add(delay);
                return ValueTask.CompletedTask;
            });

        Assert.Equal(2, attempts);
        Assert.Equal([TimeSpan.FromMilliseconds(250)], delays);
    }

    [Fact]
    public async Task ExecuteContainerStartupProbeAsync_DoesNotRetryNonTimeoutNpgsqlFailure()
    {
        var attempts = 0;
        var expected = new NpgsqlException("Authentication failed.", new InvalidOperationException());

        var actual = await Assert.ThrowsAsync<NpgsqlException>(
            () => PostgreSqlIntegrationTestDatabase.ExecuteContainerStartupProbeAsync(
                    _ =>
                    {
                        attempts++;
                        return ValueTask.FromException(expected);
                    })
                .AsTask());

        Assert.Same(expected, actual);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task ExecuteContainerStartupProbeAsync_RethrowsAfterTheRetryBudgetIsExhausted()
    {
        var attempts = 0;
        var delays = 0;

        var exception = await Assert.ThrowsAsync<NpgsqlException>(
            () => PostgreSqlIntegrationTestDatabase.ExecuteContainerStartupProbeAsync(
                    _ =>
                    {
                        attempts++;
                        return ValueTask.FromException(
                            new NpgsqlException("Timed out opening PostgreSQL.", new TimeoutException()));
                    },
                    (_, _) =>
                    {
                        delays++;
                        return ValueTask.CompletedTask;
                    })
                .AsTask());

        Assert.IsType<TimeoutException>(exception.InnerException);
        Assert.Equal(3, attempts);
        Assert.Equal(2, delays);
    }
}
