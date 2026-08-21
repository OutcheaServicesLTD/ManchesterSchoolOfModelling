using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Msm.Portfolio.Web.Configuration;
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
    int? CoverWidth,
    int? CoverHeight,
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

/// <summary>What became of an agency's enquiry.</summary>
public enum EnquiryOutcome
{
    /// <summary>It reached the model, or the guardian acting for them.</summary>
    Delivered,

    /// <summary>There is no published portfolio at that id, so there is nobody to reach.</summary>
    UnknownModel,

    /// <summary>
    /// Nobody received it. Since nothing is stored, this has to be said to the agency
    /// rather than swallowed — otherwise they are thanked for a message that went
    /// nowhere and they never find out.
    /// </summary>
    NotDelivered
}

public interface IPublicPortfolioService
{
    /// <summary>The published portfolio at this slug, or null if there is not one.</summary>
    Task<PublicPortfolio?> GetBySlugAsync(string slug, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ModelBoardCard>> GetModelBoardAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Passes an agency's enquiry to the model. Nothing is kept: MSM does not store it
    /// and is not told about it, so the message is the whole of the enquiry.
    /// </summary>
    Task<EnquiryOutcome> SendEnquiryAsync(
        Guid clientId, string name, string? company, string email, string? phone, string message,
        CancellationToken cancellationToken = default);
}

public class PublicPortfolioService(
    ApplicationDbContext db,
    IMeasurementTemplateProvider templates,
    INotificationService notifications,
    IEmailSender email,
    IOptions<MsmBrandOptions> brand,
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

        // Hair and eye colour are columns on the client rather than measurement rows —
        // the templates hold numbers, and these are words — but on an agency's card they
        // belong in the same list as height and shoe size, which is where an agency looks
        // for them. Last, because the numbers are read as a group.
        foreach (var (label, value) in new[]
                 {
                     ("Hair", client.HairColour),
                     ("Eyes", client.EyeColour)
                 })
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                measurements.Add(new PublicMeasurement(label, value.Trim(), null));
            }
        }

        return new PublicPortfolio(
            client.Id,
            portfolio.Slug!,
            client.PublicName,
            client.Location,
            client.AgeOn(DateOnly.FromDateTime(DateTime.UtcNow)),
            client.Biography,
            portfolio.FeaturedMediaId,
            FocalPoint.From(cover?.FocalPointX, cover?.FocalPointY),
            cover?.Width,
            cover?.Height,
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

    /// <remarks>
    /// The enquiry belongs to the model, not to the studio. It is not stored and MSM is
    /// not notified — an agency that contacts a model is dealing with that model.
    ///
    /// Which means the message has to actually arrive. With nothing kept there is no
    /// second chance and nowhere to look afterwards, so a send that fails is reported
    /// back to the agency instead of being logged and forgotten.
    /// </remarks>
    public async Task<EnquiryOutcome> SendEnquiryAsync(
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
            .Include(c => c.ApplicationUser)
            .Include(c => c.GuardianConsent)
            .FirstOrDefaultAsync(c => c.Id == clientId, cancellationToken);

        if (client?.Portfolio is not { IsPublished: true })
        {
            logger.LogWarning("Enquiry rejected for client {ClientId}: no published portfolio.", clientId);
            return EnquiryOutcome.UnknownModel;
        }

        var delivered = await SendEnquiryToModelAsync(
            client, name, company, email, phone, message, cancellationToken);

        if (!delivered)
        {
            return EnquiryOutcome.NotDelivered;
        }

        // On the model's own dashboard, so an enquiry is visible when they next sign in
        // as well as in their inbox. This is the model's notification, not the studio's:
        // no member of staff is told.
        notifications.NotifyUser(
            client.ApplicationUserId,
            NotificationTypes.EnquiryReceived,
            $"New enquiry from {name.Trim()}.",
            "/client");

        await db.SaveChangesAsync(cancellationToken);

        return EnquiryOutcome.Delivered;
    }

    /// <summary>
    /// Sends the enquiry to the model, and reports whether it arrived.
    /// </summary>
    /// <remarks>
    /// A model under eighteen is the exception: their enquiries go to the guardian whose
    /// consent already governs the portfolio, not to the child. With no guardian address
    /// on file there is nowhere safe to send it, and the enquiry fails rather than being
    /// delivered to a minor.
    /// </remarks>
    private async Task<bool> SendEnquiryToModelAsync(
        ClientProfile client,
        string name,
        string? company,
        string enquirerEmail,
        string? phone,
        string message,
        CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var isChild = client.RequiresGuardianConsent(today);

        var recipient = isChild
            ? client.GuardianConsent?.Email
            : client.ApplicationUser?.Email;

        if (string.IsNullOrWhiteSpace(recipient))
        {
            logger.LogError(
                "Enquiry for client {ClientId} has no {Recipient} address to send to.",
                client.Id, isChild ? "guardian" : "model");
            return false;
        }

        var addressee = isChild
            ? $"{client.GuardianConsent!.GuardianName}, about {client.PublicName}"
            : client.PublicName;

        var lines = new List<string>
        {
            $"An enquiry has come in through your {brand.Value.BusinessName} portfolio.",
            "",
            $"For: {addressee}",
            $"From: {name.Trim()}"
        };

        if (!string.IsNullOrWhiteSpace(company))
        {
            lines.Add($"Company: {company.Trim()}");
        }

        // Their address and telephone number, so a reply goes straight back to them.
        lines.Add($"Email: {enquirerEmail.Trim()}");

        if (!string.IsNullOrWhiteSpace(phone))
        {
            lines.Add($"Telephone: {phone.Trim()}");
        }

        lines.Add("");
        lines.Add(message.Trim());
        lines.Add("");
        lines.Add($"This enquiry came to you, not to {brand.Value.BusinessName}. Reply to "
                  + "the address above. If anything about it seems wrong, tell us before "
                  + "you do.");

        try
        {
            return await email.SendAsync(
                recipient,
                $"Enquiry about {client.PublicName} from {name.Trim()}",
                string.Join("\n", lines),
                cancellationToken);
        }
        catch (Exception exception)
        {
            // Nothing is stored, so a swallowed failure is an enquiry that has simply
            // disappeared. Reported instead, and the agency is told.
            logger.LogError(exception,
                "Could not send the enquiry for client {ClientId} on to {Recipient}.",
                client.Id, isChild ? "the guardian" : "the model");

            return false;
        }
    }
}
