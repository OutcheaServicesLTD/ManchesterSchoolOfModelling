using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Msm.Portfolio.Web.Authorization;
using Msm.Portfolio.Web.Configuration;
using Msm.Portfolio.Web.Domain.Entities;
using Msm.Portfolio.Web.Services;
using Msm.Portfolio.Web.ViewModels;

namespace Msm.Portfolio.Web.Areas.Client.Controllers;

/// <summary>
/// The client's own, unlimited gallery — self-managed, unlike the agency-curated
/// portfolio, and held to no count at all.
/// </summary>
/// <remarks>
/// As with the rest of the client area, no route carries a client id; the gallery is
/// resolved from the signed-in user. Locked until the curated portfolio has been
/// approved and published at least once: the gallery sits alongside the agency's work,
/// not in place of it before that work exists.
/// </remarks>
[Area("Client")]
[Route("client/gallery")]
[Authorize(Policy = Policies.ClientArea)]
public class GalleryController(
    IClientProfileAccessor profiles,
    IMediaService media,
    UserManager<ApplicationUser> userManager,
    IOptions<MediaOptions> mediaOptions) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken = default)
    {
        var client = await profiles.GetCurrentAsync(User, cancellationToken);

        if (client is null)
        {
            return View("NoProfile");
        }

        return View(await BuildAsync(client, cancellationToken));
    }

    [HttpPost("upload")]
    [Authorize(Policy = Permissions.Gallery.UploadOwn)]
    [RequestSizeLimit(1_073_741_824)]
    public async Task<IActionResult> Upload(
        List<IFormFile> files, CancellationToken cancellationToken = default)
    {
        var client = await profiles.GetCurrentAsync(User, cancellationToken);

        if (client is null)
        {
            return View("NoProfile");
        }

        if (client.Portfolio?.PublishedAt is null)
        {
            TempData["Error"] = "Your gallery opens once your portfolio has been approved.";
            return RedirectToAction(nameof(Index));
        }

        if (files.Count == 0)
        {
            TempData["Error"] = "Please choose at least one image.";
            return RedirectToAction(nameof(Index));
        }

        var outcomes = await media.UploadGalleryPhotosAsync(
            client.Id, files, CurrentUserId(), cancellationToken);

        var failed = outcomes.Where(o => !o.Succeeded).ToList();
        var succeeded = outcomes.Count - failed.Count;

        if (succeeded > 0)
        {
            TempData["Saved"] = succeeded == 1
                ? "1 photograph added, pending review."
                : $"{succeeded} photographs added, pending review.";
        }

        if (failed.Count > 0)
        {
            TempData["Error"] = string.Join(" | ", failed.Select(f => $"{f.Filename}: {f.Error}"));
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("{assetId:guid}/remove")]
    public async Task<IActionResult> Remove(Guid assetId, CancellationToken cancellationToken = default)
    {
        var client = await profiles.GetCurrentAsync(User, cancellationToken);

        if (client is null)
        {
            return View("NoProfile");
        }

        await media.RemoveGalleryPhotoAsync(client.Id, assetId, CurrentUserId(), cancellationToken);

        return RedirectToAction(nameof(Index));
    }

    private Guid? CurrentUserId() =>
        Guid.TryParse(userManager.GetUserId(User), out var id) ? id : null;

    private async Task<GalleryViewModel> BuildAsync(ClientProfile client, CancellationToken cancellationToken)
    {
        var options = mediaOptions.Value;
        var photos = await media.GetGalleryAsync(client.Id, cancellationToken);

        return new GalleryViewModel
        {
            Unlocked = client.Portfolio?.PublishedAt is not null,
            Photos =
            [
                .. photos.Select(a => new GalleryPhotoViewModel
                {
                    Id = a.Id,
                    Filename = a.OriginalFilename,
                    Width = a.Width,
                    Height = a.Height,
                    Status = a.GalleryStatus ?? Domain.Enums.GalleryPhotoStatus.PendingReview,
                    ReviewNote = a.GalleryReviewNote,
                    UploadedAt = a.UploadedAt
                })
            ],
            MaxImageBytes = options.MaxImageBytes,
            AllowedContentTypes = options.AllowedImageContentTypes
        };
    }
}
