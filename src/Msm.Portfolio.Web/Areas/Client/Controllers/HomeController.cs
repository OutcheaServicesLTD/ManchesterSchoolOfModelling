using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Msm.Portfolio.Web.Authorization;
using Msm.Portfolio.Web.Configuration;
using Msm.Portfolio.Web.Data;
using Msm.Portfolio.Web.Domain.Enums;
using Msm.Portfolio.Web.Services;
using Msm.Portfolio.Web.ViewModels;

namespace Msm.Portfolio.Web.Areas.Client.Controllers;

/// <summary>
/// The client's dashboard (specification section 17).
/// </summary>
[Area("Client")]
[Route("client")]
[Authorize(Policy = Policies.ClientArea)]
public class HomeController(
    IClientProfileAccessor profiles,
    IClientDashboardBuilder dashboards) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken = default)
    {
        var client = await profiles.GetCurrentAsync(User, cancellationToken);

        if (client is null)
        {
            // Signed in as a Client with no profile attached. Nothing useful to show,
            // and no other client's data may be substituted.
            return View("NoProfile");
        }

        var model = await dashboards.BuildAsync(client, isPreview: false, cancellationToken);

        return View(model);
    }
}
