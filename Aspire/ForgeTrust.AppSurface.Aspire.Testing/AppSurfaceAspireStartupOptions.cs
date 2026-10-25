using ForgeTrust.AppSurface.Evidence.Readiness;

namespace ForgeTrust.AppSurface.Aspire.Testing;

/// <summary>Configures the shared deadline and HTTP condition for one typed Aspire fixture.</summary>
/// <remarks>
/// Values and delegate references are snapshotted before the factory runs. The default startup budget is two minutes.
/// The default HTTP condition requires exact status 200, with a three-second request maximum and 250-millisecond retry
/// delay. A custom client factory must return a new client whose lifetime transfers to the fixture; the default client
/// uses Aspire's native resource-specific testing helper. The fixture owns application lifecycle and client cleanup.
/// </remarks>
public sealed class AppSurfaceAspireStartupOptions
{
    /// <summary>Gets the finite positive budget for creation through successful HTTP readiness.</summary>
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Gets status, request-timeout, retry-delay, and optional attempt-cap settings.</summary>
    public HttpReadinessOptions Http { get; init; } = new();

    /// <summary>
    /// Gets an optional factory invoked once after endpoint resolution. It must return a fresh client, which the
    /// fixture disposes after probing, including when probing fails.
    /// </summary>
    public Func<Uri, HttpClient>? CreateProbeClient { get; init; }
}
