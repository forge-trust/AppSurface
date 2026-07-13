using ForgeTrust.AppSurface.Auth.AspNetCore.Oidc;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = AppSurfaceOidcAuthOptions.DefaultCookieScheme;
    options.DefaultChallengeScheme = AppSurfaceOidcAuthOptions.DefaultOidcScheme;
});
builder.Services.AddAppSurfaceOidcAuth(options =>
{
    options.RequireClientSecret = builder.Configuration.GetValue("Authentication:Oidc:RequireClientSecret", false);
    options.CallbackPath = builder.Configuration["Authentication:Oidc:CallbackPath"] ?? AppSurfaceOidcAuthOptions.DefaultCallbackPath;
    options.SignedOutCallbackPath = builder.Configuration["Authentication:Oidc:SignedOutCallbackPath"] ?? AppSurfaceOidcAuthOptions.DefaultSignedOutCallbackPath;
    options.ConfigureOpenIdConnect(oidc =>
    {
        oidc.Authority = builder.Configuration["Authentication:Oidc:Authority"] ?? "http://localhost:8080/realms/appsurface-dev";
        oidc.ClientId = builder.Configuration["Authentication:Oidc:ClientId"] ?? "appsurface-web";
        oidc.RequireHttpsMetadata = false;
    });
});
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AppSurfaceAdminProof", policy => policy.RequireClaim("appsurface_role", "admin"));
});

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", (HttpContext httpContext) => Results.Content(RenderHome(httpContext), "text/html"));
app.MapGet("/login", () => Results.Challenge(
    new AuthenticationProperties { RedirectUri = "/auth/proof/result" },
    [AppSurfaceOidcAuthOptions.DefaultOidcScheme]));
app.MapGet("/logout", () => Results.SignOut(
    new AuthenticationProperties { RedirectUri = "/" },
    [AppSurfaceOidcAuthOptions.DefaultCookieScheme, AppSurfaceOidcAuthOptions.DefaultOidcScheme]));
app.MapGet("/auth/proof/status", (HttpContext httpContext) => Results.Json(CreateStatus(httpContext)));
app.MapGet("/auth/proof/protected", [Authorize] (HttpContext httpContext) => Results.Json(CreateStatus(httpContext)));
app.MapGet("/auth/proof/admin", [Authorize(Policy = "AppSurfaceAdminProof")] (HttpContext httpContext) => Results.Json(CreateStatus(httpContext)));
app.MapGet("/auth/proof/result", [Authorize] (HttpContext httpContext) => Results.Content(RenderResult(httpContext), "text/html"));

await app.RunAsync();

static object CreateStatus(HttpContext httpContext)
{
    var user = httpContext.User;
    var claims = user.Claims
        .GroupBy(claim => claim.Type, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.Select(claim => claim.Value).ToArray(), StringComparer.Ordinal);

    return new
    {
        isAuthenticated = user.Identity?.IsAuthenticated == true,
        name = user.Identity?.Name,
        role = user.FindFirst("appsurface_role")?.Value,
        subject = user.FindFirst("sub")?.Value,
        requestUrl = httpContext.Request.GetEncodedUrl(),
        claims,
    };
}

static string RenderHome(HttpContext httpContext)
{
    var isAuthenticated = httpContext.User.Identity?.IsAuthenticated == true;
    var action = isAuthenticated
        ? """<a href="/auth/proof/result">View proof result</a> <a href="/logout">Sign out</a>"""
        : """<a href="/login">Sign in with local Keycloak</a>""";

    return $$"""
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>AppSurface Keycloak proof</title>
          <style>
            body { font-family: system-ui, sans-serif; margin: 3rem; max-width: 52rem; line-height: 1.5; }
            a { color: #0b5cad; margin-right: 1rem; }
            code { background: #f3f4f6; padding: .15rem .3rem; border-radius: .25rem; }
          </style>
        </head>
        <body>
          <h1>AppSurface Keycloak proof</h1>
          <p>This app uses <code>ForgeTrust.AppSurface.Auth.AspNetCore.Oidc</code> against the local Keycloak realm seeded by the AppHost package.</p>
          <p>{{action}}</p>
          <p>Probe <code>/auth/proof/status</code> before login and <code>/auth/proof/protected</code> to see the challenge behavior.</p>
        </body>
        </html>
        """;
}

static string RenderResult(HttpContext httpContext)
{
    var status = CreateStatus(httpContext);
    return $$"""
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>AppSurface Keycloak proof result</title>
          <style>
            body { font-family: system-ui, sans-serif; margin: 3rem; max-width: 52rem; line-height: 1.5; }
            pre { background: #f3f4f6; padding: 1rem; overflow: auto; }
            a { color: #0b5cad; margin-right: 1rem; }
          </style>
        </head>
        <body>
          <h1>Signed in with local Keycloak</h1>
          <p>The proof role is <strong>{{httpContext.User.FindFirst("appsurface_role")?.Value ?? "missing"}}</strong>.</p>
          <p><a href="/auth/proof/admin">Try admin policy JSON</a> <a href="/logout">Sign out</a></p>
          <pre>{{System.Text.Json.JsonSerializer.Serialize(status, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })}}</pre>
        </body>
        </html>
        """;
}
