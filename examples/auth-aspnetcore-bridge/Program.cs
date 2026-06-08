using System.Security.Claims;
using System.Text.Encodings.Web;
using ForgeTrust.AppSurface.Auth;
using ForgeTrust.AppSurface.Auth.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddAuthentication(ProofAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, ProofAuthenticationHandler>(
        ProofAuthenticationHandler.SchemeName,
        options => { _ = options; });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(
        "OperatorsOnly",
        policy => policy
            .RequireAuthenticatedUser()
            .RequireClaim("role", "operator"));
});

builder.Services.AddAppSurfaceAspNetCoreAuth(options => options.MapSubjectClaim("sub"));

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => Results.Text("AppSurface ASP.NET Core auth bridge proof is running.", "text/plain"));
app.MapGet("/allowed", EvaluatePolicyAsync);
app.MapGet("/forbidden", EvaluatePolicyAsync);
app.MapGet("/unauthenticated", EvaluatePolicyAsync);
app.MapGet("/missing-policy", async (IAppSurfaceAspNetCorePolicyEvaluator evaluator) =>
    AuthProbe.FromResult(await evaluator.AuthorizeAsync("MissingPolicy")));
app.MapGet("/missing-subject", EvaluatePolicyAsync);
app.MapGet("/missing-services", async () =>
{
    var services = new ServiceCollection();
    services.AddAppSurfaceAspNetCoreAuth(options => options.MapSubjectClaim("sub"));

#pragma warning disable ASP0000
    await using var provider = services.BuildServiceProvider();
#pragma warning restore ASP0000
    using var scope = provider.CreateScope();
    var principal = ProofAuthenticationHandler.CreatePrincipal("operator");
    var httpContext = new DefaultHttpContext
    {
        User = principal,
        RequestServices = scope.ServiceProvider,
    };
    scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = httpContext;

    var result = await scope.ServiceProvider
        .GetRequiredService<IAppSurfaceAspNetCorePolicyEvaluator>()
        .AuthorizeAsync("OperatorsOnly");

    return AuthProbe.FromResult(result);
});

await app.RunAsync();

static async Task<AuthProbe> EvaluatePolicyAsync(IAppSurfaceAspNetCorePolicyEvaluator evaluator)
{
    var result = await evaluator.AuthorizeAsync("OperatorsOnly");
    return AuthProbe.FromResult(result);
}

internal sealed record AuthProbe(
    string Outcome,
    string Reason,
    string? Subject,
    IReadOnlyDictionary<string, string> Metadata)
{
    public static AuthProbe FromResult(AppSurfaceAuthResult result)
    {
        return new AuthProbe(
            result.Outcome.ToString(),
            result.Reason.ToString(),
            result.Context?.User?.Id,
            result.Metadata);
    }
}

internal sealed class ProofAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Proof";

    public ProofAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    public static ClaimsPrincipal CreatePrincipal(string user)
    {
        Claim[] claims = user switch
        {
            "operator" =>
            [
                new Claim("sub", "operator-1"),
                new Claim("role", "operator"),
            ],
            "viewer" =>
            [
                new Claim("sub", "viewer-1"),
                new Claim("role", "viewer"),
            ],
            "nosub" => [new Claim("role", "operator")],
            _ => [],
        };

        return new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var user = Request.Headers["X-Proof-User"].ToString();
        if (string.IsNullOrWhiteSpace(user))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var principal = CreatePrincipal(user);
        if (!principal.Identity?.IsAuthenticated ?? true)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}
