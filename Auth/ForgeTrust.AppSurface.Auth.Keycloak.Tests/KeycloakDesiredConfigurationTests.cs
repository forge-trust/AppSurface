using System.Collections.Immutable;
using ForgeTrust.AppSurface.Auth.Keycloak;

namespace ForgeTrust.AppSurface.Auth.Keycloak.Tests;

// Test card: protects the public, non-secret immutable identity declaration and its canonical validation boundary.
// fails_when: incomplete/default collections, unsupported values, ambiguous declarations, or widened limits are accepted.
// seam: public Validate plus the intentionally exposed internal ToReconcileDesired mapping; no reflection or transport.
// no_claim: these tests do not prove Keycloak mutation, browser login, external-provider acceptance, or publication.
public sealed class KeycloakDesiredConfigurationTests
{
    // protects: the default public-client shape stays valid and preserves its exact URI declarations.
    // fails_when: ordinary loopback callbacks or the explicit Public mode are rejected or silently rewritten.
    // seam: public Validate and internal non-secret reconciliation projection.
    // no_claim: no Keycloak server or user login is exercised.
    [Fact]
    public void PublicConfigurationValidatesAndMapsWithoutChangingItsSnapshot()
    {
        var configuration = Valid();

        configuration.Validate();
        var desired = configuration.ToReconcileDesired();

        Assert.False(desired.Confidential);
        Assert.Equal(configuration.Authority.OriginalString, desired.Authority);
        Assert.Equal(configuration.RedirectUris.Select(uri => uri.OriginalString), desired.RedirectUris);
        Assert.Equal(configuration.PostLogoutRedirectUris.Select(uri => uri.OriginalString), desired.PostLogoutRedirectUris);
        Assert.Empty(desired.Brokers);
        Assert.False(desired.AllowPublicDowngrade);
        Assert.Null(desired.TransportOrigin);
        Assert.Empty(desired.ApprovedHttpsOrigins);
        Assert.Null(desired.LoginThemeName);
    }

    // protects: confidential mode, exact external origins, Google display name, mapper sources, removals, and init defaults map faithfully.
    // fails_when: mapping drops a declared non-secret setting or changes Google into an arbitrary provider kind.
    // seam: internal projection into RealmReconcileDesired and its canonical validator.
    // no_claim: mapping does not prove secret custody, Google acceptance, or a successful authorization-code exchange.
    [Fact]
    public void ConfidentialConfigurationMapsAllSupportedDeclarations()
    {
        var configuration = new KeycloakDesiredConfiguration(
            "sample",
            "web-client",
            new Uri("https://login.example.test/realms/sample"),
            KeycloakClientMode.Confidential,
            [new Uri("https://app.example.test/signin-oidc")],
            [new Uri("https://app.example.test/signout-callback-oidc")],
            [new Uri("https://app.example.test/")],
            [new KeycloakClaimMapper("display-name", "name", "", UserAttribute: "display_name")],
            [new KeycloakIdentityProviderConfiguration("google", "google-client", "Continue with Google")],
            ["legacy-mapper"],
            ["retired-provider"])
        {
            AllowPublicDowngrade = true,
            TransportOrigin = new Uri("https://localhost:8443/"),
            ApprovedHttpsOrigins = [new Uri("https://login.example.test/"), new Uri("https://app.example.test/")],
            LoginThemeName = "sample-theme",
        };

        configuration.Validate();
        var desired = configuration.ToReconcileDesired();

        Assert.True(desired.Confidential);
        Assert.True(desired.AllowPublicDowngrade);
        Assert.Equal("https://localhost:8443/", desired.TransportOrigin);
        Assert.Equal<string>(["https://login.example.test/", "https://app.example.test/"], desired.ApprovedHttpsOrigins);
        Assert.Equal("sample-theme", desired.LoginThemeName);
        Assert.Equal(new RealmReconcileMapper("display-name", "name", "", "display_name"), Assert.Single(desired.Mappers));
        var broker = Assert.Single(desired.Brokers);
        Assert.Equal("google", broker.Alias);
        Assert.True(broker.Enabled);
        Assert.Equal("google", broker.ProviderId);
        Assert.Equal("google-client", broker.ClientId);
        Assert.Equal("Continue with Google", broker.DisplayName);
        Assert.Equal<string>(["legacy-mapper"], desired.RemoveMappers);
        Assert.Equal<string>(["retired-provider"], desired.RemoveBrokers);
    }

    // protects: unknown enum values fail safely before reconciliation projection.
    // fails_when: an undefined numeric enum value is interpreted as either supported client mode.
    // seam: public Validate performs its effect-free shape guard.
    // no_claim: this does not test a provider or server response.
    [Fact]
    public void UnknownClientModeFailsWithGenericDiagnostic()
    {
        var configuration = Valid() with { ClientMode = (KeycloakClientMode)42 };

        var failure = Assert.Throws<ArgumentException>(configuration.Validate);

        Assert.Equal("configuration", failure.ParamName);
        Assert.DoesNotContain("42", failure.Message, StringComparison.Ordinal);
    }

    // protects: default ImmutableArray values are rejected before any conversion or enumeration.
    // fails_when: an uninitialized array is treated as an intentionally empty declaration.
    // seam: public Validate shape guards followed by the existing canonical validator.
    // no_claim: this does not establish runtime package behavior.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void DefaultImmutableArraysFailSafely(int collection)
    {
        var configuration = WithDefaultCollection(Valid(), collection);

        Assert.Throws<ArgumentException>(configuration.Validate);
    }

    // protects: null values smuggled through runtime callers cannot cause incidental NullReferenceException failures.
    // fails_when: a null URI, mapper, provider, identifier, or authority reaches projection code.
    // seam: public Validate shape guards before internal mapping.
    // no_claim: this does not prove that any remote endpoint is reachable.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void NullReferenceMembersFailAsSafeArgumentErrors(int member)
    {
        var configuration = WithNullMember(Valid(), member);

        var failure = Assert.Throws<ArgumentException>(configuration.Validate);

        Assert.Equal("configuration", failure.ParamName);
    }

    // protects: all field-level semantics continue through the shared finite validator.
    // fails_when: invalid issuer paths, unapproved origins, conflicting mapper sources, duplicate aliases, or simultaneous upsert/removal pass.
    // seam: public Validate -> internal ToReconcileDesired -> RealmReconciler.ValidateDesired; validation performs no HTTP request.
    // no_claim: these cases do not certify provider semantics on a pinned Keycloak image.
    [Theory]
    [InlineData("realm")]
    [InlineData("authority")]
    [InlineData("remote-callback")]
    [InlineData("remote-origin")]
    [InlineData("mapper-sources")]
    [InlineData("mapper-removal")]
    [InlineData("duplicate-provider")]
    [InlineData("duplicate-callback")]
    [InlineData("provider-display-empty")]
    [InlineData("provider-display-whitespace")]
    [InlineData("provider-display-too-long")]
    [InlineData("provider-display-control")]
    public void InvalidIdentityDeclarationsFailCanonicalValidation(string kind)
    {
        var configuration = InvalidVariant(kind);

        var failure = Assert.Throws<ArgumentException>(configuration.Validate);

        Assert.DoesNotContain("credential-marker", failure.Message, StringComparison.Ordinal);
    }

    // protects: hard count bounds in the canonical validator cannot be widened by the public adapter contract.
    // fails_when: callbacks, origins, mappers, providers, or explicit removals exceed their declared caps.
    // seam: public Validate delegates to the shared core validator without contacting Keycloak.
    // no_claim: bounded input does not establish that the worker payload is packaged or published.
    [Theory]
    [InlineData("redirects")]
    [InlineData("post-logout")]
    [InlineData("web-origins")]
    [InlineData("approved-origins")]
    [InlineData("mappers")]
    [InlineData("providers")]
    [InlineData("removed-mappers")]
    [InlineData("removed-providers")]
    public void DeclarationsOverSupportedCountBoundsAreRejected(string kind)
    {
        var configuration = OverLimitVariant(kind);

        Assert.Throws<ArgumentException>(configuration.Validate);
    }

    // protects: each documented collection ceiling remains accepted at its exact maximum.
    // fails_when: a supported boundary is accidentally made exclusive or a valid maximum-size declaration is rejected.
    // seam: public Validate and the shared finite validator over an in-memory declaration only.
    // no_claim: maximum declaration size does not prove worker payload or provider behavior.
    [Fact]
    public void EveryCollectionAcceptsItsExactSupportedCeiling()
    {
        var configuration = Valid() with
        {
            RedirectUris = Enumerable.Range(0, 32)
                .Select(index => new Uri("http://localhost:5000/callback/" + index))
                .ToImmutableArray(),
            PostLogoutRedirectUris = Enumerable.Range(0, 32)
                .Select(index => new Uri("http://localhost:5000/logout/" + index))
                .ToImmutableArray(),
            WebOrigins = Enumerable.Range(0, 32)
                .Select(index => new Uri("http://localhost:" + (5000 + index) + "/"))
                .ToImmutableArray(),
            ApprovedHttpsOrigins = Enumerable.Range(0, 32)
                .Select(index => new Uri("https://app" + index + ".example.test/"))
                .ToImmutableArray(),
            Mappers = Enumerable.Range(0, 128)
                .Select(index => new KeycloakClaimMapper("mapper-" + index, "claim"))
                .ToImmutableArray(),
            IdentityProviders = Enumerable.Range(0, 16)
                .Select(index => new KeycloakIdentityProviderConfiguration("google-" + index, "google-client"))
                .ToImmutableArray(),
            RemovedMapperNames = Enumerable.Range(0, 128).Select(index => "remove-mapper-" + index).ToImmutableArray(),
            RemovedIdentityProviderAliases = Enumerable.Range(0, 16).Select(index => "remove-provider-" + index).ToImmutableArray(),
        };

        configuration.Validate();
    }

    // protects: diagnostics never echo untrusted declaration text that could contain credential-like material.
    // fails_when: a rejected URI or identifier appears in the public validation error.
    // seam: the public Validate exception from the canonical validator.
    // no_claim: this does not audit process logs, dashboards, or worker exceptions.
    [Fact]
    public void InvalidDeclarationDiagnosticDoesNotEchoInput()
    {
        const string marker = "credential-marker";
        var configuration = Valid() with
        {
            Authority = new Uri("https://localhost:8443/realms/sample?value=" + marker),
        };

        var failure = Assert.Throws<ArgumentException>(configuration.Validate);

        Assert.DoesNotContain(marker, failure.Message, StringComparison.Ordinal);
    }

    // protects: record copies describe a new immutable snapshot without changing the original contract.
    // fails_when: changing a copied declaration mutates the earlier instance.
    // seam: C# record with-copy and ImmutableArray values on the public type.
    // no_claim: this does not prove storage or cross-process snapshot consistency.
    [Fact]
    public void WithCopyLeavesOriginalDeclarationUnchanged()
    {
        var original = Valid();
        var changed = original with { ClientId = "replacement-client" };

        Assert.Equal("web-client", original.ClientId);
        Assert.Equal("replacement-client", changed.ClientId);
        Assert.Equal(original.RedirectUris, changed.RedirectUris);
    }

    private static KeycloakDesiredConfiguration Valid() => new(
        "sample",
        "web-client",
        new Uri("https://localhost:8443/realms/sample"),
        KeycloakClientMode.Public,
        [new Uri("http://localhost:5000/signin-oidc")],
        [new Uri("http://localhost:5000/signout-callback-oidc")],
        [new Uri("http://localhost:5000/")],
        [],
        [],
        [],
        []);

    private static KeycloakDesiredConfiguration WithDefaultCollection(KeycloakDesiredConfiguration value, int collection) => collection switch
    {
        0 => value with { RedirectUris = default },
        1 => value with { PostLogoutRedirectUris = default },
        2 => value with { WebOrigins = default },
        3 => value with { Mappers = default },
        4 => value with { IdentityProviders = default },
        5 => value with { RemovedMapperNames = default },
        6 => value with { RemovedIdentityProviderAliases = default },
        7 => value with { ApprovedHttpsOrigins = default },
        _ => throw new ArgumentOutOfRangeException(nameof(collection)),
    };

    private static KeycloakDesiredConfiguration WithNullMember(KeycloakDesiredConfiguration value, int member) => member switch
    {
        0 => value with { RedirectUris = [null!] },
        1 => value with { Authority = null! },
        2 => value with { Mappers = [null!] },
        3 => value with { IdentityProviders = [null!] },
        4 => value with { Realm = null! },
        5 => value with { ClientId = null! },
        6 => value with { Mappers = [new KeycloakClaimMapper(null!, null!, null!)] },
        7 => value with { IdentityProviders = [new KeycloakIdentityProviderConfiguration(null!, null!)] },
        _ => throw new ArgumentOutOfRangeException(nameof(member)),
    };

    private static KeycloakDesiredConfiguration InvalidVariant(string kind) => kind switch
    {
        "realm" => Valid() with { Realm = "bad realm" },
        "authority" => Valid() with { Authority = new Uri("https://localhost:8443/other/sample") },
        "remote-callback" => Valid() with { RedirectUris = [new Uri("https://app.example.test/signin-oidc")] },
        "remote-origin" => Valid() with { WebOrigins = [new Uri("https://app.example.test/")] },
        "mapper-sources" => Valid() with
        {
            Mappers = [new KeycloakClaimMapper("department", "department", UserAttribute: "department", SessionNote: "department")],
        },
        "mapper-removal" => Valid() with
        {
            Mappers = [new KeycloakClaimMapper("department", "department")],
            RemovedMapperNames = ["department"],
        },
        "duplicate-provider" => Valid() with
        {
            IdentityProviders =
            [
                new KeycloakIdentityProviderConfiguration("google", "google-client"),
                new KeycloakIdentityProviderConfiguration("google", "second-client"),
            ],
        },
        "duplicate-callback" => Valid() with
        {
            RedirectUris = [new Uri("http://localhost:5000/signin-oidc"), new Uri("http://localhost:5000/signin-oidc")],
        },
        "provider-display-empty" => Valid() with
        {
            IdentityProviders = [new KeycloakIdentityProviderConfiguration("google", "google-client", "")],
        },
        "provider-display-whitespace" => Valid() with
        {
            IdentityProviders = [new KeycloakIdentityProviderConfiguration("google", "google-client", "  ")],
        },
        "provider-display-too-long" => Valid() with
        {
            IdentityProviders = [new KeycloakIdentityProviderConfiguration("google", "google-client", new string('x', 129))],
        },
        "provider-display-control" => Valid() with
        {
            IdentityProviders = [new KeycloakIdentityProviderConfiguration("google", "google-client", "g" + (char)1)],
        },
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static KeycloakDesiredConfiguration OverLimitVariant(string kind) => kind switch
    {
        "redirects" => Valid() with
        {
            RedirectUris = Enumerable.Range(0, 33)
                .Select(index => new Uri("http://localhost:5000/callback/" + index))
                .ToImmutableArray(),
        },
        "post-logout" => Valid() with
        {
            PostLogoutRedirectUris = Enumerable.Range(0, 33)
                .Select(index => new Uri("http://localhost:5000/logout/" + index))
                .ToImmutableArray(),
        },
        "web-origins" => Valid() with
        {
            WebOrigins = Enumerable.Range(0, 33)
                .Select(index => new Uri("http://localhost:" + (5000 + index) + "/"))
                .ToImmutableArray(),
        },
        "approved-origins" => Valid() with
        {
            ApprovedHttpsOrigins = Enumerable.Range(0, 33)
                .Select(index => new Uri("https://app" + index + ".example.test/"))
                .ToImmutableArray(),
        },
        "mappers" => Valid() with
        {
            Mappers = Enumerable.Range(0, 129)
                .Select(index => new KeycloakClaimMapper("mapper-" + index, "claim"))
                .ToImmutableArray(),
        },
        "providers" => Valid() with
        {
            IdentityProviders = Enumerable.Range(0, 17)
                .Select(index => new KeycloakIdentityProviderConfiguration("google-" + index, "google-client"))
                .ToImmutableArray(),
        },
        "removed-mappers" => Valid() with
        {
            RemovedMapperNames = Enumerable.Range(0, 129).Select(index => "mapper-" + index).ToImmutableArray(),
        },
        "removed-providers" => Valid() with
        {
            RemovedIdentityProviderAliases = Enumerable.Range(0, 17).Select(index => "provider-" + index).ToImmutableArray(),
        },
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
