using Microsoft.EntityFrameworkCore;
using Msm.Portfolio.Web.Data;
using Msm.Portfolio.Web.Domain.Entities;
using Msm.Portfolio.Web.Domain.Enums;
using Msm.Portfolio.Web.Integrations.Bio;
using Msm.Portfolio.Web.Storage;

namespace Msm.Portfolio.Web.Services;

public record OperationResult(bool Succeeded, string? Error = null)
{
    public static OperationResult Ok() => new(true);

    public static OperationResult Fail(string error) => new(false, error);
}

public interface IPortfolioService
{
    /// <summary>Moves a reviewed portfolio into the viewing room (specification section 27).</summary>
    Task<OperationResult> MarkInViewingAsync(Guid clientId, Guid? userId, CancellationToken cancellationToken = default);

    /// <summary>Sends a portfolio back to the retoucher when review finds a problem.</summary>
    Task<OperationResult> ReturnToRetoucherAsync(Guid clientId, Guid? userId, CancellationToken cancellationToken = default);

    /// <summary>Records that the client declined (specification section 48).</summary>
    Task<OperationResult> MarkNoSaleAsync(Guid clientId, Guid? userId, CancellationToken cancellationToken = default);

    Task<OperationResult> PublishAsync(Guid clientId, Guid? userId, CancellationToken cancellationToken = default);

    Task<OperationResult> UnpublishAsync(Guid clientId, Guid? userId, string? reason = null, CancellationToken cancellationToken = default);

    Task<OperationResult> ArchiveAsync(Guid clientId, Guid? userId, CancellationToken cancellationToken = default);

    /// <summary>Super Admin only (specification section 4).</summary>
    Task<OperationResult> RestoreAsync(Guid clientId, Guid? userId, CancellationToken cancellationToken = default);

    /// <summary>Super Admin only. Destroys the portfolio and its media (specification section 4).</summary>
    Task<OperationResult> DeletePermanentlyAsync(Guid clientId, Guid? userId, CancellationToken cancellationToken = default);

    Task<OperationResult> ChangeSlugAsync(Guid clientId, string slug, Guid? userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Why a portfolio cannot currently be published, or null when it can. Used to
    /// explain a disabled Publish button rather than only rejecting the attempt.
    /// </summary>
    Task<string?> DescribePublishBlockerAsync(Guid clientId, CancellationToken cancellationToken = default);
}

public class PortfolioService(
    ApplicationDbContext db,
    ISlugService slugs,
    IMediaStorageService storage,
    IAuditService audit,
    INotificationService notifications,
    IBiographyWriter biographies,
    ILogger<PortfolioService> logger) : IPortfolioService
{
    public async Task<OperationResult> MarkInViewingAsync(
        Guid clientId, Guid? userId, CancellationToken cancellationToken = default)
    {
        var (client, portfolio) = await LoadAsync(clientId, cancellationToken);

        if (client is null || portfolio is null)
        {
            return OperationResult.Fail("That client could not be found.");
        }

        if (portfolio.Status is not (PortfolioStatus.ReadyForReview or PortfolioStatus.Retouching))
        {
            return OperationResult.Fail("Only a portfolio that has been prepared can be moved to viewing.");
        }

        if (portfolio.FeaturedMediaId is null)
        {
            return OperationResult.Fail("Choose a main image before moving this portfolio to viewing.");
        }

        Transition(portfolio, PortfolioStatus.InViewing, userId);

        // Approval is the moment a biography becomes worth suggesting: the photographs
        // are chosen, the measurements are in, and somebody is about to have to write
        // one. Asked for once and only ever as a draft — marked here and written on a
        // worker, so a provider that is slow or down cannot delay this approval or fail
        // it. Nothing is asked for at all when no provider is configured, and nothing is
        // asked for when a biography already exists.
        if (biographies.IsEnabled && client.RequestBiographyDraft())
        {
            logger.LogInformation("A biography draft was requested for {ClientId}.", clientId);
        }

        await db.SaveChangesAsync(cancellationToken);

        return OperationResult.Ok();
    }

    public async Task<OperationResult> ReturnToRetoucherAsync(
        Guid clientId, Guid? userId, CancellationToken cancellationToken = default)
    {
        var (_, portfolio) = await LoadAsync(clientId, cancellationToken);

        if (portfolio is null)
        {
            return OperationResult.Fail("That client could not be found.");
        }

        if (portfolio.IsPublished)
        {
            return OperationResult.Fail("Unpublish the portfolio before sending it back for more work.");
        }

        Transition(portfolio, PortfolioStatus.Retouching, userId);

        var assignment = await db.RetoucherAssignments
            .Where(a => a.ClientId == clientId)
            .OrderByDescending(a => a.AssignedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (assignment is not null)
        {
            assignment.Status = RetoucherAssignmentStatus.InProgress;
            assignment.SubmittedForReviewAt = null;

            // Tell the retoucher who prepared it, rather than leaving it to reappear
            // silently in their queue.
            notifications.NotifyUser(
                assignment.RetoucherUserId,
                NotificationTypes.PortfolioReturnedToRetoucher,
                "A portfolio has been sent back for more work.",
                $"/retoucher/client/{clientId}");
        }

        await db.SaveChangesAsync(cancellationToken);

        return OperationResult.Ok();
    }

    public async Task<OperationResult> MarkNoSaleAsync(
        Guid clientId, Guid? userId, CancellationToken cancellationToken = default)
    {
        var (_, portfolio) = await LoadAsync(clientId, cancellationToken);

        if (portfolio is null)
        {
            return OperationResult.Fail("That client could not be found.");
        }

        if (portfolio.IsPublished)
        {
            return OperationResult.Fail("This portfolio is live and cannot be marked as no sale.");
        }

        Transition(portfolio, PortfolioStatus.NoSale, userId);

        // Archived rather than deleted: the work stays available to staff until a
        // Super Admin chooses to remove it (specification section 48).
        Transition(portfolio, PortfolioStatus.Archived, userId);

        await db.SaveChangesAsync(cancellationToken);

        return OperationResult.Ok();
    }

    public async Task<OperationResult> PublishAsync(
        Guid clientId, Guid? userId, CancellationToken cancellationToken = default)
    {
        var (client, portfolio) = await LoadAsync(clientId, cancellationToken);

        if (client is null || portfolio is null)
        {
            return OperationResult.Fail("That client could not be found.");
        }

        if (await DescribePublishBlockerAsync(clientId, cancellationToken) is { } blocker)
        {
            return OperationResult.Fail(blocker);
        }

        // Assigned once and then left alone, so a link already shared with an agency
        // keeps working even if the model later changes their display name
        // (specification section 39).
        portfolio.Slug ??= await slugs.GenerateUniqueAsync(client.PublicName, portfolio.Id, cancellationToken);

        portfolio.IsPublished = true;
        portfolio.PublishedAt ??= DateTimeOffset.UtcNow;
        portfolio.UnpublishedAt = null;

        // The CRM needs to learn the new URL and status. Marked only: the push itself
        // happens on a worker, so a CRM that is down cannot delay publication
        // (specification section 45).
        portfolio.RequestCrmSync();

        Transition(portfolio, PortfolioStatus.Published, userId);

        notifications.NotifyUser(
            client.ApplicationUserId,
            NotificationTypes.PortfolioPublished,
            "Your portfolio is now live.",
            "/client");

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Portfolio {PortfolioId} published at /{Slug}.", portfolio.Id, portfolio.Slug);

        return OperationResult.Ok();
    }

    public async Task<OperationResult> UnpublishAsync(
        Guid clientId, Guid? userId, string? reason = null, CancellationToken cancellationToken = default)
    {
        var (client, portfolio) = await LoadAsync(clientId, cancellationToken);

        if (client is null || portfolio is null)
        {
            return OperationResult.Fail("That client could not be found.");
        }

        if (!portfolio.IsPublished)
        {
            return OperationResult.Fail("This portfolio is not currently public.");
        }

        portfolio.IsPublished = false;
        portfolio.UnpublishedAt = DateTimeOffset.UtcNow;
        portfolio.RequestCrmSync();

        Transition(portfolio, PortfolioStatus.Unpublished, userId, reason);

        notifications.NotifyUser(
            client.ApplicationUserId,
            NotificationTypes.PortfolioUnpublished,
            "Your portfolio is no longer public. Please contact us if you have questions.",
            "/client");

        await db.SaveChangesAsync(cancellationToken);

        return OperationResult.Ok();
    }

    public async Task<OperationResult> ArchiveAsync(
        Guid clientId, Guid? userId, CancellationToken cancellationToken = default)
    {
        var (_, portfolio) = await LoadAsync(clientId, cancellationToken);

        if (portfolio is null)
        {
            return OperationResult.Fail("That client could not be found.");
        }

        if (portfolio.IsPublished)
        {
            return OperationResult.Fail("Unpublish the portfolio before archiving it.");
        }

        Transition(portfolio, PortfolioStatus.Archived, userId);
        await db.SaveChangesAsync(cancellationToken);

        return OperationResult.Ok();
    }

    public async Task<OperationResult> RestoreAsync(
        Guid clientId, Guid? userId, CancellationToken cancellationToken = default)
    {
        var (_, portfolio) = await LoadAsync(clientId, cancellationToken);

        if (portfolio is null)
        {
            return OperationResult.Fail("That client could not be found.");
        }

        if (portfolio.Status != PortfolioStatus.Archived)
        {
            return OperationResult.Fail("Only an archived portfolio can be restored.");
        }

        // Comes back unpublished. Restoring recovers the work, not the public listing,
        // which is a separate deliberate decision.
        Transition(portfolio, PortfolioStatus.Unpublished, userId);
        await db.SaveChangesAsync(cancellationToken);

        return OperationResult.Ok();
    }

    public async Task<OperationResult> DeletePermanentlyAsync(
        Guid clientId, Guid? userId, CancellationToken cancellationToken = default)
    {
        var (client, portfolio) = await LoadAsync(clientId, cancellationToken);

        if (client is null || portfolio is null)
        {
            return OperationResult.Fail("That client could not be found.");
        }

        if (portfolio.IsPublished)
        {
            return OperationResult.Fail("Unpublish the portfolio before deleting it permanently.");
        }

        var assets = await db.MediaAssets.Where(m => m.ClientId == clientId).ToListAsync(cancellationToken);

        // Files first: if this fails partway the rows still point at what remains, which
        // is recoverable. Deleting rows first would orphan files with nothing naming them.
        foreach (var asset in assets)
        {
            foreach (var variant in Enum.GetValues<MediaVariant>())
            {
                var key = asset.MediaType == MediaType.SelfTape
                    ? asset.StorageKey
                    : MediaStorageKeys.ForVariant(asset.StorageKey, variant);

                await storage.DeleteAsync(key, cancellationToken);
            }
        }

        db.MediaAssets.RemoveRange(assets);
        db.Portfolios.Remove(portfolio);

        // The audit entry deliberately outlives the record it describes, so a permanent
        // deletion is itself permanently accounted for (specification section 36).
        audit.Record(nameof(Domain.Entities.Portfolio), portfolio.Id.ToString(),
            AuditActions.PortfolioDeletedPermanently,
            userId: userId,
            oldValue: $"{client.FullName}, {assets.Count} media assets, slug {portfolio.Slug ?? "none"}");

        await db.SaveChangesAsync(cancellationToken);
        logger.LogWarning(
            "Portfolio {PortfolioId} for client {ClientId} was permanently deleted by {UserId}.",
            portfolio.Id, clientId, userId);

        return OperationResult.Ok();
    }

    public async Task<OperationResult> ChangeSlugAsync(
        Guid clientId, string slug, Guid? userId, CancellationToken cancellationToken = default)
    {
        var (_, portfolio) = await LoadAsync(clientId, cancellationToken);

        if (portfolio is null)
        {
            return OperationResult.Fail("That client could not be found.");
        }

        var normalised = SlugService.Slugify(slug);

        if (string.IsNullOrWhiteSpace(normalised))
        {
            return OperationResult.Fail("Please enter a valid web address.");
        }

        if (!await slugs.IsAvailableAsync(normalised, portfolio.Id, cancellationToken))
        {
            return OperationResult.Fail($"The address '{normalised}' is already taken or reserved.");
        }

        var previous = portfolio.Slug;
        portfolio.Slug = normalised;
        portfolio.UpdatedAt = DateTimeOffset.UtcNow;
        portfolio.RequestCrmSync();

        audit.Record(nameof(Domain.Entities.Portfolio), portfolio.Id.ToString(), AuditActions.SlugChanged,
            userId: userId, oldValue: previous, newValue: normalised);

        await db.SaveChangesAsync(cancellationToken);

        return OperationResult.Ok();
    }

    public async Task<string?> DescribePublishBlockerAsync(
        Guid clientId, CancellationToken cancellationToken = default)
    {
        var (client, portfolio) = await LoadAsync(clientId, cancellationToken);

        if (client is null || portfolio is null)
        {
            return "That client could not be found.";
        }

        // The hard stop from specification section 11: a minor cannot be published
        // until their guardian has approved.
        if (client.IsBlockedPendingGuardianConsent(DateOnly.FromDateTime(DateTime.UtcNow)))
        {
            return "This client is under 18 and their guardian has not yet approved.";
        }

        var selectedCount = await db.MediaAssets.CountAsync(
            m => m.ClientId == clientId && !m.IsDeleted
                 && m.MediaType == MediaType.Image && m.IsSelectedForPortfolio,
            cancellationToken);

        if (selectedCount == 0)
        {
            return "The portfolio has no photographs on it.";
        }

        if (portfolio.FeaturedMediaId is null)
        {
            return "The portfolio has no main image.";
        }

        return null;
    }

    private void Transition(
        Domain.Entities.Portfolio portfolio, PortfolioStatus status, Guid? userId, string? reason = null)
    {
        if (portfolio.Status == status)
        {
            return;
        }

        var previous = portfolio.Status;
        portfolio.Status = status;
        portfolio.UpdatedAt = DateTimeOffset.UtcNow;

        audit.Record(nameof(Domain.Entities.Portfolio), portfolio.Id.ToString(),
            AuditActions.PortfolioStatusChanged,
            userId: userId,
            oldValue: previous.ToString(),
            newValue: reason is null ? status.ToString() : $"{status} ({reason})");
    }

    private async Task<(ClientProfile? Client, Domain.Entities.Portfolio? Portfolio)> LoadAsync(
        Guid clientId, CancellationToken cancellationToken)
    {
        var client = await db.ClientProfiles
            .Include(c => c.GuardianConsent)
            .FirstOrDefaultAsync(c => c.Id == clientId, cancellationToken);

        var portfolio = await db.Portfolios
            .FirstOrDefaultAsync(p => p.ClientId == clientId, cancellationToken);

        return (client, portfolio);
    }
}
