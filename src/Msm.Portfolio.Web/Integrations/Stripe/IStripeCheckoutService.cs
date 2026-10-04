using Msm.Portfolio.Web.Domain.Entities;

namespace Msm.Portfolio.Web.Integrations.Stripe;

/// <summary>A payment journey opened at Stripe, ready for the client to complete.</summary>
public record CheckoutSession(string ProviderReference, string RedirectUrl);

/// <summary>The outcome of a completed payment journey.</summary>
public record CheckoutOutcome(
    bool Authorised,
    string? ProviderPaymentId = null,
    string? FailureReason = null);

/// <summary>
/// The payment boundary for the £99 one-off digital-portfolio purchase (specification
/// sections 19, 20 and 21).
/// </summary>
/// <remarks>
/// Stripe Checkout is used rather than a custom card form, which would carry additional
/// PCI-compliance obligations. Everything above this interface — orders, payment state,
/// webhook idempotency and the publication rule — is provider-independent and fully
/// exercised by the test suite, so replacing the implementation does not disturb it.
/// </remarks>
public interface IStripeCheckoutService
{
    /// <summary>True when a real secret key is configured; false when running on the stub.</summary>
    bool IsLive { get; }

    Task<CheckoutSession> CreateCheckoutAsync(
        Order order,
        ClientProfile client,
        string successUrl,
        string failureUrl,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Confirms what actually happened after the client returns from Stripe.
    /// The browser's return is a hint, not proof; this asks Stripe directly.
    /// </summary>
    Task<CheckoutOutcome> CompleteCheckoutAsync(
        string providerReference, CancellationToken cancellationToken = default);
}
