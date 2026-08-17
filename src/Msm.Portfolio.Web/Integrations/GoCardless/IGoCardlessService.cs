using Msm.Portfolio.Web.Domain.Entities;

namespace Msm.Portfolio.Web.Integrations.GoCardless;

/// <summary>A payment journey opened at the provider, ready for the client to complete.</summary>
public record CheckoutSession(string ProviderReference, string RedirectUrl);

/// <summary>The outcome of a completed payment journey.</summary>
public record CheckoutOutcome(
    bool Authorised,
    string? ProviderPaymentId = null,
    string? ProviderMandateId = null,
    string? FailureReason = null);

/// <summary>
/// One event received from the provider (specification section 44).
/// </summary>
/// <param name="EventId">
/// The provider's own event identifier. Stored and made unique, which is what makes
/// webhook handling idempotent under retries.
/// </param>
public record ProviderEvent(
    string EventId,
    string ResourceType,
    string Action,
    string? PaymentId,
    string? SubscriptionId,
    string? MandateId,
    string? Reason);

/// <summary>
/// The payment provider boundary (specification sections 20 and 21).
/// </summary>
/// <remarks>
/// GoCardless is the chosen provider, and their hosted flow is preferred over a custom
/// bank-details form because a custom payment page carries additional scheme-compliance
/// obligations. Everything above this interface — orders, payment state, webhook
/// idempotency and the publication rule — is provider-independent and fully exercised
/// by the test suite, so replacing the implementation does not disturb it.
/// </remarks>
public interface IGoCardlessService
{
    /// <summary>True when a real provider is configured; false when running on the stub.</summary>
    bool IsLive { get; }

    Task<CheckoutSession> CreateCheckoutAsync(
        Order order,
        ClientProfile client,
        string successUrl,
        string failureUrl,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Confirms what actually happened after the client returns from the provider.
    /// The browser's return is a hint, not proof; this asks the provider directly.
    /// </summary>
    Task<CheckoutOutcome> CompleteCheckoutAsync(
        string providerReference, CancellationToken cancellationToken = default);
}
