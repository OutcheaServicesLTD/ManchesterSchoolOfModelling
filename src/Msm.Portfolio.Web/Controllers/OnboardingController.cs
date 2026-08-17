using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Msm.Portfolio.Web.Configuration;
using Msm.Portfolio.Web.Domain.Enums;
using Msm.Portfolio.Web.Services;
using Msm.Portfolio.Web.ViewModels;

namespace Msm.Portfolio.Web.Controllers;

/// <summary>
/// The form the client completes after their photoshoot (specification sections 8 and 34).
/// </summary>
/// <remarks>
/// Reached from a GoHighLevel link carrying the contact id, so it is open to anonymous
/// visitors by necessity. The contact id identifies which CRM contact submitted the
/// form; it is never treated as proof of identity, and nothing already stored is read
/// back to the visitor.
/// </remarks>
[Route("onboarding")]
[AllowAnonymous]
public class OnboardingController(
    IClientOnboardingService onboarding,
    IMeasurementTemplateProvider templates,
    ILogger<OnboardingController> logger) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(
        string? ghlContactId = null,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(ghlContactId)
            && await onboarding.ExistsForContactAsync(ghlContactId, cancellationToken))
        {
            // Deliberately does not prefill the stored profile. The contact id travels in
            // a URL and is not a credential, so echoing a real client's date of birth or
            // location back to whoever holds the link would disclose their personal data.
            logger.LogInformation("Onboarding reopened for an already-submitted contact.");
            return View("AlreadySubmitted");
        }

        var model = new OnboardingViewModel { GhlContactId = ghlContactId };
        PrepareTemplate(model);

        return View(model);
    }

    [HttpPost("")]
    [EnableRateLimiting(RateLimitPolicies.AnonymousForm)]
    public async Task<IActionResult> Index(
        OnboardingViewModel model,
        CancellationToken cancellationToken = default)
    {
        // The template has to be attached before validation, because the required
        // measurements for the chosen profile type are part of the rules.
        PrepareTemplate(model);
        TryValidateModel(model);

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var result = await onboarding.SubmitAsync(model, cancellationToken);

        if (!result.Succeeded)
        {
            ModelState.AddModelError(string.Empty, result.Error ?? "We could not save your details.");
            return View(model);
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        TempData["GuardianPending"] = result.Client!.RequiresGuardianConsent(today);

        return RedirectToAction(nameof(Complete));
    }

    [HttpGet("complete")]
    public IActionResult Complete()
    {
        ViewData["GuardianPending"] = TempData["GuardianPending"] as bool? ?? false;
        return View();
    }

    /// <summary>
    /// Attaches the measurement fields for the chosen profile type and lines the posted
    /// values up against them, so the form redisplays what the client entered.
    /// </summary>
    private void PrepareTemplate(OnboardingViewModel model)
    {
        var template = templates.GetTemplate(model.ModelProfileType);
        model.Template = template;

        var posted = model.Measurements.ToDictionary(m => m.Key, m => m, StringComparer.Ordinal);

        model.Measurements = [.. template.Select(field =>
            posted.TryGetValue(field.Key, out var existing)
                ? existing
                : new MeasurementInputModel { Key = field.Key, Unit = field.Unit })];

        if (model.DateOfBirth is { } dob)
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            model.GuardianRequired =
                new Domain.Entities.ClientProfile { DateOfBirth = dob }.AgeOn(today) < 18;
        }
    }
}
