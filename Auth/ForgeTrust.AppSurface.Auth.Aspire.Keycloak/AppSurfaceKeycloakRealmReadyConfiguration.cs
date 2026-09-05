using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak;

/// <summary>
/// Holds the safe, immutable inputs needed to materialize the package-owned realm-ready executable.
/// </summary>
internal sealed class AppSurfaceKeycloakRealmReadyConfiguration
{
    private readonly string _authority;
    private readonly string _callbackPath;
    private readonly string _clientId;
    private readonly string? _loginThemeName;
    private readonly string _postLogoutRedirectUris;
    private readonly string _realmImportDirectory;
    private readonly string _redirectUris;
    private readonly string _seededUserNames;
    private readonly string _signedOutCallbackPath;

    internal AppSurfaceKeycloakRealmReadyConfiguration(
        AppSurfaceKeycloakOptions snapshot,
        string realmImportFile,
        AppSurfaceKeycloakThemeRegistration? theme)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(realmImportFile);

        var projection = snapshot.CreateConfigurationProjection();
        _authority = projection.Authority;
        _clientId = projection.ClientId;
        _callbackPath = projection.CallbackPath;
        _signedOutCallbackPath = projection.SignedOutCallbackPath;
        _realmImportDirectory = Path.GetDirectoryName(realmImportFile)
            ?? throw WorkerUnavailable();
        _redirectUris = JsonSerializer.Serialize(snapshot.RedirectUris.Select(uri => uri.ToString()));
        _postLogoutRedirectUris = JsonSerializer.Serialize(snapshot.PostLogoutRedirectUris.Select(uri => uri.ToString()));
        _seededUserNames = string.Join(';', snapshot.SeededUsers.Select(user => user.Username));
        _loginThemeName = theme?.Name;
    }

    internal AppSurfaceKeycloakRealmReady Create(IResourceBuilder<KeycloakResource> keycloak)
    {
        ArgumentNullException.ThrowIfNull(keycloak);

        var worker = AppSurfaceKeycloakRealmReadyWorker.Resolve();
        var gate = keycloak.ApplicationBuilder
            .AddExecutable(
                $"{keycloak.Resource.Name}-realm-ready",
                command: "dotnet",
                workingDirectory: worker.WorkingDirectory,
                args: worker.Arguments)
            .WithEnvironment(AppSurfaceKeycloakRealmReadyEnvironment.Authority, _authority)
            .WithEnvironment(AppSurfaceKeycloakRealmReadyEnvironment.ClientId, _clientId)
            .WithEnvironment(AppSurfaceKeycloakRealmReadyEnvironment.CallbackPath, _callbackPath)
            .WithEnvironment(AppSurfaceKeycloakRealmReadyEnvironment.SignedOutCallbackPath, _signedOutCallbackPath)
            .WithEnvironment(AppSurfaceKeycloakRealmReadyEnvironment.RedirectUris, _redirectUris)
            .WithEnvironment(AppSurfaceKeycloakRealmReadyEnvironment.PostLogoutRedirectUris, _postLogoutRedirectUris)
            .WithEnvironment(AppSurfaceKeycloakRealmReadyEnvironment.RealmImportDirectory, _realmImportDirectory)
            .WithEnvironment(AppSurfaceKeycloakRealmReadyEnvironment.SeededUserNames, _seededUserNames)
            .WaitFor(keycloak);

        if (_loginThemeName is not null)
        {
            gate.WithEnvironment(AppSurfaceKeycloakRealmReadyEnvironment.LoginThemeName, _loginThemeName);
        }

        return new AppSurfaceKeycloakRealmReady(gate);
    }

    internal static AppSurfaceKeycloakException WorkerUnavailable() =>
        new(
            AppSurfaceKeycloakDiagnosticCodes.RealmReadyWorkerUnavailable,
            $"Problem: the AppSurface Keycloak realm-ready worker is unavailable. Cause: the AppHost cannot resolve the package-owned local verification payload. Fix: restore the AppHost package and run it with the supported .NET SDK. Docs: Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak/README.md. Code: {AppSurfaceKeycloakDiagnosticCodes.RealmReadyWorkerUnavailable}.");
}
