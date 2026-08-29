using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Msm.Portfolio.Web.Authorization;
using Msm.Portfolio.Web.Data;
using Msm.Portfolio.Web.Integrations.Stripe;
using Msm.Portfolio.Web.Services;

namespace Msm.Portfolio.Web.Areas.Client.Controllers;

/// <summary>
/// Starting and managing a client's own portfolio-maintenance subscription
/// (specification version 2, item 3) — the Netflix/Spotify-style screen the client
/// portal did not have before. Stripe Checkout and the Stripe Customer Portal do
/// almost everything; this only ever opens one of those pages and sends the browser on.
/// </summary>
[Area("Client")]
[Route("client/subscription")]
[Authorize(Policy = Policies.ClientArea)]
public class SubscriptionController(
    IClientProfileAccessor profiles,
    IStripeSubscriptionService subscriptions,
    IStripeService stripe,
    IMaintenanceService maintenance,
    ApplicationDbContext db) : Controller
{
    [HttpPost("start")]
    public async Task<IActionResult> Start(CancellationToken cancellationToken = default)
    {
        var client = await profiles.GetCurrentAsync(User, cancellationToken);

        if (client is null)
        {
            return RedirectToAction("Index", "Home");
        }

        var origin = $"{Request.Scheme}://{Request.Host}";

        var result = await subscriptions.StartCheckoutAsync(
            client.Id, $"{origin}/client?subscription=started", $"{origin}/client", cancellationToken);

        if (!result.Succeeded)
        {
            TempData["Error"] = result.Error;
            return RedirectToAction("Index", "Home");
        }

        return Redirect(result.RedirectUrl!);
    }

    [HttpPost("manage")]
    public async Task<IActionResult> Manage(CancellationToken cancellationToken = default)
    {
        var client = await profiles.GetCurrentAsync(User, cancellationToken);

        if (client is null)
        {
            return RedirectToAction("Index", "Home");
        }

        var origin = $"{Request.Scheme}://{Request.Host}";

        var result = await subscriptions.OpenManagePortalAsync(client.Id, $"{origin}/client", cancellationToken);

        if (!result.Succeeded)
        {
            TempData["Error"] = result.Error;
            return RedirectToAction("Index", "Home");
        }

        return Redirect(result.RedirectUrl!);
    }

    /// <summary>
    /// Stands in for Stripe Checkout when no Stripe account is configured, so the
    /// subscription journey can be walked end to end in development — the same
    /// reasoning as <c>CheckoutController.Stub</c> for the £99 purchase. Never
    /// reachable with a real provider.
    /// </summary>
    [HttpGet("stub-checkout/{clientId:guid}")]
    public async Task<IActionResult> StubCheckout(Guid clientId, CancellationToken cancellationToken = default)
    {
        if (stripe.IsLive)
        {
            return NotFound();
        }

        var client = await profiles.GetCurrentAsync(User, cancellationToken);

        if (client is null || client.Id != clientId)
        {
            return NotFound();
        }

        var product = await db.Products.FirstOrDefaultAsync(
            p => p.Code == ProductCodes.PortfolioMaintenance, cancellationToken);

        ViewData["ClientId"] = clientId;
        ViewData["Price"] = product?.Price ?? 0m;
        ViewData["Currency"] = product?.Currency ?? "GBP";

        return View();
    }

    [HttpPost("stub-checkout/{clientId:guid}/complete")]
    public async Task<IActionResult> StubComplete(Guid clientId, CancellationToken cancellationToken = default)
    {
        if (stripe.IsLive)
        {
            return NotFound();
        }

        var client = await profiles.GetCurrentAsync(User, cancellationToken);

        if (client is null || client.Id != clientId)
        {
            return NotFound();
        }

        var product = await db.Products.FirstOrDefaultAsync(
            p => p.Code == ProductCodes.PortfolioMaintenance, cancellationToken);

        // A real Checkout completion is reported through the Stripe webhook, never from
        // the browser's return. There is no webhook to fire in the stub, so this stands
        // in for it directly — the one place this application activates a subscription
        // from anywhere other than StripeWebhookProcessor.
        await maintenance.ActivateSubscriptionAsync(
            clientId, "Stripe", $"STUB-SUB-{clientId:N}"[..24], product?.Price ?? 0m,
            product?.Currency ?? "GBP", cancellationToken);

        TempData["Saved"] = "Subscription started.";
        return RedirectToAction("Index", "Home");
    }
}
