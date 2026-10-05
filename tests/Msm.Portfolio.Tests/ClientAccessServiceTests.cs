using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Msm.Portfolio.Web.Configuration;
using Msm.Portfolio.Web.Data;
using Msm.Portfolio.Web.Domain.Entities;
using Msm.Portfolio.Web.Domain.Enums;
using Msm.Portfolio.Web.Services;

namespace Msm.Portfolio.Tests;

/// <summary>
/// A model's way in to their own dashboard. Onboarding leaves the account without a
/// password on purpose, so this is the only thing that grants one.
/// </summary>
public class ClientAccessServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;
    private readonly IServiceScope _scope;
    private readonly ApplicationDbContext _db;
    private readonly UserManager<ApplicationUser> _users;
    private readonly ClientAccessService _service;
    private readonly RecordingSender _email = new();

    public ClientAccessServiceTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddDbContext<ApplicationDbContext>(o => o.UseSqlite(_connection));
        services.AddIdentityCore<ApplicationUser>(o =>
            {
                o.Password.RequiredLength = 10;
                o.Password.RequireNonAlphanumeric = true;
                o.User.RequireUniqueEmail = true;
            })
            .AddRoles<ApplicationRole>()
            .AddEntityFrameworkStores<ApplicationDbContext>()
            .AddDefaultTokenProviders();

        _provider = services.BuildServiceProvider();
        _scope = _provider.CreateScope();
        _db = _scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        _db.Database.EnsureCreated();
        _users = _scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        _service = new ClientAccessService(
            _db, _users, new AuditService(_db), _email,
            Microsoft.Extensions.Options.Options.Create(new MsmBrandOptions()),
            NullLogger<ClientAccessService>.Instance);
    }

    public void Dispose()
    {
        _scope.Dispose();
        _provider.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Mirrors onboarding: an account, and no password on it.</summary>
    private async Task<Guid> AddClientAsync(string email = "model@example.com")
    {
        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            FirstName = "Emma",
            LastName = "Johnson",
            IsActive = true
        };

        Assert.True((await _users.CreateAsync(user)).Succeeded);

        var client = new ClientProfile
        {
            ApplicationUserId = user.Id,
            FirstName = "Emma",
            LastName = "Johnson",
            DateOfBirth = new DateOnly(2000, 1, 1),
            ModelProfileType = ModelProfileType.Female
        };

        _db.ClientProfiles.Add(client);
        await _db.SaveChangesAsync();

        return client.Id;
    }

    [Fact]
    public async Task A_model_starts_with_no_way_in()
    {
        var clientId = await AddClientAsync();

        Assert.False(await _service.HasSignInDetailsAsync(clientId));
    }

    [Fact]
    public async Task Issuing_details_sets_a_password_that_actually_signs_in()
    {
        var clientId = await AddClientAsync();

        var result = await _service.IssueSignInDetailsAsync(clientId, actingUserId: null);

        Assert.True(result.Succeeded);
        Assert.Equal("model@example.com", result.Email);
        Assert.NotNull(result.Password);

        // The password handed over is the one the account will accept — the whole point,
        // and the thing a generated password gets wrong when it is stored unhashed or
        // trimmed somewhere on the way.
        var user = await _users.FindByEmailAsync("model@example.com");
        Assert.True(await _users.CheckPasswordAsync(user!, result.Password!));
        Assert.True(await _service.HasSignInDetailsAsync(clientId));
    }

    [Fact]
    public async Task Issuing_again_replaces_the_password_they_had()
    {
        var clientId = await AddClientAsync();

        var first = await _service.IssueSignInDetailsAsync(clientId, actingUserId: null);
        var second = await _service.IssueSignInDetailsAsync(clientId, actingUserId: null);

        Assert.True(second.Succeeded);
        Assert.NotEqual(first.Password, second.Password);

        var user = await _users.FindByEmailAsync("model@example.com");
        Assert.False(await _users.CheckPasswordAsync(user!, first.Password!));
        Assert.True(await _users.CheckPasswordAsync(user!, second.Password!));
    }

    /// <summary>
    /// The trail records that access was granted and by whom. It must never record what
    /// was granted: an audit log holding passwords is a list of live credentials for
    /// every model on the system.
    /// </summary>
    [Fact]
    public async Task The_audit_trail_records_the_grant_and_not_the_password()
    {
        var clientId = await AddClientAsync();

        var result = await _service.IssueSignInDetailsAsync(clientId, actingUserId: null);

        var entry = _db.AuditLogs.Single();
        Assert.Equal(AuditActions.ClientAccessIssued, entry.Action);
        Assert.Equal("model@example.com", entry.NewValue);

        var serialised = System.Text.Json.JsonSerializer.Serialize(_db.AuditLogs.ToList());
        Assert.DoesNotContain(result.Password!, serialised);
    }

    [Fact]
    public async Task An_unknown_model_is_refused()
    {
        var result = await _service.IssueSignInDetailsAsync(Guid.CreateVersion7(), actingUserId: null);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Error);
    }

    /// <summary>
    /// Every generated password has to satisfy the rules Identity is configured with, or
    /// the button fails in front of whoever pressed it.
    /// </summary>
    [Fact]
    public void A_generated_password_meets_the_complexity_rules()
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var password = PasswordGenerator.Create();

            Assert.Equal(14, password.Length);
            Assert.Contains(password, char.IsUpper);
            Assert.Contains(password, char.IsLower);
            Assert.Contains(password, char.IsDigit);
            Assert.Contains(password, c => !char.IsLetterOrDigit(c));

            // Nothing that gets misread when it is spelled out down a telephone.
            Assert.DoesNotContain(password, c => "Il1O0".Contains(c));
        }
    }

    /// <summary>
    /// The details go to the model as well as to the screen, so a password does not have to
    /// be read down a telephone.
    /// </summary>
    [Fact]
    public async Task The_details_are_emailed_to_the_model()
    {
        var clientId = await AddClientAsync();

        var result = await _service.IssueSignInDetailsAsync(clientId, actingUserId: null);

        Assert.True(result.Delivered);

        var sent = _email.Sent.Single();
        Assert.Equal("model@example.com", sent.To);
        Assert.Contains(result.Password!, sent.Body);
        Assert.Contains("/account/login", sent.Body);
    }

    /// <summary>
    /// With no provider the message cannot go, and the model must still be able to get in:
    /// the password is set and shown on screen, and the page says it was not sent.
    /// </summary>
    [Fact]
    public async Task A_provider_that_fails_does_not_stop_the_account_working()
    {
        var clientId = await AddClientAsync();
        _email.Throw = true;

        var result = await _service.IssueSignInDetailsAsync(clientId, actingUserId: null);

        Assert.True(result.Succeeded);
        Assert.False(result.Delivered);
        Assert.NotNull(result.Password);

        var user = await _users.FindByEmailAsync("model@example.com");
        Assert.True(await _users.CheckPasswordAsync(user!, result.Password!));
    }

    private sealed class RecordingSender : IEmailSender
    {
        public List<(string To, string Subject, string Body)> Sent { get; } = [];

        public bool Throw { get; set; }

        public Task<bool> SendAsync(
            string toEmail, string subject, string body, CancellationToken cancellationToken = default)
        {
            if (Throw)
            {
                throw new InvalidOperationException("No provider.");
            }

            Sent.Add((toEmail, subject, body));
            return Task.FromResult(true);
        }
    }
}
