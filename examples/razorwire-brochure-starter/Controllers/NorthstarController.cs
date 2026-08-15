using Microsoft.AspNetCore.Mvc;

namespace NorthstarBrochureStarter.Controllers;

/// <summary>
/// Serves the fictional Northstar editorial brochure pages.
/// </summary>
public sealed class NorthstarController : Controller
{
    /// <summary>
    /// Renders the editorial landing page.
    /// </summary>
    [HttpGet("/")]
    public IActionResult Home() => View();

    /// <summary>
    /// Renders the services overview.
    /// </summary>
    [HttpGet("/services")]
    public IActionResult Services() => View();

    /// <summary>
    /// Renders the journal index.
    /// </summary>
    [HttpGet("/journal")]
    public IActionResult Journal() => View();

    /// <summary>
    /// Renders the field guide feature.
    /// </summary>
    [HttpGet("/journal/field-guide")]
    public IActionResult FieldGuide() => View();

    /// <summary>
    /// Renders the contact preview form.
    /// </summary>
    [HttpGet("/contact")]
    public IActionResult Contact() => View();

    /// <summary>
    /// Renders the confirmation preview at both canonical paths.
    /// </summary>
    [HttpGet("/thank-you")]
    [HttpGet("/thank-you.html")]
    public IActionResult ThankYou() => View();
}
