using System.ComponentModel.DataAnnotations;
using Msm.Portfolio.Web.Configuration;
using Msm.Portfolio.Web.Services;

namespace Msm.Portfolio.Web.ViewModels;

/// <summary>A model's public page (specification sections 15 and 16).</summary>
public class PublicPortfolioViewModel
{
    public PublicPortfolio Portfolio { get; set; } = null!;

    public MsmBrandOptions Brand { get; set; } = new();

    public EnquiryViewModel Enquiry { get; set; } = new();

    /// <summary>Set after a successful enquiry so the page can confirm it.</summary>
    public bool EnquirySent { get; set; }

    public string ShareUrl => $"{Brand.PublicDomain.TrimEnd('/')}/{Portfolio.Slug}";

    /// <summary>
    /// A short line under the model's name. Age is included only when known, so an
    /// incomplete profile does not render "0" next to their name.
    /// </summary>
    public string? Strapline
    {
        get
        {
            var parts = new List<string>();

            if (!string.IsNullOrWhiteSpace(Portfolio.Location))
            {
                parts.Add(Portfolio.Location);
            }

            if (Portfolio.Age is { } age)
            {
                parts.Add($"Age {age}");
            }

            return parts.Count == 0 ? null : string.Join(" · ", parts);
        }
    }
}

/// <summary>
/// The agency enquiry form (specification section 46). It collects the enquirer's
/// details; the model's own contact details are never shown or used.
/// </summary>
public class EnquiryViewModel
{
    /// <summary>
    /// Carried through the form only so the post knows which portfolio it came from.
    /// The server re-checks that this portfolio is published before storing anything.
    /// </summary>
    public Guid ClientId { get; set; }

    [Required(ErrorMessage = "Please enter your name.")]
    [StringLength(200)]
    [Display(Name = "Your name")]
    public string Name { get; set; } = string.Empty;

    [StringLength(200)]
    [Display(Name = "Agency or company")]
    public string? Company { get; set; }

    [Required(ErrorMessage = "Please enter your email address.")]
    [EmailAddress(ErrorMessage = "Please enter a valid email address.")]
    [StringLength(256)]
    [Display(Name = "Your email address")]
    public string Email { get; set; } = string.Empty;

    [Phone(ErrorMessage = "Please enter a valid telephone number.")]
    [StringLength(50)]
    [Display(Name = "Your telephone number (optional)")]
    public string? Phone { get; set; }

    [Required(ErrorMessage = "Please enter a message.")]
    [StringLength(4000)]
    [Display(Name = "Message")]
    public string Message { get; set; } = string.Empty;
}

/// <summary>The Model Board (specification section 18).</summary>
public class ModelBoardViewModel
{
    public List<ModelBoardCard> Cards { get; set; } = [];

    public MsmBrandOptions Brand { get; set; } = new();
}
