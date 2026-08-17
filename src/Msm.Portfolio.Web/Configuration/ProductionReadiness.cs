using Msm.Portfolio.Web.Integrations.GoCardless;
using Msm.Portfolio.Web.Integrations.HighLevel;

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
