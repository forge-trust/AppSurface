using Microsoft.AspNetCore.Mvc;

namespace WebPwaInstallExample.Controllers;

[Route("")]
public sealed class HomeController : Controller
{
    [HttpGet("")]
    public IActionResult Index()
    {
        return View();
    }
}
