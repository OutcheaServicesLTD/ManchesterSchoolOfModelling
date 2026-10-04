using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Msm.Portfolio.Web.Configuration;
using Msm.Portfolio.Web.Domain.Entities;
using Msm.Portfolio.Web.Domain.Enums;
using Msm.Portfolio.Web.Services;
using Msm.Portfolio.Web.ViewModels;

namespace Msm.Portfolio.Web.Controllers;

/// <summary>
/// Where a model's account and portfolio profile get created — either filled in by
/// staff after a photoshoot (<c>/onboarding</c>, reached from a GoHighLevel link
/// carrying a contact id), or by the model themselves (<c>/register</c>). Both routes
/// share this one form: what differs is whether a password is collected, which is what
/// <see cref="OnboardingViewModel.IsSelfRegistration"/> is for.
/// </summary>
/// <remarks>
/// Open to anonymous visitors by necessity in both cases. The contact id identifies
/// which CRM contact submitted the form; it is never treated as proof of identity, and
/// nothing already stored is read back to the visitor.
/// </remarks>
[AllowAnonymous]
public class OnboardingController(
    IClientOnboardingService onboarding,
    IMeasurementTemplateProvider templates,
    SignInManager<ApplicationUser> signInManager,
    ILogger<OnboardingController> logger) : Controller
{
    [HttpGet("onboarding")]
    [HttpGet("register")]
    public async Task<IActionResult> Index(
        string? ghlContactId = null,
        string? firstName = null,
        string? lastName = null,
        string? email = null,
        string? phone = null,
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

        var isSelfRegistration = IsRegisterRoute();

        var model = new OnboardingViewModel
        {
            GhlContactId = ghlContactId,
            IsSelfRegistration = isSelfRegistration,
            // A personalised link — /register?firstName=...&lastName=...&email=...,
            // the kind an email automation sends — fills these in so the model does not
            // retype what the studio already knows. Nothing is submitted on their
            // behalf: they still review it and press Register themselves.
            FirstName = firstName?.Trim() ?? string.Empty,
            LastName = lastName?.Trim() ?? string.Empty,
            Email = email?.Trim() ?? string.Empty,
            Phone = phone?.Trim()
        };

        PrepareTemplate(model);

        return View(isSelfRegistration ? "Register" : "Index", model);
    }

    [HttpPost("onboarding")]
    [HttpPost("register")]
    [EnableRateLimiting(RateLimitPolicies.AnonymousForm)]
    public async Task<IActionResult> Index(
        OnboardingViewModel model,
        CancellationToken cancellationToken = default)
    {
        var isSelfRegistration = IsRegisterRoute();
        model.IsSelfRegistration = isSelfRegistration;

        // The template has to be attached before validation, because the required
        // measurements for the chosen profile type are part of the rules.
        PrepareTemplate(model);
        TryValidateModel(model);

        if (!ModelState.IsValid)
        {
            return View(isSelfRegistration ? "Register" : "Index", model);
        }

        var result = await onboarding.SubmitAsync(model, cancellationToken);

        if (!result.Succeeded)
        {
            ModelState.AddModelError(string.Empty, result.Error ?? "We could not save your details.");
            return View(isSelfRegistration ? "Register" : "Index", model);
        }

        if (isSelfRegistration)
        {
            // Already proved the password moments ago by setting it; no separate
            // credential check is needed to sign the account straight in.
            await signInManager.SignInAsync(result.Client!.ApplicationUser!, isPersistent: false);
            return Redirect("/client");
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        TempData["GuardianPending"] = result.Client!.RequiresGuardianConsent(today);

        return RedirectToAction(nameof(Complete));
    }

    [HttpGet("onboarding/complete")]
    public IActionResult Complete()
    {
        ViewData["GuardianPending"] = TempData["GuardianPending"] as bool? ?? false;
        return View();
    }

    private bool IsRegisterRoute() =>
        Request.Path.StartsWithSegments("/register", StringComparison.OrdinalIgnoreCase);

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
