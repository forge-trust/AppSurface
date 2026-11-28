using System.Collections.Immutable;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using ForgeTrust.AppSurface.Auth.Keycloak;

namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak;

/// <summary>Declares typed execution credentials separately from nonsecret local Keycloak identity options.</summary>
/// <remarks>
/// Registration snapshots parameter identities without resolving values. Parameters must belong to the same AppHost;
/// client/provider parameters must be secret and distinct from administrator parameters. Values are resolved only
/// after the local run owner is held. This object is not a credential store or a web configuration projection.
/// </remarks>
public sealed class AppSurfaceKeycloakCredentialBindings
{
    /// <summary>Gets or sets the one required confidential-client secret parameter. Public mode leaves it absent.</summary>
    public IResourceBuilder<ParameterResource>? ClientSecret { get; set; }

    /// <summary>Gets provider alias to typed secret bindings for exactly the enabled Google declarations.</summary>
    /// <remarks>Omitted or removed providers have no binding. Plain strings, environment fallbacks and cross-AppHost parameters are rejected.</remarks>
    public IDictionary<string, IResourceBuilder<ParameterResource>> IdentityProviderSecrets { get; } =
        new Dictionary<string, IResourceBuilder<ParameterResource>>(StringComparer.Ordinal);

    internal AppSurfaceKeycloakCredentialSnapshot CreateSnapshot(IDistributedApplicationBuilder builder,
        KeycloakDesiredConfiguration desired, IResourceBuilder<ParameterResource> username, IResourceBuilder<ParameterResource> password)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(username);
        ArgumentNullException.ThrowIfNull(password);
        if (!ReferenceEquals(username.ApplicationBuilder, builder) || !ReferenceEquals(password.ApplicationBuilder, builder)
            || !password.Resource.Secret || ReferenceEquals(username.Resource, password.Resource)
            || (desired.ClientMode == KeycloakClientMode.Confidential) != (ClientSecret is not null)) throw Invalid();
        var providers = IdentityProviderSecrets.ToImmutableDictionary(StringComparer.Ordinal);
        var aliases = desired.IdentityProviders.Select(provider => provider.Alias).ToHashSet(StringComparer.Ordinal);
        if (!aliases.SetEquals(providers.Keys)) throw Invalid();
        if (ClientSecret is not null && !Valid(ClientSecret)) throw Invalid();
        if (providers.Values.Any(parameter => parameter is null || !Valid(parameter)
            || ReferenceEquals(parameter.Resource, ClientSecret?.Resource))) throw Invalid();
        return new(username, password, ClientSecret, providers);

        bool Valid(IResourceBuilder<ParameterResource> parameter) => parameter.Resource.Secret
            && ReferenceEquals(parameter.ApplicationBuilder, builder)
            && !ReferenceEquals(parameter.Resource, username.Resource) && !ReferenceEquals(parameter.Resource, password.Resource);
    }

    internal static AppSurfaceKeycloakException Invalid() => new(AppSurfaceKeycloakDiagnosticCodes.InvalidCredentialBinding,
        "Problem: local Keycloak credential bindings are invalid. Fix: use separate same-AppHost typed secret parameters for the declared client and enabled providers; keep values out of identity options. Docs: Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak/README.md#identity-credential-bindings. Code: ASKEYC027.");
}

/// <summary>Holds the validated parameter references for use after the run acquires ownership.</summary>
internal sealed record AppSurfaceKeycloakCredentialSnapshot(
    IResourceBuilder<ParameterResource> AdminUsername,
    IResourceBuilder<ParameterResource> AdminPassword,
    IResourceBuilder<ParameterResource>? ClientSecret,
    ImmutableDictionary<string, IResourceBuilder<ParameterResource>> IdentityProviderSecrets);
