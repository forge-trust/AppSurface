using Microsoft.AspNetCore.Mvc;

namespace WebPwaInstallExample.Controllers;

/// <summary>
/// Serves the root page for the AppSurface PWA install example.
/// </summary>
/// <remarks>
/// The controller is intentionally small: the sample uses the root HTTP GET route to render the default
/// <c>Index</c> view that displays the generated diagnostics link and verifier command for the running app.
/// </remarks>
[Route("")]
public sealed class HomeController : Controller
{
    /// <summary>
    /// Renders the default install-proof view at the application root.
    /// </summary>
    /// <returns>The MVC view result for the sample landing page.</returns>
    /// <remarks>
    /// Use this route only as sample UI. Real applications can move the same <c>&lt;appsurface:pwa-head /&gt;</c>
    /// layout wiring into any host-owned page or layout.
    /// </remarks>
    [HttpGet("")]
    public IActionResult Index()
    {
        return View();
    }
}
