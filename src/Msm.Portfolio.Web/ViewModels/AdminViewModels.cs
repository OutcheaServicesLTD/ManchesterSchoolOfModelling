using System.ComponentModel.DataAnnotations;
using Msm.Portfolio.Web.Configuration;
using Msm.Portfolio.Web.Domain.Entities;
using Msm.Portfolio.Web.Domain.Enums;
using Msm.Portfolio.Web.Services;

namespace Msm.Portfolio.Web.ViewModels;

/// <summary>The admin client table and its filters (specification section 5).</summary>
public class AdminDashboardViewModel
{
    public string? Search { get; set; }

    public PortfolioStatus? Status { get; set; }

    public Guid? RetoucherUserId { get; set; }

    public List<AdminClientRow> Rows { get; set; } = [];

    public List<StaffOption> Retouchers { get; set; } = [];

    public bool HasFilters =>
        !string.IsNullOrWhiteSpace(Search) || Status is not null || RetoucherUserId is not null;
}

/// <summary>One client's full record (specification section 5).</summary>
public class AdminClientDetailViewModel
{
    /// <summary>
    /// Whether this model already has a password. Only changes the wording — "create" the
    /// first time, "reset" afterwards — so nobody presses it thinking it is harmless and
    /// locks a model out of an account they were already using.
    /// </summary>
    public bool HasSignInDetails { get; set; }

    public Guid ClientId { get; set; }

    public ClientProfile Client { get; set; } = null!;

    public Domain.Entities.Portfolio? Portfolio { get; set; }

    public List<MediaAssetViewModel> Assets { get; set; } = [];

    public Guid? SelfTapeId { get; set; }

    public string? RetoucherName { get; set; }

    public IReadOnlyList<MeasurementFieldDefinition> MeasurementTemplate { get; set; } = [];

    public int PortfolioLimit { get; set; }

    public int PoolLimit { get; set; }

    public bool GuardianApprovalPending { get; set; }

    /// <summary>Shown to staff, never on the public portfolio (specification section 23).</summary>
    public Services.MaintenanceWarning? MaintenanceWarning { get; set; }

    /// <summary>Why publishing is not currently possible, or null when it is.</summary>
    public string? PublishBlocker { get; set; }

    /// <summary>
    /// False when no biography provider is configured.
    /// </summary>
    /// <remarks>
    /// Shown rather than assumed, so the page can say the feature needs setting up
    /// instead of offering a button that can only ever answer with an error. A control
    /// that does nothing when pressed reads as broken software, not as a missing setting.
    /// </remarks>
    public bool BiographyFeatureIsOn { get; set; }

    public string PublicUrlBase { get; set; } = string.Empty;

    public List<MediaAssetViewModel> Selected => [.. Assets.Where(a => a.IsSelected)];

    public List<MediaAssetViewModel> Unselected => [.. Assets.Where(a => !a.IsSelected)];

    public PortfolioStatus Status => Portfolio?.Status ?? PortfolioStatus.AwaitingClientInformation;

    public bool IsPublished => Portfolio?.IsPublished ?? false;

    public bool CanPublish => PublishBlocker is null && !IsPublished;

    public string? PublicUrl =>
        Portfolio?.Slug is { } slug ? $"{PublicUrlBase}/{slug}" : null;

    public bool CanMarkInViewing =>
        Status is PortfolioStatus.ReadyForReview or PortfolioStatus.Retouching
        && Portfolio?.FeaturedMediaId is not null;

    public bool CanMarkNoSale =>
        !IsPublished && Status is PortfolioStatus.InViewing or PortfolioStatus.AwaitingPurchase;

    /// <summary>
    /// Checkout opens once the client has seen their portfolio. Guardian approval is
    /// re-checked when the checkout is actually opened, so a blocked minor is refused
    /// there rather than only being hidden here.
    /// </summary>
    public bool CanStartCheckout =>
        !IsPublished
        && !HasPaid
        && Status is PortfolioStatus.InViewing or PortfolioStatus.AwaitingPurchase;

    public bool HasPaid { get; set; }

    /// <summary>What a purchase costs today: the portfolio, for a year.</summary>
    public decimal PortfolioPriceValue { get; set; }

    public string PortfolioPrice => $"£{PortfolioPriceValue:N2}";

    public bool IsArchived => Status == PortfolioStatus.Archived;

    public AdminClientEditViewModel ToEditModel() => new()
    {
        FirstName = Client.FirstName,
        LastName = Client.LastName,
        DisplayName = Client.DisplayName,
        DateOfBirth = Client.DateOfBirth,
        Location = Client.Location,
        ModelProfileType = Client.ModelProfileType,
        HairColour = Client.HairColour,
        EyeColour = Client.EyeColour,
        Biography = Client.Biography
    };
}

/// <summary>Admin correcting a client's details (specification section 5).</summary>
public class AdminClientEditViewModel
{
    [Required]
    [StringLength(100)]
    [Display(Name = "First name")]
    public string FirstName { get; set; } = string.Empty;

    [Required]
    [StringLength(100)]
    [Display(Name = "Last name")]
    public string LastName { get; set; } = string.Empty;

    [StringLength(150)]
    [Display(Name = "Model name")]
    public string? DisplayName { get; set; }

    [DataType(DataType.Date)]
    [Display(Name = "Date of birth")]
    public DateOnly? DateOfBirth { get; set; }

    [StringLength(200)]
    [Display(Name = "Location")]
    public string? Location { get; set; }

    [Display(Name = "Profile type")]
    public ModelProfileType ModelProfileType { get; set; }

    [StringLength(50)]
    [Display(Name = "Hair colour")]
    public string? HairColour { get; set; }

    [StringLength(50)]
    [Display(Name = "Eye colour")]
    public string? EyeColour { get; set; }

    [StringLength(4000)]
    [Display(Name = "About me")]
    public string? Biography { get; set; }
}
