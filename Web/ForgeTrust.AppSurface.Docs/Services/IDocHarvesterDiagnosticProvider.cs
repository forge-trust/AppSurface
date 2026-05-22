using ForgeTrust.AppSurface.Docs.Models;

namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Exposes non-fatal diagnostics captured by a harvester during its most recent successful run.
/// </summary>
/// <remarks>
/// The aggregate health snapshot reads these diagnostics only after the harvester participates in the current snapshot.
/// Failed, timed-out, canceled, or disabled harvesters surface through the primary harvester-health diagnostic instead.
/// </remarks>
internal interface IDocHarvesterDiagnosticProvider
{
    /// <summary>
    /// Gets the diagnostics captured during the most recent completed harvester run.
    /// </summary>
    /// <returns>Structured diagnostics suitable for inclusion in the aggregate harvest-health snapshot.</returns>
    IReadOnlyList<DocHarvestDiagnostic> GetHarvestDiagnostics();
}
