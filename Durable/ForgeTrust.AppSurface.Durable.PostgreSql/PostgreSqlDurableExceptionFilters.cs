using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

internal static class PostgreSqlDurableExceptionFilters
{
    internal static bool IsExpectedCleanupFailure(Exception exception) =>
        exception is NpgsqlException or InvalidOperationException or OperationCanceledException;
}
