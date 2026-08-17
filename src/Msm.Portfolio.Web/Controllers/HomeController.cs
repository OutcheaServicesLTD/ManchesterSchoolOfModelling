using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Msm.Portfolio.Web.Models;

namespace Msm.Portfolio.Web.Controllers;

[AllowAnonymous]
public class HomeController : Controller
{
    /// <summary>
    /// The portfolio domain has no marketing homepage of its own, so the root goes to
    /// the Model Board (specification section 34).
    /// </summary>
    [HttpGet("/")]
    public IActionResult Index() => RedirectToAction("Index", "ModelBoard");

    [HttpGet("/error")]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error() =>
        View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
}
