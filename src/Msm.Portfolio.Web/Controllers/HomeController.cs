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

    /// <summary>
    /// One page for a wrong address, a refusal and a fault.
    /// </summary>
    /// <remarks>
    /// Reached two ways: thrown to by the exception handler, and re-executed into by the
    /// status code pages middleware with the code in the route. The status of the original
    /// response is preserved either way — a 404 that answered 200 would tell a search
    /// engine the missing portfolio is a real page.
    /// </remarks>
    [HttpGet("/error/{statusCode:int?}")]
    [HttpGet("/error")]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error(int? statusCode = null) =>
        View(new ErrorViewModel
        {
            StatusCode = statusCode,
            RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier
        });
}
