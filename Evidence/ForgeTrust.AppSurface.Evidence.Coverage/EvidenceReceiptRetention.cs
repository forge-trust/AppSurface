namespace ForgeTrust.AppSurface.Evidence.Coverage;

/// <summary>Defines the origin age window shared by receipt reads, comparison, and eviction.</summary>
internal static class EvidenceReceiptRetention
{
    private static readonly TimeSpan MaximumOriginAge = TimeSpan.FromDays(7);

    /// <summary>Reports why a known completion timestamp falls outside the receipt age window.</summary>
    /// <param name="completedAt">The original producer completion time.</param>
    /// <param name="now">The current time for this receipt operation.</param>
    /// <returns>A receipt miss reason, or null from completion up to but excluding the seven-day cutoff.</returns>
    internal static string? GetAgeMissReason(DateTimeOffset completedAt, DateTimeOffset now)
    {
        var age = now - completedAt;
        if (age < TimeSpan.Zero) return "origin-time-in-future";
        return age >= MaximumOriginAge ? "origin-expired" : null;
    }
}
