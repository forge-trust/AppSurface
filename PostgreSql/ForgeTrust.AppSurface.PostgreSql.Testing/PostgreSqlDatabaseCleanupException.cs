namespace ForgeTrust.AppSurface.PostgreSql.Testing;

/// <summary>A database with a known catalog identity could not be cleaned up. Retry disposal on <see cref="Database"/>.</summary>
public sealed class PostgreSqlDatabaseCleanupException : Exception
{
    internal PostgreSqlDatabaseCleanupException(PostgreSqlDatabaseLease database, Exception cleanupError, Exception? operationError = null)
        : base($"Cleanup of PostgreSQL database {database.Name} (OID {database.Oid}) failed ({cleanupError.GetType().Name}). Retry the owned database lease.")
    {
        Database = database;
        CleanupError = cleanupError;
        OperationError = operationError;
    }

    /// <summary>The original lease, including its recorded identity and any parent template registration.</summary>
    public PostgreSqlDatabaseLease Database { get; }

    /// <summary>The error from the unsuccessful cleanup attempt.</summary>
    public Exception CleanupError { get; }

    /// <summary>The callback or setup error that preceded cleanup, if one occurred.</summary>
    public Exception? OperationError { get; }
}
