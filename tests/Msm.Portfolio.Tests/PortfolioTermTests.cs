using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Msm.Portfolio.Web.Data;
using Msm.Portfolio.Web.Domain.Entities;
using Msm.Portfolio.Web.Domain.Enums;
using Msm.Portfolio.Web.Services;

namespace Msm.Portfolio.Tests;

/// <summary>
/// £99 buys the portfolio for a year, and there is nothing else to pay. What ends it is
/// the calendar, so these tests are about a date passing.
/// </summary>
public class PortfolioTermTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _db;
    private readonly PortfolioTermService _service;

    public PortfolioTermTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();

        var audit = new AuditService(_db);
        var notifications = new NotificationService(_db);

        var portfolios = new PortfolioService(
            _db, new SlugService(_db), new InMemoryStorage(), audit, notifications,
            new SilentBiographyWriter(), NullLogger<PortfolioService>.Instance);

        _service = new PortfolioTermService(
            _db, portfolios, audit, notifications, NullLogger<PortfolioTermService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private Guid AddPublishedModel(DateTimeOffset? expiresAt)
    {
        var userId = Guid.CreateVersion7();
        var clientId = Guid.CreateVersion7();

        _db.Users.Add(new ApplicationUser
        {
            Id = userId,
            UserName = $"{clientId:N}@private.example",
            Email = $"{clientId:N}@private.example"
        });

        _db.ClientProfiles.Add(new ClientProfile
        {
            Id = clientId,
            ApplicationUserId = userId,
            FirstName = "Emma",
            LastName = "Johnson",
            DateOfBirth = new DateOnly(2000, 1, 1),
            ModelProfileType = ModelProfileType.Female
        });

        _db.Portfolios.Add(new Msm.Portfolio.Web.Domain.Entities.Portfolio
        {
            ClientId = clientId,
            Slug = $"emma-{clientId:N}"[..12],
            IsPublished = true,
            Status = PortfolioStatus.Published,
            PublishedAt = DateTimeOffset.UtcNow.AddDays(-300),
            ExpiresAt = expiresAt
        });

        _db.SaveChanges();

        return clientId;
    }

    [Fact]
    public async Task A_portfolio_whose_year_is_up_is_taken_down()
    {
        var clientId = AddPublishedModel(DateTimeOffset.UtcNow.AddMinutes(-1));

        var count = await _service.ExpireElapsedTermsAsync();

        Assert.Equal(1, count);
        Assert.False(_db.Portfolios.Single(p => p.ClientId == clientId).IsPublished);
    }

    [Fact]
    public async Task A_portfolio_with_time_left_is_untouched()
    {
        var clientId = AddPublishedModel(DateTimeOffset.UtcNow.AddDays(1));

        Assert.Equal(0, await _service.ExpireElapsedTermsAsync());
        Assert.True(_db.Portfolios.Single(p => p.ClientId == clientId).IsPublished);
    }

    /// <summary>
    /// Portfolios sold before the year had a meaning carry no expiry. Reading a null as
    /// "expired long ago" would take down every one of them on the first run.
    /// </summary>
    [Fact]
    public async Task A_portfolio_with_no_term_never_expires()
    {
        var clientId = AddPublishedModel(null);

        Assert.Equal(0, await _service.ExpireElapsedTermsAsync());
        Assert.True(_db.Portfolios.Single(p => p.ClientId == clientId).IsPublished);
    }

    /// <summary>
    /// The worker runs hourly, so an expired portfolio is matched again and again unless
    /// taking it down removes it from the set. Otherwise the studio is told about the same
    /// model every hour, for ever.
    /// </summary>
    [Fact]
    public async Task Expiring_twice_takes_nothing_down_the_second_time()
    {
        AddPublishedModel(DateTimeOffset.UtcNow.AddMinutes(-1));

        Assert.Equal(1, await _service.ExpireElapsedTermsAsync());
        Assert.Equal(0, await _service.ExpireElapsedTermsAsync());

        Assert.Single(_db.Notifications, n => n.Message.Contains("year has ended"));
    }

    [Fact]
    public async Task Both_the_studio_and_the_model_are_told()
    {
        var clientId = AddPublishedModel(DateTimeOffset.UtcNow.AddMinutes(-1));
        var client = _db.ClientProfiles.Single(c => c.Id == clientId);

        await _service.ExpireElapsedTermsAsync();

        Assert.Contains(_db.Notifications, n => n.UserId == client.ApplicationUserId);
    }
}
