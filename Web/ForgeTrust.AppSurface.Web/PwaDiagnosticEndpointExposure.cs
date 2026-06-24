namespace ForgeTrust.AppSurface.Web;

/// <summary>
/// Controls when AppSurface maps browser-facing PWA diagnostics.
/// </summary>
/// <remarks>
/// Diagnostics can expose deploy and browser-install posture. Keep the default
/// <see cref="DevelopmentOnly"/> unless a host intentionally wants the diagnostics available behind its own controls.
/// </remarks>
public enum PwaDiagnosticEndpointExposure
{
    /// <summary>
    /// Map diagnostics only when the active AppSurface startup context is Development.
    /// </summary>
    DevelopmentOnly = 0,

    /// <summary>
    /// Always map diagnostics. Hosts are responsible for any production access boundary.
    /// </summary>
    Always = 1,

    /// <summary>
    /// Never map diagnostics.
    /// </summary>
    Never = 2
}
