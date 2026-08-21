using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Msm.Portfolio.Web.Authorization;
using Msm.Portfolio.Web.Configuration;
using Msm.Portfolio.Web.Domain.Entities;
using Msm.Portfolio.Web.Domain.Enums;

namespace Msm.Portfolio.Web.Data;

/// <summary>
/// Brings a fresh database up to a usable state: the four roles with their default
/// permissions, the owner's Super Admin account, the two sellable products, and the
/// runtime-editable settings.
/// </summary>
/// <remarks>
/// Every step is idempotent, so this runs safely on each startup and on redeploy.
/// </remarks>
public class DbSeeder(
    ApplicationDbContext db,
    RoleManager<ApplicationRole> roleManager,
    UserManager<ApplicationUser> userManager,
    IOptions<CommerceOptions> commerceOptions,
    IOptions<MediaOptions> mediaOptions,
    IOptions<MsmBrandOptions> brandOptions,
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<DbSeeder> logger)
{
    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        await SeedRolesAndPermissionsAsync();
        await SeedSuperAdminAsync();
        await SeedProductsAsync(cancellationToken);
        await SeedSystemSettingsAsync(cancellationToken);
    }

    private async Task SeedRolesAndPermissionsAsync()
    {
        foreach (var roleName in Roles.All)
        {
            var role = await roleManager.FindByNameAsync(roleName);
            if (role is null)
            {
                role = new ApplicationRole(roleName) { Description = DescribeRole(roleName) };
                var created = await roleManager.CreateAsync(role);
                if (!created.Succeeded)
                {
                    throw new InvalidOperationException(
                        $"Could not create role '{roleName}': {Describe(created)}");
                }

                logger.LogInformation("Created role {Role}.", roleName);
            }

            if (!Permissions.DefaultsByRole.TryGetValue(roleName, out var defaults))
            {
                // Super Admin holds no permission claims by design; the authorization
                // handler grants it everything, so the list cannot drift out of date.
                continue;
            }

            var existing = await roleManager.GetClaimsAsync(role);
            foreach (var permission in defaults)
            {
                var alreadyGranted = existing.Any(c =>
                    c.Type == Permissions.ClaimType && c.Value == permission);

                if (alreadyGranted)
                {
                    continue;
                }

                var result = await roleManager.AddClaimAsync(
                    role, new Claim(Permissions.ClaimType, permission));

                if (!result.Succeeded)
                {
                    throw new InvalidOperationException(
                        $"Could not grant '{permission}' to '{roleName}': {Describe(result)}");
                }
            }
        }
    }

    private async Task SeedSuperAdminAsync()
    {
        var email = configuration["Seed:SuperAdmin:Email"];
        var password = configuration["Seed:SuperAdmin:Password"];

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            if (!environment.IsDevelopment())
            {
                // Never invent a credential outside development: an account with a
                // guessable password and unrestricted rights is worse than no account.
                logger.LogWarning(
                    "No Super Admin seed credentials configured. Set Seed:SuperAdmin:Email and "
                    + "Seed:SuperAdmin:Password (user secrets or environment variables) to create the owner account.");
                return;
            }

            email = "superadmin@msm.local";
            password = "Dev!Passw0rd";
            logger.LogWarning(
                "Seeding the development Super Admin {Email} with a well-known password. "
                + "This happens only in the Development environment.", email);
        }

        var user = await userManager.FindByEmailAsync(email);
        if (user is not null)
        {
            // Do not reset an existing password: the owner may well have changed it.
            if (!await userManager.IsInRoleAsync(user, Roles.SuperAdmin))
            {
                await userManager.AddToRoleAsync(user, Roles.SuperAdmin);
            }

            return;
        }

        user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            FirstName = "MSM",
            LastName = "Owner",
            IsActive = true
        };

        var created = await userManager.CreateAsync(user, password);
        if (!created.Succeeded)
        {
            throw new InvalidOperationException(
                $"Could not create the Super Admin account: {Describe(created)}");
        }

        await userManager.AddToRoleAsync(user, Roles.SuperAdmin);
        logger.LogInformation("Created Super Admin account {Email}.", email);
    }

    private async Task SeedProductsAsync(CancellationToken cancellationToken)
    {
        var commerce = commerceOptions.Value;

        // Prices are only ever seeded, never corrected on an existing row: MSM may have
        // changed a price through the application, and orders reference the agreed
        // amount rather than this record.
        await EnsureProductAsync(
            ProductCodes.DigitalPortfolioYear,
            "Digital Portfolio",
            $"The public digital portfolio, live for {commerce.PortfolioTermDays / 365} year.",
            commerce.PortfolioPrice,
            commerce.Currency,
            BillingType.OneOff,
            BillingInterval.None,
            cancellationToken);

        // A new product rather than a corrected old one. Orders reference the product they
        // were bought against, and rewriting the row would restate every past sale of the
        // £3,499 programme as a sale of something else. Retiring it instead leaves that
        // history intact and stops anything new being sold against it.
        var retired = await db.Products
            .FirstOrDefaultAsync(p => p.Code == ProductCodes.ModelDevelopmentProgramme
                                      && p.IsActive, cancellationToken);

        if (retired is not null)
        {
            retired.IsActive = false;
            logger.LogInformation(
                "Retired product {Code}. The website now sells {Replacement}.",
                ProductCodes.ModelDevelopmentProgramme, ProductCodes.DigitalPortfolioYear);
        }

        await EnsureProductAsync(
            ProductCodes.PortfolioMaintenance,
            "Portfolio Maintenance",
            "Ongoing hosting and maintenance of the public digital portfolio.",
            commerce.MaintenancePrice,
            commerce.Currency,
            BillingType.Recurring,
            BillingInterval.Monthly,
            cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task EnsureProductAsync(
        string code,
        string name,
        string description,
        decimal price,
        string currency,
        BillingType billingType,
        BillingInterval billingInterval,
        CancellationToken cancellationToken)
    {
        if (await db.Products.AnyAsync(p => p.Code == code, cancellationToken))
        {
            return;
        }

        db.Products.Add(new Product
        {
            Code = code,
            Name = name,
            Description = description,
            Price = price,
            Currency = currency,
            BillingType = billingType,
            BillingInterval = billingInterval,
            IsActive = true
        });

        logger.LogInformation("Seeded product {Code} at {Price} {Currency}.", code, price, currency);
    }

    private async Task SeedSystemSettingsAsync(CancellationToken cancellationToken)
    {
        var media = mediaOptions.Value;
        var commerce = commerceOptions.Value;
        var brand = brandOptions.Value;

        var defaults = new (string Key, string? Value, string Description)[]
        {
            (SystemSettingKeys.PortfolioImageLimit, media.PortfolioImageLimit.ToString(),
                "Maximum images shown on a public portfolio."),
            (SystemSettingKeys.MediaPoolImageLimit, media.MediaPoolImageLimit.ToString(),
                "Maximum images held in a client's private media pool."),
            (SystemSettingKeys.MaintenancePrice, commerce.MaintenancePrice.ToString("0.00"),
                "Monthly portfolio maintenance price. Existing subscriptions keep their original price."),
            (SystemSettingKeys.MaintenanceGracePeriodDays, commerce.MaintenanceGracePeriodDays.ToString(),
                "Days a portfolio stays public after a failed maintenance payment."),
            (SystemSettingKeys.MsmContactEmail, brand.ContactEmail,
                "Public contact email for agency enquiries. Supplied by MSM."),
            (SystemSettingKeys.MsmPhone, brand.ContactPhone,
                "Public contact telephone number. Supplied by MSM."),
            (SystemSettingKeys.MsmWhatsApp, brand.WhatsApp,
                "Public WhatsApp contact. Supplied by MSM."),
            (SystemSettingKeys.PublicDomain, brand.PublicDomain,
                "Host serving public portfolios, used to build shareable URLs.")
        };

        var existingKeys = await db.SystemSettings
            .Select(s => s.Key)
            .ToListAsync(cancellationToken);

        foreach (var (key, value, description) in defaults)
        {
            // Only insert what is missing. An existing row is MSM's own edit and must
            // not be overwritten by a configured default on the next restart.
            if (existingKeys.Contains(key))
            {
                continue;
            }

            db.SystemSettings.Add(new SystemSetting
            {
                Key = key,
                Value = value,
                Description = description
            });
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static string DescribeRole(string roleName) => roleName switch
    {
        Roles.SuperAdmin => "System owner. Unrestricted access including permanent deletion.",
        Roles.Admin => "MSM studio and management staff.",
        Roles.Retoucher => "Prepares portfolios from photoshoot imagery.",
        Roles.Client => "The model. Owns their profile and public portfolio.",
        _ => roleName
    };

    private static string Describe(IdentityResult result) =>
        string.Join("; ", result.Errors.Select(e => $"{e.Code}: {e.Description}"));
}

/// <summary>Stable product keys used by seeding and checkout.</summary>
public static class ProductCodes
{
    /// <summary>What the website sells: the portfolio, for a year.</summary>
    public const string DigitalPortfolioYear = "digital-portfolio-year";

    /// <summary>
    /// The £3,499 programme this used to sell. Kept because orders reference it, and
    /// deactivated on start-up so nothing new can be bought against it.
    /// </summary>
    public const string ModelDevelopmentProgramme = "model-development-programme";

    public const string PortfolioMaintenance = "portfolio-maintenance";
}
