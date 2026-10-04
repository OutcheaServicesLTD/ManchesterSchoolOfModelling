using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Msm.Portfolio.Web.Configuration;
using Msm.Portfolio.Web.Services;

namespace Msm.Portfolio.Web.Controllers;

/// <summary>
/// Receives provider notifications (specification sections 34 and 44).
/// </summary>
/// <remarks>
/// Anonymous and exempt from anti-forgery by necessity: the provider has no session and
/// no token. Authenticity is established by the payload signature instead, which is
/// verified before anything is read from the body.
/// </remarks>
[Route("webhooks")]
[AllowAnonymous]
[IgnoreAntiforgeryToken]
public class WebhookController(
    IStripeWebhookProcessor stripeProcessor,
    ILogger<WebhookController> logger) : ControllerBase
{
    /// <summary>
    /// Both the £99 one-off portfolio purchase and the portfolio-maintenance
    /// subscription (specification version 2, item 3) land here — one Stripe account,
    /// one webhook.
    /// </summary>
    [HttpPost("stripe")]
    [EnableRateLimiting(RateLimitPolicies.Webhook)]
    public async Task<IActionResult> Stripe(CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(Request.Body);
        var payload = await reader.ReadToEndAsync(cancellationToken);

        var signature = Request.Headers["Stripe-Signature"].FirstOrDefault();

        var result = await stripeProcessor.ProcessAsync(payload, signature, cancellationToken);

        if (!result.Accepted)
        {
            // 400 is what Stripe expects for a signature it should not retry.
            return BadRequest(new { error = result.Error });
        }

        logger.LogInformation(
            "Stripe webhook accepted: {Processed} applied, {Skipped} already seen.",
            result.Processed, result.Skipped);

        return Ok(new { processed = result.Processed, skipped = result.Skipped });
    }
}
