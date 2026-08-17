using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Msm.Portfolio.Web.Configuration;
using Msm.Portfolio.Web.Data;
using Msm.Portfolio.Web.Domain.Entities;
using Msm.Portfolio.Web.Domain.Enums;
using Msm.Portfolio.Web.Integrations.HighLevel;
using Msm.Portfolio.Web.Services;

namespace Msm.Portfolio.Tests;

public class CrmSyncServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _db;
    private readonly CrmSyncService _sync;
    private readonly PortfolioService _portfolios;
    private readonly FakeCrm _crm = new();

    public CrmSyncServiceTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();

        var audit = new AuditService(_db);
        var notifications = new NotificationService(_db);
        var brand = new OptionsWrapper<MsmBrandOptions>(
            new MsmBrandOptions { PublicDomain = "https://msmportfolios.com" });

        _portfolios = new PortfolioService(
            _db, new SlugService(_db), new InMemoryStorage(), audit, notifications,
            NullLogger<PortfolioService>.Instance);

        _sync = new CrmSyncService(
            _db, _crm, notifications, brand, NullLogger<CrmSyncService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private Guid AddClient(string? ghlContactId = "ghl-contact-1", bool withImage = true)
    {
        var userId = Guid.CreateVersion7();
        var clientId = Guid.CreateVersion7();

        _db.Users.Add(new ApplicationUser { Id = userId, UserName = $"{clientId:N}@x.com", Email = $"{clientId:N}@x.com" });
        _db.ClientProfiles.Add(new ClientProfile
        {
            Id = clientId,
            ApplicationUserId = userId,
            FirstName = "Emma",
            LastName = "Johnson",
            DateOfBirth = new DateOnly(1998, 1, 1),
            GhlContactId = ghlContactId
        });

        var portfolio = new Msm.Portfolio.Web.Domain.Entities.Portfolio
        {
            ClientId = clientId,
            Status = PortfolioStatus.InViewing
        };
        _db.Portfolios.Add(portfolio);
        _db.SaveChanges();

        if (withImage)
        {
            var asset = new MediaAsset
            {
                ClientId = clientId,
                StorageKey = $"clients/{clientId:N}/a/original.jpg",
                OriginalFilename = "a.jpg", MimeType = "image/jpeg", FileSize = 10,
                MediaType = MediaType.Image, IsSelectedForPortfolio = true, IsFeatured = true
            };
            _db.MediaAssets.Add(asset);
            _db.SaveChanges();
            portfolio.FeaturedMediaId = asset.Id;
            _db.SaveChanges();
        }

        return clientId;
    }

    private Msm.Portfolio.Web.Domain.Entities.Portfolio PortfolioFor(Guid clientId) =>
        _db.Portfolios.Single(p => p.ClientId == clientId);

    // ---------- Marking, not pushing ----------

    /// <summary>
    /// Specification section 45: the push happens on a worker, not inside the operation.
    /// Publishing therefore only marks the portfolio as needing a sync.
    /// </summary>
    [Fact]
    public async Task Publishing_marks_the_portfolio_for_sync_without_calling_the_crm()
    {
        var clientId = AddClient();

        await _portfolios.PublishAsync(clientId, null);

        Assert.Equal(CrmSyncStatus.Pending, PortfolioFor(clientId).CrmSyncStatus);
        Assert.Equal(0, _crm.Calls);
    }

    [Fact]
    public async Task Unpublishing_and_slug_changes_also_mark_for_sync()
    {
        var clientId = AddClient();
        await _portfolios.PublishAsync(clientId, null);
        await _sync.SyncPendingAsync();
        Assert.Equal(CrmSyncStatus.Synced, PortfolioFor(clientId).CrmSyncStatus);

        await _portfolios.ChangeSlugAsync(clientId, "emmy-j", null);
        Assert.Equal(CrmSyncStatus.Pending, PortfolioFor(clientId).CrmSyncStatus);

        await _sync.SyncPendingAsync();
        await _portfolios.UnpublishAsync(clientId, null);
        Assert.Equal(CrmSyncStatus.Pending, PortfolioFor(clientId).CrmSyncStatus);
    }

    // ---------- The fields pushed ----------

    [Fact]
    public async Task A_published_portfolio_pushes_its_public_url_and_live_status()
    {
        var clientId = AddClient();
        await _portfolios.PublishAsync(clientId, null);

        await _sync.SyncPendingAsync();

        var (contactId, fields) = _crm.Last;
        Assert.Equal("ghl-contact-1", contactId);
        Assert.Equal("https://msmportfolios.com/emma-johnson", fields.PortfolioUrl);
        Assert.Equal("Live", fields.PortfolioStatus);
        Assert.NotNull(fields.PortfolioPublishedDate);
    }

    [Fact]
    public async Task An_unpublished_portfolio_pushes_no_url()
    {
        var clientId = AddClient();

        await _sync.SyncClientAsync(clientId);

        Assert.Null(_crm.Last.Fields.PortfolioUrl);
        Assert.Equal("InViewing", _crm.Last.Fields.PortfolioStatus);
    }

    [Fact]
    public async Task Purchase_status_and_date_come_from_a_confirmed_order()
    {
        var clientId = AddClient();

        var product = new Product
        {
            Code = ProductCodes.ModelDevelopmentProgramme, Name = "Programme",
            Price = 3499m, Currency = "GBP", BillingType = BillingType.OneOff
        };
        _db.Products.Add(product);
        _db.SaveChanges();

        var confirmedAt = DateTimeOffset.UtcNow.AddDays(-2);
        _db.Orders.Add(new Order
        {
            ClientId = clientId, ProductId = product.Id, Amount = 3499m, Currency = "GBP",
            Status = OrderStatus.Confirmed, ConfirmedAt = confirmedAt
        });
        _db.SaveChanges();

        await _sync.SyncClientAsync(clientId);

        Assert.Equal("Purchased", _crm.Last.Fields.PurchaseStatus);
        Assert.Equal(confirmedAt.Date, _crm.Last.Fields.PurchaseDate!.Value.Date);
    }

    [Fact]
    public async Task A_client_who_has_not_bought_shows_as_not_purchased()
    {
        var clientId = AddClient();

        await _sync.SyncClientAsync(clientId);

        Assert.Equal("Not purchased", _crm.Last.Fields.PurchaseStatus);
        Assert.Null(_crm.Last.Fields.PurchaseDate);
        Assert.Equal("None", _crm.Last.Fields.MaintenanceStatus);
    }

    /// <summary>
    /// Only the fields specification section 25 lists are sent. Nothing else about the
    /// client should leave the application.
    /// </summary>
    [Fact]
    public async Task No_personal_detail_beyond_the_listed_fields_is_sent()
    {
        var clientId = AddClient();
        var client = _db.ClientProfiles.Single(c => c.Id == clientId);
        client.Biography = "A private biography.";
        client.Location = "Manchester";
        await _db.SaveChangesAsync();

        await _portfolios.PublishAsync(clientId, null);
        await _sync.SyncPendingAsync();

        var serialised = System.Text.Json.JsonSerializer.Serialize(_crm.Last.Fields);

        Assert.DoesNotContain("private biography", serialised);
        Assert.DoesNotContain("Manchester", serialised);
        Assert.DoesNotContain("@x.com", serialised);
    }

    // ---------- Failure handling ----------

    /// <summary>
    /// The rule specification section 45 exists for: a CRM outage must never roll back
    /// a portfolio that is genuinely published.
    /// </summary>
    [Fact]
    public async Task A_crm_outage_leaves_the_portfolio_published()
    {
        var clientId = AddClient();
        await _portfolios.PublishAsync(clientId, null);

        _crm.NextResult = new CrmUpdateResult(false, "The CRM could not be reached.");
        await _sync.SyncPendingAsync();

        var portfolio = PortfolioFor(clientId);
        Assert.True(portfolio.IsPublished);
        Assert.Equal(PortfolioStatus.Published, portfolio.Status);
        Assert.Equal("emma-johnson", portfolio.Slug);
        Assert.Equal(CrmSyncStatus.Failed, portfolio.CrmSyncStatus);
    }

    /// <summary>The boundary must not be able to throw into whatever called it.</summary>
    [Fact]
    public async Task A_crm_that_throws_is_contained()
    {
        var clientId = AddClient();
        await _portfolios.PublishAsync(clientId, null);

        _crm.ThrowNext = true;

        var summary = await _sync.SyncPendingAsync();

        Assert.Equal(1, summary.Failed);
        Assert.True(PortfolioFor(clientId).IsPublished);
        Assert.Equal(CrmSyncStatus.Failed, PortfolioFor(clientId).CrmSyncStatus);
    }

    [Fact]
    public async Task A_failure_backs_off_before_the_next_attempt()
    {
        var clientId = AddClient();
        await _portfolios.PublishAsync(clientId, null);

        _crm.NextResult = new CrmUpdateResult(false, "Down");
        await _sync.SyncPendingAsync();

        var portfolio = PortfolioFor(clientId);
        Assert.Equal(1, portfolio.CrmSyncAttempts);
        Assert.NotNull(portfolio.CrmSyncNextAttemptAt);
        Assert.True(portfolio.CrmSyncNextAttemptAt > DateTimeOffset.UtcNow);

        // Still inside the backoff, so the next pass leaves it alone.
        var second = await _sync.SyncPendingAsync();
        Assert.Equal(0, second.Attempted);
    }

    [Fact]
    public async Task The_backoff_lengthens_with_repeated_failures()
    {
        var clientId = AddClient();
        await _portfolios.PublishAsync(clientId, null);
        _crm.AlwaysFail = true;

        var delays = new List<double>();

        for (var i = 0; i < 4; i++)
        {
            PortfolioFor(clientId).CrmSyncNextAttemptAt = null;
            await _db.SaveChangesAsync();

            var before = DateTimeOffset.UtcNow;
            await _sync.SyncPendingAsync();

            delays.Add((PortfolioFor(clientId).CrmSyncNextAttemptAt!.Value - before).TotalMinutes);
        }

        Assert.True(delays[1] > delays[0], "The second delay should exceed the first.");
        Assert.True(delays[3] > delays[2], "The delay should keep growing.");
    }

    [Fact]
    public async Task A_recovered_crm_clears_the_failure_state()
    {
        var clientId = AddClient();
        await _portfolios.PublishAsync(clientId, null);

        _crm.NextResult = new CrmUpdateResult(false, "Down");
        await _sync.SyncPendingAsync();
        Assert.Equal(CrmSyncStatus.Failed, PortfolioFor(clientId).CrmSyncStatus);

        PortfolioFor(clientId).CrmSyncNextAttemptAt = null;
        await _db.SaveChangesAsync();

        await _sync.SyncPendingAsync();

        var portfolio = PortfolioFor(clientId);
        Assert.Equal(CrmSyncStatus.Synced, portfolio.CrmSyncStatus);
        Assert.Equal(0, portfolio.CrmSyncAttempts);
        Assert.Null(portfolio.CrmSyncError);
        Assert.NotNull(portfolio.CrmSyncedAt);
    }

    /// <summary>
    /// A rejected request will fail identically forever, so it is not retried; it waits
    /// for a person instead of occupying the queue.
    /// </summary>
    [Fact]
    public async Task A_permanent_rejection_is_not_scheduled_for_retry()
    {
        var clientId = AddClient();
        await _portfolios.PublishAsync(clientId, null);

        _crm.NextResult = new CrmUpdateResult(false, "CRM returned 404.", IsRetryable: false);
        await _sync.SyncPendingAsync();

        var portfolio = PortfolioFor(clientId);
        Assert.Equal(CrmSyncStatus.Failed, portfolio.CrmSyncStatus);
        Assert.Null(portfolio.CrmSyncNextAttemptAt);
    }

    [Fact]
    public async Task Staff_are_alerted_after_repeated_failures_but_only_once()
    {
        var clientId = AddClient();
        await _portfolios.PublishAsync(clientId, null);

        var adminRole = new ApplicationRole(Msm.Portfolio.Web.Authorization.Roles.Admin) { Id = Guid.CreateVersion7() };
        var admin = new ApplicationUser { Id = Guid.CreateVersion7(), UserName = "a@msm.local", Email = "a@msm.local" };
        _db.Roles.Add(adminRole);
        _db.Users.Add(admin);
        _db.UserRoles.Add(new Microsoft.AspNetCore.Identity.IdentityUserRole<Guid>
        {
            RoleId = adminRole.Id, UserId = admin.Id
        });
        await _db.SaveChangesAsync();

        _crm.AlwaysFail = true;

        for (var i = 0; i < 5; i++)
        {
            PortfolioFor(clientId).CrmSyncNextAttemptAt = null;
            await _db.SaveChangesAsync();
            await _sync.SyncPendingAsync();
        }

        Assert.Equal(1, _db.Notifications.Count(n => n.Type == NotificationTypes.CrmSyncFailing));
    }

    /// <summary>
    /// A client created directly by staff has no CRM record. Retrying forever would be
    /// pointless, so it is recorded as not synced rather than failed.
    /// </summary>
    [Fact]
    public async Task A_client_with_no_crm_contact_is_skipped_rather_than_retried()
    {
        var clientId = AddClient(ghlContactId: null);
        await _portfolios.PublishAsync(clientId, null);

        var summary = await _sync.SyncPendingAsync();

        Assert.Equal(1, summary.Skipped);
        Assert.Equal(0, _crm.Calls);
        Assert.Equal(CrmSyncStatus.NotSynced, PortfolioFor(clientId).CrmSyncStatus);
    }

    [Fact]
    public async Task The_state_summary_counts_portfolios_by_sync_status()
    {
        var first = AddClient();
        var second = AddClient("ghl-contact-2");
        await _portfolios.PublishAsync(first, null);
        await _portfolios.PublishAsync(second, null);

        await _sync.SyncClientAsync(first);

        var summary = await _sync.GetStateSummaryAsync();

        Assert.Equal(1, summary[CrmSyncStatus.Synced]);
        Assert.Equal(1, summary[CrmSyncStatus.Pending]);
    }
}

/// <summary>A CRM that records what it was sent and can be told to fail.</summary>
internal class FakeCrm : IHighLevelService
{
    public bool IsLive => false;

    public int Calls { get; private set; }

    public (string ContactId, CrmContactFields Fields) Last { get; private set; }

    public CrmUpdateResult? NextResult { get; set; }

    public bool AlwaysFail { get; set; }

    public bool ThrowNext { get; set; }

    public Task<CrmUpdateResult> UpdateContactAsync(
        string contactId, CrmContactFields fields, CancellationToken cancellationToken = default)
    {
        Calls++;
        Last = (contactId, fields);

        if (ThrowNext)
        {
            ThrowNext = false;
            throw new HttpRequestException("The CRM exploded.");
        }

        if (AlwaysFail)
        {
            return Task.FromResult(new CrmUpdateResult(false, "Always failing."));
        }

        var result = NextResult ?? new CrmUpdateResult(true);
        NextResult = null;

        return Task.FromResult(result);
    }
}
