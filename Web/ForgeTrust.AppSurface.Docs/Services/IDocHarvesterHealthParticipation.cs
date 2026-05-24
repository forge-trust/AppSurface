using ForgeTrust.AppSurface.Docs.Models;

namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Allows a harvester to publish diagnostics and docs while opting out of aggregate strict-health counts.
/// </summary>
/// <remarks>
/// Implement this for best-effort harvesters whose diagnostics should be visible to operators but whose empty, failed,
/// timed-out, or canceled state must not decide whether <see cref="DocHarvestHealthStatus.Failed"/> is returned.
/// </remarks>
public interface IDocHarvesterHealthParticipation
{
    /// <summary>
    /// Gets a value indicating whether this harvester participates in aggregate strict-health success and failure totals.
    /// </summary>
    bool ParticipatesInStrictHealth { get; }
}
