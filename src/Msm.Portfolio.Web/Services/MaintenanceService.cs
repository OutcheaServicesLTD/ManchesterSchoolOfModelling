using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Msm.Portfolio.Web.Configuration;
using Msm.Portfolio.Web.Data;
using Msm.Portfolio.Web.Domain.Entities;
using Msm.Portfolio.Web.Domain.Enums;

namespace Msm.Portfolio.Web.Services;

/// <summary>What the client and staff dashboards need to show about maintenance.</summary>
public record MaintenanceWarning(
    MaintenanceSubscriptionStatus Status,
    int? DaysRemaining,
    DateTimeOffset? GracePeriodEndsAt,
    bool PortfolioTakenDown);

public interface IMaintenanceService
{
    /// <summary>
    /// Records a failed maintenance payment and opens the grace period
    /// (specification section 23).
    /// </summary>
    Task<OperationResult> RecordPaymentFailureAsync(
        Guid clientId, string? reason, CancellationToken cancellationToken = default);

    /// <summary>Clears a payment issue once collection succeeds.</summary>
    Task<OperationResult> RecordPaymentSuccessAsync(
        Guid clientId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes down portfolios whose grace period has run out. Returns how many were
    /// unpublished.
    /// </summary>
    Task<int> ExpireElapsedGracePeriodsAsync(CancellationToken cancellationToken = default);

    /// <summary>The warning to show, or null when there is nothing to warn about.</summary>
    Task<MaintenanceWarning?> GetWarningAsync(Guid clientId, CancellationToken cancellationToken = default);

    Task<MaintenanceSubscription?> FindByProviderIdAsync(
        string providerSubscriptionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// This client's subscription record, or null when they have never started one —
    /// the ordinary case, since starting one is the client's choice (specification
    /// version 2, item 3).
    /// </summary>
    Task<MaintenanceSubscription?> GetForClientAsync(
        Guid clientId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a subscription as active, creating the row the first time a client
    /// subscribes and updating it on a later Checkout completion.
    /// </summary>
    /// <remarks>
    /// A client starts this themselves from their portal, quite separately from the
    /// £99 one-off digital-portfolio purchase.
    /// </remarks>
    Task<MaintenanceSubscription> ActivateSubscriptionAsync(
        Guid clientId,
        string provider,
        string providerSubscriptionId,
        decimal price,
        string currency,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Ends a subscription outright — Stripe's <c>customer.subscription.deleted</c>,
    /// which arrives once the paid period is genuinely over, so no further grace period
    /// applies the way a failed payment gets one.
    /// </summary>
    Task<OperationResult> RecordCancelledAsync(
        Guid clientId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the renewal/expiry date and whether the client has asked to cancel, from
    /// Stripe's <c>customer.subscription.created</c> or <c>.updated</c>. Deliberately
    /// does not touch <see cref="Domain.Entities.MaintenanceSubscription.Status"/>:
    /// Stripe keeps a cancel-pending subscription's status "active" until the period
    /// genuinely ends, so this can run freely without ever ending entitlement early.
    /// </summary>
    Task UpdatePeriodAsync(
        string providerSubscriptionId,
        DateTimeOffset? currentPeriodEnd,
        bool cancelAtPeriodEnd,
        CancellationToken cancellationToken = default);
}

public class MaintenanceService(
    ApplicationDbContext db,
    IPortfolioService portfolios,
    IAuditService audit,
    INotificationService notifications,
    IOptions<CommerceOptions> commerceOptions,
    ILogger<MaintenanceService> logger) : IMaintenanceService
{
    public async Task<OperationResult> RecordPaymentFailureAsync(
        Guid clientId, string? reason, CancellationToken cancellationToken = default)
    {
        var subscription = await db.MaintenanceSubscriptions
            .Include(s => s.Client)
            .FirstOrDefaultAsync(s => s.ClientId == clientId, cancellationToken);

        if (subscription is null)
        {
            return OperationResult.Fail("That client has no maintenance subscription.");
        }

        var now = DateTimeOffset.UtcNow;

        // A second failure inside an open grace period must not restart the clock, or a
        // repeatedly failing payment would keep a portfolio live indefinitely.
        if (subscription.Status == MaintenanceSubscriptionStatus.PaymentIssue
            && subscription.GracePeriodEndsAt is not null)
        {
            logger.LogInformation(
                "Another maintenance payment failed for client {ClientId}; the existing grace period "
                + "ending {EndsAt} is unchanged.", clientId, subscription.GracePeriodEndsAt);

            return OperationResult.Ok();
        }

        var graceDays = commerceOptions.Value.MaintenanceGracePeriodDays;

        subscription.Status = MaintenanceSubscriptionStatus.PaymentIssue;
        subscription.GracePeriodEndsAt = now.AddDays(graceDays);
        subscription.UpdatedAt = now;

        // The portfolio deliberately stays public through the grace period
        // (specification section 23). Only the status changes, so staff and the client
        // can see there is a problem.
        var portfolio = await db.Portfolios.FirstOrDefaultAsync(p => p.ClientId == clientId, cancellationToken);

        if (portfolio is { IsPublished: true })
        {
            portfolio.Status = PortfolioStatus.PaymentWarning;
            portfolio.UpdatedAt = now;
        }

        portfolio?.RequestCrmSync();

        audit.Record(nameof(MaintenanceSubscription), subscription.Id.ToString(),
            AuditActions.MaintenancePaymentFailed,
            newValue: $"Grace period ends {subscription.GracePeriodEndsAt:d MMMM yyyy}. {reason}");

        await notifications.NotifyStaffAsync(
            NotificationTypes.MaintenancePaymentFailed,
            $"{subscription.Client.PublicName}'s maintenance payment failed. Their portfolio stays live "
            + $"until {subscription.GracePeriodEndsAt:d MMMM yyyy}.",
            $"/admin/clients/{clientId}",
            cancellationToken);

        notifications.NotifyUser(
            subscription.Client.ApplicationUserId,
            NotificationTypes.MaintenancePaymentFailed,
            "There is a problem with your portfolio payment. Please contact us so your portfolio stays online.",
            "/client");

        await db.SaveChangesAsync(cancellationToken);

        return OperationResult.Ok();
    }

    public async Task<OperationResult> RecordPaymentSuccessAsync(
        Guid clientId, CancellationToken cancellationToken = default)
    {
        var subscription = await db.MaintenanceSubscriptions
            .Include(s => s.Client)
            .FirstOrDefaultAsync(s => s.ClientId == clientId, cancellationToken);

        if (subscription is null)
        {
            return OperationResult.Fail("That client has no maintenance subscription.");
        }

        var wasInDifficulty = subscription.Status == MaintenanceSubscriptionStatus.PaymentIssue;
        var hadExpired = subscription.Status == MaintenanceSubscriptionStatus.GracePeriodExpired;

        subscription.Status = MaintenanceSubscriptionStatus.Active;
        subscription.GracePeriodEndsAt = null;
        subscription.UpdatedAt = DateTimeOffset.UtcNow;
        // The real renewal date follows separately from Stripe's own
        // customer.subscription.updated (UpdatePeriodAsync); guessing one here would be
        // wrong for anything other than a monthly product.

        if (!wasInDifficulty && !hadExpired)
        {
            await db.SaveChangesAsync(cancellationToken);
            return OperationResult.Ok();
        }

        audit.Record(nameof(MaintenanceSubscription), subscription.Id.ToString(),
            AuditActions.MaintenancePaymentResolved);

        var portfolio = await db.Portfolios.FirstOrDefaultAsync(p => p.ClientId == clientId, cancellationToken);

        // Inside the grace period the portfolio never came down, so resolving simply
        // clears the warning.
        if (portfolio is { IsPublished: true, Status: PortfolioStatus.PaymentWarning })
        {
            portfolio.Status = PortfolioStatus.Published;
            portfolio.UpdatedAt = DateTimeOffset.UtcNow;
        }

        portfolio?.RequestCrmSync();

        notifications.NotifyUser(
            subscription.Client.ApplicationUserId,
            NotificationTypes.MaintenancePaymentResolved,
            "Thank you. Your portfolio payment is up to date.",
            "/client");

        await db.SaveChangesAsync(cancellationToken);

        // A portfolio taken down when the grace period expired is not silently restored:
        // republishing is a deliberate act, and staff are told it is now possible.
        if (hadExpired)
        {
            await notifications.NotifyStaffAsync(
                NotificationTypes.MaintenancePaymentResolved,
                $"{subscription.Client.PublicName} has paid. Their portfolio was taken down when the "
                + "grace period expired and can now be republished.",
                $"/admin/clients/{clientId}",
                cancellationToken);

            await db.SaveChangesAsync(cancellationToken);
        }

        return OperationResult.Ok();
    }

    public async Task<int> ExpireElapsedGracePeriodsAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;

        var due = await db.MaintenanceSubscriptions
            .Include(s => s.Client)
            .Where(s => s.Status == MaintenanceSubscriptionStatus.PaymentIssue
                        && s.GracePeriodEndsAt != null
                        && s.GracePeriodEndsAt <= now)
            .ToListAsync(cancellationToken);

        var unpublished = 0;

        foreach (var subscription in due)
        {
            subscription.Status = MaintenanceSubscriptionStatus.GracePeriodExpired;
            subscription.UpdatedAt = now;

            audit.Record(nameof(MaintenanceSubscription), subscription.Id.ToString(),
                AuditActions.MaintenanceGracePeriodExpired,
                newValue: $"Grace period ended {subscription.GracePeriodEndsAt:d MMMM yyyy}");

            await db.SaveChangesAsync(cancellationToken);

            // Unpublishing also removes the Model Board listing, because the board is
            // queried from published portfolios (specification section 47).
            var result = await portfolios.UnpublishAsync(
                subscription.ClientId, null, "maintenance payment unresolved", cancellationToken);

            if (result.Succeeded)
            {
                unpublished++;

                await notifications.NotifyStaffAsync(
                    NotificationTypes.PortfolioUnpublished,
                    $"{subscription.Client.PublicName}'s portfolio was taken down: the maintenance "
                    + "payment was not resolved within the grace period.",
                    $"/admin/clients/{subscription.ClientId}",
                    cancellationToken);

                await db.SaveChangesAsync(cancellationToken);
            }

            logger.LogWarning(
                "Maintenance grace period expired for client {ClientId}; portfolio unpublished: {Result}.",
                subscription.ClientId, result.Succeeded);
        }

        return unpublished;
    }

    public async Task<MaintenanceWarning?> GetWarningAsync(
        Guid clientId, CancellationToken cancellationToken = default)
    {
        var subscription = await db.MaintenanceSubscriptions
            .FirstOrDefaultAsync(s => s.ClientId == clientId, cancellationToken);

        if (subscription is null)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;

        return subscription.Status switch
        {
            MaintenanceSubscriptionStatus.PaymentIssue => new MaintenanceWarning(
                subscription.Status,
                subscription.DaysRemainingInGracePeriod(now),
                subscription.GracePeriodEndsAt,
                PortfolioTakenDown: false),

            MaintenanceSubscriptionStatus.GracePeriodExpired => new MaintenanceWarning(
                subscription.Status, 0, subscription.GracePeriodEndsAt, PortfolioTakenDown: true),

            _ => null
        };
    }

    public Task<MaintenanceSubscription?> FindByProviderIdAsync(
        string providerSubscriptionId, CancellationToken cancellationToken = default) =>
        db.MaintenanceSubscriptions
            .Include(s => s.Client)
            .FirstOrDefaultAsync(s => s.ProviderSubscriptionId == providerSubscriptionId, cancellationToken);

    public Task<MaintenanceSubscription?> GetForClientAsync(
        Guid clientId, CancellationToken cancellationToken = default) =>
        db.MaintenanceSubscriptions.FirstOrDefaultAsync(s => s.ClientId == clientId, cancellationToken);

    public async Task<MaintenanceSubscription> ActivateSubscriptionAsync(
        Guid clientId,
        string provider,
        string providerSubscriptionId,
        decimal price,
        string currency,
        CancellationToken cancellationToken = default)
    {
        var subscription = await db.MaintenanceSubscriptions
            .FirstOrDefaultAsync(s => s.ClientId == clientId, cancellationToken);

        var now = DateTimeOffset.UtcNow;

        if (subscription is null)
        {
            var product = await db.Products.FirstOrDefaultAsync(
                p => p.Code == Data.ProductCodes.PortfolioMaintenance, cancellationToken);

            subscription = new MaintenanceSubscription
            {
                ClientId = clientId,
                // Falls back to the product row itself only if it is somehow missing —
                // seeding always creates it, so this only guards a database that was
                // never seeded rather than a real branch in ordinary operation.
                ProductId = product?.Id ?? Guid.Empty,
                StartDate = now
            };

            db.MaintenanceSubscriptions.Add(subscription);
        }

        // Any of these means entitlement had lapsed and the portfolio may have been
        // taken down for it — re-subscribing is what brings it back (specification
        // version 2: "if they later subscribe again, the portfolio is re-enabled").
        var wasEnded = subscription.Status is MaintenanceSubscriptionStatus.Cancelled
            or MaintenanceSubscriptionStatus.Ended
            or MaintenanceSubscriptionStatus.GracePeriodExpired;

        subscription.Provider = provider;
        subscription.ProviderSubscriptionId = providerSubscriptionId;
        subscription.PriceAtCreation = price;
        subscription.Currency = currency;
        subscription.Status = MaintenanceSubscriptionStatus.Active;
        subscription.GracePeriodEndsAt = null;
        subscription.CancelAtPeriodEnd = false;
        subscription.UpdatedAt = now;

        audit.Record(nameof(MaintenanceSubscription), subscription.Id.ToString(),
            AuditActions.MaintenanceActivated,
            newValue: $"Subscription active via {provider} at {price:0.00} {currency}");

        await db.SaveChangesAsync(cancellationToken);

        var client = await db.ClientProfiles.FindAsync([clientId], cancellationToken);

        if (wasEnded)
        {
            // Automatic, unlike a portfolio taken down by an unresolved payment problem
            // (see RecordPaymentSuccessAsync): a lapsed subscription is the client's own
            // choice to end or not renew, so the same choice reversed — paying again —
            // is enough on its own, with nothing for staff to decide.
            var published = await portfolios.PublishAsync(clientId, null, cancellationToken);

            if (!published.Succeeded)
            {
                logger.LogWarning(
                    "Client {ClientId} resubscribed but their portfolio could not be republished: {Reason}",
                    clientId, published.Error);
            }
        }

        if (client is not null)
        {
            notifications.NotifyUser(
                client.ApplicationUserId,
                NotificationTypes.MaintenancePaymentResolved,
                wasEnded
                    ? "Your subscription is active again and your portfolio is back online."
                    : "Thank you. Your subscription is now active.",
                "/client");
        }

        return subscription;
    }

    public async Task UpdatePeriodAsync(
        string providerSubscriptionId,
        DateTimeOffset? currentPeriodEnd,
        bool cancelAtPeriodEnd,
        CancellationToken cancellationToken = default)
    {
        var subscription = await db.MaintenanceSubscriptions.FirstOrDefaultAsync(
            s => s.ProviderSubscriptionId == providerSubscriptionId, cancellationToken);

        if (subscription is null)
        {
            // Stripe does not guarantee delivery order: this can arrive before the
            // checkout.session.completed that creates the row. Nothing is lost — the
            // next update carries the same information.
            return;
        }

        subscription.NextPaymentDate = currentPeriodEnd ?? subscription.NextPaymentDate;
        subscription.CancelAtPeriodEnd = cancelAtPeriodEnd;
        subscription.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<OperationResult> RecordCancelledAsync(
        Guid clientId, CancellationToken cancellationToken = default)
    {
        var subscription = await db.MaintenanceSubscriptions
            .Include(s => s.Client)
            .FirstOrDefaultAsync(s => s.ClientId == clientId, cancellationToken);

        if (subscription is null)
        {
            return OperationResult.Fail("That client has no subscription.");
        }

        subscription.Status = MaintenanceSubscriptionStatus.Cancelled;
        subscription.GracePeriodEndsAt = null;
        subscription.UpdatedAt = DateTimeOffset.UtcNow;

        audit.Record(nameof(MaintenanceSubscription), subscription.Id.ToString(),
            AuditActions.MaintenanceCancelled);

        // Stripe only sends this once the paid period is genuinely over — no further
        // grace period applies the way a failed payment gets one (specification
        // section 23; this is the deliberate-cancellation counterpart to it).
        var portfolio = await db.Portfolios.FirstOrDefaultAsync(p => p.ClientId == clientId, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        if (portfolio is { IsPublished: true })
        {
            var result = await portfolios.UnpublishAsync(
                clientId, null, "subscription cancelled", cancellationToken);

            if (result.Succeeded)
            {
                await notifications.NotifyStaffAsync(
                    NotificationTypes.PortfolioUnpublished,
                    $"{subscription.Client.PublicName}'s subscription ended and their portfolio was "
                    + "taken down.",
                    $"/admin/clients/{clientId}",
                    cancellationToken);
            }
        }

        notifications.NotifyUser(
            subscription.Client.ApplicationUserId,
            NotificationTypes.PortfolioUnpublished,
            "Your subscription has ended.",
            "/client");

        return OperationResult.Ok();
    }
}
