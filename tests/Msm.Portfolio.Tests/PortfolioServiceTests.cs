using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Msm.Portfolio.Web.Data;
using Msm.Portfolio.Web.Domain.Entities;
using Msm.Portfolio.Web.Domain.Enums;
using Msm.Portfolio.Web.Services;
using Msm.Portfolio.Web.Storage;

namespace Msm.Portfolio.Tests;

public class PortfolioServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _db;
    private readonly PortfolioService _service;
    private readonly InMemoryStorage _storage = new();

    public PortfolioServiceTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();

        _service = new PortfolioService(
            _db,
            new SlugService(_db),
            _storage,
            new AuditService(_db),
            new NotificationService(_db),
            NullLogger<PortfolioService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private Guid AddClient(
        PortfolioStatus status = PortfolioStatus.ReadyForReview,
        string name = "Emma Johnson",
        int age = 25,
        bool withImage = true,
        GuardianConsentStatus? guardian = null)
    {
        var userId = Guid.CreateVersion7();
        var clientId = Guid.CreateVersion7();
        var parts = name.Split(' ');

        _db.Users.Add(new ApplicationUser { Id = userId, UserName = $"{clientId:N}@x.com", Email = $"{clientId:N}@x.com" });
        _db.ClientProfiles.Add(new ClientProfile
        {
            Id = clientId,
            ApplicationUserId = userId,
            FirstName = parts[0],
            LastName = parts.Length > 1 ? parts[1] : "Model",
            DateOfBirth = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-age)
        });

        var portfolio = new Msm.Portfolio.Web.Domain.Entities.Portfolio { ClientId = clientId, Status = status };
        _db.Portfolios.Add(portfolio);

        if (guardian is { } consentStatus)
        {
            _db.GuardianConsents.Add(new GuardianConsent
            {
                ClientId = clientId,
                GuardianName = "Guardian",
                Relationship = "Parent",
                Email = "g@example.com",
                VerificationToken = Guid.NewGuid().ToString("N"),
                Status = consentStatus
            });
        }

        _db.SaveChanges();

        if (withImage)
        {
            var asset = new MediaAsset
            {
                ClientId = clientId,
                StorageKey = $"clients/{clientId:N}/{Guid.NewGuid():N}/original.jpg",
                OriginalFilename = "shot.jpg",
                MimeType = "image/jpeg",
                FileSize = 100,
                MediaType = MediaType.Image,
                IsSelectedForPortfolio = true,
                IsFeatured = true
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

    [Fact]
    public async Task Marking_in_viewing_moves_a_reviewed_portfolio_forward()
    {
        var clientId = AddClient(PortfolioStatus.ReadyForReview);

        Assert.True((await _service.MarkInViewingAsync(clientId, null)).Succeeded);
        Assert.Equal(PortfolioStatus.InViewing, PortfolioFor(clientId).Status);
    }

    [Fact]
    public async Task Publishing_assigns_a_slug_and_makes_the_portfolio_public()
    {
        var clientId = AddClient(PortfolioStatus.InViewing, "Emma Johnson");

        Assert.True((await _service.PublishAsync(clientId, null)).Succeeded);

        var portfolio = PortfolioFor(clientId);
        Assert.True(portfolio.IsPublished);
        Assert.Equal("emma-johnson", portfolio.Slug);
        Assert.NotNull(portfolio.PublishedAt);
        Assert.Equal(PortfolioStatus.Published, portfolio.Status);
    }

    /// <summary>
    /// Specification section 39: a link already shared with an agency must not break
    /// because the model later changed their display name.
    /// </summary>
    [Fact]
    public async Task Republishing_keeps_the_original_slug()
    {
        var clientId = AddClient(PortfolioStatus.InViewing, "Emma Johnson");
        await _service.PublishAsync(clientId, null);

        var client = _db.ClientProfiles.Single(c => c.Id == clientId);
        client.DisplayName = "Emmy J";
        await _db.SaveChangesAsync();

        await _service.UnpublishAsync(clientId, null);
        await _service.PublishAsync(clientId, null);

        Assert.Equal("emma-johnson", PortfolioFor(clientId).Slug);
    }

    /// <summary>
    /// The hard stop from specification section 11, promised when the guardian
    /// workflow was built: a minor cannot be published without approval.
    /// </summary>
    [Fact]
    public async Task A_minor_without_guardian_approval_cannot_be_published()
    {
        var clientId = AddClient(PortfolioStatus.InViewing, age: 16, guardian: GuardianConsentStatus.Pending);

        var result = await _service.PublishAsync(clientId, null);

        Assert.False(result.Succeeded);
        Assert.Contains("under 18", result.Error);
        Assert.False(PortfolioFor(clientId).IsPublished);
    }

    [Fact]
    public async Task A_minor_with_guardian_approval_can_be_published()
    {
        var clientId = AddClient(PortfolioStatus.InViewing, age: 16, guardian: GuardianConsentStatus.Approved);

        Assert.True((await _service.PublishAsync(clientId, null)).Succeeded);
        Assert.True(PortfolioFor(clientId).IsPublished);
    }

    [Fact]
    public async Task A_portfolio_with_no_photographs_cannot_be_published()
    {
        var clientId = AddClient(PortfolioStatus.InViewing, withImage: false);

        var result = await _service.PublishAsync(clientId, null);

        Assert.False(result.Succeeded);
        Assert.Contains("no photographs", result.Error);
    }

    [Fact]
    public async Task The_publish_blocker_explains_why_rather_than_only_refusing()
    {
        var blocked = AddClient(PortfolioStatus.InViewing, age: 15, guardian: GuardianConsentStatus.Pending);
        var ready = AddClient(PortfolioStatus.InViewing);

        Assert.NotNull(await _service.DescribePublishBlockerAsync(blocked));
        Assert.Null(await _service.DescribePublishBlockerAsync(ready));
    }

    [Fact]
    public async Task Unpublishing_takes_the_portfolio_off_the_web()
    {
        var clientId = AddClient(PortfolioStatus.InViewing);
        await _service.PublishAsync(clientId, null);

        Assert.True((await _service.UnpublishAsync(clientId, null)).Succeeded);

        var portfolio = PortfolioFor(clientId);
        Assert.False(portfolio.IsPublished);
        Assert.NotNull(portfolio.UnpublishedAt);
        Assert.Equal(PortfolioStatus.Unpublished, portfolio.Status);

        // The slug survives so the same address is restored if it goes live again.
        Assert.Equal("emma-johnson", portfolio.Slug);
    }

    [Fact]
    public async Task A_live_portfolio_cannot_be_archived_or_marked_no_sale()
    {
        var clientId = AddClient(PortfolioStatus.InViewing);
        await _service.PublishAsync(clientId, null);

        Assert.False((await _service.ArchiveAsync(clientId, null)).Succeeded);
        Assert.False((await _service.MarkNoSaleAsync(clientId, null)).Succeeded);
        Assert.False((await _service.DeletePermanentlyAsync(clientId, null)).Succeeded);
    }

    /// <summary>
    /// Specification section 48: a declined sale is archived, not deleted, and stays
    /// available to staff until a Super Admin removes it.
    /// </summary>
    [Fact]
    public async Task No_sale_archives_the_record_rather_than_deleting_it()
    {
        var clientId = AddClient(PortfolioStatus.InViewing);

        Assert.True((await _service.MarkNoSaleAsync(clientId, null)).Succeeded);

        var portfolio = PortfolioFor(clientId);
        Assert.Equal(PortfolioStatus.Archived, portfolio.Status);
        Assert.False(portfolio.IsPublished);

        // Both steps are recorded, so the reason for archiving is not lost.
        var statusChanges = _db.AuditLogs
            .Where(a => a.Action == AuditActions.PortfolioStatusChanged)
            .Select(a => a.NewValue)
            .ToList();

        Assert.Contains(PortfolioStatus.NoSale.ToString(), statusChanges);
        Assert.Contains(PortfolioStatus.Archived.ToString(), statusChanges);
    }

    [Fact]
    public async Task An_archived_portfolio_can_be_restored_but_comes_back_unpublished()
    {
        var clientId = AddClient(PortfolioStatus.InViewing);
        await _service.MarkNoSaleAsync(clientId, null);

        Assert.True((await _service.RestoreAsync(clientId, null)).Succeeded);

        var portfolio = PortfolioFor(clientId);
        Assert.Equal(PortfolioStatus.Unpublished, portfolio.Status);
        Assert.False(portfolio.IsPublished);
    }

    [Fact]
    public async Task Only_an_archived_portfolio_can_be_restored()
    {
        var clientId = AddClient(PortfolioStatus.InViewing);

        Assert.False((await _service.RestoreAsync(clientId, null)).Succeeded);
    }

    [Fact]
    public async Task Permanent_deletion_removes_the_portfolio_its_media_and_the_files()
    {
        var clientId = AddClient(PortfolioStatus.Archived);
        var asset = _db.MediaAssets.Single(m => m.ClientId == clientId);

        await _storage.UploadAsync(new MemoryStream([1, 2, 3]), asset.StorageKey, "image/jpeg");
        await _storage.UploadAsync(
            new MemoryStream([1, 2, 3]),
            MediaStorageKeys.ForVariant(asset.StorageKey, MediaVariant.Thumbnail), "image/jpeg");

        Assert.True((await _service.DeletePermanentlyAsync(clientId, null)).Succeeded);

        Assert.Empty(_db.Portfolios.Where(p => p.ClientId == clientId));
        Assert.Empty(_db.MediaAssets.Where(m => m.ClientId == clientId));
        Assert.False(_storage.Contains(asset.StorageKey));
        Assert.False(_storage.Contains(MediaStorageKeys.ForVariant(asset.StorageKey, MediaVariant.Thumbnail)));
    }

    /// <summary>
    /// The audit entry deliberately outlives the record it describes, so a permanent
    /// deletion is itself permanently accounted for (specification section 36).
    /// </summary>
    [Fact]
    public async Task Permanent_deletion_leaves_an_audit_entry_behind()
    {
        var clientId = AddClient(PortfolioStatus.Archived);

        await _service.DeletePermanentlyAsync(clientId, null);

        Assert.Contains(_db.AuditLogs, a => a.Action == AuditActions.PortfolioDeletedPermanently);
        // The client record itself survives; only the portfolio and its media go.
        Assert.NotNull(_db.ClientProfiles.SingleOrDefault(c => c.Id == clientId));
    }

    [Fact]
    public async Task The_slug_can_be_changed_to_an_available_address()
    {
        var clientId = AddClient(PortfolioStatus.InViewing);
        await _service.PublishAsync(clientId, null);

        Assert.True((await _service.ChangeSlugAsync(clientId, "Emmy J", null)).Succeeded);
        Assert.Equal("emmy-j", PortfolioFor(clientId).Slug);
    }

    [Fact]
    public async Task The_slug_cannot_be_changed_to_one_already_taken()
    {
        var first = AddClient(PortfolioStatus.InViewing, "Emma Johnson");
        var second = AddClient(PortfolioStatus.InViewing, "Sara Smith");
        await _service.PublishAsync(first, null);
        await _service.PublishAsync(second, null);

        var result = await _service.ChangeSlugAsync(second, "emma-johnson", null);

        Assert.False(result.Succeeded);
        Assert.Contains("already taken", result.Error);
    }

    [Fact]
    public async Task The_slug_cannot_be_changed_to_a_reserved_route()
    {
        var clientId = AddClient(PortfolioStatus.InViewing);
        await _service.PublishAsync(clientId, null);

        Assert.False((await _service.ChangeSlugAsync(clientId, "admin", null)).Succeeded);
    }

    [Fact]
    public async Task Sending_a_portfolio_back_reopens_the_retouchers_assignment()
    {
        var clientId = AddClient(PortfolioStatus.ReadyForReview);
        var retoucherId = Guid.CreateVersion7();

        _db.Users.Add(new ApplicationUser { Id = retoucherId, UserName = "r@msm.local", Email = "r@msm.local" });
        _db.RetoucherAssignments.Add(new RetoucherAssignment
        {
            ClientId = clientId,
            RetoucherUserId = retoucherId,
            Status = RetoucherAssignmentStatus.ReadyForReview,
            SubmittedForReviewAt = DateTimeOffset.UtcNow
        });
        _db.SaveChanges();

        Assert.True((await _service.ReturnToRetoucherAsync(clientId, null)).Succeeded);

        var assignment = _db.RetoucherAssignments.Single(a => a.ClientId == clientId);
        Assert.Equal(RetoucherAssignmentStatus.InProgress, assignment.Status);
        Assert.Null(assignment.SubmittedForReviewAt);
        Assert.Equal(PortfolioStatus.Retouching, PortfolioFor(clientId).Status);

        // The retoucher is told, rather than the work silently reappearing.
        Assert.Contains(_db.Notifications,
            n => n.UserId == retoucherId && n.Type == NotificationTypes.PortfolioReturnedToRetoucher);
    }

    [Fact]
    public async Task Publishing_marks_the_crm_as_needing_a_push()
    {
        var clientId = AddClient(PortfolioStatus.InViewing);

        await _service.PublishAsync(clientId, null);

        Assert.Equal(CrmSyncStatus.Pending, PortfolioFor(clientId).CrmSyncStatus);
    }
}
