using System.Linq;
using System.Text.RegularExpressions;

namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak;

/// <summary>
/// Configures the deterministic local Keycloak realm and public client used for AppSurface OIDC proof AppHosts.
/// </summary>
public sealed class AppSurfaceKeycloakOptions
{
    private static readonly Regex RealmPattern = new("^[a-z][a-z0-9-]{2,62}$", RegexOptions.CultureInvariant);
    private static readonly Regex ClientPattern = new("^[a-z][a-z0-9._-]{2,63}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Gets or sets the local Keycloak realm name.
    /// </summary>
    public string Realm { get; set; } = AppSurfaceKeycloakDefaults.Realm;

    /// <summary>
    /// Gets or sets the local public OIDC client id.
    /// </summary>
    public string ClientId { get; set; } = AppSurfaceKeycloakDefaults.ClientId;

    /// <summary>
    /// Gets or sets the local display name imported for the OIDC client.
    /// </summary>
    public string ClientDisplayName { get; set; } = "AppSurface local web proof";

    /// <summary>
    /// Gets or sets the OIDC callback path used by the web proof.
    /// </summary>
    public string CallbackPath { get; set; } = AppSurfaceKeycloakDefaults.CallbackPath;

    /// <summary>
    /// Gets or sets the OIDC signed-out callback path used by the web proof.
    /// </summary>
    public string SignedOutCallbackPath { get; set; } = AppSurfaceKeycloakDefaults.SignedOutCallbackPath;

    /// <summary>
    /// Gets or sets the fixed local Keycloak host port.
    /// </summary>
    public int KeycloakPort { get; set; } = AppSurfaceKeycloakDefaults.KeycloakPort;

    /// <summary>
    /// Gets or sets the fixed local web proof port used to build redirect URIs.
    /// </summary>
    public int WebProofPort { get; set; } = AppSurfaceKeycloakDefaults.WebProofPort;

    /// <summary>
    /// Gets or sets a value indicating whether Keycloak data should persist in a container volume.
    /// </summary>
    /// <remarks>
    /// Disposable data is the default so realm import is deterministic. Persistent data keeps admin credentials and
    /// imported realm state until the volume is deleted.
    /// </remarks>
    public bool UsePersistentDataVolume { get; set; }

    /// <summary>
    /// Gets or sets the directory that receives generated Keycloak realm import JSON.
    /// </summary>
    public string RealmImportDirectory { get; set; } = CreateDefaultRealmImportDirectory();

    /// <summary>
    /// Gets mutable redirect URIs imported into the public OIDC client.
    /// </summary>
    public IList<Uri> RedirectUris { get; } = [];

    /// <summary>
    /// Gets mutable post-logout redirect URIs imported into the public OIDC client.
    /// </summary>
    public IList<Uri> PostLogoutRedirectUris { get; } = [];

    /// <summary>
    /// Gets mutable local-only users imported into the proof realm.
    /// </summary>
    public IList<AppSurfaceKeycloakUserOptions> SeededUsers { get; } =
    [
        CreateSeededUser("admin", "appsurface-admin-local-only", "appsurface-admin", "AppSurface Admin", "admin"),
        CreateSeededUser("viewer", "appsurface-viewer-local-only", "appsurface-viewer", "AppSurface Viewer", "viewer"),
    ];

    /// <summary>
    /// Validates all local Keycloak proof options and populates default redirect URIs when needed.
    /// </summary>
    public void Validate()
    {
        ValidateRealm();
        ValidateClient();
        ValidatePath(CallbackPath, nameof(CallbackPath));
        ValidatePath(SignedOutCallbackPath, nameof(SignedOutCallbackPath));
        ValidatePort(KeycloakPort, nameof(KeycloakPort));
        ValidatePort(WebProofPort, nameof(WebProofPort));

        if (string.IsNullOrWhiteSpace(RealmImportDirectory))
        {
            throw Invalid(nameof(RealmImportDirectory), "realm import directory cannot be blank.");
        }

        EnsureDefaultUris();
        ValidateUris(RedirectUris, CallbackPath, nameof(RedirectUris));
        ValidateUris(PostLogoutRedirectUris, SignedOutCallbackPath, nameof(PostLogoutRedirectUris));

        if (SeededUsers.Count == 0)
        {
            throw Invalid(nameof(SeededUsers), "at least one local proof user is required.");
        }

        var usernames = new HashSet<string>(StringComparer.Ordinal);
        var subjects = new HashSet<string>(StringComparer.Ordinal);
        foreach (var user in SeededUsers)
        {
            user.Validate();
            if (!usernames.Add(user.Username))
            {
                throw Invalid(nameof(SeededUsers), $"duplicate username '{user.Username}'.");
            }

            if (!subjects.Add(user.Subject))
            {
                throw Invalid(nameof(SeededUsers), $"duplicate subject '{user.Subject}'.");
            }
        }
    }

    /// <summary>
    /// Builds the secret-safe configuration projection for the paired web proof.
    /// </summary>
    /// <returns>An immutable projection containing only allowlisted OIDC configuration.</returns>
    public AppSurfaceKeycloakConfigurationProjection CreateConfigurationProjection()
    {
        Validate();
        return new AppSurfaceKeycloakConfigurationProjection(
            AppSurfaceKeycloakDefaults.Authority(Realm, KeycloakPort),
            ClientId,
            CallbackPath,
            SignedOutCallbackPath,
            requireClientSecret: false);
    }

    private void EnsureDefaultUris()
    {
        if (RedirectUris.Count == 0)
        {
            RedirectUris.Add(new Uri($"http://localhost:{WebProofPort}{CallbackPath}", UriKind.Absolute));
        }

        if (PostLogoutRedirectUris.Count == 0)
        {
            PostLogoutRedirectUris.Add(new Uri($"http://localhost:{WebProofPort}{SignedOutCallbackPath}", UriKind.Absolute));
        }
    }

    private void ValidateRealm()
    {
        if (!RealmPattern.IsMatch(Realm))
        {
            throw Invalid(nameof(Realm), "realm must match ^[a-z][a-z0-9-]{2,62}$.");
        }
    }

    private void ValidateClient()
    {
        if (!ClientPattern.IsMatch(ClientId))
        {
            throw Invalid(nameof(ClientId), "client id must match ^[a-z][a-z0-9._-]{2,63}$.");
        }

        if (string.IsNullOrWhiteSpace(ClientDisplayName))
        {
            throw Invalid(nameof(ClientDisplayName), "client display name cannot be blank.");
        }
    }

    private static void ValidatePath(string value, string optionName)
    {
        if (string.IsNullOrWhiteSpace(value) || value[0] != '/' || value.StartsWith("//", StringComparison.Ordinal)
            || value.Contains('\\', StringComparison.Ordinal) || value.Contains('%', StringComparison.Ordinal)
            || value.Contains('?', StringComparison.Ordinal) || value.Contains('#', StringComparison.Ordinal))
        {
            throw Invalid(optionName, "path must be app-relative, start with '/', and contain no query, fragment, backslash, or encoded characters.");
        }
    }

    private static void ValidatePort(int port, string optionName)
    {
        if (port is < 1 or > 65535)
        {
            throw Invalid(optionName, "port must be between 1 and 65535.");
        }
    }

    private static void ValidateUris(IEnumerable<Uri> uris, string expectedPath, string optionName)
    {
        foreach (var uri in uris)
        {
            if (!IsSafeLocalhostUri(uri, expectedPath))
            {
                throw Invalid(optionName, $"URI '{uri}' must be absolute localhost HTTP/HTTPS with path '{expectedPath}' and no query, fragment, user info, encoded slash, or encoded backslash.");
            }
        }
    }

    private static bool IsSafeLocalhostUri(Uri uri, string expectedPath) =>
        uri.IsAbsoluteUri
        && IsAllowedLocalhost(uri)
        && string.Equals(uri.AbsolutePath, expectedPath, StringComparison.Ordinal)
        && string.IsNullOrEmpty(uri.Query)
        && string.IsNullOrEmpty(uri.Fragment)
        && string.IsNullOrEmpty(uri.UserInfo)
        && !uri.OriginalString.Contains("%2f", StringComparison.OrdinalIgnoreCase)
        && !uri.OriginalString.Contains("%5c", StringComparison.OrdinalIgnoreCase);

    private static bool IsAllowedLocalhost(Uri uri) =>
        (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        && (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) || string.Equals(uri.Host, "127.0.0.1", StringComparison.Ordinal));

    private static string CreateDefaultRealmImportDirectory() =>
        AppSurfaceKeycloakRealmImportPaths.CreateDefaultDirectory();

    private static AppSurfaceKeycloakUserOptions CreateSeededUser(
        string username,
        string password,
        string subject,
        string displayName,
        string role)
    {
        var user = new AppSurfaceKeycloakUserOptions(username, password, subject, displayName);
        user.Claims["appsurface_role"] = role;
        return user;
    }

    private static AppSurfaceKeycloakException Invalid(string optionName, string detail) =>
        new(
            AppSurfaceKeycloakDiagnosticCodes.InvalidOptions,
            $"Problem: AppSurface Keycloak option {optionName} is invalid. Cause: {detail} Fix: use deterministic localhost proof values or override the matching option. Docs: Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak/README.md. Code: {AppSurfaceKeycloakDiagnosticCodes.InvalidOptions}.");
}
