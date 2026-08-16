using System.ComponentModel.DataAnnotations;

namespace Msm.Portfolio.Web.ViewModels;

/// <summary>What the guardian sees and confirms (specification section 11).</summary>
public class GuardianApprovalViewModel
{
    public string Token { get; set; } = string.Empty;

    public string GuardianName { get; set; } = string.Empty;

    public string ClientName { get; set; } = string.Empty;

    public string Relationship { get; set; } = string.Empty;

    public string? ConsentVersion { get; set; }

    /// <summary>
    /// Consent wording in force. Supplied by MSM; a placeholder is shown until then so
    /// the page is never blank.
    /// </summary>
    public string? ConsentText { get; set; }

    public string BusinessName { get; set; } = string.Empty;

    public DateTimeOffset? ApprovedAt { get; set; }

    [Display(Name = "I confirm I am the legal guardian named above and I give my consent")]
    public bool Agreed { get; set; }
}
