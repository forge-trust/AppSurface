using System.Net;
using System.Security.Claims;
using System.Text.Json;
using ForgeTrust.AppSurface.Auth.AspNetCore;
using ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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

        Assert.Contains("AppSurface Dev Auth [DEVELOPMENT ONLY]", html, StringComparison.Ordinal);
        Assert.Contains("Select persona", html, StringComparison.Ordinal);
        Assert.Contains("Clear persona", html, StringComparison.Ordinal);
        Assert.Contains("DEV AUTH: Anonymous (AppSurface.DevAuth)", html, StringComparison.Ordinal);
        Assert.DoesNotContain("login", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("logout", html, StringComparison.OrdinalIgnoreCase);
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
    public async Task ControlAndStatus_RedactSensitiveDisplayNameAndSubject()
    {
        await using var app = BuildApp(options =>
        {
            options.Users.Add(
                "sensitive",
                user => user
                    .DisplayName("admin@example.com")
                    .Subject("secret-token")
                    .Claim("role", "operator"));
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

    private static async Task<string> ReadBodyAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }
}
