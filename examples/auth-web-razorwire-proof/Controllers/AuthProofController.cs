using AuthWebRazorWireProofExample.Models;
using ForgeTrust.AppSurface.Auth.AspNetCore;
using Microsoft.AspNetCore.Mvc;

namespace AuthWebRazorWireProofExample.Controllers;

/// <summary>
/// Renders the browser-first proof console for sample-local auth personas.
/// </summary>
/// <remarks>
/// The controller is part of the sample, not a package API. It asks the AppSurface ASP.NET Core auth
/// adapter to evaluate the host-owned policy, then renders the result as stable HTML state for browser and
/// test verification.
/// </remarks>
public sealed class AuthProofController : Controller
{
    private readonly IAppSurfaceAspNetCorePolicyEvaluator _evaluator;

    /// <summary>
    /// Creates a proof console controller backed by the AppSurface policy evaluator.
    /// </summary>
    /// <param name="evaluator">
    /// Evaluator registered by <c>AddAppSurfaceAspNetCoreAuth</c>. The sample depends on it to reuse the
    /// same host policy decision that the Minimal API endpoint renders as JSON.
    /// </param>
    /// <remarks>
    /// Passing a different evaluator in tests changes the contract being proved. Black-box tests should
    /// prefer HTTP requests against the hosted sample so the real middleware, authentication handler, and
    /// policy registration all participate.
    /// </remarks>
    public AuthProofController(IAppSurfaceAspNetCorePolicyEvaluator evaluator)
    {
        ArgumentNullException.ThrowIfNull(evaluator);

        _evaluator = evaluator;
    }

    /// <summary>
    /// Renders the proof console for the current request persona.
    /// </summary>
    /// <param name="cancellationToken">
    /// Request-abort token passed through to policy evaluation so cancelled browser requests do not keep
    /// evaluating the proof policy.
    /// </param>
    /// <returns>
    /// A Razor view model containing the normalized persona plus Minimal API and RazorWire-facing auth
    /// states for the same policy result.
    /// </returns>
    /// <remarks>
    /// Persona display follows the same precedence as <see cref="ProofAuthenticationHandler"/>: a non-empty
    /// <c>X-Proof-User</c> header wins over URL proof state even when the header normalizes to anonymous.
    /// That keeps curl checks and browser rendering aligned.
    /// </remarks>
    [HttpGet("/")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var result = await _evaluator.AuthorizeAsync(AuthProofPolicy.Name, cancellationToken: cancellationToken);
        var apiState = AuthProofState.FromResult(AuthProofSurface.MinimalApi, result);
        var razorWireState = AuthProofState.FromResult(AuthProofSurface.RazorWireState, result);
        var model = new AuthProofPageModel(ResolveCurrentPersona(), apiState, razorWireState);

        return View(model);
    }

    private string ResolveCurrentPersona()
    {
        var headerValue = Request.Headers[ProofAuthenticationHandler.HeaderName].ToString();
        if (!string.IsNullOrWhiteSpace(headerValue))
        {
            return ProofPersona.Normalize(headerValue);
        }

        return ProofPersona.Normalize(Request.Query[ProofAuthenticationHandler.QueryStateName].ToString());
    }
}
