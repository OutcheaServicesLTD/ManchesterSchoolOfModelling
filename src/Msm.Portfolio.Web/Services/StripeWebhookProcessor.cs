using Microsoft.EntityFrameworkCore;
using Msm.Portfolio.Web.Data;
using Msm.Portfolio.Web.Domain.Entities;
using Msm.Portfolio.Web.Domain.Enums;
using Msm.Portfolio.Web.Integrations.Stripe;

namespace Msm.Portfolio.Web.Services;

public interface IStripeWebhookProcessor
{
    Task<WebhookResult> ProcessAsync(
        string payload, string? signatureHeader, CancellationToken cancellationToken = default);
}

/// <summary>
/// Applies inbound Stripe events to the portfolio-maintenance subscription
/// (specification version 2, item 3).
/// </summary>
/// <remarks>
/// The same shape as <see cref="PaymentWebhookProcessor"/>: verify the signature first,
/// record every event under a unique provider event id before doing anything else, and
/// treat an event already stored as already handled. It shares that table
/// (<c>PaymentWebhookEvents</c>) with GoCardless — the unique index is on
/// (Provider, ProviderEventId), so "Stripe" and "GoCardless" event streams cannot
/// collide with each other no matter how their ids happen to look.
/// </remarks>
public class StripeWebhookProcessor(
    ApplicationDbContext db,
    IStripeWebhookVerifier verifier,
    IMaintenanceService maintenance,
    ILogger<StripeWebhookProcessor> logger) : IStripeWebhookProcessor
{
    private const string Provider = "Stripe";

    public async Task<WebhookResult> ProcessAsync(
        string payload, string? signatureHeader, CancellationToken cancellationToken = default)
    {
        // The endpoint is unauthenticated by necessity, so the signature is the only
        // thing distinguishing a real notification from a forged one.
        if (!verifier.IsSignatureValid(payload, signatureHeader))
        {
            logger.LogWarning("Rejected a Stripe webhook with an invalid or missing signature.");
            return new WebhookResult(false, 0, 0, "Invalid signature.");
        }

        var evt = verifier.ParseEvent(payload);

        if (evt is null)
        {
            return new WebhookResult(false, 0, 0, "The event could not be read.");
        }

        if (await db.PaymentWebhookEvents.AnyAsync(
                e => e.Provider == Provider && e.ProviderEventId == evt.EventId, cancellationToken))
        {
            return new WebhookResult(true, 0, 1);
        }

        var record = new PaymentWebhookEvent
        {
            Provider = Provider,
            ProviderEventId = evt.EventId,
            EventType = evt.Type,
            Payload = payload.Length > 8000 ? payload[..8000] : payload
        };

        db.PaymentWebhookEvents.Add(record);

        try
        {
            await ApplyAsync(evt, cancellationToken);

            record.ProcessingStatus = WebhookProcessingStatus.Processed;
            record.ProcessedAt = DateTimeOffset.UtcNow;

            await db.SaveChangesAsync(cancellationToken);

            return new WebhookResult(true, 1, 0);
        }
        catch (DbUpdateException ex) when (IsDuplicate(ex))
        {
            // Two deliveries of the same event raced. The unique index is the
            // authority, so this one is simply the loser and is discarded.
            db.ChangeTracker.Clear();
            logger.LogInformation("Discarded a concurrent duplicate of Stripe event {EventId}.", evt.EventId);
            return new WebhookResult(true, 0, 1);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to apply Stripe webhook event {EventId}.", evt.EventId);

            db.ChangeTracker.Clear();

            // Stored as failed so it is visible and can be investigated, rather than
            // vanishing. Its id is now taken, so a Stripe retry will not reapply it
            // blindly.
            db.PaymentWebhookEvents.Add(new PaymentWebhookEvent
            {
                Provider = Provider,
                ProviderEventId = evt.EventId,
                EventType = evt.Type,
                ProcessingStatus = WebhookProcessingStatus.Failed,
                ProcessingError = ex.Message,
                ProcessedAt = DateTimeOffset.UtcNow
            });

            await db.SaveChangesAsync(cancellationToken);

            // Still accepted: Stripe would otherwise retry an event that failed for a
            // reason retrying cannot fix, and the failure is already recorded for a
            // person to look at.
            return new WebhookResult(true, 0, 0, ex.Message);
        }
    }

    private async Task ApplyAsync(StripeWebhookEvent evt, CancellationToken cancellationToken)
    {
        switch (evt.Type)
        {
            case "checkout.session.completed":
                await ApplyCheckoutCompletedAsync(evt, cancellationToken);
                break;

            case "invoice.paid":
                await ApplyToSubscriptionAsync(
                    evt, (clientId, ct) => maintenance.RecordPaymentSuccessAsync(clientId, ct), cancellationToken);
                break;

            case "invoice.payment_failed":
                await ApplyToSubscriptionAsync(
                    evt,
                    (clientId, ct) => maintenance.RecordPaymentFailureAsync(clientId, evt.Reason, ct),
                    cancellationToken);
                break;

            case "customer.subscription.deleted":
                // Stripe sends this once the paid period is genuinely over — not on a
                // cancel-at-period-end request, which keeps the subscription "active"
                // until then. No extra grace period applies on top of one already used.
                await ApplyToSubscriptionAsync(
                    evt, (clientId, ct) => maintenance.RecordCancelledAsync(clientId, ct), cancellationToken);
                break;

            // customer.subscription.updated is deliberately not handled: invoice.paid
            // and invoice.payment_failed already drive the grace-period machinery, and
            // acting on both would risk applying the same state change twice from two
            // different event types for the one underlying change.
        }
    }

    /// <summary>
    /// Activates a subscription for the first time. The only event that carries a
    /// client reference directly — everything after this looks the client up by the
    /// subscription id instead (specification version 2, item 3).
    /// </summary>
    private async Task ApplyCheckoutCompletedAsync(StripeWebhookEvent evt, CancellationToken cancellationToken)
    {
        // Nothing else in this application opens a Stripe Checkout Session, so a
        // completion with no subscription attached is not one of ours.
        if (string.IsNullOrWhiteSpace(evt.SubscriptionId))
        {
            return;
        }

        var clientIdText = evt.ClientReferenceId ?? evt.ClientIdFromMetadata;

        if (!Guid.TryParse(clientIdText, out var clientId))
        {
            logger.LogWarning(
                "Stripe checkout.session.completed for subscription {SubscriptionId} carried no "
                + "usable client reference.", evt.SubscriptionId);
            return;
        }

        if (!string.IsNullOrWhiteSpace(evt.CustomerId))
        {
            var client = await db.ClientProfiles.FindAsync([clientId], cancellationToken);

            if (client is not null && client.StripeCustomerId != evt.CustomerId)
            {
                client.StripeCustomerId = evt.CustomerId;
            }
        }

        var product = await db.Products.FirstOrDefaultAsync(
            p => p.Code == ProductCodes.PortfolioMaintenance, cancellationToken);

        await maintenance.ActivateSubscriptionAsync(
            clientId, Provider, evt.SubscriptionId, product?.Price ?? 0m, product?.Currency ?? "GBP",
            cancellationToken);
    }

    /// <summary>
    /// Resolves the client from the subscription id and applies an action already
    /// known about (specification version 2, item 3).
    /// </summary>
    private async Task ApplyToSubscriptionAsync(
        StripeWebhookEvent evt,
        Func<Guid, CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(evt.SubscriptionId))
        {
            return;
        }

        var subscription = await maintenance.FindByProviderIdAsync(evt.SubscriptionId, cancellationToken);

        if (subscription is null)
        {
            // Stripe does not guarantee delivery order. An invoice event for a
            // subscription this application has not recorded yet — checkout.session.
            // completed still in flight — is not an error, only a race that resolves
            // itself once that event arrives.
            logger.LogInformation(
                "Stripe event {EventId} referenced subscription {SubscriptionId}, which matches no "
                + "client yet.", evt.EventId, evt.SubscriptionId);
            return;
        }

        await action(subscription.ClientId, cancellationToken);
    }

    private static bool IsDuplicate(DbUpdateException ex) =>
        ex.InnerException?.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) == true
        || ex.InnerException?.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) == true;
}
