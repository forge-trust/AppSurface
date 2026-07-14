namespace ForgeTrust.AppSurface.Web;

/// <summary>
/// Tracks legacy and current service-worker path assignments without making configuration-provider order observable.
/// </summary>
internal sealed class PwaWorkerPathState
{
    /// <summary>
    /// The default generated service-worker endpoint path.
    /// </summary>
    public const string DefaultServiceWorkerPath = "/service-worker.js";

    private string _legacyValue = DefaultServiceWorkerPath;
    private string _workerValue = DefaultServiceWorkerPath;

    /// <summary>
    /// Gets a value indicating whether the legacy offline path property was assigned explicitly.
    /// </summary>
    public bool LegacyWasSet { get; private set; }

    /// <summary>
    /// Gets a value indicating whether the current worker path property was assigned explicitly.
    /// </summary>
    public bool WorkerWasSet { get; private set; }

    /// <summary>
    /// Gets a value indicating whether the legacy and current properties were assigned different paths.
    /// </summary>
    public bool HasConflict => LegacyWasSet
        && WorkerWasSet
        && !string.Equals(_legacyValue, _workerValue, StringComparison.Ordinal);

    /// <summary>
    /// Gets the current worker path, preferring the current property over the legacy compatibility property.
    /// </summary>
    public string EffectiveValue => WorkerWasSet
        ? _workerValue
        : LegacyWasSet
            ? _legacyValue
            : DefaultServiceWorkerPath;

    /// <summary>
    /// Records an explicit assignment through the legacy offline compatibility property.
    /// </summary>
    /// <param name="value">The assigned app-root-relative worker path.</param>
    public void SetLegacyValue(string value)
    {
        _legacyValue = value;
        LegacyWasSet = true;
    }

    /// <summary>
    /// Records an explicit assignment through the current worker property.
    /// </summary>
    /// <param name="value">The assigned app-root-relative worker path.</param>
    public void SetWorkerValue(string value)
    {
        _workerValue = value;
        WorkerWasSet = true;
    }
}
