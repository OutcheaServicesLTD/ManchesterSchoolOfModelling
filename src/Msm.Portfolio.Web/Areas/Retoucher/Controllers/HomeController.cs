using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Msm.Portfolio.Web.Authorization;

namespace Msm.Portfolio.Web.Areas.Retoucher.Controllers;

/// <summary>
/// Retoucher area entry point (specification section 6). The queue and upload
/// workspace arrive in Phase 4.
/// </summary>
[Area("Retoucher")]
[Route("retoucher")]
[Authorize(Policy = Policies.RetoucherArea)]
public class HomeController : Controller
{
    [HttpGet("")]
    public IActionResult Index() => View();
}
