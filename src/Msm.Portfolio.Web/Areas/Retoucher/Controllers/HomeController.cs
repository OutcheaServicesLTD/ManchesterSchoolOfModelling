using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Msm.Portfolio.Web.Authorization;
using Msm.Portfolio.Web.Domain.Entities;
using Msm.Portfolio.Web.Services;
using Msm.Portfolio.Web.ViewModels;

namespace Msm.Portfolio.Web.Areas.Retoucher.Controllers;

/// <summary>
/// The retoucher's queue (specification sections 6 and 34).
/// </summary>
[Area("Retoucher")]
[Route("retoucher")]
[Authorize(Policy = Policies.RetoucherArea)]
public class HomeController(
    IRetoucherService retouchers,
    UserManager<ApplicationUser> userManager) : Controller
{
    [HttpGet("")]
    [HttpGet("queue")]
    public async Task<IActionResult> Index(
        RetoucherQueueTab tab = RetoucherQueueTab.Waiting,
        CancellationToken cancellationToken = default)
    {
        var userId = CurrentUserId();

        var model = new RetoucherQueueViewModel
        {
            Tab = tab,
            Items = [.. await retouchers.GetQueueAsync(tab, userId, cancellationToken)],
            Counts = await retouchers.GetCountsAsync(cancellationToken)
        };

        return View(model);
    }

    [HttpPost("client/{clientId:guid}/start")]
    public async Task<IActionResult> Start(Guid clientId, CancellationToken cancellationToken = default)
    {
        var (succeeded, error) = await retouchers.StartWorkAsync(clientId, CurrentUserId(), cancellationToken);

        if (!succeeded)
        {
            TempData["Error"] = error;
            return RedirectToAction(nameof(Index));
        }

        return Redirect($"/retoucher/client/{clientId}");
    }

    private Guid CurrentUserId() =>
        Guid.TryParse(userManager.GetUserId(User), out var id) ? id : Guid.Empty;
}
