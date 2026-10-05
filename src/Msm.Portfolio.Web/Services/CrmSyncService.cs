using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Msm.Portfolio.Web.Configuration;
using Msm.Portfolio.Web.Data;
using Msm.Portfolio.Web.Domain.Enums;
using Msm.Portfolio.Web.Integrations.HighLevel;

namespace Msm.Portfolio.Web.Services;

public record CrmSyncSummary(int Attempted, int Succeeded, int Failed, int Skipped);

public interface ICrmSyncService
{
    /// <summary>
    /// Pushes one client's current state to the CRM. Returns false when the push did
    /// not succeed; the caller is never expected to act on that beyond logging, because
    /// a CRM problem must not affect the portfolio (specification section 45).
    /// </summary>
    Task<bool> SyncClientAsync(Guid clientId, CancellationToken cancellationToken = default);

    /// <summary>Pushes everything currently due, including previous failures now off backoff.</summary>
    Task<CrmSyncSummary> SyncPendingAsync(CancellationToken cancellationToken = default);

    /// <summary>Counts by sync state, for the integration status shown to staff.</summary>
    Task<IReadOnlyDictionary<CrmSyncStatus, int>> GetStateSummaryAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Mirrors portfolio state onto the GoHighLevel contact (specification sections 25 and 45).
/// </summary>
/// <remarks>
/// The CRM is downstream of this application, never the other way round. Nothing here
/// changes a portfolio, an order or a subscription; the only fields written are the
/// sync bookkeeping ones. That is what makes specification section 45's rule hold: a
/// successfully purchased portfolio is never rolled back because the CRM was
/// temporarily unavailable.
/// </remarks>
public class CrmSyncService(
    ApplicationDbContext db,
    IHighLevelService crm,
    INotificationService notifications,
    IOptions<MsmBrandOptions> brandOptions,
    ILogger<CrmSyncService> logger) : ICrmSyncService
{
    /// <summary>
    /// After this many consecutive failures, staff are told once. Beyond it the row
    /// keeps retrying quietly rather than alerting on every pass.
    /// </summary>
    private const int AlertAfterAttempts = 3;

    private const int MaxBackoffMinutes = 360;

    public async Task<bool> SyncClientAsync(Guid clientId, CancellationToken cancellationToken = default)
    {
        var portfolio = await db.Portfolios
            .Include(p => p.Client)
            .FirstOrDefaultAsync(p => p.ClientId == clientId, cancellationToken);

        if (portfolio is null)
        {
            return false;
        }

        var succeeded = await PushAsync(portfolio, cancellationToken);

        // Saved here as well as in the batch path. Without this the push would happen
        // but the result would never be recorded, so the portfolio would be pushed
        // again on every pass for ever.
        await db.SaveChangesAsync(cancellationToken);

        return succeeded;
    }

    public async Task<CrmSyncSummary> SyncPendingAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;

        var due = await db.Portfolios
            .Include(p => p.Client)
            .Where(p => (p.CrmSyncStatus == CrmSyncStatus.Pending || p.CrmSyncStatus == CrmSyncStatus.Failed)
                        && (p.CrmSyncNextAttemptAt == null || p.CrmSyncNextAttemptAt <= now))
            // Bounded so one pass cannot take an unbounded amount of time when a long
            // outage has left a large backlog; the next pass picks up the remainder.
            .OrderBy(p => p.CrmSyncNextAttemptAt ?? p.UpdatedAt)
            .Take(100)
            .ToListAsync(cancellationToken);

        var succeeded = 0;
        var failed = 0;
        var skipped = 0;

        foreach (var portfolio in due)
        {
            if (string.IsNullOrWhiteSpace(portfolio.Client.GhlContactId))
            {
                // No CRM contact to update. Not a failure: a client created directly by
                // staff has no CRM record, and retrying forever would be pointless.
                portfolio.CrmSyncStatus = CrmSyncStatus.NotSynced;
                portfolio.CrmSyncError = "No CRM contact is linked to this client.";
                skipped++;
                continue;
            }

            if (await PushAsync(portfolio, cancellationToken))
            {
                succeeded++;
            }
            else
            {
                failed++;
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        return new CrmSyncSummary(due.Count, succeeded, failed, skipped);
    }

    private async Task<bool> PushAsync(
        Domain.Entities.Portfolio portfolio, CancellationToken cancellationToken)
    {
        var contactId = portfolio.Client.GhlContactId;

        if (string.IsNullOrWhiteSpace(contactId))
        {
            portfolio.CrmSyncStatus = CrmSyncStatus.NotSynced;
            portfolio.CrmSyncError = "No CRM contact is linked to this client.";
            return false;
        }

        var fields = await BuildFieldsAsync(portfolio, cancellationToken);

        CrmUpdateResult result;

        try
        {
            result = await crm.UpdateContactAsync(contactId, fields, cancellationToken);
        }
        catch (Exception ex)
        {
            // The CRM boundary must not be able to throw into a caller that was doing
            // something else entirely, such as publishing a portfolio.
            logger.LogError(ex, "The CRM push for client {ClientId} threw.", portfolio.ClientId);
            result = new CrmUpdateResult(false, ex.Message);
        }

        var now = DateTimeOffset.UtcNow;

        if (result.Succeeded)
        {
            portfolio.CrmSyncStatus = CrmSyncStatus.Synced;
            portfolio.CrmSyncedAt = now;
            portfolio.CrmSyncError = null;
            portfolio.CrmSyncAttempts = 0;
            portfolio.CrmSyncNextAttemptAt = null;

            return true;
        }

        portfolio.CrmSyncStatus = CrmSyncStatus.Failed;
        portfolio.CrmSyncError = result.Error;
        portfolio.CrmSyncAttempts++;

        if (result.IsRetryable)
        {
            // Exponential backoff, capped. A CRM that is down should not be retried at
            // the same rate indefinitely, but the work is never abandoned either.
            var minutes = Math.Min(MaxBackoffMinutes, (int)Math.Pow(2, Math.Min(portfolio.CrmSyncAttempts, 8)));
            portfolio.CrmSyncNextAttemptAt = now.AddMinutes(minutes);
        }
        else
        {
            // Nothing will change by trying again, so it waits for a person instead of
            // occupying the queue.
            portfolio.CrmSyncNextAttemptAt = null;
            portfolio.CrmSyncStatus = CrmSyncStatus.Failed;
        }

        if (portfolio.CrmSyncAttempts == AlertAfterAttempts)
        {
            await notifications.NotifyStaffAsync(
                NotificationTypes.CrmSyncFailing,
                $"The CRM has not accepted updates for {portfolio.Client.PublicName} after "
                + $"{portfolio.CrmSyncAttempts} attempts: {result.Error}. Their portfolio is unaffected.",
                $"/admin/clients/{portfolio.ClientId}",
                cancellationToken);
        }

        logger.LogWarning(
            "CRM sync failed for client {ClientId} (attempt {Attempt}): {Error}",
            portfolio.ClientId, portfolio.CrmSyncAttempts, result.Error);

        return false;
    }

    /// <summary>
    /// Assembles the fields in specification section 25 from the client's current state.
    /// </summary>
    private async Task<CrmContactFields> BuildFieldsAsync(
        Domain.Entities.Portfolio portfolio, CancellationToken cancellationToken)
    {
        var order = await db.Orders
            .Where(o => o.ClientId == portfolio.ClientId && o.Status == OrderStatus.Confirmed)
            .OrderByDescending(o => o.ConfirmedAt)
            .FirstOrDefaultAsync(cancellationToken);

        var subscription = await db.MaintenanceSubscriptions
            .Where(s => s.ClientId == portfolio.ClientId)
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        var publicUrl = portfolio is { IsPublished: true, Slug: { } slug }
            ? $"{brandOptions.Value.PublicDomain.TrimEnd('/')}/{slug}"
            : null;

        return new CrmContactFields(
            publicUrl,
            // "Live" rather than "Published", because this is read by MSM staff and
            // their existing automations, not by developers.
            portfolio.IsPublished ? "Live" : portfolio.Status.ToString(),
            order is null ? "Not purchased" : "Purchased",
            order?.ConfirmedAt,
            subscription?.Status.ToString() ?? "None",
            portfolio.PublishedAt);
    }

    public async Task<IReadOnlyDictionary<CrmSyncStatus, int>> GetStateSummaryAsync(
        CancellationToken cancellationToken = default)
    {
        var rows = await db.Portfolios
            .GroupBy(p => p.CrmSyncStatus)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(r => r.Status, r => r.Count);
    }
}
