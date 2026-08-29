namespace Msm.Portfolio.Web.Integrations.Stripe;

/// <summary>
/// Stands in for Stripe so the subscription journey runs end to end without a Stripe
/// account (specification version 2, item 3).
/// </summary>
/// <remarks>
/// Registered whenever no Stripe secret key is configured. It takes no money and makes
/// no network call: it issues a reference and sends the client to a local page that
/// imitates Stripe Checkout, exactly as <c>StubGoCardlessService</c> does for the
/// portfolio purchase.
/// </remarks>
public class StubStripeService(
    IHostEnvironment environment, ILogger<StubStripeService> logger) : IStripeService
{
    public bool IsLive => false;

    public Task<(string CustomerId, string CheckoutUrl)> CreateSubscriptionCheckoutAsync(
        Guid clientId,
        string clientName,
        string? clientEmail,
        string? existingCustomerId,
        string priceId,
        string successUrl,
        string cancelUrl,
        CancellationToken cancellationToken = default)
    {
        var customerId = existingCustomerId ?? $"STUB-CUS-{clientId:N}"[..20];

        logger.LogWarning(
            "Stripe is not configured. The subscription checkout for client {ClientId} is using the "
            + "local stub and no money will be taken.", clientId);

        return Task.FromResult((customerId, $"/client/subscription/stub-checkout/{clientId}"));
    }

    public Task<string> CreateManagePortalSessionAsync(
        string customerId, string returnUrl, CancellationToken cancellationToken = default)
    {
        if (!environment.IsDevelopment())
        {
            logger.LogCritical(
                "A Stripe Customer Portal session was requested through the stub outside "
                + "development. Refusing. Configure Integrations:Stripe before offering it.");

            // There is nowhere real to send them; back to where they came from is the
            // least surprising failure.
            return Task.FromResult(returnUrl);
        }

        return Task.FromResult(returnUrl);
    }
}
