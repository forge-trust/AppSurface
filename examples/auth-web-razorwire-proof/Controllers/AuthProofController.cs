using AuthWebRazorWireProofExample.Models;
using ForgeTrust.AppSurface.Auth.AspNetCore;
using Microsoft.AspNetCore.Mvc;

namespace AuthWebRazorWireProofExample.Controllers;

/// <summary>
/// Renders the browser-first proof console for sample-local auth personas.
/// </summary>
public sealed class AuthProofController : Controller
{
    private const string ProofUserQueryKey = "proofUser";
    private readonly IAppSurfaceAspNetCorePolicyEvaluator _evaluator;

    public AuthProofController(IAppSurfaceAspNetCorePolicyEvaluator evaluator)
    {
        ArgumentNullException.ThrowIfNull(evaluator);

        _evaluator = evaluator;
    }

    [HttpGet("/")]
    public async Task<IActionResult> Index([FromQuery(Name = ProofUserQueryKey)] string? proofUser, CancellationToken cancellationToken)
    {
        if (Request.Query.ContainsKey(ProofUserQueryKey))
        {
            ApplyProofPersona(proofUser);

            return RedirectToAction(nameof(Index));
        }

        var result = await _evaluator.AuthorizeAsync(AuthProofPolicy.Name, cancellationToken: cancellationToken);
        var apiState = AuthProofState.FromResult(AuthProofSurface.MinimalApi, result);
        var razorWireState = AuthProofState.FromResult(AuthProofSurface.RazorWireState, result);
        var model = new AuthProofPageModel(ResolveCurrentPersona(), apiState, razorWireState);

        return View(model);
    }

    private void ApplyProofPersona(string? proofUser)
    {
        var persona = ProofPersona.Normalize(proofUser);
        if (persona == ProofPersona.Anonymous)
        {
            Response.Cookies.Delete(ProofAuthenticationHandler.CookieName);

            return;
        }

        Response.Cookies.Append(
            ProofAuthenticationHandler.CookieName,
            persona,
            new CookieOptions
            {
                HttpOnly = true,
                IsEssential = true,
                SameSite = SameSiteMode.Lax,
                Secure = false
            });
    }

    private string ResolveCurrentPersona()
    {
        var headerPersona = ProofPersona.Normalize(Request.Headers[ProofAuthenticationHandler.HeaderName].ToString());
        if (headerPersona != ProofPersona.Anonymous)
        {
            return headerPersona;
        }

        return ProofPersona.Normalize(Request.Cookies[ProofAuthenticationHandler.CookieName]);
    }
}
