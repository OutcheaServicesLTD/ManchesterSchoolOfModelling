namespace Msm.Portfolio.Web.Integrations.Stripe;

/// <summary>
/// The Stripe boundary for the portfolio-maintenance subscription (specification
/// version 2, item 3).
/// </summary>
/// <remarks>
/// Deliberately narrow. Everything Stripe Checkout and the Stripe Customer Portal
/// already do — collecting a card, retrying a failed payment — stays on Stripe's hosted
/// pages rather than being rebuilt here. Cancelling is the one exception: it is called
/// directly, at period end, so the Netflix/Spotify-style "stays live until the paid
/// period ends" rule does not depend on how the Stripe Dashboard's own Customer Portal
/// happens to be configured.
/// </remarks>
public interface IStripeService
{
    /// <summary>True when a real secret key is configured; false when running on the stub.</summary>
    bool IsLive { get; }

    /// <summary>
    /// Opens a Stripe Checkout Session in subscription mode for one client, creating a
    /// Stripe Customer first if this client has never had one.
    /// </summary>
    /// <param name="existingCustomerId">
    /// Reused when present, so a client who cancelled and is starting again is
    /// recognised as the same Stripe Customer rather than opening a new one.
    /// </param>
    Task<(string CustomerId, string CheckoutUrl)> CreateSubscriptionCheckoutAsync(
        Guid clientId,
        string clientName,
        string? clientEmail,
        string? existingCustomerId,
        string priceId,
        string successUrl,
        string cancelUrl,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a Stripe Customer Portal session: the Netflix/Spotify-style screen where a
    /// client manages payment details, sees invoices, or cancels — none of which this
    /// application ever needs to build or store, since Stripe Billing owns all of it.
    /// </summary>
    Task<string> CreateManagePortalSessionAsync(
        string customerId, string returnUrl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks a subscription to stop at the end of the period already paid for, rather
    /// than cancelling it outright. The subscription's Stripe status stays "active"
    /// until then — Stripe's own behaviour, not something this application tracks — so
    /// entitlement is unaffected until the real expiry webhook arrives.
    /// </summary>
    Task CancelAtPeriodEndAsync(string subscriptionId, CancellationToken cancellationToken = default);
}
