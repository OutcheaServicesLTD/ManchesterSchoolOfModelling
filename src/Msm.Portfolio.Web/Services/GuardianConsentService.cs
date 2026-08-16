using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Msm.Portfolio.Web.Configuration;
using Msm.Portfolio.Web.Data;
using Msm.Portfolio.Web.Domain.Entities;
using Msm.Portfolio.Web.Domain.Enums;

namespace Msm.Portfolio.Web.Services;

public interface IGuardianConsentService
{
    /// <summary>
    /// Creates or refreshes the pending consent for a client and returns the record.
    /// Staged for the caller's transaction; the request email is sent separately once
    /// the transaction has committed.
    /// </summary>
    GuardianConsent RequestConsent(
        ClientProfile client,
        string guardianName,
        string relationship,
        string email,
        string? phone);

    Task<GuardianConsent?> FindByTokenAsync(string token, CancellationToken cancellationToken = default);

    /// <summary>Records approval. Returns false when the token has expired or was already used.</summary>
    Task<bool> ApproveAsync(GuardianConsent consent, CancellationToken cancellationToken = default);

    Task SendRequestEmailAsync(
        GuardianConsent consent,
        ClientProfile client,
        CancellationToken cancellationToken = default);
}

public class GuardianConsentService(
    ApplicationDbContext db,
    IAuditService audit,
    INotificationService notifications,
    IEmailSender emailSender,
    IOptions<MsmBrandOptions> brandOptions,
    IOptions<GuardianConsentOptions> consentOptions,
    ILogger<GuardianConsentService> logger) : IGuardianConsentService
{
    public GuardianConsent RequestConsent(
        ClientProfile client,
        string guardianName,
        string relationship,
        string email,
        string? phone)
    {
        var options = consentOptions.Value;
        var consent = client.GuardianConsent;

        if (consent is null)
        {
            consent = new GuardianConsent { ClientId = client.Id };
            client.GuardianConsent = consent;
            db.GuardianConsents.Add(consent);
        }

        consent.GuardianName = guardianName;
        consent.Relationship = relationship;
        consent.Email = email;
        consent.Phone = phone;
        consent.Status = GuardianConsentStatus.Pending;
        consent.ConsentVersion = options.CurrentVersion;
        consent.VerificationToken = GenerateToken();
        consent.TokenExpiresAt = DateTimeOffset.UtcNow.AddDays(options.TokenLifetimeDays);
        consent.ApprovedAt = null;

        audit.Record(
            nameof(GuardianConsent),
            consent.Id.ToString(),
            AuditActions.GuardianConsentRequested,
            newValue: $"Guardian {email} for client {client.Id}, consent version {options.CurrentVersion}");

        return consent;
    }

    public Task<GuardianConsent?> FindByTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return Task.FromResult<GuardianConsent?>(null);
        }

        return db.GuardianConsents
            .Include(c => c.Client)
            .FirstOrDefaultAsync(c => c.VerificationToken == token, cancellationToken);
    }

    public async Task<bool> ApproveAsync(GuardianConsent consent, CancellationToken cancellationToken = default)
    {
        if (consent.Status == GuardianConsentStatus.Approved)
        {
            // Already approved. Treated as success so a guardian who opens the link
            // twice sees confirmation rather than an error.
            return true;
        }

        if (consent.TokenExpiresAt is { } expiresAt && DateTimeOffset.UtcNow > expiresAt)
        {
            consent.Status = GuardianConsentStatus.Expired;
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Guardian consent {ConsentId} was opened after expiry.", consent.Id);
            return false;
        }

        consent.Status = GuardianConsentStatus.Approved;
        consent.ApprovedAt = DateTimeOffset.UtcNow;

        // The token is single use: rotating it means a forwarded or logged link cannot
        // be replayed against the record later.
        consent.VerificationToken = GenerateToken();
        consent.TokenExpiresAt = null;

        audit.Record(
            nameof(GuardianConsent),
            consent.Id.ToString(),
            AuditActions.GuardianConsentApproved,
            newValue: $"Approved by {consent.GuardianName} ({consent.Email}) "
                      + $"under consent version {consent.ConsentVersion}");

        await notifications.NotifyStaffAsync(
            NotificationTypes.GuardianApprovalReceived,
            $"Guardian approval received for {consent.Client.FullName}.",
            "/admin",
            cancellationToken);

        // The approval, its audit entry and the staff notification commit together.
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task SendRequestEmailAsync(
        GuardianConsent consent,
        ClientProfile client,
        CancellationToken cancellationToken = default)
    {
        var brand = brandOptions.Value;
        var approvalUrl = $"{brand.PublicDomain.TrimEnd('/')}/guardian/approve/{consent.VerificationToken}";

        var body =
            $"""
             {consent.GuardianName},

             {client.FullName} has asked to join {brand.BusinessName}. Because they are
             under 18, we need your approval as their legal guardian before their
             portfolio can be completed.

             Please review and approve here:
             {approvalUrl}

             This link expires on {consent.TokenExpiresAt:d MMMM yyyy}.

             If you were not expecting this, please contact {brand.BusinessName}.
             """;

        await emailSender.SendAsync(
            consent.Email,
            $"Approval needed for {client.FullName} at {brand.BusinessName}",
            body,
            cancellationToken);
    }

    /// <summary>
    /// 32 bytes of cryptographic randomness, URL-safe. The token is the only thing
    /// standing between a stranger and a recorded legal consent, so it is not derived
    /// from anything guessable such as the client id or a timestamp.
    /// </summary>
    private static string GenerateToken() =>
        Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
