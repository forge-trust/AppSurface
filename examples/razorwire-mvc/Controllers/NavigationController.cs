using Microsoft.AspNetCore.Mvc;

namespace RazorWireWebExample.Controllers;

/// <summary>
/// A controller that demonstrates stateful navigation and island persistence in RazorWire.
/// </summary>
public class NavigationController : Controller
{
    /// <summary>
    /// Renders the default view for the navigation index action.
    /// </summary>
    /// <returns>A ViewResult that renders the default view for this action.</returns>
    public IActionResult Index()
    {
        return View();
    }

    /// <summary>
    /// Renders a brochure-style same-page navigation sample backed by RazorWire page navigation.
    /// </summary>
    /// <remarks>
    /// The route follows the sample application's conventional MVC discovery path: the action returns
    /// <c>View()</c>, so ASP.NET Core renders <c>Views/Navigation/PageNavigation.cshtml</c>. Consumers copying the
    /// sample should keep the action, route, and view names aligned or supply an explicit view name when their routing
    /// conventions differ.
    /// </remarks>
    /// <remarks>
    /// The page demonstrates RazorWire page navigation with <c>rw-page-nav</c>, <c>rw-page-nav-link</c>,
    /// <c>rw-page-nav-toggle</c>, and <c>rw-page-nav-panel</c>. The shared layout renders <c>&lt;rw:scripts/&gt;</c>;
    /// RazorWire then loads the page-navigation asset only when those markers are present. Hosts must register
    /// <c>RazorWireWebModule</c>, import <c>ForgeTrust.RazorWire</c> TagHelpers, and keep the package static assets
    /// available. Put app scripts that depend on RazorWire after <c>&lt;rw:scripts/&gt;</c>; keep closed-panel CSS scoped
    /// to <c>data-rw-page-nav-enhanced</c> so no-JavaScript anchor navigation remains reachable.
    /// </remarks>
    /// <returns>A ViewResult that renders the page-navigation sample.</returns>
    public IActionResult PageNavigation()
    {
        return View();
    }
}
