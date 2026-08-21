namespace Msm.Portfolio.Web.Models;

/// <summary>
/// What the error page shows. One page covers a wrong address, a refusal and a fault,
/// because a visitor needs the same three things from all of them: what happened, that it
/// is not their fault, and a way onwards.
/// </summary>
public class ErrorViewModel
{
    /// <summary>The HTTP status, when the page is standing in for one.</summary>
    public int? StatusCode { get; set; }

    public string? RequestId { get; set; }

    /// <summary>
    /// Shown only for a fault. A visitor who typed an address wrongly has no use for a
    /// trace identifier, and printing one makes an ordinary mistake look like a crash.
    /// </summary>
    public bool ShowRequestId => !string.IsNullOrEmpty(RequestId) && StatusCode is null or >= 500;

    public string Heading => StatusCode switch
    {
        404 => "We cannot find that page",
        403 => "That page is not yours to see",
        401 => "Please sign in first",
        >= 500 => "Something went wrong at our end",
        _ => "Something went wrong"
    };

    /// <summary>
    /// Plain, and never blaming the visitor: a portfolio address that has stopped working
    /// is usually a portfolio that came down, not a typing mistake.
    /// </summary>
    public string Explanation => StatusCode switch
    {
        404 => "The address may have been typed differently, or the portfolio it pointed to "
               + "may no longer be online.",
        403 => "You are signed in, but this page belongs to a different part of the studio.",
        401 => "You need to be signed in to see this page.",
        >= 500 => "The fault is ours, not yours. Nothing you did caused it, and nothing you "
                  + "had entered has been lost.",
        _ => "The fault is ours, not yours."
    };
}
