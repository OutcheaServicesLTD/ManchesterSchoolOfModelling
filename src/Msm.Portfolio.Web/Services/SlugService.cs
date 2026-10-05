using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Msm.Portfolio.Web.Data;

namespace Msm.Portfolio.Web.Services;

public interface ISlugService
{
    /// <summary>
    /// Produces a slug that is unique across portfolios, appending a number if the
    /// preferred form is taken (specification section 39).
    /// </summary>
    Task<string> GenerateUniqueAsync(
        string name, Guid portfolioId, CancellationToken cancellationToken = default);

    Task<bool> IsAvailableAsync(
        string slug, Guid portfolioId, CancellationToken cancellationToken = default);
}

public class SlugService(ApplicationDbContext db) : ISlugService
{
    /// <summary>
    /// Slugs that would collide with the application's own routes. Public portfolios
    /// are served from the site root as /{slug}, so a model named "Admin" would
    /// otherwise shadow the admin area (specification section 34).
    /// </summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "admin", "client", "retoucher", "account", "accounts", "onboarding", "guardian",
        "media", "models", "checkout", "webhooks", "home", "api", "css", "js", "lib",
        "images", "img", "favicon", "robots", "sitemap", "privacy", "terms", "contact",
        "login", "logout", "register", "signup", "signin", "health", "error"
    };

    public async Task<string> GenerateUniqueAsync(
        string name, Guid portfolioId, CancellationToken cancellationToken = default)
    {
        var baseSlug = Slugify(name);

        if (baseSlug.Length == 0)
        {
            // Names made entirely of characters that do not transliterate would produce
            // an empty slug, which cannot be a URL.
            baseSlug = "model";
        }

        if (Reserved.Contains(baseSlug))
        {
            baseSlug = $"{baseSlug}-model";
        }

        var candidate = baseSlug;
        var suffix = 1;

        while (!await IsAvailableAsync(candidate, portfolioId, cancellationToken))
        {
            suffix++;
            candidate = $"{baseSlug}-{suffix}";
        }

        return candidate;
    }

    public async Task<bool> IsAvailableAsync(
        string slug, Guid portfolioId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(slug) || Reserved.Contains(slug))
        {
            return false;
        }

        return !await db.Portfolios
            .AnyAsync(p => p.Slug == slug && p.Id != portfolioId, cancellationToken);
    }

    /// <summary>
    /// Converts a display name to a URL segment: "Emma Johnson" becomes
    /// "emma-johnson" (specification section 39).
    /// </summary>
    /// <remarks>
    /// Accented characters are transliterated rather than dropped, so "Zoë Müller"
    /// becomes "zoe-muller" instead of "z-m-ller". Model names frequently carry
    /// diacritics and the result has to stay recognisable as the person's name.
    /// </remarks>
    public static string Slugify(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        // FormD splits an accented letter into its base letter plus a combining mark,
        // so dropping the marks leaves the base letter behind.
        var normalised = input.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalised.Length);
        var lastWasHyphen = false;

        foreach (var character in normalised)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(character) && character < 128)
            {
                builder.Append(character);
                lastWasHyphen = false;
            }
            else if (!lastWasHyphen && builder.Length > 0)
            {
                builder.Append('-');
                lastWasHyphen = true;
            }
        }

        return builder.ToString().Trim('-');
    }
}
