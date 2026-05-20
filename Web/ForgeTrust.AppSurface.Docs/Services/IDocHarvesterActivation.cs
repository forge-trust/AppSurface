namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Lets an optional harvester tell AppSurface Docs whether it should participate in the current source snapshot.
/// </summary>
/// <remarks>
/// Disabled optional harvesters are omitted from aggregate harvest-health accounting so a deliberately disabled feature
/// cannot convert an otherwise all-failed snapshot into a degraded one. Direct calls to the harvester may still return an
/// empty document set for compatibility.
/// </remarks>
internal interface IDocHarvesterActivation
{
    /// <summary>
    /// Gets a value indicating whether the harvester should run and count toward harvest-health totals.
    /// </summary>
    bool IsEnabled { get; }
}
