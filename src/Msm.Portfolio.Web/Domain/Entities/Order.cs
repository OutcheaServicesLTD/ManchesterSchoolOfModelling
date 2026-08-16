using Msm.Portfolio.Web.Domain.Enums;

namespace Msm.Portfolio.Web.Domain.Entities;

/// <summary>
/// A client's purchase of the programme (specification sections 19, 20 and 26).
/// </summary>
public class Order
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ClientId { get; set; }

    public ClientProfile Client { get; set; } = null!;

    public Guid ProductId { get; set; }

    public Product Product { get; set; } = null!;

    /// <summary>
    /// Amount agreed on this order, copied from the product at checkout. Deliberately
    /// not read back from <see cref="Product.Price"/>, so a later price change cannot
    /// alter what a client was charged (specification section 19).
    /// </summary>
    public decimal Amount { get; set; }

    public string Currency { get; set; } = "GBP";

    public OrderStatus Status { get; set; } = OrderStatus.Draft;

    /// <summary>GoCardless billing request or payment reference for reconciliation.</summary>
    public string? GoCardlessReference { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? CheckoutStartedAt { get; set; }

    public DateTimeOffset? ConfirmedAt { get; set; }

    /// <summary>Set when Admin records that the client declined (specification section 48).</summary>
    public DateTimeOffset? NoSaleAt { get; set; }

    public ICollection<PaymentTransaction> Transactions { get; set; } = new List<PaymentTransaction>();
}
