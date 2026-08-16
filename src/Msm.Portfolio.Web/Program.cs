using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Msm.Portfolio.Web.Authorization;
using Msm.Portfolio.Web.Configuration;
using Msm.Portfolio.Web.Data;
using Msm.Portfolio.Web.Domain.Entities;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<MediaOptions>(builder.Configuration.GetSection(MediaOptions.SectionName));
builder.Services.Configure<CommerceOptions>(builder.Configuration.GetSection(CommerceOptions.SectionName));
builder.Services.Configure<MsmBrandOptions>(builder.Configuration.GetSection(MsmBrandOptions.SectionName));
builder.Services.Configure<IntegrationOptions>(builder.Configuration.GetSection(IntegrationOptions.SectionName));

builder.Services.AddApplicationData(builder.Configuration);

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

builder.Services.AddControllersWithViews(options =>
{
    // Anti-forgery on every state-changing request by default (specification section 43).
    options.Filters.Add(new Microsoft.AspNetCore.Mvc.AutoValidateAntiforgeryTokenAttribute());
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "areas",
    pattern: "{area:exists}/{controller=Home}/{action=Index}/{id?}");

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

await InitialiseDatabaseAsync(app);

app.Run();

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

        if (databaseOptions.MigrateOnStartup)
        {
            logger.LogInformation(
                "Applying migrations against the {Provider} provider.", databaseOptions.Provider);
            await db.Database.MigrateAsync();
        }

        await services.GetRequiredService<DbSeeder>().SeedAsync();
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
