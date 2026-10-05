namespace Msm.Portfolio.Web.Domain.Enums;

/// <summary>
/// Client account state, per specification section 28. Kept separate from
/// <see cref="PortfolioStatus"/> so account and portfolio concerns never share a field.
/// </summary>
public enum ClientAccountStatus
{
    Invited = 0,
    Active = 1,
    Suspended = 2,
    Archived = 3
}
