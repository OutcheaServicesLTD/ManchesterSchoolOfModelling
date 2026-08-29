using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Msm.Portfolio.Web.Configuration;
using Msm.Portfolio.Web.Data;
using Msm.Portfolio.Web.Integrations.Stripe;

namespace Msm.Portfolio.Web.Services;

/// <summary>
/// Starts and manages a client's own portfolio-maintenance subscription
/// (specification version 2, item 3).
/// </summary>
/// <remarks>
/// A client's choice, not something a purchase provisions automatically: it sits
/// alongside the £99 digital-portfolio purchase rather than inside it, so starting one
/// is always this service's job and never <c>CheckoutService</c>'s.
/// </remarks>
public interface IStripeSubscriptionService
{
    /// <summary>True once a client can actually be sent to Stripe — a price is configured.</summary>
    bool IsAvailable { get; }

    Task<(bool Succeeded, string? RedirectUrl, string? Error)> StartCheckoutAsync(
        Guid clientId, string successUrl, string cancelUrl, CancellationToken cancellationToken = default);

    Task<(bool Succeeded, string? RedirectUrl, string? Error)> OpenManagePortalAsync(
        Guid clientId, string returnUrl, CancellationToken cancellationToken = default);
}

public class StripeSubscriptionService(
    ApplicationDbContext db,
    IStripeService stripe,
    IMaintenanceService maintenance,
    IOptions<IntegrationOptions> options,
    ILogger<StripeSubscriptionService> logger) : IStripeSubscriptionService
{
    public bool IsAvailable => !string.IsNullOrWhiteSpace(options.Value.Stripe.PriceId);

    public async Task<(bool Succeeded, string? RedirectUrl, string? Error)> StartCheckoutAsync(
        Guid clientId, string successUrl, string cancelUrl, CancellationToken cancellationToken = default)
    {
        var priceId = options.Value.Stripe.PriceId;

        if (string.IsNullOrWhiteSpace(priceId))
        {
            return (false, null, "Subscriptions are not available yet.");
        }

        var client = await db.ClientProfiles
            .Include(c => c.ApplicationUser)
            .FirstOrDefaultAsync(c => c.Id == clientId, cancellationToken);

        if (client is null)
        {
            return (false, null, "That client could not be found.");
        }

        var existing = await maintenance.GetForClientAsync(clientId, cancellationToken);

        // Active or already recorded as in trouble: there is nothing a second checkout
        // would do that the Customer Portal cannot do better, and Stripe would refuse a
        // second subscription against the same customer for the same price anyway.
        if (existing is not null
            && existing.Status is Domain.Enums.MaintenanceSubscriptionStatus.Active
                or Domain.Enums.MaintenanceSubscriptionStatus.PaymentIssue)
        {
            return (false, null, "This client already has a subscription. Use Manage subscription instead.");
        }

        try
        {
            var (customerId, checkoutUrl) = await stripe.CreateSubscriptionCheckoutAsync(
                clientId,
                client.PublicName,
                client.ApplicationUser?.Email,
                client.StripeCustomerId,
                priceId,
                successUrl,
                cancelUrl,
                cancellationToken);

            // Recorded as soon as it is known rather than waiting for the webhook, so a
            // client who starts a second checkout before the first completes is still
            // recognised as the same Stripe Customer. Activation itself — the row's
            // Status — still waits for the webhook, which is the trusted source
            // (specification version 2, item 3).
            if (!string.IsNullOrWhiteSpace(customerId) && client.StripeCustomerId != customerId)
            {
                client.StripeCustomerId = customerId;
                await db.SaveChangesAsync(cancellationToken);
            }

            return (true, checkoutUrl, null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not open a subscription checkout for client {ClientId}.", clientId);
            return (false, null, "We could not open the subscription page. Please try again.");
        }
    }

    public async Task<(bool Succeeded, string? RedirectUrl, string? Error)> OpenManagePortalAsync(
        Guid clientId, string returnUrl, CancellationToken cancellationToken = default)
    {
        var client = await db.ClientProfiles.FirstOrDefaultAsync(c => c.Id == clientId, cancellationToken);

        if (client?.StripeCustomerId is null)
        {
            return (false, null, "There is no subscription to manage yet.");
        }

        try
        {
            var url = await stripe.CreateManagePortalSessionAsync(
                client.StripeCustomerId, returnUrl, cancellationToken);

            return (true, url, null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not open the Stripe Customer Portal for client {ClientId}.", clientId);
            return (false, null, "We could not open the subscription management page. Please try again.");
        }
    }
}
