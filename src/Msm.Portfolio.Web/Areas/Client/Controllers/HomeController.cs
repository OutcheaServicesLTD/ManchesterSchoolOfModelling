using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Msm.Portfolio.Web.Authorization;
using Msm.Portfolio.Web.Configuration;
using Msm.Portfolio.Web.Data;
using Msm.Portfolio.Web.Domain.Enums;
using Msm.Portfolio.Web.Services;
using Msm.Portfolio.Web.ViewModels;

namespace Msm.Portfolio.Web.Areas.Client.Controllers;

/// <summary>
/// The client's dashboard (specification section 17).
/// </summary>
[Area("Client")]
[Route("client")]
[Authorize(Policy = Policies.ClientArea)]
public class HomeController(
    IClientProfileAccessor profiles,
    IMaintenanceService maintenance,
    ApplicationDbContext db,
    IOptions<MediaOptions> mediaOptions,
    IOptions<MsmBrandOptions> brandOptions) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken = default)
    {
        var client = await profiles.GetCurrentAsync(User, cancellationToken);

        if (client is null)
        {
            // Signed in as a Client with no profile attached. Nothing useful to show,
            // and no other client's data may be substituted.
            return View("NoProfile");
        }

        var media = mediaOptions.Value;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var counts = await db.MediaAssets
            .Where(m => m.ClientId == client.Id && !m.IsDeleted && m.MediaType == MediaType.Image)
            .GroupBy(m => m.IsSelectedForPortfolio)
            .Select(g => new { Selected = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var selected = counts.FirstOrDefault(c => c.Selected)?.Count ?? 0;
        var total = counts.Sum(c => c.Count);

        var model = new ClientDashboardViewModel
        {
            Name = client.PublicName,
            PortfolioStatus = client.Portfolio?.Status ?? PortfolioStatus.AwaitingClientInformation,
            IsPublished = client.Portfolio?.IsPublished ?? false,
            PublicUrl = client.Portfolio is { IsPublished: true, Slug: { } slug }
                ? $"{brandOptions.Value.PublicDomain.TrimEnd('/')}/{slug}"
                : null,
            PortfolioImageCount = selected,
            PortfolioImageLimit = media.PortfolioImageLimit,
            MediaPoolCount = total,
            MediaPoolLimit = media.MediaPoolImageLimit,
            ProfileCompletionPercent = CalculateCompletion(client),
            GuardianApprovalPending = client.IsBlockedPendingGuardianConsent(today),
            MaintenanceWarning = await maintenance.GetWarningAsync(client.Id, cancellationToken),
            ExpiresAt = client.Portfolio?.ExpiresAt
        };

        return View(model);
    }

    /// <summary>
    /// A rough prompt rather than a precise measure: it counts the fields a client can
    /// fill in themselves, so the dashboard can nudge them toward a fuller profile.
    /// </summary>
    private static int CalculateCompletion(Domain.Entities.ClientProfile client)
    {
        var fields = new[]
        {
            !string.IsNullOrWhiteSpace(client.FirstName),
            !string.IsNullOrWhiteSpace(client.LastName),
            client.DateOfBirth is not null,
            !string.IsNullOrWhiteSpace(client.Location),
            client.ModelProfileType != ModelProfileType.Unspecified,
            !string.IsNullOrWhiteSpace(client.Biography),
            !string.IsNullOrWhiteSpace(client.HairColour),
            !string.IsNullOrWhiteSpace(client.EyeColour),
            client.Measurements.Count > 0
        };

        return (int)Math.Round(fields.Count(f => f) * 100.0 / fields.Length);
    }
}
