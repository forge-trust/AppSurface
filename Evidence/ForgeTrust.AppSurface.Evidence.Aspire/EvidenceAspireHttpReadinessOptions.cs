using ForgeTrust.AppSurface.Evidence.Readiness;

namespace ForgeTrust.AppSurface.Evidence.Aspire;

/// <summary>Configures the HTTP condition and optional owned transport used by an Aspire readiness adapter.</summary>
/// <remarks>
/// Values are snapshotted when <see cref="EvidenceAspireApplication.CreateHttpReadiness"/> is called.
/// A custom client factory is called once for each admitted readiness wait and must return a fresh
/// <see cref="HttpClient"/>; the adapter disposes that client after the wait finishes.
/// </remarks>
public sealed class EvidenceAspireHttpReadinessOptions
{
    /// <summary>Gets the status-only HTTP policy; defaults to exact HTTP 200, a three-second request timer, and a 250 ms retry delay.</summary>
    public HttpReadinessOptions Http { get; init; } = new();

    /// <summary>
    /// Gets an optional factory for a fresh client configured for the resolved endpoint.
    /// The returned client becomes owned by the adapter and is disposed after its wait.
    /// </summary>
    public Func<Uri, HttpClient>? CreateProbeClient { get; init; }

    /// <summary>Validates and snapshots options without invoking the client factory.</summary>
    internal EvidenceAspireHttpReadinessSettings Snapshot()
    {
        ArgumentNullException.ThrowIfNull(Http);
        return new EvidenceAspireHttpReadinessSettings(
            HttpReadinessSettings.Snapshot(Http),
            CreateProbeClient);
    }
}

/// <summary>Immutable internal settings shared by all waits admitted through one adapter.</summary>
internal sealed record EvidenceAspireHttpReadinessSettings(
    HttpReadinessSettings HttpSettings,
    Func<Uri, HttpClient>? CreateProbeClient);
