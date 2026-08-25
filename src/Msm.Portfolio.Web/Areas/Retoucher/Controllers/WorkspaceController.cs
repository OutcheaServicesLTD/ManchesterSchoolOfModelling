using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Msm.Portfolio.Web.Authorization;
using Msm.Portfolio.Web.Configuration;
using Msm.Portfolio.Web.Data;
using Msm.Portfolio.Web.Domain.Entities;
using Msm.Portfolio.Web.Services;
using Msm.Portfolio.Web.ViewModels;

namespace Msm.Portfolio.Web.Areas.Retoucher.Controllers;

/// <summary>
/// Where a retoucher prepares one client's portfolio (specification sections 6 and 34).
/// </summary>
/// <remarks>
/// This area does take a client id in the route, because a retoucher legitimately works
/// on other people's records. Every action therefore checks the id against the
/// retoucher's own assignment first; an id belonging to someone else's work is refused.
/// </remarks>
[Area("Retoucher")]
[Route("retoucher/client/{clientId:guid}")]
[Authorize(Policy = Policies.RetoucherArea)]
public class WorkspaceController(
    ApplicationDbContext db,
    IRetoucherService retouchers,
    IMediaService media,
    UserManager<ApplicationUser> userManager,
    IOptions<MediaOptions> mediaOptions) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(Guid clientId, CancellationToken cancellationToken = default)
    {
        if (!await IsAllowedAsync(clientId, cancellationToken))
        {
            return Forbid();
        }

        var model = await BuildAsync(clientId, cancellationToken);

        return model is null ? NotFound() : View(model);
    }

    /// <summary>
    /// Receives one file per request so the browser can report progress for each and
    /// retry just the ones that failed, rather than restarting the batch
    /// (specification section 42).
    /// </summary>
    [HttpPost("upload")]
    [RequestSizeLimit(1_073_741_824)]
    public async Task<IActionResult> Upload(
        Guid clientId,
        List<IFormFile> files,
        CancellationToken cancellationToken = default)
    {
        // JSON rather than Forbid(), which would redirect this XHR to an HTML denial
        // page. The uploader could only report that as an unexpected response, hiding
        // the real reason from the retoucher.
        if (!await IsAllowedAsync(clientId, cancellationToken))
        {
            return StatusCode(StatusCodes.Status403Forbidden,
                new { error = "You are not assigned to this client." });
        }

        if (files.Count == 0)
        {
            return BadRequest(new { error = "No file was received." });
        }

        var outcomes = await media.UploadImagesAsync(clientId, files, CurrentUserId(), cancellationToken);

        return Json(new
        {
            results = outcomes.Select(o => new
            {
                filename = o.Filename,
                succeeded = o.Succeeded,
                assetId = o.AssetId,
                error = o.Error
            })
        });
    }

    [HttpPost("select/{assetId:guid}")]
    public async Task<IActionResult> Select(
        Guid clientId, Guid assetId, bool selected, CancellationToken cancellationToken = default)
    {
        if (!await IsAllowedAsync(clientId, cancellationToken))
        {
            return Forbid();
        }

        var (succeeded, error) = await media.SetSelectedAsync(
            clientId, assetId, selected, CurrentUserId(), cancellationToken);

        if (!succeeded)
        {
            TempData["Error"] = error;
        }

        return RedirectToAction(nameof(Index), new { clientId });
    }

    /// <summary>
    /// Adds every ticked photograph to the portfolio at once.
    /// </summary>
    /// <remarks>
    /// Working through a shoot one button at a time was the slowest part of preparing a
    /// portfolio. The per-image action is kept for removing a single photograph.
    /// </remarks>
    [HttpPost("select")]
    public async Task<IActionResult> SelectMany(
        Guid clientId,
        [FromForm(Name = "assetIds")] Guid[]? assetIds,
        CancellationToken cancellationToken = default)
    {
        if (!await IsAllowedAsync(clientId, cancellationToken))
        {
            return Forbid();
        }

        var (added, error) = await media.SetSelectedManyAsync(
            clientId, assetIds ?? [], CurrentUserId(), cancellationToken);

        if (error is not null)
        {
            TempData["Error"] = error;
        }
        else if (added > 0)
        {
            TempData["Saved"] = added == 1
                ? "1 photograph added to the portfolio."
                : $"{added} photographs added to the portfolio.";
        }

        return RedirectToAction(nameof(Index), new { clientId });
    }

    [HttpPost("featured/{assetId:guid}")]
    public async Task<IActionResult> Featured(
        Guid clientId, Guid assetId, CancellationToken cancellationToken = default)
    {
        if (!await IsAllowedAsync(clientId, cancellationToken))
        {
            return Forbid();
        }

        var (succeeded, error) = await media.SetFeaturedAsync(
            clientId, assetId, CurrentUserId(), cancellationToken);

        if (!succeeded)
        {
            TempData["Error"] = error;
        }

        return RedirectToAction(nameof(Index), new { clientId });
    }

    /// <summary>
    /// Records which part of the cover photograph must survive being cropped.
    /// </summary>
    /// <remarks>
    /// The cover fills a wide band at the top of the portfolio and a portrait card on the
    /// Model Board, neither of which is the shape of the photograph. Centred by default,
    /// that crop takes the head off a full-length shot.
    /// </remarks>
    [HttpPost("focal/{assetId:guid}")]
    public async Task<IActionResult> Focal(
        Guid clientId,
        Guid assetId,
        int? x,
        int? y,
        bool clear = false,
        CancellationToken cancellationToken = default)
    {
        if (!await IsAllowedAsync(clientId, cancellationToken))
        {
            return Forbid();
        }

        var (succeeded, error) = await media.SetFocalPointAsync(
            clientId, assetId, clear ? null : x, clear ? null : y, CurrentUserId(), cancellationToken);

        if (!succeeded)
        {
            TempData["Error"] = error;
        }

        return RedirectToAction(nameof(Index), new { clientId });
    }

    [HttpPost("remove/{assetId:guid}")]
    public async Task<IActionResult> Remove(
        Guid clientId, Guid assetId, CancellationToken cancellationToken = default)
    {
        if (!await IsAllowedAsync(clientId, cancellationToken))
        {
            return Forbid();
        }

        await media.SoftDeleteAsync(clientId, assetId, CurrentUserId(), cancellationToken);

        return RedirectToAction(nameof(Index), new { clientId });
    }

    /// <summary>
    /// Moves an image one place earlier or later in the portfolio order.
    /// </summary>
    /// <remarks>
    /// Buttons rather than drag-only reordering, because specification section 41
    /// requires keyboard navigation; a drag-only control would be unusable without a
    /// mouse.
    /// </remarks>
    [HttpPost("move/{assetId:guid}")]
    public async Task<IActionResult> Move(
        Guid clientId, Guid assetId, int direction, CancellationToken cancellationToken = default)
    {
        if (!await IsAllowedAsync(clientId, cancellationToken))
        {
            return Forbid();
        }

        var ordered = (await media.GetPoolAsync(clientId, cancellationToken))
            .Where(a => a.IsSelectedForPortfolio)
            .Select(a => a.Id)
            .ToList();

        var index = ordered.IndexOf(assetId);
        var target = index + Math.Sign(direction);

        if (index >= 0 && target >= 0 && target < ordered.Count)
        {
            (ordered[index], ordered[target]) = (ordered[target], ordered[index]);

            // Unselected images keep their relative order behind the portfolio ones.
            var rest = (await media.GetPoolAsync(clientId, cancellationToken))
                .Where(a => !a.IsSelectedForPortfolio)
                .Select(a => a.Id);

            await media.ReorderAsync(clientId, [.. ordered, .. rest], CurrentUserId(), cancellationToken);
        }

        return RedirectToAction(nameof(Index), new { clientId });
    }

    /// <summary>
    /// Saves a whole new order at once, as produced by dragging a photograph.
    /// </summary>
    /// <remarks>
    /// The order is rebuilt from the identifiers posted rather than trusted outright:
    /// anything not currently on the portfolio is ignored, and anything the browser left
    /// out keeps its place at the end. A stale page therefore cannot drop a photograph
    /// from the portfolio by omitting it.
    /// </remarks>
    [HttpPost("reorder")]
    public async Task<IActionResult> Reorder(
        Guid clientId,
        [FromForm(Name = "order")] Guid[]? order,
        CancellationToken cancellationToken = default)
    {
        if (!await IsAllowedAsync(clientId, cancellationToken))
        {
            return Forbid();
        }

        if (order is null || order.Length == 0)
        {
            return RedirectToAction(nameof(Index), new { clientId });
        }

        var pool = await media.GetPoolAsync(clientId, cancellationToken);
        var selected = pool.Where(a => a.IsSelectedForPortfolio).Select(a => a.Id).ToList();
        var rest = pool.Where(a => !a.IsSelectedForPortfolio).Select(a => a.Id);

        var reordered = RebuildOrder(order, selected);

        await media.ReorderAsync(clientId, [.. reordered, .. rest], CurrentUserId(), cancellationToken);

        return RedirectToAction(nameof(Index), new { clientId });
    }

    /// <summary>
    /// Works out the new portfolio order from the identifiers a browser posted.
    /// </summary>
    /// <remarks>
    /// Deliberately not a straight assignment. The posted list comes from a page that may
    /// be minutes old and is trivially editable, so it is treated as a preference rather
    /// than an instruction: identifiers that are not on the portfolio are ignored, and any
    /// photograph the browser failed to mention keeps its place at the end instead of
    /// silently dropping off the portfolio.
    /// </remarks>
    internal static List<Guid> RebuildOrder(IEnumerable<Guid> posted, IReadOnlyCollection<Guid> selected)
    {
        var reordered = posted.Where(selected.Contains).Distinct().ToList();

        reordered.AddRange(selected.Where(id => !reordered.Contains(id)));

        return reordered;
    }

    /// <summary>
    /// Sends the prepared portfolio to an administrator to review.
    /// </summary>
    /// <remarks>
    /// Answers JSON to a script and a redirect to a plain form post, so the page can say
    /// "sent" where the retoucher is standing rather than reloading the whole workspace and
    /// putting them back at the top of it — and so the button still works with no script.
    /// </remarks>
    [HttpPost("submit")]
    public async Task<IActionResult> Submit(Guid clientId, CancellationToken cancellationToken = default)
    {
        var wantsJson = Request.Headers.XRequestedWith == "XMLHttpRequest";

        if (!await IsAllowedAsync(clientId, cancellationToken))
        {
            return wantsJson
                ? StatusCode(StatusCodes.Status403Forbidden,
                    new { error = "You are not assigned to this client." })
                : Forbid();
        }

        var (succeeded, error) = await retouchers.SubmitForReviewAsync(
            clientId, CurrentUserId(), cancellationToken);

        if (!succeeded)
        {
            if (wantsJson)
            {
                return BadRequest(new { error });
            }

            TempData["Error"] = error;
            return RedirectToAction(nameof(Index), new { clientId });
        }

        if (wantsJson)
        {
            // A URL to navigate to, not text to display in place. Staying on the
            // workspace after sending — with only a line of text to say so — was the
            // complaint: this is finished work, and the retoucher's next move is
            // always back to the queue for whatever is next. The same address the
            // plain form post below uses, so the two paths land on one banner rather
            // than two different ones to keep in step.
            return Json(new { submitted = true, queueUrl = "/retoucher?tab=ReadyForReview&submitted=true" });
        }

        TempData["Submitted"] = true;
        return Redirect("/retoucher?tab=ReadyForReview");
    }

    /// <summary>
    /// Admins may open any client's workspace; a retoucher is limited to their own
    /// assignment or unclaimed work.
    /// </summary>
    private async Task<bool> IsAllowedAsync(Guid clientId, CancellationToken cancellationToken)
    {
        if (User.IsInRole(Roles.SuperAdmin) || User.IsInRole(Roles.Admin))
        {
            return true;
        }

        return await retouchers.CanOpenAsync(clientId, CurrentUserId(), cancellationToken);
    }

    private Guid CurrentUserId() =>
        Guid.TryParse(userManager.GetUserId(User), out var id) ? id : Guid.Empty;

    private async Task<RetoucherWorkspaceViewModel?> BuildAsync(Guid clientId, CancellationToken cancellationToken)
    {
        var client = await db.ClientProfiles
            .Include(c => c.Portfolio)
            .Include(c => c.GuardianConsent)
            .FirstOrDefaultAsync(c => c.Id == clientId, cancellationToken);

        if (client is null)
        {
            return null;
        }

        var options = mediaOptions.Value;
        var pool = await media.GetPoolAsync(clientId, cancellationToken);

        // Worked out here rather than in the page: the rule is one thing, in one place,
        // and the page only ticks what it chose.
        var room = options.PortfolioImageLimit - pool.Count(a => a.IsSelectedForPortfolio);
        var suggested = PhotographRanking.Suggest(pool, room).ToHashSet();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var assignment = await retouchers.GetAssignmentAsync(clientId, cancellationToken);

        return new RetoucherWorkspaceViewModel
        {
            ClientId = clientId,
            ClientName = client.PublicName,
            Status = client.Portfolio?.Status ?? Domain.Enums.PortfolioStatus.AwaitingClientInformation,
            AssignmentStatus = assignment?.Status,
            Assets =
            [
                .. pool.Select(a => new MediaAssetViewModel
                {
                    Id = a.Id,
                    Filename = a.OriginalFilename,
                    Orientation = a.Orientation,
                    IsSelected = a.IsSelectedForPortfolio,
                    IsFeatured = a.IsFeatured,
                    Width = a.Width,
                    Height = a.Height,
                    FocalPointX = a.FocalPointX,
                    FocalPointY = a.FocalPointY,
                    IsSuggested = suggested.Contains(a.Id)
                })
            ],
            PoolLimit = options.MediaPoolImageLimit,
            PortfolioLimit = options.PortfolioImageLimit,
            MaxImageBytes = options.MaxImageBytes,
            AllowedContentTypes = options.AllowedImageContentTypes,
            GuardianApprovalPending = client.IsBlockedPendingGuardianConsent(today)
        };
    }
}
