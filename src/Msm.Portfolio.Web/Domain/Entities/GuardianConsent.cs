using Msm.Portfolio.Web.Domain.Enums;

namespace Msm.Portfolio.Web.Domain.Entities;

/// <summary>
/// Legal guardian approval for a client under 18 (specification sections 11 and 26).
/// A minor cannot reach purchase or publication until <see cref="Status"/> is
/// <see cref="GuardianConsentStatus.Approved"/>.
/// </summary>
public class GuardianConsent
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ClientId { get; set; }

    public ClientProfile Client { get; set; } = null!;

    public string GuardianName { get; set; } = string.Empty;

    /// <summary>Guardian's stated relationship to the client, for example Parent.</summary>
    public string Relationship { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public string? Phone { get; set; }

    public GuardianConsentStatus Status { get; set; } = GuardianConsentStatus.Pending;

    /// <summary>
    /// Version of the consent wording the guardian agreed to. Recorded so a later
    /// change to MSM's wording cannot retrospectively alter what was consented to.
    /// </summary>
    public string? ConsentVersion { get; set; }

    /// <summary>Single-use token backing the /guardian/approve/{token} approval link.</summary>
    public string VerificationToken { get; set; } = string.Empty;

    public DateTimeOffset? TokenExpiresAt { get; set; }

    public DateTimeOffset? ApprovedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public bool IsApproved => Status == GuardianConsentStatus.Approved;
}
