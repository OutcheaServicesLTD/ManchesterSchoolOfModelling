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
/// The client managing their own photographs and self-tape
/// (specification sections 12, 14, 17 and 34).
/// </summary>
/// <remarks>
/// As with the rest of the client area, no route carries a client id. The library is
/// resolved from the signed-in user, so a client cannot address another client's media.
/// </remarks>
[Area("Client")]
[Route("client")]
[Authorize(Policy = Policies.ClientArea)]
public class MediaController(
    IClientProfileAccessor profiles,
    IMediaService media,
    UserManager<ApplicationUser> userManager,
    IOptions<MediaOptions> mediaOptions) : Controller
{
    [HttpGet("media")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken = default)
    {
        var client = await profiles.GetCurrentAsync(User, cancellationToken);

        if (client is null)
        {
            return View("NoProfile");
        }

        return View(await BuildAsync(client.Id, cancellationToken));
    }

    [HttpPost("media/upload")]
    [RequestSizeLimit(1_073_741_824)]
    public async Task<IActionResult> Upload(
        List<IFormFile> files,
        CancellationToken cancellationToken = default)
    {
        var client = await profiles.GetCurrentAsync(User, cancellationToken);

        if (client is null)
        {
            return View("NoProfile");
        }

        if (files.Count == 0)
        {
            TempData["Error"] = "Please choose at least one image.";
            return RedirectToAction(nameof(Index));
        }

        var outcomes = await media.UploadImagesAsync(
            client.Id, files, CurrentUserId(), cancellationToken);

        var failed = outcomes.Where(o => !o.Succeeded).ToList();
        var succeeded = outcomes.Count - failed.Count;

        // Each file is reported individually, so a rejected image does not hide the
        // ones that worked (specification section 42).
        TempData["UploadSummary"] = $"{succeeded} of {outcomes.Count} uploaded.";

        if (failed.Count > 0)
        {
            TempData["UploadFailures"] = string.Join(
                " | ", failed.Select(f => $"{f.Filename}: {f.Error}"));
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("media/{assetId:guid}/select")]
    public async Task<IActionResult> Select(
        Guid assetId, bool selected, CancellationToken cancellationToken = default)
    {
        var client = await profiles.GetCurrentAsync(User, cancellationToken);

        if (client is null)
        {
            return View("NoProfile");
        }

        var (succeeded, error) = await media.SetSelectedAsync(
            client.Id, assetId, selected, CurrentUserId(), cancellationToken);

        if (!succeeded)
        {
            TempData["Error"] = error;
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("media/{assetId:guid}/featured")]
    public async Task<IActionResult> Featured(Guid assetId, CancellationToken cancellationToken = default)
    {
        var client = await profiles.GetCurrentAsync(User, cancellationToken);

        if (client is null)
        {
            return View("NoProfile");
        }

        var (succeeded, error) = await media.SetFeaturedAsync(
            client.Id, assetId, CurrentUserId(), cancellationToken);

        if (!succeeded)
        {
            TempData["Error"] = error;
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("media/{assetId:guid}/remove")]
    public async Task<IActionResult> Remove(Guid assetId, CancellationToken cancellationToken = default)
    {
        var client = await profiles.GetCurrentAsync(User, cancellationToken);

        if (client is null)
        {
            return View("NoProfile");
        }

        await media.SoftDeleteAsync(client.Id, assetId, CurrentUserId(), cancellationToken);

        return RedirectToAction(nameof(Index));
    }

    [HttpGet("self-tape")]
    public async Task<IActionResult> SelfTape(CancellationToken cancellationToken = default)
    {
        var client = await profiles.GetCurrentAsync(User, cancellationToken);

        if (client is null)
        {
            return View("NoProfile");
        }

        var selfTape = await media.GetSelfTapeAsync(client.Id, cancellationToken);

        return View(new SelfTapeViewModel
        {
            AssetId = selfTape?.Id,
            Filename = selfTape?.OriginalFilename,
            MaxBytes = mediaOptions.Value.MaxVideoBytes,
            AllowedContentTypes = mediaOptions.Value.AllowedVideoContentTypes
        });
    }

    [HttpPost("self-tape")]
    [RequestSizeLimit(1_073_741_824)]
    public async Task<IActionResult> SelfTape(IFormFile? file, CancellationToken cancellationToken = default)
    {
        var client = await profiles.GetCurrentAsync(User, cancellationToken);

        if (client is null)
        {
            return View("NoProfile");
        }

        if (file is null || file.Length == 0)
        {
            TempData["Error"] = "Please choose a video file.";
            return RedirectToAction(nameof(SelfTape));
        }

        var outcome = await media.UploadSelfTapeAsync(client.Id, file, CurrentUserId(), cancellationToken);

        if (!outcome.Succeeded)
        {
            TempData["Error"] = outcome.Error;
        }

        return RedirectToAction(nameof(SelfTape));
    }

    [HttpPost("self-tape/remove")]
    public async Task<IActionResult> RemoveSelfTape(CancellationToken cancellationToken = default)
    {
        var client = await profiles.GetCurrentAsync(User, cancellationToken);

        if (client is null)
        {
            return View("NoProfile");
        }

        await media.RemoveSelfTapeAsync(client.Id, CurrentUserId(), cancellationToken);

        return RedirectToAction(nameof(SelfTape));
    }

    private Guid? CurrentUserId() =>
        Guid.TryParse(userManager.GetUserId(User), out var id) ? id : null;

    private async Task<MediaLibraryViewModel> BuildAsync(Guid clientId, CancellationToken cancellationToken)
    {
        var options = mediaOptions.Value;
        var pool = await media.GetPoolAsync(clientId, cancellationToken);

        return new MediaLibraryViewModel
        {
            Assets = [.. pool.Select(a => new MediaAssetViewModel
            {
                Id = a.Id,
                Filename = a.OriginalFilename,
                Orientation = a.Orientation,
                IsSelected = a.IsSelectedForPortfolio,
                IsFeatured = a.IsFeatured,
                Width = a.Width,
                Height = a.Height
            })],
            PoolLimit = options.MediaPoolImageLimit,
            PortfolioLimit = options.PortfolioImageLimit,
            MaxImageBytes = options.MaxImageBytes,
            AllowedContentTypes = options.AllowedImageContentTypes
        };
    }
}
