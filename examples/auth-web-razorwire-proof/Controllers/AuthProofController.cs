using AuthWebRazorWireProofExample.Models;
using ForgeTrust.AppSurface.Auth.AspNetCore;
using Microsoft.AspNetCore.Mvc;

namespace AuthWebRazorWireProofExample.Controllers;

/// <summary>
/// Renders the browser-first proof console for sample-local auth personas.
/// </summary>
public sealed class AuthProofController : Controller
{
    private readonly IAppSurfaceAspNetCorePolicyEvaluator _evaluator;

    public AuthProofController(IAppSurfaceAspNetCorePolicyEvaluator evaluator)
    {
        ArgumentNullException.ThrowIfNull(evaluator);

        _evaluator = evaluator;
    }

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
        var headerPersona = ProofPersona.Normalize(Request.Headers[ProofAuthenticationHandler.HeaderName].ToString());
        if (headerPersona != ProofPersona.Anonymous)
        {
            return headerPersona;
        }

        return ProofPersona.Normalize(Request.Query[ProofAuthenticationHandler.QueryStateName].ToString());
    }
}
