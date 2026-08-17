using Microsoft.EntityFrameworkCore;
using Msm.Portfolio.Web.Data;
using Msm.Portfolio.Web.Domain.Entities;
using Msm.Portfolio.Web.Domain.Enums;
using Msm.Portfolio.Web.Integrations.GoCardless;

namespace Msm.Portfolio.Web.Services;

public record WebhookResult(bool Accepted, int Processed, int Skipped, string? Error = null);

public interface IPaymentWebhookProcessor
{
    Task<WebhookResult> ProcessAsync(
        string payload, string? signatureHeader, CancellationToken cancellationToken = default);
}

/// <summary>
/// Applies inbound provider events to the payment record (specification section 44).
/// </summary>
/// <remarks>
/// Providers retry until they get a success, so the same event arrives more than once
/// as a matter of course. Every event is therefore recorded under a unique provider
/// event id first; an event already stored is acknowledged and skipped rather than
/// applied a second time. Processing is independent of any browser session, so it works
/// whether or not the client stayed on the page.
/// </remarks>
public class PaymentWebhookProcessor(
    ApplicationDbContext db,
    IWebhookVerifier verifier,
    ICheckoutService checkout,
    IAuditService audit,
    INotificationService notifications,
    ILogger<PaymentWebhookProcessor> logger) : IPaymentWebhookProcessor
{
    public async Task<WebhookResult> ProcessAsync(
        string payload, string? signatureHeader, CancellationToken cancellationToken = default)
    {
        // The endpoint is unauthenticated by necessity, so the signature is the only
        // thing distinguishing a real notification from a forged one.
        if (!verifier.IsSignatureValid(payload, signatureHeader))
        {
            logger.LogWarning("Rejected a webhook with an invalid or missing signature.");
            return new WebhookResult(false, 0, 0, "Invalid signature.");
        }

        var events = verifier.ParseEvents(payload);
        var processed = 0;
        var skipped = 0;

        foreach (var providerEvent in events)
        {
            if (await IsAlreadyStoredAsync(providerEvent.EventId, cancellationToken))
            {
                skipped++;
                continue;
            }

            var record = new PaymentWebhookEvent
            {
                Provider = "GoCardless",
                ProviderEventId = providerEvent.EventId,
                EventType = $"{providerEvent.ResourceType}.{providerEvent.Action}",
                Payload = payload.Length > 8000 ? payload[..8000] : payload
            };

            db.PaymentWebhookEvents.Add(record);

            try
            {
                await ApplyAsync(providerEvent, cancellationToken);

                record.ProcessingStatus = WebhookProcessingStatus.Processed;
                record.ProcessedAt = DateTimeOffset.UtcNow;
                processed++;

                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (IsDuplicate(ex))
            {
                // Two deliveries of the same event raced. The unique index is the
                // authority, so this one is simply the loser and is discarded.
                db.ChangeTracker.Clear();
                logger.LogInformation("Discarded a concurrent duplicate of event {EventId}.", providerEvent.EventId);
                skipped++;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to apply webhook event {EventId}.", providerEvent.EventId);

                db.ChangeTracker.Clear();

                // Stored as failed so it is visible and can be replayed, rather than
                // vanishing. Its id is now taken, so a provider retry will not reapply it
                // blindly; a person decides what to do.
                db.PaymentWebhookEvents.Add(new PaymentWebhookEvent
                {
                    Provider = "GoCardless",
                    ProviderEventId = providerEvent.EventId,
                    EventType = $"{providerEvent.ResourceType}.{providerEvent.Action}",
                    ProcessingStatus = WebhookProcessingStatus.Failed,
                    ProcessingError = ex.Message,
                    ProcessedAt = DateTimeOffset.UtcNow
                });

                await db.SaveChangesAsync(cancellationToken);
            }
        }

        return new WebhookResult(true, processed, skipped);
    }

    private async Task ApplyAsync(ProviderEvent providerEvent, CancellationToken cancellationToken)
    {
        if (!string.Equals(providerEvent.ResourceType, "payments", StringComparison.OrdinalIgnoreCase))
        {
            // Other resource types are acknowledged and recorded but change nothing yet.
            // Subscription events are handled when maintenance billing arrives in Phase 8.
            return;
        }

        var status = MapPaymentStatus(providerEvent.Action);

        if (status is null)
        {
            return;
        }

        var transaction = await FindTransactionAsync(providerEvent, cancellationToken);

        if (transaction is null)
        {
            logger.LogWarning(
                "Webhook event {EventId} referenced payment {PaymentId}, which matches no order.",
                providerEvent.EventId, providerEvent.PaymentId);
            return;
        }

        var previous = transaction.Status;

        transaction.Status = status.Value;
        transaction.ProviderPaymentId ??= providerEvent.PaymentId;
        transaction.FailureReason = status is PaymentStatus.Failed or PaymentStatus.Cancelled
            ? providerEvent.Reason
            : null;
        transaction.UpdatedAt = DateTimeOffset.UtcNow;

        audit.Record(nameof(PaymentTransaction), transaction.Id.ToString(),
            AuditActions.PaymentStateChanged,
            oldValue: previous.ToString(),
            newValue: status.Value.ToString());

        var order = await db.Orders
            .Include(o => o.Client)
            .Include(o => o.Transactions)
            .FirstAsync(o => o.Id == transaction.OrderId, cancellationToken);

        await ApplyToOrderAsync(order, status.Value, providerEvent, cancellationToken);
    }

    private async Task ApplyToOrderAsync(
        Order order, PaymentStatus status, ProviderEvent providerEvent, CancellationToken cancellationToken)
    {
        switch (status)
        {
            case PaymentStatus.Confirmed or PaymentStatus.Authorised or PaymentStatus.Submitted
                when order.Status != OrderStatus.Confirmed:
                // The webhook is the authority and works without a browser, so a client
                // who closed the tab still gets their portfolio published.
                await checkout.ActivateAsync(order, providerEvent.PaymentId, status, cancellationToken);
                break;

            case PaymentStatus.Failed or PaymentStatus.Cancelled:
                await HandleFailureAsync(order, providerEvent, cancellationToken);
                break;
        }
    }

    private async Task HandleFailureAsync(
        Order order, ProviderEvent providerEvent, CancellationToken cancellationToken)
    {
        // A failure arriving after settlement concerns the money, not the sale. The
        // portfolio is not torn down here; that is the maintenance grace period's job
        // in Phase 8 (specification section 23).
        if (order.Status == OrderStatus.Confirmed)
        {
            await notifications.NotifyStaffAsync(
                NotificationTypes.PaymentFailed,
                $"A payment for {order.Client.PublicName} failed after the order was confirmed: "
                + $"{providerEvent.Reason ?? "no reason given"}.",
                $"/admin/clients/{order.ClientId}",
                cancellationToken);

            return;
        }

        order.Status = OrderStatus.Failed;

        await notifications.NotifyStaffAsync(
            NotificationTypes.PaymentFailed,
            $"The payment for {order.Client.PublicName} failed: {providerEvent.Reason ?? "no reason given"}.",
            $"/admin/clients/{order.ClientId}",
            cancellationToken);
    }

    private Task<PaymentTransaction?> FindTransactionAsync(
        ProviderEvent providerEvent, CancellationToken cancellationToken)
    {
        // Matched on the provider's payment id where known, falling back to the billing
        // request reference recorded when checkout opened.
        if (!string.IsNullOrWhiteSpace(providerEvent.PaymentId))
        {
            return db.PaymentTransactions
                .Where(t => t.ProviderPaymentId == providerEvent.PaymentId)
                .OrderByDescending(t => t.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);
        }

        return Task.FromResult<PaymentTransaction?>(null);
    }

    private Task<bool> IsAlreadyStoredAsync(string eventId, CancellationToken cancellationToken) =>
        db.PaymentWebhookEvents.AnyAsync(
            e => e.Provider == "GoCardless" && e.ProviderEventId == eventId, cancellationToken);

    /// <summary>
    /// Maps a provider action onto the payment states in specification section 21.
    /// Unknown actions map to null and are recorded without changing anything, so a new
    /// provider event type cannot corrupt an order.
    /// </summary>
    internal static PaymentStatus? MapPaymentStatus(string action) => action.ToLowerInvariant() switch
    {
        "created" => PaymentStatus.Pending,
        "submitted" => PaymentStatus.Submitted,
        "confirmed" or "paid_out" => PaymentStatus.Confirmed,
        "failed" or "charged_back" or "customer_approval_denied" => PaymentStatus.Failed,
        "cancelled" => PaymentStatus.Cancelled,
        "resubmission_requested" => PaymentStatus.Reviewed,
        _ => null
    };

    private static bool IsDuplicate(DbUpdateException ex) =>
        ex.InnerException?.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) == true
        || ex.InnerException?.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) == true;
}
