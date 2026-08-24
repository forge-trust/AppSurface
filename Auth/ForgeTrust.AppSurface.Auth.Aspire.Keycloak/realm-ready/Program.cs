namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak;

/// <summary>
/// Hosts the package's finite realm-ready executable mode.
/// </summary>
internal static class AppSurfaceKeycloakRealmReadyEntryPoint
{
    private const string RealmReadyArgument = "--appsurface-keycloak-realm-ready";

    private static Task<int> Main(string[] args)
    {
        if (args.Length == 1 && string.Equals(args[0], RealmReadyArgument, StringComparison.Ordinal))
        {
            return AppSurfaceKeycloakRealmReadyRunner.MainAsync();
        }

        return Task.FromResult(AppSurfaceKeycloakRealmReadyRunner.FailureExitCode);
    }
}
