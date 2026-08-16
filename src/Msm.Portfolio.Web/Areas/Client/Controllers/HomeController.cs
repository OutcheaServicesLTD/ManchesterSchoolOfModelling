using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Msm.Portfolio.Web.Authorization;

namespace Msm.Portfolio.Web.Areas.Client.Controllers;

/// <summary>
/// Client dashboard entry point (specification section 17). Profile, media and
/// portfolio management arrive in Phases 2 and 3.
/// </summary>
[Area("Client")]
[Route("client")]
[Authorize(Policy = Policies.ClientArea)]
public class HomeController : Controller
{
    [HttpGet("")]
    public IActionResult Index() => View();
}
