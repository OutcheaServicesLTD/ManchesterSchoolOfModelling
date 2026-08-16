using Microsoft.EntityFrameworkCore;
using Msm.Portfolio.Web.Data;
using Msm.Portfolio.Web.Domain.Entities;
using Msm.Portfolio.Web.Domain.Enums;

namespace Msm.Portfolio.Web.Services;

/// <summary>The tabs on the retoucher dashboard (specification section 6).</summary>
public enum RetoucherQueueTab
{
    Waiting = 0,
    InProgress = 1,
    ReadyForReview = 2,
    Completed = 3
}

/// <summary>One row in the queue (specification section 6).</summary>
public record QueueItem(
    Guid ClientId,
    string ClientName,
    DateTimeOffset SubmittedAt,
    int ImageCount,
    int SelectedCount,
    PortfolioStatus Status,
    string? AssignedRetoucher,
    bool AssignedToMe,
    bool GuardianApprovalPending);

public record QueueCounts(int Waiting, int InProgress, int ReadyForReview, int Completed);

public interface IRetoucherService
{
    Task<IReadOnlyList<QueueItem>> GetQueueAsync(
        RetoucherQueueTab tab, Guid retoucherUserId, CancellationToken cancellationToken = default);

    Task<QueueCounts> GetCountsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether this retoucher may open the client's workspace. Admins bypass this;
    /// retouchers are limited to their own work or unclaimed work.
    /// </summary>
    Task<bool> CanOpenAsync(Guid clientId, Guid retoucherUserId, CancellationToken cancellationToken = default);

    /// <summary>Claims unassigned work and moves the portfolio into Retouching.</summary>
    Task<(bool Succeeded, string? Error)> StartWorkAsync(
        Guid clientId, Guid retoucherUserId, CancellationToken cancellationToken = default);

    Task<(bool Succeeded, string? Error)> SubmitForReviewAsync(
        Guid clientId, Guid retoucherUserId, CancellationToken cancellationToken = default);

    Task<RetoucherAssignment?> GetAssignmentAsync(Guid clientId, CancellationToken cancellationToken = default);
}

public class RetoucherService(
    ApplicationDbContext db,
    IAuditService audit,
    INotificationService notifications,
    ILogger<RetoucherService> logger) : IRetoucherService
{
    /// <summary>
    /// Statuses where the retoucher's part is finished. Anything past review has moved
    /// on to Admin, the viewing room or the client.
    /// </summary>
    private static readonly PortfolioStatus[] CompletedStatuses =
    [
        PortfolioStatus.InViewing, PortfolioStatus.AwaitingPurchase, PortfolioStatus.Purchased,
        PortfolioStatus.Published, PortfolioStatus.PaymentWarning, PortfolioStatus.Unpublished,
        PortfolioStatus.NoSale, PortfolioStatus.Archived
    ];

    public async Task<IReadOnlyList<QueueItem>> GetQueueAsync(
        RetoucherQueueTab tab, Guid retoucherUserId, CancellationToken cancellationToken = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var query =
            from portfolio in db.Portfolios
            join client in db.ClientProfiles on portfolio.ClientId equals client.Id
            where StatusesFor(tab).Contains(portfolio.Status)
            select new
            {
                client.Id,
                client.FirstName,
                client.LastName,
                client.DisplayName,
                client.DateOfBirth,
                portfolio.Status,
                portfolio.CreatedAt,
                GuardianStatus = client.GuardianConsent == null
                    ? (GuardianConsentStatus?)null
                    : client.GuardianConsent.Status,
                ImageCount = db.MediaAssets.Count(m =>
                    m.ClientId == client.Id && !m.IsDeleted && m.MediaType == MediaType.Image),
                SelectedCount = db.MediaAssets.Count(m =>
                    m.ClientId == client.Id && !m.IsDeleted
                    && m.MediaType == MediaType.Image && m.IsSelectedForPortfolio),
                Assignment = db.RetoucherAssignments
                    .Where(a => a.ClientId == client.Id)
                    .OrderByDescending(a => a.AssignedAt)
                    .Select(a => new { a.RetoucherUserId, a.RetoucherUser.FirstName, a.RetoucherUser.LastName })
                    .FirstOrDefault()
            };

        var rows = await query.ToListAsync(cancellationToken);

        return
        [
            .. rows
                .OrderBy(r => r.CreatedAt)
                .Select(r =>
                {
                    var profile = new ClientProfile { DateOfBirth = r.DateOfBirth };

                    return new QueueItem(
                        r.Id,
                        string.IsNullOrWhiteSpace(r.DisplayName) ? $"{r.FirstName} {r.LastName}".Trim() : r.DisplayName,
                        r.CreatedAt,
                        r.ImageCount,
                        r.SelectedCount,
                        r.Status,
                        r.Assignment is null ? null : $"{r.Assignment.FirstName} {r.Assignment.LastName}".Trim(),
                        r.Assignment is not null && r.Assignment.RetoucherUserId == retoucherUserId,
                        // Surfaced so a retoucher knows the portfolio cannot complete yet,
                        // without blocking them from preparing it (specification section 11).
                        profile.RequiresGuardianConsent(today)
                            && r.GuardianStatus != GuardianConsentStatus.Approved);
                })
        ];
    }

    public async Task<QueueCounts> GetCountsAsync(CancellationToken cancellationToken = default)
    {
        var byStatus = await db.Portfolios
            .GroupBy(p => p.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        int CountFor(RetoucherQueueTab tab) =>
            byStatus.Where(s => StatusesFor(tab).Contains(s.Status)).Sum(s => s.Count);

        return new QueueCounts(
            CountFor(RetoucherQueueTab.Waiting),
            CountFor(RetoucherQueueTab.InProgress),
            CountFor(RetoucherQueueTab.ReadyForReview),
            CountFor(RetoucherQueueTab.Completed));
    }

    public async Task<bool> CanOpenAsync(
        Guid clientId, Guid retoucherUserId, CancellationToken cancellationToken = default)
    {
        var portfolio = await db.Portfolios
            .FirstOrDefaultAsync(p => p.ClientId == clientId, cancellationToken);

        if (portfolio is null)
        {
            return false;
        }

        var assignment = await GetAssignmentAsync(clientId, cancellationToken);

        // Unclaimed work sitting in the queue is open to any retoucher to pick up.
        if (assignment is null)
        {
            return portfolio.Status == PortfolioStatus.ReadyForRetoucher;
        }

        // Otherwise it belongs to the retoucher it was assigned to, so two people
        // cannot unknowingly prepare the same portfolio (specification section 6).
        return assignment.RetoucherUserId == retoucherUserId;
    }

    public async Task<(bool Succeeded, string? Error)> StartWorkAsync(
        Guid clientId, Guid retoucherUserId, CancellationToken cancellationToken = default)
    {
        var portfolio = await db.Portfolios.FirstOrDefaultAsync(p => p.ClientId == clientId, cancellationToken);

        if (portfolio is null)
        {
            return (false, "That client could not be found.");
        }

        var assignment = await GetAssignmentAsync(clientId, cancellationToken);

        if (assignment is not null && assignment.RetoucherUserId != retoucherUserId)
        {
            return (false, "Another retoucher is already working on this client.");
        }

        if (assignment is null)
        {
            assignment = new RetoucherAssignment
            {
                ClientId = clientId,
                RetoucherUserId = retoucherUserId
            };

            db.RetoucherAssignments.Add(assignment);
        }

        assignment.Status = RetoucherAssignmentStatus.InProgress;
        assignment.StartedAt ??= DateTimeOffset.UtcNow;

        if (portfolio.Status == PortfolioStatus.ReadyForRetoucher)
        {
            portfolio.Status = PortfolioStatus.Retouching;
            portfolio.UpdatedAt = DateTimeOffset.UtcNow;

            audit.Record(nameof(Domain.Entities.Portfolio), portfolio.Id.ToString(),
                AuditActions.PortfolioStatusChanged,
                userId: retoucherUserId,
                oldValue: PortfolioStatus.ReadyForRetoucher.ToString(),
                newValue: PortfolioStatus.Retouching.ToString());
        }

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Retoucher {UserId} started work on client {ClientId}.", retoucherUserId, clientId);

        return (true, null);
    }

    public async Task<(bool Succeeded, string? Error)> SubmitForReviewAsync(
        Guid clientId, Guid retoucherUserId, CancellationToken cancellationToken = default)
    {
        var portfolio = await db.Portfolios.FirstOrDefaultAsync(p => p.ClientId == clientId, cancellationToken);
        var client = await db.ClientProfiles.FirstOrDefaultAsync(c => c.Id == clientId, cancellationToken);

        if (portfolio is null || client is null)
        {
            return (false, "That client could not be found.");
        }

        var selectedCount = await db.MediaAssets.CountAsync(
            m => m.ClientId == clientId && !m.IsDeleted
                 && m.MediaType == MediaType.Image && m.IsSelectedForPortfolio,
            cancellationToken);

        // An empty portfolio would reach Admin with nothing to review.
        if (selectedCount == 0)
        {
            return (false, "Choose at least one photograph for the portfolio before sending it for review.");
        }

        if (portfolio.FeaturedMediaId is null)
        {
            return (false, "Choose a main image before sending the portfolio for review.");
        }

        var assignment = await GetAssignmentAsync(clientId, cancellationToken);
        if (assignment is not null)
        {
            assignment.Status = RetoucherAssignmentStatus.ReadyForReview;
            assignment.SubmittedForReviewAt = DateTimeOffset.UtcNow;
        }

        var previous = portfolio.Status;
        portfolio.Status = PortfolioStatus.ReadyForReview;
        portfolio.UpdatedAt = DateTimeOffset.UtcNow;

        audit.Record(nameof(Domain.Entities.Portfolio), portfolio.Id.ToString(),
            AuditActions.PortfolioStatusChanged,
            userId: retoucherUserId,
            oldValue: previous.ToString(),
            newValue: PortfolioStatus.ReadyForReview.ToString());

        await notifications.NotifyStaffAsync(
            NotificationTypes.PortfolioReadyForReview,
            $"{client.PublicName}'s portfolio is ready for review ({selectedCount} images).",
            "/admin",
            cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        return (true, null);
    }

    public Task<RetoucherAssignment?> GetAssignmentAsync(
        Guid clientId, CancellationToken cancellationToken = default) =>
        db.RetoucherAssignments
            .Include(a => a.RetoucherUser)
            .Where(a => a.ClientId == clientId)
            .OrderByDescending(a => a.AssignedAt)
            .FirstOrDefaultAsync(cancellationToken);

    private static PortfolioStatus[] StatusesFor(RetoucherQueueTab tab) => tab switch
    {
        RetoucherQueueTab.Waiting => [PortfolioStatus.ReadyForRetoucher],
        RetoucherQueueTab.InProgress => [PortfolioStatus.Retouching],
        RetoucherQueueTab.ReadyForReview => [PortfolioStatus.ReadyForReview],
        RetoucherQueueTab.Completed => CompletedStatuses,
        _ => []
    };
}
