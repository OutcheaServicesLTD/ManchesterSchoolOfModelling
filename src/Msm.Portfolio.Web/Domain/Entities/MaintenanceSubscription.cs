using Msm.Portfolio.Web.Domain.Enums;

namespace Msm.Portfolio.Web.Domain.Entities;

/// <summary>
/// Recurring portfolio maintenance charge (specification sections 22, 23 and 26).
/// Separate from the one-off programme purchase.
/// </summary>
public class MaintenanceSubscription
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ClientId { get; set; }

    public ClientProfile Client { get; set; } = null!;

    public Guid ProductId { get; set; }

    public Product Product { get; set; } = null!;

    /// <summary>
    /// Which payment provider this subscription runs on. "Stripe" for everything a
    /// client starts themselves through the client portal (specification version 2,
    /// item 3); "GoCardless" is left available for the auto-provisioned arrangement
    /// CommerceOptions.MaintenanceEnabled would create, should MSM ever turn that
    /// back on.
    /// </summary>
    public string Provider { get; set; } = "Stripe";

    public string? ProviderSubscriptionId { get; set; }

    /// <summary>
    /// Price agreed when this subscription started. Existing subscribers keep their
    /// original rate when MSM changes the advertised price (specification section 22).
    /// </summary>
    public decimal PriceAtCreation { get; set; }

    public string Currency { get; set; } = "GBP";

    public MaintenanceSubscriptionStatus Status { get; set; } = MaintenanceSubscriptionStatus.NotStarted;

    /// <summary>
    /// Configurable, because MSM has not yet fixed whether maintenance begins at
    /// purchase or after an included initial period (specification section 22).
    /// </summary>
    public DateTimeOffset? StartDate { get; set; }

    public DateTimeOffset? NextPaymentDate { get; set; }

    /// <summary>
    /// When a failed payment's grace period ends. While this is in the future the
    /// portfolio stays public; once it passes the portfolio is unpublished
    /// (specification section 23).
    /// </summary>
    public DateTimeOffset? GracePeriodEndsAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// True while a failed payment is still inside its grace period. Callers use this
    /// to show the warning to staff and the client without exposing it publicly.
    /// </summary>
    public bool IsInGracePeriod(DateTimeOffset now) =>
        Status == MaintenanceSubscriptionStatus.PaymentIssue
        && GracePeriodEndsAt is { } endsAt
        && now < endsAt;

    /// <summary>
    /// True when the grace period has run out and the portfolio should come down
    /// (specification section 23).
    /// </summary>
    public bool HasGracePeriodExpired(DateTimeOffset now) =>
        Status == MaintenanceSubscriptionStatus.PaymentIssue
        && GracePeriodEndsAt is { } endsAt
        && now >= endsAt;

    /// <summary>
    /// Whether the client is still entitled to be shown publicly, which the Model Board
    /// requires in addition to publication (specification section 18).
    /// </summary>
    /// <remarks>
    /// A failed payment inside its grace period keeps entitlement: specification section
    /// 23 is explicit that the portfolio stays live for those seven days. Entitlement is
    /// only lost once the grace period has run out, or the arrangement has ended.
    /// </remarks>
    public bool IsEntitlementActive(DateTimeOffset now) => Status switch
    {
        // Maintenance has not begun yet, because the initial period is included in the
        // programme price (specification section 22).
        MaintenanceSubscriptionStatus.NotStarted => true,
        MaintenanceSubscriptionStatus.Active => true,
        MaintenanceSubscriptionStatus.PaymentIssue => IsInGracePeriod(now),
        _ => false
    };

    /// <summary>Days left before the portfolio comes down, for the staff and client warnings.</summary>
    public int? DaysRemainingInGracePeriod(DateTimeOffset now) =>
        IsInGracePeriod(now) && GracePeriodEndsAt is { } endsAt
            ? Math.Max(0, (int)Math.Ceiling((endsAt - now).TotalDays))
            : null;
}
