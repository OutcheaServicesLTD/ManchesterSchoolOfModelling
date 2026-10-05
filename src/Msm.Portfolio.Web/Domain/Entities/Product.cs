using Msm.Portfolio.Web.Domain.Enums;

namespace Msm.Portfolio.Web.Domain.Entities;

/// <summary>
/// A sellable item (specification sections 19, 22 and 26). Seeded with the
/// 4 Week Model Development Programme at £3,499 one-off and Portfolio Maintenance
/// at £19.99 monthly.
/// </summary>
/// <remarks>
/// <see cref="Price"/> is the current price only. Orders and subscriptions copy the
/// price agreed at the time, so MSM can change pricing without rewriting history.
/// </remarks>
public class Product
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Stable key used by code and seeding, for example "model-development-programme".</summary>
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public decimal Price { get; set; }

    public string Currency { get; set; } = "GBP";

    public BillingType BillingType { get; set; } = BillingType.OneOff;

    public BillingInterval BillingInterval { get; set; } = BillingInterval.None;

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
