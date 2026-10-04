using Msm.Portfolio.Web.Domain.Entities;
using StripeCheckout = Stripe.Checkout;

namespace Msm.Portfolio.Web.Integrations.Stripe;

/// <summary>
/// Calls Stripe Checkout in one-off payment mode for the £99 digital-portfolio purchase.
/// </summary>
/// <remarks>
/// Its HTTP calls have not been verified against a live Stripe account. Registered only
/// once Integrations:Stripe:SecretKey is configured; the stub stands in until then.
/// <para>
/// Unlike the recurring membership subscription, there is no pre-created Stripe Price
/// for this: the amount is already governed by <see cref="Order.Amount"/> (itself copied
/// from the Product at checkout, specification section 19), so it is sent to Stripe as a
/// line item built on the spot rather than requiring a matching Price object to be kept
/// in sync in the Stripe Dashboard.
/// </para>
/// </remarks>
public class StripeCheckoutService(ILogger<StripeCheckoutService> logger) : IStripeCheckoutService
{
    public bool IsLive => true;

    public async Task<CheckoutSession> CreateCheckoutAsync(
        Order order,
        ClientProfile client,
        string successUrl,
        string failureUrl,
        CancellationToken cancellationToken = default)
    {
        // Amount in the smallest currency unit: pence for GBP. Sending pounds here would
        // undercharge by a factor of a hundred, so it is converted explicitly.
        var pence = (long)decimal.Round(order.Amount * 100m, 0, MidpointRounding.AwayFromZero);

        var options = new StripeCheckout.SessionCreateOptions
        {
            Mode = "payment",
            LineItems =
            [
                new StripeCheckout.SessionLineItemOptions
                {
                    Quantity = 1,
                    PriceData = new StripeCheckout.SessionLineItemPriceDataOptions
                    {
                        Currency = order.Currency,
                        UnitAmount = pence,
                        ProductData = new StripeCheckout.SessionLineItemPriceDataProductDataOptions
                        {
                            Name = "Digital Portfolio"
                        }
                    }
                }
            ],
            SuccessUrl = successUrl,
            CancelUrl = failureUrl,
            ClientReferenceId = order.Id.ToString(),
            Metadata = new Dictionary<string, string> { ["orderId"] = order.Id.ToString() }
        };

        if (!string.IsNullOrWhiteSpace(client.StripeCustomerId))
        {
            options.Customer = client.StripeCustomerId;
        }

        var service = new StripeCheckout.SessionService();
        var session = await service.CreateAsync(options, cancellationToken: cancellationToken);

        logger.LogInformation(
            "Opened a Stripe payment checkout {SessionId} for order {OrderId}.", session.Id, order.Id);

        return new CheckoutSession(session.Id, session.Url);
    }

    public async Task<CheckoutOutcome> CompleteCheckoutAsync(
        string providerReference, CancellationToken cancellationToken = default)
    {
        try
        {
            var service = new StripeCheckout.SessionService();
            var session = await service.GetAsync(providerReference, cancellationToken: cancellationToken);

            // Only a paid session means the client actually completed the journey.
            // Returning to the success URL alone proves nothing: a client can reach that
            // address by going back, or by typing it.
            if (session.PaymentStatus != "paid")
            {
                return new CheckoutOutcome(
                    false,
                    FailureReason: $"The payment was not completed (status: {session.PaymentStatus}).");
            }

            return new CheckoutOutcome(true, ProviderPaymentId: session.PaymentIntentId);
        }
        catch (global::Stripe.StripeException ex)
        {
            // A failure to reach Stripe must not be read as a failed payment: the client
            // may well have paid. The webhook is the authority and will correct the
            // record when it arrives (specification section 44).
            logger.LogError(ex, "Could not reach Stripe to confirm checkout session {Reference}.", providerReference);

            return new CheckoutOutcome(
                false, FailureReason: "We could not confirm the payment. Please contact us before trying again.");
        }
    }
}
