using Msm.Portfolio.Web.Domain.Enums;

namespace Msm.Portfolio.Web.Domain.Entities;

/// <summary>
/// Links a client to the retoucher preparing their portfolio (specification sections 6 and 26).
/// Assignments exist so work is attributable to a named staff member rather than
/// becoming mixed between them.
/// </summary>
public class RetoucherAssignment
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ClientId { get; set; }

    public ClientProfile Client { get; set; } = null!;

    public Guid RetoucherUserId { get; set; }

    public ApplicationUser RetoucherUser { get; set; } = null!;

    public RetoucherAssignmentStatus Status { get; set; } = RetoucherAssignmentStatus.Waiting;

    public DateTimeOffset AssignedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>Set when the retoucher sends the portfolio for Admin review.</summary>
    public DateTimeOffset? SubmittedForReviewAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }
}
