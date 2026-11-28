using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using RealmReadyEnvironment = ForgeTrust.AppSurface.Auth.Keycloak.KeycloakWorkerProtocol.RealmReadyEnvironment;

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
        AppSurfaceKeycloakRegistrationSnapshot registration,
        string realmImportFile)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentException.ThrowIfNullOrWhiteSpace(realmImportFile);

        var snapshot = registration.Options;
        var projection = registration.Projection;
        _authority = projection.Authority;
        _clientId = projection.ClientId;
        _callbackPath = projection.CallbackPath;
        _signedOutCallbackPath = projection.SignedOutCallbackPath;
        _realmImportDirectory = Path.GetDirectoryName(realmImportFile)
            ?? throw WorkerUnavailable();
        _redirectUris = JsonSerializer.Serialize(snapshot.RedirectUris.Select(uri => uri.ToString()));
        _postLogoutRedirectUris = JsonSerializer.Serialize(snapshot.PostLogoutRedirectUris.Select(uri => uri.ToString()));
        _seededUserNames = string.Join(';', snapshot.SeededUsers.Select(user => user.Username));
        _loginThemeName = registration.Theme?.Registration.Name;
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
            .WithEnvironment(RealmReadyEnvironment.Authority, _authority)
            .WithEnvironment(RealmReadyEnvironment.ClientId, _clientId)
            .WithEnvironment(RealmReadyEnvironment.CallbackPath, _callbackPath)
            .WithEnvironment(RealmReadyEnvironment.SignedOutCallbackPath, _signedOutCallbackPath)
            .WithEnvironment(RealmReadyEnvironment.RedirectUris, _redirectUris)
            .WithEnvironment(RealmReadyEnvironment.PostLogoutRedirectUris, _postLogoutRedirectUris)
            .WithEnvironment(RealmReadyEnvironment.RealmImportDirectory, _realmImportDirectory)
            .WithEnvironment(RealmReadyEnvironment.SeededUserNames, _seededUserNames)
            .WaitFor(keycloak);

        if (_loginThemeName is not null)
        {
            gate.WithEnvironment(RealmReadyEnvironment.LoginThemeName, _loginThemeName);
        }

        return new AppSurfaceKeycloakRealmReady(gate);
    }

    internal static AppSurfaceKeycloakException WorkerUnavailable() =>
        new(
            AppSurfaceKeycloakDiagnosticCodes.RealmReadyWorkerUnavailable,
            $"Problem: the AppSurface Keycloak realm-ready worker is unavailable. Cause: the AppHost cannot resolve the package-owned local verification payload. Fix: restore the AppHost package and run it with the supported .NET SDK. Docs: Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak/README.md. Code: {AppSurfaceKeycloakDiagnosticCodes.RealmReadyWorkerUnavailable}.");
}
