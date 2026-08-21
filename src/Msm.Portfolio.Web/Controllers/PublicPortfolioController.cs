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
    /// Receives an agency enquiry. It reaches MSM and the model; the model's own email
    /// and telephone are never disclosed to the enquirer (specification section 46).
    /// </summary>
    /// <remarks>
    /// The prefix is not optional. The form is rendered from the page's view model, so
    /// every field posts as "Enquiry.Name" and so on. Bound without the prefix, the
    /// binder finds nothing, and the page comes back saying "Please enter your name"
    /// over a form with the name still typed into it — every enquiry rejected, and
    /// looking to the agency like their own mistake.
    /// </remarks>
    [HttpPost("/{slug}/enquire")]
    [EnableRateLimiting(RateLimitPolicies.PublicEnquiry)]
    public async Task<IActionResult> Enquire(
        string slug,
        [Bind(Prefix = nameof(PublicPortfolioViewModel.Enquiry))] EnquiryViewModel model,
        CancellationToken cancellationToken = default)
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
        var outcome = await portfolios.SendEnquiryAsync(
            portfolio.ClientId, model.Name, model.Company, model.Email, model.Phone, model.Message,
            cancellationToken);

        if (outcome == EnquiryOutcome.UnknownModel)
        {
            return NotFound();
        }

        if (outcome == EnquiryOutcome.NotDelivered)
        {
            // Nothing is stored, so there is no copy of this to follow up. Saying "thank
            // you" here would leave an agency waiting on a reply to a message that never
            // existed. The form comes back with what they typed still in it.
            ModelState.AddModelError(
                string.Empty,
                "We could not deliver your enquiry just now. Please try again shortly.");

            return View(nameof(Index), new PublicPortfolioViewModel
            {
                Portfolio = portfolio,
                Brand = brandOptions.Value,
                Enquiry = model
            });
        }

        TempData["EnquirySent"] = true;

        return Redirect($"/{slug}#contact");
    }
}
