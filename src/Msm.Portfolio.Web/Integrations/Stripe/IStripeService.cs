namespace Msm.Portfolio.Web.Integrations.Stripe;

/// <summary>
/// The Stripe boundary for the portfolio-maintenance subscription (specification
/// version 2, item 3).
/// </summary>
/// <remarks>
/// Deliberately narrow. Everything Stripe Checkout and the Stripe Customer Portal
/// already do — collecting a card, retrying a failed payment, letting a client cancel —
/// stays on Stripe's hosted pages rather than being rebuilt here. This interface only
/// ever asks Stripe to start one of those pages and hands back where to send the
/// browser; nothing above it ever sees a card number or a secret key.
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
}
