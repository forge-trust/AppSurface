using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using static ForgeTrust.AppSurface.Auth.Keycloak.KeycloakBaselineDiagnosticCodes;

namespace ForgeTrust.AppSurface.Auth.Keycloak;

/// <summary>Stable readiness codes shared by the baseline verifier, worker, and AppHost diagnostics.</summary>
internal static class KeycloakBaselineDiagnosticCodes
{
    internal const string InvalidOptions = "ASKEYC001";
    internal const string MetadataUnavailable = "ASKEYC003";
    internal const string MetadataInvalid = "ASKEYC004";
    internal const string RealmEvidenceInvalid = "ASKEYC005";
    internal const string AuthorizationChallengeInvalid = "ASKEYC006";
}

/// <summary>
/// Describes the nonsecret realm baseline that must be present before a local Keycloak realm is ready.
/// </summary>
/// <param name="Authority">The expected realm authority.</param>
/// <param name="ClientId">The expected public client id.</param>
/// <param name="Realm">The expected realm id.</param>
/// <param name="RedirectUris">The redirect URIs expected in the generated realm import.</param>
/// <param name="PostLogoutRedirectUris">The post-logout redirect URIs expected in the generated realm import.</param>
/// <param name="SeededUserNames">The nonsecret usernames expected in the generated realm import.</param>
/// <param name="RealmImportDirectory">The directory containing the generated realm import.</param>
/// <param name="LoginThemeName">The optional login theme expected in the generated realm import.</param>
internal sealed record RealmBaselineDesired(
    string Authority,
    string ClientId,
    string Realm,
    ImmutableArray<string> RedirectUris,
    ImmutableArray<string> PostLogoutRedirectUris,
    ImmutableArray<string> SeededUserNames,
    string RealmImportDirectory,
    string? LoginThemeName);

/// <summary>
/// Represents a safe, stable diagnostic emitted when a realm baseline is not ready.
/// </summary>
internal sealed class RealmBaselineException : InvalidOperationException
{
    /// <summary>
    /// Creates a realm baseline diagnostic.
    /// </summary>
    /// <param name="code">The stable diagnostic code.</param>
    /// <param name="safeMessage">The safe diagnostic message.</param>
    public RealmBaselineException(string code, string safeMessage)
        : base(safeMessage)
    {
        Code = code;
        SafeMessage = safeMessage;
    }

    /// <summary>
    /// Gets the stable diagnostic code.
    /// </summary>
    public string Code { get; }

    /// <summary>
    /// Gets the safe diagnostic message.
    /// </summary>
    public string SafeMessage { get; }
}

/// <summary>
/// Verifies Keycloak metadata, generated realm evidence, and the public-client authorization challenge.
/// </summary>
/// <remarks>
/// This verifier uses only framework HTTP, JSON, and file APIs. It has no dependency on Aspire or host configuration.
/// </remarks>
internal static class RealmBaselineVerifier
{
    private const int MaximumAuthorizationResponseBytes = 16 * 1024;
    private const string SeededUserLastName = "Local User";
    private const string SeededUserEmailDomain = "appsurface.local";

    /// <summary>
    /// Checks metadata, generated realm evidence, and authorization challenge evidence once.
    /// </summary>
    /// <param name="httpClient">HTTP client used for metadata and authorization requests.</param>
    /// <param name="desired">The nonsecret realm baseline to verify.</param>
    /// <param name="readAllText">Reader used to load the generated realm import.</param>
    /// <param name="cancellationToken">Cancellation token for HTTP work.</param>
    /// <returns>A task that completes when all readiness evidence is valid.</returns>
    public static async Task CheckOnceAsync(
        HttpClient httpClient,
        RealmBaselineDesired desired,
        Func<string, string> readAllText,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(readAllText);

        await CheckMetadataAsync(httpClient, desired, cancellationToken).ConfigureAwait(false);
        CheckRealmEvidence(desired, readAllText);
        await CheckAuthorizationChallengeAsync(httpClient, desired, cancellationToken).ConfigureAwait(false);
    }

    private static async Task CheckMetadataAsync(
        HttpClient httpClient,
        RealmBaselineDesired desired,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await httpClient
                .GetAsync($"{desired.Authority}/.well-known/openid-configuration", cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new RealmBaselineException(
                MetadataUnavailable,
                $"Problem: Keycloak OpenID metadata is unavailable. Cause: HTTP request failed ({ex.GetType().Name}). Fix: confirm the container runtime is available, port {GetAuthorityPort(desired.Authority)} is free, and Keycloak finished realm import. Docs: Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak/README.md. Code: {MetadataUnavailable}.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RealmBaselineException(
                MetadataUnavailable,
                $"Problem: Keycloak OpenID metadata is unavailable. Cause: the metadata request exceeded the configured readiness HTTP timeout. Fix: confirm the container runtime is available, port {GetAuthorityPort(desired.Authority)} is free, and Keycloak finished realm import. Docs: Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak/README.md. Code: {MetadataUnavailable}.");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new RealmBaselineException(
                    MetadataUnavailable,
                    $"Problem: Keycloak OpenID metadata returned HTTP {(int)response.StatusCode}. Cause: Keycloak is not ready for realm '{desired.Realm}'. Fix: wait for startup, inspect container logs, and inspect the effective local state. Docs: Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak/README.md. Code: {MetadataUnavailable}.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var issuer = GetOptionalString(document.RootElement, "issuer");
            if (!string.Equals(issuer, desired.Authority, StringComparison.Ordinal))
            {
                throw new RealmBaselineException(
                    MetadataInvalid,
                    $"Problem: Keycloak metadata issuer does not match the expected AppSurface realm. Cause: expected '{desired.Authority}' but received '{issuer ?? "<missing>"}'. Fix: verify the realm import and authority configuration. Docs: Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak/README.md. Code: {MetadataInvalid}.");
            }
        }
    }

    private static void CheckRealmEvidence(RealmBaselineDesired desired, Func<string, string> readAllText)
    {
        var path = RealmImportFilePathResolver.Resolve(desired.RealmImportDirectory, desired.Realm);
        if (!File.Exists(path))
        {
            throw RealmEvidence($"realm import file '{path}' is missing.");
        }

        string importJson;
        try
        {
            importJson = readAllText(path);
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
            if (!string.Equals(GetOptionalString(root, "realm"), desired.Realm, StringComparison.Ordinal))
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

            if (desired.LoginThemeName is not null
                && !string.Equals(GetOptionalString(root, "loginTheme"), desired.LoginThemeName, StringComparison.Ordinal))
            {
                throw RealmEvidence("realm import does not contain the expected login theme.");
            }

            var client = clients.EnumerateArray()
                .FirstOrDefault(candidate => string.Equals(GetOptionalString(candidate, "clientId"), desired.ClientId, StringComparison.Ordinal));
            if (client.ValueKind == JsonValueKind.Undefined)
            {
                throw RealmEvidence("realm import does not contain the expected public client id.");
            }

            CheckClientRedirectEvidence(client, desired);

            var users = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var userElement in userElements.EnumerateArray())
            {
                var username = GetOptionalString(userElement, "username");
                if (username is not null)
                {
                    users.TryAdd(username, userElement);
                }
            }

            foreach (var username in desired.SeededUserNames)
            {
                if (!users.TryGetValue(username, out var userEvidence))
                {
                    throw RealmEvidence($"realm import does not contain seeded user '{username}'.");
                }

                CheckSeededUserProfileEvidence(userEvidence, username);
            }
        }
    }

    private static void CheckSeededUserProfileEvidence(JsonElement user, string username)
    {
        if (!string.Equals(GetOptionalString(user, "lastName"), SeededUserLastName, StringComparison.Ordinal))
        {
            throw RealmEvidence($"seeded user '{username}' does not contain the expected local profile surname.");
        }

        if (!string.Equals(GetOptionalString(user, "email"), $"{username}@{SeededUserEmailDomain}", StringComparison.Ordinal))
        {
            throw RealmEvidence($"seeded user '{username}' does not contain the expected local profile email.");
        }

        if (!user.TryGetProperty("emailVerified", out var emailVerified)
            || emailVerified.ValueKind is not JsonValueKind.True)
        {
            throw RealmEvidence($"seeded user '{username}' does not contain the expected verified local profile email.");
        }
    }

    private static void CheckClientRedirectEvidence(JsonElement client, RealmBaselineDesired desired)
    {
        if (!client.TryGetProperty("redirectUris", out var redirectUris) || redirectUris.ValueKind != JsonValueKind.Array)
        {
            throw RealmEvidence("realm import does not contain client redirect URIs.");
        }

        var redirects = redirectUris.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .ToHashSet(StringComparer.Ordinal);
        var missingRedirectUri = desired.RedirectUris
            .Where(redirectUri => !redirects.Contains(redirectUri))
            .FirstOrDefault();
        if (missingRedirectUri is not null)
        {
            throw RealmEvidence($"realm import does not contain redirect URI '{missingRedirectUri}'.");
        }

        var logoutUris = GetPostLogoutRedirectUris(client);
        var missingLogoutUri = desired.PostLogoutRedirectUris
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

        return postLogoutValue
            .Split("##", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static async Task CheckAuthorizationChallengeAsync(
        HttpClient httpClient,
        RealmBaselineDesired desired,
        CancellationToken cancellationToken)
    {
        var redirectUri = desired.RedirectUris[0];
        var authorizationUri = $"{desired.Authority}/protocol/openid-connect/auth?client_id={Uri.EscapeDataString(desired.ClientId)}&redirect_uri={Uri.EscapeDataString(redirectUri)}&response_type=code&scope=openid&state=appsurface-state&nonce=appsurface-nonce";
        using var response = await httpClient
            .GetAsync(authorizationUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        var body = await ReadBoundedBodyAsync(response.Content, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.BadRequest
            || body.Contains("invalid_client", StringComparison.OrdinalIgnoreCase)
            || body.Contains("invalid_redirect_uri", StringComparison.OrdinalIgnoreCase))
        {
            throw new RealmBaselineException(
                AuthorizationChallengeInvalid,
                $"Problem: Keycloak rejected the configured public client or redirect URI. Cause: client '{desired.ClientId}' or redirect '{redirectUri}' was not accepted by the authorization endpoint. Fix: inspect effective managed settings and update the callback path and web proof port together. Docs: Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak/README.md. Code: {AuthorizationChallengeInvalid}.");
        }

        if (!response.IsSuccessStatusCode && !IsRedirect(response.StatusCode))
        {
            throw new RealmBaselineException(
                AuthorizationChallengeInvalid,
                $"Problem: Keycloak authorization endpoint returned HTTP {(int)response.StatusCode}. Cause: the configured public client did not produce a login challenge or redirect. Fix: inspect Keycloak logs, effective managed settings, and verify the realm import. Docs: Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak/README.md. Code: {AuthorizationChallengeInvalid}.");
        }
    }

    private static string GetAuthorityPort(string authority) =>
        Uri.TryCreate(authority, UriKind.Absolute, out var uri)
            ? uri.Port.ToString(CultureInfo.InvariantCulture)
            : "unknown";

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
            var read = await stream
                .ReadAsync(readBuffer.AsMemory(0, Math.Min(readBuffer.Length, remaining)), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            buffer.Write(readBuffer, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static string? GetOptionalString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static RealmBaselineException RealmEvidence(string cause) =>
        new(
            RealmEvidenceInvalid,
            $"Problem: generated Keycloak realm evidence is invalid. Cause: {cause} Fix: regenerate the realm import from AppSurfaceKeycloakOptions and verify effective managed state without deleting storage. Docs: Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak/README.md. Code: {RealmEvidenceInvalid}.");
}
