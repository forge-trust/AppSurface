namespace ForgeTrust.AppSurface.Web;

/// <summary>
/// Describes the current application-owned proof state for a named canary.
/// </summary>
public enum AppSurfaceCanaryStatus
{
    /// <summary>The expected proof is present and acceptable.</summary>
    Pass = 0,

    /// <summary>The workflow may still produce acceptable proof.</summary>
    Pending = 1,

    /// <summary>The available proof demonstrates failure.</summary>
    Fail = 2,

    /// <summary>Proof exists but does not satisfy the requested freshness boundary.</summary>
    Stale = 3,

    /// <summary>The evaluator is registered, but its proof dependency is intentionally unavailable.</summary>
    NotConfigured = 4,
}
