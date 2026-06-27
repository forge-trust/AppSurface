using AuthWebRazorWireProofExample;
using AuthWebRazorWireProofExample.Models;
using ForgeTrust.AppSurface.Auth;
using ForgeTrust.AppSurface.Auth.AspNetCore;
using ForgeTrust.RazorWire;
using ForgeTrust.RazorWire.Auth.AspNetCore;
using Microsoft.AspNetCore.Authentication;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();
builder.Services.AddRazorWire();

builder.Services
    .AddAuthentication(ProofAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, ProofAuthenticationHandler>(
        ProofAuthenticationHandler.SchemeName,
        options => { _ = options; });

builder.Services.AddAuthorization(options =>
{
    options.AddAppSurfacePolicy(
        AuthProofPolicy.Name,
        policy => policy
            .RequireAuthenticatedUser()
            .RequireClaim(AuthProofPolicy.RoleClaimType, AuthProofPolicy.OperatorRole));
});

builder.Services.AddAppSurfaceAspNetCoreAuth(options => options.MapSubjectClaim(AuthProofPolicy.SubjectClaimType));
builder.Services.AddRazorWireAspNetCoreAuth();

var app = builder.Build();

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapRazorWireProofRuntimeAssets();
app.MapRazorWire();
app.MapGet("/api/auth-proof", async (
    IAppSurfaceAspNetCorePolicyEvaluator evaluator,
    CancellationToken cancellationToken) =>
{
    var result = await evaluator.AuthorizeAsync(AuthProofPolicy.Name, cancellationToken: cancellationToken);
    var state = AuthProofState.FromResult(AuthProofSurface.MinimalApi, result);

    return Results.Json(state, statusCode: state.StatusCode);
});
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=AuthProof}/{action=Index}/{id?}");

await app.RunAsync();
