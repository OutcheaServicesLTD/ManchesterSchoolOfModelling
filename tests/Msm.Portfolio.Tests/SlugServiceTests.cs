using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Msm.Portfolio.Web.Data;
using Msm.Portfolio.Web.Domain.Entities;
using Msm.Portfolio.Web.Services;

namespace Msm.Portfolio.Tests;

public class SlugServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _db;
    private readonly SlugService _service;

    public SlugServiceTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _service = new SlugService(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private Guid AddPortfolioWithSlug(string slug)
    {
        var userId = Guid.CreateVersion7();
        var clientId = Guid.CreateVersion7();

        _db.Users.Add(new ApplicationUser { Id = userId, UserName = $"{clientId:N}@x.com", Email = $"{clientId:N}@x.com" });
        _db.ClientProfiles.Add(new ClientProfile { Id = clientId, ApplicationUserId = userId, FirstName = "A", LastName = "B" });

        var portfolio = new Msm.Portfolio.Web.Domain.Entities.Portfolio { ClientId = clientId, Slug = slug };
        _db.Portfolios.Add(portfolio);
        _db.SaveChanges();

        return portfolio.Id;
    }

    [Theory]
    [InlineData("Emma Johnson", "emma-johnson")]
    [InlineData("  Emma   Johnson  ", "emma-johnson")]
    [InlineData("Emma-Johnson", "emma-johnson")]
    [InlineData("O'Brien", "o-brien")]
    [InlineData("Anne Marie Smith", "anne-marie-smith")]
    [InlineData("Model 123", "model-123")]
    public void Names_become_readable_url_segments(string input, string expected)
    {
        Assert.Equal(expected, SlugService.Slugify(input));
    }

    /// <summary>
    /// Model names frequently carry diacritics, and the URL has to stay recognisable
    /// as the person's name rather than collapsing to initials.
    /// </summary>
    [Theory]
    [InlineData("Zoë Müller", "zoe-muller")]
    [InlineData("Renée Dupont", "renee-dupont")]
    [InlineData("Søren Åberg", "s-ren-aberg")]
    public void Accented_names_are_transliterated_rather_than_dropped(string input, string expected)
    {
        Assert.Equal(expected, SlugService.Slugify(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!")]
    [InlineData("的")]
    public void Names_that_cannot_be_transliterated_produce_an_empty_slug(string input)
    {
        Assert.Equal(string.Empty, SlugService.Slugify(input));
    }

    [Fact]
    public async Task A_name_with_no_usable_characters_still_gets_a_valid_slug()
    {
        // An empty slug could not be a URL, so a fallback is used.
        var slug = await _service.GenerateUniqueAsync("的", Guid.CreateVersion7());

        Assert.False(string.IsNullOrWhiteSpace(slug));
        Assert.StartsWith("model", slug);
    }

    [Fact]
    public async Task A_duplicate_name_gets_a_numbered_slug()
    {
        AddPortfolioWithSlug("emma-johnson");

        var slug = await _service.GenerateUniqueAsync("Emma Johnson", Guid.CreateVersion7());

        Assert.Equal("emma-johnson-2", slug);
    }

    [Fact]
    public async Task Numbering_continues_past_the_second_duplicate()
    {
        AddPortfolioWithSlug("emma-johnson");
        AddPortfolioWithSlug("emma-johnson-2");
        AddPortfolioWithSlug("emma-johnson-3");

        Assert.Equal("emma-johnson-4", await _service.GenerateUniqueAsync("Emma Johnson", Guid.CreateVersion7()));
    }

    [Fact]
    public async Task A_portfolio_does_not_collide_with_its_own_slug()
    {
        var portfolioId = AddPortfolioWithSlug("emma-johnson");

        Assert.True(await _service.IsAvailableAsync("emma-johnson", portfolioId));
    }

    /// <summary>
    /// Public portfolios are served from the site root as /{slug}, so a model named
    /// "Admin" would otherwise shadow the admin area.
    /// </summary>
    [Theory]
    [InlineData("Admin")]
    [InlineData("client")]
    [InlineData("Retoucher")]
    [InlineData("media")]
    [InlineData("Checkout")]
    [InlineData("account")]
    public async Task Reserved_route_names_cannot_be_taken_as_a_slug(string name)
    {
        Assert.False(await _service.IsAvailableAsync(SlugService.Slugify(name), Guid.CreateVersion7()));

        var generated = await _service.GenerateUniqueAsync(name, Guid.CreateVersion7());

        Assert.NotEqual(SlugService.Slugify(name), generated);
        Assert.EndsWith("-model", generated);
    }

    [Fact]
    public async Task An_empty_or_whitespace_slug_is_never_available()
    {
        Assert.False(await _service.IsAvailableAsync("", Guid.CreateVersion7()));
        Assert.False(await _service.IsAvailableAsync("   ", Guid.CreateVersion7()));
    }
}
