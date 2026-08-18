using Microsoft.EntityFrameworkCore;
using Msm.Portfolio.Web.Data;
using Msm.Portfolio.Web.Domain.Entities;
using Msm.Portfolio.Web.Domain.Enums;

namespace Msm.Portfolio.Web.Services;

/// <summary>One photograph on a public portfolio.</summary>
public record PublicImage(Guid AssetId, MediaOrientation Orientation, int? Width, int? Height, bool IsFeatured);

/// <summary>
/// Which part of the cover photograph must survive being cropped.
/// </summary>
/// <remarks>
/// Carried through to the page as a CSS object-position, formatted here rather than in a
/// view so it cannot come out as "38,5%" on a machine with a comma for a decimal point.
/// </remarks>
public record FocalPoint(int X, int Y)
{
    public string AsCss => FormattableString.Invariant($"{X}% {Y}%");

    public static FocalPoint? From(int? x, int? y) =>
        x is null || y is null ? null : new FocalPoint(x.Value, y.Value);
}

/// <summary>One measurement shown in the public stats section.</summary>
public record PublicMeasurement(string Label, string Value, string? Unit);

/// <summary>
/// Everything an agency sees on a model's public page.
/// </summary>
/// <remarks>
/// Deliberately a projection rather than the entity. The client record holds an email
/// address, a telephone number, a CRM identifier and guardian details, none of which
/// may reach a public page (specification sections 10 and 46). Building a separate
/// shape means those fields cannot leak by accident through a view.
/// </remarks>
public record PublicPortfolio(
    Guid ClientId,
    string Slug,
    string Name,
    string? Location,
    int? Age,
    string? Biography,
    Guid? FeaturedAssetId,
    FocalPoint? CoverFocus,
    IReadOnlyList<PublicImage> Images,
    IReadOnlyList<PublicMeasurement> Measurements,
    Guid? SelfTapeAssetId,
    string? InstagramUrl,
    string? TikTokUrl)
{
    public bool HasSelfTape => SelfTapeAssetId is not null;

    public bool HasSocialLinks =>
        !string.IsNullOrWhiteSpace(InstagramUrl) || !string.IsNullOrWhiteSpace(TikTokUrl);
}

/// <summary>One card on the Model Board (specification section 18).</summary>
public record ModelBoardCard(
    string Slug,
    string Name,
    string? Location,
    Guid? FeaturedAssetId,
    FocalPoint? CoverFocus);

public interface IPublicPortfolioService
{
    /// <summary>The published portfolio at this slug, or null if there is not one.</summary>
    Task<PublicPortfolio?> GetBySlugAsync(string slug, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ModelBoardCard>> GetModelBoardAsync(CancellationToken cancellationToken = default);

    Task<bool> RecordEnquiryAsync(
        Guid clientId, string name, string? company, string email, string? phone, string message,
        CancellationToken cancellationToken = default);
}

public class PublicPortfolioService(
    ApplicationDbContext db,
    IMeasurementTemplateProvider templates,
    INotificationService notifications,
    ILogger<PublicPortfolioService> logger) : IPublicPortfolioService
{
    public async Task<PublicPortfolio?> GetBySlugAsync(
        string slug, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            return null;
        }

        var portfolio = await db.Portfolios
            .Include(p => p.Client)
            .ThenInclude(c => c.Measurements)
            // IsPublished is the only gate. A portfolio with a slug that has been
            // unpublished must read as missing, not as a private page.
            .FirstOrDefaultAsync(p => p.Slug == slug && p.IsPublished, cancellationToken);

        if (portfolio is null)
        {
            return null;
        }

        var client = portfolio.Client;

        var assets = await db.MediaAssets
            .Where(m => m.ClientId == client.Id && !m.IsDeleted)
            .OrderBy(m => m.DisplayOrder)
            .ToListAsync(cancellationToken);

        // Only images the client has actually chosen. The rest of the 60-image pool is
        // private, even though this portfolio is public (specification section 12).
        var images = assets
            .Where(m => m.MediaType == MediaType.Image && m.IsSelectedForPortfolio)
            .Select(m => new PublicImage(m.Id, m.Orientation, m.Width, m.Height, m.IsFeatured))
            .ToList();

        var selfTape = assets.FirstOrDefault(m => m.MediaType == MediaType.SelfTape);
        var cover = assets.FirstOrDefault(m => m.Id == portfolio.FeaturedMediaId);

        var template = templates.GetTemplate(client.ModelProfileType);
        var measurements = client.Measurements
            .OrderBy(m => m.DisplayOrder)
            .Select(m =>
            {
                var field = template.FirstOrDefault(f => f.Key == m.MeasurementType);

                return new PublicMeasurement(
                    field?.Label ?? m.MeasurementType,
                    m.Value,
                    m.Unit switch
                    {
                        MeasurementUnit.Centimetres => "cm",
                        MeasurementUnit.Inches => "in",
                        _ => null
                    });
            })
            .ToList();

        return new PublicPortfolio(
            client.Id,
            portfolio.Slug!,
            client.PublicName,
            client.Location,
            client.AgeOn(DateOnly.FromDateTime(DateTime.UtcNow)),
            client.Biography,
            portfolio.FeaturedMediaId,
            FocalPoint.From(cover?.FocalPointX, cover?.FocalPointY),
            images,
            measurements,
            selfTape?.Id,
            client.InstagramUrl,
            client.TikTokUrl);
    }

    /// <summary>
    /// The Model Board, queried live from published portfolios rather than from a
    /// separate copy of each model (specification section 47). Unpublishing a portfolio
    /// therefore removes it from the board with no extra step.
    /// </summary>
    public async Task<IReadOnlyList<ModelBoardCard>> GetModelBoardAsync(
        CancellationToken cancellationToken = default)
    {
        var rows = await db.Portfolios
            .Where(p => p.IsPublished
                        && p.IsVisibleOnModelBoard
                        && p.Slug != null
                        // A card with no image would be an empty tile on the board.
                        && p.FeaturedMediaId != null)
            .Select(p => new
            {
                p.Slug,
                p.FeaturedMediaId,
                p.Client.FirstName,
                p.Client.LastName,
                p.Client.DisplayName,
                p.Client.Location,
                p.PublishedAt,
                Focal = db.MediaAssets
                    .Where(m => m.Id == p.FeaturedMediaId)
                    .Select(m => new { m.FocalPointX, m.FocalPointY })
                    .FirstOrDefault(),
                Subscription = db.MaintenanceSubscriptions
                    .Where(s => s.ClientId == p.ClientId)
                    .OrderByDescending(s => s.CreatedAt)
                    .FirstOrDefault()
            })
            .ToListAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;

        return
        [
            .. rows
                // Specification section 18 requires an active entitlement as well as
                // publication. Unpublishing on expiry already removes a model, but this
                // covers the window between a grace period elapsing and the worker
                // noticing, so nobody is listed while unentitled.
                .Where(r => r.Subscription is null || r.Subscription.IsEntitlementActive(now))
                .OrderByDescending(r => r.PublishedAt)
                .Select(r => new ModelBoardCard(
                    r.Slug!,
                    string.IsNullOrWhiteSpace(r.DisplayName)
                        ? $"{r.FirstName} {r.LastName}".Trim()
                        : r.DisplayName,
                    r.Location,
                    r.FeaturedMediaId,
                    FocalPoint.From(r.Focal?.FocalPointX, r.Focal?.FocalPointY)))
        ];
    }

    public async Task<bool> RecordEnquiryAsync(
        Guid clientId,
        string name,
        string? company,
        string email,
        string? phone,
        string message,
        CancellationToken cancellationToken = default)
    {
        // Re-checked here rather than trusted from the page: an enquiry must only be
        // possible against a portfolio that is actually public.
        var client = await db.ClientProfiles
            .Include(c => c.Portfolio)
            .FirstOrDefaultAsync(c => c.Id == clientId, cancellationToken);

        if (client?.Portfolio is not { IsPublished: true })
        {
            logger.LogWarning("Enquiry rejected for client {ClientId}: no published portfolio.", clientId);
            return false;
        }

        db.Enquiries.Add(new Enquiry
        {
            ClientId = clientId,
            Name = name.Trim(),
            Company = string.IsNullOrWhiteSpace(company) ? null : company.Trim(),
            Email = email.Trim(),
            Phone = string.IsNullOrWhiteSpace(phone) ? null : phone.Trim(),
            Message = message.Trim()
        });

        // Goes to MSM, never to the model. The client is not notified and their own
        // contact details are not involved (specification section 46).
        await notifications.NotifyStaffAsync(
            NotificationTypes.EnquiryReceived,
            $"New enquiry about {client.PublicName} from {name.Trim()}.",
            "/admin/enquiries",
            cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        return true;
    }
}
