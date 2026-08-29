using Msm.Portfolio.Web.Integrations.GoCardless;
using Msm.Portfolio.Web.Integrations.HighLevel;
using Msm.Portfolio.Web.Integrations.Bio;

namespace Msm.Portfolio.Web.Services;

/// <summary>One thing that is not ready for production.</summary>
public record ReadinessProblem(string Area, string Detail, bool IsFatal);

/// <summary>
/// Checks, at startup, that the application is not about to run in production with
/// development stand-ins still in place.
/// </summary>
/// <remarks>
/// <para>
/// Several parts of this application deliberately ship with stubs: payments take no
/// money, the CRM logs instead of sending, guardian emails are only written to the log,
/// and media is stored on local disk. Each is the right default while the corresponding
/// account or decision is outstanding.
/// </para>
/// <para>
/// Deployed unnoticed, though, they fail quietly and expensively — portfolios published
/// without payment, guardian approvals that never arrive, media lost on the next
/// container rebuild. This makes that impossible to do by accident: the fatal ones stop
/// the application from starting, and the rest are logged as prominent warnings.
/// </para>
/// </remarks>
public static class ProductionReadiness
{
    public static IReadOnlyList<ReadinessProblem> Check(
        IConfiguration configuration,
        IGoCardlessService payments,
        IHighLevelService crm,
        bool emailSenderIsStub,
        string mediaStorageProvider,
        bool migrateOnStartup)
    {
        var problems = new List<ReadinessProblem>();

        // Fatal: a stub that authorises payments would publish portfolios nobody has
        // paid for. The stub refuses outside development, so this would fail every sale
        // rather than silently succeed, but it must not reach production either way.
        if (!payments.IsLive)
        {
            problems.Add(new ReadinessProblem(
                "Payments",
                "No GoCardless access token is configured, so no payment can be taken. "
                + "Set Integrations:GoCardless:AccessToken once the provider has been verified "
                + "against docs/gocardless-verification.md.",
                IsFatal: true));
        }

        // Fatal: without it, a forged webhook could mark an order paid and publish a
        // portfolio. The verifier already refuses everything, so payments would simply
        // never confirm.
        if (string.IsNullOrWhiteSpace(configuration["Integrations:GoCardless:WebhookSecret"]))
        {
            problems.Add(new ReadinessProblem(
                "Payments",
                "No GoCardless webhook secret is configured, so no webhook can be trusted and "
                + "payments will never confirm. Set Integrations:GoCardless:WebhookSecret.",
                IsFatal: true));
        }

        // Fatal: a guardian who never receives their approval request means an under-18
        // client can never complete, and MSM would not know why.
        if (emailSenderIsStub)
        {
            problems.Add(new ReadinessProblem(
                "Email",
                "No email provider is configured, so guardian approval requests are only written "
                + "to the log and never delivered. Register a real IEmailSender or route guardian "
                + "messaging through GoHighLevel.",
                IsFatal: true));
        }

        // Fatal: local disk does not survive a container rebuild, and on more than one
        // server each instance would hold a different subset of a client's photographs.
        if (mediaStorageProvider.Equals("LocalDisk", StringComparison.OrdinalIgnoreCase))
        {
            problems.Add(new ReadinessProblem(
                "Media storage",
                "Media is stored on local disk, which does not survive a container rebuild and "
                + "cannot be shared between servers. Configure object storage before go-live.",
                IsFatal: true));
        }

        // Fatal: this value is baked into every outbound link. Left at a local address
        // it would send agencies to a page that does not exist, and — far worse — send
        // a guardian an approval link they cannot open, so an under-18 client could
        // never be approved and nobody would be told why. It fails silently in the sense
        // that matters: the application appears to work, and only the recipients of its
        // links ever discover otherwise.
        var domain = configuration["Msm:PublicDomain"];

        if (string.IsNullOrWhiteSpace(domain) || IsLocalAddress(domain))
        {
            problems.Add(new ReadinessProblem(
                "Public domain",
                $"Msm:PublicDomain is '{domain}', which is not a public address. Every shared "
                + "portfolio link, social preview and guardian approval link is built from it. "
                + "Set it to the live domain, with no trailing slash.",
                IsFatal: true));
        }
        else if (domain.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            // Not fatal — the links work — but they would hand an agency an insecure URL
            // and the site redirects to HTTPS anyway, so the value is simply wrong.
            problems.Add(new ReadinessProblem(
                "Public domain",
                $"Msm:PublicDomain is '{domain}'. Shared links should be https.",
                IsFatal: false));
        }

        // The portfolio-maintenance subscription (specification version 2, item 3) is
        // optional — nothing breaks with it left off, the client portal simply does not
        // offer it. Fatal only when it is half configured: a secret key with no webhook
        // secret would let a client pay Stripe and never be recorded as subscribed,
        // because nothing could then be trusted to confirm it.
        var stripeSecretKey = configuration["Integrations:Stripe:SecretKey"];
        var stripeWebhookSecret = configuration["Integrations:Stripe:WebhookSecret"];
        var stripePriceId = configuration["Integrations:Stripe:PriceId"];
        var stripeConfigured = !string.IsNullOrWhiteSpace(stripeSecretKey);

        if (stripeConfigured && string.IsNullOrWhiteSpace(stripeWebhookSecret))
        {
            problems.Add(new ReadinessProblem(
                "Subscriptions",
                "Integrations:Stripe:SecretKey is set but Integrations:Stripe:WebhookSecret is not, "
                + "so a subscription payment could never be confirmed. Set both, or neither.",
                IsFatal: true));
        }
        else if (stripeConfigured && string.IsNullOrWhiteSpace(stripePriceId))
        {
            problems.Add(new ReadinessProblem(
                "Subscriptions",
                "Integrations:Stripe:SecretKey is set but Integrations:Stripe:PriceId is not, so no "
                + "client could actually be sent to a subscription checkout. Set "
                + "Integrations:Stripe:PriceId to the recurring Price created for Portfolio "
                + "Maintenance in the Stripe Dashboard.",
                IsFatal: true));
        }
        else if (!stripeConfigured)
        {
            problems.Add(new ReadinessProblem(
                "Subscriptions",
                "No Stripe secret key is configured, so the client portal will not offer a "
                + "subscription. Set Integrations:Stripe:SecretKey, WebhookSecret and PriceId to "
                + "turn it on.",
                IsFatal: false));
        }

        // Not fatal: the application works, MSM's CRM simply falls behind, and the
        // integrations page shows it.
        if (!crm.IsLive)
        {
            problems.Add(new ReadinessProblem(
                "CRM",
                "No GoHighLevel API key is configured, so portfolio changes are recorded but never "
                + "sent. Set Integrations:HighLevel:ApiKey once verified against "
                + "docs/gohighlevel-verification.md.",
                IsFatal: false));
        }

        // Not fatal, but migrating from inside the web process races when more than one
        // instance starts at once.
        if (migrateOnStartup)
        {
            problems.Add(new ReadinessProblem(
                "Database",
                "Migrations run at startup. With more than one instance this races. Set "
                + "Database:MigrateOnStartup to false and migrate as a deployment step.",
                IsFatal: false));
        }

        // Not fatal: the feature is optional, and a studio that writes its own biographies
        // wants exactly this. Said out loud so nobody is left wondering why approvals
        // never suggest one.
        if (string.IsNullOrWhiteSpace(configuration[$"{BiographyOptions.SectionName}:ApiKey"]))
        {
            problems.Add(new ReadinessProblem(
                "Biographies",
                "No biography provider is configured, so approving a portfolio will not suggest "
                + "a biography and staff write every one by hand. Set Biography:ApiKey to turn "
                + "it on.",
                IsFatal: false));
        }

        if (string.IsNullOrWhiteSpace(configuration["Msm:ContactEmail"]))
        {
            problems.Add(new ReadinessProblem(
                "Branding",
                "No MSM contact email is configured, so public portfolios show the enquiry form "
                + "but no direct contact details. Set Msm:ContactEmail, ContactPhone and WhatsApp.",
                IsFatal: false));
        }

        return problems;
    }

    /// <summary>
    /// Whether a configured public domain is really only reachable from the machine
    /// running the application.
    /// </summary>
    private static bool IsLocalAddress(string domain)
    {
        if (!Uri.TryCreate(domain, UriKind.Absolute, out var uri))
        {
            // Not a URL at all, so it cannot be a working public address either.
            return true;
        }

        var host = uri.Host;

        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.Equals("127.0.0.1", StringComparison.Ordinal)
            || host.Equals("::1", StringComparison.Ordinal)
            || host.Equals("0.0.0.0", StringComparison.Ordinal)
            // A hostname with no dot cannot be resolved from outside this network.
            || !host.Contains('.');
    }

    /// <summary>
    /// Reports the problems, and stops the application if any are fatal.
    /// </summary>
    /// <remarks>
    /// Only enforced outside Development. Locally every stub is the point.
    /// </remarks>
    public static void Enforce(
        IReadOnlyList<ReadinessProblem> problems, IHostEnvironment environment, ILogger logger)
    {
        if (environment.IsDevelopment() || problems.Count == 0)
        {
            return;
        }

        foreach (var problem in problems.Where(p => !p.IsFatal))
        {
            logger.LogWarning("Not production ready — {Area}: {Detail}", problem.Area, problem.Detail);
        }

        var fatal = problems.Where(p => p.IsFatal).ToList();

        if (fatal.Count == 0)
        {
            return;
        }

        foreach (var problem in fatal)
        {
            logger.LogCritical("BLOCKING — {Area}: {Detail}", problem.Area, problem.Detail);
        }

        // Refusing to start is deliberately drastic. A half-configured deployment of
        // this application takes money it cannot process, or publishes a minor's
        // portfolio without their guardian ever being asked; failing loudly at startup
        // is far cheaper than discovering either later.
        throw new InvalidOperationException(
            $"The application is not ready for the {environment.EnvironmentName} environment: "
            + string.Join(" | ", fatal.Select(p => $"{p.Area}: {p.Detail}"))
            + " Set ALLOW_INCOMPLETE_DEPLOYMENT=true to override, which should only be done knowingly.");
    }
}
