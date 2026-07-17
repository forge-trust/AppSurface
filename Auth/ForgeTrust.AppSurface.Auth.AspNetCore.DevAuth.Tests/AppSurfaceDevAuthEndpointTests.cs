using System.Net;
using System.Security.Claims;
using System.Text.Json;
using ForgeTrust.AppSurface.Auth.AspNetCore;
using ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth.Tests;

public sealed class AppSurfaceDevAuthEndpointTests
{
    [Fact]
    public void MapAppSurfaceDevAuth_WithNullEndpoints_ThrowsArgumentNullException()
    {
        IEndpointRouteBuilder endpoints = null!;

        Assert.Throws<ArgumentNullException>(() => endpoints.MapAppSurfaceDevAuth());
    }

    [Fact]
    public async Task StatusEndpoint_WithoutPersona_ReturnsContractAndNoStoreHeaders()
    {
        await using var app = BuildApp();
        var endpoint = FindEndpoint(app, "/_appsurface/dev-auth/status", HttpMethods.Get);
        var context = CreateContext(app.Services);

        await endpoint.RequestDelegate!(context);

        var json = await ReadBodyAsync(context);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.True(root.GetProperty("enabled").GetBoolean());
        Assert.Equal("Development", root.GetProperty("environment").GetString());
        Assert.Equal(AppSurfaceDevAuthDefaults.AuthenticationScheme, root.GetProperty("scheme").GetString());
        Assert.Equal("/_appsurface/dev-auth", root.GetProperty("pathPrefix").GetString());
        Assert.True(root.GetProperty("isAnonymous").GetBoolean());
        Assert.Equal("no-store, no-cache", context.Response.Headers.CacheControl);
        Assert.Equal("noindex, nofollow", context.Response.Headers["X-Robots-Tag"]);
    }

    [Fact]
    public async Task ControlPage_RendersDevelopmentWarningAndPersonaControls()
    {
        await using var app = BuildApp();
        var endpoint = FindEndpoint(app, "/_appsurface/dev-auth/", HttpMethods.Get);
        var context = CreateContext(app.Services);

        await endpoint.RequestDelegate!(context);

        var html = await ReadBodyAsync(context);

        Assert.Contains("AppSurface Dev Auth [FAKE LOCAL AUTH]", html, StringComparison.Ordinal);
        Assert.Contains("data-appsurface-dev-auth=\"control-page\"", html, StringComparison.Ordinal);
        Assert.Contains("Select persona", html, StringComparison.Ordinal);
        Assert.Contains("Clear persona", html, StringComparison.Ordinal);
        Assert.Contains("DEV AUTH: Anonymous (AppSurface.DevAuth)", html, StringComparison.Ordinal);
        Assert.DoesNotContain("login", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("logout", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildMutationUrl_WithoutReturnUrl_ReturnsBareAction()
    {
        var action = AppSurfaceDevAuthEndpointRouteBuilderExtensions.BuildMutationUrl(
            "/_appsurface/dev-auth",
            "select/admin",
            returnUrl: null);

        Assert.Equal("/_appsurface/dev-auth/select/admin", action);
    }

    [Fact]
    public void BuildMutationUrl_WithReturnUrl_AppendsOneUriEscapedValue()
    {
        var action = AppSurfaceDevAuthEndpointRouteBuilderExtensions.BuildMutationUrl(
            "/_appsurface/dev-auth",
            "clear",
            "/protected?tab=auth&mode=full");

        Assert.Equal(
            "/_appsurface/dev-auth/clear?returnUrl=%2Fprotected%3Ftab%3Dauth%26mode%3Dfull",
            action);
    }

    [Fact]
    public async Task ControlPage_WithSafeReturnUrl_RendersTargetOnEveryMutationAction()
    {
        await using var app = BuildApp();
        var endpoint = FindEndpoint(app, "/_appsurface/dev-auth/", HttpMethods.Get);
        var context = CreateContext(app.Services);
        context.Request.QueryString = QueryString.Create("returnUrl", "/protected?tab=auth&mode=full");

        await endpoint.RequestDelegate!(context);

        var html = await ReadBodyAsync(context);
        const string encodedReturnUrl = "%2Fprotected%3Ftab%3Dauth%26mode%3Dfull";
        Assert.Contains($"action=\"/_appsurface/dev-auth/select/admin?returnUrl={encodedReturnUrl}\"", html, StringComparison.Ordinal);
        Assert.Contains($"action=\"/_appsurface/dev-auth/select/viewer?returnUrl={encodedReturnUrl}\"", html, StringComparison.Ordinal);
        Assert.Contains($"action=\"/_appsurface/dev-auth/clear?returnUrl={encodedReturnUrl}\"", html, StringComparison.Ordinal);
        Assert.Equal(3, CountOccurrences(html, "returnUrl="));
    }

    [Fact]
    public async Task ControlPage_WithRootReturnUrl_PreservesExplicitRoot()
    {
        await using var app = BuildApp();
        var endpoint = FindEndpoint(app, "/_appsurface/dev-auth/", HttpMethods.Get);
        var context = CreateContext(app.Services);
        context.Request.QueryString = QueryString.Create("returnUrl", "/");

        await endpoint.RequestDelegate!(context);

        var html = await ReadBodyAsync(context);
        Assert.Contains("action=\"/_appsurface/dev-auth/select/admin?returnUrl=%2F\"", html, StringComparison.Ordinal);
        Assert.Contains("action=\"/_appsurface/dev-auth/select/viewer?returnUrl=%2F\"", html, StringComparison.Ordinal);
        Assert.Contains("action=\"/_appsurface/dev-auth/clear?returnUrl=%2F\"", html, StringComparison.Ordinal);
        Assert.Equal(3, CountOccurrences(html, "returnUrl=%2F"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("?returnUrl=")]
    [InlineData("?returnUrl=%20%20")]
    public async Task ControlPage_WithoutUsableReturnUrl_RendersBareMutationActions(string queryString)
    {
        await using var app = BuildApp();
        var endpoint = FindEndpoint(app, "/_appsurface/dev-auth/", HttpMethods.Get);
        var context = CreateContext(app.Services);
        context.Request.QueryString = new QueryString(queryString);

        await endpoint.RequestDelegate!(context);

        var html = await ReadBodyAsync(context);
        AssertBareMutationActions(html, "/_appsurface/dev-auth");
    }

    [Theory]
    [InlineData("?returnUrl=dashboard")]
    [InlineData("?returnUrl=https%3A%2F%2Fexample.com%2F")]
    [InlineData("?returnUrl=%2F%2Fexample.com%2F")]
    [InlineData("?returnUrl=%2F%5Cexample.com")]
    [InlineData("?returnUrl=%2Fsafe%5Cevil")]
    [InlineData("?returnUrl=%2Fsafe%0Aevil")]
    public async Task ControlPage_WithRejectedReturnUrl_RendersBareMutationActions(string queryString)
    {
        await using var app = BuildApp();
        var endpoint = FindEndpoint(app, "/_appsurface/dev-auth/", HttpMethods.Get);
        var context = CreateContext(app.Services);
        context.Request.QueryString = new QueryString(queryString);

        await endpoint.RequestDelegate!(context);

        var html = await ReadBodyAsync(context);
        AssertBareMutationActions(html, "/_appsurface/dev-auth");
    }

    [Fact]
    public async Task ControlPage_WithCustomPathPrefixAndSafeReturnUrl_ComposesExactActions()
    {
        await using var app = BuildApp(options =>
        {
            options.PathPrefix = "/local/personas";
            AddDefaultPersonas(options);
        });
        var endpoint = FindEndpoint(app, "/local/personas/", HttpMethods.Get);
        var context = CreateContext(app.Services);
        context.Request.QueryString = QueryString.Create("returnUrl", "/protected");

        await endpoint.RequestDelegate!(context);

        var html = await ReadBodyAsync(context);
        Assert.Contains("action=\"/local/personas/select/admin?returnUrl=%2Fprotected\"", html, StringComparison.Ordinal);
        Assert.Contains("action=\"/local/personas/select/viewer?returnUrl=%2Fprotected\"", html, StringComparison.Ordinal);
        Assert.Contains("action=\"/local/personas/clear?returnUrl=%2Fprotected\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ControlPage_WithHtmlSensitiveSafeReturnUrl_EscapesBeforeEncodingActionAttribute()
    {
        await using var app = BuildApp();
        var endpoint = FindEndpoint(app, "/_appsurface/dev-auth/", HttpMethods.Get);
        var context = CreateContext(app.Services);
        context.Request.QueryString = QueryString.Create("returnUrl", "/protected?note=\"<x>&mode=1");

        await endpoint.RequestDelegate!(context);

        var html = await ReadBodyAsync(context);
        const string encodedReturnUrl = "%2Fprotected%3Fnote%3D%22%3Cx%3E%26mode%3D1";
        Assert.Contains($"action=\"/_appsurface/dev-auth/select/admin?returnUrl={encodedReturnUrl}\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("note=\"<x>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("%252Fprotected", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Marker_RendersPersistentOverlayControlsWithCurrentReturnUrl()
    {
        using var app = BuildApp();
        var context = CreateContext(app.Services);
        context.Request.Path = "/dashboard";
        context.Request.QueryString = new QueryString("?tab=auth");

        var html = AppSurfaceDevAuthMarker.Render(
            context,
            app.Services.GetRequiredService<IHostEnvironment>(),
            app.Services.GetRequiredService<IOptions<AppSurfaceDevAuthOptions>>(),
            app.Services.GetRequiredService<IDataProtectionProvider>());

        Assert.Contains("appsurface-dev-auth-marker", html, StringComparison.Ordinal);
        Assert.Contains("data-appsurface-dev-auth=\"marker\"", html, StringComparison.Ordinal);
        Assert.Contains("DEV AUTH", html, StringComparison.Ordinal);
        Assert.Contains("Anonymous", html, StringComparison.Ordinal);
        Assert.Contains("<details class=\"appsurface-dev-auth-marker__details\">", html, StringComparison.Ordinal);
        Assert.Contains("<summary class=\"appsurface-dev-auth-marker__summary\">", html, StringComparison.Ordinal);
        Assert.Contains("/_appsurface/dev-auth/select/admin?returnUrl=%2Fdashboard%3Ftab%3Dauth", html, StringComparison.Ordinal);
        Assert.Contains("/_appsurface/dev-auth/select/viewer?returnUrl=%2Fdashboard%3Ftab%3Dauth", html, StringComparison.Ordinal);
        Assert.Contains("/_appsurface/dev-auth/clear?returnUrl=%2Fdashboard%3Ftab%3Dauth", html, StringComparison.Ordinal);
        Assert.Contains("Open persona lab", html, StringComparison.Ordinal);
        Assert.Contains("Status JSON", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Marker_WithUnsafeConfiguredReturnUrl_NormalizesMutationActionsToRoot()
    {
        using var app = BuildApp();
        var context = CreateContext(app.Services);

        var html = AppSurfaceDevAuthMarker.Render(
            context,
            app.Services.GetRequiredService<IHostEnvironment>(),
            app.Services.GetRequiredService<IOptions<AppSurfaceDevAuthOptions>>(),
            app.Services.GetRequiredService<IDataProtectionProvider>(),
            options => options.ReturnUrl = "https://example.com/");

        Assert.Contains("/_appsurface/dev-auth/select/admin?returnUrl=%2F", html, StringComparison.Ordinal);
        Assert.Contains("/_appsurface/dev-auth/select/viewer?returnUrl=%2F", html, StringComparison.Ordinal);
        Assert.Contains("/_appsurface/dev-auth/clear?returnUrl=%2F", html, StringComparison.Ordinal);
        Assert.DoesNotContain("example.com", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Marker_InUnallowedEnvironment_ReturnsEmpty()
    {
        using var app = BuildApp();
        var context = CreateContext(app.Services);

        var html = AppSurfaceDevAuthMarker.Render(
            context,
            new TestHostEnvironment("Staging"),
            app.Services.GetRequiredService<IOptions<AppSurfaceDevAuthOptions>>(),
            app.Services.GetRequiredService<IDataProtectionProvider>());

        Assert.Equal(string.Empty, html);
    }

    [Fact]
    public void Marker_InConfiguredStaging_RendersPersistentOverlay()
    {
        using var app = BuildApp();
        var context = CreateContext(app.Services);
        var options = CreateOptions(devAuthOptions =>
        {
            devAuthOptions.AllowedEnvironmentNames.Add("Staging");
            AddDefaultPersonas(devAuthOptions);
        });

        var html = AppSurfaceDevAuthMarker.Render(
            context,
            new TestHostEnvironment("Staging"),
            Options.Create(options),
            app.Services.GetRequiredService<IDataProtectionProvider>());

        Assert.Contains("appsurface-dev-auth-marker", html, StringComparison.Ordinal);
        Assert.Contains("DEV AUTH", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusEndpoint_InUnallowedEnvironment_ReturnsDisabledContract()
    {
        await using var app = BuildApp();
        using var services = BuildEndpointServices("Staging", AddDefaultPersonas);
        var endpoint = FindEndpoint(app, "/_appsurface/dev-auth/status", HttpMethods.Get);
        var context = CreateContext(services);

        await endpoint.RequestDelegate!(context);

        var json = await ReadBodyAsync(context);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.False(root.GetProperty("enabled").GetBoolean());
        Assert.Equal("Staging", root.GetProperty("environment").GetString());
        Assert.Equal("no-store, no-cache", context.Response.Headers.CacheControl);
    }

    [Fact]
    public async Task ControlPage_InUnallowedEnvironment_ReturnsNotFoundWithoutControls()
    {
        await using var app = BuildApp();
        using var services = BuildEndpointServices("Staging", AddDefaultPersonas);
        var endpoint = FindEndpoint(app, "/_appsurface/dev-auth/", HttpMethods.Get);
        var context = CreateContext(services);

        await endpoint.RequestDelegate!(context);

        var html = await ReadBodyAsync(context);

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.DoesNotContain("Select persona", html, StringComparison.Ordinal);
        Assert.DoesNotContain("AppSurface Dev Auth", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SelectPersona_InUnallowedEnvironment_ReturnsNotFoundWithoutCookieMutation()
    {
        await using var app = BuildApp();
        using var services = BuildEndpointServices("Staging", AddDefaultPersonas);
        var endpoint = FindEndpoint(app, "/_appsurface/dev-auth/select/{personaId}", HttpMethods.Post);
        var context = CreateContext(services);
        context.Request.RouteValues["personaId"] = "admin";

        await endpoint.RequestDelegate!(context);

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.DoesNotContain(AppSurfaceDevAuthDefaults.CookieName, context.Response.Headers.SetCookie.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClearPersona_InUnallowedEnvironment_ReturnsNotFoundWithoutCookieMutation()
    {
        await using var app = BuildApp();
        using var services = BuildEndpointServices("Staging", AddDefaultPersonas);
        var endpoint = FindEndpoint(app, "/_appsurface/dev-auth/clear", HttpMethods.Post);
        var context = CreateContext(services);

        await endpoint.RequestDelegate!(context);

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.DoesNotContain(AppSurfaceDevAuthDefaults.CookieName, context.Response.Headers.SetCookie.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Marker_RendersSelectedPersonaWithoutSensitiveClaims()
    {
        using var app = BuildApp();
        var context = CreateContext(app.Services);
        context.Request.Headers.Cookie = $"{AppSurfaceDevAuthDefaults.CookieName}={ProtectPersonaId(app.Services, "admin")}";

        var html = AppSurfaceDevAuthMarker.Render(
            context,
            app.Services.GetRequiredService<IHostEnvironment>(),
            app.Services.GetRequiredService<IOptions<AppSurfaceDevAuthOptions>>(),
            app.Services.GetRequiredService<IDataProtectionProvider>());

        Assert.Contains("Local Admin", html, StringComparison.Ordinal);
        Assert.Contains("admin-1", html, StringComparison.Ordinal);
        Assert.Contains("aria-current=\"true\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("admin@example.com", html, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Marker_AllowsConsumerSkinningWithoutInlineStyles()
    {
        using var app = BuildApp();
        var context = CreateContext(app.Services);

        var html = AppSurfaceDevAuthMarker.Render(
            context,
            app.Services.GetRequiredService<IHostEnvironment>(),
            app.Services.GetRequiredService<IOptions<AppSurfaceDevAuthOptions>>(),
            app.Services.GetRequiredService<IDataProtectionProvider>(),
            options =>
            {
                options.CssClassPrefix = "demo-dev-auth";
                options.AdditionalCssClass = "theme-local";
                options.IncludeDefaultStyles = false;
            });

        Assert.Contains("class=\"demo-dev-auth theme-local\"", html, StringComparison.Ordinal);
        Assert.Contains("demo-dev-auth__button", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<style>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Marker_WithStartExpanded_RendersOpenDisclosure()
    {
        using var app = BuildApp();
        var context = CreateContext(app.Services);

        var html = AppSurfaceDevAuthMarker.Render(
            context,
            app.Services.GetRequiredService<IHostEnvironment>(),
            app.Services.GetRequiredService<IOptions<AppSurfaceDevAuthOptions>>(),
            app.Services.GetRequiredService<IDataProtectionProvider>(),
            options =>
            {
                options.StartExpanded = true;
            });

        Assert.Contains("<details class=\"appsurface-dev-auth-marker__details\" open>", html, StringComparison.Ordinal);
        Assert.Contains("Open persona lab", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Marker_WithCustomReturnUrlAndHiddenControls_RendersStateOnly()
    {
        using var app = BuildApp();
        var context = CreateContext(app.Services);

        var html = AppSurfaceDevAuthMarker.Render(
            context,
            app.Services.GetRequiredService<IHostEnvironment>(),
            app.Services.GetRequiredService<IOptions<AppSurfaceDevAuthOptions>>(),
            app.Services.GetRequiredService<IDataProtectionProvider>(),
            options =>
            {
                options.ReturnUrl = "/custom-proof?tab=auth";
                options.ShowPersonaControls = false;
            });

        Assert.Contains("DEV AUTH", html, StringComparison.Ordinal);
        Assert.Contains("Open persona lab", html, StringComparison.Ordinal);
        Assert.DoesNotContain("returnUrl=", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Select DevAuth persona", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Marker_WithUnsafeCssClassOptions_FallsBackAndSanitizesAdditionalClasses()
    {
        using var app = BuildApp();
        var context = CreateContext(app.Services);

        var html = AppSurfaceDevAuthMarker.Render(
            context,
            app.Services.GetRequiredService<IHostEnvironment>(),
            app.Services.GetRequiredService<IOptions<AppSurfaceDevAuthOptions>>(),
            app.Services.GetRequiredService<IDataProtectionProvider>(),
            options =>
            {
                options.CssClassPrefix = "<>";
                options.AdditionalCssClass = "safe <bad> _ok";
                options.IncludeDefaultStyles = false;
            });

        Assert.Contains("class=\"appsurface-dev-auth-marker safe bad _ok\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<bad>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<style>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SelectPersona_UsesPostOnlyEndpointAndProtectedCookieAuthenticatesNamedScheme()
    {
        await using var app = BuildApp();
        var endpoint = FindEndpoint(app, "/_appsurface/dev-auth/select/{personaId}", HttpMethods.Post);
        var selectContext = CreateContext(app.Services);
        selectContext.Request.RouteValues["personaId"] = "admin";

        await endpoint.RequestDelegate!(selectContext);

        var setCookie = selectContext.Response.Headers.SetCookie.ToString();
        var html = await ReadBodyAsync(selectContext);
        Assert.Contains(AppSurfaceDevAuthDefaults.CookieName, setCookie, StringComparison.Ordinal);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secure", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("role", html, StringComparison.Ordinal);
        Assert.Contains("operator", html, StringComparison.Ordinal);
        Assert.Contains("claim(s) hidden from preview", html, StringComparison.Ordinal);
        Assert.DoesNotContain("admin@example.com", html, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token", html, StringComparison.Ordinal);

        var cookie = setCookie.Split(';', 2)[0];
        using var scope = app.Services.CreateScope();
        var authContext = CreateContext(scope.ServiceProvider);
        authContext.Request.Headers.Cookie = cookie;

        var result = await scope.ServiceProvider.GetRequiredService<IAuthenticationService>()
            .AuthenticateAsync(authContext, AppSurfaceDevAuthDefaults.AuthenticationScheme);

        Assert.True(result.Succeeded);
        Assert.Equal("admin-1", result.Principal?.FindFirst("sub")?.Value);
        Assert.Equal("operator", result.Principal?.FindFirst("role")?.Value);
    }

    [Fact]
    public async Task SelectPersona_WithSafeReturnUrl_RedirectsBackToHostPage()
    {
        await using var app = BuildApp();
        var endpoint = FindEndpoint(app, "/_appsurface/dev-auth/select/{personaId}", HttpMethods.Post);
        var selectContext = CreateContext(app.Services);
        selectContext.Request.Method = HttpMethods.Post;
        selectContext.Request.RouteValues["personaId"] = "admin";
        selectContext.Request.QueryString = new QueryString("?returnUrl=%2Fdashboard%3Ftab%3Dauth");

        await endpoint.RequestDelegate!(selectContext);

        Assert.Equal(StatusCodes.Status302Found, selectContext.Response.StatusCode);
        Assert.Equal("/dashboard?tab=auth", selectContext.Response.Headers.Location);
        Assert.Contains(AppSurfaceDevAuthDefaults.CookieName, selectContext.Response.Headers.SetCookie.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SelectPersona_WithCrossOriginBrowserPost_RejectsWithoutCookie()
    {
        await using var app = BuildApp();
        var endpoint = FindEndpoint(app, "/_appsurface/dev-auth/select/{personaId}", HttpMethods.Post);
        var context = CreateContext(app.Services);
        context.Request.Method = HttpMethods.Post;
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("127.0.0.1", 5058);
        context.Request.Headers["Origin"] = "https://evil.example";
        context.Request.RouteValues["personaId"] = "admin";

        await endpoint.RequestDelegate!(context);

        var body = await ReadBodyAsync(context);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.Contains("AppSurface DevAuth same-origin request required", body, StringComparison.Ordinal);
        Assert.True(context.Response.Headers.SetCookie.Count == 0, "Cross-origin persona selection must not set a cookie.");
    }

    [Fact]
    public async Task SelectPersona_WithSameOriginBrowserPost_AllowsCookie()
    {
        await using var app = BuildApp();
        var endpoint = FindEndpoint(app, "/_appsurface/dev-auth/select/{personaId}", HttpMethods.Post);
        var context = CreateContext(app.Services);
        context.Request.Method = HttpMethods.Post;
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("127.0.0.1", 5058);
        context.Request.Headers["Origin"] = "http://127.0.0.1:5058";
        context.Request.RouteValues["personaId"] = "admin";

        await endpoint.RequestDelegate!(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Contains(AppSurfaceDevAuthDefaults.CookieName, context.Response.Headers.SetCookie.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SelectPersona_WithExternalReturnUrl_RendersControlPageInsteadOfRedirecting()
    {
        await using var app = BuildApp();
        var endpoint = FindEndpoint(app, "/_appsurface/dev-auth/select/{personaId}", HttpMethods.Post);
        var selectContext = CreateContext(app.Services);
        selectContext.Request.RouteValues["personaId"] = "admin";
        selectContext.Request.QueryString = new QueryString("?returnUrl=https%3A%2F%2Fexample.com%2F");

        await endpoint.RequestDelegate!(selectContext);

        var html = await ReadBodyAsync(selectContext);
        Assert.Equal(StatusCodes.Status200OK, selectContext.Response.StatusCode);
        Assert.True(selectContext.Response.Headers.Location.Count == 0, "External return URLs must not redirect.");
        Assert.Contains("Local Admin", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SelectPersona_OnHttps_SetsSecureCookie()
    {
        await using var app = BuildApp();
        var endpoint = FindEndpoint(app, "/_appsurface/dev-auth/select/{personaId}", HttpMethods.Post);
        var selectContext = CreateContext(app.Services);
        selectContext.Request.Scheme = "https";
        selectContext.Request.RouteValues["personaId"] = "admin";

        await endpoint.RequestDelegate!(selectContext);

        var setCookie = selectContext.Response.Headers.SetCookie.ToString();
        Assert.Contains("secure", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SelectPersona_WithRouteUnsafePersonaId_ReturnsSafeDiagnostic()
    {
        await using var app = BuildApp();
        var endpoint = FindEndpoint(app, "/_appsurface/dev-auth/select/{personaId}", HttpMethods.Post);
        var context = CreateContext(app.Services);
        context.Request.RouteValues["personaId"] = "admin+viewer";

        await endpoint.RequestDelegate!(context);

        var body = await ReadBodyAsync(context);
        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Contains(AppSurfaceDevAuthDiagnostics.InvalidPersonaId, body, StringComparison.Ordinal);
        Assert.DoesNotContain("admin+viewer", body, StringComparison.Ordinal);
        Assert.True(context.Response.Headers.SetCookie.Count == 0, "Invalid persona selection must not set a cookie.");
    }

    [Fact]
    public async Task SelectPersona_WithUnknownRouteSafePersonaId_ReturnsSafeDiagnostic()
    {
        await using var app = BuildApp();
        var endpoint = FindEndpoint(app, "/_appsurface/dev-auth/select/{personaId}", HttpMethods.Post);
        var context = CreateContext(app.Services);
        context.Request.RouteValues["personaId"] = "ghost";

        await endpoint.RequestDelegate!(context);

        var body = await ReadBodyAsync(context);
        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Contains(AppSurfaceDevAuthDiagnostics.InvalidPersonaId, body, StringComparison.Ordinal);
        Assert.DoesNotContain("ghost", body, StringComparison.Ordinal);
        Assert.True(context.Response.Headers.SetCookie.Count == 0, "Unknown persona selection must not set a cookie.");
    }

    [Theory]
    [InlineData("secret-token")]
    [InlineData("api-key")]
    [InlineData("admin-email")]
    public async Task SelectPersona_WithSensitivePersonaId_ReturnsSafeDiagnostic(string personaId)
    {
        await using var app = BuildApp();
        var endpoint = FindEndpoint(app, "/_appsurface/dev-auth/select/{personaId}", HttpMethods.Post);
        var context = CreateContext(app.Services);
        context.Request.RouteValues["personaId"] = personaId;

        await endpoint.RequestDelegate!(context);

        var body = await ReadBodyAsync(context);
        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Contains(AppSurfaceDevAuthDiagnostics.InvalidPersonaId, body, StringComparison.Ordinal);
        Assert.DoesNotContain(personaId, body, StringComparison.Ordinal);
        Assert.True(context.Response.Headers.SetCookie.Count == 0, "Sensitive persona selection must not set a cookie.");
    }

    [Fact]
    public async Task SelectPersona_WithBlankPersonaId_ReturnsSafeDiagnostic()
    {
        await using var app = BuildApp();
        var endpoint = FindEndpoint(app, "/_appsurface/dev-auth/select/{personaId}", HttpMethods.Post);
        var context = CreateContext(app.Services);
        context.Request.RouteValues["personaId"] = " ";

        await endpoint.RequestDelegate!(context);

        var body = await ReadBodyAsync(context);
        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Contains(AppSurfaceDevAuthDiagnostics.InvalidPersonaId, body, StringComparison.Ordinal);
        Assert.True(context.Response.Headers.SetCookie.Count == 0, "Blank persona selection must not set a cookie.");
    }

    [Fact]
    public async Task SelectPersona_WithPaddedPersonaId_ReturnsSafeDiagnostic()
    {
        await using var app = BuildApp();
        var endpoint = FindEndpoint(app, "/_appsurface/dev-auth/select/{personaId}", HttpMethods.Post);
        var context = CreateContext(app.Services);
        context.Request.RouteValues["personaId"] = " admin ";

        await endpoint.RequestDelegate!(context);

        var body = await ReadBodyAsync(context);
        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Contains(AppSurfaceDevAuthDiagnostics.InvalidPersonaId, body, StringComparison.Ordinal);
        Assert.DoesNotContain("Local Admin", body, StringComparison.Ordinal);
        Assert.True(context.Response.Headers.SetCookie.Count == 0, "Padded persona selection must not set a cookie.");
    }

    [Fact]
    public async Task TamperedCookie_AuthenticatesAsNoResultAndStatusWarns()
    {
        await using var app = BuildApp();
        using var scope = app.Services.CreateScope();
        var authContext = CreateContext(scope.ServiceProvider);
        authContext.Request.Headers.Cookie = $"{AppSurfaceDevAuthDefaults.CookieName}=tampered";

        var result = await scope.ServiceProvider.GetRequiredService<IAuthenticationService>()
            .AuthenticateAsync(authContext, AppSurfaceDevAuthDefaults.AuthenticationScheme);

        Assert.False(result.Succeeded);
        Assert.Null(result.Principal);

        var endpoint = FindEndpoint(app, "/_appsurface/dev-auth/status", HttpMethods.Get);
        var statusContext = CreateContext(app.Services);
        statusContext.Request.Headers.Cookie = $"{AppSurfaceDevAuthDefaults.CookieName}=tampered";

        await endpoint.RequestDelegate!(statusContext);

        var json = await ReadBodyAsync(statusContext);
        Assert.Contains(AppSurfaceDevAuthDiagnostics.InvalidPersonaId, json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingPersonaCookie_AuthenticatesAsNoResult()
    {
        await using var app = BuildApp();
        using var scope = app.Services.CreateScope();
        var authContext = CreateContext(scope.ServiceProvider);

        var result = await scope.ServiceProvider.GetRequiredService<IAuthenticationService>()
            .AuthenticateAsync(authContext, AppSurfaceDevAuthDefaults.AuthenticationScheme);

        Assert.False(result.Succeeded);
        Assert.Null(result.Principal);
    }

    [Fact]
    public async Task StaleProtectedPersonaCookie_AuthenticatesAsNoResultAndControlPageWarns()
    {
        await using var app = BuildApp();
        var staleCookie = ProtectPersonaId(app.Services, "ghost");

        using var scope = app.Services.CreateScope();
        var authContext = CreateContext(scope.ServiceProvider);
        authContext.Request.Headers.Cookie = $"{AppSurfaceDevAuthDefaults.CookieName}={staleCookie}";

        var result = await scope.ServiceProvider.GetRequiredService<IAuthenticationService>()
            .AuthenticateAsync(authContext, AppSurfaceDevAuthDefaults.AuthenticationScheme);

        Assert.False(result.Succeeded);
        Assert.Null(result.Principal);

        var controlEndpoint = FindEndpoint(app, "/_appsurface/dev-auth/", HttpMethods.Get);
        var controlContext = CreateContext(app.Services);
        controlContext.Request.Headers.Cookie = $"{AppSurfaceDevAuthDefaults.CookieName}={staleCookie}";

        await controlEndpoint.RequestDelegate!(controlContext);

        var html = await ReadBodyAsync(controlContext);
        Assert.Contains("Status warnings", html, StringComparison.Ordinal);
        Assert.Contains(AppSurfaceDevAuthDiagnostics.InvalidPersonaId, html, StringComparison.Ordinal);
        Assert.DoesNotContain("ghost", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClearPersona_DeletesSecureCookieAndRendersAnonymousState()
    {
        await using var app = BuildApp();
        var endpoint = FindEndpoint(app, "/_appsurface/dev-auth/clear", HttpMethods.Post);
        var context = CreateContext(app.Services);

        await endpoint.RequestDelegate!(context);

        var setCookie = context.Response.Headers.SetCookie.ToString();
        var html = await ReadBodyAsync(context);
        Assert.Contains(AppSurfaceDevAuthDefaults.CookieName, setCookie, StringComparison.Ordinal);
        Assert.Contains("expires=Thu, 01 Jan 1970", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secure", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DEV AUTH: Anonymous (AppSurface.DevAuth)", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClearPersona_WithSafeReturnUrl_RedirectsBackToHostPage()
    {
        await using var app = BuildApp();
        var endpoint = FindEndpoint(app, "/_appsurface/dev-auth/clear", HttpMethods.Post);
        var context = CreateContext(app.Services);
        context.Request.Method = HttpMethods.Post;
        context.Request.QueryString = new QueryString("?returnUrl=%2F");

        await endpoint.RequestDelegate!(context);

        Assert.Equal(StatusCodes.Status302Found, context.Response.StatusCode);
        Assert.Equal("/", context.Response.Headers.Location);
        Assert.Contains(AppSurfaceDevAuthDefaults.CookieName, context.Response.Headers.SetCookie.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClearPersona_WithExternalReturnUrl_RendersControlPageInsteadOfRedirecting()
    {
        await using var app = BuildApp();
        var endpoint = FindEndpoint(app, "/_appsurface/dev-auth/clear", HttpMethods.Post);
        var context = CreateContext(app.Services);
        context.Request.QueryString = new QueryString("?returnUrl=https%3A%2F%2Fexample.com%2F");

        await endpoint.RequestDelegate!(context);

        var html = await ReadBodyAsync(context);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.True(context.Response.Headers.Location.Count == 0, "External return URLs must not redirect.");
        Assert.Contains("DEV AUTH: Anonymous (AppSurface.DevAuth)", html, StringComparison.Ordinal);
        Assert.DoesNotContain("returnUrl=", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClearPersona_WithCrossSiteFetchMetadata_RejectsWithoutCookieMutation()
    {
        await using var app = BuildApp();
        var endpoint = FindEndpoint(app, "/_appsurface/dev-auth/clear", HttpMethods.Post);
        var context = CreateContext(app.Services);
        context.Request.Method = HttpMethods.Post;
        context.Request.Headers["Sec-Fetch-Site"] = "cross-site";

        await endpoint.RequestDelegate!(context);

        var body = await ReadBodyAsync(context);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.Contains("AppSurface DevAuth same-origin request required", body, StringComparison.Ordinal);
        Assert.True(context.Response.Headers.SetCookie.Count == 0, "Cross-site clear must not mutate the persona cookie.");
    }

    [Fact]
    public async Task ControlEndpoints_RejectNonLoopbackRequests()
    {
        await using var app = BuildApp();
        var endpoint = FindEndpoint(app, "/_appsurface/dev-auth/status", HttpMethods.Get);
        var context = CreateContext(app.Services);
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.10");

        await endpoint.RequestDelegate!(context);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        var body = await ReadBodyAsync(context);
        Assert.Contains("AppSurface DevAuth local request required", body, StringComparison.Ordinal);
        Assert.DoesNotContain(AppSurfaceDevAuthDiagnostics.NonDevelopmentEnvironment, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ControlEndpoints_RejectRequestsWithUnknownRemoteAddress()
    {
        await using var app = BuildApp();
        var endpoint = FindEndpoint(app, "/_appsurface/dev-auth/status", HttpMethods.Get);
        var context = CreateContext(app.Services);
        context.Connection.RemoteIpAddress = null;

        await endpoint.RequestDelegate!(context);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task ControlEndpoints_WhenLoopbackRequirementDisabled_AllowsNonLoopbackRequests()
    {
        await using var app = BuildApp(options =>
        {
            options.RequireLoopbackControlRequests = false;
            AddDefaultPersonas(options);
        });
        var endpoint = FindEndpoint(app, "/_appsurface/dev-auth/status", HttpMethods.Get);
        var context = CreateContext(app.Services);
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.10");

        await endpoint.RequestDelegate!(context);

        var body = await ReadBodyAsync(context);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Contains("\"enabled\":true", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ControlAndStatus_RedactSensitiveDisplayNameAndSubject()
    {
        await using var app = BuildApp(options =>
        {
            options.DisplayClaimTypes.Add("passwordHash");
            options.DisplayClaimTypes.Add("apiKey");
            options.DisplayClaimTypes.Add("accessToken");
            options.Users.Add(
                "sensitive",
                user => user
                    .DisplayName("admin@example.com")
                    .Subject("secret-token")
                    .Claim("role", "operator")
                    .Claim("passwordHash", "hash-for-local-proof")
                    .Claim("apiKey", "local-api-key")
                    .Claim("accessToken", "local-access-token"));
        });
        var controlEndpoint = FindEndpoint(app, "/_appsurface/dev-auth/", HttpMethods.Get);
        var controlContext = CreateContext(app.Services);

        await controlEndpoint.RequestDelegate!(controlContext);

        var controlHtml = await ReadBodyAsync(controlContext);
        Assert.DoesNotContain("admin@example.com", controlHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token", controlHtml, StringComparison.Ordinal);
        Assert.Contains("(hidden)", controlHtml, StringComparison.Ordinal);

        var selectEndpoint = FindEndpoint(app, "/_appsurface/dev-auth/select/{personaId}", HttpMethods.Post);
        var selectContext = CreateContext(app.Services);
        selectContext.Request.RouteValues["personaId"] = "sensitive";

        await selectEndpoint.RequestDelegate!(selectContext);

        var selectedHtml = await ReadBodyAsync(selectContext);
        Assert.DoesNotContain("admin@example.com", selectedHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token", selectedHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("passwordHash", selectedHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("hash-for-local-proof", selectedHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("apiKey", selectedHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("local-api-key", selectedHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("accessToken", selectedHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("local-access-token", selectedHtml, StringComparison.Ordinal);
        Assert.Contains("(hidden)", selectedHtml, StringComparison.Ordinal);

        var statusEndpoint = FindEndpoint(app, "/_appsurface/dev-auth/status", HttpMethods.Get);
        var statusContext = CreateContext(app.Services);
        statusContext.Request.Headers.Cookie = selectContext.Response.Headers.SetCookie.ToString().Split(';', 2)[0];

        await statusEndpoint.RequestDelegate!(statusContext);

        var statusJson = await ReadBodyAsync(statusContext);
        Assert.DoesNotContain("admin@example.com", statusJson, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token", statusJson, StringComparison.Ordinal);
        Assert.Contains("\"personaId\":\"sensitive\"", statusJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ControlPage_WithOnlyHiddenClaims_RendersHiddenCountWithoutClaimList()
    {
        await using var app = BuildApp(options =>
        {
            options.Users.Add(
                "hidden",
                user => user
                    .DisplayName("Hidden Local")
                    .Subject("secret-token")
                    .Claim("email", "hidden@example.com"));
        });
        var selectEndpoint = FindEndpoint(app, "/_appsurface/dev-auth/select/{personaId}", HttpMethods.Post);
        var selectContext = CreateContext(app.Services);
        selectContext.Request.RouteValues["personaId"] = "hidden";

        await selectEndpoint.RequestDelegate!(selectContext);

        var html = await ReadBodyAsync(selectContext);
        Assert.Contains("claim(s) hidden from preview", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<dl>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token", html, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden@example.com", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ControlPage_WithOnlyDisplaySafeClaims_DoesNotRenderHiddenCount()
    {
        await using var app = BuildApp(options =>
        {
            options.Users.Add(
                "safe",
                user => user
                    .DisplayName("Safe Local")
                    .Subject("safe-1")
                    .Claim("role", "operator"));
        });
        var selectEndpoint = FindEndpoint(app, "/_appsurface/dev-auth/select/{personaId}", HttpMethods.Post);
        var selectContext = CreateContext(app.Services);
        selectContext.Request.RouteValues["personaId"] = "safe";

        await selectEndpoint.RequestDelegate!(selectContext);

        var html = await ReadBodyAsync(selectContext);
        Assert.Contains("<dl>", html, StringComparison.Ordinal);
        Assert.Contains("safe-1", html, StringComparison.Ordinal);
        Assert.Contains("operator", html, StringComparison.Ordinal);
        Assert.DoesNotContain("claim(s) hidden from preview", html, StringComparison.Ordinal);
    }

    [Fact]
    public void MapAppSurfaceDevAuth_WithEquivalentParameterNameReservedPathConflict_ThrowsSafeDiagnostic()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.Services.AddAppSurfaceDevAuth(builder.Environment, options =>
            options.Users.Add("admin", user => user.Subject("admin-1")));
        var app = builder.Build();
        app.MapPost("/_appsurface/dev-auth/select/{id}", () => Results.Ok());

        var ex = Assert.Throws<AppSurfaceDevAuthException>(() => app.MapAppSurfaceDevAuth());

        Assert.Equal(AppSurfaceDevAuthDiagnostics.ReservedPathConflict, ex.DiagnosticCode);
        Assert.Contains("ASDEV005 Problem:", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MapAppSurfaceDevAuth_WithReservedPathConflict_ThrowsSafeDiagnostic()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.Services.AddAppSurfaceDevAuth(builder.Environment, options =>
            options.Users.Add("admin", user => user.Subject("admin-1")));
        var app = builder.Build();
        app.MapGet("/_appsurface/dev-auth/status", () => Results.Ok());

        var ex = Assert.Throws<AppSurfaceDevAuthException>(() => app.MapAppSurfaceDevAuth());

        Assert.Equal(AppSurfaceDevAuthDiagnostics.ReservedPathConflict, ex.DiagnosticCode);
        Assert.Contains("ASDEV005 Problem:", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MapAppSurfaceDevAuth_WithReservedPathConflictWithoutLeadingSlash_ThrowsSafeDiagnostic()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.Services.AddAppSurfaceDevAuth(builder.Environment, options =>
            options.Users.Add("admin", user => user.Subject("admin-1")));
        var app = builder.Build();
        app.MapGet("_appsurface/dev-auth/status", () => Results.Ok());

        var ex = Assert.Throws<AppSurfaceDevAuthException>(() => app.MapAppSurfaceDevAuth());

        Assert.Equal(AppSurfaceDevAuthDiagnostics.ReservedPathConflict, ex.DiagnosticCode);
        Assert.Contains("ASDEV005 Problem:", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NamedSchemePolicy_FlowsThroughRequireSurfacePolicy()
    {
        await using var app = BuildApp(mapProofEndpoint: true);
        var selectEndpoint = FindEndpoint(app, "/_appsurface/dev-auth/select/{personaId}", HttpMethods.Post);
        var selectContext = CreateContext(app.Services);
        selectContext.Request.RouteValues["personaId"] = "admin";
        await selectEndpoint.RequestDelegate!(selectContext);
        var cookie = selectContext.Response.Headers.SetCookie.ToString().Split(';', 2)[0];

        var proofEndpoint = FindEndpoint(app, "/api/auth-proof", HttpMethods.Get);
        using var scope = app.Services.CreateScope();
        var proofContext = CreateContext(scope.ServiceProvider);
        proofContext.Request.Headers.Cookie = cookie;
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = proofContext;
        proofContext.User = (await scope.ServiceProvider.GetRequiredService<IAuthenticationService>()
            .AuthenticateAsync(proofContext, AppSurfaceDevAuthDefaults.AuthenticationScheme)).Principal!;

        await proofEndpoint.RequestDelegate!(proofContext);

        var body = await ReadBodyAsync(proofContext);
        Assert.True(body.Contains("allowed", StringComparison.Ordinal), body);
    }

    private static WebApplication BuildApp(bool mapProofEndpoint = false)
    {
        return BuildApp(AddDefaultPersonas, mapProofEndpoint);
    }

    private static WebApplication BuildApp(Action<AppSurfaceDevAuthOptions> configureDevAuth, bool mapProofEndpoint = false)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });

        builder.Services.AddRouting();
        builder.Services.AddAuthorization(options =>
        {
            options.AddAppSurfacePolicy(
                "OperatorsOnly",
                policy => policy
                    .AddAuthenticationSchemes(AppSurfaceDevAuthDefaults.AuthenticationScheme)
                    .RequireAuthenticatedUser()
                    .RequireClaim("role", "operator"));
        });
        builder.Services.AddAppSurfaceAspNetCoreAuth(options => options.MapSubjectClaim("sub"));
        builder.Services.AddAppSurfaceDevAuth(builder.Environment, configureDevAuth);

        var app = builder.Build();
        app.MapAppSurfaceDevAuth();
        if (mapProofEndpoint)
        {
            app.MapGet("/api/auth-proof", () => Results.Text("allowed"))
                .RequireSurfacePolicy("OperatorsOnly");
        }

        return app;
    }

    private static void AddDefaultPersonas(AppSurfaceDevAuthOptions options)
    {
        options.Users.Add(
            "admin",
            user => user
                .DisplayName("Local Admin")
                .Subject("admin-1")
                .Claim("role", "operator")
                .Claim("email", "admin@example.com")
                .Claim("access_token", "secret-token"));
        options.Users.Add(
            "viewer",
            user => user
                .DisplayName("Local Viewer")
                .Subject("viewer-1")
                .Claim("role", "viewer"));
    }

    private static AppSurfaceDevAuthOptions CreateOptions(Action<AppSurfaceDevAuthOptions> configure)
    {
        var options = new AppSurfaceDevAuthOptions();
        configure(options);
        return options;
    }

    private static ServiceProvider BuildEndpointServices(
        string environmentName,
        Action<AppSurfaceDevAuthOptions> configureDevAuth)
    {
        var services = new ServiceCollection();
        services.AddDataProtection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment(environmentName));
        services.AddSingleton<IOptions<AppSurfaceDevAuthOptions>>(Options.Create(CreateOptions(configureDevAuth)));
        return services.BuildServiceProvider();
    }

    private static RouteEndpoint FindEndpoint(WebApplication app, string pattern, string method)
    {
        return ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(dataSource => dataSource.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(endpoint =>
                string.Equals(endpoint.RoutePattern.RawText, pattern, StringComparison.Ordinal) &&
                endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains(method) == true);
    }

    private static DefaultHttpContext CreateContext(IServiceProvider services)
    {
        return new DefaultHttpContext
        {
            RequestServices = services,
            Response =
            {
                Body = new MemoryStream(),
            },
            Connection =
            {
                RemoteIpAddress = IPAddress.Loopback,
            },
        };
    }

    private static string ProtectPersonaId(IServiceProvider services, string personaId)
    {
        return services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth.Persona.v1")
            .Protect(personaId);
    }

    private static async Task<string> ReadBodyAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    private static void AssertBareMutationActions(string html, string pathPrefix)
    {
        Assert.Contains($"action=\"{pathPrefix}/select/admin\"", html, StringComparison.Ordinal);
        Assert.Contains($"action=\"{pathPrefix}/select/viewer\"", html, StringComparison.Ordinal);
        Assert.Contains($"action=\"{pathPrefix}/clear\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("returnUrl=", html, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string value, string searchValue)
    {
        var count = 0;
        var startIndex = 0;
        while ((startIndex = value.IndexOf(searchValue, startIndex, StringComparison.Ordinal)) >= 0)
        {
            count++;
            startIndex += searchValue.Length;
        }

        return count;
    }
}
