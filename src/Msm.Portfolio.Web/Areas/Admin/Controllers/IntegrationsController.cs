using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Msm.Portfolio.Web.Authorization;
using Msm.Portfolio.Web.Data;
using Msm.Portfolio.Web.Domain.Enums;
using Msm.Portfolio.Web.Integrations.Bio;
using Msm.Portfolio.Web.Integrations.GoCardless;
using Msm.Portfolio.Web.Integrations.HighLevel;
using Msm.Portfolio.Web.Integrations.Stripe;
using Msm.Portfolio.Web.Services;
using Msm.Portfolio.Web.ViewModels;

namespace Msm.Portfolio.Web.Areas.Admin.Controllers;

/// <summary>
/// The state of the external integrations (specification section 4).
/// </summary>
[Area("Admin")]
[Route("admin/integrations")]
[Authorize(Policy = Permissions.System.ViewIntegrationState)]
public class IntegrationsController(
    ApplicationDbContext db,
    ICrmSyncService crmSync,
    IHighLevelService crm,
    IGoCardlessService payments,
    IStripeService subscriptions,
    IBiographyWriter biographies) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken = default)
    {
        var states = await crmSync.GetStateSummaryAsync(cancellationToken);

        var failing = await db.Portfolios
            .Include(p => p.Client)
            .Where(p => p.CrmSyncStatus == CrmSyncStatus.Failed)
            .OrderByDescending(p => p.CrmSyncAttempts)
            .Take(25)
            .Select(p => new CrmFailureRow(
                p.ClientId,
                p.Client.DisplayName ?? (p.Client.FirstName + " " + p.Client.LastName),
                p.CrmSyncAttempts,
                p.CrmSyncError,
                p.CrmSyncNextAttemptAt))
            .ToListAsync(cancellationToken);

        return View(new IntegrationsViewModel
        {
            CrmIsLive = crm.IsLive,
            PaymentsIsLive = payments.IsLive,
            SubscriptionsAreLive = subscriptions.IsLive,
            BiographiesAreOn = biographies.IsEnabled,
            BiographiesPending = await db.ClientProfiles.CountAsync(
                c => c.BiographyDraftStatus == BiographyDraftStatus.Pending, cancellationToken),
            BiographiesFailed = await db.ClientProfiles.CountAsync(
                c => c.BiographyDraftStatus == BiographyDraftStatus.Failed, cancellationToken),
            CrmStates = states,
            RecentCrmFailures = failing,
            // Scoped to GoCardless: Stripe's own webhook events land in the same table
            // and are counted separately below, so this card's numbers are not quietly
            // inflated by a second provider's traffic.
            WebhookEventsReceived = await db.PaymentWebhookEvents
                .CountAsync(e => e.Provider == "GoCardless", cancellationToken),
            WebhookEventsFailed = await db.PaymentWebhookEvents
                .CountAsync(e => e.Provider == "GoCardless"
                    && e.ProcessingStatus == WebhookProcessingStatus.Failed, cancellationToken),
            SubscriptionWebhookEventsReceived = await db.PaymentWebhookEvents
                .CountAsync(e => e.Provider == "Stripe", cancellationToken),
            SubscriptionWebhookEventsFailed = await db.PaymentWebhookEvents
                .CountAsync(e => e.Provider == "Stripe"
                    && e.ProcessingStatus == WebhookProcessingStatus.Failed, cancellationToken)
        });
    }

    /// <summary>Retries a client's CRM push immediately rather than waiting for the worker.</summary>
    [HttpPost("crm/{clientId:guid}/retry")]
    public async Task<IActionResult> Retry(Guid clientId, CancellationToken cancellationToken = default)
    {
        var portfolio = await db.Portfolios.FirstOrDefaultAsync(p => p.ClientId == clientId, cancellationToken);

        if (portfolio is null)
        {
            return NotFound();
        }

        // Clearing the backoff is what makes this a retry now rather than a queued one.
        portfolio.RequestCrmSync();
        portfolio.CrmSyncAttempts = 0;
        await db.SaveChangesAsync(cancellationToken);

        // Called once and the result reused: evaluating it twice would push to the CRM
        // twice for a single click.
        var succeeded = await crmSync.SyncClientAsync(clientId, cancellationToken);

        TempData[succeeded ? "Saved" : "Error"] = succeeded
            ? "The CRM was updated."
            : "The CRM still could not be updated. It will keep retrying.";

        return RedirectToAction(nameof(Index));
    }
}
