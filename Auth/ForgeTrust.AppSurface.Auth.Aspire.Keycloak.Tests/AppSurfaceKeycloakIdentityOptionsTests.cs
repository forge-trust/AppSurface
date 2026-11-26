using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using ForgeTrust.AppSurface.Auth.Aspire.Keycloak;
using ForgeTrust.AppSurface.Auth.Keycloak;

namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests;

public sealed class AppSurfaceKeycloakIdentityOptionsTests
{
    [Fact]
    public void CreateDesiredConfiguration_CopiesIdentityDeclarationAndExactApprovedHttpsOrigins()
    {
        var options = CreateApprovedHttpsOptions();
        options.ClientMode = KeycloakClientMode.Public;
        options.AllowPublicClientDowngrade = true;
        options.Mappers.Add(new KeycloakClaimMapper("appsurface-role", "appsurface_role", "founder"));
        options.IdentityProviders.Add(new KeycloakIdentityProviderConfiguration("google", "synthetic-google-client", "Google"));
        options.RemovedMapperNames.Add("retired-mapper");
        options.RemovedIdentityProviderAliases.Add("retired-provider");

        var desired = options.CreateDesiredConfiguration();

        Assert.Equal(options.Realm, desired.Realm);
        Assert.Equal(options.ClientId, desired.ClientId);
        Assert.Equal(options.Authority, desired.Authority);
        Assert.Equal(KeycloakClientMode.Public, desired.ClientMode);
        Assert.True(desired.AllowPublicDowngrade);
        Assert.Equal(options.RedirectUris, desired.RedirectUris);
        Assert.Equal(options.PostLogoutRedirectUris, desired.PostLogoutRedirectUris);
        Assert.Equal(options.WebOrigins, desired.WebOrigins);
        Assert.Equal(options.ApprovedHttpsOrigins, desired.ApprovedHttpsOrigins);
        Assert.Equal(options.Mappers, desired.Mappers);
        Assert.Equal(options.IdentityProviders, desired.IdentityProviders);
        Assert.Equal(options.RemovedMapperNames, desired.RemovedMapperNames);
        Assert.Equal(options.RemovedIdentityProviderAliases, desired.RemovedIdentityProviderAliases);
        Assert.Equal(new Uri("https://localhost:8080"), desired.TransportOrigin);
    }

    [Fact]
    public void CreateDesiredConfiguration_AcceptsExactApprovedHttpsIssuerAndCallbacks()
    {
        var options = CreateApprovedHttpsOptions();

        var desired = options.CreateDesiredConfiguration();

        Assert.Equal(new Uri("https://identity.example.test/realms/appsurface-test"), desired.Authority);
        Assert.Equal(new Uri("https://app.example.test/signin-appsurface-oidc"), Assert.Single(desired.RedirectUris));
        Assert.Equal(new Uri("https://app.example.test/signout-callback-appsurface-oidc"), Assert.Single(desired.PostLogoutRedirectUris));
    }

    [Theory]
    [InlineData("https://example.test", "https://web.example.test")]
    [InlineData("https://app.example.test", "https://unknown.example.test")]
    [InlineData("app.example.test", "https://app.example.test")]
    public void CreateDesiredConfiguration_RejectsUnapprovedOrRelativeCallbackOrigins(string approvedOrigin, string callbackOrigin)
    {
        var options = CreateApprovedHttpsOptions();
        options.ApprovedHttpsOrigins.RemoveAt(1);
        options.ApprovedHttpsOrigins.Add(new Uri(approvedOrigin, UriKind.RelativeOrAbsolute));
        options.RedirectUris.Clear();
        options.RedirectUris.Add(new Uri($"{callbackOrigin}/signin-appsurface-oidc"));

        var exception = AssertInvalidOptions(() => options.CreateDesiredConfiguration());

        Assert.Contains(nameof(AppSurfaceKeycloakOptions.RedirectUris), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateDesiredConfiguration_RejectsUndefinedClientModeWithSafeDiagnostic()
    {
        var options = new AppSurfaceKeycloakOptions { ClientMode = (KeycloakClientMode)12345 };

        AssertInvalidOptions(() => options.CreateDesiredConfiguration());
    }

    [Fact]
    public void CreateConfigurationProjection_ConfidentialModeRequiresAClientSecret()
    {
        var options = new AppSurfaceKeycloakOptions { ClientMode = KeycloakClientMode.Confidential };

        var projection = options.CreateConfigurationProjection();

        Assert.True(projection.RequireClientSecret);
    }

    [Fact]
    public void CreateValidatedSnapshot_DeepCopiesIdentityCollectionsAndSeedClaims()
    {
        var options = CreateApprovedHttpsOptions();
        var mapper = new KeycloakClaimMapper("appsurface-role", "appsurface_role", "founder");
        var provider = new KeycloakIdentityProviderConfiguration("google", "synthetic-google-client", "Google");
        options.Mappers.Add(mapper);
        options.IdentityProviders.Add(provider);
        options.RemovedMapperNames.Add("retired-mapper");
        options.RemovedIdentityProviderAliases.Add("retired-provider");
        options.RequireIdentitySeed = true;
        var sourceUser = options.SeededUsers[0];
        sourceUser.Claims["custom_claim"] = "before";

        var snapshot = options.CreateValidatedSnapshot();

        options.ApprovedHttpsOrigins.Clear();
        options.WebOrigins.Clear();
        options.RedirectUris.Clear();
        options.PostLogoutRedirectUris.Clear();
        options.Mappers.Clear();
        options.IdentityProviders.Clear();
        options.RemovedMapperNames.Clear();
        options.RemovedIdentityProviderAliases.Clear();
        sourceUser.Claims["custom_claim"] = "after";
        options.SeededUsers.Clear();

        Assert.Equal(2, snapshot.ApprovedHttpsOrigins.Count);
        Assert.Contains(new Uri("https://identity.example.test"), snapshot.ApprovedHttpsOrigins);
        Assert.Contains(new Uri("https://app.example.test"), snapshot.ApprovedHttpsOrigins);
        Assert.Equal(new Uri("https://app.example.test"), Assert.Single(snapshot.WebOrigins));
        Assert.Equal(new Uri("https://app.example.test/signin-appsurface-oidc"), Assert.Single(snapshot.RedirectUris));
        Assert.Equal(new Uri("https://app.example.test/signout-callback-appsurface-oidc"), Assert.Single(snapshot.PostLogoutRedirectUris));
        Assert.Equal(mapper, Assert.Single(snapshot.Mappers));
        Assert.Equal(provider, Assert.Single(snapshot.IdentityProviders));
        Assert.Equal("retired-mapper", Assert.Single(snapshot.RemovedMapperNames));
        Assert.Equal("retired-provider", Assert.Single(snapshot.RemovedIdentityProviderAliases));
        Assert.True(snapshot.RequireIdentitySeed);
        Assert.NotSame(sourceUser, snapshot.SeededUsers[0]);
        Assert.NotSame(sourceUser.Claims, snapshot.SeededUsers[0].Claims);
        Assert.Equal("before", snapshot.SeededUsers[0].Claims["custom_claim"]);
    }

    [Fact]
    public void Validate_EmptySeededUsersRequireExplicitIdentitySeed()
    {
        var options = new AppSurfaceKeycloakOptions();
        options.SeededUsers.Clear();

        var exception = AssertInvalidOptions(options.Validate);
        Assert.Contains("at least one local proof user", exception.Message, StringComparison.Ordinal);

        options.RequireIdentitySeed = true;
        options.Validate();
        Assert.Empty(options.CreateValidatedSnapshot().SeededUsers);
    }

    [Fact]
    public void ValidateAndSnapshot_NullIdentityInputsUseSafeInvalidOptionsDiagnostic()
    {
        AssertInvalidOptions(new AppSurfaceKeycloakOptions { Realm = null! }.Validate);
        AssertInvalidOptions(new AppSurfaceKeycloakOptions { ClientId = null! }.Validate);

        var nullOrigin = new AppSurfaceKeycloakOptions();
        nullOrigin.ApprovedHttpsOrigins.Add(null!);
        AssertInvalidOptions(nullOrigin.Validate);

        var nullUser = new AppSurfaceKeycloakOptions();
        nullUser.SeededUsers.Clear();
        nullUser.SeededUsers.Add(null!);
        AssertInvalidOptions(nullUser.Validate);
        AssertInvalidOptions(() => nullUser.CreateValidatedSnapshot());
    }

    [Theory]
    [InlineData(KeycloakClientMode.Confidential)]
    [InlineData(KeycloakClientMode.Public)]
    public void CredentialBindings_CreateSnapshotKeepsDeclaredSecretsTypedWithoutResolvingThem(KeycloakClientMode clientMode)
    {
        var builder = CreateBuilder();
        var resolutions = 0;
        var username = builder.AddParameter("identity-admin-user", () => { resolutions++; return "synthetic-admin"; });
        var adminPassword = builder.AddParameter("identity-admin-password", () => { resolutions++; return "synthetic-admin-secret"; }, secret: true);
        var clientSecret = clientMode == KeycloakClientMode.Confidential
            ? builder.AddParameter("identity-client-secret", () => { resolutions++; return "synthetic-client-secret"; }, secret: true)
            : null;
        var providerSecret = builder.AddParameter("identity-google-secret", () => { resolutions++; return "synthetic-provider-secret"; }, secret: true);
        var desired = DesiredWithGoogleProvider(clientMode);
        var bindings = Bindings(clientSecret, providerSecret);

        var snapshot = bindings.CreateSnapshot(builder, desired, username, adminPassword);

        Assert.Same(clientSecret, snapshot.ClientSecret);
        Assert.Same(providerSecret, snapshot.IdentityProviderSecrets["google"]);
        Assert.Equal(0, resolutions);
    }

    [Theory]
    [InlineData("missing-client")]
    [InlineData("missing-provider")]
    [InlineData("excess-provider")]
    [InlineData("non-secret-client")]
    [InlineData("non-secret-provider")]
    [InlineData("cross-builder-client")]
    [InlineData("admin-password-as-client")]
    [InlineData("admin-password-as-provider")]
    [InlineData("client-provider-reused")]
    [InlineData("foreign-admin-username")]
    [InlineData("foreign-admin-password")]
    [InlineData("public-admin-password")]
    [InlineData("shared-admin-parameter")]
    [InlineData("null-provider")]
    public void CredentialBindings_CreateSnapshotRejectsInvalidBindingsBeforeResolution(string scenario)
    {
        var builder = CreateBuilder();
        var otherBuilder = CreateBuilder();
        var resolutions = 0;
        var username = builder.AddParameter("identity-admin-user", () => { resolutions++; return "synthetic-admin"; });
        var adminPassword = builder.AddParameter("identity-admin-password", () => { resolutions++; return "synthetic-admin-secret"; }, secret: true);
        var clientSecret = builder.AddParameter("identity-client-secret", () => { resolutions++; return "synthetic-client-secret"; }, secret: true);
        var providerSecret = builder.AddParameter("identity-google-secret", () => { resolutions++; return "synthetic-provider-secret"; }, secret: true);
        var nonSecretClient = builder.AddParameter("identity-client-public", () => { resolutions++; return "synthetic-value"; });
        var nonSecretProvider = builder.AddParameter("identity-google-public", () => { resolutions++; return "synthetic-value"; });
        var crossBuilderClient = otherBuilder.AddParameter("identity-cross-builder-secret", () => { resolutions++; return "synthetic-value"; }, secret: true);
        var desired = DesiredWithGoogleProvider(KeycloakClientMode.Confidential);
        var bindings = new AppSurfaceKeycloakCredentialBindings();

        switch (scenario)
        {
            case "missing-client":
                bindings.IdentityProviderSecrets.Add("google", providerSecret);
                break;
            case "missing-provider":
                bindings.ClientSecret = clientSecret;
                break;
            case "excess-provider":
                bindings.ClientSecret = clientSecret;
                bindings.IdentityProviderSecrets.Add("google", providerSecret);
                bindings.IdentityProviderSecrets.Add("unexpected", providerSecret);
                break;
            case "non-secret-client":
                bindings.ClientSecret = nonSecretClient;
                bindings.IdentityProviderSecrets.Add("google", providerSecret);
                break;
            case "non-secret-provider":
                bindings.ClientSecret = clientSecret;
                bindings.IdentityProviderSecrets.Add("google", nonSecretProvider);
                break;
            case "cross-builder-client":
                bindings.ClientSecret = crossBuilderClient;
                bindings.IdentityProviderSecrets.Add("google", providerSecret);
                break;
            case "admin-password-as-client":
                bindings.ClientSecret = adminPassword;
                bindings.IdentityProviderSecrets.Add("google", providerSecret);
                break;
            case "admin-password-as-provider":
                bindings.ClientSecret = clientSecret;
                bindings.IdentityProviderSecrets.Add("google", adminPassword);
                break;
            case "client-provider-reused":
                bindings.ClientSecret = clientSecret;
                bindings.IdentityProviderSecrets.Add("google", clientSecret);
                break;
            case "foreign-admin-username":
                username = crossBuilderClient;
                bindings = Bindings(clientSecret, providerSecret);
                break;
            case "foreign-admin-password":
                adminPassword = crossBuilderClient;
                bindings = Bindings(clientSecret, providerSecret);
                break;
            case "public-admin-password":
                adminPassword = nonSecretProvider;
                bindings = Bindings(clientSecret, providerSecret);
                break;
            case "shared-admin-parameter":
                username = adminPassword;
                bindings = Bindings(clientSecret, providerSecret);
                break;
            case "null-provider":
                bindings.ClientSecret = clientSecret;
                bindings.IdentityProviderSecrets.Add("google", null!);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario));
        }

        var exception = Assert.Throws<AppSurfaceKeycloakException>(() =>
            bindings.CreateSnapshot(builder, desired, username, adminPassword));

        Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.InvalidCredentialBinding, exception.Code);
        Assert.DoesNotContain("synthetic-", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, resolutions);
    }

    private static AppSurfaceKeycloakOptions CreateApprovedHttpsOptions()
    {
        var options = new AppSurfaceKeycloakOptions
        {
            Realm = "appsurface-test",
            ClientId = "web-client",
            Authority = new Uri("https://identity.example.test/realms/appsurface-test"),
        };
        options.ApprovedHttpsOrigins.Add(new Uri("https://identity.example.test"));
        options.ApprovedHttpsOrigins.Add(new Uri("https://app.example.test"));
        options.RedirectUris.Clear();
        options.RedirectUris.Add(new Uri("https://app.example.test/signin-appsurface-oidc"));
        options.PostLogoutRedirectUris.Clear();
        options.PostLogoutRedirectUris.Add(new Uri("https://app.example.test/signout-callback-appsurface-oidc"));
        options.WebOrigins.Add(new Uri("https://app.example.test"));
        return options;
    }

    private static KeycloakDesiredConfiguration DesiredWithGoogleProvider(KeycloakClientMode mode)
    {
        var options = new AppSurfaceKeycloakOptions { ClientMode = mode };
        options.IdentityProviders.Add(new KeycloakIdentityProviderConfiguration("google", "synthetic-google-client"));
        return options.CreateDesiredConfiguration();
    }

    private static AppSurfaceKeycloakCredentialBindings Bindings(
        IResourceBuilder<ParameterResource>? clientSecret,
        IResourceBuilder<ParameterResource> providerSecret)
    {
        var bindings = new AppSurfaceKeycloakCredentialBindings { ClientSecret = clientSecret };
        bindings.IdentityProviderSecrets.Add("google", providerSecret);
        return bindings;
    }

    private static IDistributedApplicationBuilder CreateBuilder() =>
        DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = ["--environment", "Test"],
            DisableDashboard = true,
        });

    private static AppSurfaceKeycloakException AssertInvalidOptions(Action action)
    {
        var exception = Assert.Throws<AppSurfaceKeycloakException>(action);
        Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.InvalidOptions, exception.Code);
        Assert.DoesNotContain("synthetic-", exception.Message, StringComparison.Ordinal);
        return exception;
    }
}
