using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Msm.Portfolio.Web.Authorization;
using Msm.Portfolio.Web.Configuration;
using Msm.Portfolio.Web.Data;
using Msm.Portfolio.Web.Domain.Entities;
using Msm.Portfolio.Web.Domain.Enums;
using Msm.Portfolio.Web.Services;
using Msm.Portfolio.Web.ViewModels;

namespace Msm.Portfolio.Web.Areas.Admin.Controllers;

/// <summary>
/// One client's record: profile, media and portfolio lifecycle (specification section 5).
/// </summary>
/// <remarks>
/// Actions are gated individually by permission rather than by the Admin role, because
/// the specification requires that staff accounts can hold different privileges. An
/// Admin without the permanent-deletion permission simply cannot reach that action.
/// </remarks>
[Area("Admin")]
[Route("admin/clients/{clientId:guid}")]
[Authorize(Policy = Policies.AdminArea)]
public class ClientsController(
    ApplicationDbContext db,
    IMediaService media,
    IPortfolioService portfolios,
    IRetoucherService retouchers,
    IMeasurementTemplateProvider templates,
    IAuditService audit,
    UserManager<ApplicationUser> userManager,
    IOptions<MediaOptions> mediaOptions,
    IOptions<MsmBrandOptions> brandOptions) : Controller
{
    [HttpGet("")]
    [Authorize(Policy = Permissions.Clients.ViewAll)]
    public async Task<IActionResult> Index(Guid clientId, CancellationToken cancellationToken = default)
    {
        var model = await BuildAsync(clientId, cancellationToken);

        return model is null ? NotFound() : View(model);
    }

    [HttpPost("profile")]
    [Authorize(Policy = Permissions.Clients.Edit)]
    public async Task<IActionResult> Profile(
        Guid clientId, AdminClientEditViewModel model, CancellationToken cancellationToken = default)
    {
        var client = await db.ClientProfiles.FirstOrDefaultAsync(c => c.Id == clientId, cancellationToken);

        if (client is null)
        {
            return NotFound();
        }

        if (!ModelState.IsValid)
        {
            TempData["Error"] = "Please check the details and try again.";
            return RedirectToAction(nameof(Index), new { clientId });
        }

        var before = $"{client.FullName}, {client.Location}, DOB {client.DateOfBirth}";

        client.FirstName = model.FirstName.Trim();
        client.LastName = model.LastName.Trim();
        client.DisplayName = string.IsNullOrWhiteSpace(model.DisplayName) ? null : model.DisplayName.Trim();
        client.DateOfBirth = model.DateOfBirth;
        client.Location = model.Location?.Trim();
        client.ModelProfileType = model.ModelProfileType;
        client.Biography = model.Biography?.Trim();
        client.HairColour = model.HairColour?.Trim();
        client.EyeColour = model.EyeColour?.Trim();
        client.UpdatedAt = DateTimeOffset.UtcNow;

        audit.Record(nameof(ClientProfile), clientId.ToString(), AuditActions.AdminEditedClient,
            userId: CurrentUserId(), oldValue: before,
            newValue: $"{client.FullName}, {client.Location}, DOB {client.DateOfBirth}");

        await db.SaveChangesAsync(cancellationToken);
        TempData["Saved"] = "Client details updated.";

        return RedirectToAction(nameof(Index), new { clientId });
    }

    [HttpPost("media/{assetId:guid}/select")]
    [Authorize(Policy = Permissions.Media.Select)]
    public async Task<IActionResult> Select(
        Guid clientId, Guid assetId, bool selected, CancellationToken cancellationToken = default)
    {
        var (_, error) = await media.SetSelectedAsync(clientId, assetId, selected, CurrentUserId(), cancellationToken);
        TempData["Error"] = error;

        return RedirectToAction(nameof(Index), new { clientId });
    }

    /// <summary>Admin can override the retoucher's or client's choice (specification section 12).</summary>
    [HttpPost("media/{assetId:guid}/featured")]
    [Authorize(Policy = Permissions.Media.SetFeatured)]
    public async Task<IActionResult> Featured(
        Guid clientId, Guid assetId, CancellationToken cancellationToken = default)
    {
        var (_, error) = await media.SetFeaturedAsync(clientId, assetId, CurrentUserId(), cancellationToken);
        TempData["Error"] = error;

        return RedirectToAction(nameof(Index), new { clientId });
    }

    [HttpPost("media/{assetId:guid}/move")]
    [Authorize(Policy = Permissions.Media.Select)]
    public async Task<IActionResult> Move(
        Guid clientId, Guid assetId, int direction, CancellationToken cancellationToken = default)
    {
        var pool = await media.GetPoolAsync(clientId, cancellationToken);
        var ordered = pool.Where(a => a.IsSelectedForPortfolio).Select(a => a.Id).ToList();

        var index = ordered.IndexOf(assetId);
        var target = index + Math.Sign(direction);

        if (index >= 0 && target >= 0 && target < ordered.Count)
        {
            (ordered[index], ordered[target]) = (ordered[target], ordered[index]);
            var rest = pool.Where(a => !a.IsSelectedForPortfolio).Select(a => a.Id);
            await media.ReorderAsync(clientId, [.. ordered, .. rest], CurrentUserId(), cancellationToken);
        }

        return RedirectToAction(nameof(Index), new { clientId });
    }

    [HttpPost("media/{assetId:guid}/remove")]
    [Authorize(Policy = Permissions.Media.Delete)]
    public async Task<IActionResult> RemoveMedia(
        Guid clientId, Guid assetId, CancellationToken cancellationToken = default)
    {
        await media.SoftDeleteAsync(clientId, assetId, CurrentUserId(), cancellationToken);

        return RedirectToAction(nameof(Index), new { clientId });
    }

    [HttpPost("status/viewing")]
    [Authorize(Policy = Permissions.Portfolios.ChangeStatus)]
    public Task<IActionResult> MarkInViewing(Guid clientId, CancellationToken cancellationToken = default) =>
        RunAsync(clientId, () => portfolios.MarkInViewingAsync(clientId, CurrentUserId(), cancellationToken),
            "Portfolio moved to viewing.");

    [HttpPost("status/return")]
    [Authorize(Policy = Permissions.Portfolios.ChangeStatus)]
    public Task<IActionResult> ReturnToRetoucher(Guid clientId, CancellationToken cancellationToken = default) =>
        RunAsync(clientId, () => portfolios.ReturnToRetoucherAsync(clientId, CurrentUserId(), cancellationToken),
            "Portfolio sent back to the retoucher.");

    [HttpPost("status/no-sale")]
    [Authorize(Policy = Permissions.Payments.MarkNoSale)]
    public Task<IActionResult> MarkNoSale(Guid clientId, CancellationToken cancellationToken = default) =>
        RunAsync(clientId, () => portfolios.MarkNoSaleAsync(clientId, CurrentUserId(), cancellationToken),
            "Recorded as no sale and archived.");

    [HttpPost("publish")]
    [Authorize(Policy = Permissions.Portfolios.Publish)]
    public Task<IActionResult> Publish(Guid clientId, CancellationToken cancellationToken = default) =>
        RunAsync(clientId, () => portfolios.PublishAsync(clientId, CurrentUserId(), cancellationToken),
            "Portfolio published.");

    [HttpPost("unpublish")]
    [Authorize(Policy = Permissions.Portfolios.Unpublish)]
    public Task<IActionResult> Unpublish(
        Guid clientId, string? reason = null, CancellationToken cancellationToken = default) =>
        RunAsync(clientId, () => portfolios.UnpublishAsync(clientId, CurrentUserId(), reason, cancellationToken),
            "Portfolio unpublished.");

    [HttpPost("archive")]
    [Authorize(Policy = Permissions.Portfolios.Archive)]
    public Task<IActionResult> Archive(Guid clientId, CancellationToken cancellationToken = default) =>
        RunAsync(clientId, () => portfolios.ArchiveAsync(clientId, CurrentUserId(), cancellationToken),
            "Portfolio archived.");

    [HttpPost("slug")]
    [Authorize(Policy = Permissions.Portfolios.Edit)]
    public Task<IActionResult> Slug(
        Guid clientId, string slug, CancellationToken cancellationToken = default) =>
        RunAsync(clientId, () => portfolios.ChangeSlugAsync(clientId, slug, CurrentUserId(), cancellationToken),
            "Web address updated.");

    [HttpPost("restore")]
    [Authorize(Policy = Permissions.Portfolios.Restore)]
    public Task<IActionResult> Restore(Guid clientId, CancellationToken cancellationToken = default) =>
        RunAsync(clientId, () => portfolios.RestoreAsync(clientId, CurrentUserId(), cancellationToken),
            "Portfolio restored.");

    /// <summary>
    /// Destroys the portfolio and its media. Reserved to Super Admin
    /// (specification section 4), so an ordinary Admin cannot reach it even by
    /// posting directly.
    /// </summary>
    [HttpPost("delete")]
    [Authorize(Policy = Permissions.Portfolios.DeletePermanently)]
    public async Task<IActionResult> DeletePermanently(
        Guid clientId, CancellationToken cancellationToken = default)
    {
        var result = await portfolios.DeletePermanentlyAsync(clientId, CurrentUserId(), cancellationToken);

        if (!result.Succeeded)
        {
            TempData["Error"] = result.Error;
            return RedirectToAction(nameof(Index), new { clientId });
        }

        TempData["Saved"] = "Portfolio permanently deleted.";
        return Redirect("/admin");
    }

    private async Task<IActionResult> RunAsync(
        Guid clientId, Func<Task<OperationResult>> action, string success)
    {
        var result = await action();

        if (result.Succeeded)
        {
            TempData["Saved"] = success;
        }
        else
        {
            TempData["Error"] = result.Error;
        }

        return RedirectToAction(nameof(Index), new { clientId });
    }

    private Guid? CurrentUserId() =>
        Guid.TryParse(userManager.GetUserId(User), out var id) ? id : null;

    private async Task<AdminClientDetailViewModel?> BuildAsync(Guid clientId, CancellationToken cancellationToken)
    {
        var client = await db.ClientProfiles
            .Include(c => c.Portfolio)
            .Include(c => c.GuardianConsent)
            .Include(c => c.Measurements)
            .Include(c => c.ApplicationUser)
            .FirstOrDefaultAsync(c => c.Id == clientId, cancellationToken);

        if (client is null)
        {
            return null;
        }

        var pool = await media.GetPoolAsync(clientId, cancellationToken);
        var selfTape = await media.GetSelfTapeAsync(clientId, cancellationToken);
        var assignment = await retouchers.GetAssignmentAsync(clientId, cancellationToken);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var brand = brandOptions.Value;

        return new AdminClientDetailViewModel
        {
            ClientId = clientId,
            Client = client,
            Portfolio = client.Portfolio,
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
                    Height = a.Height
                })
            ],
            SelfTapeId = selfTape?.Id,
            RetoucherName = assignment is null
                ? null
                : $"{assignment.RetoucherUser.FirstName} {assignment.RetoucherUser.LastName}".Trim(),
            MeasurementTemplate = templates.GetTemplate(client.ModelProfileType),
            PortfolioLimit = mediaOptions.Value.PortfolioImageLimit,
            PoolLimit = mediaOptions.Value.MediaPoolImageLimit,
            GuardianApprovalPending = client.IsBlockedPendingGuardianConsent(today),
            PublishBlocker = await portfolios.DescribePublishBlockerAsync(clientId, cancellationToken),
            PublicUrlBase = brand.PublicDomain.TrimEnd('/')
        };
    }
}
