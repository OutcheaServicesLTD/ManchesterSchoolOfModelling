namespace Msm.Portfolio.Web.Domain.Entities;

/// <summary>
/// An enquiry about a model, submitted from their public portfolio
/// (specification section 46).
/// </summary>
/// <remarks>
/// <para>
/// <b>Dormant.</b> Nothing writes to this any more. MSM keeps no copy of an enquiry: it
/// goes to the model, who deals with it. The type and its table are left in place rather
/// than dropped, so the enquiries taken before that decision are not destroyed by it and
/// the choice can be reversed.
/// </para>
/// <para>
/// Because nothing is stored, delivery is no longer optional. A message that cannot be
/// sent is reported to the agency rather than logged — see
/// <see cref="Services.EnquiryOutcome"/>.
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
