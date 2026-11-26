using System.Linq;
using System.Text.RegularExpressions;
using System.Collections.Immutable;
using ForgeTrust.AppSurface.Auth.Keycloak;

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

    /// <summary>Gets or sets an explicit persistent volume name, preserving the named volume across runs.</summary>
    /// <remarks>A nonblank name selects persistent execution. The legacy boolean never authorizes replacing missing data.</remarks>
    public string? PersistentVolumeName { get; set; }

    /// <summary>Gets or sets the explicit supervised execution profile; null retains the existing local proof path.</summary>
    /// <remarks>Use the typed-credential overload and returned SupervisedRun helper. Host ProjectResource seeds are not admitted into this profile.</remarks>
    public AppSurfaceKeycloakSupervisedOptions? SupervisedExecution { get; set; }

    /// <summary>Gets or sets the OIDC client authentication mode. The default is a public client.</summary>
    /// <remarks>Confidential mode requires a separate typed client-secret binding; no secret belongs in these options.</remarks>
    public KeycloakClientMode ClientMode { get; set; }

    /// <summary>Gets or sets the exact HTTPS realm issuer. The default is the configured loopback Keycloak origin.</summary>
    /// <remarks>Public origins must also appear in <see cref="ApprovedHttpsOrigins"/>. Administrator transport stays local.</remarks>
    public Uri? Authority { get; set; }

    /// <summary>Gets the exact additional HTTPS origins approved for issuer, callback, logout and web-origin declarations.</summary>
    /// <remarks>The default is empty. Wildcards and nondefault public HTTPS ports are rejected.</remarks>
    public IList<Uri> ApprovedHttpsOrigins { get; } = [];

    /// <summary>Gets exact browser origins. An empty list derives distinct origins from the callback declarations.</summary>
    public IList<Uri> WebOrigins { get; } = [];

    /// <summary>Gets the generic claim mappers that must converge before readiness and application seeds.</summary>
    public IList<KeycloakClaimMapper> Mappers { get; } = [];

    /// <summary>Gets explicitly enabled Google provider declarations, without their credential values.</summary>
    /// <remarks>Omission preserves an existing provider. Removal requires <see cref="RemovedIdentityProviderAliases"/>.</remarks>
    public IList<KeycloakIdentityProviderConfiguration> IdentityProviders { get; } = [];

    /// <summary>Gets the explicitly managed mapper names to remove before readiness.</summary>
    public IList<string> RemovedMapperNames { get; } = [];

    /// <summary>Gets provider aliases to remove explicitly before readiness, including the consumer's disabled-provider policy.</summary>
    public IList<string> RemovedIdentityProviderAliases { get; } = [];

    /// <summary>Gets or sets whether an existing confidential client may explicitly become public. The default is false.</summary>
    /// <remarks>Default public configuration alone never permits a confidential-client downgrade.</remarks>
    public bool AllowPublicClientDowngrade { get; set; }

    /// <summary>Gets or sets whether a required application identity seed must complete before dependent web startup.</summary>
    /// <remarks>Set this before clearing <see cref="SeededUsers"/>. A required seed must actually be registered before execution.</remarks>
    public bool RequireIdentitySeed { get; set; }

    /// <summary>Gets additional explicitly local environment names permitted for Run registration.</summary>
    /// <remarks>Development, Test and Testing remain defaults; no environment entry enables Publish.</remarks>
    public ISet<string> AdditionalLocalEnvironmentNames { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Gets or sets the directory that receives generated Keycloak realm import JSON.
    /// </summary>
    public string RealmImportDirectory { get; set; } = CreateDefaultRealmImportDirectory();

    /// <summary>
    /// Gets or sets the optional application-owned login theme used by the local Keycloak AppHost proof.
    /// </summary>
    /// <remarks>
    /// When absent, Keycloak's existing behavior and the package's original five-minute proof remain unchanged.
    /// When present, the package validates and read-only mounts the theme only for local AppHost use; it does not
    /// publish an image or mutate a production realm.
    /// </remarks>
    public AppSurfaceKeycloakThemeOptions? LoginTheme { get; set; }

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
    public void Validate() => _ = ValidateCore(out _);

    private KeycloakDesiredConfiguration ValidateCore(out AppSurfaceKeycloakThemeRegistrationState? theme)
    {
        ValidateRealm();
        ValidateClient();
        ValidatePath(CallbackPath, nameof(CallbackPath));
        ValidatePath(SignedOutCallbackPath, nameof(SignedOutCallbackPath));
        ValidatePort(KeycloakPort, nameof(KeycloakPort));
        ValidatePort(WebProofPort, nameof(WebProofPort));
        theme = SupervisedExecution is null
            ? LoginTheme?.CreateRegistration(AppContext.BaseDirectory)
            : CreateSupervisedThemeRegistration(AppContext.BaseDirectory);

        if (string.IsNullOrWhiteSpace(RealmImportDirectory))
        {
            throw Invalid(nameof(RealmImportDirectory), "realm import directory cannot be blank.");
        }

        EnsureDefaultUris();
        if (ApprovedHttpsOrigins.Any(origin => origin is null))
            throw Invalid(nameof(ApprovedHttpsOrigins), "approved origins cannot contain null entries.");
        ValidateUris(RedirectUris, CallbackPath, nameof(RedirectUris));
        ValidateUris(PostLogoutRedirectUris, SignedOutCallbackPath, nameof(PostLogoutRedirectUris));

        if (!Enum.IsDefined(ClientMode)) throw Invalid(nameof(ClientMode), "client mode is unsupported.");
        if (PersistentVolumeName is not null && (string.IsNullOrWhiteSpace(PersistentVolumeName)
            || PersistentVolumeName.Length > 128 || PersistentVolumeName.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))))
            throw Invalid(nameof(PersistentVolumeName), "an explicit volume name must be nonblank and contain only supported name characters.");
        if (AdditionalLocalEnvironmentNames.Any(string.IsNullOrWhiteSpace))
            throw Invalid(nameof(AdditionalLocalEnvironmentNames), "local environment names cannot be blank.");

        if (SeededUsers.Count == 0 && !RequireIdentitySeed)
        {
            throw Invalid(nameof(SeededUsers), "at least one local proof user is required.");
        }

        var usernames = new HashSet<string>(StringComparer.Ordinal);
        var subjects = new HashSet<string>(StringComparer.Ordinal);
        foreach (var user in SeededUsers)
        {
            if (user is null) throw Invalid(nameof(SeededUsers), "local proof users cannot contain null entries.");
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

        try
        {
            var desired = CreateDesiredConfigurationCore();
            desired.Validate();
            return desired;
        }
        catch (ArgumentException) { throw Invalid("identity configuration", "the declared issuer, origins, mappers or providers are invalid."); }
    }

    /// <summary>
    /// Builds the secret-safe configuration projection for the paired web proof.
    /// </summary>
    /// <returns>An immutable projection containing only allowlisted OIDC configuration.</returns>
    public AppSurfaceKeycloakConfigurationProjection CreateConfigurationProjection()
    {
        Validate();
        return CreateProjection((Authority ?? new Uri(AppSurfaceKeycloakDefaults.Authority(Realm, KeycloakPort))).AbsoluteUri);
    }

    private AppSurfaceKeycloakConfigurationProjection CreateProjection(string authority) => new(
            authority,
            ClientId,
            CallbackPath,
            SignedOutCallbackPath,
            requireClientSecret: ClientMode == KeycloakClientMode.Confidential);

    /// <summary>Builds the validated immutable nonsecret declaration used by reconciliation and readiness.</summary>
    /// <returns>The same canonical realm/client/mapper/provider policy represented by these options.</returns>
    public KeycloakDesiredConfiguration CreateDesiredConfiguration() => ValidateCore(out _);

    private KeycloakDesiredConfiguration CreateDesiredConfigurationCore() => new(
        Realm, ClientId, Authority ?? new Uri(AppSurfaceKeycloakDefaults.Authority(Realm, KeycloakPort)), ClientMode,
        RedirectUris.ToImmutableArray(), PostLogoutRedirectUris.ToImmutableArray(),
        WebOrigins.Count == 0 ? RedirectUris.Select(uri => new Uri(uri.GetLeftPart(UriPartial.Authority))).Distinct().ToImmutableArray() : WebOrigins.ToImmutableArray(),
        Mappers.ToImmutableArray(), IdentityProviders.ToImmutableArray(), RemovedMapperNames.ToImmutableArray(), RemovedIdentityProviderAliases.ToImmutableArray())
    {
        AllowPublicDowngrade = AllowPublicClientDowngrade,
        TransportOrigin = new Uri($"https://localhost:{KeycloakPort}"),
        ApprovedHttpsOrigins = ApprovedHttpsOrigins.ToImmutableArray(),
        LoginThemeName = LoginTheme?.Name,
    };

    internal AppSurfaceKeycloakThemeRegistrationState? CreateSupervisedThemeRegistration(string sourceBaseDirectory)
    {
        var supervisedExecution = SupervisedExecution?.Snapshot()
            ?? throw Invalid(nameof(SupervisedExecution), "supervised execution options are required for supervised theme registration.");
        return LoginTheme?.CreateSupervisedRegistration(sourceBaseDirectory, supervisedExecution);
    }

    /// <summary>Copies consumer options and derives the registration projections and theme manifest in one validation pass.</summary>
    internal AppSurfaceKeycloakRegistrationSnapshot CreateRegistrationSnapshot()
    {
        var options = CopyOptions();
        var desired = options.ValidateCore(out var theme);
        return new(options, desired, options.CreateProjection(desired.Authority.AbsoluteUri), theme);
    }

    internal AppSurfaceKeycloakOptions CreateValidatedSnapshot() => CreateRegistrationSnapshot().Options;

    private AppSurfaceKeycloakOptions CopyOptions()
    {
        var snapshot = new AppSurfaceKeycloakOptions
        {
            Realm = Realm,
            ClientId = ClientId,
            ClientDisplayName = ClientDisplayName,
            CallbackPath = CallbackPath,
            SignedOutCallbackPath = SignedOutCallbackPath,
            KeycloakPort = KeycloakPort,
            WebProofPort = WebProofPort,
            UsePersistentDataVolume = UsePersistentDataVolume,
            PersistentVolumeName = PersistentVolumeName,
            SupervisedExecution = SupervisedExecution?.Snapshot(),
            ClientMode = ClientMode,
            Authority = Authority,
            AllowPublicClientDowngrade = AllowPublicClientDowngrade,
            RequireIdentitySeed = RequireIdentitySeed,
            RealmImportDirectory = RealmImportDirectory,
            LoginTheme = LoginTheme?.CreateSnapshot(),
        };

        foreach (var value in ApprovedHttpsOrigins) snapshot.ApprovedHttpsOrigins.Add(value);
        foreach (var value in WebOrigins) snapshot.WebOrigins.Add(value);
        foreach (var value in Mappers) snapshot.Mappers.Add(value);
        foreach (var value in IdentityProviders) snapshot.IdentityProviders.Add(value);
        foreach (var value in RemovedMapperNames) snapshot.RemovedMapperNames.Add(value);
        foreach (var value in RemovedIdentityProviderAliases) snapshot.RemovedIdentityProviderAliases.Add(value);
        foreach (var value in AdditionalLocalEnvironmentNames) snapshot.AdditionalLocalEnvironmentNames.Add(value);

        snapshot.RedirectUris.Clear();
        foreach (var redirectUri in RedirectUris)
        {
            snapshot.RedirectUris.Add(redirectUri);
        }

        snapshot.PostLogoutRedirectUris.Clear();
        foreach (var postLogoutRedirectUri in PostLogoutRedirectUris)
        {
            snapshot.PostLogoutRedirectUris.Add(postLogoutRedirectUri);
        }

        snapshot.SeededUsers.Clear();
        foreach (var user in SeededUsers)
        {
            snapshot.SeededUsers.Add(CreateUserSnapshot(user));
        }

        return snapshot;
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
        if (Realm is null || !RealmPattern.IsMatch(Realm))
        {
            throw Invalid(nameof(Realm), "realm must match ^[a-z][a-z0-9-]{2,62}$.");
        }
    }

    private void ValidateClient()
    {
        if (ClientId is null || !ClientPattern.IsMatch(ClientId))
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

    private void ValidateUris(IEnumerable<Uri> uris, string expectedPath, string optionName)
    {
        if (uris.Any(uri => !IsSafeLocalhostUri(uri, expectedPath)))
        {
            throw Invalid(optionName, $"URI must be absolute loopback HTTP/HTTPS or an explicitly approved HTTPS origin, with path '{expectedPath}' and no query, fragment, user info, encoded slash, or encoded backslash.");
        }
    }

    private bool IsSafeLocalhostUri(Uri? uri, string expectedPath) =>
        uri is not null && uri.IsAbsoluteUri
        && (IsAllowedLocalhost(uri) || uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort
            && ApprovedHttpsOrigins.Any(origin => origin.IsAbsoluteUri && origin.GetLeftPart(UriPartial.Authority) == uri.GetLeftPart(UriPartial.Authority)))
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
        AppSurfaceKeycloakRealmImportPaths.GetDefaultImportDirectory();

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

    private static AppSurfaceKeycloakUserOptions CreateUserSnapshot(AppSurfaceKeycloakUserOptions user)
    {
        if (user is null) throw Invalid(nameof(SeededUsers), "local proof users cannot contain null entries.");
        var snapshot = new AppSurfaceKeycloakUserOptions(user.Username, user.Password, user.Subject, user.DisplayName);
        foreach (var claim in user.Claims)
        {
            snapshot.Claims.Add(claim.Key, claim.Value);
        }

        return snapshot;
    }

    private static AppSurfaceKeycloakException Invalid(string optionName, string detail) =>
        new(
            AppSurfaceKeycloakDiagnosticCodes.InvalidOptions,
            $"Problem: AppSurface Keycloak option {optionName} is invalid. Cause: {detail} Fix: use deterministic localhost proof values or override the matching option. Docs: Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak/README.md. Code: {AppSurfaceKeycloakDiagnosticCodes.InvalidOptions}.");
}

/// <summary>Owns the copied options and the projections produced by a single registration validation.</summary>
/// <remarks>Consumers can edit the theme files after registration. The recorded manifest describes the registration-time source.</remarks>
internal sealed record AppSurfaceKeycloakRegistrationSnapshot(
    AppSurfaceKeycloakOptions Options,
    KeycloakDesiredConfiguration Desired,
    AppSurfaceKeycloakConfigurationProjection Projection,
    AppSurfaceKeycloakThemeRegistrationState? Theme);
