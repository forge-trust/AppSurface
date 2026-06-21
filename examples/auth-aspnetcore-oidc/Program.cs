using ForgeTrust.AppSurface.Auth.AspNetCore.Oidc;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAppSurfaceOidcAuth(options =>
{
    options.RequireClientSecret = false;
    options.ConfigureOpenIdConnect(oidc =>
    {
        oidc.Authority = builder.Configuration["Authentication:Oidc:Authority"] ?? "https://issuer.example";
        oidc.ClientId = builder.Configuration["Authentication:Oidc:ClientId"] ?? "appsurface-example";
        var clientSecret = builder.Configuration["Authentication:Oidc:ClientSecret"];
        if (!string.IsNullOrWhiteSpace(clientSecret))
        {
            oidc.ClientSecret = clientSecret;
        }
    });
});
builder.Services.AddAuthorization();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => Results.Text("AppSurface ASP.NET Core OIDC registration proof is running.", "text/plain"));
app.MapGet(
    "/diagnostics/oidc-options",
    (IOptions<AppSurfaceOidcAuthOptions> appSurfaceOptions, IOptionsMonitor<OpenIdConnectOptions> oidcOptions) =>
    {
        var options = appSurfaceOptions.Value;
        var handlerOptions = oidcOptions.Get(options.OidcScheme);

        return Results.Json(new
        {
            options.CookieScheme,
            options.OidcScheme,
            options.SubjectClaim,
            CallbackPath = handlerOptions.CallbackPath.Value,
            SignedOutCallbackPath = handlerOptions.SignedOutCallbackPath.Value,
            handlerOptions.SaveTokens,
            HasAuthority = !string.IsNullOrWhiteSpace(handlerOptions.Authority),
            HasClientId = !string.IsNullOrWhiteSpace(handlerOptions.ClientId),
            HasClientSecret = !string.IsNullOrWhiteSpace(handlerOptions.ClientSecret),
        });
    });

await app.RunAsync();
