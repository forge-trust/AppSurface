namespace ForgeTrust.AppSurface.PostgreSql.Testing;

/// <summary>Outcomes for the selected templates and any failed fresh database drops retried during full cleanup.</summary>
public sealed record TemplateCleanupResult(IReadOnlyList<TemplateCleanupOutcome> Outcomes, TimeSpan Elapsed)
{
    /// <summary>Outcomes of retrying previously failed fresh case drops during full fixture cleanup. These do not count as templates.</summary>
    public IReadOnlyList<TemplateCleanupOutcome> FreshDatabaseOutcomes { get; init; } = [];

    /// <summary>Number of selected registrations, including incomplete attempts.</summary>
    public int RegisteredTemplateCount => Outcomes.Count;

    /// <summary>Number of registrations successfully released.</summary>
    public int RemovedTemplateCount => Outcomes.Count(outcome => outcome.Status == TemplateCleanupStatus.Removed);

    /// <summary>Database or cancellation failures in this attempt.</summary>
    public IReadOnlyList<TemplateCleanupOutcome> Failures => Outcomes.Concat(FreshDatabaseOutcomes).Where(outcome => outcome.Status == TemplateCleanupStatus.Failed).ToArray();

    /// <summary>Every registration for which this operation could not confirm removal.</summary>
    public IReadOnlyList<TemplateCleanupOutcome> IncompleteOutcomes => Outcomes.Concat(FreshDatabaseOutcomes).Where(outcome => outcome.Status != TemplateCleanupStatus.Removed).ToArray();

    /// <summary>Throws with the full result when any selected registration remains unresolved.</summary>
    public void ThrowIfFailed()
    {
        if (IncompleteOutcomes.Count != 0)
        {
            throw new TemplateCleanupException(this);
        }
    }
}

/// <summary>The exact PostgreSQL identifier, disposition, and optional failure for one owned database.</summary>
public sealed record TemplateCleanupOutcome(string DatabaseName, TemplateCleanupStatus Status, string? Failure)
{
    /// <summary>Indicates that cleanup verified the database was removed or already absent.</summary>
    public static TemplateCleanupOutcome Removed(string databaseName) => new(databaseName, TemplateCleanupStatus.Removed, null);

    /// <summary>Indicates another cleanup call is currently responsible for this registration.</summary>
    public static TemplateCleanupOutcome AlreadyInProgress(string databaseName) => new(databaseName, TemplateCleanupStatus.AlreadyInProgress, null);

    /// <summary>Captures the failing operation for reporting and retry.</summary>
    public static TemplateCleanupOutcome Failed(string databaseName, Exception exception) =>
        new(databaseName, TemplateCleanupStatus.Failed, $"{exception.GetType().Name}: {exception.Message}");
}

/// <summary>Disposition of one database in a cleanup attempt.</summary>
public enum TemplateCleanupStatus
{
    /// <summary>The database and its registration were released.</summary>
    Removed,
    /// <summary>The operation failed. The fixture retains the registration for retry.</summary>
    Failed,
    /// <summary>A concurrent cleanup already claimed the registration.</summary>
    AlreadyInProgress,
}

/// <summary>Reports partial or failed cleanup after every selected registration has been attempted.</summary>
public sealed class TemplateCleanupException(TemplateCleanupResult result)
    : Exception("PostgreSQL fixture cleanup did not complete for " + string.Join(", ", result.IncompleteOutcomes.Select(
        outcome => $"{outcome.DatabaseName} ({outcome.Status}: {outcome.Failure ?? "cleanup was already in progress"})")))
{
    /// <summary>Includes successful, failed, and concurrently owned registrations from this attempt.</summary>
    public TemplateCleanupResult Result { get; } = result;
}
