namespace ForgeTrust.AppSurface.Web;

/// <summary>
/// Provides health-check tags understood by AppSurface Web's platform probe endpoints.
/// </summary>
public static class AppSurfaceHealthCheckTags
{
    /// <summary>
    /// Marks a health check as required for the default readiness endpoint.
    /// </summary>
    /// <remarks>
    /// Checks tagged with this value run for both <c>/health</c> and <c>/ready</c>. Untagged checks run only for
    /// <c>/health</c>. If no checks use this tag, the readiness endpoint reports healthy once the web app has started.
    /// </remarks>
    public const string Ready = "ready";
}
