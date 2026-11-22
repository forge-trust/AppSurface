namespace ForgeTrust.AppSurface.PostgreSql.Testing;

/// <summary>Reports an ownership mismatch or an uncertain PostgreSQL database creation outcome.</summary>
public sealed class PostgreSqlDatabaseOwnershipException : InvalidOperationException
{
    internal PostgreSqlDatabaseOwnershipException(string safeMessage)
        : base(safeMessage)
    {
    }
}
