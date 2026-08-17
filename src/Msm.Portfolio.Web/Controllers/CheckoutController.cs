using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Msm.Portfolio.Web.Authorization;
using Msm.Portfolio.Web.Configuration;
using Msm.Portfolio.Web.Domain.Entities;
using Msm.Portfolio.Web.Domain.Enums;
using Msm.Portfolio.Web.Integrations.GoCardless;
using Msm.Portfolio.Web.Services;
using Msm.Portfolio.Web.ViewModels;

namespace Msm.Portfolio.Web.Controllers;

/// <summary>
/// The £3,499 checkout (specification sections 19, 20 and 34).
/// </summary>
/// <remarks>
/// Opened by staff in the studio while the client is present, so the pages are reached
/// by an authenticated Admin rather than by the client alone. The provider's hosted
/// page collects the payment details; none are handled here.
/// </remarks>
[Route("checkout")]
public class CheckoutController(
    ICheckoutService checkout,
    IGoCardlessService provider,
    UserManager<ApplicationUser> userManager,
    IOptions<MsmBrandOptions> brandOptions,
    ILogger<CheckoutController> logger) : Controller
{
    /// <summary>Opens the checkout summary for a client (the Admin "Purchase" action).</summary>
    [HttpGet("{clientId:guid}")]
    [Authorize(Policy = Permissions.Payments.StartCheckout)]
    public async Task<IActionResult> Index(Guid clientId, CancellationToken cancellationToken = default)
    {
        var result = await checkout.OpenAsync(clientId, CurrentUserId(), cancellationToken);

        if (!result.Succeeded)
        {
            TempData["Error"] = result.Error;
            return Redirect($"/admin/clients/{clientId}");
        }

        return View(Build(result.Order!));
    }

    /// <summary>
    /// Accepts the terms and sends the client to the provider's hosted payment page.
    /// </summary>
    [HttpPost("{orderId:guid}/start")]
    [Authorize(Policy = Permissions.Payments.StartCheckout)]
    public async Task<IActionResult> Start(
        Guid orderId, bool termsAccepted, CancellationToken cancellationToken = default)
    {
        var order = await checkout.GetOrderAsync(orderId, cancellationToken);

        if (order is null)
        {
            return NotFound();
        }

        // Terms acceptance is part of the journey in specification section 20, and is
        // checked server-side rather than relying on the browser.
        if (!termsAccepted)
        {
            TempData["Error"] = "Please confirm the client accepts the terms before continuing.";
            return RedirectToAction(nameof(Index), new { clientId = order.ClientId });
        }

        var origin = $"{Request.Scheme}://{Request.Host}";

        var (succeeded, redirectUrl, error) = await checkout.BeginPaymentAsync(
            orderId,
            $"{origin}/checkout/{orderId}/success",
            $"{origin}/checkout/{orderId}/failure",
            cancellationToken);

        if (!succeeded)
        {
            TempData["Error"] = error;
            return RedirectToAction(nameof(Index), new { clientId = order.ClientId });
        }

        return Redirect(redirectUrl!);
    }

    /// <summary>
    /// Where the client returns from the provider. The return itself proves nothing, so
    /// the outcome is confirmed with the provider before anything is activated.
    /// </summary>
    [HttpGet("{orderId:guid}/success")]
    [AllowAnonymous]
    public async Task<IActionResult> Success(Guid orderId, CancellationToken cancellationToken = default)
    {
        var result = await checkout.CompleteAsync(orderId, cancellationToken);
        var order = await checkout.GetOrderAsync(orderId, cancellationToken);

        if (order is null)
        {
            return NotFound();
        }

        if (!result.Succeeded)
        {
            logger.LogWarning("Order {OrderId} returned to success but was not confirmed.", orderId);

            // The webhook may still confirm this independently, so the page says the
            // payment is being checked rather than declaring it failed.
            return View("Pending", Build(order, result.Error));
        }

        return View(Build(order));
    }

    [HttpGet("{orderId:guid}/failure")]
    [AllowAnonymous]
    public async Task<IActionResult> Failure(Guid orderId, CancellationToken cancellationToken = default)
    {
        var order = await checkout.GetOrderAsync(orderId, cancellationToken);

        return order is null ? NotFound() : View(Build(order));
    }

    [HttpPost("{orderId:guid}/cancel")]
    [Authorize(Policy = Permissions.Payments.StartCheckout)]
    public async Task<IActionResult> Cancel(Guid orderId, CancellationToken cancellationToken = default)
    {
        var order = await checkout.GetOrderAsync(orderId, cancellationToken);

        if (order is null)
        {
            return NotFound();
        }

        var result = await checkout.CancelAsync(orderId, CurrentUserId(), cancellationToken);

        if (!result.Succeeded)
        {
            TempData["Error"] = result.Error;
        }

        return Redirect($"/admin/clients/{order.ClientId}");
    }

    /// <summary>
    /// Stands in for the provider's hosted page when no provider is configured, so the
    /// journey can be walked end to end in development. Never reachable with a real
    /// provider, and the stub refuses to authorise outside development.
    /// </summary>
    [HttpGet("{orderId:guid}/stub")]
    [Authorize(Policy = Permissions.Payments.StartCheckout)]
    public async Task<IActionResult> Stub(Guid orderId, CancellationToken cancellationToken = default)
    {
        if (provider.IsLive)
        {
            return NotFound();
        }

        var order = await checkout.GetOrderAsync(orderId, cancellationToken);

        return order is null ? NotFound() : View(Build(order));
    }

    private CheckoutViewModel Build(Order order, string? error = null) => new()
    {
        OrderId = order.Id,
        ClientId = order.ClientId,
        ClientName = order.Client.PublicName,
        ProductName = order.Product.Name,
        ProductDescription = order.Product.Description,
        Amount = order.Amount,
        Currency = order.Currency,
        Status = order.Status,
        PaymentStatus = order.Transactions
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => (PaymentStatus?)t.Status)
            .FirstOrDefault(),
        Brand = brandOptions.Value,
        ProviderIsLive = provider.IsLive,
        Error = error ?? TempData["Error"] as string
    };

    private Guid? CurrentUserId() =>
        Guid.TryParse(userManager.GetUserId(User), out var id) ? id : null;
}
