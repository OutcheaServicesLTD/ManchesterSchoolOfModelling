using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Msm.Portfolio.Web.Domain.Entities;
using Msm.Portfolio.Web.Integrations.GoCardless;
using Msm.Portfolio.Web.Integrations.HighLevel;
using Msm.Portfolio.Web.Services;

namespace Msm.Portfolio.Tests;

/// <summary>
/// The readiness guard is the last thing standing between a half-configured deployment
/// and a live studio, so what it treats as fatal is asserted rather than assumed.
/// </summary>
public class ProductionReadinessTests
{
    private static IConfiguration Configuration(
        string? webhookSecret = "a-secret", string? contactEmail = "hello@example.com")
    {
        var values = new Dictionary<string, string?>
        {
            ["Integrations:GoCardless:WebhookSecret"] = webhookSecret,
            ["Msm:ContactEmail"] = contactEmail
        };

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static IReadOnlyList<ReadinessProblem> Check(
        bool paymentsLive = true,
        bool crmLive = true,
        bool emailSenderIsStub = false,
        string mediaStorageProvider = "ObjectStorage",
        bool migrateOnStartup = false,
        string? webhookSecret = "a-secret",
        string? contactEmail = "hello@example.com") =>
        ProductionReadiness.Check(
            Configuration(webhookSecret, contactEmail),
            new FakePayments(paymentsLive),
            new FakeCrm(crmLive),
            emailSenderIsStub,
            mediaStorageProvider,
            migrateOnStartup);

    [Fact]
    public void A_fully_configured_deployment_reports_nothing()
    {
        Assert.Empty(Check());
    }

    [Fact]
    public void Stub_payments_are_fatal()
    {
        var problem = Assert.Single(Check(paymentsLive: false));

        Assert.Equal("Payments", problem.Area);
        Assert.True(problem.IsFatal);
    }

    [Fact]
    public void A_missing_webhook_secret_is_fatal()
    {
        // Without it no payment can ever be confirmed, and a forged notification would be
        // the only thing that could publish a portfolio.
        var problem = Assert.Single(Check(webhookSecret: null));

        Assert.Equal("Payments", problem.Area);
        Assert.True(problem.IsFatal);
    }

    [Fact]
    public void A_stub_email_sender_is_fatal()
    {
        // A guardian who never receives their request means an under-18 client can never
        // complete, and nobody at MSM would be told why.
        var problem = Assert.Single(Check(emailSenderIsStub: true));

        Assert.Equal("Email", problem.Area);
        Assert.True(problem.IsFatal);
    }

    [Fact]
    public void Local_disk_media_storage_is_fatal()
    {
        var problem = Assert.Single(Check(mediaStorageProvider: "LocalDisk"));

        Assert.Equal("Media storage", problem.Area);
        Assert.True(problem.IsFatal);
    }

    [Fact]
    public void The_local_disk_check_ignores_casing()
    {
        Assert.Single(Check(mediaStorageProvider: "localdisk"));
    }

    [Fact]
    public void A_stub_crm_is_reported_but_not_fatal()
    {
        // The application works without a CRM; MSM's contact records simply fall behind,
        // and the integrations page already shows that.
        var problem = Assert.Single(Check(crmLive: false));

        Assert.Equal("CRM", problem.Area);
        Assert.False(problem.IsFatal);
    }

    [Fact]
    public void Migrating_on_startup_is_reported_but_not_fatal()
    {
        var problem = Assert.Single(Check(migrateOnStartup: true));

        Assert.Equal("Database", problem.Area);
        Assert.False(problem.IsFatal);
    }

    [Fact]
    public void A_missing_contact_email_is_reported_but_not_fatal()
    {
        var problem = Assert.Single(Check(contactEmail: null));

        Assert.Equal("Branding", problem.Area);
        Assert.False(problem.IsFatal);
    }

    [Fact]
    public void Enforce_throws_when_any_problem_is_fatal()
    {
        var problems = Check(paymentsLive: false, crmLive: false);

        var error = Assert.Throws<InvalidOperationException>(
            () => ProductionReadiness.Enforce(problems, Environment("Production"), NullLogger.Instance));

        Assert.Contains("Production", error.Message);
        Assert.Contains("Payments", error.Message);
    }

    [Fact]
    public void Enforce_allows_a_start_when_no_problem_is_fatal()
    {
        var problems = Check(crmLive: false, migrateOnStartup: true);

        ProductionReadiness.Enforce(problems, Environment("Staging"), NullLogger.Instance);
    }

    [Fact]
    public void Enforce_does_nothing_in_development()
    {
        // Locally every stub is the point: payments that take no money and email that only
        // logs are exactly what a developer wants.
        var problems = Check(paymentsLive: false, emailSenderIsStub: true, mediaStorageProvider: "LocalDisk");

        Assert.NotEmpty(problems);

        ProductionReadiness.Enforce(problems, Environment("Development"), NullLogger.Instance);
    }

    private static IHostEnvironment Environment(string name) => new NamedEnvironment(name);

    private sealed class NamedEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Msm.Portfolio.Web";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } =
            new NullFileProvider();
    }

    private sealed class FakePayments(bool isLive) : IGoCardlessService
    {
        public bool IsLive { get; } = isLive;

        public Task<CheckoutSession> CreateCheckoutAsync(
            Order order,
            ClientProfile client,
            string successUrl,
            string failureUrl,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CheckoutOutcome> CompleteCheckoutAsync(
            string providerReference, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeCrm(bool isLive) : IHighLevelService
    {
        public bool IsLive { get; } = isLive;

        public Task<CrmUpdateResult> UpdateContactAsync(
            string contactId, CrmContactFields fields, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
