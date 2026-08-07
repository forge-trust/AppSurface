using AuthAspireKeycloakWeb;
using ForgeTrust.AppSurface.Auth.AspNetCore.Oidc;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests;

public sealed class AppSurfaceKeycloakWebSignoutQaRegressionTests
{
    // Regression: ISSUE-003 — Keycloak rejected sign-out because the sample did not persist an ID token.
    // Found by /qa on 2026-08-07. Report: .gstack/qa-reports/qa-report-localhost-2026-08-07.md.
    [Fact]
    public void WebProofConfiguration_SavesTheIdTokenForKeycloakSignOut()
    {
        var services = new ServiceCollection();
        services.AddAppSurfaceOidcAuth(options =>
            AppSurfaceKeycloakWebOidcConfiguration.Configure(options, new ConfigurationBuilder().Build()));

        using var provider = services.BuildServiceProvider();
        var oidc = provider.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(AppSurfaceOidcAuthOptions.DefaultOidcScheme);

        Assert.Equal("https://localhost:8080/realms/appsurface-dev", oidc.Authority);
        Assert.True(oidc.RequireHttpsMetadata);
        Assert.True(oidc.SaveTokens);
    }
}
