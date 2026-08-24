using System.Runtime.Loader;
using System.Text.Json;

namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak;

/// <summary>
/// Runs the package-owned finite readiness verification process.
/// </summary>
internal static class AppSurfaceKeycloakRealmReadyRunner
{
    internal const int CancellationExitCode = 124;
    internal const int FailureExitCode = 1;
    internal const int SuccessExitCode = 0;

    internal static async Task<int> RunAsync(
        Func<string, string?> getEnvironmentVariable,
        TextWriter standardError,
        CancellationToken cancellationToken)
        => await RunAsync(
                getEnvironmentVariable,
                standardError,
                static (configuration, token) =>
                    new AppSurfaceKeycloakReadinessProbe(
                        configuration.Options,
                        configuration.LoginThemeName,
                        configuration.SeededUserNames)
                    .CheckOnceAsync(token),
                cancellationToken)
            .ConfigureAwait(false);

    internal static async Task<int> RunAsync(
        Func<string, string?> getEnvironmentVariable,
        TextWriter standardError,
        Func<AppSurfaceKeycloakRealmReadyRunnerConfiguration, CancellationToken, Task> checkReadinessAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        ArgumentNullException.ThrowIfNull(standardError);
        ArgumentNullException.ThrowIfNull(checkReadinessAsync);

        try
        {
            var configuration = AppSurfaceKeycloakRealmReadyRunnerConfiguration.Create(getEnvironmentVariable);
            await checkReadinessAsync(configuration, cancellationToken).ConfigureAwait(false);
            await standardError.WriteLineAsync("AppSurface Keycloak realm-ready gate completed.").ConfigureAwait(false);
            return SuccessExitCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await standardError.WriteLineAsync("AppSurface Keycloak realm-ready gate was cancelled.").ConfigureAwait(false);
            return CancellationExitCode;
        }
        catch (AppSurfaceKeycloakException exception)
        {
            await standardError.WriteLineAsync($"AppSurface Keycloak realm-ready gate failed. Code: {exception.Code}.").ConfigureAwait(false);
            return FailureExitCode;
        }
        catch (Exception)
        {
            await standardError.WriteLineAsync($"AppSurface Keycloak realm-ready gate failed. Code: {AppSurfaceKeycloakDiagnosticCodes.InvalidOptions}.").ConfigureAwait(false);
            return FailureExitCode;
        }
    }

    internal static async Task<int> MainAsync()
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Action<AssemblyLoadContext> unloadingHandler = _ => cancellation.Cancel();
        EventHandler processExitHandler = (_, _) => cancellation.Cancel();
        global::System.Console.CancelKeyPress += cancelHandler;
        AssemblyLoadContext.Default.Unloading += unloadingHandler;
        AppDomain.CurrentDomain.ProcessExit += processExitHandler;

        try
        {
            return await RunAsync(Environment.GetEnvironmentVariable, global::System.Console.Error, cancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            global::System.Console.CancelKeyPress -= cancelHandler;
            AssemblyLoadContext.Default.Unloading -= unloadingHandler;
            AppDomain.CurrentDomain.ProcessExit -= processExitHandler;
        }
    }
}

/// <summary>
/// Reconstructs validated probe inputs from the explicit nonsecret executable environment contract.
/// </summary>
internal sealed class AppSurfaceKeycloakRealmReadyRunnerConfiguration
{
    private AppSurfaceKeycloakRealmReadyRunnerConfiguration(
        AppSurfaceKeycloakOptions options,
        string? loginThemeName,
        IReadOnlyList<string> seededUserNames)
    {
        Options = options;
        LoginThemeName = loginThemeName;
        SeededUserNames = seededUserNames;
    }

    internal string? LoginThemeName { get; }

    internal AppSurfaceKeycloakOptions Options { get; }

    internal IReadOnlyList<string> SeededUserNames { get; }

    internal static AppSurfaceKeycloakRealmReadyRunnerConfiguration Create(Func<string, string?> getEnvironmentVariable)
    {
        var authority = ParseLocalAuthority(Required(getEnvironmentVariable, AppSurfaceKeycloakRealmReadyEnvironment.Authority));
        var callbackPath = Required(getEnvironmentVariable, AppSurfaceKeycloakRealmReadyEnvironment.CallbackPath);
        var signedOutCallbackPath = Required(getEnvironmentVariable, AppSurfaceKeycloakRealmReadyEnvironment.SignedOutCallbackPath);
        var redirectUris = ParseLocalRedirectUris(
            Required(getEnvironmentVariable, AppSurfaceKeycloakRealmReadyEnvironment.RedirectUris),
            callbackPath);
        var postLogoutRedirectUris = ParseLocalRedirectUris(
            Required(getEnvironmentVariable, AppSurfaceKeycloakRealmReadyEnvironment.PostLogoutRedirectUris),
            signedOutCallbackPath);
        var seededUserNames = ParseSeededUserNames(Required(getEnvironmentVariable, AppSurfaceKeycloakRealmReadyEnvironment.SeededUserNames));
        var options = new AppSurfaceKeycloakOptions
        {
            Realm = authority.Realm,
            ClientId = Required(getEnvironmentVariable, AppSurfaceKeycloakRealmReadyEnvironment.ClientId),
            CallbackPath = callbackPath,
            SignedOutCallbackPath = signedOutCallbackPath,
            KeycloakPort = authority.Port,
            WebProofPort = redirectUris[0].Port,
            RealmImportDirectory = Required(getEnvironmentVariable, AppSurfaceKeycloakRealmReadyEnvironment.RealmImportDirectory),
        };
        options.RedirectUris.Clear();
        foreach (var redirectUri in redirectUris)
        {
            options.RedirectUris.Add(redirectUri);
        }
        options.PostLogoutRedirectUris.Clear();
        foreach (var postLogoutRedirectUri in postLogoutRedirectUris)
        {
            options.PostLogoutRedirectUris.Add(postLogoutRedirectUri);
        }
        options.SeededUsers.Clear();
        foreach (var username in seededUserNames)
        {
            options.SeededUsers.Add(new AppSurfaceKeycloakUserOptions(username, "not-used", username, username));
        }

        options.Validate();
        var loginThemeName = getEnvironmentVariable(AppSurfaceKeycloakRealmReadyEnvironment.LoginThemeName);
        return new AppSurfaceKeycloakRealmReadyRunnerConfiguration(
            options,
            string.IsNullOrWhiteSpace(loginThemeName) ? null : loginThemeName,
            seededUserNames);
    }

    private static (string Realm, int Port) ParseLocalAuthority(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var authority)
            || !string.Equals(authority.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
            || !IsLocalhost(authority)
            || authority.Port is < 1 or > 65535
            || !string.IsNullOrEmpty(authority.UserInfo)
            || !string.IsNullOrEmpty(authority.Query)
            || !string.IsNullOrEmpty(authority.Fragment)
            || authority.OriginalString.Contains("%2f", StringComparison.OrdinalIgnoreCase)
            || authority.OriginalString.Contains("%5c", StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidConfiguration();
        }

        var pathSegments = authority.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (pathSegments.Length != 2 || !string.Equals(pathSegments[0], "realms", StringComparison.Ordinal))
        {
            throw InvalidConfiguration();
        }

        return (pathSegments[1], authority.Port);
    }

    private static IReadOnlyList<Uri> ParseLocalRedirectUris(string value, string expectedPath)
    {
        string[]? values;
        try
        {
            values = JsonSerializer.Deserialize<string[]>(value);
        }
        catch (JsonException)
        {
            throw InvalidConfiguration();
        }

        if (values is not { Length: > 0 })
        {
            throw InvalidConfiguration();
        }

        var uris = new List<Uri>(values.Length);
        foreach (var uriValue in values)
        {
            if (!Uri.TryCreate(uriValue, UriKind.Absolute, out var uri)
                || !IsLocalhost(uri)
                || uri.Port is < 1 or > 65535
                || !string.Equals(uri.AbsolutePath, expectedPath, StringComparison.Ordinal))
            {
                throw InvalidConfiguration();
            }

            uris.Add(uri);
        }

        return uris;
    }

    private static IReadOnlyList<string> ParseSeededUserNames(string value)
    {
        var names = value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (names.Length == 0 || names.Length != names.Distinct(StringComparer.Ordinal).Count())
        {
            throw InvalidConfiguration();
        }

        return names;
    }

    private static bool IsLocalhost(Uri uri) =>
        (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
            || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        && (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Host, "127.0.0.1", StringComparison.Ordinal));

    private static string Required(Func<string, string?> getEnvironmentVariable, string name)
    {
        var value = getEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? throw InvalidConfiguration() : value;
    }

    private static AppSurfaceKeycloakException InvalidConfiguration() =>
        new(
            AppSurfaceKeycloakDiagnosticCodes.InvalidOptions,
            $"Problem: realm-ready configuration is invalid. Cause: the AppHost did not provide a supported local proof input. Fix: register the Keycloak resource through AddAppSurfaceKeycloak and keep local proof inputs unchanged. Docs: Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak/README.md. Code: {AppSurfaceKeycloakDiagnosticCodes.InvalidOptions}.");
}
