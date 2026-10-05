using Msm.Portfolio.Web.Configuration;
using Msm.Portfolio.Web.Domain.Enums;

namespace Msm.Portfolio.Web.ViewModels;

/// <summary>The checkout summary and its outcome pages (specification section 20).</summary>
public class CheckoutViewModel
{
    public Guid OrderId { get; set; }

    public Guid ClientId { get; set; }

    public string ClientName { get; set; } = string.Empty;

    public string ProductName { get; set; } = string.Empty;

    public string? ProductDescription { get; set; }

    public decimal Amount { get; set; }

    public string Currency { get; set; } = "GBP";

    public OrderStatus Status { get; set; }

    public PaymentStatus? PaymentStatus { get; set; }

    public MsmBrandOptions Brand { get; set; } = new();

    /// <summary>False when running on the stub, which takes no money.</summary>
    public bool ProviderIsLive { get; set; }

    public string? Error { get; set; }

    public string FormattedAmount => Currency == "GBP"
        ? $"£{Amount:N2}"
        : $"{Amount:N2} {Currency}";

    public bool IsPaid => Status == OrderStatus.Confirmed;
}
