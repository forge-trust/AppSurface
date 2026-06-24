using System.Security.Claims;
using ForgeTrust.AppSurface.Auth.AspNetCore;
using ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

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
builder.Services.AddAppSurfaceDevAuth(builder.Environment, dev =>
{
    dev.Users.Add(
        "admin",
        user => user
            .DisplayName("Local Admin")
            .Subject("admin-1")
            .Claim("role", "operator")
            .Claim("tenant", "local-demo"));
    dev.Users.Add(
        "viewer",
        user => user
            .DisplayName("Local Viewer")
            .Subject("viewer-1")
            .Claim("role", "viewer")
            .Claim("tenant", "local-demo"));
});

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", (
    HttpContext httpContext,
    IHostEnvironment environment,
    IOptions<AppSurfaceDevAuthOptions> devAuthOptions,
    IDataProtectionProvider dataProtectionProvider) =>
{
    var marker = AppSurfaceDevAuthMarker.Render(
        httpContext,
        environment,
        devAuthOptions,
        dataProtectionProvider,
        options => options.AdditionalCssClass = "demo-dev-auth");

    return Results.Content(
        $$"""
    <!doctype html>
    <html lang="en">
    <head>
      <meta charset="utf-8">
      <meta name="viewport" content="width=device-width, initial-scale=1">
      <title>AppSurface DevAuth Example</title>
      <style>
        body { font-family: system-ui, -apple-system, Segoe UI, sans-serif; margin: 0; color: #111827; background: #f8fafc; }
        main { max-width: 760px; padding: 32px; }
        .proof { display: inline-block; margin-top: 12px; color: #1d4ed8; }
      </style>
    </head>
    <body>
      <main>
        <h1>AppSurface DevAuth proof is running.</h1>
        <p>This page keeps the current local persona visible while you work through the app.</p>
        <a class="proof" href="/api/auth-proof">Open protected auth proof</a>
      </main>
      {{marker}}
    </body>
    </html>
    """,
        "text/html");
});

app.MapGet(
        "/api/auth-proof",
        (ClaimsPrincipal user) => Results.Json(new
        {
            result = "allowed",
            subject = user.FindFirst("sub")?.Value,
        }))
    .RequireSurfacePolicy("OperatorsOnly");

app.MapAppSurfaceDevAuth();

await app.RunAsync();
