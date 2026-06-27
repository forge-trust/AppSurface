namespace ForgeTrust.AppSurface.Auth.Testing;

/// <summary>
/// Request-level helpers for selecting AppSurface test auth personas.
/// </summary>
public static class AppSurfaceTestAuthHttpRequestMessageExtensions
{
    /// <summary>
    /// Selects a configured AppSurface test auth persona for a single request.
    /// </summary>
    /// <param name="request">Request to update.</param>
    /// <param name="personaName">Configured persona name to select.</param>
    /// <returns>The same request for chaining.</returns>
    /// <remarks>
    /// Prefer <c>CreateAppSurfaceClient(...)</c> for whole-client persona selection. This helper is for tests that need
    /// several personas from one client. The harness still treats no selection as anonymous.
    /// </remarks>
    public static HttpRequestMessage WithAppSurfaceTestPersona(this HttpRequestMessage request, string personaName)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(personaName);
        var normalizedPersonaName = AppSurfaceTestPersonaRegistry.NormalizePersonaName(personaName);

        request.Headers.Remove(AppSurfaceTestAuthTransport.PersonaHeaderName);
        request.Headers.Add(AppSurfaceTestAuthTransport.PersonaHeaderName, normalizedPersonaName);
        return request;
    }
}
