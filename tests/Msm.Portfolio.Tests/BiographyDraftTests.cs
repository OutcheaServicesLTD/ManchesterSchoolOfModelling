using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Data.Sqlite;
using Msm.Portfolio.Web.Configuration;
using Msm.Portfolio.Web.Data;
using Msm.Portfolio.Web.Domain.Entities;
using Msm.Portfolio.Web.Domain.Enums;
using Msm.Portfolio.Web.Integrations.Bio;
using Msm.Portfolio.Web.Services;

namespace Msm.Portfolio.Tests;

/// <summary>A writer that is switched off, for every test that is not about biographies.</summary>
internal class SilentBiographyWriter : IBiographyWriter
{
    public bool IsEnabled => false;

    public Task<BiographyDraftResult> WriteAsync(
        BiographyFacts facts, CancellationToken cancellationToken = default) =>
        Task.FromResult(new BiographyDraftResult(false, null, "Switched off."));
}

/// <summary>A writer that answers however the test needs, and remembers what it was told.</summary>
internal class FakeBiographyWriter(bool enabled = true, string? text = "A biography.", string? error = null)
    : IBiographyWriter
{
    public bool IsEnabled { get; } = enabled;

    public int Calls { get; private set; }

    public BiographyFacts? LastFacts { get; private set; }

    public Task<BiographyDraftResult> WriteAsync(
        BiographyFacts facts, CancellationToken cancellationToken = default)
    {
        Calls++;
        LastFacts = facts;

        return Task.FromResult(error is null
            ? new BiographyDraftResult(true, text, null)
            : new BiographyDraftResult(false, null, error));
    }
}

/// <summary>
/// Covers the suggested biography: when one is asked for, and what happens to it.
/// </summary>
/// <remarks>
/// The rules worth holding are all about restraint. It runs once, it never overwrites
/// what a person wrote, and it never becomes the public biography without somebody
/// accepting it — this text describes a real individual and is what an agency reads
/// about them.
/// </remarks>
public class BiographyDraftTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _db;
    private readonly Guid _clientId = Guid.CreateVersion7();

    public BiographyDraftTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        _db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();

        var user = new ApplicationUser
        {
            Id = Guid.CreateVersion7(), UserName = "m@example.com", Email = "m@example.com"
        };

        _db.Users.Add(user);
        _db.ClientProfiles.Add(new ClientProfile
        {
            Id = _clientId,
            ApplicationUserId = user.Id,
            FirstName = "Emma",
            LastName = "Johnson",
            Location = "Manchester",
            DateOfBirth = new DateOnly(2000, 1, 1),
            ModelProfileType = ModelProfileType.Female
        });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private BiographyDraftService Service(IBiographyWriter writer) =>
        new(_db,
            writer,
            new MeasurementTemplateProvider(
                new StaticOptionsMonitor<MeasurementTemplateOptions>(new MeasurementTemplateOptions())),
            new AuditService(_db),
            new OptionsWrapper<BiographyOptions>(new BiographyOptions { MaxAttempts = 3 }),
            NullLogger<BiographyDraftService>.Instance);

    private ClientProfile Client() => _db.ClientProfiles.Single(c => c.Id == _clientId);

    // ── When one is asked for ────────────────────────────────────────────────────

    [Fact]
    public void A_draft_is_asked_for_once()
    {
        var client = Client();

        Assert.True(client.RequestBiographyDraft());
        Assert.Equal(BiographyDraftStatus.Pending, client.BiographyDraftStatus);

        // Approving a second time must not queue a second one.
        Assert.False(client.RequestBiographyDraft());
    }

    [Fact]
    public void A_biography_somebody_already_wrote_is_never_replaced()
    {
        var client = Client();
        client.Biography = "Written by the model herself.";

        Assert.False(client.RequestBiographyDraft());
        Assert.Equal(BiographyDraftStatus.NotRequested, client.BiographyDraftStatus);
    }

    [Fact]
    public async Task A_draft_thrown_away_is_not_offered_again()
    {
        var client = Client();
        client.RequestBiographyDraft();
        await _db.SaveChangesAsync();

        await Service(new FakeBiographyWriter()).WritePendingAsync();
        await Service(new FakeBiographyWriter()).ResolveAsync(_clientId, accept: false, null);

        Assert.False(Client().RequestBiographyDraft());
    }

    // ── Writing one ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_written_draft_waits_to_be_read_rather_than_going_live()
    {
        // The whole safety argument: a draft is a suggestion, and the public page keeps
        // showing nothing until a person accepts it.
        var client = Client();
        client.RequestBiographyDraft();
        await _db.SaveChangesAsync();

        var summary = await Service(new FakeBiographyWriter(text: "Emma is based in Manchester.")).WritePendingAsync();

        Assert.Equal(1, summary.Succeeded);

        var written = Client();
        Assert.Equal(BiographyDraftStatus.Ready, written.BiographyDraftStatus);
        Assert.Equal("Emma is based in Manchester.", written.BiographyDraft);
        Assert.Null(written.Biography);
    }

    [Fact]
    public async Task Accepting_a_draft_makes_it_the_biography()
    {
        var client = Client();
        client.RequestBiographyDraft();
        await _db.SaveChangesAsync();
        await Service(new FakeBiographyWriter(text: "Emma is based in Manchester.")).WritePendingAsync();

        Assert.True(await Service(new FakeBiographyWriter()).ResolveAsync(_clientId, accept: true, null));

        var accepted = Client();
        Assert.Equal("Emma is based in Manchester.", accepted.Biography);
        Assert.Equal(BiographyDraftStatus.Closed, accepted.BiographyDraftStatus);
        Assert.Null(accepted.BiographyDraft);
    }

    [Fact]
    public async Task Discarding_a_draft_leaves_the_biography_alone()
    {
        var client = Client();
        client.RequestBiographyDraft();
        await _db.SaveChangesAsync();
        await Service(new FakeBiographyWriter()).WritePendingAsync();

        await Service(new FakeBiographyWriter()).ResolveAsync(_clientId, accept: false, null);

        var discarded = Client();
        Assert.Null(discarded.Biography);
        Assert.Null(discarded.BiographyDraft);
        Assert.Equal(BiographyDraftStatus.Closed, discarded.BiographyDraftStatus);
    }

    [Fact]
    public async Task Only_the_facts_the_studio_holds_are_sent()
    {
        // The client record also holds an email address, a telephone number, a date of
        // birth and a CRM identifier. None of them belong in a request to anybody's API.
        var client = Client();
        client.RequestBiographyDraft();
        await _db.SaveChangesAsync();

        var writer = new FakeBiographyWriter();
        await Service(writer).WritePendingAsync();

        var facts = writer.LastFacts;
        Assert.NotNull(facts);
        Assert.Equal("Emma Johnson", facts.Name);
        Assert.Equal("Manchester", facts.Location);

        // An age, not a date of birth.
        Assert.NotNull(facts.Age);

        var sent = System.Text.Json.JsonSerializer.Serialize(facts);
        Assert.DoesNotContain("example.com", sent);
        Assert.DoesNotContain("2000-01-01", sent);
    }

    // ── When it goes wrong ───────────────────────────────────────────────────────

    [Fact]
    public async Task A_failure_is_retried_and_then_left_alone()
    {
        var client = Client();
        client.RequestBiographyDraft();
        await _db.SaveChangesAsync();

        var writer = new FakeBiographyWriter(error: "The provider is unavailable.");

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            // Clear the backoff so the test does not have to wait it out.
            var pending = Client();
            pending.BiographyDraftNextAttemptAt = null;
            await _db.SaveChangesAsync();

            await Service(writer).WritePendingAsync();
        }

        var failed = Client();
        Assert.Equal(BiographyDraftStatus.Failed, failed.BiographyDraftStatus);
        Assert.Equal("The provider is unavailable.", failed.BiographyDraftError);
        Assert.Equal(3, writer.Calls);

        // And it stays given up on rather than being picked up again for ever.
        await Service(writer).WritePendingAsync();
        Assert.Equal(3, writer.Calls);
    }

    [Fact]
    public async Task A_failed_draft_never_touches_the_biography()
    {
        var client = Client();
        client.Biography = null;
        client.RequestBiographyDraft();
        await _db.SaveChangesAsync();

        await Service(new FakeBiographyWriter(error: "Refused.")).WritePendingAsync();

        Assert.Null(Client().Biography);
    }

    [Fact]
    public async Task Nothing_is_written_when_nothing_was_asked_for()
    {
        var writer = new FakeBiographyWriter();

        var summary = await Service(writer).WritePendingAsync();

        Assert.Equal(0, summary.Total);
        Assert.Equal(0, writer.Calls);
    }

    [Fact]
    public async Task A_draft_that_was_never_written_cannot_be_accepted()
    {
        Assert.False(await Service(new FakeBiographyWriter()).ResolveAsync(_clientId, accept: true, null));
        Assert.Null(Client().Biography);
    }
}
