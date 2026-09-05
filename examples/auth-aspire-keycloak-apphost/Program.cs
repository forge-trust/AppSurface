using AuthAspireKeycloakAppHost;
using ForgeTrust.AppSurface.Aspire;

namespace AuthAspireKeycloakAppHost;

/// <summary>
/// Starts the focused Keycloak proof AppHost.
/// </summary>
public static class Program
{
    /// <summary>
    /// Runs the configured AppSurface AppHost command.
    /// </summary>
    /// <param name="args">Command-line arguments supplied to the AppHost.</param>
    /// <returns>A task that completes when the selected AppHost command exits.</returns>
    public static Task Main(string[] args) => AspireApp<AuthAspireKeycloakModule>.RunAsync(args);
}
