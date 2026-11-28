using System.Collections.Immutable;
using System.Linq;

namespace ForgeTrust.AppSurface.Auth.Keycloak;

/// <summary>Describes whether a Keycloak application client is public or confidential.</summary>
/// <remarks>
/// Public clients are appropriate for browser-based authorization-code flows with PKCE. Confidential clients require
/// a separately supplied secret at execution time; this enum and the identity declaration never contain that secret.
/// Selecting a mode does not itself authorize a realm mutation.
/// </remarks>
public enum KeycloakClientMode
{
    /// <summary>Uses a browser-facing client that authenticates without a client secret.</summary>
    Public = 0,

    /// <summary>Uses a confidential client whose secret is supplied through a separate protected binding.</summary>
    Confidential = 1,
}

/// <summary>Declares one generic claim mapper managed by an AppSurface Keycloak identity configuration.</summary>
/// <param name="Name">The stable, unique mapper name: 1–128 trimmed ASCII letters, digits, spaces, hyphens, underscores, or periods.</param>
/// <param name="Claim">The required token-claim identifier: 1–128 ASCII letters, digits, hyphens, underscores, or periods.</param>
/// <param name="Value">The fixed mapper value, at most 256 characters; defaults to the empty string. Do not put secrets here.</param>
/// <param name="UserAttribute">Optional 1–128 character identifier for a user-attribute source. It cannot be combined with <paramref name="SessionNote"/>.</param>
/// <param name="SessionNote">Optional 1–128 character identifier for a session-note source. It cannot be combined with <paramref name="UserAttribute"/>.</param>
/// <remarks>
/// Mapper declarations own only the named generic mapper. They do not define an application account schema or authorize
/// writes to users, credentials, or other unmanaged realm state.
/// </remarks>
public sealed record KeycloakClaimMapper(
    string Name,
    string Claim,
    string Value = "",
    string? UserAttribute = null,
    string? SessionNote = null);

/// <summary>Declares a Google OpenID Connect identity provider to enable for a realm.</summary>
/// <param name="Alias">The stable, unique provider identifier: 1–128 ASCII letters, digits, hyphens, underscores, or periods.</param>
/// <param name="ClientId">The non-secret Google OAuth client identifier: 1–512 characters, not all whitespace and containing no control characters.</param>
/// <param name="DisplayName">
/// Optional, nonblank provider label, defaulting to <see langword="null"/> and limited to 1–128 non-control characters
/// when supplied; credentials must never be placed here.
/// </param>
/// <remarks>
/// Google is the only provider supported by this contract. The implementation supplies the fixed
/// <c>openid profile email</c> default scope and <c>IMPORT</c> user-sync mode internally. Provider credentials are
/// supplied separately by the local adapter. Enabling this declaration does not prove Google accepted the credentials
/// or grant access to Google APIs.
/// </remarks>
public sealed record KeycloakIdentityProviderConfiguration(
    string Alias,
    string ClientId,
    string? DisplayName = null);

/// <summary>
/// Holds an immutable, non-secret declaration of the generic Keycloak identity state for one local AppHost run.
/// </summary>
/// <param name="Realm">The realm identifier: 1–128 ASCII letters, digits, hyphens, underscores, or periods.</param>
/// <param name="ClientId">The application-client identifier: 1–128 ASCII letters, digits, hyphens, underscores, or periods.</param>
/// <param name="Authority">The exact OIDC issuer URI, including the <c>/realms/{Realm}</c> path.</param>
/// <param name="ClientMode">The required public or confidential client mode.</param>
/// <param name="RedirectUris">Unique authorization callback URIs; at least one is required and at most 32 are accepted.</param>
/// <param name="PostLogoutRedirectUris">Unique post-logout callback URIs; at least one is required and at most 32 are accepted.</param>
/// <param name="WebOrigins">Unique exact browser origins; at most 32 are accepted. Wildcard origins are rejected.</param>
/// <param name="Mappers">Unique managed claim mappers; at most 128 are accepted.</param>
/// <param name="IdentityProviders">Unique Google identity providers; at most 16 are accepted.</param>
/// <param name="RemovedMapperNames">Explicit unique mapper-name removals, at most 128, disjoint from <paramref name="Mappers"/>.</param>
/// <param name="RemovedIdentityProviderAliases">Explicit unique provider-identifier removals, at most 16, disjoint from <paramref name="IdentityProviders"/>.</param>
/// <remarks>
/// This value is a desired-state contract, not an administrator API or production controller. Its immutable arrays,
/// immutable URI values, and init-only properties make each instance a stable snapshot; use a record copy to describe a
/// later run. URI values retain their original strings during projection. The complete declaration is limited to 1 MiB
/// when serialized by the reconciliation validator.
/// </remarks>
public sealed record KeycloakDesiredConfiguration(
    string Realm,
    string ClientId,
    Uri Authority,
    KeycloakClientMode ClientMode,
    ImmutableArray<Uri> RedirectUris,
    ImmutableArray<Uri> PostLogoutRedirectUris,
    ImmutableArray<Uri> WebOrigins,
    ImmutableArray<KeycloakClaimMapper> Mappers,
    ImmutableArray<KeycloakIdentityProviderConfiguration> IdentityProviders,
    ImmutableArray<string> RemovedMapperNames,
    ImmutableArray<string> RemovedIdentityProviderAliases)
{
    /// <summary>
    /// Gets or initializes whether this run explicitly permits changing an existing confidential client to public.
    /// </summary>
    /// <remarks>
    /// The default is <see langword="false"/>. Set this only when <see cref="ClientMode"/> is
    /// <see cref="KeycloakClientMode.Public"/> and intentionally downgrading the existing client; a new public client
    /// needs no downgrade permission. Dormant server-side secret material is not thereby erased.
    /// </remarks>
    public bool AllowPublicDowngrade { get; init; }

    /// <summary>
    /// Gets or initializes the optional local HTTPS loopback origin used for Keycloak administrative transport; defaults to <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// When absent, transport is derived from the authority origin. A remote authority therefore needs this explicit
    /// loopback transport for the local worker; the transport origin is not the public issuer and is not an approved
    /// callback origin.
    /// </remarks>
    public Uri? TransportOrigin { get; init; }

    /// <summary>
    /// Gets or initializes exact approved external HTTPS origins for the issuer and browser callbacks.
    /// </summary>
    /// <remarks>
    /// The default is empty. At most 32 origins are accepted; each must be a non-loopback HTTPS origin on port 443 with
    /// no wildcard, path, query, fragment, or user information. Approval is exact-origin matching, not a provider or
    /// production trust decision.
    /// </remarks>
    public ImmutableArray<Uri> ApprovedHttpsOrigins { get; init; } = [];

    /// <summary>Gets or initializes the optional Keycloak login-theme identifier; defaults to <see langword="null"/>.</summary>
    /// <remarks>
    /// When supplied, the nonblank name contains 1–128 ASCII letters, digits, hyphens, underscores, or periods. The local
    /// adapter owns related theme-file mounting and custody.
    /// </remarks>
    public string? LoginThemeName { get; init; }

    /// <summary>Validates this non-secret declaration without contacting Keycloak or resolving credentials.</summary>
    /// <exception cref="ArgumentException">The declaration is incomplete, unsupported, ambiguous, or exceeds a supported bound.</exception>
    /// <remarks>
    /// Validation delegates semantic checks to the same finite validator used by the reconciliation core. Failures use
    /// a generic message and do not echo declaration values. This method proves only that the declaration is locally
    /// well-formed; it does not prove provider acceptance, successful login, or that a later reconciliation will succeed.
    /// </remarks>
    public void Validate() => _ = ToReconcileDesired();

    /// <summary>Creates the internal non-secret representation after canonical validation.</summary>
    /// <returns>The validated declaration consumed by the internal reconciliation core.</returns>
    internal RealmReconcileDesired ToReconcileDesired()
    {
        if (!Enum.IsDefined(ClientMode)
            || Authority is null
            || !HasNoNullElements(RedirectUris)
            || !HasNoNullElements(PostLogoutRedirectUris)
            || !HasNoNullElements(WebOrigins)
            || !HasNoNullElements(Mappers)
            || !HasNoNullElements(IdentityProviders)
            || !HasNoNullElements(ApprovedHttpsOrigins)
            || RemovedMapperNames.IsDefault
            || RemovedIdentityProviderAliases.IsDefault)
        {
            throw InvalidConfiguration();
        }

        var desired = new RealmReconcileDesired(
            Realm,
            ClientId,
            Authority.OriginalString,
            ClientMode == KeycloakClientMode.Confidential,
            AllowPublicDowngrade,
            RedirectUris.Select(static uri => uri.OriginalString).ToImmutableArray(),
            PostLogoutRedirectUris.Select(static uri => uri.OriginalString).ToImmutableArray(),
            WebOrigins.Select(static uri => uri.OriginalString).ToImmutableArray(),
            Mappers.Select(static mapper => new RealmReconcileMapper(
                mapper.Name,
                mapper.Claim,
                mapper.Value,
                mapper.UserAttribute,
                mapper.SessionNote)).ToImmutableArray(),
            IdentityProviders.Select(static provider => new RealmReconcileBroker(
                provider.Alias,
                Enabled: true,
                ProviderId: "google",
                ClientId: provider.ClientId)
            {
                DisplayName = provider.DisplayName,
            }).ToImmutableArray(),
            RemovedMapperNames,
            RemovedIdentityProviderAliases)
        {
            TransportOrigin = TransportOrigin?.OriginalString,
            ApprovedHttpsOrigins = ApprovedHttpsOrigins.Select(static uri => uri.OriginalString).ToImmutableArray(),
            LoginThemeName = LoginThemeName,
        };

        try
        {
            RealmReconciler.ValidateDesired(desired);
        }
        catch (ArgumentException)
        {
            throw InvalidConfiguration();
        }
        return desired;
    }

    private static bool HasNoNullElements<T>(ImmutableArray<T> values)
        where T : class => !values.IsDefault && values.All(static value => value is not null);

    private static ArgumentException InvalidConfiguration() =>
        new("The Keycloak identity configuration is invalid.", "configuration");
}
