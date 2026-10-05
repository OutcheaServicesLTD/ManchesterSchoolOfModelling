using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Msm.Portfolio.Web.Authorization;
using Msm.Portfolio.Web.Domain.Entities;
using Msm.Portfolio.Web.Services;
using Msm.Portfolio.Web.ViewModels;

namespace Msm.Portfolio.Web.Areas.Admin.Controllers;

/// <summary>
/// Reviewing what clients have added to their own galleries, across every client at
/// once, oldest first.
/// </summary>
/// <remarks>
/// A single queue rather than one buried in each client's own page: nothing a client
/// uploads here is public until staff look at it, so finding the queue has to be at
/// least as easy as finding the retoucher's.
/// </remarks>
[Area("Admin")]
[Route("admin/gallery")]
[Authorize(Policy = Policies.AdminArea)]
[Authorize(Policy = Permissions.Gallery.Moderate)]
public class GalleryController(IMediaService media, UserManager<ApplicationUser> userManager) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken = default)
    {
        var pending = await media.GetPendingGalleryAsync(cancellationToken);

        return View(new GalleryModerationViewModel
        {
            Photos =
            [
                .. pending.Select(a => new GalleryModerationItemViewModel
                {
                    Id = a.Id,
                    ClientId = a.ClientId,
                    ClientName = a.Client.PublicName,
                    Filename = a.OriginalFilename,
                    Width = a.Width,
                    Height = a.Height,
                    UploadedAt = a.UploadedAt
                })
            ]
        });
    }

    [HttpPost("{assetId:guid}/approve")]
    public async Task<IActionResult> Approve(Guid assetId, CancellationToken cancellationToken = default)
    {
        var (succeeded, error) = await media.ApproveGalleryPhotoAsync(
            assetId, CurrentUserId(), cancellationToken);

        TempData[succeeded ? "Saved" : "Error"] = succeeded ? "Photograph approved." : error;

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("{assetId:guid}/reject")]
    public async Task<IActionResult> Reject(
        Guid assetId, string? reason, CancellationToken cancellationToken = default)
    {
        var (succeeded, error) = await media.RejectGalleryPhotoAsync(
            assetId, CurrentUserId(), reason, cancellationToken);

        TempData[succeeded ? "Saved" : "Error"] = succeeded ? "Photograph rejected." : error;

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("{assetId:guid}/remove")]
    public async Task<IActionResult> Remove(Guid assetId, CancellationToken cancellationToken = default)
    {
        var removed = await media.AdminRemoveGalleryPhotoAsync(assetId, CurrentUserId(), cancellationToken);

        TempData[removed ? "Saved" : "Error"] = removed
            ? "Photograph removed."
            : "That photograph could not be found.";

        return RedirectToAction(nameof(Index));
    }

    private Guid? CurrentUserId() =>
        Guid.TryParse(userManager.GetUserId(User), out var id) ? id : null;
}
