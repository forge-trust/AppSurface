namespace ForgeTrust.AppSurface.Web;

/// <summary>Provides the fixed request-header names used by named canary endpoints.</summary>
public static class AppSurfaceCanaryHeaderNames
{
    /// <summary>The optional or registration-required opaque deploy marker header.</summary>
    public const string Marker = "X-AppSurface-Canary-Marker";

    /// <summary>The optional or registration-required strict RFC 3339 freshness-boundary header.</summary>
    public const string FreshSince = "X-AppSurface-Canary-Fresh-Since";
}
