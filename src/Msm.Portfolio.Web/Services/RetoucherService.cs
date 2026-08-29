using Microsoft.EntityFrameworkCore;
using Msm.Portfolio.Web.Authorization;
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
    bool GuardianApprovalPending,
    /// <summary>
    /// Whether the person reading the queue can actually open this one. Claimed work
    /// belongs to whoever claimed it, and a queue that offers an Open button to everybody
    /// answers half of them with Access denied and no explanation.
    /// </summary>
    bool CanOpen,

    /// <summary>
    /// Arrived in the last day. Marked and sorted first, because the queue is otherwise
    /// oldest-first and a portfolio submitted this morning appears below a fortnight of
    /// older work — which is the one row somebody opening the queue is looking for.
    /// </summary>
    bool IsNew);

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
    /// <summary>
    /// Moves a client's retouching to somebody else, or releases it back to the queue when
    /// <paramref name="toRetoucherUserId"/> is null.
    /// </summary>
    /// <remarks>
    /// Claimed work belongs to whoever claimed it, so that two people cannot unknowingly
    /// prepare the same portfolio. Without a way to hand it over, work claimed by somebody
    /// who has left — or claimed by mistake — is stuck with them for good, and the only
    /// remedy is the database.
    /// </remarks>
    Task<(bool Succeeded, string? Error)> ReassignAsync(
        Guid clientId, Guid? toRetoucherUserId, Guid? actingUserId,
        CancellationToken cancellationToken = default);

    Task<(bool Succeeded, string? Error)> StartWorkAsync(
        Guid clientId, Guid retoucherUserId, CancellationToken cancellationToken = default);

    Task<(bool Succeeded, string? Error)> SubmitForReviewAsync(
        Guid clientId, Guid retoucherUserId, CancellationToken cancellationToken = default);

    Task<RetoucherAssignment?> GetAssignmentAsync(Guid clientId, CancellationToken cancellationToken = default);
}

public class RetoucherService(
    ApplicationDbContext db,
    IPortfolioService portfolios,
    IAuditService audit,
    INotificationService notifications,
    ILogger<RetoucherService> logger) : IRetoucherService
{
    /// <summary>
    /// Statuses where a sale is in progress or done. Submitting now carries a portfolio
    /// straight through to <see cref="PortfolioStatus.InViewing"/> (see
    /// <see cref="SubmitForReviewAsync"/>), so that status moved into the "ready for
    /// review" tab below rather than here — from a retoucher's chair it is still simply
    /// "submitted, not yet bought", and payment is what actually marks their part done.
    /// </summary>
    private static readonly PortfolioStatus[] CompletedStatuses =
    [
        PortfolioStatus.AwaitingPurchase, PortfolioStatus.Purchased,
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
                // When it last changed state, which for a submission is when it arrived.
                portfolio.UpdatedAt,
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

        // A day. Long enough that work arriving overnight is still marked when the studio
        // opens, short enough that the mark still means something.
        var newSince = DateTimeOffset.UtcNow.AddDays(-1);

        return
        [
            .. rows
                // New first, then the old order: oldest waiting the longest, so the queue
                // still reads as a queue underneath.
                .OrderByDescending(r => r.UpdatedAt >= newSince)
                .ThenByDescending(r => r.UpdatedAt >= newSince ? r.UpdatedAt : DateTimeOffset.MinValue)
                .ThenBy(r => r.CreatedAt)
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
                            && r.GuardianStatus != GuardianConsentStatus.Approved,
                        // Unclaimed work waiting in the queue is open to anybody; claimed
                        // work is open to the person it was claimed by. Same rule the
                        // workspace enforces, asked here so the row can say so instead of
                        // offering a button that answers Access denied.
                        CanOpen: r.Assignment is null
                            ? r.Status == PortfolioStatus.ReadyForRetoucher
                            : r.Assignment.RetoucherUserId == retoucherUserId,
                        IsNew: r.UpdatedAt >= newSince);
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

    public async Task<(bool Succeeded, string? Error)> ReassignAsync(
        Guid clientId, Guid? toRetoucherUserId, Guid? actingUserId,
        CancellationToken cancellationToken = default)
    {
        var portfolio = await db.Portfolios.FirstOrDefaultAsync(p => p.ClientId == clientId, cancellationToken);

        if (portfolio is null)
        {
            return (false, "That client could not be found.");
        }

        var assignment = await GetAssignmentAsync(clientId, cancellationToken);
        var before = assignment is null
            ? "unassigned"
            : (await db.Users.Where(u => u.Id == assignment.RetoucherUserId)
                .Select(u => u.Email).FirstOrDefaultAsync(cancellationToken) ?? "someone");

        if (toRetoucherUserId is null)
        {
            // Released. The row is removed rather than blanked, because an assignment with
            // nobody in it is what an unclaimed client already looks like — no row at all.
            if (assignment is not null)
            {
                db.RetoucherAssignments.Remove(assignment);
            }

            // Back to waiting, or the queue would show it in progress with nobody on it.
            if (portfolio.Status == PortfolioStatus.Retouching)
            {
                portfolio.Status = PortfolioStatus.ReadyForRetoucher;
                portfolio.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }
        else
        {
            var isRetoucher = await (
                from user in db.Users
                join userRole in db.UserRoles on user.Id equals userRole.UserId
                join role in db.Roles on userRole.RoleId equals role.Id
                where user.Id == toRetoucherUserId && user.IsActive
                      && (role.Name == Roles.Retoucher || role.Name == Roles.Admin
                          || role.Name == Roles.SuperAdmin)
                select user.Id).AnyAsync(cancellationToken);

            if (!isRetoucher)
            {
                return (false, "That person cannot be given retouching work.");
            }

            if (assignment is null)
            {
                assignment = new RetoucherAssignment { ClientId = clientId };
                db.RetoucherAssignments.Add(assignment);
            }

            assignment.RetoucherUserId = toRetoucherUserId.Value;
            assignment.AssignedAt = DateTimeOffset.UtcNow;

            // Whoever picks it up starts where the last person left off, so the queue tab
            // it appears under does not change under them.
            if (assignment.Status == RetoucherAssignmentStatus.Waiting)
            {
                assignment.Status = RetoucherAssignmentStatus.InProgress;
                assignment.StartedAt ??= DateTimeOffset.UtcNow;
            }

            if (portfolio.Status == PortfolioStatus.ReadyForRetoucher)
            {
                portfolio.Status = PortfolioStatus.Retouching;
                portfolio.UpdatedAt = DateTimeOffset.UtcNow;
            }

            // Told, rather than left to notice: somebody now has work they did not claim.
            notifications.NotifyUser(
                toRetoucherUserId.Value,
                NotificationTypes.PortfolioReadyForReview,
                "A client's retouching has been passed to you.",
                $"/retoucher/client/{clientId}");
        }

        var after = toRetoucherUserId is null
            ? "the queue"
            : (await db.Users.Where(u => u.Id == toRetoucherUserId)
                .Select(u => u.Email).FirstOrDefaultAsync(cancellationToken) ?? "someone");

        audit.Record(nameof(RetoucherAssignment), clientId.ToString(),
            AuditActions.RetouchingReassigned,
            userId: actingUserId, oldValue: before, newValue: after);

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Retouching for client {ClientId} moved from {Before} to {After}.", clientId, before, after);

        return (true, null);
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

        // Used to wait here for an administrator to click "Mark in viewing" before the
        // client could be shown anything or a sale could start (specification section
        // 27, version 1). That click checked exactly what this method already checked
        // above — a chosen main image, at least one selected photograph — so it added a
        // step without adding a decision. Reusing MarkInViewingAsync rather than
        // duplicating its transition keeps the biography-draft trigger and audit trail
        // in the one place that owns them; an administrator can still return the
        // portfolio to retouching or unpublish it later exactly as before.
        var viewing = await portfolios.MarkInViewingAsync(clientId, retoucherUserId, cancellationToken);

        if (!viewing.Succeeded)
        {
            // The checks above make this unreachable in practice — both guards
            // MarkInViewingAsync applies were just satisfied — but the submission itself
            // still succeeded and must not be reported as a failure over it.
            logger.LogWarning(
                "Portfolio {ClientId} reached ReadyForReview but could not advance to InViewing: {Reason}",
                clientId, viewing.Error);
        }

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
        // InViewing is included: submitting carries a portfolio straight there now (see
        // SubmitForReviewAsync), and this tab is still where a retoucher looks to find
        // what they have sent, right up until it is actually bought.
        RetoucherQueueTab.ReadyForReview => [PortfolioStatus.ReadyForReview, PortfolioStatus.InViewing],
        RetoucherQueueTab.Completed => CompletedStatuses,
        _ => []
    };
}
