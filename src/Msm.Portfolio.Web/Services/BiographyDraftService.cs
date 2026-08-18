using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Msm.Portfolio.Web.Data;
using Msm.Portfolio.Web.Domain.Entities;
using Msm.Portfolio.Web.Domain.Enums;
using Msm.Portfolio.Web.Integrations.Bio;

namespace Msm.Portfolio.Web.Services;

public record BiographyDraftSummary(int Succeeded, int Failed, int Total);

public interface IBiographyDraftService
{
    /// <summary>Writes the drafts that have been asked for and not yet written.</summary>
    Task<BiographyDraftSummary> WritePendingAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Puts an accepted draft into the biography itself, and closes it either way.
    /// </summary>
    Task<bool> ResolveAsync(
        Guid clientId, bool accept, Guid? actingUserId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Turns a requested biography draft into text (specification section 10).
/// </summary>
/// <remarks>
/// Runs on a worker rather than inside the approval that asked for it. Approving a
/// portfolio is the administrator's action and must not sit waiting on somebody else's
/// API, nor fail because that API is down.
/// </remarks>
public class BiographyDraftService(
    ApplicationDbContext db,
    IBiographyWriter writer,
    IMeasurementTemplateProvider templates,
    IAuditService audit,
    IOptions<BiographyOptions> options,
    ILogger<BiographyDraftService> logger) : IBiographyDraftService
{
    public async Task<BiographyDraftSummary> WritePendingAsync(
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;

        var due = await db.ClientProfiles
            .Include(c => c.Measurements)
            .Where(c => c.BiographyDraftStatus == BiographyDraftStatus.Pending
                        && (c.BiographyDraftNextAttemptAt == null || c.BiographyDraftNextAttemptAt <= now))
            .ToListAsync(cancellationToken);

        if (due.Count == 0)
        {
            return new BiographyDraftSummary(0, 0, 0);
        }

        var succeeded = 0;
        var failed = 0;

        foreach (var client in due)
        {
            var photographs = await db.MediaAssets.CountAsync(
                m => m.ClientId == client.Id && !m.IsDeleted
                     && m.MediaType == MediaType.Image && m.IsSelectedForPortfolio,
                cancellationToken);

            var hasSelfTape = await db.MediaAssets.AnyAsync(
                m => m.ClientId == client.Id && !m.IsDeleted && m.MediaType == MediaType.SelfTape,
                cancellationToken);

            var template = templates.GetTemplate(client.ModelProfileType);

            var facts = new BiographyFacts(
                client.PublicName,
                client.Location,
                client.AgeOn(DateOnly.FromDateTime(DateTime.UtcNow)),
                client.ModelProfileType.ToString(),
                [
                    .. client.Measurements
                        .OrderBy(m => m.DisplayOrder)
                        .Select(m => new BiographyMeasurement(
                            template.FirstOrDefault(f => f.Key == m.MeasurementType)?.Label ?? m.MeasurementType,
                            m.Value,
                            m.Unit switch
                            {
                                MeasurementUnit.Centimetres => "cm",
                                MeasurementUnit.Inches => "in",
                                _ => null
                            }))
                ],
                hasSelfTape,
                photographs);

            var result = await writer.WriteAsync(facts, cancellationToken);

            client.BiographyDraftAttempts++;

            if (result.Succeeded && !string.IsNullOrWhiteSpace(result.Text))
            {
                client.BiographyDraft = result.Text;
                client.BiographyDraftGeneratedAt = DateTimeOffset.UtcNow;
                client.BiographyDraftError = null;
                client.BiographyDraftStatus = BiographyDraftStatus.Ready;

                audit.Record(nameof(ClientProfile), client.Id.ToString(), "BiographyDraftWritten");
                succeeded++;

                continue;
            }

            client.BiographyDraftError = result.Error;
            failed++;

            if (client.BiographyDraftAttempts >= options.Value.MaxAttempts)
            {
                // Left alone rather than retried for ever. The reason stays on the record
                // and an administrator writes the biography themselves, which was always
                // the fallback.
                client.BiographyDraftStatus = BiographyDraftStatus.Failed;

                logger.LogWarning(
                    "Gave up on a biography draft for {ClientId} after {Attempts} attempts: {Error}",
                    client.Id, client.BiographyDraftAttempts, result.Error);
            }
            else
            {
                // Backs off, and the delay is held on the row so a restart during an
                // outage does not reset it and hammer a service already struggling.
                client.BiographyDraftNextAttemptAt =
                    DateTimeOffset.UtcNow.AddMinutes(5 * client.BiographyDraftAttempts);
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        return new BiographyDraftSummary(succeeded, failed, due.Count);
    }

    public async Task<bool> ResolveAsync(
        Guid clientId, bool accept, Guid? actingUserId, CancellationToken cancellationToken = default)
    {
        var client = await db.ClientProfiles
            .FirstOrDefaultAsync(c => c.Id == clientId, cancellationToken);

        if (client is null || client.BiographyDraftStatus is not BiographyDraftStatus.Ready)
        {
            return false;
        }

        if (accept)
        {
            client.Biography = client.BiographyDraft;

            audit.Record(nameof(ClientProfile), clientId.ToString(),
                "BiographyDraftAccepted", userId: actingUserId);
        }
        else
        {
            audit.Record(nameof(ClientProfile), clientId.ToString(),
                "BiographyDraftDiscarded", userId: actingUserId);
        }

        // Closed either way, and the text is dropped. Closed is what stops another draft
        // ever being asked for: an administrator who threw one away meant it.
        client.BiographyDraft = null;
        client.BiographyDraftStatus = BiographyDraftStatus.Closed;

        await db.SaveChangesAsync(cancellationToken);

        return true;
    }
}
