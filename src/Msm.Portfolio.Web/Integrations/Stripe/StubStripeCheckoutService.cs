using Msm.Portfolio.Web.Domain.Entities;

namespace Msm.Portfolio.Web.Integrations.Stripe;

/// <summary>
/// Stands in for Stripe so the £99 checkout journey runs end to end without a Stripe
/// account.
/// </summary>
/// <remarks>
/// <para>
/// Registered whenever no Stripe secret key is configured. It takes no money and makes
/// no network call: it issues a reference and sends the client to a local page that
/// imitates Stripe Checkout, so the order lifecycle, webhook handling and publication
/// rule can all be exercised.
/// </para>
/// <para>
/// Outside development it refuses to authorise anything. A stub that silently approved
/// payments in production would publish portfolios nobody had paid for.
/// </para>
/// </remarks>
public class StubStripeCheckoutService(
    IHostEnvironment environment,
    ILogger<StubStripeCheckoutService> logger) : IStripeCheckoutService
{
    public bool IsLive => false;

    public Task<CheckoutSession> CreateCheckoutAsync(
        Order order,
        ClientProfile client,
        string successUrl,
        string failureUrl,
        CancellationToken cancellationToken = default)
    {
        var reference = $"STUB-CS-{order.Id:N}"[..24];

        logger.LogWarning(
            "Stripe is not configured. Order {OrderId} for {Amount} {Currency} is using the "
            + "local stub checkout and no money will be taken.",
            order.Id, order.Amount, order.Currency);

        // Sends the client to the application's own imitation of Stripe Checkout.
        return Task.FromResult(new CheckoutSession(reference, $"/checkout/{order.Id}/stub"));
    }

    public Task<CheckoutOutcome> CompleteCheckoutAsync(
        string providerReference, CancellationToken cancellationToken = default)
    {
        if (!environment.IsDevelopment())
        {
            logger.LogCritical(
                "A checkout completion was attempted through the stub outside development. "
                + "Refusing. Configure Integrations:Stripe before taking payments.");

            return Task.FromResult(new CheckoutOutcome(
                false, FailureReason: "No payment provider is configured."));
        }

        return Task.FromResult(new CheckoutOutcome(
            true, ProviderPaymentId: $"STUB-PI-{Guid.CreateVersion7():N}"[..24]));
    }
}
