using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Msm.Portfolio.Web.Configuration;
using Msm.Portfolio.Web.Services;
using Msm.Portfolio.Web.ViewModels;

namespace Msm.Portfolio.Web.Controllers;

/// <summary>
/// A model's public portfolio, served from the site root as /{slug}
/// (specification sections 15, 16, 34 and 46).
/// </summary>
/// <remarks>
/// Open to anyone with the link. Agencies do not sign in, and there is deliberately no
/// join or sign-up route from these pages. Route matching prefers literal segments over
/// parameters, so /admin and the other application areas are unaffected by this
/// catch-all; slug creation additionally refuses those names.
/// </remarks>
[AllowAnonymous]
public class PublicPortfolioController(
    IPublicPortfolioService portfolios,
    IOptions<MsmBrandOptions> brandOptions) : Controller
{
    [HttpGet("/{slug}")]
    public async Task<IActionResult> Index(string slug, CancellationToken cancellationToken = default)
    {
        var portfolio = await portfolios.GetBySlugAsync(slug, cancellationToken);

        // An unpublished or unknown slug is simply not found. No hint is given that a
        // portfolio exists but is private.
        if (portfolio is null)
        {
            return NotFound();
        }

        return View(new PublicPortfolioViewModel
        {
            Portfolio = portfolio,
            Brand = brandOptions.Value,
            Enquiry = new EnquiryViewModel { ClientId = portfolio.ClientId },
            EnquirySent = TempData["EnquirySent"] as bool? ?? false
        });
    }

    /// <summary>
    /// Receives an agency enquiry. The message goes to MSM; the model's own email and
    /// telephone are never disclosed (specification section 46).
    /// </summary>
    [HttpPost("/{slug}/enquire")]
    [EnableRateLimiting(RateLimitPolicies.PublicEnquiry)]
    public async Task<IActionResult> Enquire(
        string slug, EnquiryViewModel model, CancellationToken cancellationToken = default)
    {
        var portfolio = await portfolios.GetBySlugAsync(slug, cancellationToken);

        if (portfolio is null)
        {
            return NotFound();
        }

        if (!ModelState.IsValid)
        {
            return View(nameof(Index), new PublicPortfolioViewModel
            {
                Portfolio = portfolio,
                Brand = brandOptions.Value,
                Enquiry = model
            });
        }

        // The model is taken from the portfolio in the URL, not from the posted form,
        // so an enquiry cannot be redirected at another client.
        var recorded = await portfolios.RecordEnquiryAsync(
            portfolio.ClientId, model.Name, model.Company, model.Email, model.Phone, model.Message,
            cancellationToken);

        if (!recorded)
        {
            return NotFound();
        }

        TempData["EnquirySent"] = true;

        return Redirect($"/{slug}#contact");
    }
}
