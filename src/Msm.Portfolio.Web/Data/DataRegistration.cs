using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Msm.Portfolio.Web.Configuration;

namespace Msm.Portfolio.Web.Data;

/// <summary>
/// Registers the data layer against whichever provider is configured. This is the only
/// place in the application that knows a specific database exists, which is what keeps
/// the provider decision open (specification section 32).
/// </summary>
public static class DataRegistration
{
    public static IServiceCollection AddApplicationData(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<DatabaseOptions>(configuration.GetSection(DatabaseOptions.SectionName));

        var options = configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>()
                      ?? new DatabaseOptions();

        services.AddDbContext<ApplicationDbContext>(builder =>
            ConfigureProvider(builder, options));

        return services;
    }

    /// <summary>
    /// Applies the provider-specific configuration. Migrations are kept in a
    /// per-provider namespace because a migration's generated SQL is not portable
    /// between providers.
    /// </summary>
    public static void ConfigureProvider(DbContextOptionsBuilder builder, DatabaseOptions options)
    {
        var migrationsAssembly = typeof(ApplicationDbContext).Assembly.FullName;

        switch (options.Provider)
        {
            case DatabaseProvider.PostgreSql:
                builder.UseNpgsql(options.ConnectionString, npgsql =>
                {
                    npgsql.MigrationsAssembly(migrationsAssembly);
                    npgsql.MigrationsHistoryTable("__EFMigrationsHistory");
                    npgsql.EnableRetryOnFailure();
                });
                break;

            case DatabaseProvider.SqlServer:
                builder.UseSqlServer(options.ConnectionString, sqlServer =>
                {
                    sqlServer.MigrationsAssembly(migrationsAssembly);
                    sqlServer.EnableRetryOnFailure();
                });
                break;

            case DatabaseProvider.Sqlite:
            default:
                builder.UseSqlite(options.ConnectionString, sqlite =>
                    sqlite.MigrationsAssembly(migrationsAssembly));
                break;
        }
    }
}

/// <summary>
/// Lets <c>dotnet ef</c> build a context without starting the web host. Reads the same
/// configuration the application uses, so migrations are generated for whichever
/// provider is currently selected.
/// </summary>
public class ApplicationDbContextFactory
    : Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Development";

        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile($"appsettings.{environment}.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var databaseOptions = configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>()
                              ?? new DatabaseOptions();

        var builder = new DbContextOptionsBuilder<ApplicationDbContext>();
        DataRegistration.ConfigureProvider(builder, databaseOptions);

        return new ApplicationDbContext(builder.Options);
    }
}
