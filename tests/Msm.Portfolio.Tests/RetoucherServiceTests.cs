using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Msm.Portfolio.Web.Authorization;
using Msm.Portfolio.Web.Data;
using Msm.Portfolio.Web.Domain.Entities;
using Msm.Portfolio.Web.Domain.Enums;
using Msm.Portfolio.Web.Services;
using Msm.Portfolio.Web.Storage;

namespace Msm.Portfolio.Tests;

public class RetoucherServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _db;
    private readonly RetoucherService _service;

    private readonly Guid _retoucherA = Guid.CreateVersion7();
    private readonly Guid _retoucherB = Guid.CreateVersion7();

    public RetoucherServiceTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        _db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();

        _db.Users.AddRange(
            new ApplicationUser { Id = _retoucherA, UserName = "a@msm.local", Email = "a@msm.local", FirstName = "Ann", LastName = "A" },
            new ApplicationUser { Id = _retoucherB, UserName = "b@msm.local", Email = "b@msm.local", FirstName = "Ben", LastName = "B" });
        _db.SaveChanges();

        var portfolios = new PortfolioService(
            _db,
            new SlugService(_db),
            new InMemoryStorage(),
            new AuditService(_db),
            new NotificationService(_db),
            new SilentBiographyWriter(),
            NullLogger<PortfolioService>.Instance);

        _service = new RetoucherService(
            _db, portfolios, new AuditService(_db), new NotificationService(_db),
            NullLogger<RetoucherService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private Guid AddClient(PortfolioStatus status, string name = "Test Model", DateOnly? dob = null)
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
            DateOfBirth = dob ?? new DateOnly(1998, 5, 5)
        });
        _db.Portfolios.Add(new Msm.Portfolio.Web.Domain.Entities.Portfolio { ClientId = clientId, Status = status });
        _db.SaveChanges();

        return clientId;
    }

    private void AddSelectedImage(Guid clientId, bool featured = true)
    {
        var asset = new MediaAsset
        {
            ClientId = clientId,
            StorageKey = $"clients/{clientId:N}/x/original.jpg",
            OriginalFilename = "x.jpg",
            MimeType = "image/jpeg",
            FileSize = 100,
            MediaType = MediaType.Image,
            IsSelectedForPortfolio = true,
            IsFeatured = featured
        };

        _db.MediaAssets.Add(asset);
        _db.SaveChanges();

        if (featured)
        {
            var portfolio = _db.Portfolios.Single(p => p.ClientId == clientId);
            portfolio.FeaturedMediaId = asset.Id;
            _db.SaveChanges();
        }
    }

    [Fact]
    public async Task The_waiting_queue_holds_clients_ready_for_a_retoucher()
    {
        AddClient(PortfolioStatus.ReadyForRetoucher, "Ava Waiting");
        AddClient(PortfolioStatus.Retouching, "Bob Busy");

        var waiting = await _service.GetQueueAsync(RetoucherQueueTab.Waiting, _retoucherA);

        Assert.Single(waiting);
        Assert.Equal("Ava Waiting", waiting[0].ClientName);
    }

    [Fact]
    public async Task Each_tab_shows_only_its_own_statuses()
    {
        AddClient(PortfolioStatus.ReadyForRetoucher);
        AddClient(PortfolioStatus.Retouching);
        AddClient(PortfolioStatus.ReadyForReview);
        AddClient(PortfolioStatus.Published);
        AddClient(PortfolioStatus.InViewing);

        var counts = await _service.GetCountsAsync();

        Assert.Equal(1, counts.Waiting);
        Assert.Equal(1, counts.InProgress);
        // InViewing counts alongside ReadyForReview: submitting now carries a portfolio
        // straight there (specification section 27, version 2), so from a retoucher's
        // chair both still just mean "sent, not yet bought".
        Assert.Equal(2, counts.ReadyForReview);
        // Only what is actually being paid for, or beyond, counts as done.
        Assert.Equal(1, counts.Completed);
    }

    [Fact]
    public async Task Queue_rows_carry_the_counts_the_dashboard_shows()
    {
        var clientId = AddClient(PortfolioStatus.Retouching);
        AddSelectedImage(clientId);

        var row = (await _service.GetQueueAsync(RetoucherQueueTab.InProgress, _retoucherA)).Single();

        Assert.Equal(1, row.ImageCount);
        Assert.Equal(1, row.SelectedCount);
    }

    [Fact]
    public async Task Starting_work_claims_the_client_and_moves_it_to_retouching()
    {
        var clientId = AddClient(PortfolioStatus.ReadyForRetoucher);

        var (succeeded, _) = await _service.StartWorkAsync(clientId, _retoucherA);

        Assert.True(succeeded);
        Assert.Equal(PortfolioStatus.Retouching, _db.Portfolios.Single(p => p.ClientId == clientId).Status);

        var assignment = await _service.GetAssignmentAsync(clientId);
        Assert.Equal(_retoucherA, assignment!.RetoucherUserId);
        Assert.Equal(RetoucherAssignmentStatus.InProgress, assignment.Status);
        Assert.NotNull(assignment.StartedAt);
    }

    /// <summary>
    /// Two retouchers preparing the same portfolio unknowingly is exactly what the
    /// per-retoucher assignment in specification section 6 exists to prevent.
    /// </summary>
    [Fact]
    public async Task A_second_retoucher_cannot_claim_work_already_taken()
    {
        var clientId = AddClient(PortfolioStatus.ReadyForRetoucher);
        await _service.StartWorkAsync(clientId, _retoucherA);

        var (succeeded, error) = await _service.StartWorkAsync(clientId, _retoucherB);

        Assert.False(succeeded);
        Assert.Contains("Another retoucher", error);
        Assert.Equal(_retoucherA, (await _service.GetAssignmentAsync(clientId))!.RetoucherUserId);
    }

    [Fact]
    public async Task Reclaiming_your_own_work_is_allowed()
    {
        var clientId = AddClient(PortfolioStatus.ReadyForRetoucher);
        await _service.StartWorkAsync(clientId, _retoucherA);

        Assert.True((await _service.StartWorkAsync(clientId, _retoucherA)).Succeeded);
    }

    [Fact]
    public async Task Unclaimed_waiting_work_can_be_opened_by_any_retoucher()
    {
        var clientId = AddClient(PortfolioStatus.ReadyForRetoucher);

        Assert.True(await _service.CanOpenAsync(clientId, _retoucherA));
        Assert.True(await _service.CanOpenAsync(clientId, _retoucherB));
    }

    [Fact]
    public async Task Claimed_work_can_only_be_opened_by_the_assigned_retoucher()
    {
        var clientId = AddClient(PortfolioStatus.ReadyForRetoucher);
        await _service.StartWorkAsync(clientId, _retoucherA);

        Assert.True(await _service.CanOpenAsync(clientId, _retoucherA));
        Assert.False(await _service.CanOpenAsync(clientId, _retoucherB));
    }

    [Fact]
    public async Task An_unknown_client_cannot_be_opened()
    {
        Assert.False(await _service.CanOpenAsync(Guid.CreateVersion7(), _retoucherA));
    }

    [Fact]
    public async Task Submitting_carries_the_portfolio_straight_to_viewing()
    {
        var clientId = AddClient(PortfolioStatus.ReadyForRetoucher);
        await _service.StartWorkAsync(clientId, _retoucherA);
        AddSelectedImage(clientId);

        var (succeeded, _) = await _service.SubmitForReviewAsync(clientId, _retoucherA);

        Assert.True(succeeded);

        // No administrator click is required any more: submitting reuses
        // MarkInViewingAsync itself, so the portfolio is already showable and its
        // checkout is already open the moment the retoucher sends it (spec 27, v2).
        Assert.Equal(PortfolioStatus.InViewing, _db.Portfolios.Single(p => p.ClientId == clientId).Status);

        // The assignment itself still records ReadyForReview — that is what keeps this
        // submission in the retoucher's own "ready for review" tab (spec 6).
        var assignment = await _service.GetAssignmentAsync(clientId);
        Assert.Equal(RetoucherAssignmentStatus.ReadyForReview, assignment!.Status);
        Assert.NotNull(assignment.SubmittedForReviewAt);
    }

    /// <summary>An empty portfolio would reach Admin with nothing to review.</summary>
    [Fact]
    public async Task A_portfolio_with_no_chosen_images_cannot_be_submitted()
    {
        var clientId = AddClient(PortfolioStatus.Retouching);

        var (succeeded, error) = await _service.SubmitForReviewAsync(clientId, _retoucherA);

        Assert.False(succeeded);
        Assert.Contains("at least one photograph", error);
        Assert.Equal(PortfolioStatus.Retouching, _db.Portfolios.Single(p => p.ClientId == clientId).Status);
    }

    [Fact]
    public async Task A_portfolio_without_a_main_image_cannot_be_submitted()
    {
        var clientId = AddClient(PortfolioStatus.Retouching);
        AddSelectedImage(clientId, featured: false);

        var (succeeded, error) = await _service.SubmitForReviewAsync(clientId, _retoucherA);

        Assert.False(succeeded);
        Assert.Contains("main image", error);
    }

    [Fact]
    public async Task Submitting_notifies_staff_and_writes_an_audit_entry()
    {
        var clientId = AddClient(PortfolioStatus.Retouching, "Cara Model");
        AddSelectedImage(clientId);

        // A staff account must exist for there to be anyone to notify.
        var adminRole = new ApplicationRole(Msm.Portfolio.Web.Authorization.Roles.Admin) { Id = Guid.CreateVersion7() };
        var admin = new ApplicationUser { Id = Guid.CreateVersion7(), UserName = "admin@msm.local", Email = "admin@msm.local" };
        _db.Roles.Add(adminRole);
        _db.Users.Add(admin);
        _db.UserRoles.Add(new Microsoft.AspNetCore.Identity.IdentityUserRole<Guid>
        {
            RoleId = adminRole.Id, UserId = admin.Id
        });
        _db.SaveChanges();

        await _service.SubmitForReviewAsync(clientId, _retoucherA);

        Assert.Contains(_db.Notifications,
            n => n.Type == NotificationTypes.PortfolioReadyForReview && n.UserId == admin.Id);
        Assert.Contains(_db.AuditLogs, a => a.Action == AuditActions.PortfolioStatusChanged);
    }

    /// <summary>
    /// Guardian approval is surfaced to the retoucher but does not remove the client
    /// from the queue: preparation continues while approval is outstanding
    /// (specification section 11).
    /// </summary>
    [Fact]
    public async Task A_minor_awaiting_guardian_approval_is_flagged_but_still_queued()
    {
        var minorId = AddClient(PortfolioStatus.ReadyForRetoucher, "Young Model",
            DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-15));

        var row = (await _service.GetQueueAsync(RetoucherQueueTab.Waiting, _retoucherA)).Single();

        Assert.Equal(minorId, row.ClientId);
        Assert.True(row.GuardianApprovalPending);
    }

    [Fact]
    public async Task An_approved_minor_is_not_flagged()
    {
        var minorId = AddClient(PortfolioStatus.ReadyForRetoucher, "Young Model",
            DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-16));

        _db.GuardianConsents.Add(new GuardianConsent
        {
            ClientId = minorId,
            GuardianName = "Guardian",
            Relationship = "Parent",
            Email = "g@example.com",
            VerificationToken = "token-1",
            Status = GuardianConsentStatus.Approved,
            ApprovedAt = DateTimeOffset.UtcNow
        });
        _db.SaveChanges();

        var row = (await _service.GetQueueAsync(RetoucherQueueTab.Waiting, _retoucherA)).Single();

        Assert.False(row.GuardianApprovalPending);
    }

    [Fact]
    public async Task An_adult_is_never_flagged_for_guardian_approval()
    {
        AddClient(PortfolioStatus.ReadyForRetoucher, "Adult Model", new DateOnly(1995, 1, 1));

        var row = (await _service.GetQueueAsync(RetoucherQueueTab.Waiting, _retoucherA)).Single();

        Assert.False(row.GuardianApprovalPending);
    }

    [Fact]
    public async Task The_queue_marks_which_rows_belong_to_the_signed_in_retoucher()
    {
        var mine = AddClient(PortfolioStatus.ReadyForRetoucher, "Mine Model");
        var theirs = AddClient(PortfolioStatus.ReadyForRetoucher, "Theirs Model");
        await _service.StartWorkAsync(mine, _retoucherA);
        await _service.StartWorkAsync(theirs, _retoucherB);

        var rows = await _service.GetQueueAsync(RetoucherQueueTab.InProgress, _retoucherA);

        Assert.True(rows.Single(r => r.ClientId == mine).AssignedToMe);
        Assert.False(rows.Single(r => r.ClientId == theirs).AssignedToMe);
        Assert.Equal("Ann A", rows.Single(r => r.ClientId == mine).AssignedRetoucher);
    }

    // ---------- Handing work over ----------

    /// <summary>
    /// Claimed work belongs to whoever claimed it, so nobody else can open it. Without a
    /// way to move it, a client claimed by mistake — or by somebody who has left — is stuck
    /// with them, and the retoucher who should have it is answered with Access denied.
    /// </summary>
    private void MakeRetoucher(Guid userId)
    {
        var role = _db.Roles.FirstOrDefault(r => r.Name == Roles.Retoucher);

        if (role is null)
        {
            role = new ApplicationRole(Roles.Retoucher) { Id = Guid.CreateVersion7() };
            _db.Roles.Add(role);
        }

        _db.UserRoles.Add(new Microsoft.AspNetCore.Identity.IdentityUserRole<Guid>
        {
            RoleId = role.Id, UserId = userId
        });

        var user = _db.Users.Single(u => u.Id == userId);
        user.IsActive = true;

        _db.SaveChanges();
    }

    [Fact]
    public async Task Work_can_be_passed_to_another_retoucher()
    {
        MakeRetoucher(_retoucherA);
        MakeRetoucher(_retoucherB);

        var clientId = AddClient(PortfolioStatus.ReadyForRetoucher);
        await _service.StartWorkAsync(clientId, _retoucherA);

        Assert.False(await _service.CanOpenAsync(clientId, _retoucherB));

        var (succeeded, error) = await _service.ReassignAsync(clientId, _retoucherB, actingUserId: null);

        Assert.True(succeeded, error);
        Assert.True(await _service.CanOpenAsync(clientId, _retoucherB));
        Assert.False(await _service.CanOpenAsync(clientId, _retoucherA));
    }

    /// <summary>
    /// Somebody now has work they did not claim, so they are told rather than left to
    /// find it.
    /// </summary>
    [Fact]
    public async Task The_new_retoucher_is_told()
    {
        MakeRetoucher(_retoucherA);
        MakeRetoucher(_retoucherB);

        var clientId = AddClient(PortfolioStatus.ReadyForRetoucher);
        await _service.StartWorkAsync(clientId, _retoucherA);
        await _service.ReassignAsync(clientId, _retoucherB, actingUserId: null);

        Assert.Contains(_db.Notifications, n => n.UserId == _retoucherB);
    }

    /// <summary>
    /// Released, it goes back to being work anybody can pick up — which is what an
    /// unclaimed client already looks like, so the row goes rather than being blanked.
    /// </summary>
    [Fact]
    public async Task Work_can_be_released_back_to_the_queue()
    {
        MakeRetoucher(_retoucherA);

        var clientId = AddClient(PortfolioStatus.ReadyForRetoucher);
        await _service.StartWorkAsync(clientId, _retoucherA);

        Assert.True((await _service.ReassignAsync(clientId, null, actingUserId: null)).Succeeded);

        Assert.Empty(_db.RetoucherAssignments.Where(a => a.ClientId == clientId));
        Assert.Equal(PortfolioStatus.ReadyForRetoucher,
            _db.Portfolios.Single(p => p.ClientId == clientId).Status);

        // Anybody can pick it up again, including somebody who never had it.
        Assert.True(await _service.CanOpenAsync(clientId, _retoucherB));
    }

    /// <summary>
    /// A model must not be handed retouching work: they would be given a workspace over
    /// somebody else's photographs.
    /// </summary>
    [Fact]
    public async Task Work_cannot_be_handed_to_somebody_who_is_not_staff()
    {
        MakeRetoucher(_retoucherA);

        var clientId = AddClient(PortfolioStatus.ReadyForRetoucher);
        await _service.StartWorkAsync(clientId, _retoucherA);

        var outsider = _db.ClientProfiles.Single(c => c.Id == clientId).ApplicationUserId;

        var (succeeded, error) = await _service.ReassignAsync(clientId, outsider, actingUserId: null);

        Assert.False(succeeded);
        Assert.NotNull(error);

        // And the person who had it still has it.
        Assert.True(await _service.CanOpenAsync(clientId, _retoucherA));
    }

    /// <summary>
    /// The queue asks the same question the workspace enforces, so a row can say who has
    /// something instead of offering an Open button that answers Access denied.
    /// </summary>
    [Fact]
    public async Task The_queue_says_whether_a_row_can_be_opened()
    {
        MakeRetoucher(_retoucherA);

        var clientId = AddClient(PortfolioStatus.ReadyForRetoucher);
        await _service.StartWorkAsync(clientId, _retoucherA);

        var mine = await _service.GetQueueAsync(RetoucherQueueTab.InProgress, _retoucherA);
        var theirs = await _service.GetQueueAsync(RetoucherQueueTab.InProgress, _retoucherB);

        Assert.True(mine.Single(i => i.ClientId == clientId).CanOpen);
        Assert.False(theirs.Single(i => i.ClientId == clientId).CanOpen);
    }
}
