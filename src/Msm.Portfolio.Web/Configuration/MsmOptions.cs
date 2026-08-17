namespace Msm.Portfolio.Web.Configuration;

/// <summary>
/// Which relational provider the application runs against. The choice is deliberately
/// deferred to deployment (specification section 32), so nothing in the domain or
/// services depends on it.
/// </summary>
public enum DatabaseProvider
{
    /// <summary>Local development default. Requires no server to run the application.</summary>
    Sqlite = 0,
    PostgreSql = 1,
    SqlServer = 2
}

/// <summary>Database selection, bound from the "Database" configuration section.</summary>
public class DatabaseOptions
{
    public const string SectionName = "Database";

    public DatabaseProvider Provider { get; set; } = DatabaseProvider.Sqlite;

    public string ConnectionString { get; set; } = "Data Source=msm-portfolio.db";

    /// <summary>
    /// Applies pending migrations at startup. Convenient in development; production
    /// deployments normally migrate as a separate, deliberate step.
    /// </summary>
    public bool MigrateOnStartup { get; set; } = true;
}

/// <summary>
/// Limits the specification requires to be configurable rather than compiled in
/// (specification sections 12, 13, 14 and 38).
/// </summary>
public class MediaOptions
{
    public const string SectionName = "Media";

    /// <summary>Maximum assets in a client's private pool (specification section 12).</summary>
    public int MediaPoolImageLimit { get; set; } = 60;

    /// <summary>Maximum images shown on the public portfolio (specification section 12).</summary>
    public int PortfolioImageLimit { get; set; } = 30;

    public long MaxImageBytes { get; set; } = 25 * 1024 * 1024;

    public long MaxVideoBytes { get; set; } = 500 * 1024 * 1024;

    public string[] AllowedImageContentTypes { get; set; } =
        ["image/jpeg", "image/png", "image/webp"];

    public string[] AllowedVideoContentTypes { get; set; } =
        ["video/mp4", "video/quicktime"];

    /// <summary>
    /// Storage provider key. Only local disk exists so far; object storage is a
    /// deployment decision still open per specification section 33.
    /// </summary>
    public string StorageProvider { get; set; } = "LocalDisk";

    public string LocalStorageRoot { get; set; } = "media-storage";
}

/// <summary>
/// Commercial values MSM must be able to change without a code change
/// (specification sections 19, 22 and 23).
/// </summary>
public class CommerceOptions
{
    public const string SectionName = "Commerce";

    public string Currency { get; set; } = "GBP";

    /// <summary>Price of the 4 Week Model Development Programme (specification section 19).</summary>
    public decimal ProgrammePrice { get; set; } = 3499.00m;

    /// <summary>Placeholder monthly maintenance price, pending MSM's final figure.</summary>
    public decimal MaintenancePrice { get; set; } = 19.99m;

    /// <summary>
    /// Days a portfolio stays public after a failed maintenance payment
    /// (specification section 23).
    /// </summary>
    public int MaintenanceGracePeriodDays { get; set; } = 7;

    /// <summary>
    /// Days after purchase before maintenance billing begins. MSM has not confirmed
    /// the commercial timing, so it stays configurable (specification section 22).
    /// </summary>
    public int MaintenanceStartsAfterDays { get; set; } = 0;
}

/// <summary>
/// MSM's own contact and branding details. All professional contact from a public
/// portfolio routes here rather than to the model (specification sections 16 and 46).
/// Values are supplied by MSM and are placeholders until then.
/// </summary>
public class MsmBrandOptions
{
    public const string SectionName = "Msm";

    public string BusinessName { get; set; } = "Manchester School of Modelling";

    /// <summary>
    /// Host serving public portfolios, used to build every outbound URL: the address
    /// shared with an agency, the social preview image, the guardian's approval link and
    /// the portfolio URL mirrored onto the CRM contact.
    /// </summary>
    /// <remarks>
    /// No trailing slash. Every use trims one anyway, but the value is also shown to
    /// staff on the client record, where a stray slash looks like a mistake.
    /// </remarks>
    public string PublicDomain { get; set; } = "https://model-portfolio.manchesterschoolofmodelling.co.uk";

    public string? ContactEmail { get; set; }

    public string? ContactPhone { get; set; }

    public string? WhatsApp { get; set; }

    public string? WebsiteUrl { get; set; }

    public string? InstagramUrl { get; set; }

    public string? TikTokUrl { get; set; }

    public string? CompanyInformation { get; set; }

    /// <summary>
    /// Asks search engines to ignore the whole site.
    /// </summary>
    /// <remarks>
    /// Set on a preview or staging deployment. A demonstration site carrying invented
    /// models, sitting on a subdomain of MSM's real brand, must not turn up in a search
    /// for MSM — and once indexed, that is slow and awkward to undo.
    /// </remarks>
    public bool DiscourageSearchEngines { get; set; }
}

/// <summary>
/// Guardian consent settings for under-18 clients (specification section 11).
/// </summary>
public class GuardianConsentOptions
{
    public const string SectionName = "GuardianConsent";

    /// <summary>
    /// Version of the consent wording currently in force. Recorded against each
    /// approval, so revising the wording cannot retrospectively change what a guardian
    /// agreed to. The wording itself is supplied or approved by MSM.
    /// </summary>
    public string CurrentVersion { get; set; } = "v1-draft";

    /// <summary>How long an approval link stays valid.</summary>
    public int TokenLifetimeDays { get; set; } = 14;

    /// <summary>
    /// Consent text shown to the guardian. Placeholder wording until MSM supplies the
    /// approved version.
    /// </summary>
    public string? ConsentText { get; set; }
}

/// <summary>
/// Credentials and endpoints for the external systems. Left empty in source control;
/// supplied through user secrets or environment variables (specification section 43).
/// </summary>
public class IntegrationOptions
{
    public const string SectionName = "Integrations";

    public GoCardlessOptions GoCardless { get; set; } = new();

    public HighLevelOptions HighLevel { get; set; } = new();
}

public class GoCardlessOptions
{
    public string? AccessToken { get; set; }

    /// <summary>"sandbox" until MSM's live account is connected.</summary>
    public string Environment { get; set; } = "sandbox";

    /// <summary>Secret used to verify inbound webhook signatures (specification section 44).</summary>
    public string? WebhookSecret { get; set; }
}

public class HighLevelOptions
{
    public string? ApiKey { get; set; }

    public string? LocationId { get; set; }

    public string BaseUrl { get; set; } = "https://services.leadconnectorhq.com";
}

/// <summary>
/// Keys for values stored in the SystemSetting table, where MSM can edit them at
/// runtime. These mirror the options above; a stored value takes precedence over the
/// configured default once the settings screen is built in a later phase.
/// </summary>
public static class SystemSettingKeys
{
    public const string PortfolioImageLimit = "PortfolioImageLimit";
    public const string MediaPoolImageLimit = "MediaPoolImageLimit";
    public const string MaintenancePrice = "MaintenancePrice";
    public const string MaintenanceGracePeriodDays = "MaintenanceGracePeriodDays";
    public const string MsmContactEmail = "MSMContactEmail";
    public const string MsmPhone = "MSMPhone";
    public const string MsmWhatsApp = "MSMWhatsApp";
    public const string PublicDomain = "PublicDomain";
}
