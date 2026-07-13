namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>
/// Indicates that the durable store schema is missing, incompatible, or internally inconsistent.
/// </summary>
public sealed class DurableRuntimeSchemaException : InvalidOperationException
{
    /// <summary>
    /// Initializes a schema exception from a compatibility status.
    /// </summary>
    /// <param name="status">Incompatible schema status.</param>
    public DurableRuntimeSchemaException(DurableRuntimeSchemaStatus status)
        : base(CreateMessage(status ?? throw new ArgumentNullException(nameof(status))))
    {
        Status = status;
    }

    /// <summary>
    /// Gets the incompatible schema status.
    /// </summary>
    public DurableRuntimeSchemaStatus Status { get; }

    private static string CreateMessage(DurableRuntimeSchemaStatus status) =>
        $"{GetProblemCode(status.Compatibility)}: The AppSurface durable PostgreSQL schema is not compatible. " +
        $"Installed={status.InstalledVersion}; Required={status.RequiredVersion}; " +
        $"Compatibility={status.Compatibility}. {status.Problem} " +
        "Generate or apply the explicit package migrations before starting durable workers.";

    private static string GetProblemCode(DurableRuntimeSchemaCompatibility compatibility) => compatibility switch
    {
        DurableRuntimeSchemaCompatibility.Missing => DurableProblemCodes.SchemaMissing,
        DurableRuntimeSchemaCompatibility.UpgradeRequired => DurableProblemCodes.SchemaUpgradeRequired,
        DurableRuntimeSchemaCompatibility.StoreTooNew => DurableProblemCodes.SchemaVersionUnsupported,
        DurableRuntimeSchemaCompatibility.Inconsistent => DurableProblemCodes.SchemaInconsistent,
        DurableRuntimeSchemaCompatibility.Compatible => DurableProblemCodes.SchemaInconsistent,
        _ => DurableProblemCodes.SchemaInconsistent,
    };
}
