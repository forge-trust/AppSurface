using ForgeTrust.AppSurface.Durable;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>Indicates a missing, incompatible, or inconsistent durable PostgreSQL schema.</summary>
public sealed class DurableRuntimeSchemaException : InvalidOperationException
{
    /// <summary>Initializes an exception from an incompatible schema status.</summary>
    /// <param name="status">Incompatible schema status.</param>
    public DurableRuntimeSchemaException(DurableRuntimeSchemaStatus status)
        : base(CreateMessage(status ?? throw new ArgumentNullException(nameof(status))))
    {
        Status = status;
    }

    /// <summary>Gets the incompatible schema status.</summary>
    public DurableRuntimeSchemaStatus Status { get; }

    private static string CreateMessage(DurableRuntimeSchemaStatus status) =>
        $"{GetCode(status.Compatibility)}: The AppSurface durable PostgreSQL schema is not compatible. " +
        $"Installed={status.InstalledVersion}; Required={status.RequiredVersion}; Compatibility={status.Compatibility}. " +
        $"{status.Problem} Generate or apply migrations with the migration owner; runtime startup never applies DDL.";

    private static string GetCode(DurableRuntimeSchemaCompatibility compatibility) => compatibility switch
    {
        DurableRuntimeSchemaCompatibility.Missing => DurableProblemCodes.SchemaMissing,
        DurableRuntimeSchemaCompatibility.UpgradeRequired => DurableProblemCodes.SchemaUpgradeRequired,
        DurableRuntimeSchemaCompatibility.StoreTooNew => DurableProblemCodes.SchemaVersionUnsupported,
        _ => DurableProblemCodes.SchemaInconsistent,
    };
}
