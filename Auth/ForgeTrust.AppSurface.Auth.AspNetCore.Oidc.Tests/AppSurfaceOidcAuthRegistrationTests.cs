using System.Security.Claims;
using ForgeTrust.AppSurface.Auth.AspNetCore;
using ForgeTrust.AppSurface.Auth.AspNetCore.Oidc;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace ForgeTrust.AppSurface.Auth.AspNetCore.Oidc.Tests;

public sealed class AppSurfaceOidcAuthRegistrationTests
{
    [Fact]
    public async Task AddAppSurfaceOidcAuth_RegistersNamedSchemesWithoutDefaultTakeover()
    {
        var services = new ServiceCollection();

        services.AddAppSurfaceOidcAuth(options =>
        {
            options.ConfigureOpenIdConnect(oidc =>
            {
                oidc.Authority = "https://issuer.example";
                oidc.ClientId = "client-id";
                oidc.ClientSecret = "client-secret";
            });
        });

        using var provider = services.BuildServiceProvider();
        var schemeProvider = provider.GetRequiredService<IAuthenticationSchemeProvider>();
        var authOptions = provider.GetRequiredService<IOptions<AuthenticationOptions>>().Value;
        var oidcOptions = provider.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(AppSurfaceOidcAuthOptions.DefaultOidcScheme);
        var cookieOptions = provider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(AppSurfaceOidcAuthOptions.DefaultCookieScheme);

        Assert.Null(authOptions.DefaultScheme);
        Assert.NotNull(await schemeProvider.GetSchemeAsync(AppSurfaceOidcAuthOptions.DefaultCookieScheme));
        Assert.NotNull(await schemeProvider.GetSchemeAsync(AppSurfaceOidcAuthOptions.DefaultOidcScheme));
        Assert.Equal(AppSurfaceOidcAuthOptions.DefaultCookieScheme, oidcOptions.SignInScheme);
        Assert.Equal(OpenIdConnectResponseType.Code, oidcOptions.ResponseType);
        Assert.Equal(AppSurfaceOidcAuthOptions.DefaultCallbackPath, oidcOptions.CallbackPath);
        Assert.Equal(AppSurfaceOidcAuthOptions.DefaultSignedOutCallbackPath, oidcOptions.SignedOutCallbackPath);
        Assert.False(oidcOptions.SaveTokens);
        Assert.Equal("https://issuer.example", oidcOptions.Authority);
        Assert.Equal("client-id", oidcOptions.ClientId);
        Assert.Equal("client-secret", oidcOptions.ClientSecret);
        Assert.NotNull(cookieOptions);
    }

    [Fact]
    public void AddAppSurfaceOidcAuth_PreservesExistingHostDefaults()
    {
        var services = new ServiceCollection();

        services.AddAuthentication("Host.Cookies").AddCookie("Host.Cookies");
        services.AddAppSurfaceOidcAuth(options =>
        {
            options.ConfigureOpenIdConnect(oidc =>
            {
                oidc.Authority = "https://issuer.example";
                oidc.ClientId = "client-id";
                oidc.ClientSecret = "client-secret";
            });
        });

        using var provider = services.BuildServiceProvider();
        var authOptions = provider.GetRequiredService<IOptions<AuthenticationOptions>>().Value;

        Assert.Equal("Host.Cookies", authOptions.DefaultScheme);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AddAppSurfaceOidcAuth_WhenCookieSchemeIsBlank_Throws(string scheme)
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentException>(() => services.AddAppSurfaceOidcAuth(options => options.CookieScheme = scheme));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AddAppSurfaceOidcAuth_WhenOidcSchemeIsBlank_Throws(string scheme)
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentException>(() => services.AddAppSurfaceOidcAuth(options => options.OidcScheme = scheme));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AddAppSurfaceOidcAuth_WhenSubjectClaimIsBlank_Throws(string subjectClaim)
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentException>(() =>
            services.AddAppSurfaceOidcAuth(options => options.SubjectClaim = subjectClaim));
    }

    [Fact]
    public void AddAppSurfaceOidcAuth_WhenSchemesMatch_Throws()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentException>(() => services.AddAppSurfaceOidcAuth(options =>
        {
            options.CookieScheme = "Shared";
            options.OidcScheme = "Shared";
        }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AddAppSurfaceOidcAuth_WhenCallbackPathIsBlank_Throws(string callbackPath)
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentException>(() => services.AddAppSurfaceOidcAuth(options => options.CallbackPath = callbackPath));
    }

    [Theory]
    [InlineData("//example.com")]
    [InlineData("/\\example")]
    [InlineData("/some\\path")]
    [InlineData("/line\rbreak")]
    [InlineData("/line\nbreak")]
    public void AddAppSurfaceOidcAuth_WhenCallbackPathIsUnsafe_Throws(string callbackPath)
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentException>(() => services.AddAppSurfaceOidcAuth(options => options.CallbackPath = callbackPath));
    }

    [Theory]
    [InlineData("//example.com")]
    [InlineData("/\\example")]
    [InlineData("/some\\path")]
    [InlineData("/line\rbreak")]
    [InlineData("/line\nbreak")]
    public void AddAppSurfaceOidcAuth_WhenSignedOutCallbackPathIsUnsafe_Throws(string signedOutCallbackPath)
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentException>(() =>
            services.AddAppSurfaceOidcAuth(options => options.SignedOutCallbackPath = signedOutCallbackPath));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AddAppSurfaceOidcAuth_WhenSignedOutCallbackPathIsBlank_Throws(string signedOutCallbackPath)
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentException>(() =>
            services.AddAppSurfaceOidcAuth(options => options.SignedOutCallbackPath = signedOutCallbackPath));
    }

    [Fact]
    public void ConfigureCookie_WhenConfigureIsNull_Throws()
    {
        var options = new AppSurfaceOidcAuthOptions();

        Assert.Throws<ArgumentNullException>(() => options.ConfigureCookie(null!));
    }

    [Fact]
    public void ConfigureOpenIdConnect_WhenConfigureIsNull_Throws()
    {
        var options = new AppSurfaceOidcAuthOptions();

        Assert.Throws<ArgumentNullException>(() => options.ConfigureOpenIdConnect(null!));
    }

    [Fact]
    public void AddAppSurfaceOidcAuth_MapsSubjectClaimThroughAspNetCoreAdapter()
    {
        var services = new ServiceCollection();

        services.AddAppSurfaceOidcAuth(options =>
        {
            options.SubjectClaim = "tenant-sub";
            options.ConfigureOpenIdConnect(oidc =>
            {
                oidc.Authority = "https://issuer.example";
                oidc.ClientId = "client-id";
                oidc.ClientSecret = "client-secret";
            });
        });

        using var provider = services.BuildServiceProvider();
        var adapterOptions = provider.GetRequiredService<IOptions<AppSurfaceAspNetCoreAuthOptions>>().Value;

        Assert.Equal("tenant-sub", adapterOptions.SubjectClaimTypes[0]);
    }

    [Fact]
    public void AddAppSurfaceOidcAuth_WhenRequiredOidcValuesAreMissing_ThrowsOptionsValidationException()
    {
        var services = new ServiceCollection();
        services.AddAppSurfaceOidcAuth();

        using var provider = services.BuildServiceProvider();
        var monitor = provider.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>();

        var exception = Assert.Throws<OptionsValidationException>(() =>
            monitor.Get(AppSurfaceOidcAuthOptions.DefaultOidcScheme));

        Assert.Contains(AppSurfaceOidcAuthDiagnosticCodes.MissingAuthority, exception.Message);
        Assert.Contains(AppSurfaceOidcAuthDiagnosticCodes.MissingClientId, exception.Message);
        Assert.Contains(AppSurfaceOidcAuthDiagnosticCodes.MissingClientSecret, exception.Message);
    }

    [Fact]
    public void AddAppSurfaceOidcAuth_WhenClientSecretValidationIsDisabled_AllowsMissingClientSecret()
    {
        var services = new ServiceCollection();

        services.AddAppSurfaceOidcAuth(options =>
        {
            options.RequireClientSecret = false;
            options.ConfigureOpenIdConnect(oidc =>
            {
                oidc.Authority = "https://issuer.example";
                oidc.ClientId = "client-id";
            });
        });

        using var provider = services.BuildServiceProvider();
        var oidcOptions = provider.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(AppSurfaceOidcAuthOptions.DefaultOidcScheme);

        Assert.Null(oidcOptions.ClientSecret);
    }

    [Fact]
    public void AddAppSurfaceOidcAuth_WhenSaveTokensOptIn_SetsHandlerOption()
    {
        var services = new ServiceCollection();

        services.AddAppSurfaceOidcAuth(options =>
        {
            options.SaveTokens = true;
            options.ConfigureOpenIdConnect(oidc =>
            {
                oidc.Authority = "https://issuer.example";
                oidc.ClientId = "client-id";
                oidc.ClientSecret = "client-secret";
            });
        });

        using var provider = services.BuildServiceProvider();
        var oidcOptions = provider.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(AppSurfaceOidcAuthOptions.DefaultOidcScheme);

        Assert.True(oidcOptions.SaveTokens);
    }

    [Fact]
    public void AddAppSurfaceOidcAuth_PreservesHostConfiguredOidcEvents()
    {
        var services = new ServiceCollection();
        Func<MessageReceivedContext, Task> messageReceived = _ => Task.CompletedTask;
        Func<RemoteSignOutContext, Task> remoteSignOut = _ => Task.CompletedTask;

        services.AddAppSurfaceOidcAuth(options =>
        {
            options.ConfigureOpenIdConnect(oidc =>
            {
                oidc.Authority = "https://issuer.example";
                oidc.ClientId = "client-id";
                oidc.ClientSecret = "client-secret";
                oidc.Events.OnMessageReceived = messageReceived;
                oidc.Events.OnRemoteSignOut = remoteSignOut;
            });
        });

        using var provider = services.BuildServiceProvider();
        var oidcOptions = provider.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(AppSurfaceOidcAuthOptions.DefaultOidcScheme);

        Assert.Same(messageReceived, oidcOptions.Events.OnMessageReceived);
        Assert.Same(remoteSignOut, oidcOptions.Events.OnRemoteSignOut);
    }

    [Fact]
    public async Task AddAppSurfaceOidcAuth_WhenSubjectClaimIsMissing_AddsSafeDiagnostic()
    {
        var oidcOptions = ResolveOidcOptions();
        var properties = new AuthenticationProperties();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("name", "Taylor")],
            authenticationType: "oidc"));
        var context = new TokenValidatedContext(
            new DefaultHttpContext(),
            CreateOidcScheme(),
            oidcOptions,
            principal,
            properties);

        await oidcOptions.Events.TokenValidated(context);

        Assert.Equal(
            AppSurfaceOidcAuthDiagnosticCodes.MissingSubjectClaim,
            properties.Items[AppSurfaceOidcAuthMetadataKeys.DiagnosticCode]);
        Assert.Equal(nameof(AppSurfaceOidcAuthOptions.SubjectClaim), properties.Items[AppSurfaceOidcAuthMetadataKeys.OptionName]);
        Assert.DoesNotContain(properties.Items, item => item.Value == "Taylor");
    }

    [Fact]
    public async Task AddAppSurfaceOidcAuth_WhenRemoteFailureOccurs_AddsSafeDiagnostic()
    {
        var oidcOptions = ResolveOidcOptions();
        var properties = new AuthenticationProperties();
        var context = new RemoteFailureContext(
            new DefaultHttpContext(),
            CreateOidcScheme(),
            oidcOptions,
            new InvalidOperationException("provider response with possible sensitive content"))
        {
            Properties = properties,
        };

        await oidcOptions.Events.RemoteFailure(context);

        Assert.Equal(
            AppSurfaceOidcAuthDiagnosticCodes.RemoteFailure,
            properties.Items[AppSurfaceOidcAuthMetadataKeys.DiagnosticCode]);
        Assert.Equal(nameof(OpenIdConnectEvents.RemoteFailure), properties.Items[AppSurfaceOidcAuthMetadataKeys.EventName]);
        Assert.Equal(nameof(InvalidOperationException), properties.Items[AppSurfaceOidcAuthMetadataKeys.ExceptionType]);
        Assert.DoesNotContain(properties.Items, item => item.Value?.Contains("provider response", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task AddAppSurfaceOidcAuth_WhenSaveTokensIsEnabled_AddsTokenPersistenceDiagnostic()
    {
        var oidcOptions = ResolveOidcOptions(options => options.SaveTokens = true);
        var properties = new AuthenticationProperties();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(AppSurfaceOidcAuthOptions.DefaultOidcScheme, "subject")],
            authenticationType: "oidc"));
        var ticket = new AuthenticationTicket(principal, properties, AppSurfaceOidcAuthOptions.DefaultOidcScheme);
        var context = new TicketReceivedContext(
            new DefaultHttpContext(),
            CreateOidcScheme(),
            oidcOptions,
            ticket);

        await oidcOptions.Events.TicketReceived(context);

        Assert.Equal(
            AppSurfaceOidcAuthDiagnosticCodes.TokenPersistenceEnabled,
            properties.Items[AppSurfaceOidcAuthMetadataKeys.DiagnosticCode]);
    }

    private static OpenIdConnectOptions ResolveOidcOptions(Action<AppSurfaceOidcAuthOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddAppSurfaceOidcAuth(options =>
        {
            configure?.Invoke(options);
            options.ConfigureOpenIdConnect(oidc =>
            {
                oidc.Authority = "https://issuer.example";
                oidc.ClientId = "client-id";
                oidc.ClientSecret = "client-secret";
            });
        });

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(AppSurfaceOidcAuthOptions.DefaultOidcScheme);
    }

    private static AuthenticationScheme CreateOidcScheme()
    {
        return new AuthenticationScheme(
            AppSurfaceOidcAuthOptions.DefaultOidcScheme,
            displayName: null,
            typeof(OpenIdConnectHandler));
    }
}
