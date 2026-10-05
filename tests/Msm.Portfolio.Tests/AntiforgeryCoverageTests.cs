using System.Text.RegularExpressions;

namespace Msm.Portfolio.Tests;

/// <summary>
/// Every POST on the site is checked for an antiforgery token, and the form tag helper
/// adds one automatically — but only to forms whose action it generated itself.
/// </summary>
/// <remarks>
/// Write the address out by hand, as <c>action="/account/logout"</c>, and the helper
/// leaves the token off without complaint. The form then answers 400 and does nothing,
/// which is how signing out was broken and how the focal point panel was broken before
/// it. Twice is a pattern, so it is a test rather than a thing to remember.
/// </remarks>
public class AntiforgeryCoverageTests
{
    /// <summary>
    /// A form element opening tag, with a literal action attribute rather than one built
    /// from asp-action or asp-controller.
    /// </summary>
    private static readonly Regex LiteralActionForm = new(
        """<form\b(?![^>]*\basp-(action|controller|page|route)\b)[^>]*\baction\s*=\s*"[^"]*"[^>]*>""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);

    [Fact]
    public void Every_hand_written_form_action_asks_for_an_antiforgery_token()
    {
        var offenders = new List<string>();

        foreach (var view in Views())
        {
            var markup = File.ReadAllText(view);

            foreach (Match match in LiteralActionForm.Matches(markup))
            {
                // GET forms are not checked and need no token.
                if (!match.Value.Contains("method", StringComparison.OrdinalIgnoreCase)
                    || match.Value.Contains("method=\"get\"", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!match.Value.Contains("asp-antiforgery", StringComparison.OrdinalIgnoreCase))
                {
                    offenders.Add($"{Path.GetFileName(view)}: {Collapse(match.Value)}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "These forms post to an address written out by hand, so the tag helper will not "
            + "add an antiforgery token and the submission will be refused with 400. Add "
            + "asp-antiforgery=\"true\":\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>The views as they are on disk, found from the test assembly's location.</summary>
    private static IEnumerable<string> Views()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MsmPortfolio.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        var web = Path.Combine(directory!.FullName, "src", "Msm.Portfolio.Web");
        Assert.True(Directory.Exists(web), $"Expected the web project at {web}.");

        var views = Directory.EnumerateFiles(web, "*.cshtml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .ToList();

        // A regex that matches nothing because it was pointed at nothing would pass this
        // test silently.
        Assert.NotEmpty(views);

        return views;
    }

    private static string Collapse(string value) =>
        Regex.Replace(value, @"\s+", " ").Trim();
}
