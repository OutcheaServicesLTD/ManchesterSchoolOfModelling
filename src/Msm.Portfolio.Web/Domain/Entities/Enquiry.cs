namespace Msm.Portfolio.Web.Domain.Entities;

/// <summary>
/// An enquiry about a model, submitted from their public portfolio
/// (specification section 46).
/// </summary>
/// <remarks>
/// <para>
/// Not one of the entities listed in specification section 26, but the contact form it
/// serves is required by section 46. Enquiries are stored rather than only emailed
/// because no email provider is configured yet: without a record, a genuine agency
/// enquiry would simply be lost.
/// </para>
/// <para>
/// The enquiry belongs to MSM, not the model. The model's own email and telephone are
/// never disclosed to the enquirer, and the reply address here is the agency's.
/// </para>
/// </remarks>
public class Enquiry
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>
    /// The model enquired about, taken from the portfolio being viewed rather than
    /// from the form, so it cannot be forged.
    /// </summary>
    public Guid ClientId { get; set; }

    public ClientProfile Client { get; set; } = null!;

    public string Name { get; set; } = string.Empty;

    public string? Company { get; set; }

    /// <summary>The enquirer's own email, used by MSM to reply.</summary>
    public string Email { get; set; } = string.Empty;

    public string? Phone { get; set; }

    public string Message { get; set; } = string.Empty;

    public bool IsHandled { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
