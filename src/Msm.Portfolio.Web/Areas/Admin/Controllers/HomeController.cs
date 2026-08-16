using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Msm.Portfolio.Web.Authorization;

namespace Msm.Portfolio.Web.Areas.Admin.Controllers;

/// <summary>
/// Admin area entry point (specification section 5). The client table, portfolio
/// management and payment views arrive in Phase 5.
/// </summary>
[Area("Admin")]
[Route("admin")]
[Authorize(Policy = Policies.AdminArea)]
public class HomeController : Controller
{
    [HttpGet("")]
    public IActionResult Index() => View();
}
