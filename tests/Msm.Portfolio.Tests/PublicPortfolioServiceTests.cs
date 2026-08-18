using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Msm.Portfolio.Web.Configuration;
using Msm.Portfolio.Web.Data;
using Msm.Portfolio.Web.Domain.Entities;
using Msm.Portfolio.Web.Domain.Enums;
using Msm.Portfolio.Web.Services;

namespace Msm.Portfolio.Tests;

/// <summary>
/// The public surface is reachable by anyone, so these tests concentrate on what must
/// not appear on it.
/// </summary>
public class PublicPortfolioServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _db;
    private readonly PublicPortfolioService _service;

    public PublicPortfolioServiceTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();

        _service = new PublicPortfolioService(
            _db,
            new MeasurementTemplateProvider(
                new StaticOptionsMonitor<MeasurementTemplateOptions>(new MeasurementTemplateOptions())),
            new NotificationService(_db),
            NullLogger<PublicPortfolioService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private Guid AddModel(
        string name = "Emma Johnson",
        string? slug = "emma-johnson",
        bool published = true,
        bool onBoard = true,
        int selectedImages = 3,
        int unselectedImages = 2,
        bool withSelfTape = false,
        string? location = "Manchester")
    {
        var userId = Guid.CreateVersion7();
        var clientId = Guid.CreateVersion7();
        var parts = name.Split(' ');

        _db.Users.Add(new ApplicationUser
        {
            Id = userId,
            UserName = $"{clientId:N}@private.example",
            Email = $"{clientId:N}@private.example",
            PhoneNumber = "07700900999"
        });

        _db.ClientProfiles.Add(new ClientProfile
        {
            Id = clientId,
            ApplicationUserId = userId,
            FirstName = parts[0],
            LastName = parts.Length > 1 ? parts[1] : "Model",
            Location = location,
            DateOfBirth = new DateOnly(2000, 1, 1),
            ModelProfileType = ModelProfileType.Female,
            Biography = "A biography.",
            GhlContactId = $"ghl-{clientId:N}",
            InstagramUrl = "https://instagram.com/example"
        });

        var portfolio = new Msm.Portfolio.Web.Domain.Entities.Portfolio
        {
            ClientId = clientId,
            Slug = slug,
            IsPublished = published,
            IsVisibleOnModelBoard = onBoard,
            PublishedAt = published ? DateTimeOffset.UtcNow : null,
            Status = published ? PortfolioStatus.Published : PortfolioStatus.InViewing
        };

        _db.Portfolios.Add(portfolio);
        _db.SaveChanges();

        MediaAsset? first = null;
        var order = 0;

        for (var i = 0; i < selectedImages + unselectedImages; i++)
        {
            var asset = new MediaAsset
            {
                ClientId = clientId,
                StorageKey = $"clients/{clientId:N}/{Guid.NewGuid():N}/original.jpg",
                OriginalFilename = $"shot{i}.jpg",
                MimeType = "image/jpeg",
                FileSize = 100,
                MediaType = MediaType.Image,
                Orientation = i % 2 == 0 ? MediaOrientation.Portrait : MediaOrientation.Landscape,
                IsSelectedForPortfolio = i < selectedImages,
                DisplayOrder = order++
            };

            _db.MediaAssets.Add(asset);
            first ??= asset;
        }

        if (withSelfTape)
        {
            _db.MediaAssets.Add(new MediaAsset
            {
                ClientId = clientId,
                StorageKey = $"clients/{clientId:N}/{Guid.NewGuid():N}/tape.mp4",
                OriginalFilename = "tape.mp4",
                MimeType = "video/mp4",
                FileSize = 500,
                MediaType = MediaType.SelfTape
            });
        }

        _db.ModelMeasurements.Add(new ModelMeasurement
        {
            ClientId = clientId,
            MeasurementType = "Height",
            Value = "175",
            Unit = MeasurementUnit.Centimetres,
            DisplayOrder = 1
        });

        _db.SaveChanges();

        if (first is not null && selectedImages > 0)
        {
            first.IsFeatured = true;
            portfolio.FeaturedMediaId = first.Id;
            _db.SaveChanges();
        }

        return clientId;
    }

    [Fact]
    public async Task A_published_portfolio_is_served_at_its_slug()
    {
        AddModel();

        var portfolio = await _service.GetBySlugAsync("emma-johnson");

        Assert.NotNull(portfolio);
        Assert.Equal("Emma Johnson", portfolio!.Name);
        Assert.Equal("Manchester", portfolio.Location);
    }

    [Fact]
    public async Task An_unpublished_portfolio_is_not_served_even_with_a_valid_slug()
    {
        AddModel(published: false);

        Assert.Null(await _service.GetBySlugAsync("emma-johnson"));
    }

    [Fact]
    public async Task An_unknown_slug_returns_nothing()
    {
        AddModel();

        Assert.Null(await _service.GetBySlugAsync("someone-else"));
        Assert.Null(await _service.GetBySlugAsync(""));
        Assert.Null(await _service.GetBySlugAsync("   "));
    }

    /// <summary>
    /// The pool holds up to 60 images and at most 30 are public. The unselected
    /// remainder must never reach the public page (specification section 12).
    /// </summary>
    [Fact]
    public async Task Only_selected_images_appear_publicly()
    {
        AddModel(selectedImages: 3, unselectedImages: 4);

        var portfolio = await _service.GetBySlugAsync("emma-johnson");

        Assert.Equal(3, portfolio!.Images.Count);
    }

    /// <summary>
    /// The client record carries an email, telephone, CRM identifier and guardian
    /// details. None of them belong on a public page (specification sections 10 and 46).
    /// </summary>
    [Fact]
    public async Task The_cover_crop_reaches_the_portfolio_page_and_the_board()
    {
        // The hero band and the board card are the only places a photograph is cut, and
        // both have to cut it in the same place.
        var clientId = AddModel();
        var cover = await _db.MediaAssets.SingleAsync(m => m.ClientId == clientId && m.IsFeatured);
        cover.FocalPointX = 30;
        cover.FocalPointY = 9;
        await _db.SaveChangesAsync();

        var portfolio = await _service.GetBySlugAsync("emma-johnson");
        var board = await _service.GetModelBoardAsync();

        Assert.Equal("30% 9%", portfolio!.CoverFocus!.AsCss);
        Assert.Equal("30% 9%", board.Single().CoverFocus!.AsCss);
    }

    [Fact]
    public async Task No_cover_crop_leaves_each_place_to_its_own_default()
    {
        // Null rather than 50/50 on purpose: the hero frames above centre because a face
        // usually is, and reporting a middle here would quietly override that.
        AddModel();

        var portfolio = await _service.GetBySlugAsync("emma-johnson");
        var board = await _service.GetModelBoardAsync();

        Assert.Null(portfolio!.CoverFocus);
        Assert.Null(board.Single().CoverFocus);
    }

    [Fact]
    public async Task A_crop_on_a_photograph_that_is_not_the_cover_is_not_used()
    {
        // The crop belongs to the photograph, but only the cover is ever cropped.
        var clientId = AddModel();
        var other = await _db.MediaAssets
            .FirstAsync(m => m.ClientId == clientId && !m.IsFeatured && m.IsSelectedForPortfolio);
        other.FocalPointX = 10;
        other.FocalPointY = 90;
        await _db.SaveChangesAsync();

        var portfolio = await _service.GetBySlugAsync("emma-johnson");

        Assert.Null(portfolio!.CoverFocus);
    }

    [Fact]
    public async Task The_public_projection_carries_no_private_contact_details()
    {
        AddModel();

        var portfolio = await _service.GetBySlugAsync("emma-johnson");
        var serialised = System.Text.Json.JsonSerializer.Serialize(portfolio);

        Assert.DoesNotContain("private.example", serialised);
        Assert.DoesNotContain("07700900999", serialised);
        Assert.DoesNotContain("ghl-", serialised);
    }

    [Fact]
    public async Task Measurements_are_labelled_from_the_template()
    {
        AddModel();

        var portfolio = await _service.GetBySlugAsync("emma-johnson");

        var height = portfolio!.Measurements.Single();
        Assert.Equal("Height", height.Label);
        Assert.Equal("175", height.Value);
        Assert.Equal("cm", height.Unit);
    }

    [Fact]
    public async Task A_self_tape_appears_only_when_one_exists()
    {
        AddModel(slug: "with-tape", withSelfTape: true);
        AddModel(name: "No Tape", slug: "no-tape", withSelfTape: false);

        Assert.True((await _service.GetBySlugAsync("with-tape"))!.HasSelfTape);
        Assert.False((await _service.GetBySlugAsync("no-tape"))!.HasSelfTape);
    }

    [Fact]
    public async Task A_deleted_image_does_not_appear_publicly()
    {
        var clientId = AddModel(selectedImages: 3, unselectedImages: 0);

        var asset = _db.MediaAssets.First(m => m.ClientId == clientId && m.IsSelectedForPortfolio);
        asset.IsDeleted = true;
        await _db.SaveChangesAsync();

        Assert.Equal(2, (await _service.GetBySlugAsync("emma-johnson"))!.Images.Count);
    }

    // ---------- Model board ----------

    [Fact]
    public async Task The_model_board_lists_published_models()
    {
        AddModel(name: "Emma Johnson", slug: "emma-johnson");
        AddModel(name: "Sara Smith", slug: "sara-smith");

        var board = await _service.GetModelBoardAsync();

        Assert.Equal(2, board.Count);
    }

    /// <summary>
    /// Specification section 47: the board is queried from published portfolios, so
    /// unpublishing removes a model with no separate step.
    /// </summary>
    [Fact]
    public async Task Unpublishing_removes_a_model_from_the_board()
    {
        var clientId = AddModel();
        Assert.Single(await _service.GetModelBoardAsync());

        _db.Portfolios.Single(p => p.ClientId == clientId).IsPublished = false;
        await _db.SaveChangesAsync();

        Assert.Empty(await _service.GetModelBoardAsync());
    }

    [Fact]
    public async Task A_model_who_opted_out_of_the_board_is_not_listed_but_keeps_their_portfolio()
    {
        AddModel(onBoard: false);

        Assert.Empty(await _service.GetModelBoardAsync());
        Assert.NotNull(await _service.GetBySlugAsync("emma-johnson"));
    }

    /// <summary>A card with no image would render as an empty tile.</summary>
    [Fact]
    public async Task A_model_with_no_featured_image_is_not_on_the_board()
    {
        AddModel(selectedImages: 0, unselectedImages: 2);

        Assert.Empty(await _service.GetModelBoardAsync());
    }

    // ---------- Enquiries ----------

    [Fact]
    public async Task An_enquiry_about_a_published_model_is_recorded()
    {
        var clientId = AddModel();

        var recorded = await _service.RecordEnquiryAsync(
            clientId, "Agency Scout", "Big Agency", "scout@agency.example", null, "We would like to meet.");

        Assert.True(recorded);

        var enquiry = _db.Enquiries.Single();
        Assert.Equal(clientId, enquiry.ClientId);
        Assert.Equal("scout@agency.example", enquiry.Email);
        Assert.False(enquiry.IsHandled);
    }

    /// <summary>
    /// An enquiry must only be possible against a portfolio that is genuinely public,
    /// even if the client id is supplied directly.
    /// </summary>
    [Fact]
    public async Task An_enquiry_about_an_unpublished_model_is_refused()
    {
        var clientId = AddModel(published: false);

        var recorded = await _service.RecordEnquiryAsync(
            clientId, "Scout", null, "scout@agency.example", null, "Hello.");

        Assert.False(recorded);
        Assert.Empty(_db.Enquiries);
    }

    [Fact]
    public async Task An_enquiry_about_an_unknown_client_is_refused()
    {
        Assert.False(await _service.RecordEnquiryAsync(
            Guid.CreateVersion7(), "Scout", null, "scout@agency.example", null, "Hello."));
    }

    /// <summary>
    /// The enquiry belongs to MSM. The model is not notified and their own contact
    /// details are not involved (specification section 46).
    /// </summary>
    [Fact]
    public async Task An_enquiry_notifies_staff_and_not_the_model()
    {
        var clientId = AddModel();
        var client = _db.ClientProfiles.Single(c => c.Id == clientId);

        var adminRole = new ApplicationRole(Msm.Portfolio.Web.Authorization.Roles.Admin) { Id = Guid.CreateVersion7() };
        var admin = new ApplicationUser { Id = Guid.CreateVersion7(), UserName = "a@msm.local", Email = "a@msm.local" };
        _db.Roles.Add(adminRole);
        _db.Users.Add(admin);
        _db.UserRoles.Add(new Microsoft.AspNetCore.Identity.IdentityUserRole<Guid>
        {
            RoleId = adminRole.Id, UserId = admin.Id
        });
        await _db.SaveChangesAsync();

        await _service.RecordEnquiryAsync(clientId, "Scout", null, "scout@agency.example", null, "Hello.");

        Assert.Contains(_db.Notifications, n => n.UserId == admin.Id && n.Type == NotificationTypes.EnquiryReceived);
        Assert.DoesNotContain(_db.Notifications, n => n.UserId == client.ApplicationUserId);
    }
}
