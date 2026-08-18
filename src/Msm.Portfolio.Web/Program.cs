using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Msm.Portfolio.Web.Authorization;
using Msm.Portfolio.Web.Configuration;
using Msm.Portfolio.Web.Data;
using Msm.Portfolio.Web.Domain.Entities;
using Msm.Portfolio.Web.Services;
using Msm.Portfolio.Web.Integrations.GoCardless;
using Msm.Portfolio.Web.Integrations.Bio;
using Msm.Portfolio.Web.Integrations.HighLevel;
using Msm.Portfolio.Web.Storage;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<MediaOptions>(builder.Configuration.GetSection(MediaOptions.SectionName));
builder.Services.Configure<CommerceOptions>(builder.Configuration.GetSection(CommerceOptions.SectionName));
builder.Services.Configure<MsmBrandOptions>(builder.Configuration.GetSection(MsmBrandOptions.SectionName));
builder.Services.Configure<IntegrationOptions>(builder.Configuration.GetSection(IntegrationOptions.SectionName));
builder.Services.Configure<GuardianConsentOptions>(builder.Configuration.GetSection(GuardianConsentOptions.SectionName));
builder.Services.Configure<MeasurementTemplateOptions>(builder.Configuration.GetSection(MeasurementTemplateOptions.SectionName));

builder.Services.AddApplicationData(builder.Configuration);

// Keys are shared through the database so that a deployment does not sign every member
// of staff out, and so two instances can read each other's cookies and anti-forgery
// tokens. The application name is fixed rather than derived from the assembly, because a
// changed name silently produces a different key ring.
builder.Services
    .AddDataProtection()
    .SetApplicationName("Msm.Portfolio")
    .PersistKeysToDbContext<ApplicationDbContext>();

builder.Services
    .AddIdentity<ApplicationUser, ApplicationRole>(options =>
    {
        options.User.RequireUniqueEmail = true;
        options.SignIn.RequireConfirmedAccount = false;

        options.Password.RequiredLength = 10;
        options.Password.RequireDigit = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireNonAlphanumeric = true;

        // Staff accounts are a route to every client's media, so brute force is
        // throttled rather than merely discouraged.
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    })
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/account/login";
    options.LogoutPath = "/account/logout";
    options.AccessDeniedPath = "/account/denied";
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
});

builder.Services.AddMsmAuthorization();
builder.Services.AddScoped<DbSeeder>();

// Invented clients for a preview deployment. Refuses to run unless explicitly asked,
// outside Development, or when any client already exists.
builder.Services.AddScoped<DemoDataSeeder>();

builder.Services.AddSingleton<IMeasurementTemplateProvider, MeasurementTemplateProvider>();
builder.Services.AddScoped<IAuditService, AuditService>();
builder.Services.AddScoped<INotificationService, NotificationService>();
builder.Services.AddScoped<IClientProfileAccessor, ClientProfileAccessor>();
builder.Services.AddScoped<IGuardianConsentService, GuardianConsentService>();
builder.Services.AddScoped<IClientOnboardingService, ClientOnboardingService>();
builder.Services.AddScoped<IMediaService, MediaService>();
builder.Services.AddScoped<IRetoucherService, RetoucherService>();
builder.Services.AddScoped<ISlugService, SlugService>();
builder.Services.AddScoped<IPortfolioService, PortfolioService>();
builder.Services.AddScoped<IAdminService, AdminService>();
builder.Services.AddScoped<IPublicPortfolioService, PublicPortfolioService>();
builder.Services.AddScoped<IStaffService, StaffService>();
builder.Services.AddScoped<ICheckoutService, CheckoutService>();
builder.Services.AddScoped<IPaymentWebhookProcessor, PaymentWebhookProcessor>();
builder.Services.AddScoped<IMaintenanceService, MaintenanceService>();

// A grace period expires by the passage of time, so nothing in a request can be
// relied on to notice it (specification section 23).
builder.Services.AddHostedService<MaintenanceGracePeriodWorker>();

builder.Services.AddScoped<ICrmSyncService, CrmSyncService>();

// The CRM push runs on a worker, never inside the operation that caused it, so a CRM
// that is down cannot delay the studio or roll back a purchase (specification section 45).
builder.Services.AddHostedService<CrmSyncWorker>();

// ── Suggested biographies ────────────────────────────────────────────────────────
// Off unless a key is configured, and off means nothing is requested at all rather
// than a queue of drafts that can only fail.
builder.Services.Configure<BiographyOptions>(
    builder.Configuration.GetSection(BiographyOptions.SectionName));

var biographyKey = builder.Configuration[$"{BiographyOptions.SectionName}:ApiKey"];

if (string.IsNullOrWhiteSpace(biographyKey))
{
    builder.Services.AddSingleton<IBiographyWriter, StubBiographyWriter>();
}
else
{
    builder.Services.AddSingleton<IBiographyWriter, AnthropicBiographyWriter>();
}

builder.Services.AddScoped<IBiographyDraftService, BiographyDraftService>();

// Written on a worker for the same reason the CRM push is: approving a portfolio is
// the administrator's action and must not wait on somebody else's API.
builder.Services.AddHostedService<BiographyDraftWorker>();

// As with GoCardless, the real client is only used when configured, and its HTTP calls
// have not been verified against the provider.
var highLevelKey = builder.Configuration[$"{IntegrationOptions.SectionName}:HighLevel:ApiKey"];

if (string.IsNullOrWhiteSpace(highLevelKey))
{
    builder.Services.AddScoped<IHighLevelService, StubHighLevelService>();
}
else
{
    builder.Services.AddHttpClient<IHighLevelService, HighLevelService>()
        .SetHandlerLifetime(TimeSpan.FromMinutes(5));
}
builder.Services.AddScoped<IWebhookVerifier, GoCardlessWebhookVerifier>();

// The real GoCardless client is used only when an access token is configured. Its HTTP
// calls have not been verified against the provider's sandbox, so the stub stays the
// default: a half-right payment client is worse than an obvious placeholder.
var goCardlessToken = builder.Configuration[$"{IntegrationOptions.SectionName}:GoCardless:AccessToken"];

if (string.IsNullOrWhiteSpace(goCardlessToken))
{
    builder.Services.AddScoped<IGoCardlessService, StubGoCardlessService>();
}
else
{
    builder.Services.AddHttpClient<IGoCardlessService, GoCardlessService>()
        .SetHandlerLifetime(TimeSpan.FromMinutes(5));
}
builder.Services.AddSingleton<IImageProcessor, ImageProcessor>();

// Local disk for now. Object storage replaces this registration once MSM's hosting is
// decided; nothing above the interface changes (specification section 33).
builder.Services.AddSingleton<IMediaStorageService, LocalDiskMediaStorageService>();

// Logs messages rather than delivering them. Client-facing messaging is intended to run
// through GoHighLevel automation in Phase 9; this keeps the guardian workflow complete
// until then.
builder.Services.AddScoped<IEmailSender, LoggingEmailSender>();

builder.Services.AddControllersWithViews(options =>
{
    // Anti-forgery on every state-changing request by default (specification section 43).
    options.Filters.Add(new Microsoft.AspNetCore.Mvc.AutoValidateAntiforgeryTokenAttribute());
});

// The retoucher's uploader posts each file with fetch/XHR rather than a form, so the
// token has to be accepted from a header as well as a form field.
builder.Services.AddAntiforgery(options => options.HeaderName = "RequestVerificationToken");

builder.Services.AddMsmRateLimiting();

// Probed by the deployment platform to decide whether an instance is serving.
builder.Services.AddHealthChecks()
    .AddDbContextCheck<ApplicationDbContext>("database");

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler("/error");
    app.UseHsts();
}

var brand = app.Services.GetRequiredService<IOptions<MsmBrandOptions>>().Value;

app.UseMsmSecurityHeaders(app.Environment.IsDevelopment(), brand.DiscourageSearchEngines);

// A preview deployment also serves a robots file saying the same thing, because some
// crawlers read it and never look at the response headers.
if (brand.DiscourageSearchEngines)
{
    app.MapGet("/robots.txt", () => Results.Text(
        "User-agent: *\nDisallow: /\n", "text/plain")).AllowAnonymous();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "areas",
    pattern: "{area:exists}/{controller=Home}/{action=Index}/{id?}");

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

// Anonymous and unthrottled: a probe that could be rate limited would report a healthy
// instance as unhealthy under load and take it out of rotation.
app.MapHealthChecks("/health").AllowAnonymous().DisableRateLimiting();

CheckProductionReadiness(app);

await InitialiseDatabaseAsync(app);

app.Run();

/// <summary>
/// Refuses to start a production deployment that still has development stand-ins in
/// place, rather than letting it fail quietly and expensively later.
/// </summary>
static void CheckProductionReadiness(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var services = scope.ServiceProvider;
    var logger = services.GetRequiredService<ILogger<Program>>();

    if (app.Configuration.GetValue<bool>("ALLOW_INCOMPLETE_DEPLOYMENT"))
    {
        logger.LogWarning(
            "ALLOW_INCOMPLETE_DEPLOYMENT is set, so readiness checks are being skipped.");
        return;
    }

    var problems = ProductionReadiness.Check(
        app.Configuration,
        services.GetRequiredService<IGoCardlessService>(),
        services.GetRequiredService<IHighLevelService>(),
        emailSenderIsStub: services.GetRequiredService<IEmailSender>() is LoggingEmailSender,
        mediaStorageProvider: services.GetRequiredService<IOptions<MediaOptions>>().Value.StorageProvider,
        migrateOnStartup: services.GetRequiredService<IOptions<DatabaseOptions>>().Value.MigrateOnStartup);

    ProductionReadiness.Enforce(problems, app.Environment, logger);
}

/// <summary>
/// Empties a preview so its seed data can be built again from scratch.
/// </summary>
/// <remarks>
/// <para>
/// The seeders deliberately never touch a database that already has clients, which
/// means changing what they create has no effect until the existing data is gone. On a
/// hosted preview that otherwise means deleting the platform's disk by hand — fiddly,
/// and easy to get wrong. This turns it into one setting.
/// </para>
/// <para>
/// <b>It destroys everything: the database and every uploaded photograph.</b> Guarded
/// three ways — an explicit setting, Development only, and it refuses when media storage
/// is anything other than local disk, since a shared object store may hold a real
/// studio's work. Turn the setting off again once the preview is rebuilt, or the next
/// restart wipes it a second time.
/// </para>
/// </remarks>
static async Task ResetPreviewDataIfRequestedAsync(
    WebApplication app, IServiceProvider services, ApplicationDbContext db, ILogger logger)
{
    if (!app.Configuration.GetValue<bool>("Seed:ResetPreviewData"))
    {
        return;
    }

    if (!app.Environment.IsDevelopment())
    {
        logger.LogWarning(
            "Seed:ResetPreviewData is set but the environment is {Environment}. Refusing to erase data.",
            app.Environment.EnvironmentName);
        return;
    }

    var media = services.GetRequiredService<IOptions<MediaOptions>>().Value;

    if (!media.StorageProvider.Equals("LocalDisk", StringComparison.OrdinalIgnoreCase))
    {
        logger.LogWarning(
            "Seed:ResetPreviewData is set but media storage is {Provider}. Refusing: that store may "
            + "hold real work.", media.StorageProvider);
        return;
    }

    logger.LogWarning(
        "Seed:ResetPreviewData is set. Erasing the database and all stored media, then seeding "
        + "again. Turn this setting off once the preview looks right.");

    await db.Database.EnsureDeletedAsync();

    // Rebuilt here rather than relying on the migration step below, which is optional:
    // with it turned off, dropping the database and not recreating it would leave the
    // application with nothing to serve.
    await db.Database.MigrateAsync();

    // The rows are gone, so the files they described are orphaned. Left behind they
    // would accumulate on every reset until the disk filled.
    var root = Path.IsPathRooted(media.LocalStorageRoot)
        ? media.LocalStorageRoot
        : Path.Combine(app.Environment.ContentRootPath, media.LocalStorageRoot);

    if (Directory.Exists(root))
    {
        Directory.Delete(root, recursive: true);
        Directory.CreateDirectory(root);
    }

    logger.LogWarning("Preview data erased.");
}

/// <summary>
/// Applies migrations and seeds reference data before the application serves traffic.
/// Migration on startup is opt-out, because a production deployment usually migrates
/// as a separate step rather than from inside the web process.
/// </summary>
static async Task InitialiseDatabaseAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var services = scope.ServiceProvider;
    var logger = services.GetRequiredService<ILogger<Program>>();
    var databaseOptions = services.GetRequiredService<IOptions<DatabaseOptions>>().Value;

    try
    {
        var db = services.GetRequiredService<ApplicationDbContext>();

        await ResetPreviewDataIfRequestedAsync(app, services, db, logger);

        if (databaseOptions.MigrateOnStartup)
        {
            logger.LogInformation(
                "Applying migrations against the {Provider} provider.", databaseOptions.Provider);
            await db.Database.MigrateAsync();
        }

        await services.GetRequiredService<DbSeeder>().SeedAsync();

        // After the reference data, because it creates clients through the same services
        // the application uses and those need the roles and products to exist.
        await services.GetRequiredService<DemoDataSeeder>().SeedAsync();
    }
    catch (Exception ex)
    {
        // Serving requests against an unmigrated or unseeded database would fail in
        // confusing ways later, so surface it here instead.
        logger.LogCritical(ex, "Database initialisation failed. The application will not start.");
        throw;
    }
}

/// <summary>Exposed so integration tests can reference the entry point assembly.</summary>
public partial class Program;
