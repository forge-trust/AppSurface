using System.Diagnostics;

namespace ForgeTrust.AppSurface.Core;

/// <summary>
/// Provides canonical activity-source naming for AppSurface-owned instrumentation.
/// </summary>
/// <remarks>
/// This type intentionally lives in <see cref="ForgeTrust.AppSurface.Core"/> so framework and host code can use
/// the canonical source name without depending on <c>ForgeTrust.AppSurface.Observability</c> or OpenTelemetry
/// packages.
/// </remarks>
public static class AppSurfaceActivitySources
{
    /// <summary>
    /// Gets the canonical <see cref="ActivitySource"/> name used by AppSurface-owned tracing.
    /// </summary>
    public const string ActivitySourceName = "ForgeTrust.AppSurface";

    /// <summary>
    /// Gets a shared <see cref="ActivitySource"/> instance with <see cref="ActivitySourceName"/> for framework code that
    /// emits activities.
    /// </summary>
    /// <remarks>
    /// This instance is intended for dependency-agnostic package code that needs an <see cref="ActivitySource"/> value.
    /// It is process-shared and callers must not dispose it. OpenTelemetry registration should still occur through the
    /// host package.
    /// </remarks>
    public static ActivitySource Instance { get; } = new(ActivitySourceName);

    /// <summary>
    /// Gets a read-only list containing only <see cref="ActivitySourceName"/>. Keep this list dependency-neutral and
    /// minimal so packages can merge additional framework source names where needed.
    /// </summary>
    public static IReadOnlyList<string> StandardActivitySourceNames { get; } = Array.AsReadOnly(
        [
            ActivitySourceName
        ]);
}
