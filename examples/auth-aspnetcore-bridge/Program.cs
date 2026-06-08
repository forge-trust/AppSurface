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
    var principal = ProofAuthenticationHandler.CreatePrincipal("operator")!;
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

/// <summary>
/// Response DTO showing the neutral AppSurface auth result returned by the bridge example.
/// </summary>
/// <param name="Outcome">Neutral AppSurface outcome name.</param>
/// <param name="Reason">Neutral AppSurface reason name.</param>
/// <param name="Subject">Mapped subject id when the request resolved one.</param>
/// <param name="Metadata">Safe diagnostic metadata returned by the adapter.</param>
internal sealed record AuthProbe(
    string Outcome,
    string Reason,
    string? Subject,
    IReadOnlyDictionary<string, string> Metadata)
{
    /// <summary>
    /// Creates a response DTO from an AppSurface auth result without exposing raw claims.
    /// </summary>
    /// <param name="result">The neutral AppSurface auth result produced by the adapter.</param>
    /// <returns>A serializable probe response.</returns>
    public static AuthProbe FromResult(AppSurfaceAuthResult result)
    {
        return new AuthProbe(
            result.Outcome.ToString(),
            result.Reason.ToString(),
            result.Context?.User?.Id,
            result.Metadata);
    }
}

/// <summary>
/// Local proof-only authentication handler driven by the <c>X-Proof-User</c> header.
/// </summary>
/// <remarks>
/// The handler exists only so the example can run without cookies, OIDC, or Identity. Real hosts should keep their
/// normal ASP.NET Core authentication handlers and let the AppSurface adapter observe the populated request principal.
/// </remarks>
internal sealed class ProofAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    /// <summary>
    /// Authentication scheme name used by the local proof handler.
    /// </summary>
    public const string SchemeName = "Proof";

    /// <summary>
    /// Creates the proof authentication handler.
    /// </summary>
    /// <param name="options">Authentication options monitor supplied by ASP.NET Core.</param>
    /// <param name="logger">Logger factory supplied by ASP.NET Core.</param>
    /// <param name="encoder">URL encoder supplied by ASP.NET Core.</param>
    public ProofAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    /// <summary>
    /// Creates a proof principal for the supported example users.
    /// </summary>
    /// <param name="user">
    /// Supported proof user name: <c>operator</c>, <c>viewer</c>, or <c>nosub</c>. The <c>nosub</c> user intentionally
    /// omits a subject claim so the example can demonstrate a missing-subject setup failure.
    /// </param>
    /// <returns>An authenticated principal for supported users, or <see langword="null" /> for unsupported values.</returns>
    public static ClaimsPrincipal? CreatePrincipal(string user)
    {
        Claim[]? claims = user switch
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
            _ => null,
        };

        if (claims is null)
        {
            return null;
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
    }

    /// <summary>
    /// Authenticates supported proof users from the <c>X-Proof-User</c> header.
    /// </summary>
    /// <returns>
    /// A successful ticket for supported proof users, or <c>AuthenticateResult.NoResult()</c> when the header is
    /// absent, blank, or unsupported.
    /// </returns>
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var user = Request.Headers["X-Proof-User"].ToString();
        if (string.IsNullOrWhiteSpace(user))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var principal = CreatePrincipal(user);
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}
