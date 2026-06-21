using System.Security.Claims;
using ForgeTrust.AppSurface.Auth.AspNetCore;
using ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth;

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

app.MapGet("/", () => Results.Content(
    """
    <!doctype html>
    <html lang="en">
    <head><meta charset="utf-8"><title>AppSurface DevAuth Example</title></head>
    <body>
      <p>AppSurface DevAuth proof is running.</p>
      <p><a href="/_appsurface/dev-auth">DEV AUTH: open local persona lab</a></p>
    </body>
    </html>
    """,
    "text/html"));

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
