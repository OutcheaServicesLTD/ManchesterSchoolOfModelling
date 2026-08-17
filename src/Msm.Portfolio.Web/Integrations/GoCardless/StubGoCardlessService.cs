using Msm.Portfolio.Web.Domain.Entities;

namespace Msm.Portfolio.Web.Integrations.GoCardless;

/// <summary>
/// Stands in for GoCardless so the checkout journey runs end to end without a provider
/// account.
/// </summary>
/// <remarks>
/// <para>
/// Registered whenever no GoCardless access token is configured. It takes no money and
/// makes no network call: it issues a reference and sends the client to a local page
/// that imitates the provider's hosted flow, so the order lifecycle, webhook handling
/// and publication rule can all be exercised.
/// </para>
/// <para>
/// Outside development it refuses to authorise anything. A stub that silently approved
/// payments in production would publish portfolios nobody had paid for.
/// </para>
/// </remarks>
public class StubGoCardlessService(
    IHostEnvironment environment,
    ILogger<StubGoCardlessService> logger) : IGoCardlessService
{
    public bool IsLive => false;

    public Task<CheckoutSession> CreateCheckoutAsync(
        Order order,
        ClientProfile client,
        string successUrl,
        string failureUrl,
        CancellationToken cancellationToken = default)
    {
        var reference = $"STUB-BR-{order.Id:N}"[..24];

        logger.LogWarning(
            "GoCardless is not configured. Order {OrderId} for {Amount} {Currency} is using the "
            + "local stub checkout and no money will be taken.",
            order.Id, order.Amount, order.Currency);

        // Sends the client to the application's own imitation of the hosted flow.
        return Task.FromResult(new CheckoutSession(reference, $"/checkout/{order.Id}/stub"));
    }

    public Task<CheckoutOutcome> CompleteCheckoutAsync(
        string providerReference, CancellationToken cancellationToken = default)
    {
        if (!environment.IsDevelopment())
        {
            logger.LogCritical(
                "A checkout completion was attempted through the stub outside development. "
                + "Refusing. Configure Integrations:GoCardless before taking payments.");

            return Task.FromResult(new CheckoutOutcome(
                false, FailureReason: "No payment provider is configured."));
        }

        return Task.FromResult(new CheckoutOutcome(
            true,
            ProviderPaymentId: $"STUB-PM-{Guid.CreateVersion7():N}"[..24],
            ProviderMandateId: $"STUB-MD-{Guid.CreateVersion7():N}"[..24]));
    }
}
