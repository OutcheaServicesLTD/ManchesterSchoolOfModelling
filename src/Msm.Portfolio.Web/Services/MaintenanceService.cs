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
        subscription.NextPaymentDate = DateTimeOffset.UtcNow.AddMonths(1);

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
}
