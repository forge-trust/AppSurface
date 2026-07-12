namespace ForgeTrust.AppSurface.Web;

/// <summary>
/// Tracks legacy and current service-worker path assignments without making configuration-provider order observable.
/// </summary>
internal sealed class PwaWorkerPathState
{
    public const string DefaultServiceWorkerPath = "/service-worker.js";

    private string _legacyValue = DefaultServiceWorkerPath;
    private string _workerValue = DefaultServiceWorkerPath;

    public bool LegacyWasSet { get; private set; }

    public bool WorkerWasSet { get; private set; }

    public bool HasConflict => LegacyWasSet
        && WorkerWasSet
        && !string.Equals(_legacyValue, _workerValue, StringComparison.Ordinal);

    public string EffectiveValue => WorkerWasSet
        ? _workerValue
        : LegacyWasSet
            ? _legacyValue
            : DefaultServiceWorkerPath;

    public void SetLegacyValue(string value)
    {
        _legacyValue = value;
        LegacyWasSet = true;
    }

    public void SetWorkerValue(string value)
    {
        _workerValue = value;
        WorkerWasSet = true;
    }
}
