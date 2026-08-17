using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Msm.Portfolio.Web.Authorization;
using Msm.Portfolio.Web.Domain.Enums;
using Msm.Portfolio.Web.Services;
using Msm.Portfolio.Web.ViewModels;

namespace Msm.Portfolio.Web.Areas.Admin.Controllers;

/// <summary>
/// The admin client table (specification section 5).
/// </summary>
[Area("Admin")]
[Route("admin")]
[Authorize(Policy = Policies.AdminArea)]
public class HomeController(IAdminService admin) : Controller
{
    [HttpGet("")]
    [Authorize(Policy = Permissions.Clients.ViewAll)]
    public async Task<IActionResult> Index(
        string? search = null,
        PortfolioStatus? status = null,
        Guid? retoucher = null,
        CancellationToken cancellationToken = default)
    {
        var filter = new AdminClientFilter(search, status, retoucher);

        var model = new AdminDashboardViewModel
        {
            Search = search,
            Status = status,
            RetoucherUserId = retoucher,
            Rows = [.. await admin.SearchAsync(filter, cancellationToken)],
            Retouchers = [.. await admin.GetRetouchersAsync(cancellationToken)]
        };

        return View(model);
    }
}
