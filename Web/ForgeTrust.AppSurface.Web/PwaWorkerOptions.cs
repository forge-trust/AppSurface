namespace ForgeTrust.AppSurface.Web;

/// <summary>
/// Configures the shared AppSurface service-worker and registration-helper endpoints.
/// </summary>
/// <remarks>
/// The worker is mapped when either <see cref="PwaOfflineOptions.Enabled"/> or
/// <see cref="PwaPushOptions.Enabled"/> is enabled. Worker activation is independent from install-manifest metadata.
/// </remarks>
public sealed class PwaWorkerOptions
{
    private readonly PwaWorkerPathState _pathState;

    /// <summary>
    /// Initializes a standalone worker options instance with default paths.
    /// </summary>
    public PwaWorkerOptions()
        : this(new PwaWorkerPathState())
    {
    }

    /// <summary>
    /// Initializes worker options backed by shared compatibility-path assignment state.
    /// </summary>
    /// <param name="pathState">The shared legacy and current path assignment state.</param>
    internal PwaWorkerOptions(PwaWorkerPathState pathState)
    {
        _pathState = pathState;
    }

    /// <summary>
    /// Gets or sets the app-root-relative generated service-worker endpoint path.
    /// </summary>
    /// <remarks>
    /// The default is <c>/service-worker.js</c>. On the instance owned by <see cref="PwaOptions"/>, this setting is
    /// compatible with <see cref="PwaOfflineOptions.ServiceWorkerPath"/>. Configuring both properties with different
    /// values fails startup instead of depending on configuration-provider assignment order. Percent escapes are
    /// rejected because this value owns a generated endpoint.
    /// </remarks>
    public string ServiceWorkerPath
    {
        get => _pathState.EffectiveValue;
        set => _pathState.SetWorkerValue(value);
    }

    /// <summary>
    /// Gets or sets the app-root-relative endpoint for the inert registration helper.
    /// </summary>
    /// <remarks>
    /// The default is <c>/_appsurface/pwa/register.js</c>. The helper is mapped only when push is enabled. Percent
    /// escapes are rejected because this value owns a generated endpoint.
    /// </remarks>
    public string RegistrationHelperPath { get; set; } = "/_appsurface/pwa/register.js";

    /// <summary>
    /// Gets a value indicating whether legacy and current worker path properties were assigned conflicting values.
    /// </summary>
    internal bool HasServiceWorkerPathConflict => _pathState.HasConflict;
}
