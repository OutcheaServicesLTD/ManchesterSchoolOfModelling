using StripeCheckout = Stripe.Checkout;
using StripePortal = Stripe.BillingPortal;

namespace Msm.Portfolio.Web.Integrations.Stripe;

/// <summary>
/// The real Stripe client, using Stripe.net against Stripe Checkout and the Stripe
/// Customer Portal (specification version 2, item 3).
/// </summary>
/// <remarks>
/// Its HTTP calls have not been verified against a live Stripe account — as with
/// GoCardless, that verification is a deployment step for MSM, tracked the same way.
/// Registered only once Integrations:Stripe:SecretKey is configured; the stub stands in
/// until then.
/// </remarks>
public class StripeService(ILogger<StripeService> logger) : IStripeService
{
    public bool IsLive => true;

    public async Task<(string CustomerId, string CheckoutUrl)> CreateSubscriptionCheckoutAsync(
        Guid clientId,
        string clientName,
        string? clientEmail,
        string? existingCustomerId,
        string priceId,
        string successUrl,
        string cancelUrl,
        CancellationToken cancellationToken = default)
    {
        var options = new StripeCheckout.SessionCreateOptions
        {
            Mode = "subscription",
            LineItems =
            [
                new StripeCheckout.SessionLineItemOptions { Price = priceId, Quantity = 1 }
            ],
            SuccessUrl = successUrl,
            CancelUrl = cancelUrl,
            // The webhook resolves the client from this rather than trusting anything
            // the browser sends back on return (specification version 2, item 3).
            ClientReferenceId = clientId.ToString(),
            SubscriptionData = new StripeCheckout.SessionSubscriptionDataOptions
            {
                Metadata = new Dictionary<string, string> { ["clientId"] = clientId.ToString() }
            }
        };

        // A returning customer is reused so Stripe recognises them as the same person;
        // a first-timer is identified by email so Stripe can still de-duplicate later
        // if MSM ever looks them up in the Dashboard.
        if (!string.IsNullOrWhiteSpace(existingCustomerId))
        {
            options.Customer = existingCustomerId;
        }
        else if (!string.IsNullOrWhiteSpace(clientEmail))
        {
            options.CustomerEmail = clientEmail;
        }

        var service = new StripeCheckout.SessionService();
        var session = await service.CreateAsync(options, cancellationToken: cancellationToken);

        logger.LogInformation(
            "Opened a Stripe subscription checkout for client {ClientId}.", clientId);

        // The Customer is not always assigned yet at this point — Stripe creates one on
        // completion when none was supplied — so the checkout.session.completed webhook
        // is what actually records it against the client, not this return value.
        return (session.CustomerId ?? existingCustomerId ?? string.Empty, session.Url);
    }

    public async Task<string> CreateManagePortalSessionAsync(
        string customerId, string returnUrl, CancellationToken cancellationToken = default)
    {
        var options = new StripePortal.SessionCreateOptions
        {
            Customer = customerId,
            ReturnUrl = returnUrl
        };

        var service = new StripePortal.SessionService();
        var session = await service.CreateAsync(options, cancellationToken: cancellationToken);

        return session.Url;
    }
}
