using System.Diagnostics.CodeAnalysis;
using ForgeTrust.AppSurface.Auth.Aspire.Keycloak;

namespace AuthAspireKeycloakReadinessGate;

/// <summary>
/// Runs the #782 feasibility gate as a finite process owned by the sample AppHost graph.
/// </summary>
/// <remarks>
/// This is intentionally a sample-only spike rather than a public package API. It reconstructs the existing
/// public readiness probe inputs from non-secret environment values, then returns a process exit code that Aspire
/// can consume through <c>WaitForCompletion</c>. It never receives or prints Keycloak administration credentials.
/// </remarks>
internal static class KeycloakReadinessGateRunner
{
    internal const int SuccessExitCode = 0;
    internal const int FailureExitCode = 1;
    internal const int CancellationExitCode = 124;

    /// <summary>
    /// Executes a single readiness probe and returns a process-safe status code.
    /// </summary>
    /// <param name="getEnvironmentVariable">Reads a named environment value.</param>
    /// <param name="standardError">Receives redacted stage output.</param>
    /// <param name="cancellationToken">Cancels the finite worker during AppHost shutdown.</param>
    /// <returns><see cref="SuccessExitCode"/> on success, <see cref="FailureExitCode"/> on a failed probe, or
    /// <see cref="CancellationExitCode"/> when cancellation wins.</returns>
    [ExcludeFromCodeCoverage(
        Justification = "The default probe adapter delegates to the injectable RunAsync overload, which covers the readiness gate's observable behavior.")]
    internal static Task<int> RunAsync(
        Func<string, string?> getEnvironmentVariable,
        TextWriter standardError,
        CancellationToken cancellationToken) =>
        RunAsync(
            getEnvironmentVariable,
            standardError,
            static async (options, token) =>
            {
                await new AppSurfaceKeycloakReadinessProbe(options).CheckOnceAsync(token).ConfigureAwait(false);
            },
            cancellationToken);

    /// <summary>
    /// Executes the gate with an injectable probe for deterministic feasibility tests.
    /// </summary>
    internal static async Task<int> RunAsync(
        Func<string, string?> getEnvironmentVariable,
        TextWriter standardError,
        Func<AppSurfaceKeycloakOptions, CancellationToken, Task> checkReadinessAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        ArgumentNullException.ThrowIfNull(standardError);
        ArgumentNullException.ThrowIfNull(checkReadinessAsync);

        try
        {
            var options = CreateOptions(getEnvironmentVariable);
            await checkReadinessAsync(options, cancellationToken).ConfigureAwait(false);
            await standardError.WriteLineAsync("AppSurface Keycloak readiness gate completed.").ConfigureAwait(false);
            return SuccessExitCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await standardError.WriteLineAsync("AppSurface Keycloak readiness gate was cancelled.").ConfigureAwait(false);
            return CancellationExitCode;
        }
        catch (AppSurfaceKeycloakException exception)
        {
            await standardError.WriteLineAsync($"AppSurface Keycloak readiness gate failed. Code: {exception.Code}.").ConfigureAwait(false);
            return FailureExitCode;
        }
        catch (Exception)
        {
            await standardError.WriteLineAsync($"AppSurface Keycloak readiness gate failed. Code: {AppSurfaceKeycloakDiagnosticCodes.InvalidOptions}.").ConfigureAwait(false);
            return FailureExitCode;
        }
    }

    private static AppSurfaceKeycloakOptions CreateOptions(Func<string, string?> getEnvironmentVariable)
    {
        var authority = ParseLocalAuthority(Required(getEnvironmentVariable, KeycloakReadinessGateEnvironment.Authority));
        var callbackPath = Required(getEnvironmentVariable, KeycloakReadinessGateEnvironment.CallbackPath);
        var signedOutCallbackPath = Required(getEnvironmentVariable, KeycloakReadinessGateEnvironment.SignedOutCallbackPath);
        var redirectUri = ParseLocalRedirectUri(Required(getEnvironmentVariable, KeycloakReadinessGateEnvironment.RedirectUri), callbackPath);
        var postLogoutRedirectUri = ParseLocalRedirectUri(
            Required(getEnvironmentVariable, KeycloakReadinessGateEnvironment.PostLogoutRedirectUri),
            signedOutCallbackPath);
        var realmImportDirectory = Required(getEnvironmentVariable, KeycloakReadinessGateEnvironment.RealmImportDirectory);

        var options = new AppSurfaceKeycloakOptions
        {
            Realm = authority.Realm,
            ClientId = Required(getEnvironmentVariable, KeycloakReadinessGateEnvironment.ClientId),
            CallbackPath = callbackPath,
            SignedOutCallbackPath = signedOutCallbackPath,
            KeycloakPort = authority.Port,
            WebProofPort = redirectUri.Port,
            RealmImportDirectory = realmImportDirectory,
        };
        options.RedirectUris.Clear();
        options.RedirectUris.Add(redirectUri);
        options.PostLogoutRedirectUris.Clear();
        options.PostLogoutRedirectUris.Add(postLogoutRedirectUri);
        options.Validate();
        return options;
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

    private static Uri ParseLocalRedirectUri(string value, string expectedPath)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !IsLocalhost(uri)
            || uri.Port is < 1 or > 65535
            || !string.Equals(uri.AbsolutePath, expectedPath, StringComparison.Ordinal))
        {
            throw InvalidConfiguration();
        }

        return uri;
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
            $"Problem: readiness-gate configuration is invalid. Cause: the AppHost did not provide a supported local proof input. Fix: run the documented Auth Aspire Keycloak readiness spike with its generated AppHost graph. Docs: examples/auth-aspire-keycloak-apphost/README.md. Code: {AppSurfaceKeycloakDiagnosticCodes.InvalidOptions}.");
}
