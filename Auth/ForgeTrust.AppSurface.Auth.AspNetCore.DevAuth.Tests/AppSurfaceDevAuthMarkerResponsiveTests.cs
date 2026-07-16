using ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth.Tests;

public sealed class AppSurfaceDevAuthMarkerResponsiveTests
{
    [Fact]
    public void Marker_DefaultStyles_EmitsResponsiveFlowAndLongContentRules()
    {
        var html = DevAuthMarkerTestHost.Render();
        var mediaStart = html.IndexOf("@media(max-width:640px)", StringComparison.Ordinal);
        var styleEnd = html.IndexOf("</style>", mediaStart, StringComparison.Ordinal);

        Assert.True(mediaStart >= 0, "Expected the default marker CSS to include the 640px responsive rule.");
        Assert.True(styleEnd > mediaStart, "Expected the responsive rule to be contained by the marker style element.");

        var mobileCss = html[mediaStart..styleEnd];
        Assert.Contains("position:static", mobileCss, StringComparison.Ordinal);
        Assert.Contains("right:auto", mobileCss, StringComparison.Ordinal);
        Assert.Contains("bottom:auto", mobileCss, StringComparison.Ordinal);
        Assert.Contains("z-index:auto", mobileCss, StringComparison.Ordinal);
        Assert.Contains("max-width:none", mobileCss, StringComparison.Ordinal);
        Assert.Contains("box-shadow:none", mobileCss, StringComparison.Ordinal);
        Assert.Contains("__actions form{min-width:0;max-width:100%}", mobileCss, StringComparison.Ordinal);
        Assert.Contains("__button{min-width:0;max-width:100%;overflow-wrap:anywhere}", mobileCss, StringComparison.Ordinal);

        Assert.Contains(
            ".appsurface-dev-auth-marker{position:fixed;right:16px;bottom:16px;z-index:2147483647;max-width:min(360px,calc(100vw - 32px))",
            html,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Marker_WithoutDefaultStyles_EmitsNoPackageCss()
    {
        var html = DevAuthMarkerTestHost.Render(configureMarker: options => options.IncludeDefaultStyles = false);

        Assert.Contains("data-appsurface-dev-auth=\"marker\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<style>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("@media", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Marker_WithHiddenControls_PreservesMetadataAndLinks()
    {
        var html = DevAuthMarkerTestHost.Render(configureMarker: options => options.ShowPersonaControls = false);

        Assert.Contains("appsurface-dev-auth-marker__meta", html, StringComparison.Ordinal);
        Assert.Contains("Scheme", html, StringComparison.Ordinal);
        Assert.Contains("Subject", html, StringComparison.Ordinal);
        Assert.Contains("Open persona lab", html, StringComparison.Ordinal);
        Assert.Contains("Status JSON", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<div class=\"appsurface-dev-auth-marker__actions\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<form", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<button", html, StringComparison.Ordinal);
    }
}

internal static class DevAuthMarkerTestHost
{
    private const string PersonaProtectorPurpose = "ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth.Persona.v1";

    public static string Render(
        Action<AppSurfaceDevAuthOptions>? configureDevAuth = null,
        Action<AppSurfaceDevAuthMarkerOptions>? configureMarker = null,
        string? selectedPersonaId = null)
    {
        var options = new AppSurfaceDevAuthOptions();
        if (configureDevAuth is null)
        {
            AddDefaultPersonas(options);
        }
        else
        {
            configureDevAuth(options);
        }

        var dataProtectionProvider = new EphemeralDataProtectionProvider();
        var context = new DefaultHttpContext();
        context.Request.Path = "/responsive-proof";
        if (selectedPersonaId is not null)
        {
            var protectedPersonaId = dataProtectionProvider
                .CreateProtector(PersonaProtectorPurpose)
                .Protect(selectedPersonaId);
            context.Request.Headers.Cookie = $"{AppSurfaceDevAuthDefaults.CookieName}={protectedPersonaId}";
        }

        return AppSurfaceDevAuthMarker.Render(
            context,
            new TestHostEnvironment("Development"),
            Options.Create(options),
            dataProtectionProvider,
            configureMarker);
    }

    public static void AddDefaultPersonas(AppSurfaceDevAuthOptions options)
    {
        options.Users.Add(
            "admin",
            user => user
                .DisplayName("Local Admin")
                .Subject("admin-1")
                .Claim("role", "operator"));
        options.Users.Add(
            "viewer",
            user => user
                .DisplayName("Local Viewer")
                .Subject("viewer-1")
                .Claim("role", "viewer"));
    }
}
