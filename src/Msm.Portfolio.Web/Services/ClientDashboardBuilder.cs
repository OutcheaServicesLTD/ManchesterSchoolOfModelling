using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Msm.Portfolio.Web.Configuration;
using Msm.Portfolio.Web.Data;
using Msm.Portfolio.Web.Domain.Entities;
using Msm.Portfolio.Web.Domain.Enums;
using Msm.Portfolio.Web.ViewModels;

namespace Msm.Portfolio.Web.Services;

/// <summary>
/// Builds a model's dashboard.
/// </summary>
/// <remarks>
/// Shared by the model's own page and by the preview an administrator opens from the
/// client record. One builder and one view between them, so what staff are shown really is
/// what the model is shown — two copies would drift apart, and a preview that has drifted
/// is worse than no preview, because it is believed.
/// </remarks>
public interface IClientDashboardBuilder
{
    Task<ClientDashboardViewModel> BuildAsync(
        ClientProfile client, bool isPreview = false, CancellationToken cancellationToken = default);
}

public class ClientDashboardBuilder(
    ApplicationDbContext db,
    IMaintenanceService maintenance,
    IOptions<MediaOptions> mediaOptions,
    IOptions<MsmBrandOptions> brandOptions) : IClientDashboardBuilder
{
    public async Task<ClientDashboardViewModel> BuildAsync(
        ClientProfile client, bool isPreview = false, CancellationToken cancellationToken = default)
    {
        var media = mediaOptions.Value;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var counts = await db.MediaAssets
            .Where(m => m.ClientId == client.Id && !m.IsDeleted && m.MediaType == MediaType.Image)
            .GroupBy(m => m.IsSelectedForPortfolio)
            .Select(g => new { Selected = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var selected = counts.FirstOrDefault(c => c.Selected)?.Count ?? 0;
        var total = counts.Sum(c => c.Count);

        return new ClientDashboardViewModel
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
            ExpiresAt = client.Portfolio?.ExpiresAt,
            IsPreview = isPreview
        };
    }

    /// <summary>
    /// A rough prompt rather than a precise measure: it counts the fields a client can
    /// fill in themselves, so the dashboard can nudge them toward a fuller profile.
    /// </summary>
    private static int CalculateCompletion(ClientProfile client)
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
