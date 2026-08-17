using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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
    IPaymentWebhookProcessor processor,
    ILogger<WebhookController> logger) : ControllerBase
{
    [HttpPost("gocardless")]
    public async Task<IActionResult> GoCardless(CancellationToken cancellationToken = default)
    {
        // Read as raw text: the signature covers the exact bytes sent, so re-serialising
        // a deserialised model would not produce a payload that verifies.
        using var reader = new StreamReader(Request.Body);
        var payload = await reader.ReadToEndAsync(cancellationToken);

        var signature = Request.Headers["Webhook-Signature"].FirstOrDefault();

        var result = await processor.ProcessAsync(payload, signature, cancellationToken);

        if (!result.Accepted)
        {
            // 498 is what GoCardless expects for a signature it should not retry.
            return StatusCode(498, new { error = result.Error });
        }

        logger.LogInformation(
            "Webhook accepted: {Processed} applied, {Skipped} already seen.", result.Processed, result.Skipped);

        // 200 tells the provider to stop retrying. Returned even when every event was a
        // duplicate, because a duplicate means the work is already done.
        return Ok(new { processed = result.Processed, skipped = result.Skipped });
    }
}
