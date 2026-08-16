using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Msm.Portfolio.Web.Configuration;
using Msm.Portfolio.Web.Domain.Enums;
using Msm.Portfolio.Web.Services;
using Msm.Portfolio.Web.ViewModels;

namespace Msm.Portfolio.Web.Controllers;

/// <summary>
/// Where a legal guardian approves an under-18 client (specification sections 11 and 34).
/// </summary>
/// <remarks>
/// Anonymous by necessity: the guardian has no account. The token in the URL is the
/// only authorisation, so it is single use and time limited.
/// </remarks>
[Route("guardian")]
[AllowAnonymous]
public class GuardianController(
    IGuardianConsentService consentService,
    IOptions<GuardianConsentOptions> consentOptions,
    IOptions<MsmBrandOptions> brandOptions) : Controller
{
    [HttpGet("approve/{token}")]
    public async Task<IActionResult> Approve(string token, CancellationToken cancellationToken = default)
    {
        var consent = await consentService.FindByTokenAsync(token, cancellationToken);

        if (consent is null)
        {
            return View("InvalidLink");
        }

        if (consent.Status == GuardianConsentStatus.Approved)
        {
            return View("AlreadyApproved", BuildViewModel(consent, token));
        }

        if (consent.TokenExpiresAt is { } expiresAt && DateTimeOffset.UtcNow > expiresAt)
        {
            return View("LinkExpired");
        }

        return View(BuildViewModel(consent, token));
    }

    [HttpPost("approve/{token}")]
    public async Task<IActionResult> Approve(
        string token,
        GuardianApprovalViewModel model,
        CancellationToken cancellationToken = default)
    {
        var consent = await consentService.FindByTokenAsync(token, cancellationToken);

        if (consent is null)
        {
            return View("InvalidLink");
        }

        if (consent.Status == GuardianConsentStatus.Approved)
        {
            return View("AlreadyApproved", BuildViewModel(consent, token));
        }

        if (!model.Agreed)
        {
            ModelState.AddModelError(
                nameof(model.Agreed), "Please confirm you agree before submitting.");

            return View(BuildViewModel(consent, token));
        }

        // Recording the approval also notifies staff and writes the audit entry, all in
        // one transaction.
        if (!await consentService.ApproveAsync(consent, cancellationToken))
        {
            return View("LinkExpired");
        }

        return View("Approved", BuildViewModel(consent, token));
    }

    private GuardianApprovalViewModel BuildViewModel(
        Domain.Entities.GuardianConsent consent,
        string token) => new()
        {
            Token = token,
            GuardianName = consent.GuardianName,
            ClientName = consent.Client.FullName,
            Relationship = consent.Relationship,
            ConsentVersion = consent.ConsentVersion,
            ConsentText = consentOptions.Value.ConsentText,
            BusinessName = brandOptions.Value.BusinessName,
            ApprovedAt = consent.ApprovedAt
        };
}
