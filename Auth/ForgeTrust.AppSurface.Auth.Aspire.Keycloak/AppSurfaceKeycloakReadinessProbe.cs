using System.Net;
using System.Text.Json;

namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak;

/// <summary>
/// Performs bounded, multi-signal readiness checks for the AppSurface local Keycloak proof.
/// </summary>
public sealed class AppSurfaceKeycloakReadinessProbe
{
    private const int MaximumAuthorizationResponseBytes = 16 * 1024;
    private static readonly HttpClient DefaultHttpClient = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = AppSurfaceKeycloakDefaults.ReadinessTimeout,
    };

    private readonly HttpClient _httpClient;
    private readonly AppSurfaceKeycloakOptions _options;
    private readonly Func<string, string> _readAllText;

    /// <summary>
    /// Creates a readiness probe for validated Keycloak proof options.
    /// </summary>
    /// <param name="options">The proof options to verify.</param>
    /// <param name="httpClient">Optional HTTP client, primarily for tests.</param>
    /// <remarks>
    /// The default client uses the bounded readiness timeout and disables automatic redirects so authorization
    /// challenge checks can inspect Keycloak's raw response. Injected clients should preserve those behaviors.
    /// </remarks>
    public AppSurfaceKeycloakReadinessProbe(AppSurfaceKeycloakOptions options, HttpClient? httpClient = null)
        : this(options, httpClient ?? DefaultHttpClient, File.ReadAllText)
    {
    }

    /// <summary>
    /// Creates a readiness probe with injectable HTTP and realm-file readers for deterministic tests.
    /// </summary>
    /// <param name="options">The proof options to verify.</param>
    /// <param name="httpClient">HTTP client used for metadata and authorization requests.</param>
    /// <param name="readAllText">Realm import file reader.</param>
    internal AppSurfaceKeycloakReadinessProbe(
        AppSurfaceKeycloakOptions options,
        HttpClient httpClient,
        Func<string, string> readAllText)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(readAllText);
        options.Validate();
        _options = options;
        _httpClient = httpClient;
        _readAllText = readAllText;
    }

    /// <summary>
    /// Checks metadata, generated realm evidence, and authorization challenge evidence once.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for HTTP work.</param>
    /// <returns>A successful readiness result.</returns>
    public async Task<AppSurfaceKeycloakReadinessResult> CheckOnceAsync(CancellationToken cancellationToken = default)
    {
        var projection = _options.CreateConfigurationProjection();
        await CheckMetadataAsync(projection.Authority, cancellationToken).ConfigureAwait(false);
        CheckRealmEvidence();
        await CheckAuthorizationChallengeAsync(projection, cancellationToken).ConfigureAwait(false);
        return new AppSurfaceKeycloakReadinessResult(projection.Authority, _options.ClientId, _options.Realm);
    }

    private async Task CheckMetadataAsync(string authority, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.GetAsync($"{authority}/.well-known/openid-configuration", cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new AppSurfaceKeycloakException(
                AppSurfaceKeycloakDiagnosticCodes.MetadataUnavailable,
                $"Problem: Keycloak OpenID metadata is unavailable. Cause: HTTP request failed ({ex.GetType().Name}). Fix: confirm the container runtime is available, port {_options.KeycloakPort} is free, and Keycloak finished realm import. Docs: Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak/README.md. Code: {AppSurfaceKeycloakDiagnosticCodes.MetadataUnavailable}.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AppSurfaceKeycloakException(
                AppSurfaceKeycloakDiagnosticCodes.MetadataUnavailable,
                $"Problem: Keycloak OpenID metadata is unavailable. Cause: the metadata request exceeded the configured readiness HTTP timeout. Fix: confirm the container runtime is available, port {_options.KeycloakPort} is free, and Keycloak finished realm import. Docs: Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak/README.md. Code: {AppSurfaceKeycloakDiagnosticCodes.MetadataUnavailable}.");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new AppSurfaceKeycloakException(
                    AppSurfaceKeycloakDiagnosticCodes.MetadataUnavailable,
                    $"Problem: Keycloak OpenID metadata returned HTTP {(int)response.StatusCode}. Cause: Keycloak is not ready for realm '{_options.Realm}'. Fix: wait for startup, inspect container logs, or reset stale persistent data. Docs: Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak/README.md. Code: {AppSurfaceKeycloakDiagnosticCodes.MetadataUnavailable}.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var issuer = GetOptionalString(document.RootElement, "issuer");
            if (!string.Equals(issuer, authority, StringComparison.Ordinal))
            {
                throw new AppSurfaceKeycloakException(
                    AppSurfaceKeycloakDiagnosticCodes.MetadataInvalid,
                    $"Problem: Keycloak metadata issuer does not match the expected AppSurface realm. Cause: expected '{authority}' but received '{issuer ?? "<missing>"}'. Fix: verify the realm import and authority configuration. Docs: Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak/README.md. Code: {AppSurfaceKeycloakDiagnosticCodes.MetadataInvalid}.");
            }
        }
    }

    private void CheckRealmEvidence()
    {
        var path = AppSurfaceKeycloakRealmImportPaths.GetRealmImportFilePath(_options.RealmImportDirectory, _options.Realm);
        if (!File.Exists(path))
        {
            throw RealmEvidence($"realm import file '{path}' is missing.");
        }

        string importJson;
        try
        {
            importJson = _readAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw RealmEvidence($"realm import could not be read ({ex.GetType().Name}).");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(importJson);
        }
        catch (JsonException)
        {
            throw RealmEvidence("realm import is not valid JSON.");
        }

        using (document)
        {
            var root = document.RootElement;
            if (!string.Equals(GetOptionalString(root, "realm"), _options.Realm, StringComparison.Ordinal))
            {
                throw RealmEvidence("realm import does not contain the expected realm id.");
            }

            if (!root.TryGetProperty("clients", out var clients) || clients.ValueKind != JsonValueKind.Array)
            {
                throw RealmEvidence("realm import does not contain a clients array.");
            }

            if (!root.TryGetProperty("users", out var userElements) || userElements.ValueKind != JsonValueKind.Array)
            {
                throw RealmEvidence("realm import does not contain a users array.");
            }

            if (_options.LoginTheme is not null
                && !string.Equals(GetOptionalString(root, "loginTheme"), _options.LoginTheme.Name, StringComparison.Ordinal))
            {
                throw RealmEvidence("realm import does not contain the expected login theme.");
            }

            var client = clients.EnumerateArray()
                .FirstOrDefault(candidate => string.Equals(GetOptionalString(candidate, "clientId"), _options.ClientId, StringComparison.Ordinal));
            if (client.ValueKind == JsonValueKind.Undefined)
            {
                throw RealmEvidence("realm import does not contain the expected public client id.");
            }

            CheckClientRedirectEvidence(client);

            var users = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var userElement in userElements.EnumerateArray())
            {
                var username = GetOptionalString(userElement, "username");
                if (username is not null)
                {
                    users.TryAdd(username, userElement);
                }
            }

            foreach (var user in _options.SeededUsers)
            {
                if (!users.TryGetValue(user.Username, out var userEvidence))
                {
                    throw RealmEvidence($"realm import does not contain seeded user '{user.Username}'.");
                }

                CheckSeededUserProfileEvidence(userEvidence, user.Username);
            }
        }
    }

    private static void CheckSeededUserProfileEvidence(JsonElement user, string username)
    {
        if (!string.Equals(GetOptionalString(user, "lastName"), AppSurfaceKeycloakDefaults.SeededUserLastName, StringComparison.Ordinal))
        {
            throw RealmEvidence($"seeded user '{username}' does not contain the expected local profile surname.");
        }

        if (!string.Equals(GetOptionalString(user, "email"), AppSurfaceKeycloakDefaults.SeededUserEmail(username), StringComparison.Ordinal))
        {
            throw RealmEvidence($"seeded user '{username}' does not contain the expected local profile email.");
        }

        if (!user.TryGetProperty("emailVerified", out var emailVerified)
            || emailVerified.ValueKind is not JsonValueKind.True)
        {
            throw RealmEvidence($"seeded user '{username}' does not contain the expected verified local profile email.");
        }
    }

    private void CheckClientRedirectEvidence(JsonElement client)
    {
        if (!client.TryGetProperty("redirectUris", out var redirectUris) || redirectUris.ValueKind != JsonValueKind.Array)
        {
            throw RealmEvidence("realm import does not contain client redirect URIs.");
        }

        var redirects = redirectUris.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .ToHashSet(StringComparer.Ordinal);
        var missingRedirectUri = _options.RedirectUris
            .Select(uri => uri.ToString())
            .Where(redirectUri => !redirects.Contains(redirectUri))
            .FirstOrDefault();
        if (missingRedirectUri is not null)
        {
            throw RealmEvidence($"realm import does not contain redirect URI '{missingRedirectUri}'.");
        }

        var logoutUris = GetPostLogoutRedirectUris(client);
        var missingLogoutUri = _options.PostLogoutRedirectUris
            .Select(uri => uri.ToString())
            .Where(logoutUri => !logoutUris.Contains(logoutUri))
            .FirstOrDefault();
        if (missingLogoutUri is not null)
        {
            throw RealmEvidence($"realm import does not contain post-logout redirect URI '{missingLogoutUri}'.");
        }
    }

    private static HashSet<string> GetPostLogoutRedirectUris(JsonElement client)
    {
        if (!client.TryGetProperty("attributes", out var attributes) || attributes.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        if (!attributes.TryGetProperty("post.logout.redirect.uris", out var postLogout) || postLogout.ValueKind != JsonValueKind.String)
        {
            return [];
        }

        var postLogoutValue = postLogout.GetString();
        if (string.IsNullOrEmpty(postLogoutValue))
        {
            return [];
        }

        return postLogoutValue.Split("##", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
    }

    private async Task CheckAuthorizationChallengeAsync(AppSurfaceKeycloakConfigurationProjection projection, CancellationToken cancellationToken)
    {
        var authorizationUri = $"{projection.Authority}/protocol/openid-connect/auth?client_id={Uri.EscapeDataString(projection.ClientId)}&redirect_uri={Uri.EscapeDataString(_options.RedirectUris[0].ToString())}&response_type=code&scope=openid&state=appsurface-state&nonce=appsurface-nonce";
        using var response = await _httpClient
            .GetAsync(authorizationUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        var body = await ReadBoundedBodyAsync(response.Content, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.BadRequest
            || body.Contains("invalid_client", StringComparison.OrdinalIgnoreCase)
            || body.Contains("invalid_redirect_uri", StringComparison.OrdinalIgnoreCase))
        {
            throw new AppSurfaceKeycloakException(
                AppSurfaceKeycloakDiagnosticCodes.AuthorizationChallengeInvalid,
                $"Problem: Keycloak rejected the configured public client or redirect URI. Cause: client '{projection.ClientId}' or redirect '{_options.RedirectUris[0]}' was not accepted by the authorization endpoint. Fix: reset stale persistent data or update the callback path and web proof port together. Docs: Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak/README.md. Code: {AppSurfaceKeycloakDiagnosticCodes.AuthorizationChallengeInvalid}.");
        }

        if (!response.IsSuccessStatusCode && !IsRedirect(response.StatusCode))
        {
            throw new AppSurfaceKeycloakException(
                AppSurfaceKeycloakDiagnosticCodes.AuthorizationChallengeInvalid,
                $"Problem: Keycloak authorization endpoint returned HTTP {(int)response.StatusCode}. Cause: the configured public client did not produce a login challenge or redirect. Fix: inspect Keycloak logs, reset stale persistent data, or verify the realm import. Docs: Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak/README.md. Code: {AppSurfaceKeycloakDiagnosticCodes.AuthorizationChallengeInvalid}.");
        }
    }

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.Moved
            or HttpStatusCode.Redirect
            or HttpStatusCode.RedirectMethod
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private static async Task<string> ReadBoundedBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaximumAuthorizationResponseBytes)
        {
            return string.Empty;
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var readBuffer = new byte[4096];
        while (buffer.Length < MaximumAuthorizationResponseBytes)
        {
            var remaining = MaximumAuthorizationResponseBytes - (int)buffer.Length;
            var read = await stream.ReadAsync(readBuffer.AsMemory(0, Math.Min(readBuffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            buffer.Write(readBuffer, 0, read);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static string? GetOptionalString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static AppSurfaceKeycloakException RealmEvidence(string cause) =>
        new(
            AppSurfaceKeycloakDiagnosticCodes.RealmEvidenceInvalid,
            $"Problem: generated Keycloak realm evidence is invalid. Cause: {cause} Fix: regenerate the realm import from AppSurfaceKeycloakOptions and reset stale persistent data if enabled. Docs: Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak/README.md. Code: {AppSurfaceKeycloakDiagnosticCodes.RealmEvidenceInvalid}.");
}
