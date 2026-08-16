using Msm.Portfolio.Web.Domain.Enums;

namespace Msm.Portfolio.Web.Domain.Entities;

/// <summary>
/// A single payment attempt against an order (specification sections 21 and 26).
/// Payment is tracked as a state rather than a paid flag, because GoCardless
/// settlement moves through several stages after authorisation.
/// </summary>
public class PaymentTransaction
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrderId { get; set; }

    public Order Order { get; set; } = null!;

    /// <summary>Payment provider name, for example "GoCardless".</summary>
    public string Provider { get; set; } = "GoCardless";

    /// <summary>Provider's own payment identifier, used to reconcile inbound webhooks.</summary>
    public string? ProviderPaymentId { get; set; }

    public decimal Amount { get; set; }

    public string Currency { get; set; } = "GBP";

    public PaymentStatus Status { get; set; } = PaymentStatus.Pending;

    public string? FailureReason { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
