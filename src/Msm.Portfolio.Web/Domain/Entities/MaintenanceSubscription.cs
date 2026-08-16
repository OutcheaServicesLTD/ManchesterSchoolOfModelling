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
}
